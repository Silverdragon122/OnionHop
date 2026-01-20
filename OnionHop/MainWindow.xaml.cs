using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Principal;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Effects;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SystemColors = System.Windows.SystemColors;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace OnionHop;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly Regex AnsiEscapeRegex = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex SingBoxConnectionToRegex = new(@"connection to (?<dest>\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] BrowserProcessNames =
    {
        "firefox.exe",
        "chrome.exe",
        "msedge.exe"
    };

    private const int SocksPort = 9050;
    private const string DefaultTorRelativePath = "tor\\tor.exe";
    private const string DefaultSingBoxRelativePath = "vpn\\sing-box.exe";
    private const string DefaultWintunRelativePath = "vpn\\wintun.dll";
    private const string DefaultPtConfigRelativePath = "tor\\pluggable_transports\\pt_config.json";
    private const string AutoStartRegistryKey = @"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string AutoStartValueName = "OnionHop";
    private const string UpdateApiUrl = "https://api.github.com/repos/center2055/OnionHop/releases/latest";
    private const string AutoStartModeOff = "Off";
    private const string AutoStartModeOn = "On";
    private const string AutoStartModeMinimized = "On (Minimized)";
    private const string AutomaticLocationLabel = "Automatic";
    private const string ConnectionModeProxy = "Proxy Mode (Recommended)";
    private const string ConnectionModeTun = "TUN/VPN Mode (Admin)";
    private const string CensoredBridgePrimary = "snowflake";
    private const string CensoredBridgeFallback = "meek-azure";
    private const string WebTunnelBridgeType = "webtunnel";
    private const string WebTunnelClientFileName = "webtunnel-client.exe";
    private const string TorFallbackVersion = "14.0.4";
    private const string TorBaseUrl = "https://dist.torproject.org/torbrowser";
    private const string TorArchiveBaseUrl = "https://archive.torproject.org/tor-package-archive/torbrowser";
    private const string SingBoxApiUrl = "https://api.github.com/repos/SagerNet/sing-box/releases/latest";
    private const string WintunUrl = "https://www.wintun.net/builds/wintun-0.14.1.zip";
    private const string WebTunnelBridgeSourceUrl = "https://bridges.torproject.org/bridges?transport=webtunnel&format=plain";
    private static readonly TimeSpan TorExitCooldown = TimeSpan.FromSeconds(5);

    private bool _isConnecting;
    private bool _isConnected;
    private string _selectedLocation = AutomaticLocationLabel;
    private string _statusMessage = "Ready to route traffic through Tor.";
    private string _connectionStatus = "Disconnected";
    private string _currentIp = "--.--.--.--";
    private Brush _statusBrush = new SolidColorBrush(Color.FromRgb(209, 67, 75));
    private double _connectionProgress;
    private bool _dependencyDownloadInProgress;
    private double _dependencyDownloadProgress;
    private string _dependencyDownloadStatus = "Checking components...";
    private Task<bool>? _dependencyEnsureTask;

    private readonly TorService _torService;
    private readonly VpnService _vpnService;
    private JobObject? _processJob;
    private CancellationTokenSource? _connectCts;
    private TaskCompletionSource<bool>? _bootstrapSource;
    private PluggableTransportConfig? _ptConfig;
    private DateTime _lastNewnymUtc = DateTime.MinValue;
    private DateTime _lastTorExitUtc = DateTime.MinValue;
    private readonly DateTime _appStartUtc = DateTime.UtcNow;
    private DateTime? _lastConnectAttemptUtc;
    private bool _autoRetryConnectUsed;
    private bool _pendingAutoRetry;
    private string? _bridgeValidationMessage;
    private bool _webTunnelBridgeFetchStarted;
    private string _aboutVersionText = "Version: -";
    private string _aboutReleaseDateText = "Release date: -";

    private DateTime _lastVpnMessageUtc = DateTime.MinValue;
    private readonly object _singBoxLogLock = new();
    private readonly Queue<string> _singBoxRecentLines = new();

    private readonly MainViewModel _viewModel = new();
    public ObservableCollection<string> LogLines => _viewModel.LogLines;

    private readonly object _logLock = new();
    private bool _showLogs;
    private bool _showAbout;
    private bool _showSettings;

    private string? _previousProxy;
    private int? _previousProxyEnabled;
    private bool _systemProxyApplied;

    private bool _loadingSettings;
    private CancellationTokenSource? _settingsSaveCts;
    private bool _isExiting;
    private bool _startMinimizedOnLaunch;
    private bool _isCheckingUpdates;
    private bool _trayBalloonShown;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _trayIconImage;
    private WindowChrome? _customChrome;
    private bool _startHiddenToTray;
    private readonly SettingsService _settingsService = new();
    private readonly UpdateService _updateService = new();
    private readonly AdminHelperClient _adminHelper = new();
    private CancellationTokenSource? _adminVpnMonitorCts;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> Locations { get; } = new()
    {
        AutomaticLocationLabel,
        "United States",
        "United Kingdom",
        "Germany",
        "France",
        "Switzerland",
        "Netherlands",
        "Canada",
        "Singapore"
    };

    public ObservableCollection<string> ConnectionModes { get; } = new()
    {
        ConnectionModeProxy,
        ConnectionModeTun
    };

    public ObservableCollection<string> AutoStartModes { get; } = new()
    {
        AutoStartModeOff,
        AutoStartModeOn,
        AutoStartModeMinimized
    };

    public ObservableCollection<string> BridgeTypes { get; } = new();

    public string SelectedLocation
    {
        get => _selectedLocation;
        set
        {
            if (SetField(ref _selectedLocation, value))
            {
                _viewModel.SelectedLocation = value;
            }
        }
    }

    private void OnTorExited(object? sender, EventArgs e)
    {
        if (_isDisconnecting)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastTorExitUtc < TorExitCooldown)
        {
            return;
        }
        _lastTorExitUtc = now;

        if (_isConnected && IsTunMode && KillSwitchEnabled && !UseHybridRouting)
        {
            EnableKillSwitchEmergencyBlock();
        }

        AppendLog("Tor exited unexpectedly.");
        Dispatcher.BeginInvoke(new Action(() => _ = DisconnectAsync()));
    }

    public string SelectedConnectionMode
    {
        get => _selectedConnectionMode;
        set
        {
            if (SetField(ref _selectedConnectionMode, value))
            {
                Raise(nameof(IsTunMode));
                Raise(nameof(IsProxyMode));
            }
        }
    }
    private string _selectedConnectionMode = ConnectionModeProxy;

    public bool IsTunMode => string.Equals(SelectedConnectionMode, ConnectionModeTun, StringComparison.Ordinal);
    public bool IsProxyMode => !IsTunMode;

    public bool SystemWideMode
    {
        get => IsTunMode;
        set => SelectedConnectionMode = value ? ConnectionModeTun : ConnectionModeProxy;
    }

    public bool UseHybridRouting
    {
        get => _useHybridRouting;
        set => SetField(ref _useHybridRouting, value);
    }
    private bool _useHybridRouting;

    public bool AutoConnect
    {
        get => _autoConnect;
        set => SetField(ref _autoConnect, value);
    }
    private bool _autoConnect;

    public string AutoStartMode
    {
        get => _autoStartMode;
        set
        {
            if (SetField(ref _autoStartMode, value))
            {
                UpdateStartupRegistration();
            }
        }
    }
    private string _autoStartMode = AutoStartModeOff;

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => SetField(ref _minimizeToTray, value);
    }
    private bool _minimizeToTray;

    public bool AutoUpdate
    {
        get => _autoUpdate;
        set
        {
            if (SetField(ref _autoUpdate, value) && value && !_loadingSettings)
            {
                _ = CheckForUpdatesAsync();
            }
        }
    }
    private bool _autoUpdate;

    public bool UseNativeTheme
    {
        get => _useNativeTheme;
        set
        {
            if (SetField(ref _useNativeTheme, value))
            {
                ApplyTheme(IsDarkMode);
            }
        }
    }
    private bool _useNativeTheme;

    public bool KillSwitchEnabled
    {
        get => _killSwitchEnabled;
        set
        {
            if (value)
            {
                if (!IsTunMode || UseHybridRouting)
                {
                    StatusMessage = "Kill switch is available only in TUN/VPN Mode with Hybrid disabled.";
                    SetField(ref _killSwitchEnabled, false);
                    return;
                }

            }

            if (SetField(ref _killSwitchEnabled, value) && !_killSwitchEnabled)
            {
                if (!_loadingSettings)
                {
                    _ = Task.Run(() => DisableKillSwitchEmergencyBlock(allowElevation: IsTunMode));
                }
            }
        }
    }
    private bool _killSwitchEnabled;

    public bool UseTorBridges
    {
        get => _useTorBridges;
        set
        {
            if (SetField(ref _useTorBridges, value))
            {
                if (!value && _useCensoredMode)
                {
                    _useCensoredMode = false;
                    Raise(nameof(UseCensoredMode));
                }
                NotifyBridgeSettingsChanged();
            }
        }
    }
    private bool _useTorBridges;

    public bool UseCensoredMode
    {
        get => _useCensoredMode;
        set
        {
            if (SetField(ref _useCensoredMode, value))
            {
                if (value)
                {
                    ApplyCensoredDefaults();
                }

                NotifyBridgeSettingsChanged();
            }
        }
    }
    private bool _useCensoredMode;

    public string SelectedBridgeType
    {
        get => _selectedBridgeType;
        set
        {
            if (SetField(ref _selectedBridgeType, value))
            {
                NotifyBridgeSettingsChanged();
            }
        }
    }
    private string _selectedBridgeType = "obfs4";

    public string CustomBridges
    {
        get => _customBridges;
        set
        {
            if (SetField(ref _customBridges, value))
            {
                NotifyBridgeSettingsChanged();
            }
        }
    }
    private string _customBridges = string.Empty;

    public bool IsDarkMode
    {
        get => _isDarkMode;
        set
        {
            if (SetField(ref _isDarkMode, value))
            {
                ApplyTheme(_isDarkMode);
            }
        }
    }
    private bool _isDarkMode;

    public bool ShowLogs
    {
        get => _showLogs;
        set
        {
            if (SetField(ref _showLogs, value))
            {
                if (value)
                {
                    ShowAbout = false;
                    ShowSettings = false;
                }
                Raise(nameof(IsOverlayVisible));
            }
        }
    }

    public bool ShowAbout
    {
        get => _showAbout;
        set
        {
            if (SetField(ref _showAbout, value))
            {
                if (value)
                {
                    ShowLogs = false;
                    ShowSettings = false;
                }
                Raise(nameof(IsOverlayVisible));
            }
        }
    }

    public bool ShowSettings
    {
        get => _showSettings;
        set
        {
            if (SetField(ref _showSettings, value))
            {
                if (value)
                {
                    ShowLogs = false;
                    ShowAbout = false;
                }
                Raise(nameof(IsOverlayVisible));
            }
        }
    }

    public bool IsOverlayVisible => ShowLogs || ShowAbout || ShowSettings;

    public string AboutVersionText
    {
        get => _aboutVersionText;
        private set => SetField(ref _aboutVersionText, value);
    }

    public string AboutReleaseDateText
    {
        get => _aboutReleaseDateText;
        private set => SetField(ref _aboutReleaseDateText, value);
    }

    public string AboutText =>
        "=== Modes ===\n" +
        "\n" +
        "Proxy Mode (Recommended)\n" +
        "- Starts Tor (SOCKS5 on 127.0.0.1:9050)\n" +
        "- Sets Windows proxy to use Tor for apps that respect proxy settings\n" +
        "- Does NOT require Administrator\n" +
        "- Most stable, best for everyday browsing\n" +
        "- NOTE: DNS queries may leak! Apps can make direct DNS requests bypassing Tor.\n" +
        "  For maximum privacy, use TUN/VPN Mode or enable Censored Network Mode.\n" +
        "\n" +
        "TUN/VPN Mode (Admin)\n" +
        "- Starts Tor + sing-box + Wintun (virtual adapter)\n" +
        "- Can force routing rules at the OS level\n" +
        "- DNS queries are routed through Tor (no DNS leaks)\n" +
        "- REQUIRES Administrator\n" +
        "\n" +
        "Hybrid (browser via Tor)\n" +
        "- In TUN mode, only browsers are routed through Tor; everything else goes direct\n" +
        "- Useful when you want Tor browsing without breaking other apps\n" +
        "\n" +
        "=== Settings ===\n" +
        "\n" +
        "- Auto-Connect, Auto-Start (Off/On/Minimized), Minimize to Tray, Auto Update, Dark Mode, Native UI, Censored Mode, and Kill Switch are in the Settings tab.\n" +
        "- Exit Location: Automatic picks the best exit; country selections are hints only.\n" +
        "\n" +
        "=== Tor Bridges ===\n" +
        "\n" +
        "- Use pluggable transports like obfs4, snowflake, meek-azure, or webtunnel when Tor is blocked.\n" +
        "- Webtunnel needs webtunnel-client.exe.\n" +
        "- OnionHop can refresh built-in webtunnel bridges automatically, or you can paste your own.\n" +
        "- Enable in Settings and reconnect to apply.\n" +
        "\n" +
        "Webtunnel bridges\n" +
        "1) Choose transport: webtunnel.\n" +
        "2) OnionHop will refresh built-in bridges when available.\n" +
        "3) Paste custom lines from https://bridges.torproject.org/ if needed.\n" +
        "4) Reconnect.\n" +
        "\n" +
        "=== Actions ===\n" +
        "\n" +
        "Change Identity\n" +
        "- Action Center button requests a new Tor circuit (like Tor Browser). It can take a few seconds to reflect.\n" +
        "\n" +
        "Censored Network Mode\n" +
        "- Enables SNI-based bridges (snowflake/meek-azure) and Secure DNS (DoH) for restrictive networks.\n" +
        "\n" +
        "Auto-Connect\n" +
        "- Connect automatically when OnionHop starts.\n" +
        "\n" +
        "Auto-Start\n" +
        "- Launch on Windows sign-in; optional start minimized.\n" +
        "\n" +
        "Minimize to Tray\n" +
        "- Closing the window keeps OnionHop running in the tray.\n" +
        "\n" +
        "Auto Update\n" +
        "- Checks GitHub releases and offers updates.\n" +
        "\n" +
        "=== Safety & Privacy ===\n" +
        "\n" +
        "DNS Leak Warning (Proxy Mode)\n" +
        "- In Proxy Mode, DNS requests may bypass Tor and leak to your ISP.\n" +
        "- For best privacy: use TUN/VPN Mode or enable Censored Network Mode for DNS-over-HTTPS.\n" +
        "- You can test for leaks at: dnsleaktest.com or browserleaks.com\n" +
        "\n" +
        "Windows Defender\n" +
        "- Releases are not code-signed. Defender/SmartScreen may warn; verify the SHA-256 from the release notes.\n" +
        "\n" +
        "Native Windows UI\n" +
        "- Uses standard window chrome; Dark Mode still applies.\n" +
        "\n" +
        "Kill Switch\n" +
        "- Available only in TUN/VPN Mode with Hybrid disabled (strict).\n" +
        "- If the tunnel drops unexpectedly, OnionHop blocks outbound traffic via Windows Firewall to prevent leaks.\n" +
        "- Disconnect (as Administrator) to restore normal traffic.\n";

    public string ConnectionStatus
    {
        get => _connectionStatus;
        set => SetField(ref _connectionStatus, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        set => SetField(ref _statusBrush, value);
    }

    public string CurrentIp
    {
        get => _currentIp;
        set => SetField(ref _currentIp, value);
    }

    public double ConnectionProgress
    {
        get => _connectionProgress;
        set => SetField(ref _connectionProgress, value);
    }

    public bool IsDependencyDownloadInProgress
    {
        get => _dependencyDownloadInProgress;
        private set => SetField(ref _dependencyDownloadInProgress, value);
    }

    public double DependencyDownloadProgress
    {
        get => _dependencyDownloadProgress;
        private set => SetField(ref _dependencyDownloadProgress, value);
    }

    public string DependencyDownloadStatus
    {
        get => _dependencyDownloadStatus;
        private set => SetField(ref _dependencyDownloadStatus, value);
    }

    public string ConnectButtonText
        => _isConnected ? "Disconnect"
            : _isDisconnecting ? "Disconnecting..."
            : _isConnecting ? "Connecting..."
            : "Connect";
    private bool _isDisconnecting;

    public bool CanChangeIdentity => _isConnected && !_isConnecting;

    public MainWindow()
    {
        StartupLogger.Write("MainWindow ctor start.");
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            StartupLogger.Write("MainWindow InitializeComponent failed.", ex);
            throw;
        }
        StartupLogger.Write("MainWindow InitializeComponent done.");
        _customChrome = WindowChrome.GetWindowChrome(this);
        DataContext = this;

        _torService = new TorService(AppendLog);
        _torService.OutputReceived += OnTorDataReceived;
        _torService.Exited += OnTorExited;
        _vpnService = new VpnService(AppendLog);
        _vpnService.OutputReceived += OnSingBoxDataReceived;
        _vpnService.Exited += OnSingBoxExited;

        try
        {
            _processJob = new JobObject();
        }
        catch (Exception ex)
        {
            AppendLog($"Process job setup failed: {ex.Message}");
        }

        LoadBridgeConfig();
        LoadUserSettings();
        ApplyStartupArguments(Environment.GetCommandLineArgs());
        PrepareStartupVisibility();
        ApplyTheme(IsDarkMode);
        UpdateAboutMetadata();
        UpdateConnectVisualState();
        UpdateMaximizeGlyph();

        AppendLog("OnionHop started.");
        SystemEvents.SessionEnding += OnSessionEnding;

        if (IsKillSwitchEmergencyBlockActive() && !IsAdministrator())
        {
            StatusMessage = "Kill switch is active and blocking traffic. Restart OnionHop as Administrator and disconnect to restore (or reboot to clear).";
        }
        else if (IsAdministrator())
        {
            _ = Task.Run(() => DisableKillSwitchEmergencyBlock());
        }

        StartupLogger.Write("MainWindow ctor complete.");
    }

    private void UpdateAboutMetadata()
    {
        var version = GetCurrentVersion();
        AboutVersionText = $"Version: v{version}";

        var buildDate = GetBuildDate();
        AboutReleaseDateText = buildDate.HasValue
            ? $"Release date: {buildDate.Value:yyyy-MM-dd}"
            : "Release date: Unknown";
    }

    private static DateTime? GetBuildDate()
    {
        var entry = Assembly.GetEntryAssembly();
        if (entry == null)
        {
            return null;
        }

        var location = entry.Location;
        if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
        {
            return null;
        }

        try
        {
            return File.GetLastWriteTime(location);
        }
        catch
        {
            return null;
        }
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        _isExiting = true;
    }

    private sealed class PluggableTransportConfig
    {
        public string? RecommendedDefault { get; set; }
        public Dictionary<string, string> PluggableTransports { get; set; } = new();
        public Dictionary<string, List<string>> Bridges { get; set; } = new();
    }

    private void LoadBridgeConfig()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, DefaultPtConfigRelativePath);
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                _ptConfig = JsonSerializer.Deserialize<PluggableTransportConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                AppendLog($"Bridge config load failed: {ex.Message}");
            }
        }

        var bridgeKeys = _ptConfig?.Bridges?.Keys?
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<string>();

        if (bridgeKeys.Count == 0)
        {
            bridgeKeys.AddRange(new[] { "obfs4", "snowflake", "meek-azure" });
        }

        if (!bridgeKeys.Any(key => string.Equals(key, WebTunnelBridgeType, StringComparison.OrdinalIgnoreCase)))
        {
            bridgeKeys.Add(WebTunnelBridgeType);
        }

        bridgeKeys = bridgeKeys
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        BridgeTypes.Clear();
        foreach (var key in bridgeKeys)
        {
            BridgeTypes.Add(key);
        }

        var defaultType = _ptConfig?.RecommendedDefault;
        if (string.IsNullOrWhiteSpace(defaultType) || !BridgeTypes.Contains(defaultType))
        {
            defaultType = BridgeTypes.FirstOrDefault() ?? "obfs4";
        }

        if (string.IsNullOrWhiteSpace(_selectedBridgeType) || !BridgeTypes.Contains(_selectedBridgeType))
        {
            var wasLoading = _loadingSettings;
            _loadingSettings = true;
            _selectedBridgeType = defaultType;
            Raise(nameof(SelectedBridgeType));
            _loadingSettings = wasLoading;
        }

        if (!_webTunnelBridgeFetchStarted && !HasUsableWebTunnelBridges())
        {
            _webTunnelBridgeFetchStarted = true;
            _ = Task.Run(() => TryRefreshWebTunnelBridgesAsync());
        }
    }

    private bool HasUsableWebTunnelBridges()
    {
        if (_ptConfig?.Bridges == null)
        {
            return false;
        }

        if (!_ptConfig.Bridges.TryGetValue(WebTunnelBridgeType, out var bridges))
        {
            return false;
        }

        return bridges.Any(line => !IsPlaceholderBridgeLine(line));
    }

    private async Task TryRefreshWebTunnelBridgesAsync()
    {
        try
        {
            var client = GetDependencyHttpClient();
            var bridges = await FetchWebTunnelBridgeLinesAsync(client, CancellationToken.None).ConfigureAwait(false);
            if (bridges.Count == 0)
            {
                AppendLog("No webtunnel bridges fetched (BridgeDB requires CAPTCHA). Get bridges manually from bridges.torproject.org.");
                _webTunnelBridgeFetchStarted = false;
                return;
            }

            var configPath = Path.Combine(AppContext.BaseDirectory, DefaultPtConfigRelativePath);
            if (!File.Exists(configPath))
            {
                AppendLog("Bridge config not found for webtunnel refresh.");
                _webTunnelBridgeFetchStarted = false;
                return;
            }

            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<PluggableTransportConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (config == null)
            {
                _webTunnelBridgeFetchStarted = false;
                return;
            }

            config.Bridges ??= new Dictionary<string, List<string>>();
            if (config.Bridges.TryGetValue(WebTunnelBridgeType, out var existing)
                && existing.Any(line => !IsPlaceholderBridgeLine(line)))
            {
                return;
            }

            config.Bridges[WebTunnelBridgeType] = bridges.ToList();
            var updatedJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, updatedJson);

            Dispatcher.Invoke(() =>
            {
                _ptConfig = config;
            });

            AppendLog($"Loaded {bridges.Count} webtunnel bridges.");
        }
        catch (Exception ex)
        {
            _webTunnelBridgeFetchStarted = false;
            AppendLog($"Webtunnel bridge refresh failed: {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<string>> FetchWebTunnelBridgeLinesAsync(HttpClient client, CancellationToken token)
    {
        try
        {
            using var response = await client.GetAsync(WebTunnelBridgeSourceUrl, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                AppendLog($"Webtunnel bridge fetch failed: HTTP {(int)response.StatusCode}.");
                return Array.Empty<string>();
            }

            var content = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var extracted = ExtractWebTunnelBridgeLines(content);
            AppendLog($"Webtunnel fetch: extracted {extracted.Count} bridge(s) from response.");
            
            var lines = extracted.Where(line => !IsPlaceholderBridgeLine(line)).ToList();
            if (lines.Count < extracted.Count)
            {
                AppendLog($"Webtunnel fetch: {extracted.Count - lines.Count} bridge(s) filtered as placeholders.");
            }

            return lines;
        }
        catch (Exception ex)
        {
            AppendLog($"Webtunnel bridge fetch error: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private static List<string> ExtractWebTunnelBridgeLines(string content)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(content))
        {
            return results;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // Match webtunnel bridges - they may span multiple lines in HTML
        // Pattern: webtunnel [IP]:port FINGERPRINT url=... ver=...
        var matches = Regex.Matches(content, @"webtunnel\s+\[[^\]]+\]:\d+\s+[A-F0-9]+\s+url=[^\s<]+(?:\s+ver=[^\s<]+)?", 
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        
        foreach (Match match in matches)
        {
            var line = WebUtility.HtmlDecode(match.Value).Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Normalize whitespace
            line = Regex.Replace(line, @"\s+", " ").Trim();
            if (seen.Add(line))
            {
                results.Add(line);
            }
        }

        if (results.Count > 0)
        {
            return results;
        }

        // Fallback: try line-by-line parsing for plain text responses
        foreach (var raw in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (!line.StartsWith("webtunnel ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            line = Regex.Replace(line, @"\s+", " ").Trim();
            if (seen.Add(line))
            {
                results.Add(line);
            }
        }

        return results;
    }

    private void LoadUserSettings()
    {
        try
        {
            var settings = _settingsService.Load();
            if (settings == null)
            {
                return;
            }

            _loadingSettings = true;

            AutoConnect = settings.AutoConnect;
            AutoStartMode = ResolveAutoStartMode(settings);
            MinimizeToTray = settings.MinimizeToTray;
            AutoUpdate = settings.AutoUpdate;
            IsDarkMode = settings.IsDarkMode;
            UseNativeTheme = settings.UseNativeTheme;

            if (!string.IsNullOrWhiteSpace(settings.SelectedLocation) && Locations.Contains(settings.SelectedLocation))
            {
                SelectedLocation = settings.SelectedLocation;
            }

            if (!string.IsNullOrWhiteSpace(settings.SelectedConnectionMode) && ConnectionModes.Contains(settings.SelectedConnectionMode))
            {
                SelectedConnectionMode = settings.SelectedConnectionMode;
            }

            UseHybridRouting = settings.UseHybridRouting;
            KillSwitchEnabled = settings.KillSwitchEnabled;
            UseTorBridges = settings.UseTorBridges;
            if (!string.IsNullOrWhiteSpace(settings.SelectedBridgeType) && BridgeTypes.Contains(settings.SelectedBridgeType))
            {
                SelectedBridgeType = settings.SelectedBridgeType;
            }

            CustomBridges = settings.CustomBridges ?? string.Empty;
            UseCensoredMode = settings.UseCensoredMode;
        }
        catch (Exception ex)
        {
            AppendLog($"Settings load failed: {ex.Message}");
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private string ResolveAutoStartMode(UserSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AutoStartMode))
        {
            return settings.StartWithWindows
                ? settings.StartMinimized ? AutoStartModeMinimized : AutoStartModeOn
                : AutoStartModeOff;
        }

        if (string.Equals(settings.AutoStartMode, AutoStartModeOn, StringComparison.OrdinalIgnoreCase))
        {
            return AutoStartModeOn;
        }

        if (string.Equals(settings.AutoStartMode, AutoStartModeMinimized, StringComparison.OrdinalIgnoreCase))
        {
            return AutoStartModeMinimized;
        }

        return AutoStartModeOff;
    }

    private void ScheduleSaveUserSettings()
    {
        if (_loadingSettings)
        {
            return;
        }

        _settingsSaveCts?.Cancel();
        _settingsSaveCts?.Dispose();
        _settingsSaveCts = new CancellationTokenSource();
        var token = _settingsSaveCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, token);
                SaveUserSettings();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppendLog($"Settings save failed: {ex.Message}");
            }
        }, token);
    }

    private void SaveUserSettings()
    {
        var settings = new UserSettings
        {
            AutoConnect = AutoConnect,
            AutoStartMode = AutoStartMode,
            StartWithWindows = !string.Equals(AutoStartMode, AutoStartModeOff, StringComparison.OrdinalIgnoreCase),
            StartMinimized = string.Equals(AutoStartMode, AutoStartModeMinimized, StringComparison.OrdinalIgnoreCase),
            MinimizeToTray = MinimizeToTray,
            AutoUpdate = AutoUpdate,
            KillSwitchEnabled = KillSwitchEnabled,
            IsDarkMode = IsDarkMode,
            UseNativeTheme = UseNativeTheme,
            SelectedLocation = SelectedLocation,
            SelectedConnectionMode = SelectedConnectionMode,
            UseHybridRouting = UseHybridRouting,
            UseTorBridges = UseTorBridges,
            UseCensoredMode = UseCensoredMode,
            SelectedBridgeType = SelectedBridgeType,
            CustomBridges = CustomBridges
        };

        _settingsService.Save(settings);
    }

    private void UpdateStartupRegistration()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegistryKey, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(AutoStartRegistryKey);
            if (key == null)
            {
                AppendLog("Startup registration failed: registry key not found.");
                return;
            }

            if (string.IsNullOrWhiteSpace(AutoStartMode) ||
                string.Equals(AutoStartMode, AutoStartModeOff, StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue(AutoStartValueName, false);
                return;
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                AppendLog("Startup registration failed: executable path unavailable.");
                return;
            }

            var command = $"\"{exePath}\"";
            if (string.Equals(AutoStartMode, AutoStartModeMinimized, StringComparison.OrdinalIgnoreCase))
            {
                command = $"{command} --minimized";
            }

            key.SetValue(AutoStartValueName, command);
        }
        catch (Exception ex)
        {
            AppendLog($"Startup registration failed: {ex.Message}");
        }
    }

    private void NotifyBridgeSettingsChanged()
    {
        if (_isConnected)
        {
            StatusMessage = "Bridge settings will apply after reconnecting.";
        }
    }

    private void ApplyCensoredDefaults()
    {
        if (!UseTorBridges)
        {
            UseTorBridges = true;
        }

        if (HasCustomBridgeLines())
        {
            return;
        }

        var preferred = ResolveCensoredBridgeType();
        if (!string.IsNullOrWhiteSpace(preferred) &&
            !string.Equals(SelectedBridgeType, preferred, StringComparison.OrdinalIgnoreCase))
        {
            SelectedBridgeType = preferred;
        }
    }

    private string? ResolveCensoredBridgeType()
    {
        if (BridgeTypes.Contains(CensoredBridgePrimary))
        {
            return CensoredBridgePrimary;
        }

        if (BridgeTypes.Contains(CensoredBridgeFallback))
        {
            return CensoredBridgeFallback;
        }

        return BridgeTypes.FirstOrDefault();
    }

    private void ApplyTheme(bool dark)
    {
        if (UseNativeTheme)
        {
            ApplyNativeTheme(dark);
            return;
        }

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ApplyWindowChrome(true);
        var appBg = dark ? Color.FromRgb(12, 16, 26) : Color.FromRgb(233, 237, 245);
        var cardBg = dark ? Color.FromRgb(26, 33, 48) : Colors.White;
        var navBg = dark ? Color.FromRgb(18, 24, 38) : Color.FromRgb(247, 249, 253);
        var titleBarBg = dark ? Color.FromRgb(18, 24, 38) : Color.FromRgb(246, 248, 253);
        var titleBtnBg = dark ? Color.FromRgb(26, 33, 48) : Color.FromRgb(238, 242, 251);
        var titleBtnHover = dark ? Color.FromRgb(33, 42, 61) : Color.FromRgb(226, 233, 255);
        var titleBtnPressed = dark ? Color.FromRgb(40, 51, 74) : Color.FromRgb(207, 216, 247);
        var navBtnFg = dark ? Color.FromRgb(221, 232, 248) : Color.FromRgb(54, 65, 82);
        var navBtnBg = dark ? Color.FromRgb(26, 33, 48) : Color.FromRgb(243, 246, 251);
        var navBtnHover = dark ? Color.FromRgb(33, 42, 61) : Color.FromRgb(227, 236, 255);
        var segmentedBg = dark ? Color.FromRgb(26, 33, 48) : Color.FromRgb(246, 247, 251);
        var primaryText = dark ? Color.FromRgb(226, 234, 248) : Color.FromRgb(42, 50, 66);
        var secondaryText = dark ? Color.FromRgb(163, 178, 205) : Color.FromRgb(123, 131, 150);
        var tertiaryText = dark ? Color.FromRgb(136, 151, 179) : Color.FromRgb(107, 116, 136);
        var inputBorder = dark ? Color.FromRgb(46, 58, 84) : Color.FromRgb(224, 228, 237);
        var toggleThumb = dark ? Color.FromRgb(240, 245, 255) : Colors.White;
        var toggleTrack = dark ? Color.FromRgb(76, 86, 108) : Color.FromRgb(211, 215, 225);
        var comboBg = dark ? Color.FromRgb(26, 33, 48) : Color.FromRgb(246, 248, 252);
        var comboBorder = dark ? Color.FromRgb(46, 58, 84) : Color.FromRgb(224, 228, 237);
        var comboItemHover = dark ? Color.FromRgb(33, 42, 61) : Color.FromRgb(238, 242, 251);
        var comboItemSelected = dark ? Color.FromRgb(40, 51, 74) : Color.FromRgb(221, 229, 251);
        var hero = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new(Color.FromRgb(dark ? (byte)15 : (byte)47, dark ? (byte)23 : (byte)42, dark ? (byte)48 : (byte)124), 0),
                new(Color.FromRgb(dark ? (byte)28 : (byte)51, dark ? (byte)43 : (byte)76, dark ? (byte)77 : (byte)139), 0.4),
                new(Color.FromRgb(dark ? (byte)15 : (byte)45, dark ? (byte)118 : (byte)165, dark ? (byte)110 : (byte)181), 1)
            }
        };

        Resources["AppBackgroundBrush"] = new SolidColorBrush(appBg);
        Resources["CardBackgroundBrush"] = new SolidColorBrush(cardBg);
        Resources["NavBackgroundBrush"] = new SolidColorBrush(navBg);
        Resources["TitleBarBrush"] = new SolidColorBrush(titleBarBg);
        Resources["TitleButtonBrush"] = new SolidColorBrush(titleBtnBg);
        Resources["TitleButtonHoverBrush"] = new SolidColorBrush(titleBtnHover);
        Resources["TitleButtonPressedBrush"] = new SolidColorBrush(titleBtnPressed);
        Resources["NavButtonForegroundBrush"] = new SolidColorBrush(navBtnFg);
        Resources["NavButtonBackgroundBrush"] = new SolidColorBrush(navBtnBg);
        Resources["NavButtonHoverBrush"] = new SolidColorBrush(navBtnHover);
        Resources["SegmentedButtonBackgroundBrush"] = new SolidColorBrush(segmentedBg);
        Resources["HeroGradient"] = hero;
        Resources["PrimaryTextBrush"] = new SolidColorBrush(primaryText);
        Resources["SecondaryTextBrush"] = new SolidColorBrush(secondaryText);
        Resources["TertiaryTextBrush"] = new SolidColorBrush(tertiaryText);
        Resources["InputBorderBrush"] = new SolidColorBrush(inputBorder);
        Resources["ToggleThumbBrush"] = new SolidColorBrush(toggleThumb);
        Resources["ToggleTrackBrush"] = new SolidColorBrush(toggleTrack);
        Resources["ToggleTrackCheckedBrush"] = new SolidColorBrush(Color.FromRgb(54, 193, 122));
        Resources["ComboBackgroundBrush"] = new SolidColorBrush(comboBg);
        Resources["ComboBorderBrush"] = new SolidColorBrush(comboBorder);
        Resources["ComboForegroundBrush"] = new SolidColorBrush(primaryText);
        Resources["ComboItemHoverBrush"] = new SolidColorBrush(comboItemHover);
        Resources["ComboItemSelectedBrush"] = new SolidColorBrush(comboItemSelected);
        Resources["WindowControlBorderBrush"] = new SolidColorBrush(dark ? Color.FromRgb(46, 58, 84) : Color.FromRgb(224, 228, 237));
        ApplyHeroPalette(light: false);
        Resources["HeroComboStyle"] = Resources["DarkCombo"];
        Background = (Brush)Resources["AppBackgroundBrush"];
    }

    private void ApplyNativeTheme(bool dark)
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        ApplyWindowChrome(false);

        if (dark)
        {
            var appBg = Color.FromRgb(12, 16, 26);
            var cardBg = Color.FromRgb(26, 33, 48);
            var navBg = Color.FromRgb(18, 24, 38);
            var titleBarBg = Color.FromRgb(18, 24, 38);
            var titleBtnBg = Color.FromRgb(26, 33, 48);
            var titleBtnHover = Color.FromRgb(33, 42, 61);
            var titleBtnPressed = Color.FromRgb(40, 51, 74);
            var navBtnFg = Color.FromRgb(221, 232, 248);
            var navBtnBg = Color.FromRgb(26, 33, 48);
            var navBtnHover = Color.FromRgb(33, 42, 61);
            var segmentedBg = Color.FromRgb(26, 33, 48);
            var primaryText = Color.FromRgb(226, 234, 248);
            var secondaryText = Color.FromRgb(163, 178, 205);
            var tertiaryText = Color.FromRgb(136, 151, 179);
            var inputBorder = Color.FromRgb(46, 58, 84);
            var toggleThumb = Color.FromRgb(240, 245, 255);
            var toggleTrack = Color.FromRgb(76, 86, 108);
            var comboBg = Color.FromRgb(26, 33, 48);
            var comboBorder = Color.FromRgb(46, 58, 84);
            var comboItemHover = Color.FromRgb(33, 42, 61);
            var comboItemSelected = Color.FromRgb(40, 51, 74);
            var hero = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(1, 1),
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromRgb(15, 23, 48), 0),
                    new(Color.FromRgb(28, 43, 77), 0.4),
                    new(Color.FromRgb(15, 118, 110), 1)
                }
            };

            Resources["AppBackgroundBrush"] = new SolidColorBrush(appBg);
            Resources["CardBackgroundBrush"] = new SolidColorBrush(cardBg);
            Resources["NavBackgroundBrush"] = new SolidColorBrush(navBg);
            Resources["TitleBarBrush"] = new SolidColorBrush(titleBarBg);
            Resources["TitleButtonBrush"] = new SolidColorBrush(titleBtnBg);
            Resources["TitleButtonHoverBrush"] = new SolidColorBrush(titleBtnHover);
            Resources["TitleButtonPressedBrush"] = new SolidColorBrush(titleBtnPressed);
            Resources["NavButtonForegroundBrush"] = new SolidColorBrush(navBtnFg);
            Resources["NavButtonBackgroundBrush"] = new SolidColorBrush(navBtnBg);
            Resources["NavButtonHoverBrush"] = new SolidColorBrush(navBtnHover);
            Resources["SegmentedButtonBackgroundBrush"] = new SolidColorBrush(segmentedBg);
            Resources["HeroGradient"] = hero;
            Resources["PrimaryTextBrush"] = new SolidColorBrush(primaryText);
            Resources["SecondaryTextBrush"] = new SolidColorBrush(secondaryText);
            Resources["TertiaryTextBrush"] = new SolidColorBrush(tertiaryText);
            Resources["InputBorderBrush"] = new SolidColorBrush(inputBorder);
            Resources["ToggleThumbBrush"] = new SolidColorBrush(toggleThumb);
            Resources["ToggleTrackBrush"] = new SolidColorBrush(toggleTrack);
            Resources["ToggleTrackCheckedBrush"] = new SolidColorBrush(Color.FromRgb(54, 193, 122));
            Resources["ComboBackgroundBrush"] = new SolidColorBrush(comboBg);
            Resources["ComboBorderBrush"] = new SolidColorBrush(comboBorder);
            Resources["ComboForegroundBrush"] = new SolidColorBrush(primaryText);
            Resources["ComboItemHoverBrush"] = new SolidColorBrush(comboItemHover);
            Resources["ComboItemSelectedBrush"] = new SolidColorBrush(comboItemSelected);
            Resources["WindowControlBorderBrush"] = new SolidColorBrush(Color.FromRgb(46, 58, 84));
        }
        else
        {
            Resources["AppBackgroundBrush"] = SystemColors.WindowBrush;
            Resources["CardBackgroundBrush"] = SystemColors.ControlBrush;
            Resources["NavBackgroundBrush"] = SystemColors.ControlBrush;
            Resources["TitleBarBrush"] = SystemColors.ControlBrush;
            Resources["TitleButtonBrush"] = SystemColors.ControlBrush;
            Resources["TitleButtonHoverBrush"] = SystemColors.ControlLightBrush;
            Resources["TitleButtonPressedBrush"] = SystemColors.ControlDarkBrush;
            Resources["NavButtonForegroundBrush"] = SystemColors.ControlTextBrush;
            Resources["NavButtonBackgroundBrush"] = SystemColors.ControlLightBrush;
            Resources["NavButtonHoverBrush"] = SystemColors.ControlLightBrush;
            Resources["SegmentedButtonBackgroundBrush"] = SystemColors.ControlLightBrush;
            Resources["HeroGradient"] = SystemColors.ControlLightBrush;
            Resources["PrimaryTextBrush"] = SystemColors.ControlTextBrush;
            Resources["SecondaryTextBrush"] = SystemColors.GrayTextBrush;
            Resources["TertiaryTextBrush"] = SystemColors.GrayTextBrush;
            Resources["InputBorderBrush"] = SystemColors.ActiveBorderBrush;
            Resources["ToggleThumbBrush"] = SystemColors.WindowBrush;
            Resources["ToggleTrackBrush"] = SystemColors.ControlDarkBrush;
            Resources["ToggleTrackCheckedBrush"] = SystemColors.HighlightBrush;
            Resources["ComboBackgroundBrush"] = SystemColors.WindowBrush;
            Resources["ComboBorderBrush"] = SystemColors.ActiveBorderBrush;
            Resources["ComboForegroundBrush"] = SystemColors.ControlTextBrush;
            Resources["ComboItemHoverBrush"] = SystemColors.ControlLightBrush;
            Resources["ComboItemSelectedBrush"] = SystemColors.HighlightBrush;
            Resources["WindowControlBorderBrush"] = SystemColors.ActiveBorderBrush;
        }

        ApplyHeroPalette(light: !dark);
        Resources["HeroComboStyle"] = Resources[dark ? "DarkCombo" : "LightCombo"];

        if (!UseNativeTheme)
        {
            Background = System.Windows.Media.Brushes.Transparent;
            if (RootGrid != null)
            {
                RootGrid.Background = (Brush)Resources["AppBackgroundBrush"];
            }
        }
        else
        {
             Background = (Brush)Resources["AppBackgroundBrush"];
             if (RootGrid != null)
             {
                 RootGrid.Background = System.Windows.Media.Brushes.Transparent;
             }
        }
        
        TrySetImmersiveDarkMode(dark);
    }

    private void ApplyWindowChrome(bool enable)
    {
        if (!enable)
        {
            WindowChrome.SetWindowChrome(this, null);
            return;
        }

        _customChrome ??= new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(-1),
            UseAeroCaptionButtons = false
        };

        WindowChrome.SetWindowChrome(this, _customChrome);
    }

    private void ApplyHeroPalette(bool light)
    {
        if (light)
        {
            Resources["HeroTitleBrush"] = SystemColors.ControlTextBrush;
            Resources["HeroSubtitleBrush"] = SystemColors.GrayTextBrush;
            Resources["HeroCardBackgroundBrush"] = SystemColors.WindowBrush;
            Resources["HeroCardTextBrush"] = SystemColors.ControlTextBrush;
            Resources["HeroCardSecondaryTextBrush"] = SystemColors.GrayTextBrush;
            Resources["HeroCardValueBrush"] = SystemColors.ControlTextBrush;
            Resources["HeroStatusTextBrush"] = SystemColors.GrayTextBrush;
        }
        else
        {
            Resources["HeroTitleBrush"] = new SolidColorBrush(Colors.White);
            Resources["HeroSubtitleBrush"] = new SolidColorBrush(Color.FromRgb(219, 232, 255));
            Resources["HeroCardBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(43, 47, 115));
            Resources["HeroCardTextBrush"] = new SolidColorBrush(Color.FromRgb(223, 230, 255));
            Resources["HeroCardSecondaryTextBrush"] = new SolidColorBrush(Color.FromRgb(219, 232, 255));
            Resources["HeroCardValueBrush"] = new SolidColorBrush(Colors.White);
            Resources["HeroStatusTextBrush"] = new SolidColorBrush(Color.FromRgb(223, 239, 255));
        }
    }

    private void TrySetImmersiveDarkMode(bool enabled)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            if (!IsLoaded)
            {
                Dispatcher.BeginInvoke(new Action(() => TrySetImmersiveDarkMode(enabled)));
            }
            return;
        }

        var value = enabled ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref value, sizeof(int));
    }

    private void PrepareStartupVisibility()
    {
        if (_startMinimizedOnLaunch && MinimizeToTray)
        {
            _startHiddenToTray = true;
            ShowInTaskbar = false;
            Opacity = 0;
        }
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon != null)
        {
            return;
        }

        _trayIconImage = GetTrayIcon();
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "OnionHop",
            Icon = _trayIconImage,
            Visible = false
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        var openItem = new System.Windows.Forms.ToolStripMenuItem("Open OnionHop");
        openItem.Click += (_, _) => Dispatcher.Invoke(ShowFromTray);
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Dispatcher.Invoke(ExitApplication);
        menu.Items.Add(openItem);
        menu.Items.Add(exitItem);
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private static System.Drawing.Icon GetTrayIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "OnionHop.ico");
            if (File.Exists(iconPath))
            {
                return new System.Drawing.Icon(iconPath);
            }

            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon != null)
                {
                    return (System.Drawing.Icon)icon.Clone();
                }
            }
        }
        catch
        {
        }

        return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }

    private void HideToTray(bool showBalloon = true)
    {
        try
        {
            EnsureTrayIcon();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = true;
                if (showBalloon && !_trayBalloonShown)
                {
                    _trayBalloonShown = true;
                    _trayIcon.BalloonTipTitle = "OnionHop";
                    _trayIcon.BalloonTipText = "OnionHop is still running in the tray.";
                    _trayIcon.ShowBalloonTip(2000);
                }
            }

            ShowInTaskbar = false;
            Hide();
        }
        catch (Exception ex)
        {
            AppendLog($"Minimize to tray failed: {ex.Message}");
            StatusMessage = "Minimize to tray failed; disabling it.";
            MinimizeToTray = false;
            Opacity = 1;
            ShowInTaskbar = true;
            WindowState = WindowState.Minimized;
        }
    }

    private void ShowFromTray()
    {
        Opacity = 1;
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
        }
    }

    public void RestoreFromExternalActivation()
    {
        Opacity = 1;
        if (MinimizeToTray)
        {
            ShowInTaskbar = true;
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
            }
        }

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void HandleCloseRequest()
    {
        if (MinimizeToTray && !_isExiting)
        {
            HideToTray();
            return;
        }

        ExitApplication();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
        }
        if (Application.Current != null)
        {
            Application.Current.Shutdown();
            return;
        }

        Close();
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_startMinimizedOnLaunch)
        {
            if (MinimizeToTray)
            {
                HideToTray(showBalloon: false);
            }
            else
            {
                WindowState = WindowState.Minimized;
            }
        }
        if (_startHiddenToTray)
        {
            Opacity = 1;
            _startHiddenToTray = false;
        }

        var dependenciesReady = await EnsureDependenciesAsync();
        if (dependenciesReady && AutoConnect)
        {
            await ConnectAsync();
        }

        if (AutoUpdate)
        {
            _ = CheckForUpdatesAsync();
        }
    }

    private Task<bool> EnsureDependenciesAsync()
    {
        return _dependencyEnsureTask ??= EnsureDependenciesCoreAsync();
    }

    private async Task<bool> EnsureDependenciesCoreAsync()
    {
        var torPath = Path.Combine(AppContext.BaseDirectory, DefaultTorRelativePath);
        var torGenCertPath = Path.Combine(AppContext.BaseDirectory, "tor\\tor-gencert.exe");
        var geoipPath = Path.Combine(AppContext.BaseDirectory, "tor\\geoip");
        var geoip6Path = Path.Combine(AppContext.BaseDirectory, "tor\\geoip6");
        var ptDir = Path.Combine(AppContext.BaseDirectory, "tor\\pluggable_transports");
        var singBoxPath = Path.Combine(AppContext.BaseDirectory, DefaultSingBoxRelativePath);
        var wintunPath = Path.Combine(AppContext.BaseDirectory, DefaultWintunRelativePath);

        var needsTor = !File.Exists(torPath)
                       || !File.Exists(torGenCertPath)
                       || !File.Exists(geoipPath)
                       || !File.Exists(geoip6Path)
                       || !Directory.Exists(ptDir);
        var needsSingBox = !File.Exists(singBoxPath);
        var needsWintun = !File.Exists(wintunPath);

        if (!needsTor && !needsSingBox && !needsWintun)
        {
            return true;
        }

        IsDependencyDownloadInProgress = true;
        DependencyDownloadStatus = "Preparing downloads...";
        DependencyDownloadProgress = 0;
        StatusMessage = "Downloading components...";

        var succeeded = false;
        var tempRoot = Path.Combine(Path.GetTempPath(), "OnionHop", "deps");
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(torPath) ?? AppContext.BaseDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(singBoxPath) ?? AppContext.BaseDirectory);
            Directory.CreateDirectory(ptDir);

            var client = GetDependencyHttpClient();
            var steps = new List<(string Label, Func<Task> Action)>();
            if (needsTor)
            {
                steps.Add(("Downloading Tor...", () => DownloadTorAsync(client, tempRoot, torPath, ptDir)));
            }
            if (needsSingBox)
            {
                steps.Add(("Downloading sing-box...", () => DownloadSingBoxAsync(client, tempRoot, singBoxPath)));
            }
            if (needsWintun)
            {
                steps.Add(("Downloading Wintun...", () => DownloadWintunAsync(client, tempRoot, wintunPath)));
            }

            for (var i = 0; i < steps.Count; i++)
            {
                DependencyDownloadStatus = steps[i].Label;
                DependencyDownloadProgress = i / (double)steps.Count;
                await steps[i].Action();
                DependencyDownloadProgress = (i + 1) / (double)steps.Count;
            }

            EnsurePluggableTransportConfig(Path.Combine(ptDir, "pt_config.json"));
            _webTunnelBridgeFetchStarted = false;
            LoadBridgeConfig();

            StatusMessage = "Components ready.";
            succeeded = true;
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"Dependency download failed: {ex.Message}");
            StatusMessage = $"Dependency download failed: {ex.Message}";
            return false;
        }
        finally
        {
            IsDependencyDownloadInProgress = false;
            if (!succeeded)
            {
                _dependencyEnsureTask = null;
            }
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
            catch
            {
            }
        }
    }

    private static HttpClient GetDependencyHttpClient()
    {
        return HttpClientFactory.LongTimeout;
    }

    private async Task DownloadTorAsync(HttpClient client, string tempRoot, string torPath, string ptDir)
    {
        var version = await GetLatestTorVersionAsync(client);
        var fileName = $"tor-expert-bundle-windows-x86_64-{version}.tar.gz";
        var primaryUrl = $"{TorBaseUrl}/{version}/{fileName}";
        var archiveUrl = $"{TorArchiveBaseUrl}/{version}/{fileName}";
        var torArchivePath = Path.Combine(tempRoot, "tor.tar.gz");

        await DownloadWithFallbackAsync(client, new[] { primaryUrl, archiveUrl }, torArchivePath);

        await Task.Run(() =>
        {
            var extractRoot = Path.Combine(tempRoot, "tor_extract");
            if (Directory.Exists(extractRoot))
            {
                Directory.Delete(extractRoot, true);
            }
            Directory.CreateDirectory(extractRoot);
            ExtractTarGz(torArchivePath, extractRoot);

            var extractedTorRoot = Path.Combine(extractRoot, "tor");
            if (!Directory.Exists(extractedTorRoot))
            {
                throw new InvalidOperationException("Tor extraction failed or unexpected structure.");
            }

            var torDir = Path.GetDirectoryName(torPath) ?? AppContext.BaseDirectory;
            var torGenCertPath = Path.Combine(torDir, "tor-gencert.exe");
            var geoipPath = Path.Combine(torDir, "geoip");
            var geoip6Path = Path.Combine(torDir, "geoip6");

            File.Copy(Path.Combine(extractedTorRoot, "tor.exe"), torPath, true);
            File.Copy(Path.Combine(extractedTorRoot, "tor-gencert.exe"), torGenCertPath, true);

            var dataRoot = Path.Combine(extractRoot, "data");
            var geoipSource = Path.Combine(dataRoot, "geoip");
            var geoip6Source = Path.Combine(dataRoot, "geoip6");
            if (File.Exists(geoipSource))
            {
                File.Copy(geoipSource, geoipPath, true);
            }
            if (File.Exists(geoip6Source))
            {
                File.Copy(geoip6Source, geoip6Path, true);
            }

            var extractedPtDir = Path.Combine(extractedTorRoot, "pluggable_transports");
            if (Directory.Exists(extractedPtDir))
            {
                CopyDirectory(extractedPtDir, ptDir, overwrite: true, preserveFileName: "pt_config.json");
            }

            var obfs4proxy = Path.Combine(ptDir, "obfs4proxy.exe");
            var lyrebird = Path.Combine(ptDir, "lyrebird.exe");
            if (!File.Exists(lyrebird) && File.Exists(obfs4proxy))
            {
                File.Move(obfs4proxy, lyrebird);
            }
        });
    }

    private static async Task DownloadSingBoxAsync(HttpClient client, string tempRoot, string singBoxPath)
    {
        using var response = await client.GetAsync(SingBoxApiUrl);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Failed to query sing-box releases.");
        }

        var json = await response.Content.ReadAsStringAsync();
        var release = JsonSerializer.Deserialize<GitHubRelease>(json);
        var asset = release?.Assets?.FirstOrDefault(a => a.Name != null
                                                        && a.Name.Contains("windows-amd64.zip", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(asset?.BrowserDownloadUrl))
        {
            throw new InvalidOperationException("No sing-box windows-amd64 asset found.");
        }

        var zipPath = Path.Combine(tempRoot, "sing-box.zip");
        await DownloadToFileAsync(client, asset.BrowserDownloadUrl, zipPath);

        await Task.Run(() =>
        {
            var extractDir = Path.Combine(tempRoot, "sing-box");
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, true);
            }
            ZipFile.ExtractToDirectory(zipPath, extractDir, true);

            var exePath = Directory.GetFiles(extractDir, "sing-box.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exePath == null)
            {
                throw new FileNotFoundException("sing-box.exe not found in archive.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(singBoxPath) ?? AppContext.BaseDirectory);
            File.Copy(exePath, singBoxPath, true);
        });
    }

    private static async Task DownloadWintunAsync(HttpClient client, string tempRoot, string wintunPath)
    {
        var zipPath = Path.Combine(tempRoot, "wintun.zip");
        await DownloadToFileAsync(client, WintunUrl, zipPath);

        await Task.Run(() =>
        {
            var extractDir = Path.Combine(tempRoot, "wintun");
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, true);
            }
            ZipFile.ExtractToDirectory(zipPath, extractDir, true);

            var dllPath = Directory.GetFiles(extractDir, "wintun.dll", SearchOption.AllDirectories).FirstOrDefault();
            if (dllPath == null)
            {
                throw new FileNotFoundException("wintun.dll not found in archive.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(wintunPath) ?? AppContext.BaseDirectory);
            File.Copy(dllPath, wintunPath, true);
        });
    }

    private static async Task DownloadToFileAsync(HttpClient client, string url, string targetPath)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        await using var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(file);
    }

    private static async Task DownloadWithFallbackAsync(HttpClient client, IEnumerable<string> urls, string targetPath)
    {
        Exception? lastError = null;
        foreach (var url in urls)
        {
            try
            {
                await DownloadToFileAsync(client, url, targetPath);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }
            }
        }

        throw new InvalidOperationException($"Tor download failed: {lastError?.Message}");
    }

    private static async Task<string> GetLatestTorVersionAsync(HttpClient client)
    {
        try
        {
            var html = await client.GetStringAsync(TorBaseUrl);
            var matches = Regex.Matches(html, "href=\"(?<ver>\\d+\\.\\d+(\\.\\d+)*)/\"");
            var versions = new List<Version>();
            foreach (Match match in matches)
            {
                if (Version.TryParse(match.Groups["ver"].Value, out var version))
                {
                    versions.Add(version);
                }
            }

            if (versions.Count > 0)
            {
                return versions.OrderByDescending(v => v).First().ToString();
            }
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"Failed to fetch latest Tor version: {ex.Message}. Using fallback.");
        }

        return TorFallbackVersion;
    }

    private static void ExtractTarGz(string archivePath, string destination)
    {
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: true);
    }

    private static void CopyDirectory(string sourceDir, string destinationDir, bool overwrite, string? preserveFileName = null)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dirPath.Replace(sourceDir, destinationDir, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var destPath = filePath.Replace(sourceDir, destinationDir, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(preserveFileName)
                && string.Equals(Path.GetFileName(filePath), preserveFileName, StringComparison.OrdinalIgnoreCase)
                && File.Exists(destPath))
            {
                continue;
            }

            var destFolder = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrWhiteSpace(destFolder))
            {
                Directory.CreateDirectory(destFolder);
            }
            File.Copy(filePath, destPath, overwrite);
        }
    }

    private void EnsurePluggableTransportConfig(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<PluggableTransportConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (config == null)
            {
                return;
            }

            var updated = false;
            config.PluggableTransports ??= new Dictionary<string, string>();
            if (!config.PluggableTransports.ContainsKey("lyrebird"))
            {
                config.PluggableTransports["lyrebird"] =
                    "ClientTransportPlugin meek_lite,obfs2,obfs3,obfs4,scramblesuit exec ${pt_path}lyrebird.exe";
                updated = true;
            }

            if (!config.PluggableTransports.ContainsKey("webtunnel"))
            {
                config.PluggableTransports["webtunnel"] =
                    "ClientTransportPlugin webtunnel exec ${pt_path}webtunnel-client.exe";
                updated = true;
            }

            if (updated)
            {
                var updatedJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configPath, updatedJson);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Bridge config update failed: {ex.Message}");
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_isCheckingUpdates || !AutoUpdate)
        {
            return;
        }

        _isCheckingUpdates = true;
        try
        {
            AppendLog("Checking for updates...");
            var latest = await _updateService.GetLatestReleaseAsync(UpdateApiUrl);
            if (latest == null)
            {
                AppendLog("Update check failed.");
                return;
            }

            var currentVersion = GetCurrentVersion();
            if (latest.Version <= currentVersion)
            {
                AppendLog("No updates available.");
                return;
            }

            AppendLog($"Update available: {latest.Version} (current {currentVersion}).");
            StatusMessage = $"Update available: v{latest.Version}.";

            if (!IsVisible)
            {
                return;
            }

            var result = MessageBox.Show(
                this,
                $"An update is available (v{latest.Version}). Download and install now?",
                "OnionHop Update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(latest.DownloadUrl))
            {
                OpenUpdatePage(latest.HtmlUrl);
                return;
            }

            var installerPath = await _updateService.DownloadUpdateAsync(latest);
            if (string.IsNullOrWhiteSpace(installerPath))
            {
                OpenUpdatePage(latest.HtmlUrl);
                return;
            }

            AppendLog($"Launching updater: {installerPath}");
            Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
            ExitApplication();
        }
        catch (Exception ex)
        {
            AppendLog($"Update check failed: {ex.Message}");
        }
        finally
        {
            _isCheckingUpdates = false;
        }
    }

    private static Version GetCurrentVersion()
    {
        var entry = Assembly.GetEntryAssembly();
        if (entry == null)
        {
            return new Version(1, 0, 0);
        }

        var info = entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var parsed = ParseVersion(info);
        if (parsed.Major != 0 || parsed.Minor != 0 || parsed.Build != 0)
        {
            return parsed;
        }

        return entry.GetName().Version ?? new Version(1, 0, 0);
    }

    private static Version ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return new Version(0, 0, 0);
        }

        var clean = tag.Trim().TrimStart('v', 'V');
        var match = Regex.Match(clean, @"\d+(\.\d+){0,3}");
        if (match.Success && Version.TryParse(match.Value, out var version))
        {
            return version;
        }

        return new Version(0, 0, 0);
    }

    private static void OpenUpdatePage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"Failed to open update page: {ex.Message}");
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isConnecting)
        {
            return;
        }

        if (IsDependencyDownloadInProgress)
        {
            StatusMessage = "Downloading components. Please wait.";
            return;
        }

        if (_isConnected)
        {
            await DisconnectAsync();
        }
        else
        {
            if (!await EnsureDependenciesAsync())
            {
                return;
            }

            await ConnectAsync();
        }
    }

    private async void RefreshIpButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isConnected)
        {
            StatusMessage = "Connect first to refresh the exit IP.";
            return;
        }

        await UpdateCurrentIpAsync();
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await DisconnectAsync();
    }

    private async void ChangeIdentityButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isConnected || !_torService.IsRunning)
        {
            StatusMessage = "Connect to Tor before requesting a new identity.";
            return;
        }

        if (DateTime.UtcNow - _lastNewnymUtc < TimeSpan.FromSeconds(10))
        {
            StatusMessage = "Please wait a moment before requesting another identity.";
            return;
        }

        StatusMessage = "Requesting a new Tor circuit...";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var success = await SendTorControlSignalAsync("SIGNAL NEWNYM", cts.Token);
            if (!success)
            {
                StatusMessage = "Unable to request a new identity. Check Tor is running.";
                return;
            }

            _lastNewnymUtc = DateTime.UtcNow;
            await Task.Delay(1200);
            await UpdateCurrentIpAsync();
            StatusMessage = "New identity requested. It may take a few seconds to update.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Identity request timed out.";
        }
        catch (Exception ex)
        {
            AppendLog($"Change identity failed: {ex.Message}");
            StatusMessage = $"Failed to change identity: {ex.Message}";
        }
    }

    private async Task ConnectAsync()
    {
        if (_isConnecting)
        {
            return;
        }

        if (IsTunMode && !await EnsureVpnPrivilegesAsync())
        {
            return;
        }

        _lastConnectAttemptUtc = DateTime.UtcNow;
        _bridgeValidationMessage = null;
        _isDisconnecting = false;
        UpdateConnectVisualState();
        Raise(nameof(ConnectButtonText));

        var torPath = Path.Combine(AppContext.BaseDirectory, DefaultTorRelativePath);
        if (!File.Exists(torPath))
        {
            ConnectionStatus = "Tor missing";
            StatusMessage = "Place tor.exe inside a 'tor' folder next to OnionHop.exe.";
            StatusBrush = new SolidColorBrush(Color.FromRgb(209, 67, 75));
            return;
        }

        _connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        _isConnecting = true;
        Raise(nameof(ConnectButtonText));
        UpdateConnectVisualState();
        ConnectionStatus = "Connecting...";
        StatusMessage = "Starting Tor and bootstrapping network...";
        StatusBrush = new SolidColorBrush(Color.FromRgb(255, 166, 43));
        ConnectionProgress = 0.1;
        CurrentIp = "Resolving...";

        try
        {
            var bridgeSummary = UseTorBridges
                ? (HasCustomBridgeLines() ? "Bridges=custom" : $"Bridges={SelectedBridgeType}")
                : "Bridges=off";
            AppendLog($"Connecting. Mode={SelectedConnectionMode}, Hybrid={UseHybridRouting}, Exit={SelectedLocation}, {bridgeSummary}");
            await StartTorAsync(torPath, SelectedLocation, _connectCts.Token);

            if (IsTunMode)
            {
                ConnectionProgress = Math.Max(ConnectionProgress, 0.9);
                StatusMessage = UseHybridRouting
                    ? "Tor is running. Starting Hybrid tunnel (web via Tor)..."
                    : "Tor is running. Starting VPN tunnel (all traffic via Tor)...";
                await StartSingBoxVpnAsync(_connectCts.Token);

                if (KillSwitchEnabled && !UseHybridRouting)
                {
                    AppendLog("Kill switch armed (will block traffic if tunnel drops unexpectedly). ");
                }
            }
            else
            {
                ApplySystemProxy(true);
            }

            _isConnected = true;
            _viewModel.IsConnected = true;
            Raise(nameof(ConnectButtonText));
            ConnectionStatus = "Connected";
            StatusBrush = new SolidColorBrush(Color.FromRgb(69, 201, 147));
            StatusMessage = IsTunMode
                ? (UseHybridRouting
                    ? "Tor is running. Hybrid routing is active (browser via Tor)."
                    : "Tor is running. VPN tunnel is active (all traffic via Tor).")
                : "Tor is running. Proxy mode is active (apps must respect proxy settings).";

            ConnectionProgress = 1;
            UpdateConnectVisualState();

            await UpdateCurrentIpAsync();
        }
        catch (OperationCanceledException)
        {
            AppendLog("Connect timed out.");
            StopSingBoxProcess(allowElevation: IsTunMode);
            if (IsTunMode && KillSwitchEnabled && !UseHybridRouting)
            {
                _ = Task.Run(() => DisableKillSwitchEmergencyBlock());
            }
            StatusMessage = "Tor connection timed out.";
            ConnectionStatus = "Disconnected";
            StatusBrush = new SolidColorBrush(Color.FromRgb(209, 67, 75));
            ConnectionProgress = 0;
            StopTorProcess();

            if (ShouldAutoRetryConnect())
            {
                AppendLog("Initial connect timed out quickly; retrying once...");
                StatusMessage = "Initial connect timed out. Retrying once...";
                _pendingAutoRetry = true;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Connect failed: {ex.Message}");
            StopSingBoxProcess(allowElevation: IsTunMode);
            if (IsTunMode && KillSwitchEnabled && !UseHybridRouting)
            {
                _ = Task.Run(() => DisableKillSwitchEmergencyBlock());
            }
            StatusMessage = $"Failed to connect: {ex.Message}";
            ConnectionStatus = "Disconnected";
            StatusBrush = new SolidColorBrush(Color.FromRgb(209, 67, 75));
            ConnectionProgress = 0;
            StopTorProcess();

            if (ShouldAutoRetryConnect())
            {
                AppendLog("Initial connect failed quickly; retrying once...");
                StatusMessage = "Initial connect failed. Retrying once...";
                _pendingAutoRetry = true;
            }
        }
        finally
        {
            _isConnecting = false;
            Raise(nameof(ConnectButtonText));
            UpdateConnectVisualState();
            _connectCts?.Dispose();
            _connectCts = null;

            if (_pendingAutoRetry)
            {
                _pendingAutoRetry = false;
                _ = Dispatcher.BeginInvoke(new Action(() => _ = RetryConnectAsync()));
            }
        }
    }

    private bool ShouldAutoRetryConnect()
    {
        if (_autoRetryConnectUsed)
        {
            return false;
        }

        if (_lastConnectAttemptUtc == null)
        {
            return false;
        }

        if (DateTime.UtcNow - _appStartUtc > TimeSpan.FromMinutes(5))
        {
            return false;
        }

        if (DateTime.UtcNow - _lastConnectAttemptUtc.Value > TimeSpan.FromSeconds(15))
        {
            return false;
        }

        _autoRetryConnectUsed = true;
        return true;
    }

    private async Task RetryConnectAsync()
    {
        await Task.Delay(1500);
        await ConnectAsync();
    }

    private async Task DisconnectAsync()
    {
        if (_isConnecting)
        {
            return;
        }

        _isDisconnecting = true;
        Raise(nameof(ConnectButtonText));
        UpdateConnectVisualState();
        StatusMessage = "Stopping Tor...";
        ConnectionStatus = "Disconnecting...";
        ConnectionProgress = 0.2;

        StopSingBoxProcess(allowElevation: IsTunMode);
        if (IsTunMode && KillSwitchEnabled && !UseHybridRouting)
        {
            _ = Task.Run(() => DisableKillSwitchEmergencyBlock());
        }

        if (_systemProxyApplied)
        {
            ApplySystemProxy(false);
        }

        StopTorProcess();
        await Task.Delay(300);

        _isConnected = false;
        _viewModel.IsConnected = false;
        _isDisconnecting = false;
        Raise(nameof(ConnectButtonText));
        UpdateConnectVisualState();
        ConnectionStatus = "Disconnected";
        ConnectionProgress = 0;
        StatusBrush = new SolidColorBrush(Color.FromRgb(209, 67, 75));
        StatusMessage = "Tor stopped. Traffic is back to normal.";
        CurrentIp = "--.--.--.--";
    }

    private const int MaxLogLines = 500;
    private const int LogTrimBatchSize = 50;

    private void AppendLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        Dispatcher.Invoke(() =>
        {
            lock (_logLock)
            {
                LogLines.Add(line);
                
                // Batch removal is more efficient than removing one at a time
                if (LogLines.Count > MaxLogLines + LogTrimBatchSize)
                {
                    var toRemove = LogLines.Count - MaxLogLines;
                    for (var i = 0; i < toRemove; i++)
                    {
                        LogLines.RemoveAt(0);
                    }
                }
            }
        });
    }


    private void TryAssignProcessToJob(Process process, string name)
    {
        if (_processJob == null)
        {
            return;
        }

        if (!_processJob.TryAddProcess(process, out var error))
        {
            if (error == 0)
            {
                return;
            }

            if (error == JobObject.ErrorAccessDenied)
            {
                AppendLog($"Process job attach skipped for {name} (already in another job).");
                return;
            }

            AppendLog($"Process job attach failed for {name}: Win32 {error}.");
        }
    }

    private void CloseOverlay_Click(object sender, RoutedEventArgs e)
    {
        ShowLogs = false;
        ShowAbout = false;
        ShowSettings = false;
    }

    private void OverlayBackground_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Only close if the click was on the semi-transparent background itself,
        // not bubbled up from the card content.
        if (e.OriginalSource == sender)
        {
            CloseOverlay_Click(sender, e);
        }
    }

    private string GetLogsText()
    {
        lock (_logLock)
        {
            return string.Join(Environment.NewLine, LogLines);
        }
    }

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = GetLogsText();
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusMessage = "No logs to copy.";
                return;
            }

            Clipboard.SetText(text);
            StatusMessage = "Logs copied to clipboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Copy failed: {ex.Message}";
            AppendLog($"Copy logs failed: {ex.Message}");
        }
    }

    private void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export OnionHop Logs",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = $"OnionHop-logs-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var text = GetLogsText();
            File.WriteAllText(dialog.FileName, text, Encoding.UTF8);
            StatusMessage = $"Logs exported to {Path.GetFileName(dialog.FileName)}";
            AppendLog($"Logs exported: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
            AppendLog($"Export logs failed: {ex.Message}");
        }
    }

    private void DiscordButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://discord.gg/y3MVspPzKQ") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open Discord: {ex.Message}";
            AppendLog($"Discord link failed: {ex.Message}");
        }
    }

    private async Task StartSingBoxVpnAsync(CancellationToken token)
    {
        StopSingBoxProcess(allowElevation: false);

        var singBoxPath = Path.Combine(AppContext.BaseDirectory, DefaultSingBoxRelativePath);
        var wintunPath = Path.Combine(AppContext.BaseDirectory, DefaultWintunRelativePath);
        var config = new VpnLaunchConfig
        {
            SingBoxPath = singBoxPath,
            WintunPath = wintunPath,
            HybridRouting = UseHybridRouting,
            SecureDns = UseCensoredMode,
            SocksPort = SocksPort,
            BrowserProcessNames = BrowserProcessNames,
            ProcessStarted = process => TryAssignProcessToJob(process, "sing-box")
        };

        if (!IsAdministrator())
        {
            if (!await _adminHelper.StartVpnAsync(config).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Unable to start elevated VPN helper.");
            }

            StartAdminVpnMonitor();
            return;
        }

        await _vpnService.StartAsync(config, token);
    }

    private void OnSingBoxDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        var line = AnsiEscapeRegex.Replace(e.Data, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        AppendLog($"sing-box: {line}");

        lock (_singBoxLogLock)
        {
            _singBoxRecentLines.Enqueue(line);
            while (_singBoxRecentLines.Count > 40)
            {
                _singBoxRecentLines.Dequeue();
            }
        }

        if (line.Contains("socks5: request rejected", StringComparison.OrdinalIgnoreCase))
        {
            var destMatch = SingBoxConnectionToRegex.Match(line);
            var dest = destMatch.Success ? destMatch.Groups["dest"].Value : "a destination";
            var now = DateTime.UtcNow;
            if (now - _lastVpnMessageUtc >= TimeSpan.FromSeconds(10))
            {
                _lastVpnMessageUtc = now;
                Dispatcher.Invoke(() =>
                    StatusMessage = $"VPN tunnel: Tor rejected a connection to {dest}. Non-web ports are often blocked by Tor exits.");
            }
            return;
        }

        if (line.Contains("outbound/direct", StringComparison.OrdinalIgnoreCase) &&
            (line.Contains("dial tcp", StringComparison.OrdinalIgnoreCase) || line.Contains("connectex", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (line.Contains("fatal", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("panic", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("wintun", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("tun", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.Invoke(() => StatusMessage = $"VPN tunnel: {line}");
        }
    }

    private void OnSingBoxExited(object? sender, EventArgs e)
    {
        var exitCode = 0;
        try
        {
            exitCode = _vpnService.ExitCode ?? 0;
        }
        catch
        {
        }

        AppendLog($"sing-box exited with code {exitCode}.");

        if (_isConnected && IsTunMode && KillSwitchEnabled && !UseHybridRouting && !_isDisconnecting)
        {
            EnableKillSwitchEmergencyBlock();
        }

        string lastLines;
        lock (_singBoxLogLock)
        {
            lastLines = string.Join("\n", _singBoxRecentLines.Count > 6
                ? _singBoxRecentLines.Skip(Math.Max(0, _singBoxRecentLines.Count - 6))
                : _singBoxRecentLines);
        }

        Dispatcher.Invoke(() =>
        {
            if (_isDisconnecting)
            {
                return;
            }

            ConnectionStatus = "VPN stopped";
            StatusBrush = new SolidColorBrush(Color.FromRgb(209, 67, 75));
            StatusMessage = string.IsNullOrWhiteSpace(lastLines)
                ? $"VPN tunnel stopped unexpectedly (exit code {exitCode}). Disconnecting..."
                : $"VPN tunnel stopped unexpectedly (exit code {exitCode}). Last logs:\n{lastLines}";
        });

        try
        {
            Dispatcher.BeginInvoke(new Action(() => _ = DisconnectAsync()));
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"OnSingBoxExited: Failed to invoke disconnect: {ex.Message}");
        }
    }

    private void StopSingBoxProcess(bool allowElevation = true)
    {
        _adminVpnMonitorCts?.Cancel();
        _adminVpnMonitorCts = null;

        if (!IsAdministrator())
        {
            // Never start the admin helper just to stop VPN
            // Only try to stop if helper is already running
            _ = Task.Run(async () => await _adminHelper.StopVpnIfAvailableAsync().ConfigureAwait(false));
            lock (_singBoxLogLock)
            {
                _singBoxRecentLines.Clear();
            }
            return;
        }

        _vpnService.Stop();
        lock (_singBoxLogLock)
        {
            _singBoxRecentLines.Clear();
        }
    }

    private void StartAdminVpnMonitor()
    {
        _adminVpnMonitorCts?.Cancel();
        _adminVpnMonitorCts = new CancellationTokenSource();
        var token = _adminVpnMonitorCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(3000, token).ConfigureAwait(false);
                    var status = await _adminHelper.GetStatusAsync().ConfigureAwait(false);
                    if (status == null)
                    {
                        AppendLog("VPN helper unavailable. Disconnecting...");
                        Dispatcher.Invoke(() =>
                        {
                            ConnectionStatus = "VPN stopped";
                            StatusBrush = new SolidColorBrush(Color.FromRgb(209, 67, 75));
                            StatusMessage = "VPN helper unavailable. Disconnecting...";
                        });
                        _ = Dispatcher.InvokeAsync(DisconnectAsync);
                        return;
                    }

                    if (status.VpnRunning)
                    {
                        continue;
                    }

                    if (_isDisconnecting || !_isConnected || !IsTunMode)
                    {
                        return;
                    }

                    if (KillSwitchEnabled && !UseHybridRouting)
                    {
                        EnableKillSwitchEmergencyBlock();
                    }

                    AppendLog("VPN helper stopped unexpectedly. Disconnecting...");
                    Dispatcher.Invoke(() =>
                    {
                        ConnectionStatus = "VPN stopped";
                        StatusBrush = new SolidColorBrush(Color.FromRgb(209, 67, 75));
                        StatusMessage = "VPN helper stopped unexpectedly. Disconnecting...";
                    });
                    _ = Dispatcher.InvokeAsync(DisconnectAsync);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                }
            }
        }, token);
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> EnsureVpnPrivilegesAsync()
    {
        if (IsAdministrator())
        {
            return true;
        }

        StatusMessage = "Starting elevated VPN helper...";
        if (await _adminHelper.EnsureConnectedAsync())
        {
            return true;
        }

        StatusMessage = "VPN mode requires Administrator. Helper could not be started.";
        return false;
    }

    private void ApplyStartupArguments(string[] args)
    {
        if (args.Length == 0)
        {
            return;
        }

        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--vpn", StringComparison.OrdinalIgnoreCase))
            {
                SystemWideMode = true;
            }

            if (string.Equals(args[i], "--hybrid", StringComparison.OrdinalIgnoreCase))
            {
                UseHybridRouting = true;
            }

            if (string.Equals(args[i], "--strict", StringComparison.OrdinalIgnoreCase))
            {
                UseHybridRouting = false;
            }

            if (string.Equals(args[i], "--location", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                SelectedLocation = args[i + 1];
                i++;
            }

            if (string.Equals(args[i], "--connect", StringComparison.OrdinalIgnoreCase))
            {
                AutoConnect = true;
            }

            if (string.Equals(args[i], "--minimized", StringComparison.OrdinalIgnoreCase))
            {
                _startMinimizedOnLaunch = true;
            }
        }
    }

    private bool HasCustomBridgeLines()
    {
        return ExtractBridgeLines(CustomBridges).Count > 0;
    }

    private Task<IReadOnlyList<string>> GetBridgeLinesAsync(CancellationToken token)
    {
        var custom = ExtractBridgeLines(CustomBridges);
        if (custom.Count > 0)
        {
            if (string.Equals(SelectedBridgeType, WebTunnelBridgeType, StringComparison.OrdinalIgnoreCase))
            {
                var filtered = custom.Where(line => !IsPlaceholderBridgeLine(line)).ToList();
                if (filtered.Count == 0)
                {
                    AppendLog("Webtunnel bridge lines look like examples (2001:db8/192.0.2/etc). Attempting anyway.");
                    return Task.FromResult<IReadOnlyList<string>>(custom);
                }

                if (filtered.Count != custom.Count)
                {
                    AppendLog("Removed example WebTunnel bridge lines (use real BridgeDB entries).");
                }

                return Task.FromResult<IReadOnlyList<string>>(filtered);
            }

            return Task.FromResult<IReadOnlyList<string>>(custom);
        }

        if (_ptConfig?.Bridges != null &&
            _ptConfig.Bridges.TryGetValue(SelectedBridgeType, out var bridges) &&
            bridges.Count > 0)
        {
            if (string.Equals(SelectedBridgeType, WebTunnelBridgeType, StringComparison.OrdinalIgnoreCase))
            {
                var filtered = bridges.Where(line => !IsPlaceholderBridgeLine(line)).ToList();
                if (filtered.Count == 0)
                {
                    AppendLog("No usable webtunnel bridges. Get bridges from bridges.torproject.org and paste in Custom Bridges.");
                    return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
                }

                return Task.FromResult<IReadOnlyList<string>>(filtered);
            }

            return Task.FromResult<IReadOnlyList<string>>(bridges);
        }

        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private static List<string> ExtractBridgeLines(string? text)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return results;
        }

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("Bridge ", StringComparison.OrdinalIgnoreCase))
            {
                line = line.Substring("Bridge ".Length).Trim();
            }

            results.Add(line);
        }

        return results;
    }

    private IReadOnlyList<string> GetClientTransportPlugins(IReadOnlyList<string> bridgeLines, string torDir)
    {
        var ptPath = Path.Combine(torDir, "pluggable_transports");
        var ptPathWithSlash = ptPath.EndsWith(Path.DirectorySeparatorChar)
            ? ptPath
            : ptPath + Path.DirectorySeparatorChar;

        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in bridgeLines)
        {
            var transport = ExtractBridgeTransport(line);
            if (!string.IsNullOrWhiteSpace(transport))
            {
                needed.Add(transport);
            }
        }

        if (needed.Count == 0)
        {
            return Array.Empty<string>();
        }

        string? webTunnelPlugin = null;
        if (needed.Contains(WebTunnelBridgeType))
        {
            if (!TryEnsureWebTunnelClient(ptPath, out webTunnelPlugin))
            {
                return Array.Empty<string>();
            }
        }

        if (_ptConfig?.PluggableTransports != null && _ptConfig.PluggableTransports.Count > 0)
        {
            var transportMap = BuildTransportPluginMap(_ptConfig.PluggableTransports, ptPathWithSlash);
            var pluginLines = new List<string>();
            foreach (var transport in needed)
            {
                if (string.Equals(transport, WebTunnelBridgeType, StringComparison.OrdinalIgnoreCase))
                {
                    pluginLines.Add(webTunnelPlugin!);
                    continue;
                }

                if (transportMap.TryGetValue(transport, out var plugin))
                {
                    pluginLines.Add(ReplaceTransportSegment(plugin, transport));
                    continue;
                }

                if (string.Equals(transport, "conjure", StringComparison.OrdinalIgnoreCase))
                {
                    pluginLines.Add($"ClientTransportPlugin conjure exec {Path.Combine(ptPath, "conjure-client.exe")} -registerURL https://registration.refraction.network/api");
                }
                else
                {
                    pluginLines.Add($"ClientTransportPlugin {transport} exec {Path.Combine(ptPath, "lyrebird.exe")}");
                }
            }

            return pluginLines.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        return BuildFallbackTransportPlugins(needed, ptPath, webTunnelPlugin);
    }

    private static IReadOnlyList<string> BuildFallbackTransportPlugins(IReadOnlyCollection<string> transports, string ptPath, string? webTunnelPlugin)
    {
        var plugins = new List<string>();
        foreach (var transport in transports)
        {
            if (string.Equals(transport, WebTunnelBridgeType, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(webTunnelPlugin))
                {
                    plugins.Add(webTunnelPlugin);
                }
                continue;
            }

            if (string.Equals(transport, "conjure", StringComparison.OrdinalIgnoreCase))
            {
                plugins.Add($"ClientTransportPlugin conjure exec {Path.Combine(ptPath, "conjure-client.exe")} -registerURL https://registration.refraction.network/api");
            }
            else
            {
                plugins.Add($"ClientTransportPlugin {transport} exec {Path.Combine(ptPath, "lyrebird.exe")}");
            }
        }

        return plugins;
    }

    private static Dictionary<string, string> BuildTransportPluginMap(Dictionary<string, string> pluggableTransports, string ptPathWithSlash)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in pluggableTransports.Values)
        {
            var resolved = entry.Replace("${pt_path}", ptPathWithSlash, StringComparison.OrdinalIgnoreCase);
            var transportSegment = ExtractTransportSegment(resolved);
            if (string.IsNullOrWhiteSpace(transportSegment))
            {
                continue;
            }

            var transports = transportSegment.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var transport in transports)
            {
                var normalized = ReplaceTransportSegment(resolved, transport);
                map[transport] = normalized;
            }
        }

        return map;
    }

    private static string? ExtractTransportSegment(string pluginLine)
    {
        const string prefix = "ClientTransportPlugin ";
        var startIndex = pluginLine.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0)
        {
            return null;
        }

        startIndex += prefix.Length;
        var execIndex = pluginLine.IndexOf(" exec ", startIndex, StringComparison.OrdinalIgnoreCase);
        if (execIndex < 0)
        {
            return null;
        }

        return pluginLine.Substring(startIndex, execIndex - startIndex).Trim();
    }

    private static string ReplaceTransportSegment(string pluginLine, string transport)
    {
        const string prefix = "ClientTransportPlugin ";
        var startIndex = pluginLine.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0)
        {
            return pluginLine;
        }

        startIndex += prefix.Length;
        var execIndex = pluginLine.IndexOf(" exec ", startIndex, StringComparison.OrdinalIgnoreCase);
        if (execIndex < 0)
        {
            return pluginLine;
        }

        return $"{pluginLine.Substring(0, startIndex)}{transport}{pluginLine.Substring(execIndex)}";
    }

    private static string NormalizeClientTransportPlugin(string pluginLine)
    {
        if (string.IsNullOrWhiteSpace(pluginLine))
        {
            return string.Empty;
        }

        const string prefix = "ClientTransportPlugin ";
        var trimmed = pluginLine.Trim();
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.Substring(prefix.Length).Trim();
        }

        return trimmed;
    }

    private static string? ExtractBridgeTransport(string bridgeLine)
    {
        if (string.IsNullOrWhiteSpace(bridgeLine))
        {
            return null;
        }

        var parts = bridgeLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : null;
    }

    private static bool IsPlaceholderBridgeLine(string bridgeLine)
    {
        if (string.IsNullOrWhiteSpace(bridgeLine))
        {
            return true;
        }

        // Webtunnel bridges use documentation IPs but connect via url= parameter
        // So don't filter them out if they have a real URL
        if (bridgeLine.StartsWith("webtunnel", StringComparison.OrdinalIgnoreCase) 
            && bridgeLine.Contains("url=", StringComparison.OrdinalIgnoreCase))
        {
            // Check if the URL looks like a real domain (not example.com/test.invalid)
            var urlMatch = Regex.Match(bridgeLine, @"url=https?://([^/\s]+)", RegexOptions.IgnoreCase);
            if (urlMatch.Success)
            {
                var host = urlMatch.Groups[1].Value.ToLowerInvariant();
                if (!host.Contains("example.") && !host.Contains(".invalid") && !host.Contains(".test"))
                {
                    return false; // Real webtunnel bridge
                }
            }
        }

        return bridgeLine.Contains("2001:db8", StringComparison.OrdinalIgnoreCase)
            || bridgeLine.Contains("192.0.2.", StringComparison.OrdinalIgnoreCase)
            || bridgeLine.Contains("198.51.100.", StringComparison.OrdinalIgnoreCase)
            || bridgeLine.Contains("203.0.113.", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryEnsureWebTunnelClient(string ptPath, out string? pluginLine)
    {
        var targetPath = Path.Combine(ptPath, WebTunnelClientFileName);
        if (!File.Exists(targetPath))
        {
            var sourcePath = FindWebTunnelClientInTorBrowser();
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                try
                {
                    Directory.CreateDirectory(ptPath);
                    File.Copy(sourcePath, targetPath, true);
                    AppendLog($"Copied {WebTunnelClientFileName} from Tor Browser.");
                }
                catch (Exception ex)
                {
                    AppendLog($"Failed to copy {WebTunnelClientFileName}: {ex.Message}");
                }
            }
        }

        if (File.Exists(targetPath))
        {
            pluginLine = $"ClientTransportPlugin {WebTunnelBridgeType} exec {targetPath}";
            return true;
        }

        pluginLine = null;
        _bridgeValidationMessage = $"Webtunnel client is missing ({WebTunnelClientFileName}). Install Tor Browser and copy it into tor\\pluggable_transports, or rerun download-deps.ps1.";
        return false;
    }

    private static string? FindWebTunnelClientInTorBrowser()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tor Browser", "Browser", "TorBrowser", "Tor", "PluggableTransports", WebTunnelClientFileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tor Browser", "Browser", "TorBrowser", "Tor", "PluggableTransports", WebTunnelClientFileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tor Browser", "Browser", "TorBrowser", "Tor", "PluggableTransports", WebTunnelClientFileName)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task StartTorAsync(string torPath, string location, CancellationToken token)
    {
        _bootstrapSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => _bootstrapSource.TrySetCanceled(token));

        var torDir = Path.GetDirectoryName(torPath) ?? AppContext.BaseDirectory;
        var geoIpPath = Path.Combine(torDir, "geoip");
        var geoIp6Path = Path.Combine(torDir, "geoip6");

        IReadOnlyList<string>? bridgeLines = null;
        List<string>? normalizedPlugins = null;
        if (UseTorBridges)
        {
            bridgeLines = await GetBridgeLinesAsync(token);
            if (bridgeLines.Count == 0)
            {
                var message = _bridgeValidationMessage;
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = string.Equals(SelectedBridgeType, WebTunnelBridgeType, StringComparison.OrdinalIgnoreCase)
                        ? "Webtunnel bridges require manual setup. Visit bridges.torproject.org, select 'webtunnel', solve the CAPTCHA, and paste the bridge lines in Custom Bridges."
                        : "Bridges enabled but no bridge lines are configured.";
                }
                throw new InvalidOperationException(message);
            }

            var pluginLines = GetClientTransportPlugins(bridgeLines, torDir);
            if (pluginLines.Count == 0)
            {
                var message = _bridgeValidationMessage;
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = "Bridges enabled but no transport plugins were found.";
                }
                throw new InvalidOperationException(message);
            }

            normalizedPlugins = pluginLines
                .Select(NormalizeClientTransportPlugin)
                .Where(normalized => !string.IsNullOrWhiteSpace(normalized))
                .ToList();

            if (normalizedPlugins.Count == 0)
            {
                var message = _bridgeValidationMessage;
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = "Bridges enabled but no transport plugins were found.";
                }
                throw new InvalidOperationException(message);
            }
        }

        var countryCode = GetCountryCode(location);
        var config = new TorLaunchConfig
        {
            TorPath = torPath,
            SocksPort = SocksPort,
            GeoIpPath = geoIpPath,
            GeoIp6Path = geoIp6Path,
            BridgeLines = bridgeLines,
            ClientTransportPlugins = normalizedPlugins,
            ExitCountryCode = countryCode,
            ProcessStarted = process => TryAssignProcessToJob(process, "tor")
        };

        await _torService.StartAsync(config, token);

        await _bootstrapSource.Task;
    }

    private async Task<bool> SendTorControlSignalAsync(string command, CancellationToken token)
    {
        return await _torService.SendControlSignalAsync(command, token);
    }

    private void OnTorDataReceived(object? sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        var line = e.Data;
        if (line.Contains("Bootstrapped", StringComparison.OrdinalIgnoreCase))
        {
            var percent = ExtractProgress(line);
            Dispatcher.Invoke(() =>
            {
                ConnectionProgress = percent / 100d;
                if (percent >= 100)
                {
                    _bootstrapSource?.TrySetResult(true);
                }
            });

            AppendLog($"Tor bootstrapped: {line}");
        }
        else if (line.Contains("Failed", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog($"Tor error: {line}");
            Dispatcher.Invoke(() => _bootstrapSource?.TrySetException(new InvalidOperationException(line)));
        }
        else if (line.Contains("warn", StringComparison.OrdinalIgnoreCase) || line.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog($"Tor log: {line}");
        }
    }

    private static int ExtractProgress(string line)
    {
        var percentIndex = line.IndexOf('%');
        if (percentIndex <= 0)
        {
            return 0;
        }

        var start = percentIndex - 1;
        while (start >= 0 && char.IsDigit(line[start]))
        {
            start--;
        }

        var number = line.Substring(start + 1, percentIndex - start - 1);
        return int.TryParse(number, out var value) ? value : 0;
    }

    private async Task UpdateCurrentIpAsync()
    {
        try
        {
            if (SystemWideMode && !UseHybridRouting)
            {
                // Need custom handler for proxy, can't use singleton here
                using var handler = new HttpClientHandler { UseProxy = true };
                using var client = HttpClientFactory.CreateWithHandler(handler, TimeSpan.FromSeconds(35));

                var response = await client.GetAsync("https://check.torproject.org/api/ip");
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync();
                var doc = await JsonDocument.ParseAsync(stream);
                if (doc.RootElement.TryGetProperty("IP", out var ipProperty))
                {
                    CurrentIp = ipProperty.GetString() ?? CurrentIp;
                }
                else
                {
                    CurrentIp = await response.Content.ReadAsStringAsync();
                }

                StatusMessage = "IP refreshed via Tor route.";
            }
            else
            {
                CurrentIp = await HttpClientFactory.Default.GetStringAsync("https://api.ipify.org");
                StatusMessage = SystemWideMode
                    ? "Hybrid mode: your browser is routed via Tor. Current IP shows your normal route."
                    : "IP refreshed.";
            }
        }
        catch (Exception)
        {
            try
            {
                CurrentIp = await HttpClientFactory.Default.GetStringAsync("https://api.ipify.org");
                StatusMessage = "IP fetched via standard route (Tor lookup failed).";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Unable to fetch IP: {ex.Message}";
            }
        }
    }

    private void StopTorProcess()
    {
        _torService.Stop();
        _lastNewnymUtc = DateTime.MinValue;
    }

    private void ApplySystemProxy(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", writable: true);
            if (key == null)
            {
                StatusMessage = "Unable to edit proxy settings.";
                AppendLog("Proxy update failed: registry key not found.");
                return;
            }

            if (enable)
            {
                _previousProxy ??= key.GetValue("ProxyServer") as string;
                if (_previousProxyEnabled == null && key.GetValue("ProxyEnable") is int enabledValue)
                {
                    _previousProxyEnabled = enabledValue;
                }

                key.SetValue("ProxyServer", $"socks=127.0.0.1:{SocksPort}", RegistryValueKind.String);
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                _systemProxyApplied = true;
                AppendLog("Proxy enabled: socks=127.0.0.1:9050");
            }
            else if (_systemProxyApplied)
            {
                key.SetValue("ProxyEnable", _previousProxyEnabled ?? 0, RegistryValueKind.DWord);
                if (_previousProxy is not null)
                {
                    key.SetValue("ProxyServer", _previousProxy, RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue("ProxyServer", false);
                }

                _systemProxyApplied = false;
                AppendLog("Proxy disabled (restored previous settings).");
            }

            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Proxy update failed: {ex.Message}";
            AppendLog($"Proxy update failed: {ex.Message}");
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExiting && MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        SystemEvents.SessionEnding -= OnSessionEnding;
        if (_systemProxyApplied)
        {
            ApplySystemProxy(false);
        }

        DisableKillSwitchEmergencyBlock(allowElevation: false);

        StopSingBoxProcess(allowElevation: false);
        StopTorProcess();
        
        // Shutdown admin helper if connected (don't wait forever)
        try
        {
            if (_adminHelper.IsConnected)
            {
                _adminHelper.ShutdownIfConnectedAsync().Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"OnClosed: Admin helper shutdown failed: {ex.Message}");
        }
        _adminHelper.Dispose();
        
        // Kill any orphaned helper processes
        KillOrphanedHelperProcesses();
        _vpnService.Dispose();
        _torService.Dispose();
        _processJob?.Dispose();
        _processJob = null;

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        if (_trayIconImage != null)
        {
            _trayIconImage.Dispose();
            _trayIconImage = null;
        }
    }

    private static string GetKillSwitchRuleName() => "OnionHop KillSwitch Emergency Block";
    private static string GetKillSwitchCleanupTaskName() => "OnionHop KillSwitch Cleanup";

    private void EnableKillSwitchEmergencyBlock()
    {
        try
        {
            if (!IsAdministrator())
            {
                _ = Task.Run(async () =>
                {
                    if (await _adminHelper.EnsureConnectedAsync().ConfigureAwait(false))
                    {
                        await _adminHelper.EnableKillSwitchAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        AppendLog("Kill switch could not be enabled (admin helper unavailable).");
                    }
                });
                AppendLog("Kill switch requested via helper.");
                Dispatcher.Invoke(() =>
                    StatusMessage = "Kill switch engaged: traffic blocked to prevent leaks.");
                return;
            }

            RunNetsh($"advfirewall firewall delete rule name=\"{GetKillSwitchRuleName()}\"");
            RunNetsh($"advfirewall firewall add rule name=\"{GetKillSwitchRuleName()}\" dir=out action=block profile=any enable=yes");
            EnableKillSwitchFailsafe();
            AppendLog("Kill switch engaged: outbound traffic blocked.");
            Dispatcher.Invoke(() =>
                StatusMessage = "Kill switch engaged: traffic blocked to prevent leaks. Disconnect to restore.");
        }
        catch (Exception ex)
        {
            AppendLog($"Kill switch enable failed: {ex.Message}");
        }
    }

    private void DisableKillSwitchEmergencyBlock(bool allowElevation = true)
    {
        try
        {
            // Only try to disable if kill switch is actually active
            if (!IsKillSwitchEmergencyBlockActive())
            {
                return;
            }

            if (!IsAdministrator())
            {
                // Never start the admin helper just to disable kill switch
                // Only try to connect if helper is already running
                _ = Task.Run(async () =>
                {
                    if (!await _adminHelper.DisableKillSwitchIfAvailableAsync().ConfigureAwait(false))
                    {
                        if (allowElevation)
                        {
                            AppendLog("Kill switch is active but helper unavailable. Run as admin to clear.");
                        }
                    }
                });
                return;
            }

            RunNetsh($"advfirewall firewall delete rule name=\"{GetKillSwitchRuleName()}\"");
            DisableKillSwitchFailsafe();
        }
        catch
        {
        }
    }

    private void EnableKillSwitchFailsafe()
    {
        try
        {
            if (!IsAdministrator())
            {
                return;
            }

            var action = $"cmd /c netsh advfirewall firewall delete rule name=\\\"{GetKillSwitchRuleName()}\\\"";
            RunSchTasks($"/Create /TN \"{GetKillSwitchCleanupTaskName()}\" /TR \"{action}\" /SC ONSTART /RL HIGHEST /RU SYSTEM /F");
        }
        catch (Exception ex)
        {
            AppendLog($"Kill switch failsafe setup failed: {ex.Message}");
        }
    }

    private void DisableKillSwitchFailsafe()
    {
        try
        {
            if (!IsAdministrator())
            {
                return;
            }

            RunSchTasks($"/Delete /TN \"{GetKillSwitchCleanupTaskName()}\" /F");
        }
        catch (Exception ex)
        {
            AppendLog($"Kill switch failsafe cleanup failed: {ex.Message}");
        }
    }

    private bool IsKillSwitchEmergencyBlockActive()
    {
        try
        {
            var output = RunNetshWithOutput($"advfirewall firewall show rule name=\"{GetKillSwitchRuleName()}\"");
            if (string.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            // Check if the output actually contains the rule name (indicating it was found)
            // Using "No rules match" is unreliable on non-English systems.
            return output.Contains(GetKillSwitchRuleName(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void RunNetsh(string args)
    {
        var psi = new ProcessStartInfo("netsh", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            return;
        }

        proc.WaitForExit(8000);
    }

    private static void RunSchTasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            return;
        }

        proc.WaitForExit(8000);
    }

    private static void KillOrphanedHelperProcesses()
    {
        try
        {
            var currentPid = Environment.ProcessId;
            var currentExeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "OnionHop");
            
            foreach (var proc in Process.GetProcessesByName(currentExeName))
            {
                try
                {
                    // Don't kill ourselves
                    if (proc.Id == currentPid)
                    {
                        continue;
                    }
                    
                    // Check if it's a helper process by checking command line
                    // Helper processes are typically older than us and running elevated
                    if (!proc.HasExited)
                    {
                        proc.Kill();
                        proc.WaitForExit(1000);
                        StartupLogger.Write($"Killed orphaned helper process: PID {proc.Id}");
                    }
                }
                catch (Exception ex)
                {
                    StartupLogger.Write($"Failed to kill orphaned process {proc.Id}: {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"KillOrphanedHelperProcesses failed: {ex.Message}");
        }
    }

    private static string RunNetshWithOutput(string args)
    {
        var psi = new ProcessStartInfo("netsh", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            return string.Empty;
        }

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(8000);
        return string.Join(Environment.NewLine, new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private static string GetCountryCode(string location)
    {
        return location switch
        {
            AutomaticLocationLabel => string.Empty,
            "United States" => "us",
            "United Kingdom" => "gb",
            "Germany" => "de",
            "France" => "fr",
            "Switzerland" => "ch",
            "Netherlands" => "nl",
            "Canada" => "ca",
            "Singapore" => "sg",
            _ => string.Empty
        };
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(propertyName);
        return true;
    }

    private void Raise(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(_isConnected) or nameof(_isConnecting))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectButtonText)));
        }

        if (propertyName is nameof(AutoConnect)
            or nameof(AutoStartMode)
            or nameof(MinimizeToTray)
            or nameof(AutoUpdate)
            or nameof(KillSwitchEnabled)
            or nameof(IsDarkMode)
            or nameof(UseNativeTheme)
            or nameof(SelectedLocation)
            or nameof(SelectedConnectionMode)
            or nameof(UseHybridRouting)
            or nameof(UseTorBridges)
            or nameof(UseCensoredMode)
            or nameof(SelectedBridgeType)
            or nameof(CustomBridges))
        {
            ScheduleSaveUserSettings();
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettings = true;
    }

    private void LogsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowLogs = true;
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        ShowAbout = true;
    }

    private void DashboardButton_Click(object sender, RoutedEventArgs e)
    {
        ShowLogs = false;
        ShowAbout = false;
        ShowSettings = false;
        StatusMessage = "Dashboard is active.";
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HandleCloseRequest();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // ignored
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeGlyph();
    }

    private void ToggleWindowState()
    {
        if (ResizeMode is ResizeMode.CanMinimize or ResizeMode.NoResize)
        {
            return;
        }

        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeGlyph();
    }

    private void UpdateMaximizeGlyph()
    {
        if (MaximizeIcon == null)
        {
            return;
        }

        MaximizeIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void UpdateConnectVisualState()
    {
        if (ConnectBubbleFill is not RadialGradientBrush fill || fill.GradientStops.Count < 2 || ConnectBubbleGlow is not DropShadowEffect glow)
        {
            return;
        }

        var (inner, outer, shadow) = _isConnected
            ? (Color.FromRgb(56, 255, 156), Color.FromRgb(29, 159, 100), Color.FromRgb(44, 255, 156))
            : (_isConnecting || _isDisconnecting)
                ? (Color.FromRgb(255, 229, 138), Color.FromRgb(244, 174, 44), Color.FromRgb(248, 197, 91))
                : (Color.FromRgb(126, 201, 255), Color.FromRgb(60, 120, 216), Color.FromRgb(137, 185, 255));

        fill.GradientStops[0].Color = inner;
        fill.GradientStops[1].Color = outer;
        glow.Color = shadow;

        Raise(nameof(CanChangeIdentity));
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int lpdwBufferLength);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
}
