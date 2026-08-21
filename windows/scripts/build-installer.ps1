#Requires -Version 5.1
<#
.SYNOPSIS
    Builds Clippy for Windows and produces an installer per architecture.

.PARAMETER Architecture
    x64, arm64, or both (default). Both architectures can be built from either kind of
    host — cross-publishing is a normal .NET operation.

.PARAMETER SkipInstaller
    Publish and bundle FFmpeg, but stop before Inno Setup.
#>
param(
    [ValidateSet('x64', 'arm64', 'both')]
    [string]$Architecture = 'both',
    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root "Clippy\Clippy.csproj"
$BuildDir = Join-Path $Root "build"
$IssFile = Join-Path $Root "installer\Clippy.iss"
$NuGetConfig = Join-Path $Root "nuget.config"
$RepoRoot = Split-Path -Parent $Root

# Both architectures come from the same FFmpeg release so the two builds stay in step.
$FfmpegRelease = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest"
$FfmpegAssets = @{
    "x64"   = "ffmpeg-n8.1-latest-win64-gpl-8.1.zip"
    "arm64" = "ffmpeg-n8.1-latest-winarm64-gpl-8.1.zip"
}

$Architectures = if ($Architecture -eq 'both') { @('x64', 'arm64') } else { @($Architecture) }

Write-Host "> Clippy Windows build" -ForegroundColor Cyan
Write-Host "  Root:           $Root"
Write-Host "  Architectures:  $($Architectures -join ', ')"

if (-not (Test-Path $Project)) {
    throw "Project not found: $Project"
}

& (Join-Path $PSScriptRoot "prepare-assets.ps1") -WindowsRoot $Root -RepoRoot $RepoRoot

Write-Host "> Installed SDKs:"
$installedSdks = dotnet --list-sdks
$installedSdks

$hasDotNet10 = @($installedSdks | Select-String -Pattern '^\s*10\.' -Quiet) -contains $true
if (-not $hasDotNet10) {
    Write-Host ""
    Write-Host "ERROR: .NET 10 SDK not found." -ForegroundColor Red
    Write-Host "This project targets net10.0-windows. Install it from:" -ForegroundColor Yellow
    Write-Host "  https://dotnet.microsoft.com/download/dotnet/10.0"
    throw "Missing .NET 10 SDK"
}

New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null

function Get-HostArchitecture {
    switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        'Arm64' { return 'arm64' }
        'X64'   { return 'x64' }
        default { return 'other' }
    }
}

function Install-BundledFfmpeg {
    param(
        [Parameter(Mandatory)][string]$Arch,
        [Parameter(Mandatory)][string]$Destination
    )

    $cacheDir = Join-Path $BuildDir "ffmpeg-cache\$Arch"
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null

    $assetName = $FfmpegAssets[$Arch]
    $archive = Join-Path $cacheDir $assetName
    $extract = Join-Path $cacheDir "extract"

    if (-not (Test-Path $archive)) {
        Write-Host "  Downloading FFmpeg for $Arch ($assetName)..."
        $previous = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        try {
            Invoke-WebRequest -Uri "$FfmpegRelease/$assetName" -OutFile $archive -UseBasicParsing
        }
        finally {
            $ProgressPreference = $previous
        }
    }
    else {
        Write-Host "  Using cached FFmpeg archive for $Arch"
    }

    if (Test-Path $extract) {
        Remove-Item $extract -Recurse -Force
    }

    Expand-Archive -Path $archive -DestinationPath $extract -Force

    $source = Get-ChildItem -Path $extract -Filter ffmpeg.exe -Recurse | Select-Object -First 1
    if (-not $source) {
        throw "ffmpeg.exe not found inside $assetName"
    }

    Copy-Item $source.FullName $Destination -Force
    Write-Host "  Bundled ffmpeg.exe ($([math]::Round((Get-Item $Destination).Length / 1MB, 1)) MB)"

    # An FFmpeg built for another architecture cannot be executed here, so only the
    # matching one gets a capability check.
    if ($Arch -eq (Get-HostArchitecture)) {
        $encoders = & $Destination -hide_banner -encoders 2>&1 | Out-String
        if ($encoders -notmatch 'libx264') {
            throw "Bundled FFmpeg for $Arch has no libx264 encoder — video capture would not work"
        }

        $formats = & $Destination -hide_banner -devices 2>&1 | Out-String
        if ($formats -notmatch 'gdigrab') {
            throw "Bundled FFmpeg for $Arch has no gdigrab input — screen capture would not work"
        }

        Write-Host "  Verified: libx264 + gdigrab present"
    }
    else {
        Write-Host "  Skipped capability probe (cannot run $Arch binaries on this host)"
    }
}

function Invoke-CodeSigning {
    param([Parameter(Mandatory)][string[]]$Paths)

    if (-not $env:WINDOWS_SIGN_CERT_BASE64 -or -not $env:WINDOWS_SIGN_CERT_PASSWORD) {
        return
    }

    $signtool = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if (-not $signtool) {
        Write-Host "  signtool.exe not found; skipping signing" -ForegroundColor Yellow
        return
    }

    $pfxPath = Join-Path $BuildDir "sign-cert.pfx"
    [IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($env:WINDOWS_SIGN_CERT_BASE64))
    try {
        foreach ($path in $Paths) {
            if (Test-Path $path) {
                & $signtool.FullName sign /f $pfxPath /p $env:WINDOWS_SIGN_CERT_PASSWORD `
                    /tr http://timestamp.digicert.com /td sha256 /fd sha256 $path
                Write-Host "  Signed $(Split-Path -Leaf $path)"
            }
        }
    }
    finally {
        Remove-Item $pfxPath -Force -ErrorAction SilentlyContinue
    }
}

$InnoPaths = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
)
$Iscc = $InnoPaths | Where-Object { Test-Path $_ } | Select-Object -First 1

$outputs = @()

foreach ($arch in $Architectures) {
    $rid = "win-$arch"
    $publishDir = Join-Path $BuildDir "publish\$arch"

    Write-Host ""
    Write-Host "=== $arch ===" -ForegroundColor Cyan

    if (Test-Path $publishDir) {
        Remove-Item $publishDir -Recurse -Force
    }

    Push-Location $Root
    try {
        Write-Host "> Restoring packages ($rid)"
        $restoreArgs = @("restore", $Project, "-r", $rid, "--force-evaluate")
        if (Test-Path $NuGetConfig) { $restoreArgs += @("--configfile", $NuGetConfig) }

        & dotnet @restoreArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Host ""
            Write-Host "NuGet restore failed. Common fixes:" -ForegroundColor Yellow
            Write-Host "  1. Install .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0"
            Write-Host "  2. Ensure internet access to https://api.nuget.org"
            throw "dotnet restore failed with exit code $LASTEXITCODE"
        }

        Write-Host "> Publishing (Release, $rid, self-contained)"
        $publishArgs = @(
            "publish", $Project,
            "-c", "Release",
            "-r", $rid,
            "-p:Platform=$($arch.ToUpperInvariant())",
            "--self-contained", "true",
            "-p:WindowsAppSDKSelfContained=true",
            "-o", $publishDir
        )

        # ReadyToRun precompiles to native code, which needs a host that can emit it.
        if ($arch -eq (Get-HostArchitecture)) {
            $publishArgs += "-p:PublishReadyToRun=true"
        }

        if (Test-Path $NuGetConfig) { $publishArgs += @("--configfile", $NuGetConfig) }

        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for $rid with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }

    $exe = Join-Path $publishDir "Clippy.exe"
    if (-not (Test-Path $exe)) {
        throw "Clippy.exe not found in publish output: $publishDir"
    }

    Write-Host "> Bundling FFmpeg"
    Install-BundledFfmpeg -Arch $arch -Destination (Join-Path $publishDir "ffmpeg.exe")

    Invoke-CodeSigning -Paths @($exe)

    if ($SkipInstaller) {
        Write-Host "  Skipping installer (-SkipInstaller)"
        $outputs += [pscustomobject]@{ Arch = $arch; Path = $publishDir; Kind = "publish" }
        continue
    }

    if (-not $Iscc) {
        Write-Host "> Inno Setup not found - creating portable ZIP instead" -ForegroundColor Yellow
        $zipPath = Join-Path $BuildDir "Clippy-win-$arch.zip"
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force
        $outputs += [pscustomobject]@{ Arch = $arch; Path = $zipPath; Kind = "zip" }
        continue
    }

    Write-Host "> Building installer with Inno Setup"
    & $Iscc $IssFile "/DPublishDir=$publishDir" "/DBuildDir=$BuildDir" "/DTargetArch=$arch"
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compile failed for $arch"
    }

    $setup = Join-Path $BuildDir "ClippySetup-$arch.exe"
    if (-not (Test-Path $setup)) {
        throw "ClippySetup-$arch.exe was not produced"
    }

    Invoke-CodeSigning -Paths @($setup)
    $outputs += [pscustomobject]@{ Arch = $arch; Path = $setup; Kind = "installer" }
}

Write-Host ""
Write-Host "OK: build complete" -ForegroundColor Green
foreach ($output in $outputs) {
    $size = if (Test-Path -PathType Leaf $output.Path) {
        " ($([math]::Round((Get-Item $output.Path).Length / 1MB, 2)) MB)"
    } else { "" }
    Write-Host "  [$($output.Arch)] $($output.Kind): $($output.Path)$size"
}

if (-not $Iscc -and -not $SkipInstaller) {
    Write-Host ""
    Write-Host "  Install Inno Setup 6 to produce installers instead of ZIPs:" -ForegroundColor Yellow
    Write-Host "  https://jrsoftware.org/isdl.php"
}
