# Based on https://github.com/waffle-ful/Aeterna-End-K-not (GPL-3.0) の手の絵の描き方 (太さの変わる曲がった指・丸い掌・縁 → 本体 → 影の帯)
# エモートの手の絵を描いて Resources/Icons/emote_*.png に書き出す。
# 本編の手と同じ様式: 3 本指 + 親指・腕は無く手だけ。色はプレイヤー色のひな形 (本体 = 赤・影 = 青・縁 = 紺 (0,0,44))。
# 実行時に本人の色で塗られる。本編の画素は読まない (寸法と配色の約束だけ合わせる)。
#   縁の太さ = 手の長さの約 8%・指の塗りの幅 = 縁の約 1.3 倍・光は右上から (左下の縁に影の帯)。
#   掌と指はつなげて描き (付け根に線を入れない)、離れた指の間は縁の色が付け根まで細って届く。
#   重なった指の境目は指先の側で縁の 2/3 ほどの太さ、付け根へ向かって細って消える。
# 160 px 四方・ゲーム内では 1 単位 = 100 px・原点は絵の中心。手首の位置は EmoteArt.Wrists と合わせる。
#   python tools/make-emote-hands.py
import os

import numpy as np
from PIL import Image, ImageDraw, ImageFilter
from scipy import ndimage as ndi

S = 4                  # 4 倍で描いて縮める
W = H = 160
OUTLINE = 10.0         # 縁の太さ (仕上がり px)
SHADE = 8.0            # 影の帯の幅
SEAM = 6.5             # 重なった指の境目の太さ (指先の側。縁より細い)
LIGHT = (0.42, -1.0)   # 光の来る向き (右上)
BODY = (255, 0, 0)
BACK = (0, 0, 255)
EDGE = (0, 0, 44)
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Resources", "Icons")


def bez(*p, n=40):
    """2 次または 3 次のベジェ曲線の点列"""
    out = []
    for i in range(n):
        t = i / (n - 1)
        if len(p) == 3:
            x = (1 - t) ** 2 * p[0][0] + 2 * (1 - t) * t * p[1][0] + t * t * p[2][0]
            y = (1 - t) ** 2 * p[0][1] + 2 * (1 - t) * t * p[1][1] + t * t * p[2][1]
        else:
            x = (1 - t) ** 3 * p[0][0] + 3 * (1 - t) ** 2 * t * p[1][0] + 3 * (1 - t) * t * t * p[2][0] + t ** 3 * p[3][0]
            y = (1 - t) ** 3 * p[0][1] + 3 * (1 - t) ** 2 * t * p[1][1] + 3 * (1 - t) * t * t * p[2][1] + t ** 3 * p[3][1]
        out.append((x, y))
    return out


def stroke(pts, w0, w1):
    """太さが w0 → w1 と変わる丸い筆の線。座標は仕上がり px"""
    im = Image.new("L", (W * S, H * S), 0)
    d = ImageDraw.Draw(im)
    n = len(pts)
    for i, (x, y) in enumerate(pts):
        w = w0 + (w1 - w0) * i / max(1, n - 1)
        r = w * S / 2
        d.ellipse([x * S - r, y * S - r, x * S + r, y * S + r], fill=255)
    return np.array(im) > 127


def blob(poly, round_=4.0):
    """角を丸めた多角形 (掌)"""
    im = Image.new("L", (W * S, H * S), 0)
    ImageDraw.Draw(im).polygon([(x * S, y * S) for x, y in poly], fill=255)
    if round_ > 0:
        im = im.filter(ImageFilter.GaussianBlur(round_ * S / 2))
    return np.array(im) > 127


def dist_out(m):
    """m の外の各画素から m までの距離 (仕上がり px)"""
    return ndi.distance_transform_edt(~m) / S


def shift(m, dx, dy):
    out = np.zeros_like(m)
    dx, dy = int(round(dx * S)), int(round(dy * S))
    h, w = m.shape
    out[max(0, dy):h + min(0, dy), max(0, dx):w + min(0, dx)] = m[max(0, -dy):h + min(0, -dy), max(0, -dx):w + min(0, -dx)]
    return out


class Finger:
    """指 1 本: 芯の点列と太さ。芯の先 (t = 1) が指先"""

    def __init__(self, pts, w0, w1):
        self.pts = pts
        self.mask = stroke(pts, w0, w1)


def seam(front, back, start=0.35, full=0.7):
    """重なった指の境目: back の見えている側で front の縁に沿う線。front の芯の t が start 以下では消え、full 以上で SEAM の太さ"""
    d = dist_out(front.mask)
    cand = back.mask & ~front.mask & (d < SEAM)
    ys, xs = np.nonzero(cand)
    if len(ys) == 0:
        return cand
    axis = np.array(front.pts) * S
    # 各画素に一番近い芯の点の t
    px = np.stack([xs, ys], 1).astype(np.float32)
    best = np.full(len(px), 1e18, np.float32)
    bt = np.zeros(len(px), np.float32)
    n = len(axis)
    for i, (ax, ay) in enumerate(axis):
        dd = (px[:, 0] - ax) ** 2 + (px[:, 1] - ay) ** 2
        better = dd < best
        best[better] = dd[better]
        bt[better] = i / (n - 1)
    k = np.clip((bt - start) / max(1e-6, full - start), 0, 1)
    k = k * k * (3 - 2 * k)
    keep = d[ys, xs] < SEAM * k
    out = np.zeros_like(cand)
    out[ys[keep], xs[keep]] = True
    return out


def render(groups, light=LIGHT):
    """groups = 奥から順の塊。塊 = dict(parts=[部品], seams=[(手前の指, 奥の指)], seam_t=(消える t, 太さが揃う t))。
    塊ごとに: 縁 (膨らませた形) → 本体 → 影 → 指の境目。後の塊は前の塊の上に自分の縁ごと描く (親指が拳の前を横切る等)。
    影は塊の外形で付ける (掌と指は 1 つの形なので付け根に影の線は出ない)。重なった指の境目は、奥の指の見えている側が
    光と反対を向く所にだけ影を添える"""
    rgb = np.zeros((H * S, W * S, 3), np.uint8)
    a = np.zeros((H * S, W * S), bool)
    dx, dy = SHADE * light[0], SHADE * light[1]
    for g in groups:
        parts = [p.mask if isinstance(p, Finger) else p for p in g["parts"]]
        sil = np.zeros_like(a)
        for m in parts:
            sil |= m
        e = dist_out(sil) <= OUTLINE
        rgb[e] = EDGE
        a |= e
        rgb[sil] = BODY
        rgb[sil & ~shift(sil, dx, dy)] = BACK
        st = g.get("seam_t", (0.35, 0.7))
        for f, b in g.get("seams", []):
            line = seam(f, b, *st)
            vis = b.mask & ~f.mask
            near = dist_out(line) < SHADE * 0.8
            rgb[vis & near & ~shift(vis, dx, dy) & ~line] = BACK
            rgb[line] = EDGE
    img = Image.fromarray(np.dstack([rgb, (a * 255).astype(np.uint8)]), "RGBA")
    return img.resize((W, H), Image.LANCZOS)


def open_palm():
    # 手を振る: 掌をこちらへ向けて 3 本の指を軽く開く (中指が一番長い・指の長さ ≈ 掌の高さ)。親指は頭の側 (左) へ張り出し、
    # 下側の線は掌の下の線から切れ目なく続く。手首 = (80, 146)
    palm = blob([(54, 90), (70, 82), (92, 82), (108, 90), (115, 108), (111, 128), (97, 141), (76, 144), (60, 138), (52, 124)], 6)
    index = Finger(bez((63, 92), (58, 64), (49, 36)), 17, 12)
    middle = Finger(bez((81, 88), (82, 58), (84, 25)), 17, 12)
    ring = Finger(bez((99, 92), (105, 70), (113, 46)), 16, 11)
    thumb = Finger(bez((66, 128), (44, 124), (30, 96)), 20, 13)
    return render([dict(parts=[palm, index, middle, ring, thumb])])


def fist_up():
    # 跳ねる: 握りこぶしを突き上げる。掌の側から見て、曲げた 3 本の指 (第 2 関節から先) が横に並び (上の縁に節の山)、親指が前を横切る。手首 = (80, 146)
    back = blob([(50, 98), (60, 88), (104, 88), (112, 98), (114, 114), (106, 132), (90, 142), (68, 142), (54, 132), (48, 114)], 6)
    f1 = Finger(bez((61, 110), (59, 92), (61, 78)), 23, 22)
    f2 = Finger(bez((82, 108), (82, 90), (82, 72)), 24, 23)
    f3 = Finger(bez((102, 110), (103, 94), (101, 80)), 22, 21)
    thumb = Finger(bez((50, 122), (60, 108), (88, 100)), 20, 15)
    return render([
        dict(parts=[back, f3, f2, f1], seams=[(f1, f2), (f2, f3)], seam_t=(0.0, 0.4)),
        dict(parts=[thumb]),
    ])


def side_flat():
    # 拍手: 手を横から見た形 (指をそろえたへら形)。指は重なって並び (奥の指ほど右へずれて短い)、境目は指先から半分ほどで消える。
    # 親指は掌の側 (右) で上を向き、掌とつながる (指との間は V 字の隙間)。手首 = (78, 146)
    palm = blob([(58, 96), (96, 94), (104, 112), (98, 134), (82, 145), (64, 139), (56, 118)], 6)
    ring = Finger(bez((92, 104), (95, 72), (97, 44)), 18, 14)
    middle = Finger(bez((80, 104), (81, 66), (83, 28)), 20, 16)
    index = Finger(bez((68, 104), (66, 70), (66, 36)), 20, 16)
    thumb = Finger(bez((94, 128), (114, 118), (117, 90)), 19, 14)
    return render([dict(parts=[palm, thumb, ring, middle, index], seams=[(index, middle), (middle, ring)], seam_t=(0.4, 0.8))])


# 手招きの掌。起こしたコマは指が左右に並ぶので、薬指の付け根が載るように右へ広げる
BECKON_PALM = [(30, 100), (40, 90), (62, 88), (84, 88), (96, 96), (95, 110), (82, 120), (56, 122), (36, 114)]
BECKON_PALM_WIDE = [(30, 100), (40, 90), (62, 88), (90, 88), (106, 94), (108, 108), (94, 120), (60, 122), (36, 114)]

# 手招きの 3 コマの指 (人差し指・中指・薬指の芯 = 付け根 → 曲がり → 指先のベジェの制御点)・親指・掌
BECKON = [
    # 0: 前へ伸ばす。指は上下に並び、先へ向かって開く
    ([((82, 88), (102, 80), (124, 72)), ((86, 99), (108, 98), (132, 96)), ((82, 110), (102, 116), (122, 120))],
     ((46, 98), (40, 78), (48, 60)), BECKON_PALM),
    # 1: 上へ起こし始める。指は斜めに並ぶ
    ([((72, 94), (78, 72), (86, 52)), ((88, 98), (100, 78), (114, 62)), ((102, 106), (118, 96), (132, 86))],
     ((44, 100), (36, 80), (40, 62)), BECKON_PALM),
    # 2: 上へ起こして指先を手前 (左) へ巻く (指の背が上で丸く折れて指先が少し下を向く)。指は左右に並ぶ
    ([((61, 98), (61, 70), (66, 48), (58, 56)), ((84, 96), (84, 64), (90, 40), (82, 48)), ((106, 100), (107, 76), (111, 56), (104, 63))],
     ((44, 100), (30, 82), (31, 64)), BECKON_PALM_WIDE),
]


def beckon(i):
    # 手招き: 掌を上へ向けて前 (右) へ出し、横から見た形。指を前へ伸ばす → 上へ起こす → 指先を手前へ巻く。
    # 3 本の指は付け根で隣と接して先へ向かって細り、間は縁の色で埋まって付け根で細って消える (親指の隣が人差し指)。
    # 親指は掌の手前の側から上へ立つ。手首 = (34, 100)
    lines, th, pal = BECKON[i]
    palm = blob(pal, 6)
    thumb = Finger(bez(*th), 18, 15)
    fingers = [Finger(bez(*ln), 20, 14) for ln in lines]
    return render([dict(parts=[palm, thumb] + fingers)])


def main():
    os.makedirs(OUT, exist_ok=True)
    poses = {"emote_open": open_palm(), "emote_fist": fist_up(), "emote_side": side_flat()}
    for i in range(len(BECKON)):
        poses[f"emote_beckon{i}"] = beckon(i)
    for name, img in poses.items():
        img.save(os.path.join(OUT, name + ".png"))
    print("ok", OUT)


if __name__ == "__main__":
    main()
