"""水を止める絵の画素の地図 (柵・家具の絵) を、ゲームの絵から作る。

使い方:
  python tools/make-water-barrier.py <floor_*.json ...> [--bundles <StandaloneWindows の aa フォルダ>] [--preview <dir>]
  (json はブリッジの `floorexport` が書く物。床マスクと同じ)

出力は Resources/WaterBarrier/<船>.bin (リポジトリには入れない・ビルドで DLL に埋め込む)。
水は縁を越える所の画面の上に、物の絵の不透明な画素があれば止まり、無ければ抜ける (網の目や柵の柱の間は抜ける)。
止まる絵の画素:
  - 手前の絵 (柵・家具・小物) の不透明な所は全部
  - 部屋の絵に描き込まれた手すりは、RAIL_RECTS の範囲の中の、床でない不透明な所。部屋の絵の手すりは崖の下の背景と
    同じ絵の中にあって画素だけでは見分けられないので、範囲は手で与える。床のすぐ脇の暗い画素 (床の縁取りの線) は除く
壁は歩ける所の地図で閉じているので、ここには入れない。細かさは BARRIER_PPU 画素/単位 (柱の太さ 0.05 が 1〜2 画素)。
"""
import argparse
import importlib.util
import os
import struct
import sys
import zlib

import numpy as np

sys.stdout.reconfigure(encoding="utf-8")
HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("make_floor_mask", os.path.join(HERE, "make-floor-mask.py"))
mfm = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mfm)

try:
    from PIL import Image
except ImportError:
    sys.exit("pip install UnityPy pillow numpy scipy")

MAGIC = b"MRPB"
VERSION = 1
BARRIER_PPU = 32      # 出力の細かさ (画素/単位)
OUTLINE_PX = 4        # 床からこの画素 (細かい方の画素) 以内の暗い画素は床の縁取り
SAMPLE = 2            # 1 出力画素を SAMPLE×SAMPLE で調べ、1 つでも絵があれば絵にする (細い柱を取りこぼさない)
# 部屋の絵に描き込まれた手すりの範囲 (x0, y0, x1, y1・世界座標)。柱の間に背景の絵が見える所 (エアシップの換気室の台の北の手すり) は
# 背景まで手すりになるので入れられない。空が透明な所 (Mira) だけ
RAIL_RECTS = {}


def read_floor(ship):
    """床マスク (Resources/FloorMasks/<船>.bin) の床の画素"""
    d = open(os.path.join(HERE, "..", "Resources", "FloorMasks", ship + ".bin"), "rb").read()
    ver, w, h = struct.unpack_from("<iii", d, 4)
    ox, oy, ppu = struct.unpack_from("<fff", d, 16)
    lb = struct.unpack_from("<i", d, 32)[0]
    bits = np.unpackbits(np.frombuffer(zlib.decompress(d[40:40 + lb]), np.uint8), bitorder="little")[: w * h]
    return bits.reshape(h, w).astype(bool), ox, oy, ppu


def sample(src, sox, soy, sppu, ox, oy, ppu, shape):
    """別の細かさの画素の地図 src を、(ox, oy) 原点・ppu の画素の真ん中で引く"""
    h, w = shape
    gx = np.floor((ox + (np.arange(w) + 0.5) / ppu - sox) * sppu).astype(int)
    gy = np.floor((oy + (np.arange(h) + 0.5) / ppu - soy) * sppu).astype(int)
    okx = (gx >= 0) & (gx < src.shape[1])
    oky = (gy >= 0) & (gy < src.shape[0])
    out = np.zeros(shape, bool)
    out[np.ix_(oky, okx)] = src[np.ix_(gy[oky], gx[okx])]
    return out


def build(j, folder):
    ship = j["ship"].replace("(Clone)", "")
    j["backZ"] = mfm.BACK_Z_SHIP.get(ship, mfm.BACK_Z)
    s = j["solid"]
    ox, oy = s["origin"]
    fine = BARRIER_PPU * SAMPLE
    tex = mfm.load_textures(folder, j["rooms"] + j.get("covers", []))
    canvas = mfm.compose(j, tex, fine)
    shape = canvas.shape[:2]
    cover = mfm.cover_mask(j, tex, fine, shape)
    floor, fox, foy, fppu = read_floor(ship)
    floor = sample(floor, fox, foy, fppu, ox, oy, fine, shape)
    rails = np.zeros(shape, bool)
    xs = ox + (np.arange(shape[1]) + 0.5) / fine
    ys = oy + (np.arange(shape[0]) + 0.5) / fine
    for x0, y0, x1, y1 in RAIL_RECTS.get(ship, []):
        rails[np.ix_((ys >= y0) & (ys < y1), (xs >= x0) & (xs < x1))] = True
    lum = canvas[..., :3] @ np.array([0.299, 0.587, 0.114], np.float32)
    rim = (lum < mfm.OUTLINE) & mfm.ndimage.binary_dilation(floor, iterations=OUTLINE_PX)
    room = (canvas[..., 3] > mfm.DRAWN) & rails & ~floor & ~rim
    fine_bar = cover | room
    h, w = shape[0] // SAMPLE, shape[1] // SAMPLE
    bar = fine_bar[: h * SAMPLE, : w * SAMPLE].reshape(h, SAMPLE, w, SAMPLE).any(axis=(1, 3))
    return ship, bar, (ox, oy), (cover, room, floor, canvas)


def write(path, sig, origin, bar):
    h, w = bar.shape
    body = zlib.compress(np.packbits(bar.ravel(), bitorder="little").tobytes(), 9)
    # sig = 床マスクと同じ部屋の絵の指紋 (ゲームの絵が変わったら読まない)
    head = MAGIC + struct.pack("<iiifffI", VERSION, w, h, origin[0], origin[1], float(BARRIER_PPU), sig)
    with open(path, "wb") as fo:
        fo.write(head + struct.pack("<i", len(body)) + body)
    return os.path.getsize(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("json", nargs="+")
    ap.add_argument("--bundles", default=None)
    ap.add_argument("--out", default=os.path.join(HERE, "..", "Resources", "WaterBarrier"))
    ap.add_argument("--preview", default=None)
    a = ap.parse_args()
    folder = a.bundles or next((b for b in mfm.DEFAULT_BUNDLES if os.path.isdir(b)), None)
    if not folder:
        sys.exit("ゲームの StandaloneWindows フォルダが見つからない (--bundles で指定)")
    os.makedirs(a.out, exist_ok=True)
    import json
    for path in a.json:
        j = json.load(open(path, encoding="utf-8"))
        ship, bar, origin, parts = build(j, folder)
        size = write(os.path.join(a.out, ship + ".bin"), j["sig"], origin, bar)
        print(f"{ship}: {bar.shape[1]}x{bar.shape[0]} barrier={bar.mean():.3f} -> {size} B")
        if a.preview:
            os.makedirs(a.preview, exist_ok=True)
            cover, room, floor, canvas = parts
            rgb = canvas[..., :3] * 0.35
            rgb[floor] = rgb[floor] * 0.5 + np.array([0.0, 0.3, 0.8]) * 0.5
            rgb[room] = np.array([1.0, 0.55, 0.0])
            rgb[cover] = np.array([1.0, 1.0, 0.2])
            im = Image.fromarray((np.clip(rgb, 0, 1)[::-1] * 255).astype(np.uint8))
            im.save(os.path.join(a.preview, ship + "_barrier.png"))


if __name__ == "__main__":
    main()
