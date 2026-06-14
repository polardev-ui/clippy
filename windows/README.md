# Clippy for Windows

Windows port of [Clippy](https://clippy.asia) — instant screen clips with a rolling 60-second buffer.

Built with **WinUI 3**, **.NET 10**, **FFmpeg**, **NAudio**, and **Windows Speech Recognition**. The UI, flow, and features mirror the macOS app: onboarding, library, settings, global hotkey, voice commands, system audio + mic, and debug log.

## Requirements

- Windows 10 version 19041 (2004) or later — **Windows 11 recommended**
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [FFmpeg](https://ffmpeg.org/download.html) on `PATH` or at `C:\ffmpeg\bin\ffmpeg.exe`
- Visual Studio 2022 with **Windows application development** workload (or `dotnet build` from CLI)

## Build the installer (Windows only)

WinUI apps must be built on Windows. From PowerShell:

```powershell
cd windows
.\scripts\build-installer.ps1
```

Output: `windows/build/ClippySetup.exe`

Requires [Inno Setup 6](https://jrsoftware.org/isdl.php). If Inno Setup is not installed, the script falls back to a portable ZIP at `windows/build/Clippy-win-x64.zip`.

### Build with GitHub Actions (recommended)

You do **not** need a local Windows build machine. GitHub builds the installer on `windows-latest` and uploads it as an artifact.

1. Push this repo to GitHub (e.g. `https://github.com/polardev-ui/clippy`)
2. Open the repo on GitHub → **Actions**
3. Select **Windows Installer** in the left sidebar
4. Click **Run workflow** → **Run workflow** (manual run works even before anything is on `main`)
5. Wait for the green checkmark (~5–10 min)
6. Open the completed run → scroll to **Artifacts** → download **ClippySetup-win-x64**

The artifact contains `ClippySetup.exe`. Artifacts are kept for 30 days.

Pushes to `main`/`master` that change files under `windows/` also trigger this workflow automatically.

**First-time push example:**

```bash
git add .
git commit -m "Add Windows port and GitHub Actions installer build"
git remote add origin https://github.com/polardev-ui/clippy.git
git push -u origin main
```

Then go to **Actions** → **Windows Installer** → **Run workflow**.

## Build (app only)

```powershell
cd windows
dotnet restore Clippy.sln
dotnet build Clippy.sln -c Release
```

Run:

```powershell
dotnet run --project Clippy\Clippy.csproj -c Release
```

Output: `Clippy\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\`

## First launch

1. Grant **microphone** access when prompted (Settings → Privacy → Microphone)
2. Enable **online speech recognition** (Settings → Privacy → Speech) for voice commands
3. Complete onboarding — pick mic, audio output, try “Clippy, clip that”
4. Default clip hotkey: **Ctrl+K**

## Features (parity with macOS)

| Feature | Windows |
|--------|---------|
| Rolling 60s buffer (5s segments) | ✅ FFmpeg + gdigrab + WASAPI loopback |
| Clip 15s / 30s / 60s | ✅ |
| Global hotkey | ✅ Ctrl+K (RegisterHotKey) |
| Voice commands | ✅ Windows Speech Recognition |
| System + mic audio | ✅ WASAPI loopback + DirectShow mic |
| Clip library | ✅ `%LocalAppData%\Clippy\Clips` |
| Onboarding | ✅ |
| Debug log | ✅ |
| Dark green UI | ✅ |

## Data locations

```
%LocalAppData%\Clippy\
  settings.json
  clips.json
  Buffer\          # rolling segments
  Clips\           # saved clips
```

## FFmpeg

Screen capture uses FFmpeg:

- **Video:** `gdigrab` (desktop)
- **System audio:** `wasapi` loopback (default output device)
- **Microphone:** DirectShow (`dshow`)

Install FFmpeg and verify:

```powershell
ffmpeg -version
```

You can also place `ffmpeg.exe` next to `Clippy.exe`.

## Known differences from macOS

- Default hotkey is **Ctrl+K** instead of ⌘K
- Custom hotkey recording UI is simplified (reset to Ctrl+K); full key capture coming later
- Capture uses FFmpeg rather than ScreenCaptureKit / AVFoundation
- Requires FFmpeg as an external dependency
- Display selection uses monitor index (not per-display SCStream)

## License

MIT — same as the main Clippy project.
