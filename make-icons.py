# Genererar appikonerna (körs av GitHub Actions – PNG-filer kan inte laddas upp som text).
from PIL import Image, ImageDraw
def icon(size, path):
    S = 512; img = Image.new("RGBA", (S, S), (0, 0, 0, 0)); d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, S - 1, S - 1], radius=110, fill=(60, 140, 231, 255))
    d.rounded_rectangle([0, 0, S - 1, S // 2], radius=110, fill=(74, 147, 234, 255)); d.rectangle([0, 110, S - 1, S // 2], fill=(74, 147, 234, 255))
    d.rounded_rectangle([96, 128, 416, 344], radius=70, fill=(255, 255, 255, 255))
    d.polygon([(150, 330), (130, 410), (230, 340)], fill=(255, 255, 255, 255))
    for cx in (176, 256, 336): d.ellipse([cx - 24, 212, cx + 24, 260], fill=(60, 140, 231, 255))
    img.resize((size, size), Image.LANCZOS).save(path)
icon(180, "icon-180.png"); icon(512, "icon-512.png")
print("ikoner klara")
