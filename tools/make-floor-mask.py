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
BACK_Z_SHIP = {"PolusShip": 0.5, "FungleShip": 0.5}  # 床の絵が z 1〜4 にある船 (ゲームの FloorMask.BackZ と同じ表)
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
FACE_MAX = 4.0        # 床の縁から真上へこれ (世界単位) までの絵を壁の面とする (水が這い上がる所)

DEFAULT_BUNDLES = [
    r"C:/Program Files/Epic Games/AmongUs/Among Us_Data/StreamingAssets/aa/EGS/StandaloneWindows",
    r"C:/Program Files (x86)/Steam/steamapps/common/Among Us/Among Us_Data/StreamingAssets/aa/Steam/StandaloneWindows",
]


def load_textures(folder, rooms):
    need = {(r["texture"], r["texSize"][0], r["texSize"][1]) for r in rooms}
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
        if r["z"] < j["backZ"] or r["z"] >= SKY_Z or key not in tex:
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
        if r["z"] < j["backZ"] or r["z"] >= SKY_Z:
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


def classify(j, canvas, ppu):
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
    for _ in range(LEARN_PASSES):
        more = learn(j, canvas, ppu, floor, inner & drawn, rgb, lsum, ch, key) & near & drawn & ~floor
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
    return smooth(floor & near & ~deep), walk, furn


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
    ap.add_argument("--preview", default=None)
    ap.add_argument("--report", action="store_true", help="取りこぼし (歩ける所なのに床でない塊) を一覧し、プレビューに赤で出す")
    a = ap.parse_args()
    folder = a.bundles or next((b for b in DEFAULT_BUNDLES if os.path.isdir(b)), None)
    if not folder:
        sys.exit("ゲームの StandaloneWindows フォルダが見つからない (--bundles で指定)")
    os.makedirs(a.out, exist_ok=True)
    for path in a.json:
        j = json.load(open(path, encoding="utf-8"))
        j["backZ"] = BACK_Z_SHIP.get(j["ship"].replace("(Clone)", ""), BACK_Z)
        ds = sorted(abs(r["d"][0]) for r in j["rooms"])
        ppu = round(1.0 / ds[len(ds) // 2], 4)  # 部屋の絵でいちばん多い細かさ
        tex = load_textures(folder, j["rooms"])
        canvas = compose(j, tex, ppu)
        floor, walk, furn = classify(j, canvas, ppu)
        ship = j["ship"].replace("(Clone)", "")
        face = wall_face(canvas, floor, ppu)
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
            Image.fromarray((np.clip(rgb, 0, 1)[::-1] * 255).astype(np.uint8)).save(os.path.join(a.preview, ship + "_floor.png"))


if __name__ == "__main__":
    main()
