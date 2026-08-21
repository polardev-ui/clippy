# Clippy

A lightweight macOS clipping app that continuously buffers your screen in the background and saves the last **15s**, **30s**, or **1 minute** on demand.

## Features

- **Low RAM footprint** — rolling 5-second disk segments instead of keeping raw frames in memory
- **Global hotkey** — default `⌘K`, fully customizable in Settings
- **Voice commands** — say *"Clippy, do your thing"* or *"Clippy, clip that"*
- **Clip library** — view, rename, export, and delete saved clips inside the app
- **Clip sound** — satisfying audio feedback on every capture
- **Native SwiftUI UI** — black background, green accents, smooth animations

## Requirements

- macOS 14 Sonoma or later
- Screen Recording permission (required)
- Microphone + Speech Recognition permission (optional, for voice commands)

## Build from source

```bash
cd ~/Projects/Clippy
chmod +x scripts/build-dmg.sh
./scripts/build-dmg.sh
```

Outputs:

- `build/Release/Clippy.app`
- `build/Clippy.dmg`

## Install

1. Open `build/Clippy.dmg`
2. Drag **Clippy** into **Applications**
3. Launch Clippy and grant **Screen Recording** in System Settings → Privacy & Security
4. Optionally enable **Microphone** and **Speech Recognition** for voice triggers

## Usage

| Action | How |
|--------|-----|
| Clip now | `⌘K` (default) or click **Clip Now** |
| Voice clip | "Clippy, do your thing" / "Clippy, clip that" |
| Change duration | Settings → Clip Length |
| Change hotkey | Settings → Keyboard Shortcut → **Change** |
| Export clip | Library → **Export** or open clip → **Download** |

Clips are stored in `~/Library/Application Support/Clippy/Clips/`.

## Architecture

- **ScreenCaptureKit** — continuous capture with H.264 segment rolling buffer (max 60s)
- **AVFoundation** — segment merge + export on clip
- **Carbon hotkeys** — system-wide shortcut registration
- **Speech framework** — on-device voice trigger detection when available

## Windows

A native Windows port lives in [`windows/`](windows/README.md), with installers for **x64**
and **ARM64**. See that README for build and install instructions.

## License

MIT
