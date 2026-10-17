"""水を見せてよい床の画素の地図 (床マスク) を、ゲームの部屋の絵から作る。

使い方:
  1. ゲームでマップに入り、ブリッジで `floorexport` を打つ (Screens/floor_<船>.json ができる)
  2. python tools/make-floor-mask.py <floor_*.json ...> [--bundles <StandaloneWindows の aa フォルダ>] [--preview <dir>]

出力は Resources/FloorMasks/<船>.bin (リポジトリには入れない・ビルドで DLL に埋め込む)。
作り方:
  - 奥の部屋の絵 (人より奥に描く物) を、絵が実際に描かれる三角形の中だけ世界座標の 1 枚に重ねる
  - 黒い縁取り (壁の根元・家具の輪郭) で区切られたまとまりに分け、歩ける所 (家具の当たり判定の中を除く) が
    大半を占めるまとまりを床にする
  - 床の上の細い飾り線 (両側が床) は床に含める。歩ける所から離れすぎた画素は床にしない (縁取りの切れ目から壁へ漏れた分)
  - 壁の面 = 床の真上に途切れず続く床でない絵の画素 (FACE_MAX まで)。深い水はここを這い上がって見える
"""
import argparse
import glob
import json
import os
import re
import struct
import sys
import zlib

import numpy as np

try:
    import UnityPy
    from PIL import Image, ImageDraw
    from scipy import ndimage
except ImportError:
    sys.exit("pip install UnityPy pillow numpy scipy")

BACK_Z = 4.0          # これより奥 (z が大きい) の部屋の絵だけ重ねる (床に貼った飾りや手前の絵は床の判定に入れない)
SKY_Z = 20.0          # これより奥は空の絵 (Fungle の星空・夕焼け)。床の判定に入れない
FRONT_Z = 0.1         # これより手前の絵はどの床の水より手前 (ゲームの DamageMap.PlayerBandZ と同じ)
BACK_Z_SHIP = {"PolusShip": 0.5, "FungleShip": 0.5}  # 床の絵が z 1〜4 にある船 (ゲームの FloorMask.BackZ と同じ表)
ALWAYS_FLOOR = {"room_weapon", "room_broadcast"}  # z に関わらず床の層に入れる絵 (ゲームの FloorMask.AlwaysFloor と同じ表)
# クルーと同じ奥行きに置かれた歩ける床の絵 (ドロップシップの坂・床)。手前の絵でなく床の層に入れ、歩ける所の画素は床にする (ゲームの FloorCovers.Is と同じ表)
FLOOR_COVERS = {"PolusShip": {"ramp"}, "FungleShip": {"dropship_floorandramp", "rampwithsand2_nobox"}}
OUTLINE = 0.22        # 明るさがこれ未満の画素は縁取り
DRAWN = 0.25          # 不透明度がこれを超える画素は絵がある (ナビの床は半透明で 0.45 ほど)
FLOOR_SHARE = 0.5     # まとまりのうち歩ける所の割合がこれ以上なら床
PALETTE_SHARE = 0.96  # 部屋の歩ける所の画素のうち、これだけを占めるよく使われる色を床の色にする
PALETTE_TOP = 24
CHROMA_TOL = 0.035    # 床の色と色合い (明るさで割った色) がこれ以内
SHADE_MIN = 0.45      # 床の色に比べた明るさの範囲 (影の落ちた床も床)
SHADE_MAX = 1.15
LINE_CLOSE = 3        # 両側が床の細い線を埋める半径 (画素)
REACH = 0.2           # 歩ける所からこの距離 (世界単位) より離れた画素は床にしない
# 床が部屋の絵でなく地面の絵の船: 部屋の絵 (壁・家具) も手前の絵 (家具・木・岩) も無い地面は、歩ける所からこの距離まで
# つながっていれば床にする。当たり判定は家具や壁の絵より大きく取ってあり、REACH で切ると家具の手前に乾いた地面の帯が残る。
# 広げるのは手前の絵の形の手前まで (影の中では水の写しが手前の絵より前に出る)。歩ける所の近くの床は手前の絵があっても削らない (乗れる床板の絵がある)
GROUND_REACH_SHIP = {"FungleShip": 1.0}
# 背景の網目のうち、水より手前 (クルーより手前) に描かれる物。水はこの形の下に見せない。
# 影の中の水の写しは一番手前に出るので、ここで抜かないと影の中だけ水晶や茂みの上に水が乗る
MESH_COVERS_SHIP = {"FungleShip": {"BaseOverlays", "BaseOverlaysForegroud"}}
# 手前の網目は水晶の尖った上の方だけで、本体は地面と同じ網目に描かれている。手前の網目に触れる赤い画素のまとまりを本体として水を見せない
# (色だけで拾うとジャングルの赤い草木まで入るので、手前の網目から辿れる物だけ)
CRYSTAL_SEED_SHIP = {"FungleShip": {"BaseOverlaysForegroud"}}
CRYSTAL_HUE = (330.0, 15.0)   # 赤〜赤紫 (度・0 をまたぐ)
CRYSTAL_SAT = 0.35
CRYSTAL_VAL = 0.15
# 床の絵が壁の面と同じ色で続く広い床 (Mira の発射台): 床の色の判定・壁の面と前面の除外・隙間の幅の下限を外す範囲 (世界座標の矩形 x0, y0, x1, y1)
PHYS_RELAX_SHIP = {"MiraShip": [(-11.7, -0.3, 0.2, 8.8)]}
# 例外の範囲から抜く部屋の絵の家具 (手前の絵の一覧に無い物・Mira の発射台の燃料タンク)
PHYS_RELAX_CUT_SHIP = {"MiraShip": [(-9.4, 0.9, -6.85, 4.35)]}
# 水の届く升にしない範囲。小さな段差の縁 (エアシップの機関室の南の桃色の床の上り口) は通れない縁のままでないと段として上れない
PHYS_EXCLUDE_SHIP = {"Airship": [(-2.6, -2.05, 2.8, -1.74)]}
FURN_DEEP = 0.08      # 家具の当たり判定の縁からこれより内側は床にしない (当たり判定が絵より大きい所の縁の帯は床のまま)
SPECK = 60            # これより小さい床の島・床の中の穴 (画素数) は消す
EDGE_SIGMA = 1.6      # 縁をならすぼかしの幅 (画素)
LEARN_PASSES = 3      # 取りこぼした歩ける所の色を床の色に足して判定し直す回数
LEARN_INSET = 0.3     # 学ぶのは壁 (歩けない所) と家具の当たり判定からこれ以上離れた画素だけ (壁の根元の帯の色を床の色にしない)
REPORT_INSET = 0.1    # 取りこぼしの一覧に出すのは壁と家具の当たり判定からこれ以上離れた所
LEARN_MIN = 0.05      # 学ぶ取りこぼしの塊の最小の広さ (世界単位の 2 乗)
LEARN_SHARE = 0.9     # 取りこぼしの画素のうち、これだけを占める色を足す
DARK_SHADE = 0.7      # 学んだ色の明るさの下限 (色に比べて)。暗い床は縁取りの明るさの線 (OUTLINE) より暗いので、色ごとの比で縁取りと分ける
DARK_FLOOR = 0.07     # これより暗い画素は学んだ色でも床にしない (黒い縁取り)
MAGIC = b"MRPF"
VERSION = 4
PHYS_REACH = 6.0      # 当たり判定で閉じた所を水に開けるのは、歩ける所からこの距離 (世界単位) まで
PHYS_GATE = 0.3       # 歩ける所から床の続きへ水が通れる隙間の幅の下限 (世界単位)
WET_BRIDGE = 2        # 水の届く升と歩ける升の間でこれまでの隙間は埋める (SolidMap の升)
SEAL_LOCAL = 12       # 水だけの升のまとまりの周りこの升の中で、触れている歩ける所が歩いてつながらなければ壁を挟んでいる
# 壁を挟んだ水の升を外さない船。Fungle の採掘場は、床 (低い) と穴のまわりの高台 (高い) が水の計算では同じ段で、
# 高台の水が床へ入る道はドリルの穴の当たり判定の切り欠きの下の水の升だけ (外すと採掘場に水が入らない)
SEAL_SKIP_SHIP = {"FungleShip"}
PHYS_MIN_W = 0.1      # これより細い筋は水の通り道にしない (世界単位)
BARRIER_PX = 3        # 影の線を画素に引く太さ (線の継ぎ目から漏れないように)
FRONT_MAX = 1.0       # 床の縁から真下へこれまでの床でない絵は南の壁の前面 (水の通り道にしない)
FACE_MAX = 4.0        # 床の縁から真上へこれ (世界単位) までの絵を壁の面とする (水が這い上がる所)
# 一方向の影の多角形 (台地の上の面) が上の面の外 (岩の面・空) まで含む所で、上の面と外を分ける折れ線 (世界座標)。
# 空の絵と岩の面は床の判定の絵に無く、上の面の地面と同じ「絵の無い所」になるので線で分ける。
# Fungle の通信室の台地 = 建物の裏から旗の角 (砂の上の面と後ろの空の境・建物の当たり判定の中の隙間から裏の空へ回らない)、旗の角から採掘坑の上まで
# (砂の上の面と左の高い岩・採掘坑へ下りる崖の境・崖の縁の当たり判定に沿う)
TOP_SPLIT_SHIP = {"FungleShip": [[(20.587, 14.668), (18.329, 14.6), (16.464, 14.919), (16.536, 14.027), (16.483, 13.521), (17.11, 12.715), (17.023, 11.524), (17.139, 11.267)]]}
TOP_LINE_W = 0.16     # 上の面を分ける線を引く幅 (世界単位)。歩ける所の升 (1/16) の丸めのはみ出しより太く
TOP_REFINE = 0.5      # 絵の色で崖の縁を決め直す、多角形の輪郭の前後の幅 (世界単位)
TOP_LEARN_IN = 0.3    # 上の面の色を学ぶのは、上の面の歩ける所のうち輪郭からこれより奥の画素
TOP_LEARN_OUT = (0.15, 0.8)  # 崖の面の色を学ぶのは、輪郭の外のこの距離の帯 (歩ける所の近くは除く)
FACE_SCAN = 2.5       # 上の面の輪郭の外のこの距離 (世界単位) まで、崖の面の色の画素を下の地面の床にしない
TOP_LINE_CLOSE = 0.05 # 上の面の中の黒い縁取りの線を埋める半径 (世界単位)
TOP_HOLE = 0.01       # 上の面の中で埋める穴の広さの上限 (世界単位の 2 乗)
TOP_SMOOTH = 0.03     # 色で分けた後にならす半径 (世界単位)
TOP_FACE = 0.5        # 上の面の輪郭 (崖の縁) の外のこの幅 (世界単位) は崖の面として、下の地面の床を広げない
CLIFF_SCAN = 5.5      # 台地 (いちばん広い地面以外の歩ける所) からこの距離 (世界単位) まで、崖の面の色の画素を地面の床にしない
CLIFF_LEARN_OUT = (1.2, 3.0)  # 崖の面の色を学ぶのは、歩ける所からこの距離の帯の絵の無い地面
CLIFF_BLOCK = 4.0     # 色を学ぶ升の一辺 (世界単位)。学ぶ範囲は升の周りこの幅まで (地面の色も崖の色も場所で違う)
CLIFF_MIN = 200       # 升の周りで地面と崖の面の色がこれだけ (画素) 学べない所は分けない
# 一方向の影の多角形の無い台地: 歩ける所からこの距離 (世界単位) までの台地の地面と、その上に置いた物 (箱・双眼鏡) の下を水の升にする。
# 歩ける所は物の当たり判定で崖の縁より手前で終わるので、そのままでは物の裏が崖の縁になり、物から滝が出る。
# 崖の縁 (壁の線)・視界の影の線 (柵)・崖の面の色の画素・はしごの絵は越えない
PLATEAU_REACH_SHIP = {"FungleShip": 2.0}
PLATEAU_SKIP = {"ladder1", "ladder2"}
PLATEAU_GAP = 0.5     # 別のまとまり (歩ける所・足した地面) からこの距離 (世界単位) までは足さない
PLATEAU_POCKET = 0.5  # 台地の地面と歩ける所と線で囲まれて残った所を埋める広さの上限 (世界単位の 2 乗)
WATER_SUB = 4         # 水の計算の升 (0.25 単位) の一辺の、歩ける所の地図の升 (1/16 単位) の数

DEFAULT_BUNDLES = [
    r"C:/Program Files/Epic Games/AmongUs/Among Us_Data/StreamingAssets/aa/EGS/StandaloneWindows",
    r"C:/Program Files (x86)/Steam/steamapps/common/Among Us/Among Us_Data/StreamingAssets/aa/Steam/StandaloneWindows",
]


def load_textures(folder, rooms):
    need = {(r["texture"], r["texSize"][0], r["texSize"][1]) for r in rooms if "texture" in r}
    out = {}
    for b in sorted(glob.glob(os.path.join(folder, "*.bundle"))):
        env = UnityPy.load(b)
        for o in env.objects:
            if o.type.name != "Texture2D":
                continue
            d = o.read()
            key = (d.m_Name, d.m_Width, d.m_Height)
            if key in need and key not in out:
                out[key] = np.asarray(d.image.convert("RGBA"))[::-1].astype(np.float32) / 255.0  # 左下原点
        if len(out) == len(need):
            break
    return out


def compose(j, tex, ppu):
    s = j["solid"]
    ox, oy = s["origin"]
    cw, ch = int(np.ceil(s["w"] / s["ppu"] * ppu)), int(np.ceil(s["h"] / s["ppu"] * ppu))
    canvas = np.zeros((ch, cw, 4), np.float32)
    for r in sorted(j["rooms"], key=lambda r: -r["z"]):
        key = (r["texture"], r["texSize"][0], r["texSize"][1])
        if (r["z"] < j["backZ"] and r["sprite"] not in j["alwaysFloor"]) or r["z"] >= SKY_Z or key not in tex:
            continue
        t = tex[key]
        tri = np.array(r["tris"], np.float64).reshape(-1, 3, 2)
        im = Image.new("L", (cw, ch), 0)
        dr = ImageDraw.Draw(im)
        for p in tri:
            dr.polygon([((x - ox) * ppu, (y - oy) * ppu) for x, y in p], fill=1)
        m = np.asarray(im).astype(bool)  # PIL の y は上から下だが、ここでは行 = y の昇順として扱う (左下原点)
        ys, xs = np.nonzero(m)
        if len(xs) == 0:
            continue
        wx = ox + (xs + 0.5) / ppu
        wy = oy + (ys + 0.5) / ppu
        tx = np.floor((wx - r["w0"][0]) / r["d"][0]).astype(int)
        ty = np.floor((wy - r["w0"][1]) / r["d"][1]).astype(int)
        x0, y0, w, h = r["texRect"]
        ok = (tx >= int(round(x0))) & (tx < int(round(x0 + w))) & (ty >= int(round(y0))) & (ty < int(round(y0 + h)))
        xs, ys, tx, ty = xs[ok], ys[ok], tx[ok], ty[ok]
        src = t[ty, tx]
        a = src[:, 3:4]
        dst = canvas[ys, xs]
        out = src * a + dst * (1 - a)
        out[:, 3] = np.maximum(dst[:, 3], src[:, 3])
        canvas[ys, xs] = out
    return canvas


def front_cover_mask(j, tex, ppu, shape):
    """クルーの奥行きより手前 (z < FRONT_Z) の手前の絵 (機関・端末の台・壁の上面など) が描く画素。
    床の水の絵はクルーより奥の部屋の絵のすぐ手前に置かれるので、この絵は視界の中ではどこでも水より手前に描かれて水を隠す。
    影の中の写しは一番手前に出て床マスクで切るので、床から抜かないと絵の上に水が乗る"""
    return cover_mask(dict(j, covers=[r for r in j.get("covers", []) if r["z"] < FRONT_Z]), tex, ppu, shape)


def mesh_cover_mask(j, ppu, shape):
    """MESH_COVERS_SHIP の網目の三角形が描く画素 (網目は頂点の色で塗った形なので三角形がそのまま絵の形)"""
    names = MESH_COVERS_SHIP.get(j["ship"].replace("(Clone)", ""), set())
    ox, oy = j["solid"]["origin"]
    ch, cw = shape
    im = Image.new("L", (cw, ch), 0)
    dr = ImageDraw.Draw(im)
    for m in j.get("meshCovers", []):
        if m["name"] not in names:
            continue
        for p in np.array(m["tris"], np.float64).reshape(-1, 3, 2):
            dr.polygon([((x - ox) * ppu, (y - oy) * ppu) for x, y in p], fill=1)
    return np.asarray(im).astype(bool)


def crystal_mask(j, ppu, shape, art):
    """CRYSTAL_SEED_SHIP の網目の三角形に触れる、絵の赤い画素のまとまり (水晶の本体)"""
    names = CRYSTAL_SEED_SHIP.get(j["ship"].replace("(Clone)", ""))
    if not names or art is None:
        return np.zeros(shape, bool)
    img, appu = art
    ah, aw = img.shape[:2]
    ox, oy = j["solid"]["origin"]
    im = Image.new("L", (aw, ah), 0)
    dr = ImageDraw.Draw(im)
    for m in j.get("meshCovers", []):
        if m["name"] not in names:
            continue
        for p in np.array(m["tris"], np.float64).reshape(-1, 3, 2):
            dr.polygon([((x - ox) * appu, (y - oy) * appu) for x, y in p], fill=1)
    seed = np.asarray(im).astype(bool)
    rgb = img.astype(np.float32) / 255.0
    mx, mn = rgb.max(-1), rgb.min(-1)
    c = mx - mn
    sat = np.where(mx > 0, c / np.maximum(mx, 1e-6), 0)
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    hue = np.zeros_like(mx)
    has = c > 1e-6
    rr = has & (mx == r)
    gg = has & (mx == g) & ~rr
    bb = has & ~rr & ~gg
    hue[rr] = ((g - b)[rr] / c[rr]) % 6
    hue[gg] = (b - r)[gg] / c[gg] + 2
    hue[bb] = (r - g)[bb] / c[bb] + 4
    hue *= 60
    red = ((hue >= CRYSTAL_HUE[0]) | (hue <= CRYSTAL_HUE[1])) & (sat >= CRYSTAL_SAT) & (mx >= CRYSTAL_VAL)
    red = ndimage.binary_closing(red, iterations=2)
    lab, _ = ndimage.label(red)
    hit = np.unique(lab[seed & red])
    body = ndimage.binary_fill_holes(np.isin(lab, hit[hit > 0]))
    gx = np.clip(((np.arange(shape[1]) + 0.5) / ppu * appu).astype(int), 0, aw - 1)
    gy = np.clip(((np.arange(shape[0]) + 0.5) / ppu * appu).astype(int), 0, ah - 1)
    return body[gy[:, None], gx[None, :]]


def cover_mask(j, tex, ppu, shape):
    """手前の絵 (家具・木・岩) が描かれている画素。画素を引ける絵は不透明な所だけ、引けない絵は三角形の形"""
    ox, oy = j["solid"]["origin"]
    ch, cw = shape
    out = np.zeros(shape, bool)
    for r in j.get("covers", []):
        if r["sprite"] in ALWAYS_FLOOR:
            continue
        tri = np.array(r["tris"], np.float64).reshape(-1, 3, 2)
        if len(tri) == 0:
            continue
        im = Image.new("L", (cw, ch), 0)
        dr = ImageDraw.Draw(im)
        for p in tri:
            dr.polygon([((x - ox) * ppu, (y - oy) * ppu) for x, y in p], fill=1)
        m = np.asarray(im).astype(bool)
        key = (r.get("texture"), *r.get("texSize", (0, 0)))
        if key not in tex:
            out |= m
            continue
        ys, xs = np.nonzero(m)
        wx = ox + (xs + 0.5) / ppu
        wy = oy + (ys + 0.5) / ppu
        tx = np.floor((wx - r["w0"][0]) / r["d"][0]).astype(int)
        ty = np.floor((wy - r["w0"][1]) / r["d"][1]).astype(int)
        x0, y0, w, h = r["texRect"]
        ok = (tx >= int(round(x0))) & (tx < int(round(x0 + w))) & (ty >= int(round(y0))) & (ty < int(round(y0 + h)))
        xs, ys, tx, ty = xs[ok], ys[ok], tx[ok], ty[ok]
        hit = tex[key][ty, tx, 3] > DRAWN
        out[ys[hit], xs[hit]] = True
    return out


def grid_at(j, ppu, shape, bits):
    """SolidMap の升 (1/16 単位) の値を画素へ引く"""
    s = j["solid"]
    ch, cw = shape
    gx = np.minimum(((np.arange(cw) + 0.5) / ppu * s["ppu"]).astype(int), s["w"] - 1)
    gy = np.minimum(((np.arange(ch) + 0.5) / ppu * s["ppu"]).astype(int), s["h"] - 1)
    return bits[gy[:, None], gx[None, :]]


def furniture_mask(j, ppu, shape):
    s = j["solid"]
    ox, oy = s["origin"]
    ch, cw = shape
    im = Image.new("L", (cw, ch), 0)
    dr = ImageDraw.Draw(im)
    for f in j["furniture"]:
        segs = f["segs"]
        # 線分の組 (a, b, a, b, ...) を順につないだ輪郭として塗る
        pts = [((x - ox) * ppu, (y - oy) * ppu) for x, y in segs[0::2]]
        if len(pts) >= 3:
            dr.polygon(pts, fill=1)
    return np.asarray(im).astype(bool)


def top_mask(j, ppu, shape):
    """一方向の影の多角形 (外から中が見えない = 一段高い台地の上の面・輪郭は崖の縁) の内側の画素"""
    s = j["solid"]
    ox, oy = s["origin"]
    ch, cw = shape
    im = Image.new("L", (cw, ch), 0)
    dr = ImageDraw.Draw(im)
    for path in j.get("tops", []):
        pts = [((x - ox) * ppu, (y - oy) * ppu) for x, y in path]
        if len(pts) >= 3:
            dr.polygon(pts, fill=1)
    return np.asarray(im).astype(bool)


def top_split(j, ppu, shape, top):
    """TOP_SPLIT_SHIP の折れ線を TOP_LINE_W の幅で引いた画素 (台地の上の面の中だけ)。
    上の面の地面はこれを越えてつながない (歩ける所の升の丸めで線の外へはみ出した分も切る)"""
    s = j["solid"]
    ox, oy = s["origin"]
    ch, cw = shape
    im = Image.new("L", (cw, ch), 0)
    dr = ImageDraw.Draw(im)
    w = max(1, int(round(TOP_LINE_W * ppu)))
    for line in TOP_SPLIT_SHIP.get(j["ship"].replace("(Clone)", ""), []):
        dr.line([((x - ox) * ppu, (y - oy) * ppu) for x, y in line], fill=1, width=w, joint="curve")
    return np.asarray(im).astype(bool) & top


def top_walk(walk, top):
    """歩ける所のまとまりのうち、大半が台地の上の面の内側にあるもの (戻り値は内側の分) と、大半が外にあるもの (外の分)。
    升の丸めで輪郭の反対側へはみ出した歩ける所は、どちらの起点にも使わない"""
    lab, n = ndimage.label(walk)
    tot = np.bincount(lab.ravel(), minlength=n + 1)
    hit = np.bincount(lab.ravel(), weights=top.ravel(), minlength=n + 1)
    keep = hit * 2 >= tot
    keep[0] = False
    on = keep[lab]
    return on & top, walk & ~on & ~top


def load_art(path, appu):
    """paintbase の絵 (上の行から) を下の行からの配列にして返す"""
    Image.MAX_IMAGE_PIXELS = None
    return np.asarray(Image.open(path).convert("RGB"))[::-1], appu


def refine_top(j, ppu, top, walk, art):
    """台地の上の面の輪郭を、ゲームが描いた絵の色で画素ごとに決め直す。一方向の影の多角形の輪郭は崖の縁を粗くなぞっただけで、
    垂れた草や砂の縁が 0.1〜0.3 外へ出ている所・内へ入っている所がある。輪郭の前後 TOP_REFINE の帯の中だけ、
    上の面の色 (上の面の歩ける所の画素で学ぶ) と崖の面の色 (輪郭のすぐ外の画素で学ぶ) のどちらに近いかで分け直す。
    先に上の面と岩の面を分ける線 (TOP_SPLIT_SHIP) で切って上の面の歩ける所につながる側だけにするので、手で引いた直線も絵の縁へ寄る"""
    img, appu = art
    s = j["solid"]
    ah, aw = img.shape[:2]
    cut = top & ~top_split(j, ppu, top.shape, top)
    lab, n = ndimage.label(cut)
    keep = np.zeros(n + 1, bool)
    keep[np.unique(lab[top_walk(walk, top)[0] & cut])] = True
    keep[0] = False
    top = keep[lab]
    ys, xs = np.nonzero(ndimage.binary_dilation(top, iterations=1) ^ ndimage.binary_erosion(top, iterations=1))
    if len(xs) == 0:
        return top
    m = int((max(TOP_REFINE, FACE_SCAN) + 0.5) * ppu)
    y0, y1 = max(0, ys.min() - m), min(top.shape[0], ys.max() + m + 1)
    x0, x1 = max(0, xs.min() - m), min(top.shape[1], xs.max() + m + 1)
    t = top[y0:y1, x0:x1]
    wk = walk[y0:y1, x0:x1]
    gx = np.clip(((np.arange(x0, x1) + 0.5) / ppu * appu).astype(int), 0, aw - 1)
    gy = np.clip(((np.arange(y0, y1) + 0.5) / ppu * appu).astype(int), 0, ah - 1)
    q = (img[gy[:, None], gx[None, :]] >> 3).astype(np.int32)
    key = (q[..., 0] << 10) | (q[..., 1] << 5) | q[..., 2]
    din = ndimage.distance_transform_edt(t)
    dout = ndimage.distance_transform_edt(~t)
    on_top, _ = top_walk(wk, t)
    learn_top = on_top & (din > TOP_LEARN_IN * ppu)
    learn_face = ~t & (dout > TOP_LEARN_OUT[0] * ppu) & (dout < TOP_LEARN_OUT[1] * ppu) & ~ndimage.binary_dilation(wk, iterations=int(0.3 * ppu))
    def hist(sel):
        hh = np.bincount(key[sel], minlength=1 << 15).astype(np.float32).reshape(32, 32, 32)
        return ndimage.uniform_filter(hh, 3) / max(1, sel.sum())
    ht, hf = hist(learn_top), hist(learn_face)
    like = (ht >= hf).reshape(-1)[key]
    band = np.where(t, din, dout) <= TOP_REFINE * ppu
    core = t & ~band
    new = core | (band & like)
    # 色のまだら (草の中の橙の石・崖の面の明るい縞) をならし、上の面の奥につながる分だけ残す
    st = ndimage.generate_binary_structure(2, 1)
    r = max(1, int(round(TOP_SMOOTH * ppu)))
    # 草の葉や砂の縁取りの黒い線 (上の面にも崖の面にも出る色) は閉じてから穴を埋め、上の面の中を切らない
    # (水の絵の写しは床の画素だけを伝うので、細い線で切れると縁の升に水が入らず升の形の段が出る)
    new = ndimage.binary_closing(new, st, iterations=max(r, int(round(TOP_LINE_CLOSE * ppu)))) | core
    holes = ndimage.binary_fill_holes(new) & ~new
    lab, n = ndimage.label(holes)
    small = np.bincount(lab.ravel(), minlength=n + 1) <= TOP_HOLE * ppu * ppu
    small[0] = False
    new |= small[lab]  # 岩やキノコ (上の面の上に立つ物) の大きな穴は残す
    new = ndimage.binary_opening(new, st, iterations=r) | core
    lab, n = ndimage.label(new)
    keep = np.zeros(n + 1, bool)
    keep[np.unique(lab[core])] = True
    keep[0] = False
    out = top.copy()
    out[y0:y1, x0:x1] = keep[lab]
    # 上の面の外で崖の面の色の画素 (下の地面の床の続きにしない・下の水が崖の面を這い上がって見えない)
    fc = ~keep[lab] & ~like & (dout <= FACE_SCAN * ppu)
    fc = ndimage.binary_closing(ndimage.binary_opening(fc, st, iterations=r), st, iterations=r)
    face = np.zeros_like(top)
    face[y0:y1, x0:x1] = fc
    print(f"{j['ship']}: top refined +{int((out & ~top).sum())} -{int((top & ~out).sum())} px face {int(fc.sum())} px (learn top {int(learn_top.sum())} face {int(learn_face.sum())})")
    return out, face


def chroma(rgb):
    return rgb / np.maximum(rgb.sum(axis=-1, keepdims=True), 1e-3)


def palette(rgb, key, sel, share):
    """sel の画素でよく使われる色 (合わせて share を占めるまで・多い順)"""
    ks, counts = np.unique(key[sel], return_counts=True)
    cols, acc = [], 0
    for i in np.argsort(-counts)[:PALETTE_TOP]:
        cols.append(rgb[sel & (key == ks[i])].mean(axis=0))
        acc += counts[i]
        if acc >= counts.sum() * share:
            break
    return cols


def match(rgb, lum, ch, cols, shade_min):
    """cols のどれかと色合いが同じで、その色に比べた明るさが影の範囲の画素"""
    m = np.zeros(rgb.shape[:2], bool)
    for c in cols:
        cc = c / max(c.sum(), 1e-3)
        near = np.abs(ch - cc).max(axis=-1) < CHROMA_TOL
        ratio = lum / max(c.sum(), 1e-3)
        m |= near & (ratio > shade_min) & (ratio < SHADE_MAX)
    return m


def room_slices(j, ppu, shape):
    s = j["solid"]
    ox, oy = s["origin"]
    for r in j["rooms"]:
        if (r["z"] < j["backZ"] and r["sprite"] not in j["alwaysFloor"]) or r["z"] >= SKY_Z:
            continue
        x0, y0, w, h = r["texRect"]
        xs = [r["w0"][0] + (x0 + a) * r["d"][0] for a in (0, w)]
        ys = [r["w0"][1] + (y0 + b) * r["d"][1] for b in (0, h)]
        cx0, cx1 = int((min(xs) - ox) * ppu), int((max(xs) - ox) * ppu) + 1
        cy0, cy1 = int((min(ys) - oy) * ppu), int((max(ys) - oy) * ppu) + 1
        cx0, cy0 = max(cx0, 0), max(cy0, 0)
        if cx0 < min(cx1, shape[1]) and cy0 < min(cy1, shape[0]):
            yield (slice(cy0, cy1), slice(cx0, cx1))


def floor_colored(j, canvas, ppu, good):
    """部屋の絵ごとに、歩ける所でよく使われる色 (床の色) と色合いが同じで明るさが影の範囲の画素。
    戻り値の 2 つ目 = 学び直しで足した暗い床の色に合う画素 (縁取りの明るさの線より暗くても床の候補)"""
    rgb = canvas[..., :3]
    lum = rgb.sum(axis=-1)
    ch = chroma(rgb)
    q = np.clip((rgb * 15).round().astype(int), 0, 15)
    key = q[..., 0] * 256 + q[..., 1] * 16 + q[..., 2]
    out = np.zeros(canvas.shape[:2], bool)
    for sl in room_slices(j, ppu, canvas.shape[:2]):
        g = good[sl] & (canvas[sl][..., 3] > DRAWN)
        if g.sum() < 200:
            continue
        out[sl] |= match(rgb[sl], lum[sl], ch[sl], palette(rgb[sl], key[sl], g, PALETTE_SHARE), SHADE_MIN)
    return out, rgb, lum, ch, key


def learn(j, canvas, ppu, floor, inner, rgb, lum, ch, key):
    """壁と家具から離れた歩ける所なのに床でない塊 (床の色が部屋で少数派・暗い床・敷物) の色を床の色に足す"""
    area = LEARN_MIN * ppu * ppu
    lab, n = ndimage.label(inner & ~floor & (lum >= DARK_FLOOR * 3))
    if n == 0:
        return np.zeros(floor.shape, bool)
    big = np.bincount(lab.ravel(), minlength=n + 1) >= area
    big[0] = False
    miss = big[lab]
    out = np.zeros(floor.shape, bool)
    for sl in room_slices(j, ppu, canvas.shape[:2]):
        g = miss[sl]
        if g.sum() < area:
            continue
        m = match(rgb[sl], lum[sl], ch[sl], palette(rgb[sl], key[sl], g, LEARN_SHARE), DARK_SHADE)
        out[sl] |= m & (lum[sl] >= DARK_FLOOR * 3)
    return out


def cliff_faces(j, ppu, walk, bare, art):
    """台地の周りの崖の面の色の画素。台地 = 歩ける所のまとまりのうち、いちばん広い地面以外 (はしごでだけつながる高い所と低い所)。
    地面の船では台地の上の面・崖の面・下の地面が同じ絵の無い地面で、歩ける所から GROUND_REACH まで床にすると崖の面まで床になる
    (台地の水が崖の面へはみ出し、下の地面の水が崖の面を這い上がって見える)。台地から CLIFF_SCAN までの絵の無い地面を、
    近くの歩ける所の色 (地面) と歩ける所から少し離れた絵の無い所の色 (崖の面) のどちらに近いかで分ける。色は CLIFF_BLOCK の升ごとに学ぶ"""
    img, appu = art
    ah, aw = img.shape[:2]
    out = np.zeros(walk.shape, bool)
    lab, n = ndimage.label(walk)
    if n < 2:
        return out
    size = np.bincount(lab.ravel(), minlength=n + 1)
    size[0] = 0
    high = walk & (lab != size.argmax())
    ys, xs = np.nonzero(high)
    # 切り出しの角は升の格子に揃える (升の区切りが範囲で動かない)
    b = int(CLIFF_BLOCK * ppu)
    m = int((CLIFF_SCAN + CLIFF_LEARN_OUT[1] + CLIFF_BLOCK) * ppu)
    y0, y1 = max(0, ys.min() - m) // b * b, min(walk.shape[0], ys.max() + m + 1)
    x0, x1 = max(0, xs.min() - m) // b * b, min(walk.shape[1], xs.max() + m + 1)
    wk, bw = walk[y0:y1, x0:x1], bare[y0:y1, x0:x1]
    gx = np.clip(((np.arange(x0, x1) + 0.5) / ppu * appu).astype(int), 0, aw - 1)
    gy = np.clip(((np.arange(y0, y1) + 0.5) / ppu * appu).astype(int), 0, ah - 1)
    q = (img[gy[:, None], gx[None, :]] >> 3).astype(np.int32)
    key = (q[..., 0] << 10) | (q[..., 1] << 5) | q[..., 2]
    dout = ndimage.distance_transform_edt(~wk)
    learn_ground = wk & (ndimage.distance_transform_edt(wk) > TOP_LEARN_IN * ppu)
    learn_face = bw & (dout > CLIFF_LEARN_OUT[0] * ppu) & (dout < CLIFF_LEARN_OUT[1] * ppu)
    scope = bw & ~wk & (ndimage.distance_transform_edt(~high[y0:y1, x0:x1]) <= CLIFF_SCAN * ppu)
    fc = np.zeros(wk.shape, bool)
    for by in range(0, wk.shape[0], b):
        for bx in range(0, wk.shape[1], b):
            blk = (slice(by, by + b), slice(bx, bx + b))
            if not scope[blk].any():
                continue
            win = (slice(max(0, by - b), by + 2 * b), slice(max(0, bx - b), bx + 2 * b))
            sg, sf = learn_ground[win], learn_face[win]
            if sg.sum() < CLIFF_MIN or sf.sum() < CLIFF_MIN:
                continue
            kw = key[win]
            like = (hist_of(kw, sg) >= hist_of(kw, sf)).reshape(-1)
            fc[blk] = scope[blk] & ~like[key[blk]]
    # 色のまだら (崖の面の明るい縞・地面の中の橙の石) をならす
    st = ndimage.generate_binary_structure(2, 1)
    r = max(1, int(round(TOP_SMOOTH * ppu)))
    fc = ndimage.binary_closing(ndimage.binary_opening(fc, st, iterations=r), st, iterations=r) & scope
    out[y0:y1, x0:x1] = fc
    print(f"{j['ship']}: cliff faces {int(fc.sum())} px around {n - 1} raised areas")
    return out


def hist_of(key, sel):
    """色 (各 5 ビット) の出現の割合 (隣の色へ少しならす)"""
    hh = np.bincount(key[sel], minlength=1 << 15).astype(np.float32).reshape(32, 32, 32)
    return ndimage.uniform_filter(hh, 3) / max(1, sel.sum())


def classify(j, canvas, ppu, cover, art=None):
    s = j["solid"]
    raw = np.frombuffer(__import__("base64").b64decode(s["open"]), np.uint8)
    bits = np.unpackbits(raw, bitorder="little")[: s["w"] * s["h"]].reshape(s["h"], s["w"]).astype(bool)
    shape = canvas.shape[:2]
    walk = grid_at(j, ppu, shape, bits)
    furn = furniture_mask(j, ppu, shape)
    good = walk & ~furn
    lum = canvas[..., :3] @ np.array([0.299, 0.587, 0.114], np.float32)
    drawn = canvas[..., 3] > DRAWN
    colored, rgb, lsum, ch, key = floor_colored(j, canvas, ppu, good)
    body = drawn & (lum >= OUTLINE) & colored
    floor = keep_walkable(body, good, drawn)
    # 部屋の絵の無い所 (屋外の地面は部屋の絵でない) は歩ける所をそのまま床にする
    floor |= walk & ~drawn
    # 距離は丸く測る (升の歩ける所を 4 近傍で太らせると菱形の角が縁に出る)
    near = ndimage.distance_transform_edt(~walk) <= REACH * ppu
    # 学び直し: 壁と家具の当たり判定から離れた歩ける所の取りこぼし。足した色は壁の面にも使われていることがある
    # (ナビの暗い床板と斜めの壁) ので、歩ける所の近くだけに切ってから、歩ける所が大半のまとまりだけを足す
    inner = (ndimage.distance_transform_edt(walk) > LEARN_INSET * ppu) & (ndimage.distance_transform_edt(~furn) > LEARN_INSET * ppu)
    learned = np.zeros(shape, bool)
    for _ in range(LEARN_PASSES):
        raw = learn(j, canvas, ppu, floor, inner & drawn, rgb, lsum, ch, key)
        learned |= raw
        more = raw & near & drawn & ~floor
        lab, n = ndimage.label(more)
        if n == 0:
            break
        tot = np.bincount(lab.ravel(), minlength=n + 1)
        hit = np.bincount(lab.ravel(), weights=good.ravel(), minlength=n + 1)
        ok = hit >= tot * FLOOR_SHARE
        ok[0] = False
        if not ok.any():
            break
        # 床の色に入っていても、同じ色の壁とつながって落ちたまとまりがある。歩ける所の近くで切ったまとまりのまま足す
        floor |= keep_walkable(ok[lab], good, drawn)
    deep = ndimage.distance_transform_edt(furn) > FURN_DEEP * ppu
    # 床の色の画素のうち歩ける所から遠い物も、同じまとまりなら床の続き (当たり判定だけで閉じた所)。水が物理的に届く所の候補
    cand = drawn & ((lum >= OUTLINE) & colored | learned & (lsum >= DARK_FLOOR * 3)) & ~cover
    floor &= near
    reach = GROUND_REACH_SHIP.get(j["ship"].replace("(Clone)", ""))
    top = poly = top_mask(j, ppu, shape)
    cliff = None
    if art is not None and top.any():
        top, cliff = refine_top(j, ppu, poly, walk, art)
    band = top_split(j, ppu, shape, top)
    # 一方向の影の多角形の無い台地 (上の面と崖の縁の手がかりが無い) の崖の面の画素 (絵の色で分ける)
    faces = cliff_faces(j, ppu, walk, ~drawn & ~cover, art) if art is not None else np.zeros(shape, bool)
    if reach:
        # 歩ける所につながった絵の無い地面 (家具と壁の絵の縁まで)。台地の上の面と外 (崖の面・下の地面) は同じ絵の無い地面なので、
        # 崖の縁 (上の面の輪郭) をまたいでつながない: 上の面の地面は上の面の歩ける所から、外の地面は外の歩ける所からだけ。
        # 上の面の中でも上の面と岩の面を分ける線 (TOP_SPLIT_SHIP) は越えない
        ground = ~drawn & ~cover & (ndimage.distance_transform_edt(~walk) <= reach * ppu)
        on_top, off_top = top_walk(walk, top)
        # 外の地面のうち上の面の輪郭 (崖の縁) から TOP_FACE までは崖の面: 下の地面の水はそこまで這い上がらず、
        # 上の面の床とも離れる (写しが崖の縁を越えて上の面の水を崖の面へ運ばない)
        below = ~top & (ndimage.distance_transform_edt(~top) > TOP_FACE * ppu) if top.any() else ~top
        # 多角形のうち上の面から離れた所 (分け線の向こうの岩の面) は下の地面の床にもしない
        if top is not poly:
            below &= ~(poly & (ndimage.distance_transform_edt(~top) > TOP_REFINE * ppu))
        if cliff is not None:
            below &= ~cliff
        # 崖の面は下の地面と台地の地面から外す
        below &= ~faces
        for side, seed in ((top & ~band, on_top), (below, off_top)):
            g = ground & side
            lab, n = ndimage.label(g)
            touch = np.zeros(n + 1, bool)
            touch[np.unique(lab[seed & g])] = True
            touch[0] = False
            floor |= touch[lab]
    wet, show, hide, plat = beyond(j, ppu, floor, cand, wall_face(canvas, floor, ppu) | front_face(canvas, floor, ppu), walk, cover, drawn, canvas, top, band, faces)
    return smooth((floor & ~deep & ~hide) | show), walk, furn, wet, plat


def beyond(j, ppu, floor, cand, front, walk, cover, drawn, canvas, top, band, faces):
    """水が物理的に届く所: 歩ける所から、床の色の画素 (壁の面は除く・地面の船は海でない地面も) だけを通り、影の線 (壁の根元) と
    手前の絵 (家具・木・岩) を越えずに PHYS_REACH まで塗った範囲。当たり判定だけで閉じた所 (見えない柵の向こうの床・家具の当たり判定の
    張り出し) にも水は流れる。戻り値 = (水の範囲, 水を見せる画素, 水を見せない画素)"""
    s = j["solid"]
    ox, oy = s["origin"]
    shape = floor.shape
    im = Image.new("L", (shape[1], shape[0]), 0)
    dr = ImageDraw.Draw(im)
    for x0, y0, x1, y1 in j.get("shadows", []):
        dr.line([((x0 - ox) * ppu, (y0 - oy) * ppu), ((x1 - ox) * ppu, (y1 - oy) * ppu)], fill=1, width=BARRIER_PX)
    barrier = np.asarray(im).astype(bool)
    im = Image.new("L", (shape[1], shape[0]), 0)
    dr = ImageDraw.Draw(im)
    for x0, y0, x1, y1 in PHYS_RELAX_SHIP.get(j["ship"].replace("(Clone)", ""), []):
        dr.rectangle([(x0 - ox) * ppu, (y0 - oy) * ppu, (x1 - ox) * ppu, (y1 - oy) * ppu], fill=1)
    for x0, y0, x1, y1 in PHYS_RELAX_CUT_SHIP.get(j["ship"].replace("(Clone)", ""), []):
        dr.rectangle([(x0 - ox) * ppu, (y0 - oy) * ppu, (x1 - ox) * ppu, (y1 - oy) * ppu], fill=0)
    relax = np.asarray(im).astype(bool)
    sea = sea_mask(j, ppu, shape)
    # 例外の範囲は絵のある画素を全部床の続きにする (床の黒い模様・床に這うホース・鎖の柵)。家具は手前の絵と抜く範囲 (燃料タンク) で除く
    land = cand & ~front | relax & drawn
    if sea is not None:
        land &= ~sea
    reach = ndimage.distance_transform_edt(~walk) <= PHYS_REACH * ppu
    st = ndimage.generate_binary_structure(2, 1)
    room = (land & ~cover & ~barrier & reach) | walk
    # つながりは PHYS_GATE より狭い隙間 (縁取りの切れ目・機械と壁の間の細い床の色) を通さずに決める
    g = max(1, int(round(PHYS_GATE * ppu / 2)))
    lab, n = ndimage.label(ndimage.binary_opening(room & ~relax, st, iterations=g) | (room & relax) | walk)
    touch = np.zeros(n + 1, bool)
    touch[np.unique(lab[walk])] = True
    touch[0] = False
    core = touch[lab]
    # 通したまとまりの縁 (開いて削れた分) は元の形まで戻す
    region = core | (ndimage.binary_dilation(core, st, iterations=g + 1) & room)
    # 細い筋 (絵の枠・縁取りの脇の 1〜3 画素) は水の通り道にしない
    k = max(1, int(round(PHYS_MIN_W * ppu / 2)))
    region = ndimage.binary_opening(region, st, iterations=k) & ~walk
    # 台地の上の面の絵の無い地面: 輪郭が崖の縁なので、崖の面へ出ずに縁まで水が届く (当たり判定の多角形で閉じた縁の帯も)。
    # 上の面の歩ける所からだけ広げ、上の面と岩の面を分ける線 (TOP_SPLIT_SHIP) は越えない
    seed = top_walk(walk, top)[0] & ~band
    up = top & ~drawn & ~cover & ~band & ~barrier & reach
    if sea is not None:
        up &= ~sea
    lab, n = ndimage.label(up | seed)
    touch = np.zeros(n + 1, bool)
    touch[np.unique(lab[seed])] = True
    touch[0] = False
    region |= ndimage.binary_opening(touch[lab], st, iterations=k) & ~walk
    # 台地の上の面の外の絵の無い地面 (Fungle の崖の面・下の地面) は高さの違う所がつながって見えるので、水の通り道には足さず見せるだけ (GROUND_REACH)。
    # 一方向の影の多角形の無い台地は、崖の縁 (壁の線) までの台地の地面と置いた物の下を水の通り道にする (見せるのは絵の無い地面だけ)
    plat = plateau_ground(j, ppu, walk, top, faces, barrier, sea)
    # 例外の範囲の手前の絵 (ドロップシップ) は床の色でも水を見せない
    return region | plat, region | (plat & ~drawn & ~cover), relax & cover, plat


def plateau_ground(j, ppu, walk, top, faces, barrier, sea):
    """一方向の影の多角形の無い台地 (歩ける所のまとまりのうち、いちばん広い地面以外) ごとに、歩ける所から PLATEAU_REACH までを、
    崖の縁の壁の線・視界の影の線・崖の面の色の画素・はしごの絵・海・ほかのまとまりの歩ける所を越えずに塗った範囲 (歩ける所を除く)"""
    out = np.zeros(walk.shape, bool)
    own = np.zeros(walk.shape, np.int16)  # 足した地面のまとまりの番号 (-1 = 2 つ以上から届く)
    reach = PLATEAU_REACH_SHIP.get(j["ship"].replace("(Clone)", ""))
    if not reach:
        return out
    s = j["solid"]
    ox, oy = s["origin"]
    shape = walk.shape
    im = Image.new("L", (shape[1], shape[0]), 0)
    dr = ImageDraw.Draw(im)
    for x0, y0, x1, y1 in j.get("lines", []):
        dr.line([((x0 - ox) * ppu, (y0 - oy) * ppu), ((x1 - ox) * ppu, (y1 - oy) * ppu)], fill=1, width=BARRIER_PX)
    for r in j.get("covers", []):
        if r["sprite"] not in PLATEAU_SKIP:
            continue
        for p in np.array(r["tris"], np.float64).reshape(-1, 3, 2):
            dr.polygon([((x - ox) * ppu, (y - oy) * ppu) for x, y in p], fill=1)
    edges = np.asarray(im).astype(bool) | barrier
    block = edges | faces
    if sea is not None:
        block |= sea
    lab, n = ndimage.label(walk)
    size = np.bincount(lab.ravel(), minlength=n + 1)
    size[0] = 0
    r = int(np.ceil(reach * ppu)) + BARRIER_PX
    st = ndimage.generate_binary_structure(2, 1)
    for i, sl in enumerate(ndimage.find_objects(lab), 1):
        if i == size.argmax() or sl is None:
            continue
        isl = lab == i
        # 一方向の影の多角形の中の台地は上の面の地面として塗ってある
        if (isl & top).sum() * 2 >= size[i]:
            continue
        win = (slice(max(0, sl[0].start - r), sl[0].stop + r), slice(max(0, sl[1].start - r), sl[1].stop + r))
        me = isl[win]
        other = ndimage.binary_dilation(walk[win] & ~me, st, iterations=BARRIER_PX)
        dist, (iy, ix) = ndimage.distance_transform_edt(~me, return_indices=True)
        near = dist <= reach * ppu
        # 歩ける所に囲まれた物 (台地の真ん中の水晶) は崖の縁に接していないので、水の通り道のまま閉じておく
        near &= ~(ndimage.binary_fill_holes(me) & ~me)
        reachable = near.copy()
        # いちばん近い歩ける所から真っすぐ見えない所 (線の端を回り込んだ柵の裏) は広げない
        vy, vx = np.indices(me.shape)
        vy, vx = vy - iy, vx - ix
        bw = block[win]
        thick = ndimage.binary_dilation(bw, st, iterations=BARRIER_PX)
        steps = max(2, int(np.ceil(reach * ppu / (2 * BARRIER_PX))))
        for t in np.arange(1, steps) / steps:
            near &= ~thick[np.round(iy + vy * t).astype(int), np.round(ix + vx * t).astype(int)]
        l2, m = ndimage.label((near & ~bw & ~other) | me)
        touch = np.zeros(m + 1, bool)
        touch[np.unique(l2[me])] = True
        touch[0] = False
        got = touch[l2] & ~me
        # 足した地面と歩ける所と線で囲まれた小さな残り (箱の上の面が崖の面の色で外れた所) も埋める。
        # 残すと箱の裏の柵の線まで升が届かず、箱の上の列だけ柵を越えて落ちる縁になる
        wall = ndimage.binary_dilation(edges[win], st, iterations=BARRIER_PX)
        pocket = ndimage.binary_fill_holes(me | got | wall) & ~(me | got | wall) & reachable & ~other
        l3, m3 = ndimage.label(pocket)
        if m3:
            small = np.bincount(l3.ravel(), minlength=m3 + 1) <= PLATEAU_POCKET * ppu * ppu
            small[0] = False
            got |= small[l3]
        own[win][got & (own[win] == 0)] = i
        own[win][got & (own[win] != i)] = -1
    # 別のまとまりの歩ける所か足した地面に PLATEAU_GAP より近い所は足さない (2 つの台地の間の柵の裏を水だけの升でつなぐと、
    # どちらでもない高さの升になって両側から滝が落ちる)
    gap = max(1, int(round(PLATEAU_GAP * ppu)))
    for i in np.unique(own[own > 0]):
        mine = own == i
        others = (own != 0) & ~mine | (lab != 0) & (lab != i)
        out |= mine & (ndimage.distance_transform_edt(~others) > gap)
    k = max(1, int(round(PHYS_MIN_W * ppu / 2)))
    out = ndimage.binary_opening(out, st, iterations=k) & ~walk
    print(f"{j['ship']}: plateau ground {int(out.sum())} px")
    return out


def air_cells(j):
    """手で塗った空 (src/Terrain/MapAirData.cs の <船>Air の矩形) を水の計算の升 (0.25 単位) へ。升の真ん中が矩形に入る升 (MapNotes.RasterAir と同じ)"""
    s = j["solid"]
    ship = j["ship"].replace("(Clone)", "").replace("Ship", "")
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "Terrain", "MapAirData.cs")
    m = re.search(r"\W" + ship + r"Air\s*=\s*\{([^}]*)\}", open(path, encoding="utf-8").read()) if os.path.exists(path) else None
    if not m:
        return None
    v = [float(t.strip().rstrip("fF")) for t in m.group(1).split(",") if t.strip()]
    ox, oy = s["origin"]
    w, h = s["w"] // WATER_SUB, s["h"] // WATER_SUB
    c = WATER_SUB / s["ppu"]
    air = np.zeros((h, w), bool)
    for i in range(0, len(v) - 3, 4):
        x0 = max(0, int(np.ceil((v[i] - ox) / c - 0.5)))
        x1 = min(w - 1, int(np.floor((v[i + 2] - ox) / c - 0.5)))
        y0 = max(0, int(np.ceil((v[i + 1] - oy) / c - 0.5)))
        y1 = min(h - 1, int(np.floor((v[i + 3] - oy) / c - 0.5)))
        if x0 <= x1 and y0 <= y1:
            air[y0:y1 + 1, x0:x1 + 1] = True
    return air


def drop_air_backs(j, wet, plat):
    """台地の地面として足した水の升のうち、画面の上隣か横隣が閉じた空の升になるものを外す (不動点まで)。
    空は足す前の歩ける所の境に合わせて塗ってあり、台地の上の物 (機械・箱) や奥の上の崖の根元まで掛かっている所がある。
    隣が空だと、計算は物の向こうの空へ落ちる縁にする (物や上の崖の根元から水が消える)。台地の地面の水は画面の下向きの縁から落とす"""
    air = air_cells(j)
    if air is None or not plat.any():
        return wet
    s = j["solid"]
    raw = np.frombuffer(__import__("base64").b64decode(s["open"]), np.uint8)
    walk = np.unpackbits(raw, bitorder="little")[: s["w"] * s["h"]].reshape(s["h"], s["w"]).astype(bool)
    h, w = air.shape
    hs, ws = h * WATER_SUB, w * WATER_SUB
    wet = wet.copy()
    plat = plat & wet
    dropped = 0
    while True:
        both = (walk | wet)[:hs, :ws].reshape(h, WATER_SUB, w, WATER_SUB).sum(axis=(1, 3))
        opened = both >= WATER_SUB
        mine = plat[:hs, :ws].reshape(h, WATER_SUB, w, WATER_SUB).any(axis=(1, 3))
        shut = ~opened & air
        back = np.zeros_like(opened)
        back[:-1] |= shut[1:]
        back[:, :-1] |= shut[:, 1:]
        back[:, 1:] |= shut[:, :-1]
        bad = opened & back & mine
        if not bad.any():
            break
        cut = np.repeat(np.repeat(bad, WATER_SUB, 0), WATER_SUB, 1) & plat[:hs, :ws]
        wet[:hs, :ws] &= ~cut
        plat[:hs, :ws] &= ~cut
        dropped += int(bad.sum())
    print(f"{j['ship']}: dropped {dropped} plateau cells next to painted air")
    return wet


def sea_mask(j, ppu, shape):
    """Resources/Heights の海の升を画素へ"""
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Resources", "Heights", j["ship"].replace("(Clone)", "") + ".bin")
    if not os.path.exists(path):
        return None
    d = open(path, "rb").read()
    ver, ox16, oy16, c16, bw, bh = struct.unpack("<BhhBHH", d[4:14])
    v = np.frombuffer(d[14:14 + bw * bh], np.int8).reshape(bh, bw)
    s = j["solid"]
    ox, oy = s["origin"]
    ch, cw = shape
    wx = ox + (np.arange(cw) + 0.5) / ppu
    wy = oy + (np.arange(ch) + 0.5) / ppu
    bx = np.floor((wx - ox16 / 16) / (c16 / 16)).astype(int)
    by = np.floor((wy - oy16 / 16) / (c16 / 16)).astype(int)
    okx = (bx >= 0) & (bx < bw)
    oky = (by >= 0) & (by < bh)
    m = np.zeros(shape, bool)
    sub = v[np.clip(by, 0, bh - 1)][:, np.clip(bx, 0, bw - 1)] == -128
    m[:] = sub & oky[:, None] & okx[None, :]
    return m


def wet_cells(j, ppu, phys):
    """歩ける所の地図の升 (1/16 単位) ごとに、半分以上が phys なら 1"""
    s = j["solid"]
    ch, cw = phys.shape
    gx = np.minimum(((np.arange(cw) + 0.5) / ppu * s["ppu"]).astype(int), s["w"] - 1)
    gy = np.minimum(((np.arange(ch) + 0.5) / ppu * s["ppu"]).astype(int), s["h"] - 1)
    idx = (gy[:, None] * s["w"] + gx[None, :]).ravel()
    tot = np.bincount(idx, minlength=s["w"] * s["h"])
    hit = np.bincount(idx, weights=phys.ravel().astype(np.float32), minlength=s["w"] * s["h"])
    return ((hit * 2 >= tot) & (tot > 0)).reshape(s["h"], s["w"])


def bridge_wet(j, wet):
    """水の届く升と歩ける升の間の WET_BRIDGE 升までの隙間 (画素の床を升へ丸めた時の継ぎ目・柵の当たり判定の線) を埋める。
    影の線 (壁の根元) の升は埋めない (壁の向こうの部屋とつながない)"""
    s = j["solid"]
    w, h = s["w"], s["h"]
    raw = np.frombuffer(__import__("base64").b64decode(s["open"]), np.uint8)
    walk = np.unpackbits(raw, bitorder="little")[: w * h].reshape(h, w).astype(bool)
    ox, oy = s["origin"]
    c = s["ppu"]
    im = Image.new("L", (w, h), 0)
    dr = ImageDraw.Draw(im)
    for x0, y0, x1, y1 in j.get("shadows", []):
        dr.line([((x0 - ox) * c, (y0 - oy) * c), ((x1 - ox) * c, (y1 - oy) * c)], fill=1, width=1)
    bar = ndimage.binary_dilation(np.asarray(im).astype(bool))
    # 台地の上の面と岩の面を分ける線 (TOP_SPLIT_SHIP) も埋めない
    bar |= top_split(j, c, (h, w), top_mask(j, c, (h, w)))
    st = ndimage.generate_binary_structure(2, 1)
    near_wet = ndimage.binary_dilation(wet, st, iterations=WET_BRIDGE)
    near_walk = ndimage.binary_dilation(walk, st, iterations=WET_BRIDGE)
    gap = near_wet & near_walk & ~walk & ~wet & ~bar
    # 1 升の穴と欠け (画素を升へ丸めた時のまだら) も埋める。升の中の歩ける升が分かれると水の升ごと閉じる
    full = ndimage.binary_closing(wet | gap | walk, np.ones((3, 3), bool), border_value=1) & ~walk & ~bar & near_wet
    print(f"{j['ship']}: bridged {int((full & ~wet).sum())} cells")
    return wet | full


def exclude_wet(j, wet):
    """PHYS_EXCLUDE_SHIP の矩形の升を水の届く升から外す (隙間を埋めた後に外す)"""
    s = j["solid"]
    ox, oy = s["origin"]
    c = s["ppu"]
    for x0, y0, x1, y1 in PHYS_EXCLUDE_SHIP.get(j["ship"].replace("(Clone)", ""), []):
        wet[max(0, int((y0 - oy) * c)):max(0, int(np.ceil((y1 - oy) * c))), max(0, int((x0 - ox) * c)):max(0, int(np.ceil((x1 - ox) * c)))] = False
    return wet


def seal_walls(j, wet):
    """水だけの升のまとまりが、近くで歩いてつながらない 2 つ以上の歩ける所に触れていたら (壁を挟んでいる)、
    まとまりを近い方の歩ける所で塗り分け、持ち主の替わる境目の升を外す。床の絵が壁の根元まで続き、
    壁のすぐ外がまた歩ける所だと (Polus の筒の通路の壁・屋外との境・壁の線の角)、水だけの升が壁を越えて内と外をつないでいた。
    柵や扉の敷居のように近くで回り込める所は 1 つの歩ける所に触れているだけなので外さない"""
    if j["ship"].replace("(Clone)", "") in SEAL_SKIP_SHIP:
        return wet
    s = j["solid"]
    w, h = s["w"], s["h"]
    raw = np.frombuffer(__import__("base64").b64decode(s["open"]), np.uint8)
    walk = np.unpackbits(raw, bitorder="little")[: w * h].reshape(h, w).astype(bool)
    st = ndimage.generate_binary_structure(2, 1)
    lab, n = ndimage.label(wet & ~walk, st)
    cut = np.zeros_like(wet)
    for i, sl in enumerate(ndimage.find_objects(lab)):
        y0, x0 = max(0, sl[0].start - SEAL_LOCAL), max(0, sl[1].start - SEAL_LOCAL)
        y1, x1 = min(h, sl[0].stop + SEAL_LOCAL), min(w, sl[1].stop + SEAL_LOCAL)
        g = lab[y0:y1, x0:x1] == i + 1
        wl, _ = ndimage.label(walk[y0:y1, x0:x1], st)
        ids = np.unique(wl[ndimage.binary_dilation(g, st) & (wl > 0)])
        if len(ids) < 2:
            continue
        own = np.where(np.isin(wl, ids), wl, 0)
        reach = g | (own > 0)
        while True:
            grow = ndimage.grey_dilation(own, footprint=st) * (reach & (own == 0))
            if not grow.any():
                break
            own = np.where(grow > 0, grow, own)
        hi = ndimage.maximum_filter(own, footprint=st)
        lo = ndimage.minimum_filter(np.where(own > 0, own, np.iinfo(own.dtype).max), footprint=st)
        cut[y0:y1, x0:x1] |= g & (own > 0) & ((hi != own) | (lo != own))
    print(f"{j['ship']}: sealed {int(cut.sum())} cells")
    return wet & ~cut


def write_wet(path, j, cells):
    s = j["solid"]
    packed = zlib.compress(np.packbits(cells.ravel(), bitorder="little").tobytes(), 9)
    ox, oy = s["origin"]
    with open(path, "wb") as fo:
        fo.write(b"MRPW" + struct.pack("<BhhHHBI", 1, int(round(ox * 16)), int(round(oy * 16)), s["w"], s["h"], s["ppu"], len(packed)) + packed)
    return os.path.getsize(path)


def keep_walkable(body, good, drawn):
    """床の色のまとまりのうち、歩ける所が大半のものを床に (床の上の細い飾り線も埋める)"""
    lab, n = ndimage.label(body)
    tot = np.bincount(lab.ravel(), minlength=n + 1)
    hit = np.bincount(lab.ravel(), weights=good.ravel(), minlength=n + 1)
    keep = hit >= tot * FLOOR_SHARE
    keep[0] = False
    st = ndimage.generate_binary_structure(2, 1)
    return ndimage.binary_closing(keep[lab], st, iterations=LINE_CLOSE) & drawn


def smooth(floor):
    """縁の画素のぎざぎざ (絵の輪郭のにじみで床の判定が 1 画素ずつ揺れる) をならす"""
    lab, n = ndimage.label(floor)
    floor &= (np.bincount(lab.ravel(), minlength=n + 1) >= SPECK)[lab]
    lab, n = ndimage.label(~floor)
    floor |= (np.bincount(lab.ravel(), minlength=n + 1) < SPECK)[lab]
    # ぼかして半分で切り直す: 面積はほぼ保ったまま、縁が滑らかな線になる
    return ndimage.gaussian_filter(floor.astype(np.float32), EDGE_SIGMA) >= 0.5


def report(j, ppu, floor, walk, furn, top=20):
    """取りこぼしの一覧: 壁と家具から離れた歩ける所なのに床でない塊 (広い順)。床の判定の誤りを探す"""
    ox, oy = j["solid"]["origin"]
    inner = (ndimage.distance_transform_edt(walk) > REPORT_INSET * ppu) & (ndimage.distance_transform_edt(~furn) > REPORT_INSET * ppu)
    miss = inner & ~floor
    lab, n = ndimage.label(miss)
    sz = np.bincount(lab.ravel(), minlength=n + 1)
    sz[0] = 0
    objs = ndimage.find_objects(lab)
    total = inner.sum()
    print(f"  歩ける所 {total / ppu / ppu:.1f} 単位^2 のうち取りこぼし {miss.sum() / ppu / ppu:.2f} ({miss.sum() / max(total, 1):.2%})")
    for i in np.argsort(-sz)[:top]:
        if sz[i] < LEARN_MIN * ppu * ppu * 0.5:
            break
        ys, xs = objs[i - 1]
        print(f"  {sz[i] / ppu / ppu:6.2f} 単位^2  x {ox + xs.start / ppu:7.2f}..{ox + xs.stop / ppu:7.2f}  y {oy + ys.start / ppu:7.2f}..{oy + ys.stop / ppu:7.2f}")
    return miss


def wall_face(canvas, floor, ppu):
    """列ごとに、床の画素の真上から途切れずに続く床でない絵の画素 (床から FACE_MAX 以内)"""
    drawn = canvas[..., 3] > DRAWN
    rows = np.arange(floor.shape[0])[:, None]
    last = np.maximum.accumulate(np.where(floor, rows, -1), axis=0)
    # 床でない絵が途切れた所 (絵の無い画素) も数える: その行より下の床から続く面は、そこで切れる
    gap = np.maximum.accumulate(np.where(~drawn & ~floor, rows, -1), axis=0)
    return drawn & ~floor & (last >= 0) & (last > gap) & (rows - last <= FACE_MAX * ppu)


def front_face(canvas, floor, ppu):
    """列ごとに、床の画素の真下へ途切れずに続く床でない絵の画素 (FRONT_MAX まで)。南の壁の前面 (床より下に描かれる)"""
    drawn = canvas[..., 3] > DRAWN
    rows = np.arange(floor.shape[0])[:, None]
    h = floor.shape[0]
    # 上下を返して wall_face と同じ数え方 (配列の行 0 = 世界の下)
    f, d = floor[::-1], drawn[::-1]
    last = np.maximum.accumulate(np.where(f, rows, -1), axis=0)
    gap = np.maximum.accumulate(np.where(~d & ~f, rows, -1), axis=0)
    out = d & ~f & (last >= 0) & (last > gap) & (rows - last <= FRONT_MAX * ppu)
    return out[::-1]


def write_mask(path, sig, origin, ppu, floor, face):
    h, w = floor.shape
    packed = np.packbits(floor.ravel(), bitorder="little").tobytes()
    packed_face = zlib.compress(np.packbits(face.ravel(), bitorder="little").tobytes(), 9)
    # sig = ゲームが書き出した部屋の絵の指紋。読み込む時に今のゲームの絵の指紋と比べる
    head = MAGIC + struct.pack("<iiifffI", VERSION, w, h, origin[0], origin[1], ppu, sig)
    body = zlib.compress(packed, 9)
    with open(path, "wb") as fo:
        fo.write(head + struct.pack("<ii", len(body), len(packed_face)) + body + packed_face)
    return os.path.getsize(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("json", nargs="+")
    ap.add_argument("--bundles", default=None)
    ap.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "..", "Resources", "FloorMasks"))
    ap.add_argument("--wet", default=os.path.join(os.path.dirname(__file__), "..", "Resources", "WaterOpen"))
    ap.add_argument("--preview", default=None)
    ap.add_argument("--report", action="store_true", help="取りこぼし (歩ける所なのに床でない塊) を一覧し、プレビューに赤で出す")
    ap.add_argument("--art", default=None, help="ゲームの paintbase が描いたマップ全体の絵 (台地の上の面の崖の縁を画素で決め直す・左下 = 歩ける所の地図の左下)")
    ap.add_argument("--art-ppu", type=float, default=64.0, help="--art の 1 単位の画素数 (paintbase 4 = 64)")
    a = ap.parse_args()
    folder = a.bundles or next((b for b in DEFAULT_BUNDLES if os.path.isdir(b)), None)
    if not folder:
        sys.exit("ゲームの StandaloneWindows フォルダが見つからない (--bundles で指定)")
    os.makedirs(a.out, exist_ok=True)
    os.makedirs(a.wet, exist_ok=True)
    for path in a.json:
        j = json.load(open(path, encoding="utf-8"))
        j["backZ"] = BACK_Z_SHIP.get(j["ship"].replace("(Clone)", ""), BACK_Z)
        fc = FLOOR_COVERS.get(j["ship"].replace("(Clone)", ""), set())
        j["alwaysFloor"] = ALWAYS_FLOOR | fc
        j["rooms"] = j["rooms"] + [r for r in j.get("covers", []) if r["sprite"] in fc]
        j["covers"] = [r for r in j.get("covers", []) if r["sprite"] not in fc]
        ds = sorted(abs(r["d"][0]) for r in j["rooms"])
        ppu = round(1.0 / ds[len(ds) // 2], 4)  # 部屋の絵でいちばん多い細かさ
        ship = j["ship"].replace("(Clone)", "")
        tex = load_textures(folder, j["rooms"] + j.get("covers", []))
        canvas = compose(j, tex, ppu)
        cover = cover_mask(j, tex, ppu, canvas.shape[:2])
        art = load_art(a.art, a.art_ppu) if a.art else None
        floor, walk, furn, phys, plat = classify(j, canvas, ppu, cover, art)
        if fc:
            # 床の絵の画素は色で選ばず、歩ける所の近くを全部床にする (坂の縞は部屋の床の色と違う)
            fl = cover_mask(dict(j, covers=[r for r in j["rooms"] if r["sprite"] in fc]), tex, ppu, floor.shape)
            floor |= fl & (ndimage.distance_transform_edt(~walk) <= REACH * ppu)
        front = front_cover_mask(j, tex, ppu, floor.shape)
        floor &= ~(mesh_cover_mask(j, ppu, floor.shape) | crystal_mask(j, ppu, floor.shape, art) | front)
        wet = seal_walls(j, exclude_wet(j, bridge_wet(j, wet_cells(j, ppu, phys))))
        if plat.any():
            base = seal_walls(j, exclude_wet(j, bridge_wet(j, wet_cells(j, ppu, phys & ~plat))))
            wet = drop_air_backs(j, wet, wet & ~base)
        wsize = write_wet(os.path.join(a.wet, ship + ".bin"), j, wet)
        print(f"{ship}: wet cells={int(wet.sum())} -> {wsize} B")
        ship = j["ship"].replace("(Clone)", "")
        # 手前の絵の下の部屋の絵は床でなくなるが、壁ではない (視界でも絵に隠れて水は這い上がって見えない)
        face = wall_face(canvas, floor, ppu) & ~front
        size = write_mask(os.path.join(a.out, ship + ".bin"), j["sig"], j["solid"]["origin"], ppu, floor, face)
        print(f"{ship}: {floor.shape[1]}x{floor.shape[0]} ppu={ppu} floor={floor.mean():.3f} face={face.mean():.3f} -> {size} B")
        miss = report(j, ppu, floor, walk, furn) if a.report else None
        if a.preview:
            os.makedirs(a.preview, exist_ok=True)
            rgb = canvas[..., :3].copy()
            rgb = rgb * 0.55
            rgb[floor] = rgb[floor] * 0.35 + np.array([0.0, 0.75, 1.0]) * 0.65
            rgb[face] = rgb[face] * 0.4 + np.array([1.0, 0.7, 0.0]) * 0.6
            if miss is not None:
                rgb[miss] = np.array([1.0, 0.1, 0.1])
            rgb[cover & ~floor] = rgb[cover & ~floor] * 0.5 + np.array([0.6, 0.0, 0.8]) * 0.5
            rgb[phys] = rgb[phys] * 0.3 + np.array([0.1, 1.0, 0.2]) * 0.7
            Image.fromarray((np.clip(rgb, 0, 1)[::-1] * 255).astype(np.uint8)).save(os.path.join(a.preview, ship + "_floor.png"))


if __name__ == "__main__":
    main()
