# 能力ボタンの絵 (ハンマー・爆弾) と破壊音のマーク (noise_*) を描いて Resources/Icons/*.png に書き出す。
# 本編のボタンの絵に合わせた描き方: 外側に白い縁 → 黒い太線 → ベタ塗り + 明るい帯と一段の影。
# 220 px 四方 (ゲーム内では 172 px = 1 単位で、本編の能力ボタンの絵の枠と同じ 1.28 単位になる)。4 倍で描いて縮める。
#   python tools/make-button-icons.py
import math
import os
from PIL import Image, ImageChops, ImageDraw, ImageFilter

SIZE = 220
SS = 4                      # 描く倍率 (縮めて縁をなめらかにする)
N = SIZE * SS
BLACK_LINE = 5              # 部品ごとの黒い線 (出来上がりの px)
WHITE_EDGE = 6              # 全体の外側の白い縁

OUT = os.path.join(os.path.dirname(__file__), "..", "Resources", "Icons")


def blank():
    return Image.new("L", (N, N), 0)


def grow(mask, px):
    # 太らせる量が大きい時は窓を何回かに分ける (MaxFilter の窓は奇数で上限がある)
    left = px * SS
    out = mask
    while left > 0:
        step = min(left, 30)
        out = out.filter(ImageFilter.MaxFilter(step * 2 + 1))
        left -= step
    return out


def poly(points, rot=0.0, center=(110, 110)):
    m = blank()
    cx, cy = center
    c, s = math.cos(rot), math.sin(rot)
    pts = []
    for x, y in points:
        dx, dy = x - cx, y - cy
        pts.append(((cx + dx * c - dy * s) * SS, (cy + dx * s + dy * c) * SS))
    ImageDraw.Draw(m).polygon(pts, fill=255)
    return m


def ellipse(x0, y0, x1, y1):
    m = blank()
    ImageDraw.Draw(m).ellipse([x0 * SS, y0 * SS, x1 * SS, y1 * SS], fill=255)
    return m


def star(cx, cy, r_out, r_in, n, phase=0.0, jitter=()):
    pts = []
    for i in range(n * 2):
        a = phase + math.pi * i / n
        r = r_out if i % 2 == 0 else r_in
        if jitter:
            r *= jitter[i % len(jitter)]
        pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    return poly(pts)


class Icon:
    def __init__(self):
        self.img = Image.new("RGBA", (N, N), (0, 0, 0, 0))
        self.union = blank()

    def part(self, mask, color, bands=()):
        # 黒い線 (部品を太らせた形) → 塗り → 帯 (塗りの中だけ)
        line = grow(mask, BLACK_LINE)
        self.union = ImageChops.lighter(self.union, line)
        self.img.paste((20, 20, 24, 255), (0, 0), line)
        self.img.paste(color, (0, 0), mask)
        for band, band_color in bands:
            self.img.paste(band_color, (0, 0), ImageChops.multiply(band, mask))

    def save(self, name):
        edge = grow(self.union, WHITE_EDGE)
        out = Image.new("RGBA", (N, N), (0, 0, 0, 0))
        out.paste((255, 255, 255, 255), (0, 0), edge)
        out.alpha_composite(self.img)
        out = out.resize((SIZE, SIZE), Image.LANCZOS)
        os.makedirs(OUT, exist_ok=True)
        out.save(os.path.join(OUT, name + ".png"))


def hammer():
    ic = Icon()
    rot = math.radians(-40)
    scale = 0.85       # 衝撃と柄の端が絵の内側に収まる大きさ
    shift = (-4, 18)   # 縮めた後の位置 (上の衝撃が切れないよう少し下げる)

    def at(points):
        return [(110 + (x - 110) * scale + shift[0], 110 + (y - 110) * scale + shift[1]) for x, y in points]

    def face_point():
        # 打つ面の真ん中が回った後に来る所 (衝撃をそこに置く)
        (px, py), = at([(182, 77)])
        dx, dy = px - 110, py - 110
        c, s = math.cos(rot), math.sin(rot)
        return 110 + dx * c - dy * s, 110 + dx * s + dy * c

    fx, fy = face_point()
    # 背景: 打つ面の先の衝撃 (橙のギザギザ)
    ic.part(star(fx + 12, fy - 2, 44, 27, 9, 0.2, (1.0, 1.0, 0.85, 1.0, 1.1, 1.0, 0.9, 1.0)), (242, 107, 29, 255))
    # 柄 (茶色・握りに赤い巻き)
    ic.part(poly(at([(98, 96), (122, 96), (126, 196), (94, 196)]), rot), (122, 82, 48, 255), [
        (poly(at([(110, 96), (122, 96), (126, 196), (110, 196)]), rot), (84, 56, 32, 255)),
    ])
    for y in (146, 166):
        ic.part(poly(at([(92, y), (128, y), (128, y + 11), (92, y + 11)]), rot), (196, 32, 22, 255))
    # 頭 (金属): 右が打つ面で広い・左は細くなる
    head = poly(at([(44, 64), (56, 58), (156, 48), (182, 54), (182, 100), (156, 106), (56, 96), (44, 90)]), rot)
    ic.part(head, (196, 203, 211, 255), [
        (poly(at([(30, 40), (190, 40), (190, 64), (30, 70)]), rot), (240, 244, 247, 255)),
        (poly(at([(30, 86), (190, 90), (190, 120), (30, 120)]), rot), (132, 142, 153, 255)),
    ])
    # 打つ面の縁 (濃い帯)
    ic.part(poly(at([(166, 51), (182, 54), (182, 100), (166, 103)]), rot), (150, 160, 170, 255))
    ic.save("hammer")


def bomb():
    ic = Icon()
    # 背景: 導火線の火花の先の衝撃
    ic.part(star(160, 58, 46, 26, 8, 0.4, (1.0, 1.0, 1.1, 1.0, 0.85, 1.0)), (242, 107, 29, 255))
    # 本体
    body = ellipse(36, 70, 160, 194)
    ic.part(body, (62, 67, 76, 255), [
        (ellipse(50, 76, 140, 150), (98, 106, 118, 255)),
        (ellipse(56, 96, 196, 220), (40, 44, 52, 255)),
    ])
    # 光り (左上の小さな楕円)
    ic.part(ellipse(62, 92, 88, 112), (236, 240, 244, 255))
    # 口金
    cap = poly([(112, 62), (138, 82), (126, 98), (98, 78)])
    ic.part(cap, (160, 168, 178, 255), [(poly([(112, 62), (138, 82), (132, 90), (104, 70)]), (214, 220, 226, 255))])
    # 導火線
    fuse = blank()
    d = ImageDraw.Draw(fuse)
    pts = [(124, 72), (134, 58), (146, 54), (156, 58)]
    d.line([(x * SS, y * SS) for x, y in pts], fill=255, width=8 * SS, joint="curve")
    ic.part(fuse, (150, 104, 60, 255))
    # 火花
    ic.part(star(160, 56, 22, 9, 6, 0.1), (255, 212, 59, 255), [(star(160, 56, 10, 5, 6, 0.6), (255, 138, 28, 255))])
    ic.save("bomb")


# 破壊音のマーク: 種類ごとの絵 (本編のノイズメーカーの矢印の真ん中に置く。向きは矢印が示すので絵は回さない)
def noise_blast():
    ic = Icon()
    ic.part(star(110, 110, 86, 49, 11, 0.15, (1.0, 0.86, 1.06, 0.92, 1.0, 1.1, 0.88, 1.0, 0.95, 1.08, 0.9)), (242, 107, 29, 255))
    ic.part(star(110, 110, 54, 30, 9, 0.5, (1.0, 0.9, 1.1, 1.0, 0.92)), (255, 212, 59, 255))
    ic.part(ellipse(92, 92, 128, 128), (255, 250, 228, 255))
    ic.save("noise_blast")


def impact(ic, r_out, color, band):
    # 叩いた衝撃: 長短の尖りが交互のギザギザ (金属を叩いた時の白っぽい火花)
    burst = star(110, 110, r_out, r_out * 0.36, 8, 0.39, (1.0, 0.62, 0.95, 0.66, 1.05, 0.6, 0.9, 0.64))
    ic.part(burst, color, [(star(110, 132, r_out, r_out * 0.36, 8, 0.39, (1.0, 0.62, 0.95, 0.66, 1.05, 0.6, 0.9, 0.64)), band)])


def noise_hit():
    ic = Icon()
    impact(ic, 84, (232, 236, 240, 255), (176, 186, 198, 255))
    ic.save("noise_hit")


def noise_break():
    ic = Icon()
    # 崩れる: 衝撃の下に壁の塊が 3 つ落ちる
    for pts in ([(40, 150), (78, 140), (86, 178), (48, 190)],
                [(96, 158), (132, 150), (140, 194), (100, 198)],
                [(146, 140), (182, 148), (176, 182), (142, 178)]):
        ic.part(poly(pts), (150, 132, 112, 255), [(poly([(x, y + 14) for x, y in pts]), (108, 92, 78, 255))])
    impact(ic, 70, (255, 240, 196, 255), (214, 186, 132, 255))
    ic.save("noise_break")


if __name__ == "__main__":
    hammer()
    bomb()
    noise_blast()
    noise_hit()
    noise_break()
    print("wrote", os.path.abspath(OUT))
