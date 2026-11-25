"""从用户给的图标原图做出所有图标：python tools/make_icon.py

art/icon-source.png（1024 px，2026-11-25 用户给的白云玻璃方块）：浅灰背景上一个玻璃方块。
裁出方块（圆角外透明），写出：
  LanBridge.ico      16-256 px，程序文件、快捷方式、窗口和托盘
  web/icon.png       256 px，电脑界面和手机网页左上角、Brand.Render 的来源
  web/touch.png      180 px，iPhone 主屏图标（方块里面的部分铺满，iPhone 自己切圆角）
  web/icon.svg       包一张 128 px 的 PNG（老页面和 manifest 还引用 icon.svg）
"""
import base64, io
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

root = Path(__file__).resolve().parent.parent
src = Image.open(root / "art" / "icon-source.png").convert("RGBA")

# 方块的玻璃边：x 161–867、y 160–866，圆角约 205
x0, y0, side, radius = 161, 160, 706, 205
tile = src.crop((x0, y0, x0 + side, y0 + side))
k = 4
mask = Image.new("L", (side * k, side * k), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, side * k - 1, side * k - 1], radius=radius * k, fill=255)
tile.putalpha(mask.resize((side, side), Image.LANCZOS))

def at(n):
    im = tile.resize((n, n), Image.LANCZOS)
    if n <= 48:
        im = im.filter(ImageFilter.UnsharpMask(radius=0.6, percent=70, threshold=0))
    return im

web = root / "web"
at(256).save(web / "icon.png", optimize=True)
inset = 40  # iPhone 主屏图标：取方块里面，避开透明圆角
src.crop((x0 + inset, y0 + inset, x0 + side - inset, y0 + side - inset)).convert("RGB").resize((180, 180), Image.LANCZOS).save(web / "touch.png", optimize=True)
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
at(256).save(root / "LanBridge.ico", sizes=[(n, n) for n in sizes], append_images=[at(n) for n in sizes[:-1]])
buf = io.BytesIO(); at(128).save(buf, "PNG", optimize=True)
b64 = base64.b64encode(buf.getvalue()).decode()
(web / "icon.svg").write_text(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256"><image width="256" height="256" href="data:image/png;base64,{b64}"/></svg>', encoding="utf-8")
print("icons written")
