from PIL import Image, ImageDraw, ImageFont

BASE = 256
SIZES = [16, 24, 32, 48, 64, 128, 256]

img = Image.new("RGBA", (BASE, BASE), (0, 0, 0, 0))

# 背景：竖向渐变 + 圆角
grad = Image.new("RGBA", (BASE, BASE))
gd = ImageDraw.Draw(grad)
for y in range(BASE):
    t = y / (BASE - 1)
    r = int(31 + (9 - 31) * t)
    g = int(111 + (64 - 111) * t)
    b = int(235 + (150 - 235) * t)
    gd.line([(0, y), (BASE, y)], fill=(r, g, b, 255))
mask = Image.new("L", (BASE, BASE), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, BASE - 1, BASE - 1], radius=56, fill=255)
img.paste(grad, (0, 0), mask)

draw = ImageDraw.Draw(img)

# 字形：白色 IP
font = None
for path in (r"C:\Windows\Fonts\arialbd.ttf", r"C:\Windows\Fonts\segoeuib.ttf", r"C:\Windows\Fonts\arial.ttf"):
    try:
        font = ImageFont.truetype(path, 132)
        break
    except Exception:
        continue
if font is None:
    font = ImageFont.load_default()

text = "IP"
bbox = draw.textbbox((0, 0), text, font=font)
w = bbox[2] - bbox[0]
h = bbox[3] - bbox[1]
draw.text(((BASE - w) / 2 - bbox[0], (BASE - h) / 2 - bbox[1] - 14), text, font=font, fill=(255, 255, 255, 255))

# 底部一条“网线”，让图标在 16px 下也有辨识度
draw.rounded_rectangle([62, 188, 194, 204], radius=8, fill=(255, 255, 255, 235))
draw.rounded_rectangle([62, 216, 128, 228], radius=6, fill=(255, 255, 255, 150))

img.save(r"D:\ip\LanAutoConfig\app.ico", format="ICO", sizes=[(s, s) for s in SIZES])
img.save(r"D:\ip\LanAutoConfig\app-preview.png")
print("ok", img.size)
