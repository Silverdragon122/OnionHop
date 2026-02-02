using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnionHopV2.Core;
using OnionHopV2.Core.Models;
using OnionHopV2.Core.Platform.Windows;
using OnionHopV2.Core.Services;

namespace OnionHopV2.App.ViewModels;

public sealed partial class AppStateViewModel : ViewModelBase, IDisposable
{
    public const string AutomaticLocationLabel = "Automatic";
    public const string ConnectionModeProxy = "Proxy Mode (Recommended)";
    public const string ConnectionModeTun = "TUN/VPN Mode (Admin)";
    public const string AutoStartModeOff = "Off";
    public const string AutoStartModeOn = "On";
    public const string AutoStartModeMinimized = "On (Minimized)";

    public const string DnsProviderCloudflare = "Cloudflare (DoH)";
    public const string DnsProviderGoogle = "Google (DoH)";
    public const string DnsProviderQuad9 = "Quad9 (DoH)";
    public const string DnsProviderCustom = "Custom (DoH)";

    private static readonly HashSet<string> SettingsProperties = new(StringComparer.Ordinal)
    {
        nameof(AutoConnect),
        nameof(AutoStartMode),
        nameof(MinimizeToTray),
        nameof(AutoUpdate),
        nameof(KillSwitchEnabled),
        nameof(IsDarkMode),
        nameof(UseNativeTheme),
        nameof(SelectedLocation),
        nameof(SelectedEntryLocation),
        nameof(SelectedConnectionMode),
        nameof(UseHybridRouting),
        nameof(UseTorBridges),
        nameof(UseCensoredMode),
        nameof(SelectedBridgeType),
        nameof(CustomBridges),
        nameof(CustomSniHosts),
        nameof(UseSnowflakeAmp),
        nameof(SnowflakeAmpCache),
        nameof(TorIpv6Mode),
        nameof(HardwareAccelerationMode),
        nameof(ConnectionPaddingMode),
        nameof(SelectedDnsProvider),
        nameof(CustomDohHost),
        nameof(CustomDohPath),
        nameof(HybridRouteAllWebTraffic),
        nameof(HybridBlockQuicForTorApps),
        nameof(HybridTorApps),
        nameof(HybridBypassApps)
    };

    private readonly OnionHopClient _client;
    private readonly SettingsService _settingsService = new();
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _settingsSaveCts;
    private bool _loadingSettings;
    private bool _disposed;

    public AppStateViewModel()
    {
        Locations =
        [
            AutomaticLocationLabel,
            "United States",
            "United Kingdom",
            "Germany",
            "France",
            "Switzerland",
            "Netherlands",
            "Canada",
            "Singapore"
        ];

        ConnectionModes =
        [
            ConnectionModeProxy,
            ConnectionModeTun
        ];

        AutoStartModes =
        [
            AutoStartModeOff,
            AutoStartModeOn,
            AutoStartModeMinimized
        ];

        DnsProviders =
        [
            DnsProviderCloudflare,
            DnsProviderGoogle,
            DnsProviderQuad9,
            DnsProviderCustom
        ];

        TorOptionModes =
        [
            OnionHopConnectOptions.ToggleModeDefault,
            OnionHopConnectOptions.ToggleModeEnabled,
            OnionHopConnectOptions.ToggleModeDisabled
        ];

        ConnectionPaddingModes =
        [
            OnionHopConnectOptions.ConnectionPaddingAuto,
            OnionHopConnectOptions.ConnectionPaddingEnabled,
            OnionHopConnectOptions.ConnectionPaddingDisabled
        ];

        BridgeTypes.Add("obfs4");
        BridgeTypes.Add("snowflake");
        BridgeTypes.Add("meek-azure");
        BridgeTypes.Add("webtunnel");
        BridgeTypes.Add("custom");

        _client = new OnionHopClient();
        _client.Log += (_, message) => Dispatcher.UIThread.Post(() => AppendLog(message));
        _client.DnsLog += (_, message) => Dispatcher.UIThread.Post(() => AppendDnsLog(message));
        _client.StatusUpdated += (_, update) => Dispatcher.UIThread.Post(() => ApplyClientStatus(update));
        _client.DependencyUpdated += (_, update) => Dispatcher.UIThread.Post(() => ApplyDependencyUpdate(update));

        LoadSettings();
        if (OperatingSystem.IsWindows() &&
            !string.Equals(AutoStartMode, AutoStartModeOff, StringComparison.OrdinalIgnoreCase))
        {
            WindowsAutoStartService.Update(StartWithWindows, StartMinimized, AppendLog);
        }
        ApplyTheme();

        PropertyChanged += OnAnyPropertyChanged;
    }

    public ObservableCollection<string> Locations { get; }
    public ObservableCollection<string> ConnectionModes { get; }
    public ObservableCollection<string> AutoStartModes { get; }
    public ObservableCollection<string> BridgeTypes { get; } = [];
    public ObservableCollection<string> DnsProviders { get; }
    public ObservableCollection<string> TorOptionModes { get; }
    public ObservableCollection<string> ConnectionPaddingModes { get; }

    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<string> DnsLogLines { get; } = [];

    [ObservableProperty] private string _downloadSpeed = "--";
    [ObservableProperty] private string _uploadSpeed = "--";
    [ObservableProperty] private double _downloadSpeedGauge;
    [ObservableProperty] private double _uploadSpeedGauge;

    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isDisconnecting;

    [ObservableProperty] private string _selectedLocation = AutomaticLocationLabel;
    [ObservableProperty] private string _selectedEntryLocation = AutomaticLocationLabel;
    [ObservableProperty] private string _selectedConnectionMode = ConnectionModeProxy;
    [ObservableProperty] private bool _useHybridRouting;
    [ObservableProperty] private bool _killSwitchEnabled;

    [ObservableProperty] private bool _useTorBridges;
    [ObservableProperty] private bool _useCensoredMode;
    [ObservableProperty] private string _selectedBridgeType = "obfs4";
    [ObservableProperty] private string _customBridges = string.Empty;
    [ObservableProperty] private string _customSniHosts = string.Empty;
    [ObservableProperty] private bool _useSnowflakeAmp;
    [ObservableProperty] private string _snowflakeAmpCache = string.Empty;

    [ObservableProperty] private string _torIpv6Mode = OnionHopConnectOptions.ToggleModeDefault;
    [ObservableProperty] private string _hardwareAccelerationMode = OnionHopConnectOptions.ToggleModeDefault;
    [ObservableProperty] private string _connectionPaddingMode = OnionHopConnectOptions.ConnectionPaddingAuto;

    [ObservableProperty] private string _selectedDnsProvider = DnsProviderCloudflare;
    [ObservableProperty] private string _customDohHost = string.Empty;
    [ObservableProperty] private string _customDohPath = "/dns-query";

    [ObservableProperty] private bool _hybridRouteAllWebTraffic = true;
    [ObservableProperty] private bool _hybridBlockQuicForTorApps = true;
    [ObservableProperty] private string _hybridTorApps = string.Empty;
    [ObservableProperty] private string _hybridBypassApps = string.Empty;

    [ObservableProperty] private bool _autoConnect;
    [ObservableProperty] private string _autoStartMode = AutoStartModeOff;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _autoUpdate;
    [ObservableProperty] private bool _isDarkMode;
    // Windows: always use custom chrome (native titlebar creates an unavoidable top bar).
    [ObservableProperty] private bool _useNativeTheme = !OperatingSystem.IsWindows();

    [ObservableProperty] private string _statusMessage = "Ready to route traffic through Tor.";
    [ObservableProperty] private string _connectionStatus = "Disconnected";
    [ObservableProperty] private string _currentIp = "--.--.--.--";
    [ObservableProperty] private double _connectionProgress;

    [ObservableProperty] private bool _isDependencyDownloadInProgress;
    [ObservableProperty] private double _dependencyDownloadProgress;
    [ObservableProperty] private string _dependencyDownloadStatus = "Checking components...";

    private DispatcherTimer? _speedTimer;
    private DispatcherTimer? _ipRefreshTimer;
    private long _lastBytesReceived;
    private long _lastBytesSent;
    private DateTime _lastSpeedSampleUtc;
    private bool _speedUpdateInProgress;

    public bool IsBusy => IsConnecting || IsDisconnecting || IsDependencyDownloadInProgress;

    public bool ShowConnectButton => !IsConnected && !IsConnecting;
    public bool ShowDisconnectButton => IsConnected && !IsDisconnecting;
    public bool ShowCancelButton => IsConnecting;

    public bool IsTunMode => string.Equals(SelectedConnectionMode, ConnectionModeTun, StringComparison.Ordinal);
    public bool IsProxyMode => !IsTunMode;
    public bool CanUseKillSwitch => IsTunMode && !UseHybridRouting;
    public bool IsCustomDoh => string.Equals(SelectedDnsProvider, DnsProviderCustom, StringComparison.Ordinal);
    public bool UseCustomBridges => string.Equals(SelectedBridgeType, "custom", StringComparison.OrdinalIgnoreCase);
    public bool IsSnowflakeBridgeSelected => string.Equals(SelectedBridgeType, "snowflake", StringComparison.OrdinalIgnoreCase);
    public bool UseCustomChrome => !UseNativeTheme;
    public bool SupportsNativeWindowChrome => !OperatingSystem.IsWindows();
    public bool CanConfigureSplitTunneling => IsTunMode && UseHybridRouting;

    partial void OnSelectedBridgeTypeChanged(string value)
    {
        OnPropertyChanged(nameof(UseCustomBridges));
        OnPropertyChanged(nameof(IsSnowflakeBridgeSelected));
    }

    partial void OnIsConnectingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowConnectButton));
        OnPropertyChanged(nameof(ShowDisconnectButton));
        OnPropertyChanged(nameof(ShowCancelButton));
    }

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowConnectButton));
        OnPropertyChanged(nameof(ShowDisconnectButton));
    }

    partial void OnIsDisconnectingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowDisconnectButton));
    }

    partial void OnIsDependencyDownloadInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowConnectButton));
    }

    partial void OnSelectedConnectionModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsTunMode));
        OnPropertyChanged(nameof(IsProxyMode));
        OnPropertyChanged(nameof(CanUseKillSwitch));
        OnPropertyChanged(nameof(CanConfigureSplitTunneling));
    }

    partial void OnUseHybridRoutingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUseKillSwitch));
        OnPropertyChanged(nameof(CanConfigureSplitTunneling));
    }

    partial void OnSelectedDnsProviderChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomDoh));
    }

    partial void OnUseNativeThemeChanged(bool value)
    {
        OnPropertyChanged(nameof(UseCustomChrome));
    }

    partial void OnAutoStartModeChanged(string value)
    {
        StartWithWindows = !string.Equals(value, AutoStartModeOff, StringComparison.OrdinalIgnoreCase);
        StartMinimized = string.Equals(value, AutoStartModeMinimized, StringComparison.OrdinalIgnoreCase);

        if (_loadingSettings || _disposed)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            WindowsAutoStartService.Update(StartWithWindows, StartMinimized, AppendLog);
        }
    }

    public Task InitializeAsync()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        Dispatcher.UIThread.Post(StartSpeedMonitor);
        Dispatcher.UIThread.Post(StartIpAutoRefresh);

        CurrentIp = "Resolving...";
        _ = Task.Run(async () =>
        {
            try
            {
                await _client.RefreshIpAsync(updateStatusMessage: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => AppendLog($"Startup IP lookup failed: {ex.Message}"));
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await _client.EnsureDependenciesAsync().ConfigureAwait(false);
                var bridgeTypes = _client.GetBridgeTypes();
                Dispatcher.UIThread.Post(() =>
                {
                    BridgeTypes.Clear();
                    foreach (var type in bridgeTypes)
                    {
                        BridgeTypes.Add(type);
                    }

                    var recommended = _client.GetRecommendedBridgeType();
                    if (!BridgeTypes.Contains(SelectedBridgeType) &&
                        !string.IsNullOrWhiteSpace(recommended) &&
                        BridgeTypes.Contains(recommended))
                    {
                        SelectedBridgeType = recommended;
                    }
                });

                if (AutoConnect)
                {
                    Dispatcher.UIThread.Post(async () => await ConnectAsync());
                }
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => AppendLog($"Initialization failed: {ex.Message}"));
            }
        });

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PropertyChanged -= OnAnyPropertyChanged;

        try
        {
            if (_speedTimer != null)
            {
                _speedTimer.Stop();
                _speedTimer.Tick -= OnSpeedTimerTick;
                _speedTimer = null;
            }
        }
        catch
        {
        }

        try
        {
            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = null;
        }
        catch
        {
        }

        try
        {
            _settingsSaveCts?.Cancel();
            _settingsSaveCts?.Dispose();
            _settingsSaveCts = null;
        }
        catch
        {
        }

        try
        {
            SaveSettings();
        }
        catch
        {
        }

        _client.Dispose();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (IsDependencyDownloadInProgress)
        {
            StatusMessage = "Downloading components. Please wait.";
            return;
        }

        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = new CancellationTokenSource();

        var options = BuildConnectOptions();

        try
        {
            // Only prompt for elevation when the user actually tries to connect in TUN mode.
            if (OperatingSystem.IsWindows() && IsTunMode && !WindowsAdmin.IsAdministrator())
            {
                StatusMessage = "TUN/VPN mode requires Administrator. Requesting elevation...";
                OnionHopV2.Core.Services.StartupLogger.Write("ConnectAsync: Calling EnsureAdminHelperAsync...");
                
                if (!await _client.EnsureAdminHelperAsync().ConfigureAwait(false))
                {
                    OnionHopV2.Core.Services.StartupLogger.Write("ConnectAsync: EnsureAdminHelperAsync returned false");
                    StatusMessage = "Administrator access is required for TUN/VPN mode. Connection canceled.";
                    return;
                }
                
                OnionHopV2.Core.Services.StartupLogger.Write("ConnectAsync: EnsureAdminHelperAsync succeeded, calling ConnectAsync...");
            }

            OnionHopV2.Core.Services.StartupLogger.Write("ConnectAsync: Calling _client.ConnectAsync...");
            await _client.ConnectAsync(options, _connectCts.Token);
            OnionHopV2.Core.Services.StartupLogger.Write("ConnectAsync: _client.ConnectAsync completed");
        }
        catch (Exception ex)
        {
            OnionHopV2.Core.Services.StartupLogger.Write($"ConnectAsync EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            OnionHopV2.Core.Services.StartupLogger.Write($"Stack trace: {ex.StackTrace}");
            StatusMessage = $"Connection failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        _connectCts?.Cancel();
        await _client.DisconnectAsync();
    }

    [RelayCommand]
    private void CancelConnect()
    {
        if (!IsConnecting)
        {
            return;
        }

        StatusMessage = "Canceling connection attempt...";
        _connectCts?.Cancel();
    }

    [RelayCommand]
    private async Task RefreshIpAsync()
    {
        await _client.RefreshIpAsync(updateStatusMessage: true, CancellationToken.None);
    }

    [RelayCommand]
    private async Task ChangeIdentityAsync()
    {
        await _client.ChangeIdentityAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        if (_disposed)
        {
            return;
        }

        _loadingSettings = true;
        try
        {
            AutoConnect = false;
            AutoStartMode = AutoStartModeOff;
            MinimizeToTray = false;
            AutoUpdate = false;

            KillSwitchEnabled = false;
            UseHybridRouting = false;

            SelectedLocation = AutomaticLocationLabel;
            SelectedEntryLocation = AutomaticLocationLabel;
            SelectedConnectionMode = ConnectionModeProxy;

            UseTorBridges = false;
            UseCensoredMode = false;
            SelectedBridgeType = "obfs4";
            CustomBridges = string.Empty;
            CustomSniHosts = string.Empty;
            UseSnowflakeAmp = false;
            SnowflakeAmpCache = string.Empty;

            TorIpv6Mode = OnionHopConnectOptions.ToggleModeDefault;
            HardwareAccelerationMode = OnionHopConnectOptions.ToggleModeDefault;
            ConnectionPaddingMode = OnionHopConnectOptions.ConnectionPaddingAuto;

            SelectedDnsProvider = DnsProviderCloudflare;
            CustomDohHost = string.Empty;
            CustomDohPath = "/dns-query";

            HybridRouteAllWebTraffic = true;
            HybridBlockQuicForTorApps = true;
            HybridTorApps = string.Empty;
            HybridBypassApps = string.Empty;

            IsDarkMode = true;

            // Windows: always use custom chrome (native titlebar creates an unavoidable top bar).
            if (OperatingSystem.IsWindows())
            {
                UseNativeTheme = false;
            }
        }
        finally
        {
            _loadingSettings = false;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                WindowsAutoStartService.Update(StartWithWindows, StartMinimized, AppendLog);
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to update Windows startup after reset: {ex.Message}");
            }
        }

        ApplyTheme();
        SaveSettings();
        StatusMessage = "Default settings restored.";
    }

    private OnionHopConnectOptions BuildConnectOptions()
    {
        return new OnionHopConnectOptions
        {
            SelectedLocation = SelectedLocation,
            SelectedEntryLocation = SelectedEntryLocation,
            SelectedConnectionMode = SelectedConnectionMode,
            UseHybridRouting = UseHybridRouting,
            KillSwitchEnabled = KillSwitchEnabled,
            UseTorBridges = UseTorBridges,
            UseCensoredMode = UseCensoredMode,
            SelectedBridgeType = SelectedBridgeType,
            CustomBridges = CustomBridges,
            CustomSniHosts = CustomSniHosts,
            UseSnowflakeAmp = UseSnowflakeAmp,
            SnowflakeAmpCache = SnowflakeAmpCache,
            TorIpv6Mode = TorIpv6Mode,
            HardwareAccelerationMode = HardwareAccelerationMode,
            ConnectionPaddingMode = ConnectionPaddingMode,
            SelectedDnsProvider = SelectedDnsProvider,
            CustomDohHost = CustomDohHost,
            CustomDohPath = CustomDohPath,
            HybridRouteAllWebTraffic = HybridRouteAllWebTraffic,
            HybridBlockQuicForTorApps = HybridBlockQuicForTorApps,
            HybridTorApps = HybridTorApps,
            HybridBypassApps = HybridBypassApps
        };
    }

    private void ApplyClientStatus(OnionHopClient.StatusUpdate update)
    {
        IsConnecting = update.IsConnecting;
        IsConnected = update.IsConnected;
        IsDisconnecting = update.IsDisconnecting;
        ConnectionStatus = update.ConnectionStatus;
        StatusMessage = update.StatusMessage;
        ConnectionProgress = update.ConnectionProgress;
        CurrentIp = update.CurrentIp;
    }

    private void ApplyDependencyUpdate(OnionHopClient.DependencyUpdate update)
    {
        IsDependencyDownloadInProgress = update.InProgress;
        DependencyDownloadStatus = update.Status;
        DependencyDownloadProgress = update.Progress;
    }

    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loadingSettings || _disposed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(e.PropertyName))
        {
            return;
        }

        if (SettingsProperties.Contains(e.PropertyName))
        {
            ScheduleSave();
        }

        if (e.PropertyName == nameof(IsDarkMode) || e.PropertyName == nameof(UseNativeTheme))
        {
            ApplyTheme();
        }
    }

    private void ScheduleSave()
    {
        _settingsSaveCts?.Cancel();
        _settingsSaveCts?.Dispose();

        var cts = new CancellationTokenSource();
        _settingsSaveCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token).ConfigureAwait(false);
                SaveSettings();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => AppendLog($"Settings save failed: {ex.Message}"));
            }
        }, token);
    }

    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            var settings = _settingsService.Load();
            if (settings == null)
            {
                return;
            }

            AutoConnect = settings.AutoConnect;
            AutoStartMode = ResolveAutoStartMode(settings);
            MinimizeToTray = settings.MinimizeToTray;
            AutoUpdate = settings.AutoUpdate;
            KillSwitchEnabled = settings.KillSwitchEnabled;
            IsDarkMode = settings.IsDarkMode;
            UseNativeTheme = settings.UseNativeTheme;

            // Force custom chrome on Windows to remove the titlebar strip and keep custom buttons visible.
            if (OperatingSystem.IsWindows())
            {
                UseNativeTheme = false;
            }

            SelectedLocation = string.IsNullOrWhiteSpace(settings.SelectedLocation) ? AutomaticLocationLabel : settings.SelectedLocation;
            if (!Locations.Contains(SelectedLocation))
            {
                SelectedLocation = AutomaticLocationLabel;
            }

            SelectedEntryLocation = string.IsNullOrWhiteSpace(settings.SelectedEntryLocation) ? AutomaticLocationLabel : settings.SelectedEntryLocation;
            if (!Locations.Contains(SelectedEntryLocation))
            {
                SelectedEntryLocation = AutomaticLocationLabel;
            }

            SelectedConnectionMode = string.IsNullOrWhiteSpace(settings.SelectedConnectionMode) ? ConnectionModeProxy : settings.SelectedConnectionMode;
            if (!ConnectionModes.Contains(SelectedConnectionMode))
            {
                SelectedConnectionMode = ConnectionModeProxy;
            }

            UseHybridRouting = settings.UseHybridRouting;
            UseTorBridges = settings.UseTorBridges;
            UseCensoredMode = settings.UseCensoredMode;
            SelectedBridgeType = string.IsNullOrWhiteSpace(settings.SelectedBridgeType) ? SelectedBridgeType : settings.SelectedBridgeType;
            CustomBridges = settings.CustomBridges ?? string.Empty;
            CustomSniHosts = settings.CustomSniHosts ?? string.Empty;
            UseSnowflakeAmp = settings.UseSnowflakeAmp;
            SnowflakeAmpCache = settings.SnowflakeAmpCache ?? string.Empty;

            TorIpv6Mode = string.IsNullOrWhiteSpace(settings.TorIpv6Mode)
                ? OnionHopConnectOptions.ToggleModeDefault
                : settings.TorIpv6Mode;
            if (!TorOptionModes.Contains(TorIpv6Mode))
            {
                TorIpv6Mode = OnionHopConnectOptions.ToggleModeDefault;
            }

            HardwareAccelerationMode = string.IsNullOrWhiteSpace(settings.HardwareAccelerationMode)
                ? OnionHopConnectOptions.ToggleModeDefault
                : settings.HardwareAccelerationMode;
            if (!TorOptionModes.Contains(HardwareAccelerationMode))
            {
                HardwareAccelerationMode = OnionHopConnectOptions.ToggleModeDefault;
            }

            ConnectionPaddingMode = string.IsNullOrWhiteSpace(settings.ConnectionPaddingMode)
                ? OnionHopConnectOptions.ConnectionPaddingAuto
                : settings.ConnectionPaddingMode;
            if (!ConnectionPaddingModes.Contains(ConnectionPaddingMode))
            {
                ConnectionPaddingMode = OnionHopConnectOptions.ConnectionPaddingAuto;
            }

            SelectedDnsProvider = string.IsNullOrWhiteSpace(settings.SelectedDnsProvider) ? DnsProviderCloudflare : settings.SelectedDnsProvider;
            if (!DnsProviders.Contains(SelectedDnsProvider))
            {
                SelectedDnsProvider = DnsProviderCloudflare;
            }

            CustomDohHost = settings.CustomDohHost ?? string.Empty;
            CustomDohPath = string.IsNullOrWhiteSpace(settings.CustomDohPath) ? "/dns-query" : settings.CustomDohPath;

            HybridRouteAllWebTraffic = settings.HybridRouteAllWebTraffic ?? true;
            HybridBlockQuicForTorApps = settings.HybridBlockQuicForTorApps ?? true;
            HybridTorApps = settings.HybridTorApps ?? string.Empty;
            HybridBypassApps = settings.HybridBypassApps ?? string.Empty;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void StartIpAutoRefresh()
    {
        _ipRefreshTimer?.Stop();
        _ipRefreshTimer = new DispatcherTimer
        {
            // "every few seconds" but without spamming the endpoint
            Interval = TimeSpan.FromSeconds(15)
        };

        _ipRefreshTimer.Tick += async (_, _) =>
        {
            if (_disposed || _loadingSettings)
            {
                return;
            }

            // Refresh in the background; avoid disturbing status text during connects/disconnects.
            if (IsBusy)
            {
                return;
            }

            try
            {
                await _client.RefreshIpAsync(updateStatusMessage: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => AppendLog($"Auto IP refresh failed: {ex.Message}"));
            }
        };

        _ipRefreshTimer.Start();
    }

    private void SaveSettings()
    {
        if (_disposed)
        {
            return;
        }

        var startWithWindows = !string.Equals(AutoStartMode, AutoStartModeOff, StringComparison.OrdinalIgnoreCase);
        var startMinimized = string.Equals(AutoStartMode, AutoStartModeMinimized, StringComparison.OrdinalIgnoreCase);

        var settings = new UserSettings
        {
            AutoConnect = AutoConnect,
            AutoStartMode = AutoStartMode,
            StartWithWindows = startWithWindows,
            StartMinimized = startMinimized,
            MinimizeToTray = MinimizeToTray,
            AutoUpdate = AutoUpdate,
            KillSwitchEnabled = KillSwitchEnabled,
            IsDarkMode = IsDarkMode,
            UseNativeTheme = UseNativeTheme,
            SelectedLocation = SelectedLocation,
            SelectedEntryLocation = SelectedEntryLocation,
            SelectedConnectionMode = SelectedConnectionMode,
            UseHybridRouting = UseHybridRouting,
            UseTorBridges = UseTorBridges,
            UseCensoredMode = UseCensoredMode,
            SelectedBridgeType = SelectedBridgeType,
            CustomBridges = CustomBridges,
            CustomSniHosts = CustomSniHosts,
            UseSnowflakeAmp = UseSnowflakeAmp,
            SnowflakeAmpCache = SnowflakeAmpCache,
            TorIpv6Mode = TorIpv6Mode,
            HardwareAccelerationMode = HardwareAccelerationMode,
            ConnectionPaddingMode = ConnectionPaddingMode,
            SelectedDnsProvider = SelectedDnsProvider,
            CustomDohHost = CustomDohHost,
            CustomDohPath = CustomDohPath,
            HybridRouteAllWebTraffic = HybridRouteAllWebTraffic,
            HybridBlockQuicForTorApps = HybridBlockQuicForTorApps,
            HybridTorApps = HybridTorApps,
            HybridBypassApps = HybridBypassApps
        };

        _settingsService.Save(settings);
    }

    private static string ResolveAutoStartMode(UserSettings settings)
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

    private void ApplyTheme()
    {
        if (Application.Current == null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = IsDarkMode ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    public void AppendLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        LogLines.Add(line);
        TrimLogs(LogLines);
    }

    public void AppendDnsLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        DnsLogLines.Add(line);
        TrimLogs(DnsLogLines);
    }

    private static void TrimLogs(ObservableCollection<string> list)
    {
        const int max = 2000;
        const int batch = 200;
        if (list.Count <= max + batch)
        {
            return;
        }

        var toRemove = list.Count - max;
        for (var i = 0; i < toRemove; i++)
        {
            list.RemoveAt(0);
        }
    }

    private void StartSpeedMonitor()
    {
        if (_disposed)
        {
            return;
        }

        _speedTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        _speedTimer.Tick -= OnSpeedTimerTick;
        _speedTimer.Tick += OnSpeedTimerTick;

        _lastSpeedSampleUtc = DateTime.UtcNow;
        _lastBytesReceived = 0;
        _lastBytesSent = 0;

        OnSpeedTimerTick(this, EventArgs.Empty);
        _speedTimer.Start();
    }

    private async void OnSpeedTimerTick(object? sender, EventArgs e)
    {
        if (_speedUpdateInProgress || _disposed)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var elapsed = (now - _lastSpeedSampleUtc).TotalSeconds;
        if (elapsed <= 0.2)
        {
            return;
        }

        _speedUpdateInProgress = true;
        try
        {
            var traffic = await _client.TryGetTorTrafficBytesAsync(CancellationToken.None);
            if (!traffic.HasValue)
            {
                DownloadSpeed = "--";
                UploadSpeed = "--";
                DownloadSpeedGauge = 0;
                UploadSpeedGauge = 0;
                _lastSpeedSampleUtc = now;
                _lastBytesReceived = 0;
                _lastBytesSent = 0;
                return;
            }

            var rx = traffic.Value.BytesRead;
            var tx = traffic.Value.BytesWritten;

            if (_lastBytesReceived == 0 && _lastBytesSent == 0)
            {
                _lastBytesReceived = rx;
                _lastBytesSent = tx;
                _lastSpeedSampleUtc = now;
                DownloadSpeed = "0 B/s";
                UploadSpeed = "0 B/s";
                DownloadSpeedGauge = 0;
                UploadSpeedGauge = 0;
                return;
            }

            var downBytesPerSecond = Math.Max(0, rx - _lastBytesReceived) / elapsed;
            var upBytesPerSecond = Math.Max(0, tx - _lastBytesSent) / elapsed;

            _lastBytesReceived = rx;
            _lastBytesSent = tx;
            _lastSpeedSampleUtc = now;

            DownloadSpeed = FormatRate(downBytesPerSecond);
            UploadSpeed = FormatRate(upBytesPerSecond);
            DownloadSpeedGauge = NormalizeGauge(downBytesPerSecond);
            UploadSpeedGauge = NormalizeGauge(upBytesPerSecond);
        }
        catch
        {
            DownloadSpeed = "--";
            UploadSpeed = "--";
            DownloadSpeedGauge = 0;
            UploadSpeedGauge = 0;
        }
        finally
        {
            _speedUpdateInProgress = false;
        }
    }

    private static string FormatRate(double bytesPerSecond)
    {
        const double kilo = 1024;
        const double mega = 1024 * 1024;

        if (bytesPerSecond < kilo)
        {
            return $"{bytesPerSecond:0} B/s";
        }

        if (bytesPerSecond < mega)
        {
            return $"{bytesPerSecond / kilo:0.0} KB/s";
        }

        return $"{bytesPerSecond / mega:0.00} MB/s";
    }

    private static double NormalizeGauge(double bytesPerSecond)
    {
        // Display up to ~50 MB/s on the gauge.
        const double max = 50d * 1024 * 1024;
        var value = bytesPerSecond / max;
        if (value < 0)
        {
            return 0;
        }

        return value > 1 ? 1 : value;
    }
}
