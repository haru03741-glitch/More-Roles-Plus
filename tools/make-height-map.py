# 水の床の高さ (同じ段の中の坂) と海を、マップ全体の絵 (bridge `paintbase 1` の paintbase.ppm) から作り、
# Resources/Heights/<船>.bin に書く。確認用に高さを色で重ねた絵も書く。
#   使い方: python tools/make-height-map.py fungle <paintbase.ppm> [--preview out.png]
# 形式 (リトルエンディアン): "MRPH" / 版 u8 / 原点 x,y i16 (1/16 単位) / 升 u8 (1/16 単位) / 幅・高さ u16 /
#   升ごとに i8 (下の行から): 高さ (1/32 単位・水の深さと同じ物差し) / -128 = 海
import os, struct, sys
import numpy as np
from PIL import Image
from scipy import ndimage

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PPU = 16          # paintbase の 1 単位あたりの画素
CELL_PX = 4       # 升 = 1/4 単位
UNIT = 32         # 高さの 1 単位あたりの値
SEA = -128


def fungle(img):
    ox, oy = -30.0, -22.0
    h, w = img.shape[:2]
    cw, ch = w // CELL_PX, h // CELL_PX
    # 升の真ん中の世界座標 (ppm は上の行から = 下の行が y の小さい側)
    cx = ox + (np.arange(cw) + 0.5) * CELL_PX / PPU
    cy = oy + (np.arange(ch) + 0.5) * CELL_PX / PPU
    X, Y = np.meshgrid(cx, cy)
    flip = img[::-1]
    blocks = flip[:ch * CELL_PX, :cw * CELL_PX].reshape(ch, CELL_PX, cw, CELL_PX, 3).astype(np.int16)
    # 海 = 絵の海の色 (夕焼けの単色) が升の半分以上
    seacol = np.array([243, 187, 84])
    is_sea_px = (np.abs(blocks - seacol).sum(axis=4) <= 12)
    sea = is_sea_px.sum(axis=(1, 3)) >= CELL_PX * CELL_PX // 2
    # 地図の外まで続く海だけ (島の中の同じ色の点は海にしない)
    lab, _ = ndimage.label(sea)
    edge = set(np.unique(np.concatenate([lab[0], lab[-1], lab[:, 0], lab[:, -1]]))) - {0}
    sea = np.isin(lab, list(edge))
    # 地面: 海から離れるほど高い (浜へゆるく下る)
    dist = ndimage.distance_transform_edt(~sea) * CELL_PX / PPU
    bed = np.minimum(0.6, 0.15 + 0.12 * dist)
    # 建物の中は平ら (部屋の範囲の平均)
    rooms = {
        'Cafeteria': (-19.83, 3.80, -13.04, 8.27), 'Kitchen': (-18.98, -10.51, -12.26, -5.64),
        'RecRoom': (-20.16, -2.94, -14.12, 1.18), 'MeetingRoom': (-5.94, -4.27, -0.15, 0.58),
        'SleepingQuarters': (-0.20, -2.97, 3.93, 0.03), 'Storage': (-1.70, 2.80, 3.94, 7.73),
        'Laboratory': (-6.60, -11.50, -1.93, -8.07), 'Greenhouse': (7.03, -13.42, 11.46, -9.43),
        'Reactor': (19.45, -10.18, 25.06, -5.25),
    }
    for (x0, y0, x1, y1) in rooms.values():
        m = (X >= x0) & (X <= x1) & (Y >= y0) & (Y <= y1)
        bed[m] = bed[m].mean()
    # 桟橋: 海の少し上で平ら
    m = (X >= -24.33) & (X <= -18.87) & (Y >= -8.35) & (Y <= -6.70)
    bed[m] = 0.25
    # ドロップシップへの坂: 南の口 (y 7.5) から北 (y 11) へ 0.5 上る
    m = (X >= -12.02) & (X <= -3.60) & (Y >= 7.5)
    bed[m] = 0.6 + np.clip((Y[m] - 7.5) / 3.5, 0, 1) * 0.5
    # 山の上 (台地・中段・通信室) は段ごとに平ら。鉱山は台地の扉 (y 6.3) から 0.4 上る
    m = (X >= 4.5) & (Y >= -1.6)
    bed[m] = 0.0
    m = (X >= 10.02) & (X <= 15.70) & (Y >= 6.0) & (Y <= 10.97)
    bed[m] = np.clip((Y[m] - 6.0) / 1.5, 0, 1) * 0.4
    v = np.clip(np.round(bed * UNIT), -127, 127).astype(np.int8)
    v[sea] = SEA
    return 'FungleShip', ox, oy, v


MAPS = {'fungle': fungle}


def preview(img, v, path):
    out = img.astype(np.float32).copy()
    h = img.shape[0]
    ch, cw = v.shape
    for y in range(ch):
        r0 = h - (y + 1) * CELL_PX
        for x in range(cw):
            c0 = x * CELL_PX
            val = int(v[y, x])
            if val == SEA:
                col, a = (0, 60, 200), 0.45
            else:
                t = max(0.0, min(1.0, val / (1.2 * UNIT)))
                col, a = (255 * t, 255 * (1 - abs(t - 0.5) * 2) * 0.9 + 40, 255 * (1 - t)), 0.35
            blk = out[max(0, r0):r0 + CELL_PX, c0:c0 + CELL_PX]
            blk[:] = blk * (1 - a) + np.array(col) * a
    Image.fromarray(out.clip(0, 255).astype(np.uint8)).save(path)


def main():
    if len(sys.argv) < 3 or sys.argv[1] not in MAPS:
        print('usage: make-height-map.py <' + '|'.join(MAPS) + '> <paintbase.ppm> [--preview out.png]')
        return 1
    img = np.array(Image.open(sys.argv[2]).convert('RGB'))
    name, ox, oy, v = MAPS[sys.argv[1]](img)
    ch, cw = v.shape
    path = os.path.join(ROOT, 'Resources', 'Heights', name + '.bin')
    with open(path, 'wb') as f:
        f.write(b'MRPH' + struct.pack('<BhhBHH', 1, int(round(ox * PPU)), int(round(oy * PPU)), CELL_PX, cw, ch))
        f.write(v.tobytes())
    land = v[v != SEA]
    print(f'{name}: {cw}x{ch} sea={int((v == SEA).sum())} bed min={land.min() / UNIT:.2f} max={land.max() / UNIT:.2f} -> {path}')
    if '--preview' in sys.argv:
        preview(img, v, sys.argv[sys.argv.index('--preview') + 1])
    return 0


if __name__ == '__main__':
    sys.exit(main())
