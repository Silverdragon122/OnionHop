# OnionHop V2 (Avalonia + SukiUI)

This is the **V2 UI** for OnionHop, rebuilt with **Avalonia** + **SukiUI** for a cleaner, modern, cross-platform UI.

## Status

- UI: **Windows / Linux / macOS compatible**
- Networking/routing: **Windows-only for now** (Linux/macOS integration will be added later)

## Build

```powershell
dotnet build "OnionHop V2/OnionHopV2.sln" -c Release
```

## Run

```powershell
dotnet run --project "OnionHop V2/src/OnionHopV2.App" -c Release
```

On first connect, the app will ensure Tor + sing-box/Wintun dependencies in its output directory (Windows).

