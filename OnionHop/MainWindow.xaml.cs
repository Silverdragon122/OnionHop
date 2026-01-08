using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Principal;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private const string CensoredBridgePrimary = "snowflake";
    private const string CensoredBridgeFallback = "meek-azure";

    private bool _isConnecting;
    private bool _isConnected;
    private string _selectedLocation = AutomaticLocationLabel;
    private string _statusMessage = "Ready to route traffic through Tor.";
    private string _connectionStatus = "Disconnected";
    private string _currentIp = "--.--.--.--";
    private Brush _statusBrush = new SolidColorBrush(Color.FromRgb(209, 67, 75));
    private double _connectionProgress;

    private Process? _torProcess;
    private Process? _singBoxProcess;
    private CancellationTokenSource? _connectCts;
    private TaskCompletionSource<bool>? _bootstrapSource;
    private PluggableTransportConfig? _ptConfig;

    private DateTime _lastVpnMessageUtc = DateTime.MinValue;
    private readonly object _singBoxLogLock = new();
    private readonly Queue<string> _singBoxRecentLines = new();

    public ObservableCollection<string> LogLines { get; } = new();

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
        "Proxy Mode (Recommended)",
        "TUN/VPN Mode (Admin)"
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
        set => SetField(ref _selectedLocation, value);
    }

    private void OnTorExited(object? sender, EventArgs e)
    {
        if (_isDisconnecting)
        {
            return;
        }

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
    private string _selectedConnectionMode = "Proxy Mode (Recommended)";

    public bool IsTunMode => string.Equals(SelectedConnectionMode, "TUN/VPN Mode (Admin)", StringComparison.Ordinal);
    public bool IsProxyMode => !IsTunMode;

    public bool SystemWideMode
    {
        get => IsTunMode;
        set => SelectedConnectionMode = value ? "TUN/VPN Mode (Admin)" : "Proxy Mode (Recommended)";
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

                if (_isConnected && !IsAdministrator())
                {
                    StatusMessage = "Kill switch requires Administrator. Disconnect and reconnect in TUN mode.";
                    SetField(ref _killSwitchEnabled, false);
                    return;
                }
            }

            if (SetField(ref _killSwitchEnabled, value) && !_killSwitchEnabled)
            {
                _ = Task.Run(() => DisableKillSwitchEmergencyBlock());
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

    public string AboutText =>
        "OnionHop Modes\n" +
        "\n" +
        "Proxy Mode (Recommended)\n" +
        "- Starts Tor (SOCKS5 on 127.0.0.1:9050)\n" +
        "- Sets Windows proxy to use Tor for apps that respect proxy settings\n" +
        "- Does NOT require Administrator\n" +
        "- Most stable, best for everyday browsing\n" +
        "\n" +
        "TUN/VPN Mode (Admin)\n" +
        "- Starts Tor + sing-box + Wintun (virtual adapter)\n" +
        "- Can force routing rules at the OS level\n" +
        "- REQUIRES Administrator\n" +
        "\n" +
        "Hybrid (browser via Tor)\n" +
        "- In TUN mode, only browsers are routed through Tor; everything else goes direct\n" +
        "- Useful when you want Tor browsing without breaking other apps\n" +
        "\n" +
        "Settings\n" +
        "- Auto-Connect, Auto-Start (Off/On/Minimized), Minimize to Tray, Auto Update, Dark Mode, Native UI, Censored Mode, and Kill Switch are in the Settings tab.\n" +
        "\n" +
        "Exit Location\n" +
        "- Automatic picks the best exit; country selections are hints only.\n" +
        "\n" +
        "Tor Bridges\n" +
        "- Use pluggable transports like obfs4, snowflake, or meek-azure when Tor is blocked\n" +
        "- Enable in Settings and reconnect to apply\n" +
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

    public string ConnectButtonText
        => _isConnected ? "Disconnect"
            : _isDisconnecting ? "Disconnecting..."
            : _isConnecting ? "Connecting..."
            : "Connect";
    private bool _isDisconnecting;

    public MainWindow()
    {
        InitializeComponent();
        _customChrome = WindowChrome.GetWindowChrome(this);
        DataContext = this;

        LoadBridgeConfig();
        LoadUserSettings();
        ApplyStartupArguments(Environment.GetCommandLineArgs());
        ApplyTheme(IsDarkMode);
        UpdateConnectVisualState();
        UpdateMaximizeGlyph();

        AppendLog("OnionHop started.");
        SystemEvents.SessionEnding += OnSessionEnding;

        if (IsKillSwitchEmergencyBlockActive() && !IsAdministrator())
        {
            StatusMessage = "Kill switch is active and blocking traffic. Restart OnionHop as Administrator and disconnect to restore.";
        }
        else
        {
            _ = Task.Run(() => DisableKillSwitchEmergencyBlock());
        }
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        _isExiting = true;
    }

    private sealed class UserSettings
    {
        public bool AutoConnect { get; set; }
        public string? AutoStartMode { get; set; }
        public bool StartWithWindows { get; set; }
        public bool StartMinimized { get; set; }
        public bool MinimizeToTray { get; set; }
        public bool AutoUpdate { get; set; }
        public bool KillSwitchEnabled { get; set; }
        public bool IsDarkMode { get; set; }
        public bool UseNativeTheme { get; set; }
        public string? SelectedLocation { get; set; }
        public string? SelectedConnectionMode { get; set; }
        public bool UseHybridRouting { get; set; }
        public bool UseTorBridges { get; set; }
        public bool UseCensoredMode { get; set; }
        public string? SelectedBridgeType { get; set; }
        public string? CustomBridges { get; set; }
    }

    private sealed class PluggableTransportConfig
    {
        public string? RecommendedDefault { get; set; }
        public Dictionary<string, string> PluggableTransports { get; set; } = new();
        public Dictionary<string, List<string>> Bridges { get; set; } = new();
    }

    private static string GetSettingsPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnionHop");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
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
    }

    private void LoadUserSettings()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
            {
                return;
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<UserSettings>(json);
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

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(GetSettingsPath(), json);
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

    private void HideToTray()
    {
        EnsureTrayIcon();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = true;
            if (!_trayBalloonShown)
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

    private void ShowFromTray()
    {
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
        Close();
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_startMinimizedOnLaunch)
        {
            if (MinimizeToTray)
            {
                HideToTray();
            }
            else
            {
                WindowState = WindowState.Minimized;
            }
        }

        if (AutoConnect)
        {
            await ConnectAsync();
        }

        if (AutoUpdate)
        {
            _ = CheckForUpdatesAsync();
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
            var latest = await GetLatestReleaseAsync();
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

            var installerPath = await DownloadUpdateAsync(latest);
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
        return Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);
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

    private async Task<UpdateInfo?> GetLatestReleaseAsync()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OnionHop");

        using var response = await client.GetAsync(UpdateApiUrl);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        var release = JsonSerializer.Deserialize<GitHubRelease>(json);
        if (release == null)
        {
            return null;
        }

        var latestVersion = ParseVersion(release.TagName);
        if (latestVersion.Major == 0 && latestVersion.Minor == 0 && latestVersion.Build == 0)
        {
            return null;
        }

        var asset = release.Assets?
            .FirstOrDefault(a => a.Name != null
                                 && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                 && a.Name.Contains("OnionHop", StringComparison.OrdinalIgnoreCase))
            ?? release.Assets?.FirstOrDefault(a => a.Name != null
                                                  && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        return new UpdateInfo
        {
            Version = latestVersion,
            DownloadUrl = asset?.BrowserDownloadUrl,
            HtmlUrl = release.HtmlUrl,
            FileName = asset?.Name
        };
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
        catch
        {
        }
    }

    private static async Task<string?> DownloadUpdateAsync(UpdateInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
        {
            return null;
        }

        var updatesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnionHop", "updates");
        Directory.CreateDirectory(updatesDir);
        var fileName = string.IsNullOrWhiteSpace(info.FileName)
            ? $"OnionHop-Setup-{info.Version}.exe"
            : info.FileName;
        var targetPath = Path.Combine(updatesDir, fileName);

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OnionHop");

        using var response = await client.GetAsync(info.DownloadUrl);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        await using var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(file);
        return targetPath;
    }

    private sealed class UpdateInfo
    {
        public Version Version { get; init; } = new Version(0, 0, 0);
        public string? DownloadUrl { get; init; }
        public string? HtmlUrl { get; init; }
        public string? FileName { get; init; }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isConnecting)
        {
            return;
        }

        if (_isConnected)
        {
            await DisconnectAsync();
        }
        else
        {
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

    private async Task ConnectAsync()
    {
        if (_isConnecting)
        {
            return;
        }

        if (IsTunMode && !EnsureAdministratorOrRelaunch())
        {
            return;
        }

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
            StopSingBoxProcess();

            _ = Task.Run(() => DisableKillSwitchEmergencyBlock());
            StatusMessage = "Tor connection timed out.";
            ConnectionStatus = "Disconnected";
            StatusBrush = new SolidColorBrush(Color.FromRgb(209, 67, 75));
            ConnectionProgress = 0;
            StopTorProcess();
        }
        catch (Exception ex)
        {
            AppendLog($"Connect failed: {ex.Message}");
            StopSingBoxProcess();
            StatusMessage = $"Failed to connect: {ex.Message}";
            ConnectionStatus = "Disconnected";
            StatusBrush = new SolidColorBrush(Color.FromRgb(209, 67, 75));
            ConnectionProgress = 0;
            StopTorProcess();
        }
        finally
        {
            _isConnecting = false;
            Raise(nameof(ConnectButtonText));
            UpdateConnectVisualState();
            _connectCts?.Dispose();
            _connectCts = null;
        }
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

        StopSingBoxProcess();

        _ = Task.Run(() => DisableKillSwitchEmergencyBlock());

        if (_systemProxyApplied)
        {
            ApplySystemProxy(false);
        }

        StopTorProcess();
        await Task.Delay(300);

        _isConnected = false;
        _isDisconnecting = false;
        Raise(nameof(ConnectButtonText));
        UpdateConnectVisualState();
        ConnectionStatus = "Disconnected";
        ConnectionProgress = 0;
        StatusBrush = new SolidColorBrush(Color.FromRgb(209, 67, 75));
        StatusMessage = "Tor stopped. Traffic is back to normal.";
        CurrentIp = "--.--.--.--";
    }

    private void AppendLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        Dispatcher.Invoke(() =>
        {
            lock (_logLock)
            {
                LogLines.Add(line);
                while (LogLines.Count > 500)
                {
                    LogLines.RemoveAt(0);
                }
            }
        });
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

    private async Task StartSingBoxVpnAsync(CancellationToken token)
    {
        StopSingBoxProcess();

        var singBoxPath = Path.Combine(AppContext.BaseDirectory, DefaultSingBoxRelativePath);
        if (!File.Exists(singBoxPath))
        {
            throw new FileNotFoundException("VPN component missing: vpn\\sing-box.exe", singBoxPath);
        }

        var wintunPath = Path.Combine(AppContext.BaseDirectory, DefaultWintunRelativePath);
        if (!File.Exists(wintunPath))
        {
            throw new FileNotFoundException("VPN component missing: vpn\\wintun.dll", wintunPath);
        }

        var workDir = Path.GetDirectoryName(singBoxPath) ?? AppContext.BaseDirectory;
        var configDir = Path.Combine(Path.GetTempPath(), "OnionHop", "sing-box");
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "sing-box.json");
        await File.WriteAllTextAsync(configPath, BuildSingBoxConfigJson(UseHybridRouting, UseCensoredMode), token);

        AppendLog($"Starting sing-box with config: {configPath}");

        var psi = new ProcessStartInfo(singBoxPath, $"run -c \"{configPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workDir
        };

        _singBoxProcess = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        _singBoxProcess.Exited += OnSingBoxExited;

        _singBoxProcess.OutputDataReceived += OnSingBoxDataReceived;
        _singBoxProcess.ErrorDataReceived += OnSingBoxDataReceived;

        if (!_singBoxProcess.Start())
        {
            throw new InvalidOperationException("Unable to launch sing-box.exe");
        }

        _singBoxProcess.BeginOutputReadLine();
        _singBoxProcess.BeginErrorReadLine();
        await Task.Delay(750, token);

        if (_singBoxProcess.HasExited)
        {
            throw new InvalidOperationException("sing-box exited unexpectedly during startup.");
        }
    }

    private static string BuildSingBoxConfigJson(bool hybridRouting, bool secureDns)
    {
        var rules = new List<object>
        {
            new { action = "sniff" },
            new { process_name = "tor.exe", outbound = "direct" },
            new { ip_is_private = true, outbound = "direct" }
        };

        if (!hybridRouting)
        {
            rules.Insert(1, new { protocol = "dns", action = "hijack-dns" });
            rules.Add(new { network = "udp", outbound = "block" });
        }
        else
        {
            rules.Insert(1, new { protocol = "dns", action = "hijack-dns" });
            rules.Add(new { process_name = BrowserProcessNames, network = "udp", port = 443, outbound = "block" });
            rules.Add(new { process_name = BrowserProcessNames, network = "udp", outbound = "block" });
            rules.Add(new { process_name = BrowserProcessNames, outbound = "tor" });
            rules.Add(new { network = "tcp", port = new[] { 80, 443 }, outbound = "tor" });
        }

        object dnsServer = secureDns
            ? hybridRouting
                ? new
                {
                    tag = "remote",
                    type = "https",
                    server = "cloudflare-dns.com",
                    server_port = 443,
                    path = "/dns-query"
                }
                : new
                {
                    tag = "remote",
                    type = "https",
                    server = "cloudflare-dns.com",
                    server_port = 443,
                    path = "/dns-query",
                    detour = "tor"
                }
            : hybridRouting
                ? new
                {
                    tag = "remote",
                    type = "udp",
                    server = "1.1.1.1",
                    server_port = 53
                }
                : new
                {
                    tag = "remote",
                    type = "tcp",
                    server = "1.1.1.1",
                    server_port = 53,
                    detour = "tor"
                };

        var config = new
        {
            log = new
            {
                level = "info",
                timestamp = true
            },
            dns = new
            {
                servers = new object[]
                {
                    dnsServer
                },
                final = "remote"
            },
            inbounds = new object[]
            {
                new
                {
                    type = "tun",
                    tag = "tun-in",
                    interface_name = "OnionHop",
                    address = new[] { "172.19.0.1/30" },
                    auto_route = true,
                    strict_route = true
                }
            },
            outbounds = new object[]
            {
                new
                {
                    type = "socks",
                    tag = "tor",
                    server = "127.0.0.1",
                    server_port = SocksPort,
                    version = "5"
                },
                new
                {
                    type = "direct",
                    tag = "direct"
                },
                new
                {
                    type = "block",
                    tag = "block"
                }
            },
            route = new
            {
                auto_detect_interface = true,
                rules = rules,
                final = hybridRouting ? "direct" : "tor"
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
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
            exitCode = _singBoxProcess?.ExitCode ?? 0;
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
        catch
        {
        }
    }

    private void StopSingBoxProcess()
    {
        if (_singBoxProcess == null)
        {
            return;
        }

        try
        {
            if (!_singBoxProcess.HasExited)
            {
                try
                {
                    _singBoxProcess.CloseMainWindow();
                    _singBoxProcess.WaitForExit(1500);
                }
                catch
                {
                }

                _singBoxProcess.Kill(true);
                _singBoxProcess.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to stop sing-box: {ex.Message}");
        }
        finally
        {
            _singBoxProcess.Exited -= OnSingBoxExited;
            _singBoxProcess.OutputDataReceived -= OnSingBoxDataReceived;
            _singBoxProcess.ErrorDataReceived -= OnSingBoxDataReceived;
            _singBoxProcess.Dispose();
            _singBoxProcess = null;

            lock (_singBoxLogLock)
            {
                _singBoxRecentLines.Clear();
            }
        }
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

    private bool EnsureAdministratorOrRelaunch()
    {
        if (IsAdministrator())
        {
            return true;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            StatusMessage = "VPN mode requires Administrator. Please restart OnionHop as admin.";
            return false;
        }

        var args = new StringBuilder();
        args.Append("--connect ");
        args.Append("--vpn ");
        args.Append(UseHybridRouting ? "--hybrid " : "--strict ");
        args.Append("--location ");
        args.Append('"').Append(SelectedLocation.Replace("\"", string.Empty)).Append('"');

        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = args.ToString()
            };
            Process.Start(psi);
            Application.Current.Shutdown();
            return false;
        }
        catch
        {
            StatusMessage = "VPN mode requires Administrator permission.";
            return false;
        }
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

    private IReadOnlyList<string> GetBridgeLines()
    {
        var custom = ExtractBridgeLines(CustomBridges);
        if (custom.Count > 0)
        {
            return custom;
        }

        if (_ptConfig?.Bridges != null &&
            _ptConfig.Bridges.TryGetValue(SelectedBridgeType, out var bridges) &&
            bridges.Count > 0)
        {
            return bridges;
        }

        return Array.Empty<string>();
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

        if (_ptConfig?.PluggableTransports != null && _ptConfig.PluggableTransports.Count > 0)
        {
            var transportMap = BuildTransportPluginMap(_ptConfig.PluggableTransports, ptPathWithSlash);
            var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in bridgeLines)
            {
                var transport = ExtractBridgeTransport(line);
                if (!string.IsNullOrWhiteSpace(transport))
                {
                    needed.Add(transport);
                }
            }

            var pluginLines = new List<string>();
            foreach (var transport in needed)
            {
                if (transportMap.TryGetValue(transport, out var plugin))
                {
                    pluginLines.Add(plugin);
                }
            }

            if (pluginLines.Count > 0)
            {
                return pluginLines.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            return transportMap.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        return BuildFallbackTransportPlugins(bridgeLines, ptPath);
    }

    private static IReadOnlyList<string> BuildFallbackTransportPlugins(IEnumerable<string> bridgeLines, string ptPath)
    {
        var transports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in bridgeLines)
        {
            var transport = ExtractBridgeTransport(line);
            if (!string.IsNullOrWhiteSpace(transport))
            {
                transports.Add(transport);
            }
        }

        var plugins = new List<string>();
        foreach (var transport in transports)
        {
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
                map[transport] = resolved;
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

    private static string? ExtractBridgeTransport(string bridgeLine)
    {
        if (string.IsNullOrWhiteSpace(bridgeLine))
        {
            return null;
        }

        var parts = bridgeLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : null;
    }

    private async Task StartTorAsync(string torPath, string location, CancellationToken token)
    {
        _bootstrapSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => _bootstrapSource.TrySetCanceled(token));

        var dataDir = Path.Combine(Path.GetTempPath(), "OnionHop", "tor-data");
        Directory.CreateDirectory(dataDir);

        var argsBuilder = new StringBuilder();
        argsBuilder.Append($"--SocksPort {SocksPort} ");
        argsBuilder.Append($"--DataDirectory \"{dataDir}\" ");
        argsBuilder.Append("--ClientOnly 1 ");
        argsBuilder.Append("--Log \"notice stdout\" ");
        var torDir = Path.GetDirectoryName(torPath) ?? AppContext.BaseDirectory;
        argsBuilder.Append($"--GeoIPFile \"{Path.Combine(torDir, "geoip")}\" ");
        argsBuilder.Append($"--GeoIPv6File \"{Path.Combine(torDir, "geoip6")}\" ");

        if (UseTorBridges)
        {
            var bridgeLines = GetBridgeLines();
            if (bridgeLines.Count == 0)
            {
                throw new InvalidOperationException("Bridges enabled but no bridge lines are configured.");
            }

            var pluginLines = GetClientTransportPlugins(bridgeLines, torDir);
            if (pluginLines.Count == 0)
            {
                throw new InvalidOperationException("Bridges enabled but no transport plugins were found.");
            }

            argsBuilder.Append("--UseBridges 1 ");
            foreach (var pluginLine in pluginLines)
            {
                argsBuilder.Append($"--ClientTransportPlugin \"{pluginLine}\" ");
            }

            foreach (var bridgeLine in bridgeLines)
            {
                argsBuilder.Append($"--Bridge \"{bridgeLine}\" ");
            }
        }

        var countryCode = GetCountryCode(location);
        if (!string.IsNullOrWhiteSpace(countryCode))
        {
            argsBuilder.Append($"--ExitNodes {{{countryCode}}} ");
        }

        var psi = new ProcessStartInfo(torPath, argsBuilder.ToString())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(torPath) ?? AppContext.BaseDirectory
        };

        _torProcess = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        _torProcess.Exited += OnTorExited;

        _torProcess.OutputDataReceived += OnTorDataReceived;
        _torProcess.ErrorDataReceived += OnTorDataReceived;

        if (!_torProcess.Start())
        {
            throw new InvalidOperationException("Unable to launch tor.exe");
        }

        _torProcess.BeginOutputReadLine();
        _torProcess.BeginErrorReadLine();

        await _bootstrapSource.Task;
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
                using var handler = new HttpClientHandler
                {
                    UseProxy = true
                };

                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(35)
                };

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
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                CurrentIp = await client.GetStringAsync("https://api.ipify.org");
                StatusMessage = SystemWideMode
                    ? "Hybrid mode: your browser is routed via Tor. Current IP shows your normal route."
                    : "IP refreshed.";
            }
        }
        catch (Exception)
        {
            try
            {
                using var fallbackClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                CurrentIp = await fallbackClient.GetStringAsync("https://api.ipify.org");
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
        if (_torProcess == null)
        {
            return;
        }

        try
        {
            if (_torProcess != null)
            {
                _torProcess.Exited -= OnTorExited;
                if (!_torProcess.HasExited)
                {
                    try
                    {
                        _torProcess.CloseMainWindow();
                        _torProcess.WaitForExit(1500);
                    }
                    catch
                    {
                    }

                    _torProcess.Kill(true);
                    _torProcess.WaitForExit(5000);
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to stop Tor: {ex.Message}");
        }
        finally
        {
            _torProcess.OutputDataReceived -= OnTorDataReceived;
            _torProcess.ErrorDataReceived -= OnTorDataReceived;
            _torProcess.Dispose();
            _torProcess = null;
        }
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

        DisableKillSwitchEmergencyBlock();

        StopSingBoxProcess();
        StopTorProcess();

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

    private void EnableKillSwitchEmergencyBlock()
    {
        try
        {
            if (!IsAdministrator())
            {
                AppendLog("Kill switch could not be enabled (admin required).");
                return;
            }

            RunNetsh($"advfirewall firewall delete rule name=\"{GetKillSwitchRuleName()}\"");
            RunNetsh($"advfirewall firewall add rule name=\"{GetKillSwitchRuleName()}\" dir=out action=block profile=any enable=yes");
            AppendLog("Kill switch engaged: outbound traffic blocked.");
            Dispatcher.Invoke(() =>
                StatusMessage = "Kill switch engaged: traffic blocked to prevent leaks. Disconnect to restore.");
        }
        catch (Exception ex)
        {
            AppendLog($"Kill switch enable failed: {ex.Message}");
        }
    }

    private void DisableKillSwitchEmergencyBlock()
    {
        try
        {
            if (!IsAdministrator())
            {
                return;
            }

            RunNetsh($"advfirewall firewall delete rule name=\"{GetKillSwitchRuleName()}\"");
        }
        catch
        {
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
        if (WindowState == WindowState.Minimized && MinimizeToTray && !_isExiting)
        {
            HideToTray();
        }
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
