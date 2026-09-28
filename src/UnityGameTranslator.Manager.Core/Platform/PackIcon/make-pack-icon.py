"""Renders the .ugtpack file icon from its two SVG sources.

    python make-pack-icon.py [path-to-inkscape]

Writes, next to this script:
  ugtpack.ico          16, 20, 24, 32, 40, 48, 64, 256 px — what Windows shows for a pack
  ugtpack-<N>.png      16 … 256 px — the hicolor theme sizes the Linux desktop reads

Small sizes (up to 32 px) come from ugtpack-small.svg, which drops the band and its text:
at that size they are a smudge. Needs Inkscape (rendering) and Pillow (the .ico container).
The outputs are committed; run this again only after changing an SVG.
"""
import io
import subprocess
import sys
from pathlib import Path

from PIL import Image

HERE = Path(__file__).resolve().parent
INKSCAPE = sys.argv[1] if len(sys.argv) > 1 else r"C:\Program Files\Inkscape\bin\inkscape.exe"
SMALL_UP_TO = 32
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
PNG_SIZES = [16, 22, 24, 32, 48, 64, 128, 256]


def render(size: int) -> Image.Image:
    source = HERE / ("ugtpack-small.svg" if size <= SMALL_UP_TO else "ugtpack.svg")
    png = subprocess.run(
        [INKSCAPE, str(source), "--export-type=png", "--export-filename=-",
         f"--export-width={size}", f"--export-height={size}"],
        check=True, capture_output=True).stdout
    return Image.open(io.BytesIO(png)).convert("RGBA")


def main() -> None:
    frames = {size: render(size) for size in sorted(set(ICO_SIZES + PNG_SIZES))}

    for size in PNG_SIZES:
        frames[size].save(HERE / f"ugtpack-{size}.png")

    # Pillow writes one frame per size, each rendered from the right source rather than resized.
    largest = frames[max(ICO_SIZES)]
    largest.save(HERE / "ugtpack.ico", format="ICO",
                 sizes=[(s, s) for s in ICO_SIZES],
                 append_images=[frames[s] for s in ICO_SIZES if s != max(ICO_SIZES)])
    print("written:", ", ".join(f"ugtpack-{s}.png" for s in PNG_SIZES), "and ugtpack.ico")


if __name__ == "__main__":
    main()
