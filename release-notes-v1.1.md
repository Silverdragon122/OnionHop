## OnionHop v1.1 - UX & Quality Update

**OnionHop** is a lightweight Windows WPF app that routes your traffic through **Tor**.

### Highlights

- **Minimize to Tray**: Closing the window keeps OnionHop running in the tray
- **Start with Windows**: Off / On / Minimized option in Settings
- **Auto Update**: Checks GitHub releases and offers updates
- **Native Windows UI**: Standard window chrome with dark mode support
- **Single-Instance**: Relaunching focuses the running app instead of opening a second one
- **Icon Fixes**: Window + tray icons now use the OnionHop icon

### Installation

1. Download OnionHop-Setup-1.1.0.exe
2. Run the installer
3. Launch OnionHop and choose your connection mode
4. Configure auto-start, tray, and update options in Settings

### Requirements

- Windows 10/11
- .NET 9.0 Runtime (included in self-contained installer)

### Notes

- Unsigned binaries may trigger antivirus warnings - this is normal for bundled Tor/sing-box executables
- TUN/VPN mode requires Administrator privileges
- Kill Switch requires Administrator to add/remove firewall rules

See the [README](https://github.com/center2055/OnionHop#readme) for full documentation.
