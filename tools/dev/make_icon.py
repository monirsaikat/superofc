"""Generates the placeholder app icon (assets/app.ico and the Desktop copy).

Replace with the real brand icon once the product name is final (docs/10, Q18).
Usage: python -I tools/dev/make_icon.py
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
SIZES = [16, 24, 32, 48, 64, 128, 256]


def draw(size: int) -> Image.Image:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    radius = max(2, size // 5)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=(0, 103, 192, 255))
    try:
        font = ImageFont.truetype("segoeuib.ttf", int(size * 0.62))
    except OSError:
        font = ImageFont.load_default()
    text = "S"
    box = d.textbbox((0, 0), text, font=font)
    w, h = box[2] - box[0], box[3] - box[1]
    d.text(((size - w) / 2 - box[0], (size - h) / 2 - box[1]), text, font=font, fill=(255, 255, 255, 255))
    return img


def main() -> None:
    big = draw(256)
    targets = [ROOT / "assets" / "app.ico", ROOT / "src" / "SuperOfc.Desktop" / "Assets" / "app.ico"]
    for target in targets:
        target.parent.mkdir(parents=True, exist_ok=True)
        big.save(target, sizes=[(s, s) for s in SIZES])
    draw(256).save(ROOT / "src" / "SuperOfc.Desktop" / "Assets" / "app.png")
    print("icons written")


if __name__ == "__main__":
    main()
