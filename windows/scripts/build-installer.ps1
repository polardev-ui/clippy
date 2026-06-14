#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root "Clippy\Clippy.csproj"
$BuildDir = Join-Path $Root "build"
$PublishDir = Join-Path $BuildDir "publish"
$IssFile = Join-Path $Root "installer\Clippy.iss"
$NuGetConfig = Join-Path $Root "nuget.config"

Write-Host "> Clippy Windows installer build" -ForegroundColor Cyan
Write-Host "  Root: $Root"

if (-not (Test-Path $Project)) {
    throw "Project not found: $Project"
}

$RepoRoot = Split-Path -Parent $Root
& (Join-Path $PSScriptRoot "prepare-assets.ps1") -WindowsRoot $Root -RepoRoot $RepoRoot

Write-Host "> dotnet --info"
dotnet --info

Write-Host "> Installed SDKs:"
$installedSdks = dotnet --list-sdks
$installedSdks

$hasDotNet10 = @($installedSdks | Select-String -Pattern '^\s*10\.' -Quiet) -contains $true
if (-not $hasDotNet10) {
    Write-Host ""
    Write-Host "ERROR: .NET 10 SDK not found." -ForegroundColor Red
    Write-Host "This project targets net10.0-windows. Install .NET 10 SDK:" -ForegroundColor Yellow
    Write-Host "  https://dotnet.microsoft.com/download/dotnet/10.0"
    throw "Missing .NET 10 SDK"
}

Write-Host "> Restoring NuGet packages"
$restoreArgs = @(
    "restore", $Project,
    "-r", "win-x64",
    "--force-evaluate"
)
if (Test-Path $NuGetConfig) {
    $restoreArgs += @("--configfile", $NuGetConfig)
}

Push-Location $Root
try {
    & dotnet @restoreArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "NuGet restore failed. Common fixes:" -ForegroundColor Yellow
        Write-Host "  1. Install .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0"
        Write-Host "  2. Ensure internet access to https://api.nuget.org"
        Write-Host "  3. Run: dotnet nuget list source"
        Write-Host "  4. Install Visual Studio 2022 with 'Windows application development' workload"
        throw "dotnet restore failed with exit code $LASTEXITCODE"
    }

    Write-Host "> dotnet publish (Release, win-x64, self-contained)"
    if (Test-Path $PublishDir) {
        Remove-Item $PublishDir -Recurse -Force
    }

    $publishArgs = @(
        "publish", $Project,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-p:PublishReadyToRun=true",
        "-p:WindowsAppSDKSelfContained=true",
        "-o", $PublishDir
    )
    if (Test-Path $NuGetConfig) {
        $publishArgs += @("--configfile", $NuGetConfig)
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$exe = Join-Path $PublishDir "Clippy.exe"
if (-not (Test-Path $exe)) {
    throw "Clippy.exe not found in publish output: $PublishDir"
}

Write-Host "> Bundling FFmpeg"
$ffmpegCache = Join-Path $BuildDir "ffmpeg-cache"
$ffmpegZip = Join-Path $ffmpegCache "ffmpeg-win64.zip"
$ffmpegExtract = Join-Path $ffmpegCache "extract"
$ffmpegDest = Join-Path $PublishDir "ffmpeg.exe"
New-Item -ItemType Directory -Force -Path $ffmpegCache | Out-Null

if (-not (Test-Path $ffmpegDest)) {
    if (-not (Test-Path $ffmpegZip)) {
        $ffmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip"
        Write-Host "  Downloading FFmpeg..."
        Invoke-WebRequest -Uri $ffmpegUrl -OutFile $ffmpegZip
    }

    if (Test-Path $ffmpegExtract) {
        Remove-Item $ffmpegExtract -Recurse -Force
    }
    Expand-Archive -Path $ffmpegZip -DestinationPath $ffmpegExtract
    $ffmpegSource = Get-ChildItem -Path $ffmpegExtract -Filter ffmpeg.exe -Recurse | Select-Object -First 1
    if (-not $ffmpegSource) {
        throw "ffmpeg.exe not found inside downloaded archive"
    }

    Copy-Item $ffmpegSource.FullName $ffmpegDest -Force
}

Write-Host "  Bundled: $ffmpegDest ($([math]::Round((Get-Item $ffmpegDest).Length / 1MB, 1)) MB)"

Write-Host "> Published: $exe"

New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null

$InnoPaths = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
)

$Iscc = $InnoPaths | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($Iscc) {
    Write-Host "> Building installer with Inno Setup"
    & $Iscc $IssFile "/DPublishDir=$PublishDir" "/DBuildDir=$BuildDir"
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compile failed"
    }

    $Setup = Join-Path $BuildDir "ClippySetup.exe"
    if (-not (Test-Path $Setup)) {
        throw "ClippySetup.exe was not produced"
    }

    Write-Host ""
    Write-Host "OK: Installer ready: $Setup" -ForegroundColor Green
    Write-Host "  Size: $([math]::Round((Get-Item $Setup).Length / 1MB, 2)) MB"

    if ($env:WINDOWS_SIGN_CERT_BASE64 -and $env:WINDOWS_SIGN_CERT_PASSWORD) {
        Write-Host "> Code signing (optional certificate provided)"
        $signtool = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($signtool) {
            $pfxPath = Join-Path $BuildDir "sign-cert.pfx"
            [IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($env:WINDOWS_SIGN_CERT_BASE64))
            & $signtool.FullName sign /f $pfxPath /p $env:WINDOWS_SIGN_CERT_PASSWORD /tr http://timestamp.digicert.com /td sha256 /fd sha256 $exe
            & $signtool.FullName sign /f $pfxPath /p $env:WINDOWS_SIGN_CERT_PASSWORD /tr http://timestamp.digicert.com /td sha256 /fd sha256 $Setup
            Remove-Item $pfxPath -Force
            Write-Host "  Signed Clippy.exe and ClippySetup.exe"
        }
        else {
            Write-Host "  signtool.exe not found; skipping signing" -ForegroundColor Yellow
        }
    }

    exit 0
}

Write-Host "> Inno Setup not found - creating portable ZIP instead" -ForegroundColor Yellow
$ZipPath = Join-Path $BuildDir "Clippy-win-x64.zip"
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $ZipPath -Force

Write-Host ""
Write-Host "OK: Portable build ready: $ZipPath" -ForegroundColor Green
Write-Host "  Install Inno Setup 6 to produce ClippySetup.exe:" -ForegroundColor Yellow
Write-Host "  https://jrsoftware.org/isdl.php"
Write-Host ""
Write-Host "  Then re-run: .\scripts\build-installer.ps1"
