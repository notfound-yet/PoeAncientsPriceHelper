using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MahApps.Metro.Controls;

namespace PoeAncientsPriceHelper;

public partial class MainWindow : MetroWindow
{
    private AppConfig _config = new();
    private PriceRepository? _repo;
    private IconCache? _icons;
    private ScanEngine? _engine;
    private readonly HttpClient _http = new();
    private bool _loading;

    private const int HotkeyId = 1;
    private const int StartStopHotkeyId = 2;
    private const int VK_F4 = 0x73;
    private const int VK_F5 = 0x74;
    private IntPtr _hwnd;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            RegisterHotKey(_hwnd, HotkeyId, 0, VK_F4);
            RegisterHotKey(_hwnd, StartStopHotkeyId, 0, VK_F5);
            HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HotkeyId) { RunCalibration(); handled = true; }
            else if (id == StartStopHotkeyId) { ToggleStartStop(); handled = true; }
        }
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

    // Shared by the Start/Stop button and the F5 global hotkey.
    private void ToggleStartStop()
    {
        if (_engine is null)
        {
            // F5 can fire even when the button is disabled — don't start until we're ready.
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
        UnregisterHotKey(_hwnd, StartStopHotkeyId);
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
}
