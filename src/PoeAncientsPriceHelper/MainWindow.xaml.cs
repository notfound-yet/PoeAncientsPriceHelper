using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MahApps.Metro.Controls;
using SharpHook.Data;

namespace PoeAncientsPriceHelper;

public partial class MainWindow : MetroWindow
{
    private AppConfig _config = new();
    private PriceRepository? _repo;
    private IconCache? _icons;
    private ScanEngine? _engine;
    private readonly HttpClient _http = new();
    private bool _loading;

    // F4 (calibrate) stays a Win32 hotkey — it's a rare, full-screen action that benefits from being
    // suppressed from the game. Start/Stop moved to the App-level SharpHook hook (configurable key).
    private const int HotkeyId = 1;
    private const int VK_F4 = 0x73;
    private IntPtr _hwnd;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public MainWindow()
    {
        InitializeComponent();
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (v is not null) VersionLabel.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
        Loaded += OnLoaded;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // RegisterHotKey is system-wide and fails (returns false) if another window already owns
            // the key. Surface a failed F4 in the title rather than letting it fail silently.
            if (!RegisterHotKey(_hwnd, HotkeyId, 0, VK_F4))
            {
                Console.Error.WriteLine("[Hotkey] RegisterHotKey failed for F4 (already held by another app/instance)");
                Title += "  ⚠ F4 hotkey unavailable";
            }
            HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId) { RunCalibration(); handled = true; }
        return IntPtr.Zero;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _config = ConfigStore.Load();
        PopulateFields();
        await StartupAsync();
    }

    private void PopulateFields()
    {
        _loading = true;
        LeagueBox.ItemsSource = _config.AvailableLeagues;
        LeagueBox.SelectedItem = _config.AvailableLeagues.Contains(_config.LeagueName)
            ? _config.LeagueName
            : _config.AvailableLeagues.FirstOrDefault();
        var key = HotkeyBinding.Parse(_config.StartStopHotkey);
        HotkeyLabel.Text = HotkeyBinding.Display(key);
        App.SetStartStopKey(key);   // arm the global hook with the persisted binding
        UpdateRegionLabel();
        _loading = false;
    }

    private void UpdateRegionLabel()
    {
        RegionLabel.Text = _config.IsCalibrated
            ? $"x={_config.RegionX} y={_config.RegionY} {_config.RegionWidth}×{_config.RegionHeight}"
            : "Not calibrated";
    }

    private async Task StartupAsync()
    {
        StatusLabel.Text = "Fetching prices from poe.ninja…";
        StartStopButton.IsEnabled = false;

        _repo?.Dispose();
        _icons?.Dispose();

        _repo = new PriceRepository(_http);
        _repo.PricesUpdated += OnPricesUpdated;   // keep the "last fetch" label live on each refresh
        _icons = new IconCache(_http);

        await Task.WhenAll(
            _repo.InitialFetchAsync(_config),
            _icons.LoadAsync());

        _repo.StartAutoRefresh(_config);

        UpdateStatusLabel();
        StartStopButton.IsEnabled = _config.IsCalibrated;
    }

    // The 30-min background refresh fires on a thread-pool thread — marshal to the UI thread
    // before touching the label. (Previously the label was set once at startup and never updated,
    // so it stayed frozen at the launch-time fetch even though prices kept refreshing.)
    private void OnPricesUpdated() => Dispatcher.BeginInvoke(UpdateStatusLabel);

    private void UpdateStatusLabel()
    {
        if (_repo is null) return;
        string fetched = _repo.LastFetchedAt is { } t ? t.ToString("MMM d HH:mm") : "never";
        StatusLabel.Text = $"{_repo.ItemCount} items loaded  ·  last fetch {fetched}";
    }

    private void DonateButton_Click(object sender, RoutedEventArgs e)
    {
        const string url = "https://www.paypal.com/donate/?business=pedro.levi.magic%40gmail.com&currency_code=USD&item_name=PoeAncientsPriceHelper";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Donate] failed to open browser: {ex.Message}");
        }
    }

    private void RunCalibration()
    {
        var rect = CalibrationOverlay.RunOnStaThread();
        if (rect is null) return;
        _config.RegionRect = rect.Value;
        ConfigStore.Save(_config);
        Dispatcher.Invoke(() =>
        {
            UpdateRegionLabel();
            StartStopButton.IsEnabled = _config.IsCalibrated;
        });
    }

    private void CalibrateButton_Click(object sender, RoutedEventArgs e) => RunCalibration();

    private void StartStopButton_Click(object sender, RoutedEventArgs e) => ToggleStartStop();

    // Shared by the Start/Stop button and the configurable global hotkey (invoked via App, marshalled
    // to the UI thread). internal so the App-level hook can reach it.
    internal void ToggleStartStop()
    {
        if (_engine is null)
        {
            // The hotkey can fire even when the button is disabled — don't start until we're ready.
            if (!_config.IsCalibrated || _repo is null || _icons is null) return;
            _engine = new ScanEngine(_config, _repo, _icons);
            _engine.Start();
            StartStopButton.Content = "Stop";
            StartStopButton.Background = System.Windows.Media.Brushes.DarkRed;
        }
        else
        {
            _engine.StopAndWait(TimeSpan.FromSeconds(2));
            _engine.Dispose();
            _engine = null;
            StartStopButton.Content = "Start";
            StartStopButton.Background = System.Windows.Media.Brushes.DarkGreen;
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        UnregisterHotKey(_hwnd, HotkeyId);
        _engine?.StopAndWait(TimeSpan.FromSeconds(2));
        _engine?.Dispose();
        _repo?.Dispose();
        _icons?.Dispose();
        _http.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private async void LeagueBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || LeagueBox.SelectedItem is not string league || league == _config.LeagueName) return;
        _config.LeagueName = league;
        ConfigStore.Save(_config);
        await StartupAsync();   // re-fetch prices for the newly selected league
    }

    private void RebindButton_Click(object sender, RoutedEventArgs e)
    {
        RebindButton.IsEnabled = false;
        RebindButton.Content = "Press a key… (Esc to cancel)";
        App.BeginHotkeyCapture(OnHotkeyCaptured);   // outcome arrives on the UI thread
    }

    // Invoked (marshalled to the UI thread) when the global hook resolves a rebind capture.
    private void OnHotkeyCaptured(App.CaptureOutcome outcome, KeyCode code)
    {
        switch (outcome)
        {
            case App.CaptureOutcome.Captured:
                _config.StartStopHotkey = HotkeyBinding.ToStorage(code);
                ConfigStore.Save(_config);
                App.SetStartStopKey(code);
                HotkeyLabel.Text = HotkeyBinding.Display(code);
                EndRebind();
                break;
            case App.CaptureOutcome.Reserved:
                // Still listening — tell the user why that key won't take, keep the prompt up.
                RebindButton.Content = $"{HotkeyBinding.Display(code)} is in use — try another";
                break;
            case App.CaptureOutcome.Cancelled:
                EndRebind();
                break;
        }
    }

    private void EndRebind()
    {
        RebindButton.Content = "Rebind";
        RebindButton.IsEnabled = true;
    }
}
