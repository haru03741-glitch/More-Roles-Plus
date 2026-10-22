# 会議の名札に出す能力ボタンの下地 (Resources/Icons/meeting_tile.png) を描く。
# 本編の ✓ / ✗ のタイルと同じ作り: 濃い縁・白い細い内縁・塗り・一段暗い内側の面・下の影。色は無地 (上に能力の絵を載せる)
# 使い方: python tools/make-meeting-tile.py
from PIL import Image, ImageDraw

SIZE = 220
SS = 4  # 縁を滑らかにするために大きく描いて縮める
BASE = (122, 140, 162)


def shade(c, k):
    return tuple(max(0, min(255, round(v * k))) for v in c) + (255,)


def rect(d, inset, radius, color, dy=0):
    s = SS
    d.rounded_rectangle(
        [inset * s, (inset + dy) * s, (SIZE - inset) * s - 1, (SIZE - inset + dy) * s - 1],
        radius=radius * s, fill=color)


def main():
    im = Image.new("RGBA", (SIZE * SS, SIZE * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    rect(d, 8, 30, shade(BASE, 0.35), dy=6)   # 下の影
    rect(d, 8, 30, shade(BASE, 0.45))         # 濃い縁
    rect(d, 15, 24, (236, 243, 245, 255))     # 白い細い内縁
    rect(d, 20, 20, shade(BASE, 1.0))         # 塗り
    rect(d, 34, 14, shade(BASE, 0.9))         # 一段暗い内側の面
    im = im.resize((SIZE, SIZE), Image.LANCZOS)
    im.save("Resources/Icons/meeting_tile.png")


if __name__ == "__main__":
    main()
