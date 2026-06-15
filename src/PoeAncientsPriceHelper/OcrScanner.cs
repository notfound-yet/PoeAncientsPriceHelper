using System.Drawing;
using System.Drawing.Imaging;
using Tesseract;

namespace PoeAncientsPriceHelper;

internal sealed record OcrRow(string NormalizedName, string RawText, int CenterY, int Multiplier = 1, float Confidence = 0, int BoxY1 = 0, int BoxY2 = 0);

internal sealed class OcrScanner : IDisposable
{
    // Two independent engines so the two segmentation passes can run concurrently — Tesseract
    // engines are single-threaded internally, but separate instances on separate threads are fine.
    private readonly TesseractEngine _engineCol;
    private readonly TesseractEngine _engineSparse;
    private readonly TesseractEngine _engineLine;
    private readonly Action<string>? _log;
    private readonly bool _debug;
    private readonly object _logLock = new();
    private const float MinConfidence = 10f;
    private const int UpscaleFactor = 2;
    private const int MinNameLength = 4;
    // A real row must contain a word at least this long. 4 (not 5) so two-short-word names
    // like "Void Flux" survive; OCR fragments are still mostly 1–3 char tokens.
    private const int MinWordLength = 4;

    // debug gates the diagnostic debug_ocr.png dump (see Scan). The flag is injected rather than
    // read from App.DebugMode so this engine-level type stays free of UI/app statics.
    public OcrScanner(string tessdataDir, Action<string>? log = null, bool debug = false)
    {
        _engineCol = CreateEngine(tessdataDir);
        _engineSparse = CreateEngine(tessdataDir);
        _engineLine = CreateEngine(tessdataDir);
        _log = log;
        _debug = debug;
    }

    private static TesseractEngine CreateEngine(string tessdataDir)
    {
        var engine = new TesseractEngine(tessdataDir, "eng", EngineMode.Default);
        engine.SetVariable("tessedit_char_whitelist",
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789x' ");
        engine.SetVariable("user_defined_dpi", "300");
        // English dictionaries dramatically improve PoE item names (Gemcutter's, Jeweller's, etc.).
        engine.SetVariable("load_system_dawg", "1");
        engine.SetVariable("load_freq_dawg", "1");
        return engine;
    }

    // Each row starts with ~3 cost-rune glyphs on the left, then "Nx ItemName". Cropping the
    // left IconColumnFraction removes the glyphs (which produce leading OCR garbage) while
    // keeping the quantity marker and the name. RightTrimFraction shaves the panel's right
    // border, which otherwise tacks stray characters onto the last word.
    // Exchange lists put rune glyphs in the left ~30%; Runeshape bars use full-width text so a
    // second pass with RuneshapeIconColumnFraction runs when few item rows are found.
    internal const double IconColumnFraction = 0.30;
    internal const double RuneshapeIconColumnFraction = 0.05;
    internal const double RightTrimFraction = 0.02;

    public IReadOnlyList<OcrRow> Scan(Bitmap regionBitmap)
    {
        // Soft preprocess (no Otsu) reads exchange lists more reliably; hard path is fallback only.
        var rows = ScanWithCrop(regionBitmap, IconColumnFraction, soft: true);
        if (CountRewards(rows) <= 1)
            rows = MergeByPosition(rows, ScanWithCrop(regionBitmap, IconColumnFraction, soft: false));
        if (CountRewards(rows) <= 1)
        {
            rows = MergeByPosition(rows, ScanWithCrop(regionBitmap, 0, soft: true));
            rows = MergeByPosition(rows, ScanRewardBands(regionBitmap));
        }
        return rows;
    }

    private static int CountRewards(IReadOnlyList<OcrRow> rows) =>
        rows.Count(r => OcrRowFilters.LooksLikeReward(r.NormalizedName));

    // Runeshape reward text sits in dark bars below the rune glyphs — a full-page OCR pass
    // often reads only the title or glyph noise. Scan fixed horizontal bands with SingleLine.
    private IReadOnlyList<OcrRow> ScanRewardBands(Bitmap regionBitmap)
    {
        int bandCount = Math.Clamp(regionBitmap.Height / 45, 3, 6);
        var bandFractions = new double[bandCount];
        for (int i = 0; i < bandCount; i++)
            bandFractions[i] = (i + 1.0) / (bandCount + 1.0);

        int bandH = Math.Max(14, regionBitmap.Height / (bandCount + 2));
        var tasks = bandFractions.Select(yf => Task.Run(() => ScanOneBand(regionBitmap, yf, bandH))).ToArray();
        Task.WaitAll(tasks);

        var result = new List<OcrRow>(bandCount);
        foreach (var t in tasks)
            result.AddRange(t.Result);
        return result;
    }

    private IReadOnlyList<OcrRow> ScanOneBand(Bitmap regionBitmap, double yFraction, int bandH)
    {
        int cy = (int)(regionBitmap.Height * yFraction);
        int y1 = Math.Max(0, cy - bandH / 2);
        int h = Math.Min(bandH, regionBitmap.Height - y1);
        if (h < 10) return [];

        using var cropped = CropBitmap(regionBitmap, 0, y1, regionBitmap.Width, h);
        using var prep = PreprocessSoft(cropped);
        using var upscaled = Upscale(prep, UpscaleFactor);
        byte[] png = ToPng(upscaled);
        var rows = new List<OcrRow>();
        foreach (var row in RunPass(_engineLine, png, PageSegMode.SingleLine, regionBitmap.Height))
        {
            if (OcrRowFilters.IsPanelChrome(row.NormalizedName)) continue;
            rows.Add(row with { CenterY = cy });
        }
        return rows;
    }

    private IReadOnlyList<OcrRow> ScanWithCrop(Bitmap regionBitmap, double iconColumnFraction, bool soft = false)
    {
        int leftCut = Math.Max(1, (int)(regionBitmap.Width * iconColumnFraction));
        int rightCut = (int)(regionBitmap.Width * RightTrimFraction);
        int cropW = Math.Max(1, regionBitmap.Width - leftCut - rightCut);
        using var cropped = CropBitmap(regionBitmap, leftCut, 0, cropW, regionBitmap.Height);
        using var inverted = soft ? PreprocessSoft(cropped) : Preprocess(cropped);
        using var upscaled = Upscale(inverted, UpscaleFactor);
        byte[] png = ToPng(upscaled);
        int height = regionBitmap.Height;

        var tCol = Task.Run(() => RunPass(_engineCol, png, PageSegMode.SingleColumn, height));
        Task.WaitAll(tCol);
        var rows = tCol.Result;

        if (_debug && rows.Count <= 2)
        {
            try { upscaled.Save(Path.Combine(AppContext.BaseDirectory, "debug_ocr.png"), System.Drawing.Imaging.ImageFormat.Png); }
            catch { /* best-effort diagnostic */ }
        }
        return rows;
    }

    private IReadOnlyList<OcrRow> RunPass(TesseractEngine engine, byte[] png, PageSegMode mode, int regionHeight)
    {
        using var pix = Pix.LoadFromMemory(png);
        using var page = engine.Process(pix, mode);
        return ExtractRows(page, regionHeight, UpscaleFactor);
    }

    private static IReadOnlyList<OcrRow> MergeByPosition(IReadOnlyList<OcrRow> a, IReadOnlyList<OcrRow> b)
    {
        const int Tol = 25;   // px: reads within this vertical distance are the same row
        static int Letters(string s) { int c = 0; foreach (var ch in s) if (char.IsLetter(ch)) c++; return c; }

        var result = new List<OcrRow>(a);
        foreach (var rb in b)
        {
            int idx = -1;
            for (int i = 0; i < result.Count; i++)
                if (Math.Abs(result[i].CenterY - rb.CenterY) <= Tol) { idx = i; break; }
            if (idx < 0) result.Add(rb);
            else if (rb.Confidence > result[idx].Confidence + 1f) result[idx] = rb;
            else if (Math.Abs(rb.Confidence - result[idx].Confidence) <= 1f &&
                     Letters(rb.NormalizedName) > Letters(result[idx].NormalizedName)) result[idx] = rb;
        }
        result.Sort((x, y) => x.CenterY.CompareTo(y.CenterY));
        return result;
    }

    private static Bitmap CropBitmap(Bitmap src, int x, int y, int w, int h)
    {
        var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
        return dst;
    }

    private IReadOnlyList<OcrRow> ExtractRows(Page page, int bitmapHeight, int scale = 1)
    {
        var rows = new List<OcrRow>();
        var diag = new List<string>();
        using var iter = page.GetIterator();
        iter.Begin();
        do
        {
            if (!iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var box)) continue;
            var text = iter.GetText(PageIteratorLevel.TextLine);
            float conf = iter.GetConfidence(PageIteratorLevel.TextLine);
            // Bounding box coords are in upscaled space — divide back to original coords
            int centerY = Math.Clamp((box.Y1 + (box.Y2 - box.Y1) / 2) / scale, 0, bitmapHeight - 1);

            string? reject = null;
            string normalized = "";
            int multiplier = 1;
            if (string.IsNullOrWhiteSpace(text)) reject = "empty";
            else if (conf < MinConfidence) reject = "lowconf";
            else if (LooksLikeGarbage(text)) reject = "garbage";
            else
            {
                var normalizedRaw = NameNormalizer.Normalize(text);
                multiplier = ExtractMultiplier(normalizedRaw);
                normalized = NameNormalizer.FixOcrArtifacts(StripLeadingNoise(normalizedRaw));
                if (normalized.Length < MinNameLength) reject = "short";
                else if (!HasLongWord(normalized, MinWordLength)) reject = "noword";
            }

            if (reject is null)
                rows.Add(new OcrRow(normalized, text.Trim(), centerY, multiplier, conf, box.Y1, box.Y2));
            diag.Add($"y={centerY} conf={conf:0} '{(text ?? "").Trim()}'{(reject is null ? "" : $" REJ:{reject}")}");
        }
        while (iter.Next(PageIteratorLevel.TextLine));

        // Diagnostic: when few rows survive, show every line Tesseract actually produced so we
        // can tell "Tesseract only saw 1 line" from "saw 5 but the filters dropped 4".
        // Runs on a pass thread — serialize so two concurrent passes don't race the logger.
        if (rows.Count <= 2 && diag.Count > 0)
            lock (_logLock) { _log?.Invoke($"OCR raw {diag.Count} lines → " + string.Join(" | ", diag)); }

        return rows;
    }

    // Reject random caps / low-vowel noise Tesseract emits on bad frames or overlay bleed.
    private static bool LooksLikeGarbage(string text)
    {
        int letters = 0, upper = 0, lower = 0, vowels = 0;
        foreach (char c in text)
        {
            if (char.IsUpper(c)) { upper++; letters++; }
            else if (char.IsLower(c)) { lower++; letters++; }
            if ("aeiouAEIOU".Contains(c)) vowels++;
        }
        if (letters < 4) return true;
        if (upper >= 3 && lower >= 3 && upper > letters / 3 && lower > letters / 4) return true;
        if (letters >= 8 && vowels < letters / 6) return true;
        return false;
    }

    // Re-OCR individual rows with SingleLine when the full-page pass had low confidence.
    private IReadOnlyList<OcrRow> RefineLowConfidenceRows(Bitmap upscaled, IReadOnlyList<OcrRow> rows, int regionHeight)
    {
        const float RefineBelow = 55f;
        const int MaxRefines = 2;   // cap — each refine is a full Tesseract SingleLine call
        var result = new List<OcrRow>(rows.Count);
        int refines = 0;
        foreach (var row in rows)
        {
            if (row.Confidence >= RefineBelow || row.BoxY2 <= row.BoxY1 || refines >= MaxRefines)
            {
                result.Add(row);
                continue;
            }

            int y1 = Math.Max(0, row.BoxY1 - 4);
            int y2 = Math.Min(upscaled.Height, row.BoxY2 + 4);
            int h = y2 - y1;
            if (h < 4)
            {
                result.Add(row);
                continue;
            }

            using var crop = CropBitmap(upscaled, 0, y1, upscaled.Width, h);
            byte[] png = ToPng(crop);
            var refined = RunPass(_engineLine, png, PageSegMode.SingleLine, regionHeight);
            if (refined.Count == 0)
            {
                result.Add(row);
                continue;
            }

            var best = refined.MaxBy(r => r.Confidence)!;
            if (best.Confidence > row.Confidence + 2f)
                result.Add(best with { CenterY = row.CenterY });
            else
                result.Add(row);
            refines++;
        }
        return result;
    }

    private static Bitmap Upscale(Bitmap src, int factor)
    {
        var dst = new Bitmap(src.Width * factor, src.Height * factor, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        return dst;
    }

    // The list shows a stack quantity as "Nx" before the item name ("1x", "2x", "14x").
    // Capture it so the price can be multiplied by the stack size. Read from the raw
    // normalized string BEFORE StripLeadingNoise removes the marker. Returns 1 when absent.
    internal static int ExtractMultiplier(string normalized)
    {
        var m = Regex.Match(normalized, @"(?<![a-z0-9])(\d{1,3})\s*x(?![a-z0-9])");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n >= 1)
            return Math.Min(n, 999);
        return 1;
    }

    // Strip leading noise: short/numeric tokens ("e", "l8"), then anything before the first
    // quantity marker ("1x", "11x"), then remaining leading non-alpha chars.
    // e.g. "krogin 1x ancient rune of decay"  → "ancient rune of decay"
    // e.g. "e l8 n 1x the greatwolf"          → "the greatwolf"
    internal static string StripLeadingNoise(string normalized)
    {
        var s = Regex.Replace(normalized, @"^(?:\S{1,2}\s+|\S*\d\S*\s+)+", "");
        // If a quantity marker still exists, drop everything before (and including) it
        var qm = Regex.Match(s, @"(?<!\w)\d+\s*x\s+");
        if (qm.Success) s = s.Substring(qm.Index + qm.Length);
        s = Regex.Replace(s, @"^[^a-z]+", "");
        return s.Trim();
    }

    private static bool HasLongWord(string normalized, int minLen)
    {
        int run = 0;
        foreach (char c in normalized)
        {
            if (char.IsLetter(c)) { if (++run >= minLen) return true; }
            else run = 0;
        }
        return false;
    }

    // Soft path for Runeshape gold/dark bars — Otsu binarization erases the highlight text.
    private static Bitmap PreprocessSoft(Bitmap src)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.DrawImage(src, 0, 0);
        InvertBitmap(dst);
        StretchContrast(dst);
        return dst;
    }

    // Invert + contrast stretch + binarize: PoE list panel has light beveled text on a textured
    // parchment background. Tesseract reads cleaner with high-contrast dark-on-light glyphs.
    private static Bitmap Preprocess(Bitmap src)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.DrawImage(src, 0, 0);
        InvertBitmap(dst);
        StretchContrast(dst);
        Sharpen(dst);
        BinarizeOtsu(dst);
        return dst;
    }

    private static void Sharpen(Bitmap bmp)
    {
        // 3×3 unsharp kernel — helps the ornate PoE font edges before binarization.
        int w = bmp.Width, h = bmp.Height;
        var src = new byte[w * h];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            var buf = new byte[stride * h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    src[y * w + x] = buf[y * stride + x * 3];

            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int sum = -src[(y - 1) * w + x] - src[y * w + x - 1] + 5 * src[y * w + x]
                              - src[y * w + x + 1] - src[(y + 1) * w + x];
                    byte v = (byte)Math.Clamp(sum, 0, 255);
                    int i = y * stride + x * 3;
                    buf[i] = buf[i + 1] = buf[i + 2] = v;
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        }
        finally { bmp.UnlockBits(data); }
    }

    private static void BinarizeOtsu(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            int len = data.Stride * bmp.Height;
            var buf = new byte[len];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, len);

            var hist = new int[256];
            int pixels = bmp.Width * bmp.Height;
            var lum = new byte[pixels];
            int idx = 0;
            for (int i = 0; i < buf.Length; i += 3)
            {
                byte l = (byte)((buf[i] + buf[i + 1] + buf[i + 2]) / 3);
                lum[idx++] = l;
                hist[l]++;
            }

            int threshold = ComputeOtsuThreshold(hist, pixels);
            idx = 0;
            for (int i = 0; i < buf.Length; i += 3)
            {
                byte v = (byte)(lum[idx++] < threshold ? 0 : 255);
                buf[i] = buf[i + 1] = buf[i + 2] = v;
            }
            System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, len);
        }
        finally { bmp.UnlockBits(data); }
    }

    private static int ComputeOtsuThreshold(int[] hist, int total)
    {
        long sum = 0;
        for (int i = 0; i < 256; i++) sum += (long)i * hist[i];

        long sumB = 0;
        int wB = 0;
        double maxVar = 0;
        int threshold = 128;

        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            int wF = total - wB;
            if (wF == 0) break;

            sumB += (long)t * hist[t];
            double mB = (double)sumB / wB;
            double mF = (double)(sum - sumB) / wF;
            double var = wB * wF * (mB - mF) * (mB - mF);
            if (var > maxVar) { maxVar = var; threshold = t; }
        }
        return threshold;
    }

    private static void StretchContrast(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            int len = data.Stride * bmp.Height;
            var buf = new byte[len];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, len);

            int min = 255, max = 0;
            for (int i = 0; i < buf.Length; i += 3)
            {
                int lum = (buf[i] + buf[i + 1] + buf[i + 2]) / 3;
                if (lum < min) min = lum;
                if (lum > max) max = lum;
            }
            if (max <= min) return;

            double scale = 255.0 / (max - min);
            for (int i = 0; i < buf.Length; i++)
                buf[i] = (byte)Math.Clamp((buf[i] - min) * scale, 0, 255);

            System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, len);
        }
        finally { bmp.UnlockBits(data); }
    }

    private static void Binarize(Bitmap bmp, int threshold)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            int len = data.Stride * bmp.Height;
            var buf = new byte[len];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, len);
            for (int i = 0; i < buf.Length; i += 3)
            {
                int lum = (buf[i] + buf[i + 1] + buf[i + 2]) / 3;
                byte v = (byte)(lum < threshold ? 0 : 255);
                buf[i] = buf[i + 1] = buf[i + 2] = v;
            }
            System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, len);
        }
        finally { bmp.UnlockBits(data); }
    }

    // Kept for tests / fallback — production path uses BinarizeOtsu.

    private static void InvertBitmap(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            int len = data.Stride * bmp.Height;
            var buf = new byte[len];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, len);
            for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(255 - buf[i]);
            System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, len);
        }
        finally { bmp.UnlockBits(data); }
    }

    private static byte[] ToPng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    internal static string NormalizeName(string text) => NameNormalizer.Normalize(text);

    public void Dispose() { _engineCol.Dispose(); _engineSparse.Dispose(); _engineLine.Dispose(); }
}
