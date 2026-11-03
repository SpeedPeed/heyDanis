"""Erzeugt das heyDanis-Icon (Sprechblase mit Zeiger auf orangem Grund).

Aufruf:  python scripts/make-icon.py
Ergebnis: src/HeyDanis.App/Assets/heydanis.ico und docs/icon.png
Benötigt: Pillow (pip install pillow)
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
SIZE = 1024
ORANGE_TOP = (255, 138, 31)
ORANGE_BOTTOM = (255, 84, 54)
WHITE = (255, 255, 255, 255)


def vertical_gradient(size, top, bottom):
    gradient = Image.new("RGBA", (size, size))
    draw = ImageDraw.Draw(gradient)
    for y in range(size):
        t = y / (size - 1)
        color = tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
        draw.line([(0, y), (size, y)], fill=color)
    return gradient


def build_icon_image():
    # Hintergrund: abgerundetes Quadrat mit Orange-Verlauf
    mask = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, SIZE - 1, SIZE - 1], radius=230, fill=255)
    icon = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    icon.paste(vertical_gradient(SIZE, ORANGE_TOP, ORANGE_BOTTOM), (0, 0), mask)

    draw = ImageDraw.Draw(icon)
    # Weiße Sprechblase mit Schwänzchen unten links
    draw.rounded_rectangle([170, 200, 854, 700], radius=170, fill=WHITE)
    draw.polygon([(300, 640), (250, 860), (480, 680)], fill=WHITE)

    # Oranger Zeiger (Buddy-Cursor) in der Blase
    cursor = [(400, 300), (400, 610), (480, 530), (545, 660), (600, 635), (535, 505), (640, 505)]
    draw.polygon(cursor, fill=ORANGE_BOTTOM + (255,))
    return icon


def main():
    image = build_icon_image()
    (ROOT / "docs").mkdir(exist_ok=True)
    image.resize((256, 256), Image.LANCZOS).save(ROOT / "docs" / "icon.png")
    sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    image.save(ROOT / "src" / "HeyDanis.App" / "Assets" / "heydanis.ico", sizes=sizes)
    print("Icon geschrieben.")


if __name__ == "__main__":
    main()
