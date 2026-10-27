"""Draw StarDad's mark (a "42" in a ring) and everything made from it, in the Guide's colours.

Writes:
  helper/StarDexs.Sync/stardad.ico           the app's icon (16 to 256 px)
  helper/installer/banner.bmp, dialog.bmp    the installer's pictures (WiX: 493x58 and 493x312)
  site/.../wwwroot/favicon.png, img/icon.png the website's icons (64 and 256 px)
  site/.../wwwroot/img/logo-120.png          the logo Google's sign-in branding asks for

Usage: python helper/installer/make_art.py
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).parent
APP = HERE.parent / "StarDexs.Sync"
SITE = HERE.parents[1] / "site" / "StarDexs.Site" / "wwwroot"
NIGHT, PANEL, TOWEL, TEXT, SOFT = (21, 18, 31), (30, 25, 48), (246, 201, 69), (239, 233, 220), (179, 168, 214)
SCALE = 4  # drawn large, then scaled down, for smooth edges


def font(size: int, weight: str = "bold"):
    names = {"black": ("seguibl.ttf", "segoeuib.ttf"), "bold": ("segoeuib.ttf", "arialbd.ttf"), "regular": ("segoeui.ttf", "arial.ttf")}[weight]
    for name in names:
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def mark(size: int, background: bool = True) -> Image.Image:
    """The mark: a towel-yellow ring with 42 inside, on a rounded night square (or transparent)."""
    big = size * SCALE
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    if background:
        draw.rounded_rectangle([0, 0, big - 1, big - 1], radius=big * 0.22, fill=NIGHT + (255,))
    ring = big * (0.14 if background else 0.06)
    width = max(SCALE * 2, round(big * 0.075))
    draw.ellipse([ring, ring, big - ring, big - ring], outline=TOWEL + (255,), width=width)
    draw.text((big / 2, big / 2 + big * 0.02), "42", font=font(round(big * 0.36), "black"), fill=TOWEL + (255,), anchor="mm")
    return image.resize((size, size), Image.LANCZOS)


def icon() -> None:
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    mark(256).save(APP / "stardad.ico", sizes=[(s, s) for s in sizes])


def site() -> None:
    mark(64).save(SITE / "favicon.png")
    mark(256).save(SITE / "img" / "icon.png")
    mark(120).save(SITE / "img" / "logo-120.png")


def banner() -> None:
    # The strip across the top of the inner pages. WiX writes the page title in black on the left,
    # so the left two-thirds stay light and the brand sits on the dark right end.
    image = Image.new("RGB", (493, 58), (246, 244, 238))
    draw = ImageDraw.Draw(image)
    draw.rectangle([340, 0, 493, 58], fill=NIGHT)
    draw.line([340, 0, 340, 58], fill=TOWEL, width=2)
    logo = mark(34, background=False)
    image.paste(logo, (352, 12), logo)
    draw.text((394, 18), "StarDad", font=font(17), fill=TEXT)
    image.save(HERE / "banner.bmp")


def dialog() -> None:
    # The welcome and finish pages: WiX writes their text on the right, from about x=180.
    image = Image.new("RGB", (493, 312), (246, 244, 238))
    draw = ImageDraw.Draw(image)
    draw.rectangle([0, 0, 164, 312], fill=NIGHT)
    draw.line([164, 0, 164, 312], fill=TOWEL, width=2)
    logo = mark(96, background=False)
    image.paste(logo, (34, 70), logo)
    draw.text((82, 184), "StarDad", font=font(21), fill=TEXT, anchor="mm")
    draw.text((82, 208), "Companion", font=font(13, "regular"), fill=TOWEL, anchor="mm")
    image.save(HERE / "dialog.bmp")


if __name__ == "__main__":
    icon()
    site()
    banner()
    dialog()
    print("wrote stardad.ico, the installer pictures, and the website's icons")
