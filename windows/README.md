# Clippy for Windows

Windows port of [Clippy](https://clippy.asia) — instant screen clips with a rolling 60-second buffer.

Built with **WinUI 3**, **.NET 10**, **FFmpeg**, **NAudio**, and **Windows Speech Recognition**. The UI, flow, and features mirror the macOS app: onboarding, library, settings, global hotkey, voice commands, system audio + mic, and debug log.

## Requirements

- Windows 10 version 19041 (2004) or later — **Windows 11 recommended**
- **x64** or **ARM64** — a native build is published for each
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (build only)

**End users do not install FFmpeg or .NET separately.** Each installer ships a self-contained app with the matching FFmpeg build alongside it.

## Which installer

| PC | Installer |
|----|-----------|
| Intel / AMD | `ClippySetup-x64.exe` |
| Snapdragon / ARM (Surface Pro X, Copilot+ PCs) | `ClippySetup-arm64.exe` |

The ARM64 installer refuses to run on x64. The x64 installer *will* run on ARM64 through emulation, so it works as a fallback — but the native ARM64 build encodes noticeably faster and uses less battery, so prefer it.

## Build

From PowerShell, on Windows:

```powershell
cd windows
.\scripts\build-installer.ps1
```

That builds **both** architectures. To build just one:

```powershell
.\scripts\build-installer.ps1 -Architecture arm64
```

The build script:

1. Prepares logo, icon, and sound assets
2. Publishes a self-contained `Clippy.exe` per architecture
3. Downloads and bundles the matching `ffmpeg.exe`
4. Creates `ClippySetup-<arch>.exe` with Inno Setup

Output: `windows/build/ClippySetup-x64.exe` and `windows/build/ClippySetup-arm64.exe`.

Requires [Inno Setup 6](https://jrsoftware.org/isdl.php). Without it, the script falls back to portable ZIPs at `windows/build/Clippy-win-<arch>.zip`.

### Build with GitHub Actions (recommended)

You do **not** need a local Windows build machine. GitHub builds both architectures on `windows-latest` and uploads them as artifacts.

1. Push this repo to GitHub
2. Open the repo → **Actions** → **Windows Installer** → **Run workflow**
3. Download **ClippySetup-x64** and **ClippySetup-arm64** from Artifacts

Pushes to `main`/`master` that change files under `windows/` also trigger the workflow.

### Optional code signing (SmartScreen / Defender)

Unsigned Windows apps often trigger **Microsoft Defender SmartScreen** (“Windows protected your PC”) until they build reputation. This is normal for new indie releases.

To reduce warnings for users:

1. **Sign both** `Clippy.exe` and `ClippySetup-<arch>.exe` with an Authenticode certificate.
2. **EV code signing** gives immediate SmartScreen reputation in most cases.
3. Set GitHub Actions secrets for automated signing:
   - `WINDOWS_SIGN_CERT_BASE64` — PFX file, base64-encoded
   - `WINDOWS_SIGN_CERT_PASSWORD` — PFX password

The build script signs automatically when those variables are present.

## First launch

1. Grant **microphone** access when prompted (Settings → Privacy → Microphone)
2. Enable **online speech recognition** (Settings → Privacy → Speech) for voice commands
3. Complete onboarding — pick mic, audio output, try “Clippy, clip that”
4. Default clip hotkey: **Ctrl+K** — change it in Settings → Keyboard Shortcut

## Features (parity with macOS)

| Feature | Windows |
|--------|---------|
| Rolling 60s buffer (5s segments) | One long-lived FFmpeg process, gdigrab + WASAPI |
| Clip 15s / 30s / 60s | Segmented picker |
| Global hotkey | Any modifier + key, recorded in Settings (default Ctrl+K) |
| Voice commands | Windows Speech Recognition |
| System + mic audio | WASAPI loopback + WASAPI capture, mixed in-process |
| Clip library | `%LocalAppData%\Clippy\Clips` |
| Onboarding | Multi-step flow matching macOS |
| Debug log | In-app diagnostics panel |
| Dark green UI | Shared Clippy theme |

## Data locations

```
%LocalAppData%\Clippy\
  settings.json
  clips.json
  Buffer\          # rolling segments (scratch; removed on uninstall)
  Clips\           # saved clips (kept on uninstall)
  Thumbnails\
```

## Capture stack

A single long-running FFmpeg process captures the desktop with `gdigrab` and writes the
buffer as 5-second **MPEG-TS** segments.

Two details are load-bearing:

- **One process, not one per segment.** Restarting FFmpeg per segment lost roughly a second
  of footage to process startup each time, so the buffer had a gap every five seconds.
- **MPEG-TS, not MP4.** TS has no trailing index, so the segment FFmpeg is still writing is
  already playable. That is what lets a clip include the instant the hotkey was pressed
  rather than ending up to five seconds earlier.

Audio is captured with NAudio (WASAPI loopback for system audio, WASAPI capture for the
mic), mixed in-process, and fed to FFmpeg through a pipe as 48 kHz stereo PCM. Doing it here
rather than through FFmpeg's own `wasapi`/`dshow` demuxers means any FFmpeg build works,
devices are chosen by endpoint ID instead of a display name that has to survive command-line
escaping, and — because the stream is paced by a clock — silence still advances it. WASAPI
loopback emits no packets at all while nothing is playing, so without generated silence the
audio would drift out of sync by however long the machine was quiet.

The desktop is grabbed at native resolution and scaled afterwards. `gdigrab`'s `-video_size`
crops the grab region rather than scaling it, so setting it directly would record the
top-left corner of a high-resolution monitor instead of the whole screen.

## Known differences from macOS

- Default hotkey is **Ctrl+K** instead of ⌘K
- Capture uses FFmpeg + gdigrab rather than ScreenCaptureKit / AVFoundation
- Voice commands need Windows online speech recognition enabled

## License

MIT — same as the main Clippy project.
