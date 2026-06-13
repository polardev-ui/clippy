#!/usr/bin/env python3

from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parent.parent
OUT_DIR = Path(__file__).resolve().parent / "dmg"
LOGO = ROOT / "Clippy/Resources/Assets.xcassets/ClippyLogo.imageset/clippy-logo@2x.png"

WIDTH = 640
HEIGHT = 420

BG = (10, 10, 10)
SURFACE = (18, 18, 18)
ACCENT = (46, 217, 107)
ACCENT_DIM = (31, 140, 71)
TEXT = (245, 245, 245)
TEXT_MUTED = (150, 150, 150)


def lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * t


def load_font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    candidates = [
        "/System/Library/Fonts/SFNSRounded.ttf",
        "/System/Library/Fonts/SFNSText.ttf",
        "/System/Library/Fonts/Supplemental/Avenir Next.ttc",
        "/System/Library/Fonts/Supplemental/Avenir.ttc",
        "/Library/Fonts/Arial.ttf",
    ]
    for path in candidates:
        if Path(path).exists():
            try:
                return ImageFont.truetype(path, size=size, index=1 if bold and path.endswith(".ttc") else 0)
            except OSError:
                try:
                    return ImageFont.truetype(path, size=size)
                except OSError:
                    continue
    return ImageFont.load_default()


def draw_radial_glow(base: Image.Image, center: tuple[int, int], radius: int, color: tuple[int, int, int, int]) -> None:
    glow = Image.new("RGBA", base.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(glow)
    for step in range(radius, 0, -2):
        alpha = int(color[3] * (step / radius) ** 2)
        draw.ellipse(
            (center[0] - step, center[1] - step, center[0] + step, center[1] + step),
            fill=(color[0], color[1], color[2], alpha),
        )
    glow = glow.filter(ImageFilter.GaussianBlur(radius=18))
    base.alpha_composite(glow)


def draw_grid(base: Image.Image) -> None:
    overlay = Image.new("RGBA", base.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(overlay)
    spacing = 28
    for x in range(0, WIDTH, spacing):
        draw.line((x, 0, x, HEIGHT), fill=(255, 255, 255, 10), width=1)
    for y in range(0, HEIGHT, spacing):
        draw.line((0, y, WIDTH, y), fill=(255, 255, 255, 10), width=1)
    base.alpha_composite(overlay)


def draw_arrow(draw: ImageDraw.ImageDraw, start: tuple[int, int], end: tuple[int, int]) -> None:
    draw.line([start, end], fill=(*ACCENT, 210), width=3)

    angle = math.atan2(end[1] - start[1], end[0] - start[0])
    head_len = 12
    left = (
        end[0] - head_len * math.cos(angle - math.pi / 7),
        end[1] - head_len * math.sin(angle - math.pi / 7),
    )
    right = (
        end[0] - head_len * math.cos(angle + math.pi / 7),
        end[1] - head_len * math.sin(angle + math.pi / 7),
    )
    draw.polygon([end, left, right], fill=(*ACCENT, 220))


def main() -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    img = Image.new("RGBA", (WIDTH, HEIGHT), (*BG, 255))
    gradient = Image.new("RGBA", (WIDTH, HEIGHT))
    gdraw = ImageDraw.Draw(gradient)
    for y in range(HEIGHT):
        t = y / (HEIGHT - 1)
        r = int(lerp(BG[0], SURFACE[0], t))
        g = int(lerp(BG[1], SURFACE[1], t))
        b = int(lerp(BG[2], SURFACE[2], t))
        gdraw.line((0, y, WIDTH, y), fill=(r, g, b, 255))
    img = Image.alpha_composite(img, gradient)

    draw_radial_glow(img, (320, 95), 180, (*ACCENT, 55))
    draw_radial_glow(img, (170, 210), 120, (*ACCENT_DIM, 35))
    draw_radial_glow(img, (470, 210), 120, (*ACCENT_DIM, 35))
    draw_grid(img)

    draw = ImageDraw.Draw(img)

    draw.rounded_rectangle((24, 18, WIDTH - 24, 92), radius=16, fill=(22, 22, 22, 230), outline=(46, 217, 107, 45), width=1)

    if LOGO.exists():
        logo = Image.open(LOGO).convert("RGBA")
        logo.thumbnail((52, 52), Image.Resampling.LANCZOS)
        img.alpha_composite(logo, (38, 28))

    title_font = load_font(28, bold=True)
    subtitle_font = load_font(13)
    label_font = load_font(14, bold=True)
    hint_font = load_font(12)

    draw.text((98, 30), "Clippy", fill=TEXT, font=title_font)
    draw.text((98, 62), "Instant screen clips for macOS", fill=TEXT_MUTED, font=subtitle_font)

    for cx, cy in ((170, 190), (470, 190)):
        draw.ellipse((cx - 58, cy - 58, cx + 58, cy + 58), outline=(46, 217, 107, 35), width=2)
        draw.ellipse((cx - 48, cy - 48, cx + 48, cy + 48), outline=(255, 255, 255, 12), width=1)

    draw_arrow(draw, (248, 190), (392, 190))

    draw.text((188, 268), "Drag", fill=TEXT, font=label_font, anchor="mm")
    draw.text((320, 268), "Clippy", fill=ACCENT, font=label_font, anchor="mm")
    draw.text((452, 268), "Applications", fill=TEXT, font=label_font, anchor="mm")
    draw.text((320, 292), "Drop the app on Applications to install", fill=TEXT_MUTED, font=hint_font, anchor="mm")

    draw.line((24, HEIGHT - 34, WIDTH - 24, HEIGHT - 34), fill=(46, 217, 107, 40), width=1)
    draw.text((320, HEIGHT - 16), "Press ⌘K or say “Clippy, clip that”", fill=TEXT_MUTED, font=hint_font, anchor="mm")

    out = OUT_DIR / "background.png"
    img.convert("RGB").save(out, format="PNG", optimize=True)
    print(f"Wrote {out}")


if __name__ == "__main__":
    main()
