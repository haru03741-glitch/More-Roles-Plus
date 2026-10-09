# Based on https://github.com/waffle-ful/Aeterna-End-K-not (役職演出の効果音の生成・GPL-3.0)
# 壁を壊した音を合成して unity/MrpBundle/Assets/Generated/noise_*.wav に書き出す (tools/build-bundle.ps1 が焼く前に呼ぶ)。
#   noise_boom              … 爆発
#   noise_hit_<素材>        … 叩いた (まだ壊れない)。素材 = metal (船体) / stone (岩・コンクリート) / wood (木)
#   noise_crumble_<素材>    … 叩いて崩れた
#   noise_fuse              … 置いた爆弾の導火線 (爆発まで)
#   noise_rubble_<素材>     … 爆発で壁が崩れ落ちる音 (爆発の音に少し遅れて重ねる。壁に当たらない爆発では鳴らさない)
#   noise_leak              … 壊れた配管から水が噴き出す音 (約 1 秒ごとに重ねて鳴らす。頭と尻は長めに薄れる)
#   noise_splash_1..3       … 水たまりを踏んだ音
#   noise_decomp_breach     … 外壁に穴が開いた瞬間 (金属が裂けて圧が抜ける)
#   noise_decomp_loop       … 穴から吸い出される持続音 (継ぎ目なしのループ)
#   noise_decomp_whistle    … 泡で口が狭まる時の笛 (継ぎ目なしのループ・高さは鳴らす側で変える)
#   noise_decomp_seal       … 口がふさがった最後の「ぷしゅっ」
#   noise_bump_small_1..3   … 小物が壁や床に当たる
#   noise_bump_heavy_1..2   … 重い家具が壁に当たる
#   noise_bump_clash_1..2   … 家具どうしがぶつかる
#   noise_fire_loop         … 燃えている火 (継ぎ目なしのループ・ごうごうと鳴る炎とぱちぱちはぜる音)
#   noise_fire_ignite       … 火が付いた瞬間 (ぼっ)
#   noise_steam             … 火に水が掛かって湯気になる (じゅうっ)
#   noise_flare             … 油の火に水が掛かって噴き上がる
#   noise_arc               … 濡れた配線の火花 (ばちばち)
#   それぞれ <名前>_m       … 遠い・壁越しのこもった音 (16kHz)
# 32kHz / 16bit / mono。1 本ごとに乱数を種から引き直すので、生成の順に依らず同じ音になる。
#   python tools/make-break-sounds.py   (numpy と scipy が要る)
import os
import wave
import zlib

import numpy as np
from scipy.signal import butter, fftconvolve, resample_poly, sawtooth, sosfilt

SR = 32000
OUT = os.path.join(os.path.dirname(__file__), '..', 'unity', 'MrpBundle', 'Assets', 'Generated')
rng = np.random.default_rng(0)


def reseed(name):
    global rng
    rng = np.random.default_rng(20261007 + zlib.crc32(name.encode()))


def T(dur):
    n = int(SR * dur)
    return n, np.arange(n) / SR


def noise(n):
    return rng.standard_normal(n)


def bp(x, lo, hi, order=2):
    return sosfilt(butter(order, [lo, min(hi, SR * 0.45)], 'bp', fs=SR, output='sos'), x)


def lp(x, f, order=2):
    return sosfilt(butter(order, f, 'lp', fs=SR, output='sos'), x)


def hp(x, f, order=2):
    return sosfilt(butter(order, f, 'hp', fs=SR, output='sos'), x)


def sweep(t, f0, f1, tau):
    # f0 から f1 へ指数的に寄る周波数の正弦の位相
    f = f1 + (f0 - f1) * np.exp(-t / tau)
    return 2 * np.pi * np.cumsum(f) / SR


def place(dst, src, at, gain=1.0):
    i = int(at * SR)
    if i >= len(dst):
        return
    m = min(len(src), len(dst) - i)
    dst[i:i + m] += src[:m] * gain


def fade(x, a=0.002, r=0.04):
    n = len(x)
    e = np.ones(n)
    na, nr = int(a * SR), int(r * SR)
    e[:na] = np.linspace(0, 1, na)
    e[n - nr:] = np.linspace(1, 0, nr)
    return x * e


# ── 部品 ───────────────────────────────────────────────────

def grains(n_out, rate, f_lo, f_hi, dur=0.012, tau=0.003, t0=0.0, t1=None, shape=None):
    # 細かい粒 (砂・火花・ひび) を不規則に撒く。shape(u) は 0〜1 の進み具合に対する粒の強さ
    out = np.zeros(n_out)
    t1 = t1 if t1 is not None else n_out / SR
    at = t0
    m = int(dur * SR)
    tm = np.arange(m) / SR
    while at < t1:
        u = (at - t0) / max(1e-6, t1 - t0)
        g = shape(u) if shape else 1.0
        place(out, bp(noise(m), f_lo, f_hi) * np.exp(-tm / tau), at, g * rng.uniform(0.5, 1.0))
        at += rng.exponential(1.0 / rate)
    return out


def debris(n, t0, t1, f_lo=300, f_hi=3500, rate=45):
    # 破片や土がばらばら落ちる音
    return grains(n, rate, f_lo, f_hi, dur=0.035, tau=0.009, t0=t0, t1=t1, shape=lambda u: (1 - u) ** 1.5)


def impact(dur=1.0, weight=1.0):
    # 重い一撃: 鋭い立ち上がり (高域の破裂) + 胴 (中低域) + 腹に来る低音。低音だけにしないで全帯域で鳴らす
    n, t = T(dur)
    snap = hp(noise(n), 2500) * np.exp(-t / 0.012)
    body = bp(noise(n), 90, 1200) * np.exp(-t / (0.09 * weight))
    low = np.sin(sweep(t, 140, 38, 0.07)) * np.exp(-t / (0.28 * weight))
    rumble = lp(noise(n), 180) * np.exp(-t / (0.35 * weight)) * 2.0
    return np.tanh(1.8 * (0.9 * snap + 1.2 * body + 1.0 * low + 0.6 * rumble))


def sub_hit(dur=0.9, f0=95, f1=36, tau=0.09, decay=0.3):
    # 腹に来る下降する正弦 + ごく短い打撃
    n, t = T(dur)
    body = np.sin(sweep(t, f0, f1, tau)) * np.exp(-t / decay)
    knock = lp(noise(n), 500) * np.exp(-t / 0.015)
    return np.tanh(1.6 * (body + 0.5 * knock))


def braam(f, dur, decay=0.5):
    # 低い金管のような厚い唸り。のこぎり波を重ねて強く歪ませる
    n, t = T(dur)
    x = np.zeros(n)
    for mult, g in [(1, 1.0), (2, 0.7), (3, 0.4), (1.5, 0.3)]:
        for d in (-0.008, 0.0, 0.008):
            x += g * sawtooth(2 * np.pi * f * mult * (1 + d) * t + rng.uniform(0, 6.28))
    x = np.tanh(3.0 * lp(x, 1400) / 4)
    x = lp(x, 2600) + 0.35 * bp(x, 700, 2400)
    return x * np.minimum(1, t / 0.02) * np.exp(-t / decay)


def riser(dur, f_lo=200, f_hi=6000, power=2.5):
    # 一瞬吸い込む風: 帯域が上がりながら大きくなる
    n, t = T(dur)
    u = t / dur
    x = noise(n)
    out = np.zeros(n)
    w = np.zeros(n)
    seg = 8
    for i in range(seg):
        fc = f_lo * (f_hi / f_lo) ** (i / (seg - 1))
        y = bp(x, fc * 0.6, fc * 1.6)
        c = (i + 0.5) / seg * n
        win = np.exp(-0.5 * ((np.arange(n) - c) / (n / seg * 0.7)) ** 2)
        out += y * win
        w += win
    return out / np.maximum(w, 1e-6) * u ** power


def fire_bed(dur, tau):
    # 炎の轟き: 低くうねる雑音 + ぱちぱちはぜる粒
    n, t = T(dur)
    roar = bp(noise(n), 120, 2200) * (0.55 + 0.45 * lp(noise(n), 18) * 9).clip(0.1, 1.6)
    crack = grains(n, 55, 1800, 7000, dur=0.01, tau=0.0025, t0=0.0, t1=dur)
    return (roar + 0.6 * crack) * np.exp(-t / tau)


def jet(x, d0=0.0005, d1=0.004, rate=0.2, phase=0.0, mix=0.9):
    # 遅れ時間がゆっくり動く写しを重ねて「ゴォォ」とうねらせる
    n = len(x)
    t = np.arange(n) / SR
    d = (d0 + (d1 - d0) * (0.5 + 0.5 * np.sin(2 * np.pi * rate * t + phase))) * SR
    idx = np.arange(n) - d
    i0 = np.floor(idx).astype(int)
    fr = idx - i0
    i0 = np.clip(i0, 0, n - 2)
    return x + mix * (x[i0] * (1 - fr) + x[i0 + 1] * fr)


def roll(dur, bumps, tau, lo=40, hi=260, t0=0.0):
    # 転がる地鳴り: 低い雑音に不規則なふくらみを重ねる
    n, t = T(dur)
    env = np.zeros(n)
    for _ in range(bumps):
        c = t0 + rng.exponential(dur * 0.25)
        w = rng.uniform(0.05, 0.18)
        env += rng.uniform(0.4, 1.0) * np.exp(-0.5 * ((t - c) / w) ** 2)
    env = (0.35 + env) * np.exp(-t / tau)
    return bp(noise(n), lo, hi) * env * 3


def clank(f=420, dur=0.35, decay=0.12):
    # 金属の打音: 整数比でない倍音の束 + 打撃
    n, t = T(dur)
    x = sum(g * np.sin(2 * np.pi * f * r * t + ph) * np.exp(-t / (decay / r ** 0.5)) for r, g, ph in
            [(1, 1.0, 0), (1.47, 0.8, 1), (2.09, 0.7, 2), (2.83, 0.5, 3), (3.61, 0.4, 4), (5.13, 0.3, 5)])
    return np.tanh(1.5 * x) + 1.2 * bp(noise(n), 200, 3000) * np.exp(-t / 0.015)


def wood_knock(f=900, dur=0.08):
    # 木の打音
    n, t = T(dur)
    body = sum(g * np.sin(2 * np.pi * f * r * t) for r, g in [(1, 1.0), (1.9, 0.5), (2.7, 0.3)]) * np.exp(-t / 0.02)
    return body + 0.8 * bp(noise(n), f * 0.8, f * 4) * np.exp(-t / 0.006)


def crack(dur=0.25, lo=1500, hi=9000):
    # 割れる: 鋭いはぜの連打が一気に走る
    n, _ = T(dur)
    return grains(n, 260, lo, hi, dur=0.006, tau=0.0015, t0=0.0, t1=dur * 0.6, shape=lambda u: 1 - u)


# ── 仕上げ ─────────────────────────────────────────────────

def master(x, drive=2.2, ratio=0.45):
    # 小さい所を持ち上げる圧縮 → 軽い歪みで頭を丸める。同じ最大値でも体感の音量が上がる
    x = x / (np.max(np.abs(x)) + 1e-9)
    env = np.sqrt(lp(x * x, 25, order=1).clip(1e-6, None))
    env = np.maximum(env, 0.05)
    y = x / env ** ratio
    y = y / (np.max(np.abs(y)) + 1e-9)
    return np.tanh(drive * y) / np.tanh(drive)


def muffled(x):
    # 壁越し・遠く: 500Hz より上を強く落とす
    return resample_poly(lp(x, 500, order=3), 1, 2)


def save(name, x, sr, peak):
    x = x / (np.max(np.abs(x)) + 1e-9) * peak
    path = os.path.abspath(os.path.join(OUT, name + '.wav'))
    with wave.open(path, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes((x * 32767).astype(np.int16).tobytes())
    print(f'{name:24s} {len(x) / sr:5.2f}s')


def emit(name, make, peak=0.95, drive=2.2, ratio=0.45):
    reseed(name)
    x = fade(master(fade(make()), drive, ratio))
    save(name, x, SR, peak)
    save(name + '_m', muffled(x), SR // 2, peak * 0.75)


# ── 音 ─────────────────────────────────────────────────────

def boom():
    # 爆発 (2.0s): 一瞬吸い込む → 全帯域の一撃と火球の膨張 → 誘爆 2 つ → 遠くへ地鳴りが転がる。
    # 壁が崩れ落ちる音は素材ごとの noise_rubble_* を別に重ねる
    dur = 2.0
    n, t = T(dur)
    out = np.zeros(n)
    place(out, riser(0.06, 800, 6000, 2.0), 0.0, 0.5)
    h = 0.06
    place(out, impact(1.6, 1.9), h, 2.4)
    m, tm = T(0.2)
    place(out, hp(noise(m), 1800) * np.exp(-tm / 0.025), h, 1.7)
    place(out, sub_hit(1.2, 120, 30, 0.08, 0.45), h, 1.4)
    place(out, braam(41.2, 1.4, 0.45), h, 1.0)
    place(out, braam(61.7, 0.9, 0.3), h, 0.5)
    fb = jet(fire_bed(1.6, 0.5), 0.0006, 0.005, 1.6, 0.0, 0.8)
    place(out, fb * np.minimum(1, T(1.6)[1] / 0.05), h + 0.03, 0.9)
    place(out, crack(0.3), h + 0.02, 0.8)
    for at, g in [(0.3, 0.8), (0.52, 0.6)]:
        place(out, impact(0.6, 0.6), at, g)
    out += roll(dur, 6, 0.7, t0=0.2) * 0.8
    out += 0.2 * debris(n, 0.25, 1.4)  # 爆風で舞った細かい物
    return out


# 叩いた音は素材ごとに「芯の一撃」と「響き」を変える。崩れた音は叩いた音に崩れ落ちる物の雨を足す
def hit_core(weight):
    n, _ = T(0.9)
    out = np.zeros(n)
    place(out, impact(0.8, weight), 0.0, 1.8)
    place(out, sub_hit(0.6, 130, 45, 0.04, 0.16 * weight), 0.0, 0.9)
    return out


def hit_metal():
    # 船体を打つ: 重い一撃 + 低く唸る鉄板の響き + 高い金属の鳴り
    out = hit_core(1.0)
    place(out, clank(rng.uniform(150, 190), 0.9, 0.35), 0.0, 0.9)
    place(out, clank(rng.uniform(600, 760), 0.4, 0.1), 0.0, 0.45)
    return out


def hit_stone():
    # 岩を打つ: 硬く乾いた一撃 + ひびが走る + 細かい石が散る
    out = hit_core(0.8)
    n = len(out)
    place(out, crack(0.25, 1200, 7000), 0.005, 0.9)
    out += 0.5 * grains(n, 70, 900, 5000, dur=0.015, tau=0.004, t0=0.03, t1=0.45, shape=lambda u: 1 - u)
    return out


def hit_wood():
    # 木を打つ: 鈍い胴鳴りの一撃 + 板がきしんで裂ける
    out = hit_core(0.9)
    n = len(out)
    place(out, wood_knock(rng.uniform(180, 240), 0.25) * 1.2, 0.0, 1.0)
    place(out, wood_knock(rng.uniform(520, 700), 0.12), 0.0, 0.6)
    out += 0.45 * grains(n, 120, 1500, 6000, dur=0.008, tau=0.002, t0=0.01, t1=0.2, shape=lambda u: 1 - u)
    return out


# ── 崩れる音の部品 (素材ごとの鳴り方) ─────────────────────────

def modal(modes, dur, strike=0.004):
    # 物の固有の鳴り: (周波数, 減衰の時定数, 強さ) の組を減衰する正弦で重ね、叩いた瞬間の雑音を足す
    n, t = T(dur)
    x = np.zeros(n)
    for f, tau, g in modes:
        x += g * np.sin(2 * np.pi * f * t + rng.uniform(0, 6.28)) * np.exp(-t / tau)
    hi = max(f for f, _, _ in modes)
    return x + 0.6 * bp(noise(n), hi * 0.5, hi * 3) * np.exp(-t / strike)


def bounce(out, at, first_gap, e, gain, make, count=5):
    # 落ちた物が跳ねる: 間隔も強さも跳ねるたびに e 倍で縮む
    gap = first_gap
    for _ in range(count):
        if at * SR >= len(out) or gain < 0.03:
            return
        place(out, make(), at, gain)
        at += gap
        gap *= e * rng.uniform(0.9, 1.1)
        gain *= e * rng.uniform(0.85, 1.05)


def stick_slip(dur, r0, r1, kernel, jitter=0.25, swell=0.3):
    # きしみ: 擦れて引っかかる瞬間の連打を物の鳴りに通す。連打の速さ r0 → r1 (回/秒)
    n, t = T(dur)
    imp = np.zeros(n)
    at = 0.0
    while at < dur:
        u = at / dur
        i = int(at * SR)
        imp[i] += rng.uniform(0.5, 1.0) * np.sin(np.pi * u) ** swell
        at += 1.0 / ((r0 + (r1 - r0) * u) * rng.uniform(1 - jitter, 1 + jitter))
    return fftconvolve(imp, kernel)[:n]


def tear(dur, lo, hi, rate):
    # 裂ける: 帯域を絞った雑音を細かく途切れさせる (金属なら金切り、木なら繊維が切れる音)
    n, t = T(dur)
    x = bp(noise(n), lo, hi, 3)
    gate = (lp(noise(n), rate) * 6).clip(0, None) ** 1.5
    return x * gate * np.sin(np.pi * np.minimum(t / dur, 1)) ** 0.5


def grind(dur, lo, hi, rough=30):
    # 重い物同士が擦れる: 低い雑音にざらざらした揺れを掛ける
    n, t = T(dur)
    x = bp(noise(n), lo, hi)
    rough_env = 0.4 + (lp(noise(n), rough) * 5).clip(0, None)
    return x * rough_env * np.sin(np.pi * np.minimum(t / dur, 1))


def room(x, rt, tone, mix):
    # 部屋の響き: 指数で減る雑音を残響として畳み込む。tone より上は響きが早く消える
    n, t = T(rt * 1.2)
    ir = lp(noise(n), tone) * np.exp(-t * 6.9 / rt)
    ir[: int(0.012 * SR)] = 0  # 最初の反射まで少し間を空ける
    wet = fftconvolve(x, ir)[: len(x)]
    wet *= np.max(np.abs(x)) / (np.max(np.abs(wet)) + 1e-9)
    return x + mix * wet


def thud(weight=1.0):
    # 重い塊が床に落ちる: 低い衝撃 + 床の鳴り
    n, t = T(0.5)
    low = np.sin(sweep(t, 110 / weight, 45 / weight, 0.03)) * np.exp(-t / (0.09 * weight))
    knock = bp(noise(n), 120, 1400) * np.exp(-t / 0.02)
    return np.tanh(1.5 * (low + 0.8 * knock))


# ── 叩いて崩れた音 (2.4s) ─────────────────────────────────────
# 叩いた音で始まり、素材が壊れていく過程 (きしむ → 裂ける・割れる → 落ちる → 跳ねる・転がる → 収まる) を素材の鳴りで鳴らす

def crumble_metal():
    # 船体の板: 継ぎ目がきしんで唸る → ボルトが弾けて板が裂ける → 大きな板が床に倒れて長く響く → 細かい部品が跳ねる
    n, t = T(2.4)
    out = np.zeros(n)
    place(out, hit_metal(), 0.0, 0.9)
    plate = modal([(rng.uniform(70, 90), 0.35, 1.0), (rng.uniform(150, 175), 0.25, 0.7),
                   (rng.uniform(260, 300), 0.18, 0.5), (rng.uniform(430, 480), 0.12, 0.35)], 0.5)
    place(out, stick_slip(0.55, 18, 9, plate[: int(0.25 * SR)] * 0.25), 0.08, 1.0)
    place(out, tear(0.3, 1800, 5200, 60), 0.32, 0.55)
    for at in (0.3, 0.36, 0.45):  # ボルトが弾ける
        place(out, clank(rng.uniform(1800, 2600), 0.15, 0.05), at, 0.35)
    # 大きな板が倒れる: 床への一撃 + 板全体のうなる鳴り (ゆっくり揺れる)
    place(out, thud(1.3), 0.62, 1.2)
    ring = modal([(rng.uniform(55, 65), 0.9, 1.0), (rng.uniform(118, 130), 0.7, 0.8), (rng.uniform(205, 225), 0.5, 0.6),
                  (rng.uniform(340, 370), 0.35, 0.45), (rng.uniform(610, 660), 0.2, 0.3)], 1.6)
    wob = 1 + 0.35 * np.sin(2 * np.pi * 5.5 * T(1.6)[1])
    place(out, np.tanh(1.3 * ring * wob), 0.62, 0.9)
    bounce(out, 0.78, 0.16, 0.55, 0.5, lambda: clank(rng.uniform(380, 520), 0.4, 0.14))
    for _ in range(5):  # ナットや小さい破片が跳ねて転がる
        bounce(out, 0.7 + rng.uniform(0, 0.6), rng.uniform(0.08, 0.14), 0.6, rng.uniform(0.12, 0.22),
               lambda: clank(rng.uniform(2200, 3600), 0.08, 0.025), 7)
    out += 0.25 * debris(n, 0.65, 1.6, 600, 4000, 25)
    return room(out, 1.1, 2500, 0.45)


def crumble_stone():
    # 岩とコンクリート: 深く割れる → 擦れながらずれる → 塊がいくつも落ちる → 砂利が流れて砂ぼこりが収まる
    n, t = T(2.4)
    out = np.zeros(n)
    place(out, hit_stone(), 0.0, 0.85)
    m, tm = T(0.12)
    place(out, np.tanh(3 * bp(noise(m), 400, 6000) * np.exp(-tm / 0.012)), 0.14, 1.0)  # 深い割れの破裂
    place(out, crack(0.3, 700, 6000), 0.15, 0.8)
    place(out, grind(0.55, 70, 700), 0.2, 1.1)
    for i in range(6):  # 大小の塊が床に落ち、少し跳ねて砂利を散らす
        at = 0.45 + i * rng.uniform(0.06, 0.14)
        w = rng.uniform(0.8, 1.4)
        g = rng.uniform(0.6, 1.0) * (1.0 if i < 2 else 0.6)
        place(out, thud(w), at, g)
        place(out, grains(int(0.4 * SR), 120, 800, 5000, 0.012, 0.003, 0, 0.3, lambda u: 1 - u), at + 0.005, g * 0.5)
        bounce(out, at + 0.09, 0.07, 0.4, g * 0.3, lambda: thud(0.6), 3)
    out += 0.9 * debris(n, 0.5, 2.1, 300, 4000, 70)  # 砂利が流れる
    m, tm = T(1.8)
    place(out, hp(noise(m), 3000) * np.exp(-tm / 0.6) * np.minimum(1, tm / 0.2), 0.55, 0.12)  # 砂ぼこり
    return room(out, 0.7, 1800, 0.3)


def crumble_wood():
    # 木の板と柱: 繊維がきしむ → めりめり裂ける → 板が倒れて床で弾む → 木っ端が跳ね回る
    n, t = T(2.4)
    out = np.zeros(n)
    place(out, hit_wood(), 0.0, 0.9)
    body = modal([(rng.uniform(210, 250), 0.035, 1.0), (rng.uniform(470, 540), 0.025, 0.6),
                  (rng.uniform(880, 980), 0.015, 0.35)], 0.12)
    place(out, stick_slip(0.5, 35, 14, body * 0.3, 0.35), 0.06, 1.0)
    m, _ = T(0.45)
    place(out, grains(m, 150, 1200, 7000, 0.006, 0.0015, 0, 0.45, lambda u: u ** 0.7), 0.3, 0.9)  # 繊維が切れていく
    place(out, tear(0.4, 700, 3500, 45), 0.32, 0.6)
    place(out, crack(0.2, 1000, 6000), 0.68, 1.0)  # 最後に折れる
    plank = lambda f: (lambda: np.tanh(1.5 * modal([(f, 0.06, 1.0), (f * 2.3, 0.04, 0.5), (f * 4.1, 0.02, 0.3)], 0.25)))
    place(out, thud(1.1), 0.82, 0.9)
    bounce(out, 0.82, 0.22, 0.5, 0.9, plank(rng.uniform(140, 180)), 5)
    for _ in range(3):
        bounce(out, 0.9 + rng.uniform(0, 0.4), rng.uniform(0.12, 0.18), 0.5, rng.uniform(0.3, 0.5),
               plank(rng.uniform(300, 600)), 5)
    for _ in range(6):  # 木っ端
        bounce(out, 0.85 + rng.uniform(0, 0.7), 0.07, 0.55, rng.uniform(0.1, 0.2), lambda: wood_knock(rng.uniform(900, 1600), 0.05), 4)
    out += 0.2 * debris(n, 0.8, 1.8, 400, 3000, 25)
    return room(out, 0.55, 2000, 0.3)


# ── 爆発で崩れ落ちる音 (2.2s) ─────────────────────────────────
# 爆風で砕けた壁が降ってくる: 飛び散った破片の雨 → 大きな塊が落ちる → 跳ねる・転がる → 収まる。爆発の音の後ろに重ねる

def rubble_metal():
    # 船体: 金属片が床に降り注ぐ → 外れた板が倒れて鳴る → 部品が跳ねて転がる
    n, t = T(2.2)
    out = np.zeros(n)
    for _ in range(26):  # 降ってくる金属片 (始めほど多い)
        at = rng.exponential(0.35)
        if at < 1.6:
            place(out, clank(rng.uniform(700, 3200), 0.2, rng.uniform(0.03, 0.08)), at, rng.uniform(0.15, 0.45) * np.exp(-at / 0.8))
    out += 0.4 * debris(n, 0.0, 1.5, 500, 4500, 40)
    place(out, thud(1.4), 0.25, 1.0)
    ring = modal([(rng.uniform(60, 72), 0.8, 1.0), (rng.uniform(130, 150), 0.6, 0.7), (rng.uniform(230, 260), 0.4, 0.5),
                  (rng.uniform(390, 430), 0.3, 0.35)], 1.5)
    wob = 1 + 0.3 * np.sin(2 * np.pi * 4.5 * T(1.5)[1])
    place(out, np.tanh(1.3 * ring * wob), 0.25, 0.75)
    bounce(out, 0.5, 0.15, 0.55, 0.45, lambda: clank(rng.uniform(300, 480), 0.35, 0.12))
    for _ in range(4):
        bounce(out, 0.4 + rng.uniform(0, 0.7), rng.uniform(0.08, 0.13), 0.6, rng.uniform(0.12, 0.2),
               lambda: clank(rng.uniform(2000, 3600), 0.08, 0.025), 7)
    return room(out, 1.1, 2500, 0.45)


def rubble_stone():
    # 岩とコンクリート: 石つぶてが降る → 大きな塊がどさどさ落ちる → 砂利が崩れ続けて砂ぼこりが収まる
    n, t = T(2.2)
    out = np.zeros(n)
    out += 0.7 * grains(n, 160, 600, 5000, 0.02, 0.005, 0.0, 0.9, lambda u: (1 - u) ** 1.2)  # 石つぶて
    for i in range(7):
        at = 0.12 + i * rng.uniform(0.07, 0.16)
        g = rng.uniform(0.55, 1.0) * (1.0 if i < 3 else 0.6)
        place(out, thud(rng.uniform(0.9, 1.5)), at, g)
        place(out, grains(int(0.4 * SR), 120, 800, 5000, 0.012, 0.003, 0, 0.3, lambda u: 1 - u), at + 0.005, g * 0.5)
        bounce(out, at + 0.09, 0.07, 0.4, g * 0.3, lambda: thud(0.6), 3)
    place(out, grind(0.6, 70, 600), 0.3, 0.7)
    out += 0.9 * debris(n, 0.3, 2.0, 300, 4000, 70)
    m, tm = T(1.6)
    place(out, hp(noise(m), 3000) * np.exp(-tm / 0.6) * np.minimum(1, tm / 0.2), 0.4, 0.12)
    return room(out, 0.7, 1800, 0.3)


def rubble_wood():
    # 木: 木っ端と板切れが降る → 柱が倒れて床で弾む → 板が跳ね回る
    n, t = T(2.2)
    out = np.zeros(n)
    for _ in range(24):
        at = rng.exponential(0.3)
        if at < 1.4:
            place(out, wood_knock(rng.uniform(500, 1600), 0.06), at, rng.uniform(0.2, 0.5) * np.exp(-at / 0.7))
    out += 0.4 * grains(n, 120, 1500, 7000, 0.006, 0.0015, 0.0, 0.5, lambda u: 1 - u)  # 裂けた繊維
    plank = lambda f: (lambda: np.tanh(1.5 * modal([(f, 0.06, 1.0), (f * 2.3, 0.04, 0.5), (f * 4.1, 0.02, 0.3)], 0.25)))
    place(out, thud(1.2), 0.3, 0.9)
    bounce(out, 0.3, 0.22, 0.5, 0.9, plank(rng.uniform(120, 160)), 5)
    for _ in range(3):
        bounce(out, 0.35 + rng.uniform(0, 0.5), rng.uniform(0.12, 0.18), 0.5, rng.uniform(0.3, 0.5),
               plank(rng.uniform(280, 600)), 5)
    out += 0.25 * debris(n, 0.2, 1.6, 400, 3000, 30)
    return room(out, 0.55, 2000, 0.3)


def fuse():
    # 導火線 (1.7s): 火の付く擦れ → シューという燃える音に火花のはぜる粒 → 終わりへ向けて速く強く
    n, t = T(1.7)
    out = np.zeros(n)
    m, tm = T(0.08)
    place(out, bp(noise(m), 1500, 7000) * np.exp(-tm / 0.02), 0.0, 0.8)  # 火を付ける擦れ
    flicker = 0.7 + 0.3 * np.sin(2 * np.pi * 23 * t + 3 * np.sin(2 * np.pi * 3.1 * t))
    hiss = bp(noise(n), 2500, 9000) * flicker * (0.5 + 0.5 * t / 1.7) * np.minimum(1, t / 0.06)
    out += 0.35 * hiss
    out += 0.9 * grains(n, 60, 2500, 9000, 0.006, 0.0015, 0.05, 1.7, lambda u: 0.4 + 0.6 * u)
    # 急かす刻み (チッ): 間が 0.32 秒から 0.08 秒へ縮む
    at, gap = 0.12, 0.32
    while at < 1.62:
        k, tk = T(0.02)
        place(out, bp(noise(k), 3500, 8000) * np.exp(-tk / 0.004), at, 1.2)
        at += gap
        gap = max(0.08, gap * 0.8)
    return out


def leak():
    # 圧のかかった噴き出し (高い擦れ) + 床を打つ水の粒 + 低いごぼごぼ。重ねて鳴らすので頭と尻を長く薄れさせる
    dur = 1.4
    n, t = T(dur)
    flutter = 0.8 + 0.2 * np.sin(2 * np.pi * 7.3 * t + 2 * np.sin(2 * np.pi * 1.7 * t))
    out = 0.5 * bp(noise(n), 1400, 8000) * flutter
    out += 0.9 * grains(n, 110, 700, 3500, 0.01, 0.0025)
    lfo = lp(np.abs(noise(n)), 6, order=1)
    out += 0.6 * bp(noise(n), 160, 520) * lfo / (np.max(lfo) + 1e-9)
    env = np.minimum(1, np.minimum(t / 0.25, (dur - t) / 0.35))
    return out * np.clip(env, 0, 1)


def drop(f0, f1, dur=0.05):
    # 水の粒が落ちる「ぽちゃ」: 上がっていく正弦が速く消える
    m, tm = T(dur)
    return np.sin(sweep(tm, f0, f1, 0.012)) * np.exp(-tm / (dur * 0.3))


def splash():
    # 足が水を打つ音 → 跳ねた粒と泡
    n, t = T(0.4)
    out = np.zeros(n)
    m, tm = T(0.09)
    slap = rng.uniform(0.022, 0.035)
    place(out, bp(noise(m), 450, 3200) * np.exp(-tm / slap), 0.0, 1.0)
    place(out, lp(noise(m), 300) * np.exp(-tm / 0.02), 0.0, 0.6)
    for _ in range(rng.integers(3, 6)):
        f = rng.uniform(500, 1100)
        place(out, drop(f, f * rng.uniform(1.6, 2.4)), rng.uniform(0.03, 0.22), rng.uniform(0.25, 0.6))
    out += 0.6 * grains(n, 70, 2000, 7000, 0.008, 0.002, 0.02, 0.3, lambda u: (1 - u) ** 2)
    return out


# ── 外壁の穴 (吸い出し) ─────────────────────────────────────

def pnoise(n, shape):
    # 周期的な雑音: 周波数ごとの大きさ shape(f) に乱数の位相を付けて逆 FFT。頭と尻がつながるのでループの継ぎ目が出ない
    f = np.fft.rfftfreq(n, 1 / SR)
    mag = shape(f)
    ph = rng.uniform(0, 2 * np.pi, len(f))
    x = np.fft.irfft(mag * np.exp(1j * ph), n)
    return x / (np.std(x) + 1e-9)


def band(f, lo, hi, edge=0.5):
    # lo〜hi を通す滑らかな窓 (端は edge オクターブで落ちる)
    lf = np.log2(np.maximum(f, 1.0))
    a = np.clip((lf - np.log2(lo)) / edge + 1, 0, 1)
    b = np.clip((np.log2(hi) - lf) / edge + 1, 0, 1)
    return a * b


def plfo(t, dur, cycles, phase=0.0):
    # ループの長さにちょうど cycles 回収まる揺れ
    return np.sin(2 * np.pi * cycles * t / dur + phase)


def decomp_breach():
    # 開通 (1.8s): 金属が裂ける鋭い破裂 → 圧が一気に抜けるバシュッ → 船体の鳴り → 破片
    dur = 1.8
    n, t = T(dur)
    out = np.zeros(n)
    m, tm = T(0.05)
    place(out, hp(noise(m), 3000) * np.exp(-tm / 0.006), 0.0, 1.6)        # はじける
    place(out, tear(0.32, 2200, 7500, 70), 0.0, 1.3)                       # 金切り
    place(out, impact(0.7, 0.7), 0.0, 1.1)                                 # バン (爆発より軽い)
    hiss = bp(noise(n), 1400, 10000) * np.exp(-t / 0.32) + 0.7 * bp(noise(n), 350, 1800) * np.exp(-t / 0.55)
    out += 1.5 * hiss * np.minimum(1, t / 0.004)                           # シュッ
    place(out, clank(rng.uniform(210, 250), 1.2, 0.45), 0.01, 0.7)         # 船体の板の唸り
    place(out, clank(rng.uniform(880, 1020), 0.5, 0.12), 0.0, 0.4)
    out += 0.35 * debris(n, 0.05, 1.0, 900, 6000, 60)
    return room(out, 0.6, 2500, 0.25)


def decomp_loop():
    # 吸い出し (4.0s ループ): 低いゴォォ + 中域の風切り + 細い高い擦れ。揺れはループに整数回だけ入れる
    dur = 4.0
    n, t = T(dur)
    roar = pnoise(n, lambda f: band(f, 30, 220, 0.8) / (1 + f / 90))
    rush = pnoise(n, lambda f: band(f, 300, 2200, 0.7))
    hiss = pnoise(n, lambda f: band(f, 3000, 8000, 0.6))
    roar *= 1 + 0.25 * plfo(t, dur, 3) + 0.1 * plfo(t, dur, 7, 1.0)
    rush *= 1 + 0.35 * plfo(t, dur, 5, 2.0) + 0.15 * plfo(t, dur, 11, 0.5)
    hiss *= 1 + 0.3 * plfo(t, dur, 9, 1.5)
    return 1.0 * roar + 0.55 * rush + 0.16 * hiss


def decomp_whistle():
    # 笛 (2.0s ループ): 1100Hz に細く絞った雑音 (隙間を抜ける空気) + 倍音 + 薄い擦れ
    dur = 2.0
    n, t = T(dur)
    def peak(f, c, w):
        return np.exp(-0.5 * ((f - c) / w) ** 2)
    x = pnoise(n, lambda f: peak(f, 1100, 18) + 0.35 * peak(f, 2200, 30) + 0.12 * peak(f, 3300, 45) + 0.03 * band(f, 1500, 6000))
    return x * (1 + 0.12 * plfo(t, dur, 6))


def decomp_seal():
    # ふさがる (0.55s): 最後の空気がぷしゅっと抜ける + 泡が口をとじるぽふっ
    n, t = T(0.55)
    out = np.zeros(n)
    env = np.minimum(1, t / 0.004) * np.exp(-t / 0.09)
    out += bp(noise(n), 2200, 9000) * env * 1.2
    out += 0.5 * bp(noise(n), 700, 2200) * np.minimum(1, t / 0.004) * np.exp(-t / 0.05)
    m, tm = T(0.12)
    place(out, np.sin(sweep(tm, 1500, 600, 0.03)) * np.exp(-tm / 0.03), 0.0, 0.25)  # 笛の名残が落ちる
    place(out, lp(noise(m), 260) * np.exp(-tm / 0.03), 0.07, 1.4)                   # ぽふっ
    return out


def emit_loop(name, make, peak=0.8):
    # ループは頭と尻をつなぐので、時間で効く圧縮・フェード・IIR を通さない (どれも継ぎ目を作る)
    reseed(name)
    x = make()
    x = x / (np.max(np.abs(x)) + 1e-9)
    x = np.tanh(1.4 * x) / np.tanh(1.4)
    save(name, x, SR, peak)
    spec = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1 / SR)
    spec *= 1 / (1 + (f / 500) ** 6)  # muffled() と同じ所から落とす (周期のまま)
    m = np.fft.irfft(spec[: len(x) // 4 + 1], len(x) // 2)
    save(name + '_m', m, SR // 2, peak * 0.75)


# ── 家具がぶつかる ─────────────────────────────────────────

def bump_small():
    # 小物 (0.35s): 硬く軽い物が壁に当たってカツン → 1〜2 回小さく跳ねる
    n, t = T(0.35)
    out = np.zeros(n)
    f = rng.uniform(1300, 2200)
    hit = lambda: modal([(f, 0.03, 1.0), (f * 1.73, 0.018, 0.6), (f * 2.61, 0.01, 0.4)], 0.08, 0.002)
    place(out, hit(), 0.0, 1.0)
    place(out, bp(noise(int(0.02 * SR)), 300, 1500) * np.exp(-np.arange(int(0.02 * SR)) / SR / 0.004), 0.0, 0.5)
    bounce(out, rng.uniform(0.06, 0.09), 0.05, 0.6, 0.45, hit, 3)
    return out


def bump_heavy():
    # 重い家具が壁に当たる (0.7s): 鈍いドン + 箱の胴鳴り + 壁の板の唸り + 少し擦れる
    n, t = T(0.7)
    out = np.zeros(n)
    place(out, thud(1.2), 0.0, 1.2)
    b = rng.uniform(170, 230)
    place(out, modal([(b, 0.12, 1.0), (b * 1.6, 0.08, 0.6), (b * 2.3, 0.05, 0.4), (b * 3.4, 0.03, 0.25)], 0.5, 0.006), 0.0, 0.9)
    place(out, clank(rng.uniform(120, 150), 0.6, 0.25), 0.005, 0.35)
    place(out, grind(0.18, 150, 1200), 0.02, 0.3)
    return room(out, 0.4, 2000, 0.2)


def bump_clash():
    # 家具どうし (0.8s): 2 つの物の金属の鳴りが少しずれて重なる + ドン + がたつき
    n, t = T(0.8)
    out = np.zeros(n)
    place(out, thud(1.0), 0.0, 0.9)
    place(out, clank(rng.uniform(300, 420), 0.6, 0.18), 0.0, 0.8)
    place(out, clank(rng.uniform(650, 900), 0.5, 0.12), rng.uniform(0.004, 0.015), 0.6)
    out += 0.35 * grains(n, 90, 600, 4000, dur=0.02, tau=0.005, t0=0.02, t1=0.25, shape=lambda u: 1 - u)
    return room(out, 0.45, 2200, 0.2)


def fire_loop():
    # 燃える火 (4.0s ループ): 低くうねる炎 + 中域の揺らぎ + ぱちぱち (継ぎ目の手前ではぜさせない)
    dur = 4.0
    n, t = T(dur)
    roar = pnoise(n, lambda f: band(f, 50, 700, 0.8) / (1 + f / 160))
    flutter = pnoise(n, lambda f: band(f, 700, 3000, 0.7))
    roar *= 1 + 0.3 * plfo(t, dur, 3) + 0.15 * plfo(t, dur, 8, 1.3)
    flutter *= 0.6 + 0.4 * (0.5 + 0.5 * plfo(t, dur, 13, 0.4)) * (0.5 + 0.5 * plfo(t, dur, 5, 2.1))
    pops = grains(n, 9, 1500, 6500, dur=0.012, tau=0.003, t0=0.03, t1=dur - 0.05)
    crackle = grains(n, 40, 2500, 9000, dur=0.006, tau=0.0015, t0=0.02, t1=dur - 0.03)
    return 1.0 * roar + 0.3 * flutter + 1.2 * pops + 0.5 * crackle


def fire_ignite():
    # 火が付く (0.9s): 空気を吸い込むぼっ + 炎の立ち上がり
    n, t = T(0.9)
    whump = lp(noise(n), 260) * np.minimum(1, t / 0.03) * np.exp(-t / 0.18)
    sub = np.sin(sweep(t, 90, 45, 0.15)) * np.exp(-t / 0.12)
    rise = bp(noise(n), 300, 3500) * np.minimum(1, t / 0.08) * np.exp(-t / 0.35)
    pops = grains(n, 25, 1500, 6000, dur=0.01, tau=0.0025, t0=0.05, t1=0.8, shape=lambda u: 1 - u)
    return 1.2 * whump + 0.7 * sub + 0.5 * rise + 0.6 * pops


def steam():
    # 湯気 (1.0s): 熱い所に水が触れたじゅうっ + 細かい泡のはぜ
    n, t = T(1.0)
    hiss = bp(noise(n), 2200, 9500) * np.minimum(1, t / 0.015) * np.exp(-t / 0.35)
    hiss *= 0.8 + 0.2 * lp(noise(n), 30) * 8
    sizzle = grains(n, 160, 3000, 9000, dur=0.004, tau=0.001, t0=0.0, t1=0.8, shape=lambda u: (1 - u) ** 1.5)
    return hiss + 0.5 * sizzle


def flare():
    # 噴き上がり (1.4s): 水が一気に沸くばしゅっ → 炎の柱のごおっ
    n, t = T(1.4)
    burst = bp(noise(n), 1200, 9000) * np.minimum(1, t / 0.006) * np.exp(-t / 0.08)
    whoosh = lp(noise(n), 900) * np.minimum(1, t / 0.06) * np.exp(-t / 0.4)
    sub = np.sin(sweep(t, 80, 38, 0.25)) * np.exp(-t / 0.22)
    roar = jet(bp(noise(n), 150, 2500), rate=1.3) * np.minimum(1, t / 0.1) * np.exp(-t / 0.5)
    pops = grains(n, 45, 1500, 7000, dur=0.01, tau=0.0025, t0=0.05, t1=1.2, shape=lambda u: 1 - u)
    return 0.9 * burst + 1.0 * whoosh + 0.8 * sub + 0.7 * roar + 0.5 * pops


def arc():
    # 火花 (0.45s): 120Hz のうなり (途切れ途切れ) + 鋭いばちばち
    n, t = T(0.45)
    buzz = sawtooth(2 * np.pi * 120 * t) * 0.6 + sawtooth(2 * np.pi * 240 * t + 0.3) * 0.3
    gate = (lp(noise(n), 40) > 0).astype(float)
    buzz = bp(buzz, 100, 3000) * lp(gate, 200, order=1) * np.exp(-t / 0.25)
    snaps = grains(n, 60, 3000, 12000, dur=0.003, tau=0.0007, t0=0.0, t1=0.4, shape=lambda u: 1 - u)
    return 0.6 * buzz + 1.0 * snaps


if __name__ == '__main__':
    os.makedirs(OUT, exist_ok=True)
    emit('noise_boom', boom, drive=2.4)
    emit('noise_fuse', fuse, peak=0.8, drive=1.6, ratio=0.3)
    emit('noise_leak', leak, peak=0.75, drive=1.4, ratio=0.15)
    for i in range(1, 4):
        emit(f'noise_splash_{i}', splash, peak=0.8, drive=1.6, ratio=0.3)
    hits = {'metal': hit_metal, 'stone': hit_stone, 'wood': hit_wood}
    crumbles = {'metal': crumble_metal, 'stone': crumble_stone, 'wood': crumble_wood}
    rubbles = {'metal': rubble_metal, 'stone': rubble_stone, 'wood': rubble_wood}
    for mat, h in hits.items():
        emit('noise_hit_' + mat, h)
        # 崩れる音は細かい音 (跳ねる破片・砂利) が聞こえるよう圧縮を弱める
        emit('noise_crumble_' + mat, crumbles[mat], drive=1.6, ratio=0.3)
        emit('noise_rubble_' + mat, rubbles[mat], drive=1.6, ratio=0.3)
    emit('noise_decomp_breach', decomp_breach, drive=2.0)
    emit_loop('noise_decomp_loop', decomp_loop)
    emit_loop('noise_decomp_whistle', decomp_whistle, peak=0.7)
    emit('noise_decomp_seal', decomp_seal, peak=0.8, drive=1.6, ratio=0.3)
    for i in range(1, 4):
        emit(f'noise_bump_small_{i}', bump_small, peak=0.8, drive=1.6, ratio=0.3)
    for i in range(1, 3):
        emit(f'noise_bump_heavy_{i}', bump_heavy, peak=0.9, drive=1.8, ratio=0.35)
        emit(f'noise_bump_clash_{i}', bump_clash, peak=0.9, drive=1.8, ratio=0.35)
    emit_loop('noise_fire_loop', fire_loop, peak=0.75)
    emit('noise_fire_ignite', fire_ignite, peak=0.85, drive=1.8, ratio=0.35)
    emit('noise_steam', steam, peak=0.75, drive=1.6, ratio=0.3)
    emit('noise_flare', flare, peak=0.95, drive=2.0)
    emit('noise_arc', arc, peak=0.7, drive=1.6, ratio=0.3)
