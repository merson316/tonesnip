#!/usr/bin/env python3
"""Generate ToneSnip's icon.ico, MSIX tile set and tray PNGs from assets/icon.svg,
plus the hand-fitted assets/icon-16.svg, icon-20.svg and icon-24.svg for the
sizes where the full icon's proportions get too thin to read.

Renders the source SVGs at every required pixel size with cairosvg
(vector-accurate at every size, including 16 px) into Pillow images, then
writes:

  - assets/icon.ico            (16, 20, 24, 32, 48, 64, 256 px)
  - assets/tray-16.png, assets/tray-20.png
  - assets/icon-256.png, assets/icon-32-preview.png   (README/doc previews)
  - assets/tiles/*.png          (the MSIX asset set the manifest references)

Run with: python3 tools/gen-assets.py [--check]

Requires Pillow and cairosvg (both installed for the WSL user Python this
project uses -- run with `python3`, not a virtualenv).

--check regenerates everything into a temporary directory and reports
whether it matches what's committed, without touching the working tree.
Intended for CI.
"""
from __future__ import annotations

import argparse
import filecmp
import io
import math
import sys
import tempfile
from pathlib import Path

import cairosvg
from PIL import Image

REPO_ROOT = Path(__file__).resolve().parent.parent
SVG_PATH = REPO_ROOT / "assets" / "icon.svg"

# .ico: a superset of what the tray reads via GetSystemMetrics(SM_CXSMICON) --
# 16 px at 100% DPI, 20 px at 125%, 24 px at 150%.
ICO_SIZES = [16, 20, 24, 32, 48, 64, 256]

# MSIX scale-NNN suffixes, as percentages of each logo's base size. Windows picks
# the nearest scale and resizes it, so 125 and 150 would only duplicate 200.
SCALES = [100, 200, 400]

# Base (unscaled) sizes for the square logos, keyed by manifest name. Windows 11
# has no live tiles, so the Wide310x150, Square71x71 and Square310x310 logos are
# never shown and are not generated.
TILE_BASES: dict[str, tuple[int, int]] = {
    "Square44x44Logo": (44, 44),
    "Square150x150Logo": (150, 150),
}
STORE_LOGO_BASE = (50, 50)

# Square44x44Logo also ships fixed target sizes (taskbar / jump list / etc.),
# each with a plain and an "unplated" (no background tile) variant.
TARGET_SIZES = [16, 24, 32, 48, 256]


def _round_half_up(x: float) -> int:
    return math.floor(x + 0.5)


def _scaled(base: tuple[int, int], percent: int) -> tuple[int, int]:
    w, h = base
    return _round_half_up(w * percent / 100), _round_half_up(h * percent / 100)


_SVG_TEXT = SVG_PATH.read_text(encoding="utf-8")
# The icon has no background plate, so the "unplated" MSIX variant renders
# the same SVG.
_SVG_UNPLATED = _SVG_TEXT

# Hand-fitted SVGs for the sizes where the full icon's proportions get too
# thin to read: exact pixel-grid geometry instead of a scaled-down render of
# icon.svg. Keyed by the square size they replace.
_SMALL_SVG_TEXT: dict[int, str] = {
    size: (REPO_ROOT / "assets" / f"icon-{size}.svg").read_text(encoding="utf-8")
    for size in (16, 20, 24)
}


def render_svg(width: int, height: int, *, unplated: bool = False) -> Image.Image:
    """Render the icon SVG directly at the target pixel size with cairosvg, so
    small sizes (16 px tray, MSIX targetsize-16) stay crisp instead of being
    resampled down from a large bitmap. Square renders at 16 / 20 / 24 px use
    the hand-fitted small SVG instead of the main icon (same for plated and
    unplated: neither has a plate to remove)."""
    if width == height and width in _SMALL_SVG_TEXT:
        svg = _SMALL_SVG_TEXT[width]
    else:
        svg = _SVG_UNPLATED if unplated else _SVG_TEXT
    png_bytes = cairosvg.svg2png(
        bytestring=svg.encode("utf-8"),
        output_width=width,
        output_height=height,
    )
    return Image.open(io.BytesIO(png_bytes)).convert("RGBA")


def render_inset(size: int, inset: int, *, unplated: bool = False) -> Image.Image:
    """Render a size x size icon inset by `inset` px on every edge (transparent
    border), for the tiny tray sizes whose rounded corners clip badly flush."""
    if inset <= 0:
        return render_svg(size, size, unplated=unplated)
    inner = size - 2 * inset
    inner_img = render_svg(inner, inner, unplated=unplated)
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    canvas.paste(inner_img, (inset, inset), inner_img)
    return canvas


def _save_png(img: Image.Image, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    img.save(path, format="PNG", optimize=True)


# Per-size transparent inset for the tray PNGs; none is needed at this icon's
# corner radius.
TRAY_INSET = {16: 0, 20: 0}


def build(out_root: Path) -> list[Path]:
    """Render every output under out_root/assets/... . Returns the list of
    paths written, relative to out_root."""
    assets = out_root / "assets"
    tiles = assets / "tiles"
    written: list[Path] = []

    def emit(img: Image.Image, rel_path: str) -> None:
        path = assets / rel_path
        _save_png(img, path)
        written.append(path.relative_to(out_root))

    # .ico
    ico_imgs = {s: render_svg(s, s) for s in ICO_SIZES}
    base = ico_imgs[256]
    others = [ico_imgs[s] for s in ICO_SIZES if s != 256]
    ico_path = assets / "icon.ico"
    ico_path.parent.mkdir(parents=True, exist_ok=True)
    base.save(
        ico_path,
        format="ICO",
        sizes=[(s, s) for s in ICO_SIZES],
        append_images=others,
    )
    written.append(ico_path.relative_to(out_root))

    # Tray notification-area icons.
    for s, inset in TRAY_INSET.items():
        emit(render_inset(s, inset), f"tray-{s}.png")

    # README / doc preview PNGs.
    emit(render_svg(256, 256), "icon-256.png")
    emit(render_svg(128, 128), "icon-32-preview.png")

    # MSIX tiles.
    for name, base_size in TILE_BASES.items():
        for pct in SCALES:
            w, h = _scaled(base_size, pct)
            emit(render_svg(w, h), f"tiles/{name}.scale-{pct}.png")

    # Square44x44Logo fixed target sizes, plain and unplated.
    for s in TARGET_SIZES:
        emit(render_svg(s, s), f"tiles/Square44x44Logo.targetsize-{s}.png")
        emit(
            render_svg(s, s, unplated=True),
            f"tiles/Square44x44Logo.targetsize-{s}_altform-unplated.png",
        )

    # StoreLogo.
    for pct in SCALES:
        w, h = _scaled(STORE_LOGO_BASE, pct)
        emit(render_svg(w, h), f"tiles/StoreLogo.scale-{pct}.png")

    return written


def check() -> int:
    with tempfile.TemporaryDirectory(prefix="tonesnip-gen-assets-") as tmp:
        tmp_root = Path(tmp)
        written = build(tmp_root)
        mismatches: list[str] = []
        for rel in written:
            committed = REPO_ROOT / rel
            generated = tmp_root / rel
            if not committed.exists():
                mismatches.append(f"missing: {rel}")
            elif not filecmp.cmp(committed, generated, shallow=False):
                mismatches.append(f"stale:   {rel}")
        # The csproj packages every PNG in assets/tiles, so one this script no
        # longer writes would still ship.
        expected = {REPO_ROOT / rel for rel in written}
        for extra in sorted((REPO_ROOT / "assets" / "tiles").glob("*.png")):
            if extra not in expected:
                mismatches.append(f"extra:   {extra.relative_to(REPO_ROOT)}")
        if mismatches:
            print(f"gen-assets --check: {len(mismatches)} file(s) out of date:")
            for m in sorted(mismatches):
                print(f"  {m}")
            print("Run `python3 tools/gen-assets.py` and commit the result.")
            return 1
        print(f"gen-assets --check: {len(written)} generated files match assets/.")
        return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--check",
        action="store_true",
        help="regenerate into a temp dir and report whether assets/ is up to date, without writing",
    )
    args = parser.parse_args(argv)

    if args.check:
        return check()

    written = build(REPO_ROOT)
    print(f"gen-assets: wrote {len(written)} files under {REPO_ROOT / 'assets'}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
