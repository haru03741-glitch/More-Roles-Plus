"""部屋の絵に描き込まれた家具の跡を埋める床を、ゲームの絵から測って src/Terrain/FurnitureFloors.cs を作る。

使い方:
  1. ゲームでマップに入り、ブリッジで `lifts` を打つ (Screens/lifts_<船>.json ができる)
  2. python tools/measure-room-floors.py <lifts_*.json ...> [--bundle <initialmaps_assets_all.bundle>] [--write] [--preview <dir>]

家具ごとに、周りのきれいな床 (当たり判定から離れた所) で次の床の型を試し、当てはまる割合がいちばん高いものを選ぶ:
  - 縦横に繰り返す模様 (市松・ひし形): 横と縦の繰り返しの幅
  - 縦には変わらない床 (縦じま): 横は同じ列をそのまま引き、縦は短い幅で繰り返す
  - 横には変わらない床 (横じま): 縦は同じ行をそのまま引き、横は短い幅で繰り返す
形を当たり判定から広げる幅は、広げた外側にまだ床でない物が続かない一番広い幅にする (隣に壁や柄の違う床がある家具は狭くなる)。
当てはまる割合が低い家具は SPECIAL と出し、表には載せない (手で測る)。出すのは数字だけで、ゲームの絵は書き出さない
(--preview は確認用の画像を手元の指定の場所へ出すだけ)。
"""
import argparse
import glob
import json
import os
import sys

import numpy as np

try:
    import UnityPy
    from PIL import Image, ImageDraw
except ImportError:
    sys.exit("pip install UnityPy pillow numpy")

TOL = 0.05            # 床の模様との差 (シェーダの _FloorTol と同じ)
PADS = [0.3, 0.22, 0.15, 0.1, 0.06]
PROBE = 0.08          # 広げた外側を見る幅 (世界単位)
PROBE_LIMIT = 0.06    # 外側で床でない画素の割合がこれ未満なら、その幅まで広げてよい
MIN_COVER = 0.97      # 周りのきれいな床のうち模様で言い当てられる割合
CLEAN_GAP = 0.35      # 当たり判定からこれだけ離れた所をきれいな床の候補にする (世界単位)
MIN_T = 8             # これより短い繰り返しは隣の画素が似ているだけ (模様の繰り返しではない)
UNIFORM = 0.02        # どの幅でずらしても食い違いがこれ未満なら、その向きには模様が変わらない
MAX_MISMATCH = 0.12   # 一番合う幅でも食い違いがこれ以上なら、繰り返しは無い
MAX_T = 300           # 探す繰り返しの幅の上限 (画素)

BUNDLES = [
    r"C:/Program Files (x86)/Steam/steamapps/common/Among Us/Among Us_Data/StreamingAssets/aa/Steam/StandaloneWindows/initialmaps_assets_all.bundle",
    r"C:/Program Files/Epic Games/AmongUs/Among Us_Data/StreamingAssets/aa/EGS/StandaloneWindows/initialmaps_assets_all.bundle",
]


def load_sprites(bundle, names):
    env = UnityPy.load(bundle)
    out = {}
    for o in env.objects:
        if o.type.name != "Sprite":
            continue
        d = o.read()
        if d.m_Name in names and d.m_Name not in out:
            img = np.asarray(d.image.convert("RGBA")).astype(np.float32) / 255.0
            out[d.m_Name] = img[::-1]  # 左下原点
    return out


def poly_mask(shape, pts):
    h, w = shape
    im = Image.new("L", (w, h), 0)
    ImageDraw.Draw(im).polygon([(x, h - 1 - y) for x, y in pts], fill=1)
    return np.asarray(im)[::-1].astype(bool)


def dilate(mask, r):
    """半径 r 画素の円で太らせる"""
    r = int(np.ceil(r))
    if r <= 0:
        return mask.copy()
    out = mask.copy()
    ys, xs = np.mgrid[-r:r + 1, -r:r + 1]
    for dy, dx in zip(ys.ravel(), xs.ravel()):
        if dx * dx + dy * dy > r * r or (dx == 0 and dy == 0):
            continue
        out |= np.roll(np.roll(mask, dy, 0), dx, 1)
    return out


def bilinear(img, x, y):
    h, w = img.shape[:2]
    x = np.clip(x - 0.5, 0, w - 1.001)
    y = np.clip(y - 0.5, 0, h - 1.001)
    x0 = np.floor(x).astype(int)
    y0 = np.floor(y).astype(int)
    fx = (x - x0)[..., None]
    fy = (y - y0)[..., None]
    return (img[y0, x0] * (1 - fx) * (1 - fy) + img[y0, x0 + 1] * fx * (1 - fy)
            + img[y0 + 1, x0] * (1 - fx) * fy + img[y0 + 1, x0 + 1] * fx * fy)


def predict(img, xs, ys, model):
    """model = (x0, y0, tx, ty)。tx/ty = 0 はその向きは同じ列/行"""
    x0, y0, tx, ty = model
    qx = xs + 0.5
    qy = ys + 0.5
    sx = x0 + np.mod(qx - x0, tx) if tx > 0 else qx
    sy = y0 + np.mod(qy - y0, ty) if ty > 0 else qy
    return bilinear(img[..., :3], sx, sy)


def miss(img, xs, ys, model):
    p = predict(img, xs, ys, model)
    return np.abs(p - img[ys, xs, :3]).max(axis=1) >= TOL


def shift_error(img, clean, dx, dy):
    """床の候補を (dx, dy) ずらして重ねた時に、床の模様として食い違う画素の割合 (重なりが少なすぎる時は None)"""
    h, w = clean.shape
    if abs(dx) >= w - 20 or abs(dy) >= h - 20:
        return None
    a = clean[max(0, -dy):h - max(0, dy), max(0, -dx):w - max(0, dx)]
    b = clean[max(0, dy):max(0, dy) + a.shape[0], max(0, dx):max(0, dx) + a.shape[1]]
    m = a & b
    if m.sum() < 200:
        return None
    ia = img[max(0, -dy):max(0, -dy) + a.shape[0], max(0, -dx):max(0, -dx) + a.shape[1], :3]
    ib = img[max(0, dy):max(0, dy) + a.shape[0], max(0, dx):max(0, dx) + a.shape[1], :3]
    return float((np.abs(ia - ib).max(axis=2)[m] >= TOL).mean())


def best_period(img, clean, axis):
    """axis 0 = 横 (dx)・1 = 縦 (dy)。食い違いの小さい一番短い幅 (画素・小数)。模様が変わらない向きは 1・無ければ None"""
    def err(t):
        return shift_error(img, clean, t, 0) if axis == 0 else shift_error(img, clean, 0, t)
    errs = np.array([np.nan if e is None else e for e in (err(t) for t in range(1, MAX_T + 1))])
    if np.all(np.isnan(errs)):
        return None
    if np.nanmax(errs) < UNIFORM:
        return 1.0
    errs = np.where(np.isnan(errs), 1.0, errs)
    tail = errs[MIN_T - 1:]
    floor = tail.min()
    if floor > MAX_MISMATCH:
        return None
    t = int(np.argmax(tail < floor * 1.5 + 0.01)) + MIN_T
    # 小数の幅: n 倍ずらした所でいちばん合う整数の幅 / n (3 倍が範囲の外なら 2 倍。取れなければ整数のまま)
    for n in (3, 2):
        found = False
        best, bt = errs[t - 1], float(t)
        for k in range(t * n - n, t * n + n + 1):
            e = err(k)
            if e is None:
                continue
            found = True
            if e < best - 1e-4:
                best, bt = e, k / float(n)
        if found:
            return bt
    return float(t)


def floorish(img, clean, top=6, share=0.85, near=0.08, apply_to=None):
    """きれいな床の候補 (clean) でよく使われている色 (床の色) を選び、apply_to (既定 clean) のうちその色に近い画素だけを返す
    (壁・ほかの家具のはみ出しを外す)"""
    rgb = img[..., :3]
    q = np.clip((rgb * 15).round().astype(int), 0, 15)
    key = q[..., 0] * 256 + q[..., 1] * 16 + q[..., 2]
    ks, counts = np.unique(key[clean], return_counts=True)
    order = np.argsort(-counts)
    picked, total = [], counts.sum()
    acc = 0
    for i in order[:top]:
        picked.append(ks[i])
        acc += counts[i]
        if acc >= total * share:
            break
    cols = np.array([rgb[clean & (key == k)].mean(axis=0) for k in picked])
    dist = np.min(np.abs(rgb[..., None, :] - cols[None, None, :, :]).max(axis=3), axis=2)
    return (clean if apply_to is None else apply_to) & (dist < near)


def refine(img, model, ys, xs):
    """繰り返しの幅を ±1.5 画素の範囲で 0.05 刻みに動かし、家具の近くの床をいちばん多く言い当てる幅にする (横 → 縦の順)"""
    x0, y0, tx, ty = model
    if len(xs) == 0:
        return model
    def score(m):
        return miss(img, xs, ys, m).mean()
    best = score(model)
    for axis in (0, 1):
        t = tx if axis == 0 else ty
        if t <= 1:
            continue
        for c in np.arange(t - 1.5, t + 1.501, 0.05):
            m = (x0, y0, c, ty) if axis == 0 else (x0, y0, tx, c)
            e = score(m)
            if e < best - 1e-5:
                best = e
                if axis == 0:
                    tx = float(c)
                else:
                    ty = float(c)
    return (x0, y0, round(tx, 2), round(ty, 2))


def find_patch(clean, tx, ty, x_range, y_range, cx, cy):
    """幅 tx × ty (0 = 家具の範囲いっぱい) の全部きれいな四角のうち家具に近いもの (左下)"""
    h, w = clean.shape
    pw = int(np.ceil(tx)) + 1 if tx > 0 else x_range[1] - x_range[0]
    ph = int(np.ceil(ty)) + 1 if ty > 0 else y_range[1] - y_range[0]
    if pw >= w or ph >= h:
        return None
    ii = np.pad(clean.astype(np.int64), ((1, 0), (1, 0))).cumsum(0).cumsum(1)
    full = (ii[ph:, pw:] - ii[:-ph, pw:] - ii[ph:, :-pw] + ii[:-ph, :-pw]) == pw * ph  # [y, x] = 左下 (x, y) の四角が全部きれい
    if tx <= 0:
        keep = np.zeros_like(full)
        keep[:, x_range[0]] = full[:, x_range[0]]
        full = keep
    if ty <= 0:
        keep = np.zeros_like(full)
        keep[y_range[0], :] = full[y_range[0], :]
        full = keep
    ys, xs = np.nonzero(full)
    if len(xs) == 0:
        return None
    dist = (xs + pw / 2 - cx) ** 2 + (ys + ph / 2 - cy) ** 2
    i = int(np.argmin(dist))
    return int(xs[i]), int(ys[i])


def measure(entry, img, others):
    d = entry["d"]
    ppu = 1.0 / abs(d[0])
    tr = entry["texRect"]
    w0 = entry["w0"]
    segs = entry["segs"]
    pts = [((p[0] - w0[0]) / d[0] - tr[0], (p[1] - w0[1]) / d[1] - tr[1]) for p in segs[0::2]]
    h, w = img.shape[:2]
    col = poly_mask((h, w), pts)
    opaque = img[..., 3] > 0.5
    others_mask = np.zeros_like(col)
    for o in others:
        others_mask |= poly_mask((h, w), o)
    xs_c = [p[0] for p in pts]
    ys_c = [p[1] for p in pts]
    bx0, bx1, by0, by1 = min(xs_c), max(xs_c), min(ys_c), max(ys_c)
    cx, cy = (bx0 + bx1) / 2, (by0 + by1) / 2
    m = int((max(PADS) + 1.2) * ppu)
    region = np.zeros_like(col)
    region[max(0, int(by0) - m):min(h, int(by1) + m), max(0, int(bx0) - m):min(w, int(bx1) + m)] = True
    clean = region & opaque & ~dilate(col | others_mask, CLEAN_GAP * ppu)
    if clean.sum() < 500:
        return {"special": f"no clean floor nearby ({int(clean.sum())} px)"}
    free = opaque & ~dilate(col | others_mask, CLEAN_GAP * ppu)     # 部屋全体の家具でない所
    # 繰り返しの幅を探す用: 部屋全体の、この家具の周りでよく使われている色にごく近い床 (落ちた影なども外す)
    strict = floorish(img, clean, top=3, share=0.6, near=0.03, apply_to=free)
    room_floor = floorish(img, clean, apply_to=free)               # 部屋全体の、この家具の周りと同じ色の床
    clean = floorish(img, clean)

    pad_px = max(PADS) * ppu
    x_range = (max(0, int(bx0 - pad_px) - 2), min(w, int(bx1 + pad_px) + 3))
    y_range = (max(0, int(by0 - pad_px) - 2), min(h, int(by1 + pad_px) + 3))
    cands = []
    tx = best_period(img, strict, 0)
    ty = best_period(img, strict, 1)
    if tx and ty:
        cands.append(("grid", tx, ty))
    if ty:
        cands.append(("same-column", 0.0, ty))
    if tx:
        cands.append(("same-row", tx, 0.0))
    near = region & opaque
    nys, nxs = np.nonzero(near)
    fit_ys, fit_xs = np.nonzero(clean & dilate(col, 0.6 * ppu))
    rings = [(p, dilate(col, p * ppu), dilate(col, (p + PROBE) * ppu)) for p in PADS]
    best = None
    for kind, mtx, mty in cands:
        # 繰り返す向きはきれいな床の四角・そのまま引く向きは家具でない帯 (壁も同じ向きに変わらないので入ってよい)
        at = find_patch(room_floor if kind == "grid" else free, mtx, mty, x_range, y_range, cx, cy)
        if at is None:
            continue
        model = refine(img, (at[0], at[1], mtx, mty), fit_ys, fit_xs)
        bad = np.zeros_like(col)
        bad[nys, nxs] = miss(img, nxs, nys, model)
        # 広げる幅: 外側の帯にまだ床でない物が続かない一番広い幅。当てはまりはその幅の外側の帯までの床で測る
        for p, inner, outer_full in rings:
            outer = outer_full & ~inner & opaque
            if os.environ.get("MRP_FLOOR_DEBUG"):
                print(f"   {entry['name']} {kind} t=({mtx:.1f},{mty:.1f}) pad={p} probe={bad[outer].mean() if outer.sum() else -1:.3f}")
            if outer.sum() == 0 or bad[outer].mean() >= PROBE_LIMIT:
                continue
            judged = clean & outer_full
            cover = 1.0 - bad[judged].mean() if judged.sum() > 0 else 0.0
            score = (cover >= MIN_COVER, p, cover)
            if best is None or score > best[0]:
                best = (score, kind, model, p, cover, bad)
            break
    if best is None:
        return {"special": f"no repeating floor found (tx={tx} ty={ty} strict={int(strict.sum())} px)"}
    _, kind, model, pad, cover, bad = best
    result = {"kind": kind, "x": model[0], "y": model[1], "tx": model[2], "ty": model[3], "cover": cover, "pad": pad}
    if cover < MIN_COVER:
        result["special"] = f"floor cover {cover:.3f} < {MIN_COVER}"
    result["_col"] = col
    result["_ppu"] = ppu
    result["_bad"] = bad
    return result


def preview(path, img, r):
    pad = r.get("pad") or min(PADS)
    shape = dilate(r["_col"], pad * r["_ppu"])
    cut = shape & r["_bad"]
    rgb = (img[..., :3] * 255).astype(np.uint8).copy()
    rgb[cut] = (rgb[cut] * 0.4 + np.array([255, 0, 0]) * 0.6).astype(np.uint8)
    edge = shape & dilate(~shape, 1)
    rgb[edge] = [0, 255, 0]
    ys, xs = np.nonzero(shape)
    y0, y1, x0, x1 = max(0, ys.min() - 30), ys.max() + 30, max(0, xs.min() - 30), xs.max() + 30
    Image.fromarray(rgb[y0:y1, x0:x1][::-1]).save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("lifts", nargs="+")
    ap.add_argument("--bundle")
    ap.add_argument("--write", action="store_true", help="src/Terrain/FurnitureFloors.cs を書き換える")
    ap.add_argument("--preview", help="確認用の画像を出す場所")
    a = ap.parse_args()
    bundle = a.bundle or next((b for b in BUNDLES if os.path.exists(b)), None)
    if not bundle:
        sys.exit("bundle not found (--bundle)")
    entries = []
    for pat in a.lifts:
        for f in glob.glob(pat):
            entries += json.load(open(f, encoding="utf-8"))
    sprites = load_sprites(bundle, {e["room"] for e in entries})
    table = {}
    for e in entries:
        img = sprites.get(e["room"])
        key = e["room"] + "/" + e["name"]
        if img is None:
            print(f"SPECIAL {key}: room art not found")
            continue
        others = []
        for o in entries:
            if o is e or o["room"] != e["room"]:
                continue
            d, tr, w0 = o["d"], o["texRect"], o["w0"]
            others.append([((p[0] - w0[0]) / d[0] - tr[0], (p[1] - w0[1]) / d[1] - tr[1]) for p in o["segs"][0::2]])
        r = measure(e, img, others)
        if a.preview and "_col" in r:
            os.makedirs(a.preview, exist_ok=True)
            preview(os.path.join(a.preview, key.replace("/", "__").replace(" ", "_") + ".png"), img, r)
        if "special" in r:
            print(f"SPECIAL {key}: {r['special']}" + (f" (kind={r.get('kind')} cover={r.get('cover', 0):.3f} pad={r.get('pad')})" if "kind" in r else ""))
            continue
        print(f"OK {key}: {r['kind']} patch=({r['x']},{r['y']}) t=({r['tx']:.2f},{r['ty']:.2f}) cover={r['cover']:.4f} pad={r['pad']}")
        table[key] = r
    if a.write:
        write_table(table)


def write_table(table):
    path = os.path.join(os.path.dirname(__file__), "..", "src", "Terrain", "FurnitureFloors.cs")
    lines = [
        "using System.Collections.Generic;",
        "",
        "namespace MoreRolesPlus.Terrain;",
        "",
        "// 部屋の絵に描き込まれた家具の跡を埋める床の測り方 (FurnitureLift.Floor)。tools/measure-room-floors.py が作る。",
        "// キー = 部屋の絵の名前/当たり判定の名前 (家具ごと) か 部屋の絵の名前 (部屋の全部の家具)",
        "// 道具が測れなかった家具は FurnitureFloorsManual.cs (手で測った表) にある",
        "internal static partial class FurnitureFloors",
        "{",
        "    internal static readonly Dictionary<string, FurnitureLift.Floor> Table = new()",
        "    {",
    ]
    for key in sorted(table):
        r = table[key]
        lines.append(f'        ["{key}"] = new FurnitureLift.Floor({r["x"]}f, {r["y"]}f, {r["tx"]:.2f}f, {r["ty"]:.2f}f, {r["pad"]}f),')
    lines += ["    };", "}", ""]
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))
    print(f"wrote {os.path.normpath(path)} ({len(table)} entries)")


if __name__ == "__main__":
    main()
