$ErrorActionPreference = "Stop"

# Ensure TLS 1.2+ is used
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13

# Configuration
$TorVersion = "14.0.4"
$TorUrl = "https://archive.torproject.org/tor-package-archive/torbrowser/$TorVersion/tor-expert-bundle-windows-x86_64-$TorVersion.tar.gz"
$SingBoxApiUrl = "https://api.github.com/repos/SagerNet/sing-box/releases/latest"
$WintunUrl = "https://www.wintun.net/builds/wintun-0.14.1.zip"

$RepoRoot = $PSScriptRoot
$TorDir = Join-Path $RepoRoot "OnionHop\tor"
$VpnDir = Join-Path $RepoRoot "OnionHop\vpn"
$PtDir = Join-Path $TorDir "pluggable_transports"
$TempDir = Join-Path $RepoRoot "temp_deps"

# Helper to ensure directory exists
function Ensure-Dir($path) {
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
}

# Helper to pause on exit
function Exit-WithPause($code) {
    if ($Host.Name -eq "ConsoleHost") {
        Write-Host "`nPress any key to exit..."
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    }
    exit $code
}

try {
    Write-Host "=== OnionHop Dependency Downloader ==="

    # Cleanup temp
    if (Test-Path $TempDir) { Remove-Item $TempDir -Recurse -Force }
    Ensure-Dir $TempDir
    Ensure-Dir $TorDir
    Ensure-Dir $VpnDir
    Ensure-Dir $PtDir

    # --- 1. Tor Expert Bundle ---
    Write-Host "`n[1/3] Downloading Tor Expert Bundle ($TorVersion)..."
    $TorArchive = Join-Path $TempDir "tor.tar.gz"
    try {
        Invoke-WebRequest -Uri $TorUrl -OutFile $TorArchive
    } catch {
        Write-Error "Failed to download Tor. Check the version URL."
        throw $_
    }

    Write-Host "Extracting Tor..."
    if (Get-Command "tar" -ErrorAction SilentlyContinue) {
        tar -xf $TorArchive -C $TempDir
    } else {
        throw "The 'tar' command is required to extract the Tor Expert Bundle but was not found."
    }

    # Check structure
    $ExtractedTorRoot = Join-Path $TempDir "tor"
    if (-not (Test-Path $ExtractedTorRoot)) {
        Write-Warning "Expected 'tor' directory in archive not found. Listing temp contents:"
        Get-ChildItem $TempDir | ForEach-Object { Write-Host " - $($_.Name)" }
        throw "Tor extraction failed or unexpected structure."
    }

    Write-Host "Installing Tor binaries..."
    Copy-Item (Join-Path $ExtractedTorRoot "tor.exe") $TorDir -Force
    Copy-Item (Join-Path $ExtractedTorRoot "tor-gencert.exe") $TorDir -Force

    $ExtractedDataRoot = Join-Path $TempDir "data"
    if (Test-Path $ExtractedDataRoot) {
        Copy-Item (Join-Path $ExtractedDataRoot "geoip") $TorDir -Force
        Copy-Item (Join-Path $ExtractedDataRoot "geoip6") $TorDir -Force
    } else {
        Write-Warning "Could not find 'data' directory for geoip files."
    }

    Write-Host "Installing Pluggable Transports..."
    $ExtractedPtDir = Join-Path $ExtractedTorRoot "pluggable_transports"
    if (Test-Path $ExtractedPtDir) {
        Copy-Item "$ExtractedPtDir\*" $PtDir -Recurse -Force
    }

    # Handle renamed binaries
    if (-not (Test-Path (Join-Path $PtDir "lyrebird.exe")) -and (Test-Path (Join-Path $PtDir "obfs4proxy.exe"))) {
        Write-Host "Renaming obfs4proxy.exe to lyrebird.exe..."
        Rename-Item -Path (Join-Path $PtDir "obfs4proxy.exe") -NewName "lyrebird.exe" -Force
    }

    # --- 2. Sing-box ---
    Write-Host "`n[2/3] Fetching Sing-box..."
    $SbRelease = Invoke-RestMethod -Uri $SingBoxApiUrl
    $SbAsset = $SbRelease.assets | Where-Object { $_.name -like "*windows-amd64.zip" } | Select-Object -First 1
    if (-not $SbAsset) { throw "No windows-amd64 asset found." }
    
    $SbUrl = $SbAsset.browser_download_url
    Write-Host "Downloading $($SbAsset.name)..."
    $SbArchive = Join-Path $TempDir "sing-box.zip"
    Invoke-WebRequest -Uri $SbUrl -OutFile $SbArchive
    
    Write-Host "Extracting Sing-box..."
    Expand-Archive -Path $SbArchive -DestinationPath $TempDir -Force
    $SbExtractedDir = Get-ChildItem -Path $TempDir -Directory | Where-Object { $_.Name -like "sing-box-*" } | Select-Object -First 1
    Copy-Item (Join-Path $SbExtractedDir.FullName "sing-box.exe") $VpnDir -Force

    # --- 3. Wintun ---
    Write-Host "`n[3/3] Downloading Wintun..."
    $WintunArchive = Join-Path $TempDir "wintun.zip"
    Invoke-WebRequest -Uri $WintunUrl -OutFile $WintunArchive
    
    Write-Host "Extracting Wintun..."
    Expand-Archive -Path $WintunArchive -DestinationPath (Join-Path $TempDir "wintun") -Force
    Copy-Item (Join-Path $TempDir "wintun\wintun\bin\amd64\wintun.dll") $VpnDir -Force

    # Cleanup
    Remove-Item $TempDir -Recurse -Force

    Write-Host "`nDone! Binaries updated." -ForegroundColor Green
    Exit-WithPause 0

} catch {
    Write-Error "An error occurred: $_"
    Exit-WithPause 1
}
