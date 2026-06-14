# Clippy for Windows

Windows port of [Clippy](https://clippy.asia) — instant screen clips with a rolling 60-second buffer.

Built with **WinUI 3**, **.NET 10**, **FFmpeg**, **NAudio**, and **Windows Speech Recognition**. The UI, flow, and features mirror the macOS app: onboarding, library, settings, global hotkey, voice commands, system audio + mic, and debug log.

## Requirements

- Windows 10 version 19041 (2004) or later — **Windows 11 recommended**
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (build only)
- Visual Studio 2022 with **Windows application development** workload (or `dotnet build` from CLI)

**End users do not install FFmpeg or .NET separately.** The installer ships a self-contained app with FFmpeg bundled.

## Build the installer (Windows only)

WinUI apps must be built on Windows. From PowerShell:

```powershell
cd windows
.\scripts\build-installer.ps1
```

The build script:

1. Prepares logo, icon, and sound assets
2. Publishes a self-contained `Clippy.exe`
3. Downloads and bundles `ffmpeg.exe` next to the app
4. Creates `ClippySetup.exe` with Inno Setup

Output: `windows/build/ClippySetup.exe`

Requires [Inno Setup 6](https://jrsoftware.org/isdl.php). If Inno Setup is not installed, the script falls back to a portable ZIP at `windows/build/Clippy-win-x64.zip`.

### Build with GitHub Actions (recommended)

You do **not** need a local Windows build machine. GitHub builds the installer on `windows-latest` and uploads it as an artifact.

1. Push this repo to GitHub
2. Open the repo → **Actions** → **Windows Installer** → **Run workflow**
3. Download **ClippySetup-win-x64** from Artifacts (~5–10 min build)

Pushes to `main`/`master` that change files under `windows/` also trigger this workflow automatically.

### Optional code signing (SmartScreen / Defender)

Unsigned Windows apps often trigger **Microsoft Defender SmartScreen** (“Windows protected your PC”) until they build reputation. This is normal for new indie releases.

To reduce warnings for users:

1. **Sign both** `Clippy.exe` and `ClippySetup.exe` with an Authenticode certificate (Standard or EV code signing).
2. **EV code signing** gives immediate SmartScreen reputation in most cases.
3. Set GitHub Actions secrets for automated signing:
   - `WINDOWS_SIGN_CERT_BASE64` — PFX file, base64-encoded
   - `WINDOWS_SIGN_CERT_PASSWORD` — PFX password

The build script signs automatically when those secrets are present.

Additional steps that help over time:

- Publish releases from a consistent domain (e.g. [clippy.asia](https://clippy.asia))
- Keep the same publisher name in the installer and certificate
- If Defender flags a build as a false positive, submit the file at [Microsoft Security Intelligence](https://www.microsoft.com/en-us/wdsi/filesubmission)

## First launch

1. Grant **microphone** access when prompted (Settings → Privacy → Microphone)
2. Enable **online speech recognition** (Settings → Privacy → Speech) for voice commands
3. Complete onboarding — pick mic, audio output, try “Clippy, clip that”
4. Default clip hotkey: **Ctrl+K**

## Features (parity with macOS)

| Feature | Windows |
|--------|---------|
| Rolling 60s buffer (5s segments) | FFmpeg + gdigrab + WASAPI loopback |
| Clip 15s / 30s / 60s | Segmented picker |
| Global hotkey | Ctrl+K (RegisterHotKey) |
| Voice commands | Windows Speech Recognition |
| System + mic audio | WASAPI loopback + DirectShow mic |
| Clip library | `%LocalAppData%\Clippy\Clips` |
| Onboarding | Multi-step flow matching macOS |
| Debug log | In-app diagnostics panel |
| Dark green UI | Shared Clippy theme |

## Data locations

```
%LocalAppData%\Clippy\
  settings.json
  clips.json
  Buffer\          # rolling segments
  Clips\           # saved clips
```

## Capture stack

- **Video:** `gdigrab` (desktop)
- **System audio:** `wasapi` loopback (default output device)
- **Microphone:** DirectShow (`dshow`)

FFmpeg is bundled at `{app}\ffmpeg.exe` and detected automatically.

## Known differences from macOS

- Default hotkey is **Ctrl+K** instead of ⌘K
- Custom hotkey recording UI is simplified (reset to Ctrl+K); full key capture coming later
- Capture uses FFmpeg rather than ScreenCaptureKit / AVFoundation

## License

MIT — same as the main Clippy project.
