#Requires -Version 5.1
param(
    [string]$WindowsRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$RepoRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$AssetsDir = Join-Path $WindowsRoot "Clippy\Assets"
$LogoPath = Join-Path $AssetsDir "clippy-logo.png"
$SoundPath = Join-Path $AssetsDir "clip.wav"
$IconPath = Join-Path $AssetsDir "clippy-icon.ico"

$MacLogo = Join-Path $RepoRoot "Clippy\Resources\Assets.xcassets\ClippyLogo.imageset\clippy-logo@2x.png"
$MacSound = Join-Path $RepoRoot "Clippy\Resources\clip.wav"
$MacIconSource = Join-Path $RepoRoot "Clippy\Resources\Assets.xcassets\AppIcon.appiconset\icon_256x256@1x.png"

Write-Host "> Preparing Windows assets"
New-Item -ItemType Directory -Force -Path $AssetsDir | Out-Null

if (-not (Test-Path $LogoPath) -and (Test-Path $MacLogo)) {
    Copy-Item $MacLogo $LogoPath
    Write-Host "  Copied logo from macOS assets"
}

if (-not (Test-Path $SoundPath) -and (Test-Path $MacSound)) {
    Copy-Item $MacSound $SoundPath
    Write-Host "  Copied clip sound from macOS assets"
}

if (-not (Test-Path $IconPath)) {
    if (Test-Path $MacIconSource) {
        Add-Type -AssemblyName System.Drawing
        $bitmap = [System.Drawing.Bitmap]::FromFile($MacIconSource)
        $handle = $bitmap.GetHicon()
        $icon = [System.Drawing.Icon]::FromHandle($handle)
        $stream = [System.IO.FileStream]::new($IconPath, [System.IO.FileMode]::Create)
        $icon.Save($stream)
        $stream.Close()
        $icon.Dispose()
        $bitmap.Dispose()
        Write-Host "  Generated clippy-icon.ico"
    }
    else {
        throw "Missing clippy-icon.ico and no macOS icon source at $MacIconSource"
    }
}

foreach ($required in @($LogoPath, $SoundPath, $IconPath)) {
    if (-not (Test-Path $required)) {
        throw "Missing required asset: $required"
    }
}

Write-Host "  Assets ready in $AssetsDir"
