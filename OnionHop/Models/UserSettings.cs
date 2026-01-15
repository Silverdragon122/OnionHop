namespace OnionHop;

internal sealed class UserSettings
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
