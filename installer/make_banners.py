"""Draw the installer's two pictures in stardexs.com's colours (WiX: 493x58 banner, 493x312 welcome/finish).

Usage: python helper/installer/make_banners.py   (writes banner.bmp and dialog.bmp next to this file)
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).parent
LOGO = HERE.parents[1] / "site" / "StarDexs.Site" / "wwwroot" / "img" / "icon.png"
BACKGROUND, PANEL, ACCENT, TEXT = (10, 14, 20), (18, 26, 34), (63, 208, 212), (230, 238, 245)


def font(size: int, bold: bool = False):
    for name in (("segoeuib.ttf" if bold else "segoeui.ttf"), "arial.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def logo(size: int) -> Image.Image:
    return Image.open(LOGO).convert("RGBA").resize((size, size), Image.LANCZOS)


def banner() -> None:
    # The strip across the top of the inner pages. WiX writes the page title in black on the left,
    # so the left two-thirds stay light and the brand sits on the dark right end.
    image = Image.new("RGB", (493, 58), (246, 248, 250))
    draw = ImageDraw.Draw(image)
    draw.rectangle([340, 0, 493, 58], fill=BACKGROUND)
    draw.line([340, 0, 340, 58], fill=ACCENT, width=2)
    image.paste(logo(34), (350, 12), logo(34))
    draw.text((390, 18), "StarDexs", font=font(17, bold=True), fill=TEXT)
    image.save(HERE / "banner.bmp")


def dialog() -> None:
    # The welcome and finish pages: WiX writes their text on the right, from about x=180.
    image = Image.new("RGB", (493, 312), (246, 248, 250))
    draw = ImageDraw.Draw(image)
    draw.rectangle([0, 0, 164, 312], fill=BACKGROUND)
    draw.line([164, 0, 164, 312], fill=ACCENT, width=2)
    image.paste(logo(96), (34, 70), logo(96))
    draw.text((82, 182), "StarDexs", font=font(20, bold=True), fill=TEXT, anchor="mm")
    draw.text((82, 206), "Companion", font=font(13), fill=ACCENT, anchor="mm")
    image.save(HERE / "dialog.bmp")


if __name__ == "__main__":
    banner()
    dialog()
    print("wrote banner.bmp and dialog.bmp")
