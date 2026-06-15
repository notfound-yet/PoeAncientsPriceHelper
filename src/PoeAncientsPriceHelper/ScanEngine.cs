using System.Diagnostics;
using System.Drawing;

namespace PoeAncientsPriceHelper;

internal sealed class ScanEngine : IDisposable
{
    private readonly AppConfig _config;
    private readonly PriceRepository _prices;
    private readonly IconCache _icons;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Dictionary<string, int> _lastPositions = new();
    private string _logPath = "";

    // Shared with the global hotkey hook (App). The loop owns the detection state, so the hook
    // only sets a "dismissed" latch; the loop reads it and keeps the overlay hidden.
    private static volatile bool _dismissed;
    private static volatile bool _showing;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    // True while the overlay is actually showing a confirmed panel.
    public static bool IsShowing => _showing;

    // ESC / Left-Ctrl+click: hide the overlay and keep it hidden until the panel actually closes
    // (ESC closes the panel, so it clears fast; Ctrl+click leaves the panel open, so it stays
    // dismissed without flickering until the user closes the panel themselves).
    public static void RequestDismiss() => _dismissed = true;

    public ScanEngine(AppConfig config, PriceRepository prices, IconCache icons)
    {
        _config = config;
        _prices = prices;
        _icons = icons;
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    public void StopAndWait(TimeSpan timeout)
    {
        Stop();
        try { _loopTask?.Wait(timeout); } catch { }
    }

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        try { File.AppendAllText(_logPath, line + "\n"); } catch { }
        if (App.DebugMode) Console.WriteLine(line);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        _logPath = Path.Combine(AppContext.BaseDirectory, "scan_log.txt");
        File.WriteAllText(_logPath, "");

        var tessdataDir = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!Directory.Exists(tessdataDir))
        {
            Log($"ERROR tessdata not found at {tessdataDir}");
            return;
        }

        Log($"START prices={_prices.ItemCount} catalog={_prices.Catalog.Count} icons={_icons.IsAvailable} region={_config.RegionRect}");
        if (_prices.Catalog.Count == 0)
            Log("WARN catálogo vazio — clique em 'Atualizar preços' ou verifique a liga");

        using var scanner = new OcrScanner(tessdataDir, Log, App.DebugMode);
        var detector = new ListDetector();
        var sw = Stopwatch.StartNew();
        var slots = new List<RowSlot>();             // per-row accumulator: priced rows lock, misses keep retrying
        IReadOnlyList<PriceRow> lastRows = [];       // what the overlay shows
        int topmostCounter = 0;
        const int TopmostEveryN = 10;
        bool isOpen = false;          // brightness gate: bright enough to attempt OCR
        bool confirmedOpen = false;   // OCR actually found a list — only then show the overlay
        // After a dismiss (ESC / Ctrl+click) the brightness gate can re-trip on ambient light that
        // grazes the threshold (the game world after the panel closes reads almost as bright as a real
        // panel — measured 105 vs a real panel's 101). That re-show is the post-ESC flicker. While this
        // is set, the brightness-only "reading…" hint is suppressed: nothing shows until OCR actually
        // confirms a priced row again. Cleared on the next real confirm.
        bool suppressHintUntilConfirm = false;
        int brightStreak = 0;
        int darkStreak = 0;
        int dismissDark = 0;          // dark frames seen while dismissed — releases the latch when the panel closes
        int cycleCount = 0;
        var lastOcrAt = DateTime.MinValue;
        const int MinOcrIntervalMs = 100;            // OCR floor while panel is open — fast price turnaround
        const int OpenCycleMs = 100;                 // tight loop while scanning
        const int ClosedCycleMs = 150;               // polling while watching for the panel — snappy detection
        const int DarkToRelease = 3;                 // dark frames before a dismiss latch releases
        // Asymmetric brightness hysteresis. A frame counts toward OPENING only above OpenBrightness and
        // toward CLOSING only below CloseBrightness; readings in the [80,100] dead zone hold the current
        // state so brightness hovering at the boundary can't flicker the overlay. OpenBrightness stays
        // at the detector's old threshold (100) on purpose — real panels read as low as 101, so raising
        // it would miss dim ones; the confirm-gate (above) is what rejects bright-but-fake frames.
        const int OpenBrightness = 100;
        const int CloseBrightness = 80;

        PriceOverlayManager.EnsureVisible(_config.RegionRect, _config.OverlayXOffset, _icons);
        if (App.DebugMode) PriceOverlayManager.EnableDebug();
        Log("overlay ready");

        double captureMs, detectMs, ocrMs, matchMs;
        ScanTimings BuildTimings(double cycleMs) => new(
            captureMs, detectMs, ocrMs, matchMs, cycleMs,
            _prices.LastFetchDurationMs, _prices.IsFetching);

        while (!ct.IsCancellationRequested)
        {
            var cycleStart = sw.ElapsedMilliseconds;
            cycleCount++;
            captureMs = detectMs = ocrMs = matchMs = 0;
            try
            {
                var phase = Stopwatch.StartNew();
                PriceOverlayManager.SuspendForCapture();
                Bitmap? bmp = null;
                try
                {
                    bmp = ScreenCapture.CaptureRegion(_config.RegionRect);
                }
                finally
                {
                    PriceOverlayManager.ResumeAfterCapture();
                }
                using (bmp)
                {
                captureMs = phase.Elapsed.TotalMilliseconds;
                phase.Restart();
                detector.IsOpen(bmp, out var sampledPixel);   // bool unused — we apply our own hysteresis
                detectMs = phase.Elapsed.TotalMilliseconds;
                int brightness = (sampledPixel.R + sampledPixel.G + sampledPixel.B) / 3;
                bool brightFrame = brightness > OpenBrightness;   // strong enough to count toward opening
                bool darkFrame = brightness < CloseBrightness;    // dim enough to count toward closing

                // Dismissed (ESC / Left-Ctrl+click): stay hidden and don't scan until the panel
                // actually closes (a few genuinely dark frames). ESC closes the panel so this clears
                // quickly; Ctrl+click keeps it open, so the overlay stays dismissed (no flicker) until
                // the user closes the panel. On release, arm hint-suppression so the next brightness
                // blip can't re-show the overlay before OCR re-confirms a real panel.
                if (_dismissed)
                {
                    if (darkFrame) dismissDark++; else dismissDark = 0;
                    if (dismissDark >= DarkToRelease)
                    {
                        _dismissed = false;
                        suppressHintUntilConfirm = true;
                        Log("dismiss released (panel closed)");
                    }
                    isOpen = false; confirmedOpen = false; brightStreak = 0; darkStreak = 0;
                    slots.Clear(); lastRows = [];
                    _showing = false;
                    PriceOverlayManager.UpdateState([], false, false, false, BuildTimings(sw.ElapsedMilliseconds - cycleStart));
                }
                else
                {
                    dismissDark = 0;

                    // Hysteresis: 2 consecutive bright frames to open, 3 dark frames to close; readings
                    // in the [CloseBrightness, OpenBrightness] dead zone hold the current state.
                    if (brightFrame) { brightStreak++; darkStreak = 0; }
                    else if (darkFrame) { darkStreak++; brightStreak = 0; }
                    else { brightStreak = 0; darkStreak = 0; }
                    bool prevIsOpen = isOpen;
                    if (!isOpen && brightStreak >= 2) isOpen = true;
                    else if (isOpen && darkStreak >= 3) isOpen = false;

                    // Heartbeat every ~5s so we know the loop is alive
                    if (cycleCount % 12 == 0)
                    {
                        Log($"heartbeat cycle={cycleCount} panelOpen={isOpen} confirmed={confirmedOpen} region={_config.RegionRect} rows={lastRows.Count} " +
                            $"avgPixel=#{sampledPixel.R:X2}{sampledPixel.G:X2}{sampledPixel.B:X2} brightness={brightness}");
                    }

                    if (isOpen != prevIsOpen)
                    {
                        Log($"panel {(isOpen ? "OPEN" : "CLOSED")} brightness={brightness} " +
                            $"avgPixel=#{sampledPixel.R:X2}{sampledPixel.G:X2}{sampledPixel.B:X2}");

                        // Panel just detected — show the "reading…" hint right away, before the first
                        // (200–400ms) OCR runs, so the wait isn't a blank screen. But right after a
                        // dismiss, suppress it: a brightness blip that isn't a real panel never
                        // confirms, so showing the hint here is exactly the post-ESC flicker.
                        if (isOpen && !suppressHintUntilConfirm)
                        {
                            _showing = false;
                            PriceOverlayManager.UpdateState([], isOpen, false, true, BuildTimings(sw.ElapsedMilliseconds - cycleStart));
                        }
                    }

                    if (isOpen)
                    {
                        var now = DateTime.UtcNow;
                        if ((now - lastOcrAt).TotalMilliseconds >= MinOcrIntervalMs)
                        {
                            lastOcrAt = now;
                            phase.Restart();
                            var ocrRows = scanner.Scan(bmp);
                            ocrMs = phase.Elapsed.TotalMilliseconds;
                            if (ocrRows.Count == 0)
                            {
                                // Panel mid-animation or a bad frame — don't disturb locked rows.
                                Log("OCR returned 0 rows");
                            }
                            else
                            {
                                phase.Restart();
                                var reads = BuildPriceRows(ocrRows);
                                lastRows = MergeReads(slots, reads);
                                matchMs = phase.Elapsed.TotalMilliseconds;
                                Log($"OCR {ocrRows.Count} rows → " +
                                    string.Join(" | ", reads.Select(r =>
                                        $"raw='{r.OcrText.Trim()}' y={r.CenterY} " +
                                        $"{(r.HasPrice ? $"HIT→'{r.Name}'" : "MISS")}")));

                                // Confirm once OCR sees real item lines (not only brightness).
                                if (!confirmedOpen)
                                {
                                    int itemLines = ocrRows.Count(r => !OcrRowFilters.IsPanelChrome(r.NormalizedName));
                                    if (reads.Any(r => r.HasPrice) ||
                                        reads.Any(r => OcrRowFilters.LooksLikeReward(r.Name)) ||
                                        itemLines > 0)
                                    {
                                        confirmedOpen = true;
                                        suppressHintUntilConfirm = false;
                                        Log($"panel CONFIRMED (items={itemLines} priced={reads.Count(r => r.HasPrice)})");
                                    }
                                }

                                // Per-row slots: a row locks once confirmed, then stays fixed;
                                // unpriced rows keep being retried every pass.
                            }

                            if (App.DebugMode)
                                Log($"perf captura={captureMs:F1} detecção={detectMs:F1} ocr={ocrMs:F1} match={matchMs:F1} ciclo={sw.ElapsedMilliseconds - cycleStart:F1}");
                        }
                    }
                    else
                    {
                        slots.Clear();
                        lastRows = [];
                        confirmedOpen = false;
                    }

                    // "reading" = brightness says a panel is up but OCR hasn't confirmed prices yet.
                    // Suppressed straight after a dismiss until a real confirm (anti-flicker, see above).
                    bool reading = isOpen && !confirmedOpen && !suppressHintUntilConfirm
                                              && !lastRows.Any(r => r.HasPrice);

                    // Show prices only once OCR has confirmed a real list, not on brightness alone.
                    _showing = confirmedOpen;
                    PriceOverlayManager.UpdateState(lastRows, isOpen, confirmedOpen, reading, BuildTimings(sw.ElapsedMilliseconds - cycleStart));

                    topmostCounter++;
                    if (topmostCounter >= TopmostEveryN)
                    {
                        PriceOverlayManager.ForceTopmost();
                        topmostCounter = 0;
                    }
                }
                }   // using (bmp)
            }
            catch (Exception ex)
            {
                Log($"ERROR {ex.GetType().Name}: {ex.Message}");
            }

            var cycleMs = sw.ElapsedMilliseconds - cycleStart;
            var wait = (int)Math.Max(0, (isOpen ? OpenCycleMs : ClosedCycleMs) - cycleMs);
            if (wait > 0)
            {
                try { await Task.Delay(wait, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        _showing = false;
        PriceOverlayManager.Hide();
        Log("loop exited");
    }

    private IReadOnlyList<PriceRow> BuildPriceRows(IReadOnlyList<OcrRow> ocrRows)
    {
        var snapshot = _prices.Prices;
        var rows = new List<PriceRow>(ocrRows.Count);
        var newPositions = new Dictionary<string, int>(ocrRows.Count);

        foreach (var row in ocrRows)
        {
            if (OcrRowFilters.IsPanelChrome(row.NormalizedName))
                continue;

            int stableY = row.CenterY;
            if (_lastPositions.TryGetValue(row.NormalizedName, out int prevY) &&
                Math.Abs(prevY - row.CenterY) < 5)
                stableY = prevY;
            newPositions[row.NormalizedName] = stableY;

            // Uncut gems (skill / spirit / support) are priced PER LEVEL, and adjacent levels differ
            // several-fold (e.g. spirit gem L18 ≈ 0.027 div vs L19 ≈ 0.143 div). The only things that
            // distinguish one gem line from another are the TYPE word and the LEVEL number, so we pin
            // both EXACTLY and deliberately skip the prefix/fuzzy fallbacks here: a single-character OCR
            // slip on the digit (or skill↔spirit) would otherwise lock a confidently-wrong, multiples-off
            // price. If the type or level can't be read cleanly, the row shows '?' until a clean read
            // arrives — better than guessing a neighbouring level.
            if (TryResolveGemKey(row.NormalizedName, out var gemKey))
            {
                if (gemKey is not null && snapshot.TryGetValue(gemKey, out var gemEntry))
                    rows.Add(new PriceRow(stableY, row.RawText, gemEntry.DivineValue, gemEntry.ExaltedValue,
                        true, row.Multiplier, gemKey, true));
                else
                    // Recognised as an uncut gem but type+level didn't pin to a known price → '?', never fuzzy.
                    rows.Add(new PriceRow(stableY, row.RawText, 0m, 0m, false, row.Multiplier, row.NormalizedName));
                continue;
            }

            // Easter eggs: certain OCR'd names render as a gag icon + caption instead of a price.
            // ExactMatch=true so they lock on the first read like a real priced row.
            //   "5x random currency" (the "5x" is stripped into the multiplier, leaving "random
            //    currency") → Mirror of Kalandra. "unique belt" → Headhunter.
            if (row.NormalizedName.Contains("random") && row.NormalizedName.Contains("currency"))
            {
                rows.Add(new PriceRow(stableY, row.RawText, 0m, 0m, true, row.Multiplier, "random currency", true, MemeKind.Mirror));
                continue;
            }
            if (row.NormalizedName.Contains("unique") && row.NormalizedName.Contains("belt"))
            {
                rows.Add(new PriceRow(stableY, row.RawText, 0m, 0m, true, row.Multiplier, "unique belt", true, MemeKind.Headhunter));
                continue;
            }

            // Resolve the OCR'd name to a price key: exact → artifact fix → prefix → fuzzy → tokens.
            if (ItemNameMatcher.TryMatch(row.NormalizedName, _prices.Catalog, out var matchedKey, out var entry, out var exact))
                rows.Add(new PriceRow(stableY, row.RawText, entry.DivineValue, entry.ExaltedValue, true, row.Multiplier, matchedKey, exact));
            else
                rows.Add(new PriceRow(stableY, row.RawText, 0m, 0m, false, row.Multiplier, row.NormalizedName));
        }
        _lastPositions = newPositions;
        return rows;
    }

    // Detect an uncut gem and pin its identity. Returns true when the name is an uncut gem (a type
    // word skill/spirit/support together with "gem"); the discriminating type word and "gem" are what
    // mark it, so a slip in the boilerplate words ("uncot", "levei") doesn't hide a gem. When a level
    // number is also present, `key` is the canonical price key with the type and level pinned exactly
    // (no fuzzy) — caller looks it up as-is. When the level can't be read, `key` is null so the caller
    // shows '?' rather than guessing an adjacent level (which can be several-fold off).
    internal static bool TryResolveGemKey(string normalizedName, out string? key)
    {
        key = null;
        if (!normalizedName.Contains("gem")) return false;
        var type = Regex.Match(normalizedName, @"\b(skill|spirit|support)\b");
        if (!type.Success) return false;
        var lvl = Regex.Match(normalizedName, @"\blevel\s+(\d+)\b");
        if (lvl.Success) key = $"uncut {type.Groups[1].Value} gem level {lvl.Groups[1].Value}";
        return true;
    }

    internal static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    // One display row per screen position. A slot locks onto a price once the same item name
    // is read on two consecutive passes, then stays fixed (noise can't dislodge it). Rows that
    // are still unpriced keep showing the latest attempt and get re-read every pass, so an early
    // misread no longer freezes a row — a later correct read upgrades it.
    private sealed class RowSlot
    {
        public int Y;                    // stable display position (first-seen)
        public PriceRow Latest = null!;  // most recent read (shown, as unpriced, until locked)
        public bool Locked;              // a confirmed price is pinned
        public PriceRow LockedRow = null!;
        public string? PendingName;      // candidate price name awaiting a second confirming read
        public int PendingCount;
        public int Unseen;               // consecutive passes this slot wasn't matched
    }

    private IReadOnlyList<PriceRow> MergeReads(List<RowSlot> slots, IReadOnlyList<PriceRow> reads)
    {
        const int Tolerance = 20;   // px: how far a read can move and still be the same row
        const int Confirm = 2;      // matching fuzzy/prefix reads before a row locks (exact: 1)
        const int EvictAfter = 3;   // passes a slot can go unmatched before it's dropped

        // Panel-switch detection: the user opened a different panel without the overlay closing.
        // Locked rows are otherwise sticky (a miss never unlocks them), so they'd keep showing the
        // previous panel's prices. If two or more locked positions now read a *different* priced
        // item, the content changed — drop the stale locks so the new panel takes over at once.
        int changedPositions = 0;
        foreach (var read in reads)
        {
            if (!read.HasPrice) continue;
            var locked = slots.FirstOrDefault(s => s.Locked && Math.Abs(s.Y - read.CenterY) <= Tolerance);
            if (locked is not null && locked.LockedRow.Name != read.Name) changedPositions++;
        }
        if (changedPositions >= 2)
        {
            Log($"panel switch detected ({changedPositions} rows changed) — resetting prices");
            slots.Clear();
        }

        var matched = new HashSet<RowSlot>();
        foreach (var read in reads)
        {
            RowSlot? slot = null;
            int best = int.MaxValue;
            foreach (var s in slots)
            {
                if (matched.Contains(s)) continue;
                int d = Math.Abs(s.Y - read.CenterY);
                if (d <= Tolerance && d < best) { best = d; slot = s; }
            }
            if (slot is null)
            {
                slot = new RowSlot { Y = read.CenterY };
                slots.Add(slot);
            }
            matched.Add(slot);
            slot.Unseen = 0;
            slot.Latest = read;

            if (read.HasPrice)
            {
                if (slot.PendingName == read.Name) slot.PendingCount++;
                else { slot.PendingName = read.Name; slot.PendingCount = 1; }

                // Exact dictionary matches are trustworthy enough to lock immediately; only the
                // uncertain fuzzy/prefix matches need a second confirming read.
                int needed = read.ExactMatch ? 1 : Confirm;
                if (slot.PendingCount >= needed)
                {
                    if (!slot.Locked || slot.LockedRow.Name != read.Name)
                        Log($"locked y={slot.Y} '{read.Name}'");
                    slot.Locked = true;
                    slot.LockedRow = read with { CenterY = slot.Y };
                }
            }
            else
            {
                // A miss breaks a pending streak but never unlocks an already-priced row.
                slot.PendingName = null;
                slot.PendingCount = 0;
            }
        }

        for (int i = slots.Count - 1; i >= 0; i--)
        {
            if (matched.Contains(slots[i])) continue;
            if (++slots[i].Unseen > EvictAfter) slots.RemoveAt(i);
        }

        var display = new List<PriceRow>(slots.Count);
        foreach (var s in slots.OrderBy(s => s.Y))
        {
            // Show the latest match immediately — locking only pins a row across noisy frames,
            // it must not hide a price that already matched on the first OCR pass.
            display.Add(s.Locked ? s.LockedRow : s.Latest with { CenterY = s.Y });
        }
        return display;
    }

    public void Dispose()
    {
        StopAndWait(TimeSpan.FromSeconds(2));
        _cts?.Dispose();
    }
}
