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
#   noise_burn_hit_<素材>   … 炎に焼かれた壁が傷む (炎が息をつく・素材がきしむ/はぜる)
#   noise_burn_crumble_<素材> … 焼けた壁が崩れ落ちる (焦げて鈍い崩れ・炎がごうっと吹き込む・火の粉がはぜる)
#   それぞれ <名前>_m       … 遠い・壁越しのこもった音 (16kHz)
#   noise_portal_open       … ポータルが開く (風と電気の唸りでためて、開き切りに低い一撃と放電・揺れるうなりと倍音が残る)
#   noise_portal_pass       … ポータルを通った (しゅっと抜ける風と短い放電・軽い低音)
#   noise_portal_close      … ポータルが閉じる (うなりが吸い込まれ、内側へつぶれる低い一撃と火花)
#   noise_emote_clap_1..3   … 拍手の 1 打 (手のひらの間の空気の鳴りを打つたびに変える)
#   noise_emote_land_1..2   … 跳ねて着地した (宇宙服の靴の底が床を打つ・両足は少しずれて着く)
#   noise_emote_push        … 跳ねる前の踏み切り (靴底が床を擦る・服が揺れる)
#   noise_emote_swish_1..2  … 手を振った時の袖の擦れ (しゅっ)
#   noise_emote_rustle      … 手招きで指を曲げた時の手袋の擦れ (ごく小さい)
#   noise_drone_loop        … 構築士のドローンの羽音 (継ぎ目なしのループ・4 つのモーターの羽根の刻みが少しずつずれてうねる)
#   noise_end_spectate      … 観戦の人の終わりの画面 (勝ちでも負けでもない、見届けた音。こもった版は作らない)
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


def emit(name, make, peak=0.95, drive=2.2, ratio=0.45, trim=False):
    reseed(name)
    x = master(fade(make()), drive, ratio)
    x = fade(trim_tail(x) if trim else x)
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
    # 長く尾を引く部品は終わりまで伸ばして最後にまとめて絞る (途中で切ると減り切らない残りがぶつっと途切れる)
    place(out, impact(dur - h, 1.9), h, 2.4)
    m, tm = T(0.2)
    place(out, hp(noise(m), 1800) * np.exp(-tm / 0.025), h, 1.7)
    place(out, sub_hit(1.2, 120, 30, 0.08, 0.45), h, 1.4)
    place(out, braam(41.2, 1.4, 0.45), h, 1.0)
    place(out, braam(61.7, 0.9, 0.3), h, 0.5)
    fd = dur - h - 0.03
    fb = jet(fire_bed(fd, 0.5), 0.0006, 0.005, 1.6, 0.0, 0.8)
    place(out, fb * np.minimum(1, T(fd)[1] / 0.05), h + 0.03, 0.9)
    place(out, crack(0.3), h + 0.02, 0.8)
    for at, g in [(0.3, 0.8), (0.52, 0.6)]:
        place(out, impact(0.6, 0.6), at, g)
    out += roll(dur, 6, 0.7, t0=0.2) * 0.8
    out += 0.2 * debris(n, 0.25, 1.4)  # 爆風で舞った細かい物
    tail = 0.5
    out *= np.where(t > dur - tail, 0.5 + 0.5 * np.cos(np.pi * (t - (dur - tail)) / tail), 1.0)
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


def drone_loop():
    # 4 枚羽根の小さなドローン (4.0s ループ): 4 つのモーターの羽根の刻み (少しずつ違う高さで唸りがうねる)
    # + 刻みで揺れる風切り + モーターの細い鳴り。どの高さもループの長さにちょうど収まる (0.25Hz 刻み)
    dur = 4.0
    n, t = T(dur)
    buzz = np.zeros(n)
    for f0, ph, g in [(186.0, 0.0, 1.0), (191.25, 1.1, 0.9), (195.5, 2.3, 0.85), (200.75, 0.7, 0.8)]:
        # 刻みは鋸の歯に近い形 (倍音が 1/h より少し速く落ちる)
        for h in range(1, 9):
            buzz += g * (1.0 / h ** 1.2) * np.sin(2 * np.pi * f0 * h * t + ph * h)
    buzz /= np.max(np.abs(buzz)) + 1e-9
    buzz *= 1 + 0.1 * plfo(t, dur, 1, 0.3) + 0.05 * plfo(t, dur, 3, 1.7)
    wind = pnoise(n, lambda f: band(f, 300, 4500, 0.8) / (1 + f / 2500))
    wind *= 0.65 + 0.35 * (0.5 + 0.5 * np.sin(2 * np.pi * 186.0 * t))
    whine = 0.6 * np.sin(2 * np.pi * 1488.0 * t) + 0.4 * np.sin(2 * np.pi * 1530.25 * t)
    whine *= 0.7 + 0.3 * plfo(t, dur, 2, 0.9)
    return 1.0 * buzz + 0.22 * wind / 3 + 0.06 * whine


def burn_strain(mat, dur, gain):
    # 熱で素材が傷む音: 木 = 繊維のきしみと節のはぜ / 金属 = 熱で反る板の唸りとちりちり鳴る継ぎ目 / 岩 = 表面が弾けて剥がれる
    n, t = T(dur)
    out = np.zeros(n)
    if mat == 'wood':
        body = modal([(rng.uniform(190, 230), 0.04, 1.0), (rng.uniform(450, 520), 0.03, 0.6), (rng.uniform(900, 1000), 0.015, 0.3)], 0.12)
        place(out, stick_slip(dur * 0.8, 28, 12, body * 0.3, 0.4), 0.0, 0.9 * gain)
        for _ in range(4):
            place(out, crack(0.08, 2000, 8000), rng.uniform(0.05, dur * 0.8), rng.uniform(0.4, 0.8) * gain)
    elif mat == 'metal':
        plate = modal([(rng.uniform(60, 75), 0.6, 1.0), (rng.uniform(130, 150), 0.4, 0.6), (rng.uniform(230, 260), 0.25, 0.4)], 0.6)
        place(out, stick_slip(dur * 0.85, 10, 5, plate[: int(0.3 * SR)] * 0.3, 0.3, 0.6), 0.0, 1.0 * gain)
        for _ in range(6):  # 熱で継ぎ目がちりっと鳴る
            place(out, clank(rng.uniform(2400, 3800), 0.06, 0.015), rng.uniform(0.05, dur * 0.85), rng.uniform(0.15, 0.3) * gain)
    else:
        for _ in range(5):
            place(out, crack(0.12, 1200, 6500), rng.uniform(0.03, dur * 0.8), rng.uniform(0.4, 0.8) * gain)
        out += 0.4 * gain * grains(n, 40, 800, 4500, dur=0.015, tau=0.004, t0=0.05, t1=dur * 0.9, shape=lambda u: 1 - u)
    return out


def burn_hit(mat):
    # 焼けた壁が傷む (1.4s): 叩く一撃は無く、炎がふっと強まる息 + 素材の傷む音 + ぱちぱち
    n, t = T(1.4)
    out = np.zeros(n)
    breath = bp(noise(n), 150, 2200) * np.minimum(1, t / 0.12) * np.exp(-t / 0.45)
    out += 0.7 * breath
    place(out, burn_strain(mat, 1.1, 1.0), 0.04, 1.0)
    out += 0.7 * grains(n, 35, 1800, 7500, dur=0.008, tau=0.002, t0=0.0, t1=1.3, shape=lambda u: 1 - u)
    return room(out, 0.5, 2200, 0.3)


def burn_crumble(mat):
    # 焼けた壁が崩れる (2.8s): 傷みが強まる → 折れる → 焦げて鈍い崩れ (響かない) → 開いた穴へ炎がごうっと吹き込む → 火の粉がはぜて収まる
    n, t = T(2.8)
    out = np.zeros(n)
    place(out, burn_strain(mat, 0.45, 1.3), 0.0, 1.0)
    give = 0.38
    place(out, crack(0.25, 900, 6000) if mat != 'metal' else tear(0.3, 900, 3200, 40), give, 1.0)
    # 崩れ落ちる塊: 炭や焼けた板は鳴らずにぼすっと落ちる (低域だけ)
    place(out, lp(thud(1.2), 900), give + 0.12, 1.3)
    for _ in range(6):
        place(out, lp(thud(rng.uniform(0.5, 0.8)), 1400), give + 0.15 + rng.uniform(0, 0.6), rng.uniform(0.25, 0.5))
    if mat == 'metal':
        place(out, lp(clank(rng.uniform(140, 180), 0.6, 0.12), 1200), give + 0.14, 0.5)
    m, _ = T(1.4)  # 炭が崩れるざくざく
    place(out, grains(m, 90, 600, 3500, 0.012, 0.003, 0, 1.4, lambda u: (1 - u) ** 1.5), give + 0.1, 0.8)
    # 穴へ空気が吸い込まれて炎が膨らむ
    whoosh = lp(noise(n), 320) * np.clip((t - give - 0.05) / 0.12, 0, 1) * np.exp(-np.clip(t - give - 0.05, 0, None) / 0.35)
    out += 1.1 * whoosh
    m, _ = T(2.2)
    place(out, fire_bed(2.2, 0.8), give + 0.1, 0.55)
    out += 0.8 * grains(n, 50, 1800, 8000, dur=0.008, tau=0.002, t0=give + 0.1, t1=2.7, shape=lambda u: (1 - u) ** 1.2)
    return room(out, 0.7, 1800, 0.35)


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


def end_spectate():
    # 見届けた音 (3.4s): 低い 5 度の和音がふくらむ上で、鐘が 3 つ (D5・G5・A5 = 解決しない sus4) 鳴り、
    # 高い E6 がかすかに残る。勝ちの明るさも負けの暗さも出さない
    n, t = T(3.4)
    pad = np.zeros(n)
    for f, g in [(146.83, 1.0), (220.0, 0.7), (293.66, 0.35)]:
        for d in (-0.003, 0.003):
            pad += g * np.sin(2 * np.pi * f * (1 + d) * t + rng.uniform(0, 6.28))
    pad = lp(pad, 900) * np.minimum(1, t / 0.45) * np.exp(-t / 1.6) * 0.35

    def bell(f, dur=2.6):
        # 鐘の倍音は整数倍からずらす
        return modal([(f, 0.9, 1.0), (f * 2.0, 0.45, 0.35), (f * 2.76, 0.22, 0.18), (f * 5.4, 0.08, 0.06)], dur, strike=0.002)

    x = pad.copy()
    for at, f, g in [(0.05, 587.33, 0.55), (0.24, 783.99, 0.5), (0.43, 880.0, 0.6)]:
        place(x, bell(f), at, g)
    shimmer_n, st = T(2.4)
    shimmer = np.sin(2 * np.pi * 1318.5 * st) * np.minimum(1, st / 0.3) * np.exp(-st / 0.9) * 0.12
    place(x, shimmer, 0.7)
    return room(x, 2.2, 3500, 0.55)


def hum(t, f0, f1, tau, detune=0.004):
    # 少しずらした 2 本の正弦のうなり (高さは f0 から f1 へ指数的に寄る)
    ph = sweep(t, f0, f1, tau)
    return np.sin(ph * (1 - detune)) + np.sin(ph * (1 + detune) + 1.3) + 0.35 * np.sin(2 * ph + 0.4)


def smooth_end(x, frac=0.4):
    # 部品の終わりを余弦で絞る (部品の長さで切れた所が縦の段差にならないように)
    nr = int(len(x) * frac)
    e = np.ones(len(x))
    if nr:
        e[len(x) - nr:] = 0.5 + 0.5 * np.cos(np.linspace(0, np.pi, nr))
    return x * e


def room_tail(x, rt, tone, mix):
    # room は入力の長さで響きを切るので、先に無音を足して尾が減り切るまで響かせる
    return room(np.concatenate([x, np.zeros(int(rt * SR))]), rt, tone, mix)


def trim_tail(x, floor_db=-66):
    # 床より小さくなった後ろを捨てる (長めに作った無音を配らない)
    env = lp(np.abs(x) / (np.max(np.abs(x)) + 1e-9), 30, order=1)
    keep = np.nonzero(env > 10 ** (floor_db / 20))[0]
    return x[:min(len(x), keep[-1] + int(0.05 * SR))] if len(keep) else x


def fm(fc, ratio, index):
    # 周波数変調の唸り (fc は 1 標本ごとの周波数・index が大きいほど濁る)
    ph = 2 * np.pi * np.cumsum(fc) / SR
    return np.sin(ph + index * np.sin(ph * ratio))


def zap(dur, f_hi, f_lo, tau, index=4.0):
    # 放電: 高い所から一気に落ちる濁った音
    n, t = T(dur)
    return fm(f_lo + (f_hi - f_lo) * np.exp(-t / tau), 2.01, index) * np.exp(-t / (dur * 0.25))


def portal_open():
    # 開く (約 2s): 0〜0.36s に吸い込む風と上がる FM の唸りでためる → 開き切り (0.36s・絵と同時) に
    # 腹に来る低音・厚い唸り・放電の一撃 → 揺れるうなりときらめく倍音・途切れる電気のうなりが床まで消える
    hit = 0.36
    n, t = T(3.0)
    out = np.zeros(n)
    k, tk = T(hit)
    u = tk / hit
    rise = riser(hit, 250, 7000, 2.2)
    place(out, 0.8 * smooth_end(rise, 0.08), 0.0)
    place(out, 0.4 * fm(110 * 8 ** u ** 1.5, 1.5, 0.5 + 3.0 * u) * u ** 1.5 * smooth_end(np.ones(k), 0.03), 0.0)
    place(out, 0.5 * grains(k, 60, 3000, 12000, 0.003, 0.0007, 0.12, hit, lambda v: v * v), 0.0)
    place(out, smooth_end(sub_hit(2.0, 95, 34, tau=0.09, decay=0.38)), hit, 1.1)
    place(out, smooth_end(0.6 * braam(55, 2.0, decay=0.4) + 0.4 * braam(82.4, 2.0, decay=0.35)), hit, 0.45)
    place(out, smooth_end(zap(0.5, 3000, 70, 0.05)), hit, 0.45)
    s, ts = T(0.25)
    place(out, hp(noise(s), 3000) * np.exp(-ts / 0.035), hit, 0.5)
    r, tr = T(1.6)
    trem = 0.75 + 0.25 * np.sin(2 * np.pi * 5.5 * tr + 1.5 * np.sin(2 * np.pi * 0.7 * tr))
    place(out, smooth_end(0.35 * lp(hum(tr, 330, 220, 0.08, 0.006), 1500) * trem * np.minimum(1, tr / 0.03) * np.exp(-tr / 0.3), 0.5), hit)
    buzz = sawtooth(2 * np.pi * 110 * tr) * 0.6 + sawtooth(2 * np.pi * 220.7 * tr + 0.3) * 0.4
    gate = 0.55 + 0.45 * np.tanh(lp(noise(r), 25) * 20)
    place(out, smooth_end(0.2 * bp(buzz, 100, 2500) * gate * np.exp(-tr / 0.28) * np.minimum(1, tr / 0.02), 0.5), hit)
    chime = modal([(1760, 0.32, 1.0), (2637, 0.24, 0.6), (3520 * 1.006, 0.16, 0.35), (5274, 0.09, 0.2)], 1.4, strike=0.002)
    chime *= 0.7 + 0.3 * np.sin(2 * np.pi * 9 * np.arange(len(chime)) / SR)
    place(out, smooth_end(chime), hit + 0.01, 0.25)
    place(out, 0.4 * grains(r, 20, 3000, 11000, 0.003, 0.0007, 0.0, 0.7, lambda v: 1 - v), hit)
    return room_tail(out, 0.9, 5000, 0.35)


def portal_pass():
    # 通った (約 0.9s): 入口と出口で同時に 2 本鳴るので短く、低音は軽く。
    # 高い所から下がって抜ける風 + 放電の短い一撃 + 軽い低音 + 落ちるうなり + 火花
    n, t = T(1.4)
    out = np.zeros(n)
    w, tw = T(0.7)
    x = noise(w)
    air = np.zeros(w)
    wsum = np.zeros(w)
    seg = 8
    for i in range(seg):
        fc = 8000 * (400 / 8000) ** (i / (seg - 1))
        c = (i + 0.5) / seg * w * 0.5
        win = np.exp(-0.5 * ((np.arange(w) - c) / (w / seg * 0.5)) ** 2)
        air += bp(x, fc * 0.6, fc * 1.6) * win
        wsum += win
    air = air / np.maximum(wsum, 1e-6) * np.minimum(1, tw / 0.006) * np.exp(-tw / 0.2)
    place(out, smooth_end(air, 0.3), 0.0, 1.0)
    place(out, smooth_end(zap(0.3, 2600, 120, 0.035, 3.0)), 0.0, 0.5)
    place(out, smooth_end(sub_hit(0.7, 80, 40, tau=0.05, decay=0.14)), 0.0, 0.55)
    h, th = T(0.8)
    place(out, smooth_end(0.4 * lp(hum(th, 520, 130, 0.12), 1800) * np.exp(-th / 0.22), 0.5), 0.0)
    place(out, 0.6 * grains(int(SR * 0.5), 70, 4000, 11000, 0.003, 0.0008, 0.0, 0.35, lambda v: 1 - v), 0.0)
    return room_tail(out, 0.5, 5000, 0.22)


def portal_close():
    # 閉じる (約 1s): 0〜0.3s (絵が縦の線に縮む間) にうなりと放電の濁りが下へ吸い込まれ、帯域の下がる風が細る →
    # 0.3s に内側へつぶれる低い一撃と小さな破裂 → 火花が散って消える。壊れた時は 0.75〜0.85 倍の高さで鳴らす
    cut = 0.3
    n, t = T(2.0)
    out = np.zeros(n)
    k, tk = T(cut)
    u = tk / cut
    pull = smooth_end(np.ones(k), 0.05) * (0.4 + 0.6 * u)
    place(out, 0.45 * lp(hum(tk, 420, 70, 0.12), 2000) * pull, 0.0)
    place(out, 0.35 * fm(900 * (60 / 900) ** u, 1.5, 3.5 * (1 - u) + 0.3) * pull, 0.0)
    rise = riser(cut, 250, 6000, 1.5)[::-1]
    place(out, 0.6 * rise * smooth_end(np.ones(k), 0.1) * np.minimum(1, tk / 0.01), 0.0)
    place(out, smooth_end(sub_hit(1.0, 130, 38, tau=0.04, decay=0.2)), cut, 1.1)
    place(out, smooth_end(braam(55, 0.8, decay=0.12)), cut, 0.35)
    s, ts = T(0.15)
    place(out, bp(noise(s), 1500, 8000) * np.exp(-ts / 0.02), cut, 0.6)
    place(out, 0.5 * grains(int(SR * 0.6), 40, 3000, 11000, 0.003, 0.0007, 0.0, 0.4, lambda v: 1 - v), cut)
    return room_tail(out, 0.7, 4500, 0.3)


# ── エモート (身ぶりの音・本物の音に寄せる) ─────────────────────

def emote_clap(res, spread):
    # 拍手の 1 打: 手のひらの当たり始めから閉じきるまでに小さな破裂が 2〜3 回 (spread 秒ずつ) 起き、
    # 手のひらの間の空気が res 付近で短く鳴る。皮膚の高い擦れと掌の低い当たりを少し足して小さな部屋で響かせる
    n, t = T(0.3)
    x = np.zeros(n)
    m, tm = T(0.06)
    for dt, g, tau in [(0.0, 0.5, 0.003), (spread, 1.0, 0.004), (spread * 2.2, 0.35, 0.006)]:
        place(x, noise(m) * np.exp(-tm / tau), 0.004 + dt, g)
    air = bp(noise(n), res * 0.75, res * 1.35, order=3) * np.exp(-np.maximum(t - 0.004 - spread, 0) / 0.011)
    air[: int((0.004 + spread) * SR)] = 0
    body = bp(x, res * 0.6, res * 1.6) * 1.4 + hp(x, 4000) * 0.3 + bp(x, 200, 500) * 0.6 + air * 0.5
    return room_tail(smooth_end(body, 0.3), 0.22, 5000, 0.16)


def emote_land(gap, ring):
    # 着地: 両足が gap 秒ずれて床を打つ。1 歩ごとに低い当たり + 靴底の鈍い打音 + 床の金属の短い鳴り + 靴底の擦れ
    n, t = T(0.4)
    out = np.zeros(n)
    for at, g in [(0.004, 1.0), (0.004 + gap, 0.7)]:
        k, tk = T(0.25)
        low = np.sin(sweep(tk, 120, 55, 0.025)) * np.exp(-tk / 0.035)
        knock = bp(noise(k), 280, 1400) * np.exp(-tk / 0.012)
        deck = modal([(ring, 0.03, 0.25), (ring * 2.37, 0.02, 0.12)], 0.25, strike=0.002) * 0.3
        scuff = bp(noise(k), 1200, 6000) * np.exp(-tk / 0.02) * np.minimum(1, tk / 0.004) * 0.25
        place(out, smooth_end(1.2 * low + knock + deck + scuff, 0.3), at, g)
    return room_tail(out, 0.25, 3500, 0.15)


def emote_push():
    # 踏み切り: 靴底が床を後ろへ擦る (立ち上がりの遅い高い擦れ) + 軽い当たり + 服の揺れ
    n, t = T(0.25)
    env = np.minimum(1, t / 0.025) * np.exp(-t / 0.045)
    scuff = bp(noise(n), 900, 5000) * env
    k, tk = T(0.12)
    tap = np.sin(sweep(tk, 140, 80, 0.02)) * np.exp(-tk / 0.025) * 0.5
    cloth = bp(noise(n), 2000, 7000) * np.minimum(1, t / 0.04) * np.exp(-t / 0.06) * 0.35
    out = scuff + cloth
    place(out, tap, 0.0)
    return room_tail(smooth_end(out, 0.3), 0.2, 4000, 0.12)


def emote_swish(dur, lo, hi):
    # 袖の擦れ: 布の高い雑音がふくらんで消える (振る速さの山で一番大きく・帯が少し上がる) + 布のかさつき + 空気の低い流れ
    n, t = T(dur)
    u = t / dur
    env = np.sin(np.pi * np.minimum(1, u * 1.25)) ** 2
    a = bp(noise(n), lo, hi * 0.6) * (1 - u)
    b = bp(noise(n), lo * 1.6, hi) * u
    crinkle = grains(n, 140, 2500, 9000, dur=0.004, tau=0.001, shape=lambda v: np.sin(np.pi * v)) * 0.3
    air = bp(noise(n), 250, 900) * env * 0.2
    return smooth_end((a + b) * env + crinkle * env + air, 0.2)


def emote_rustle():
    # 手袋の指が曲がる時の擦れ: 短く小さい布のかさつき
    n, t = T(0.14)
    env = np.sin(np.pi * np.minimum(1, t / 0.12)) ** 2
    rub = bp(noise(n), 1800, 7000) * env * 0.6
    crinkle = grains(n, 180, 3000, 10000, dur=0.003, tau=0.0008, shape=lambda v: np.sin(np.pi * v)) * 0.4
    return smooth_end(rub + crinkle * env, 0.2)


# ── 時戻しの部品 (映画の予告編の型: 巨大な機構・低い大鐘・金管の唸り・低い弦と男声・長い暗い響き) ──
# 高い澄んだ鐘・明るい和音・きらめき・電子のはぜは使わない (小さく可愛く聞こえる)。和音は空五度と短調、音域は低く

def thick_bass(t, f, drive=2.5):
    # 歪ませた低音: 正弦だけだとスマホでは聞こえないので、倍音 (150〜500Hz) まで出して「重さ」を聞かせる。f は標本ごと
    ph = 2 * np.pi * np.cumsum(np.broadcast_to(f, t.shape)) / SR
    x = np.tanh(drive * (np.sin(ph) + 0.45 * np.sin(2 * ph + 0.7) + 0.2 * np.sin(3 * ph + 1.9)))
    return lp(x, 650, order=3)


def duck(n, hits, depth=0.6, attack=0.004, release=0.12):
    # 打つたびに下の層を一瞬沈める (打音の頭を立たせ、層どうしが脈打つ)。hits = (時刻, 強さ 0..1) の並び
    g = np.ones(n)
    m = int((attack + release * 5) * SR)
    tm = np.arange(m) / SR
    dip = depth * np.minimum(1, tm / attack) * np.exp(-np.maximum(0, tm - attack) / release)
    for at, d in hits:
        i = int(at * SR)
        if i >= n:
            continue
        k = min(m, n - i)
        g[i:i + k] = np.minimum(g[i:i + k], 1 - dip[:k] * d)
    return g


def hall(x, rt, tone=2500, mix=0.5, pre=0.025):
    # 大きな石の広間の響き: 低い所ほど長く残り、高い所は早く消える (明るい響きは小部屋に聞こえる)。尾が減り切るまで伸ばす
    x = np.concatenate([x, np.zeros(int(rt * SR))])
    n, t = T(rt * 1.3)
    low = lp(noise(n), 600) * np.exp(-t * 6.9 / rt)
    mid = bp(noise(n), 600, tone) * np.exp(-t * 6.9 / (rt * 0.55))
    ir = low + 0.6 * mid
    ir[:int(pre * SR)] = 0
    wet = fftconvolve(x, ir)[:len(x)]
    wet *= np.max(np.abs(x)) / (np.max(np.abs(wet)) + 1e-9)
    return x + mix * wet


def big_bell(f, dur=4.0, decay=1.0, strike=1.0):
    # 塔の大鐘 (f = 基音)。倍音は本物の鐘の比 (ハム 0.5・短三度 1.19・五度 1.5・…) で、わずかにずれた 2 本で唸らせる。
    # 叩いた瞬間は鋼の打撃と低い胴鳴り
    n, t = T(dur)
    x = np.zeros(n)
    for r, tau, g in ((0.5, 3.0, 0.9), (1.0, 2.0, 1.0), (1.19, 1.6, 0.75), (1.5, 1.1, 0.45), (2.0, 1.2, 0.6),
                      (2.5, 0.6, 0.3), (2.66, 0.55, 0.28), (3.01, 0.45, 0.22), (4.0, 0.3, 0.14), (5.33, 0.18, 0.08)):
        for d in (-0.0012, 0.0012):
            x += g * np.sin(2 * np.pi * f * r * (1 + d) * t + rng.uniform(0, 6.28)) * np.exp(-t / (tau * decay))
    hitn = bp(noise(n), f * 2, f * 12) * np.exp(-t / 0.006) + 0.8 * lp(noise(n), f * 1.5) * np.exp(-t / 0.03)
    return np.tanh(0.5 * x + strike * 1.5 * hitn)


CLOCK_TICKS = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'sounds', 'clock_tick_otologic.wav')
_clock = []


def clock_tick(weight=1.0, pitch=1.0):
    # 時計の秒針の 1 打 (録音した本物の音 4 種から選ぶ・CC BY 4.0 OtoLogic) を少し低く読み、
    # 箱の胴鳴りを weight の分だけ下に足す (時計の音のまま、大きな機構の重さを出す)。
    # 音程が落ちる低い正弦は刻むたびに小さな「ドッ」になるので入れない
    if not _clock:
        with wave.open(CLOCK_TICKS) as w:
            raw = np.frombuffer(w.readframes(w.getnframes()), np.int16) / 32768
        k = len(raw) // 4
        _clock.extend(raw[i * k:(i + 1) * k] for i in range(4))
    src = _clock[int(rng.integers(0, 4))]
    v = 0.92 * pitch * rng.uniform(0.98, 1.02)
    pos = np.arange(0, len(src) - 1, v)
    i0 = pos.astype(int)
    fr = pos - i0
    tick = src[i0] * (1 - fr) + src[i0 + 1] * fr
    n, t = T(0.3)
    x = np.zeros(n)
    x[:len(tick)] += tick
    case = bp(noise(n), 260, 900) * np.exp(-t / 0.018)
    x += weight * 0.3 * case
    return np.tanh(1.2 * x)


def groan(dur, r0, r1, modes, jitter=0.3):
    # 巨大な金属がきしんで唸る (擦れて引っかかる連打を鋼の低い鳴りに通す)。連打の速さ r0 → r1 回/秒が唸りの高さになる
    kn, kt = T(0.6)
    kernel = sum(g * np.sin(2 * np.pi * f * kt) * np.exp(-kt / tau) for f, tau, g in modes)
    return stick_slip(dur, r0, r1, kernel, jitter, 0.6)


def braam2(f, dur, attack=0.02, decay=1.2, open_tau=0.3):
    # 予告編の金管の唸り: 根音・五度・オクターブを 3 本ずつずらしたのこぎり波。頭で音色が開いてから閉じ (ぶわぁ…)、強く歪ませる
    n, t = T(dur)
    x = np.zeros(n)
    for mult, g in ((1.0, 1.0), (1.5, 0.6), (2.0, 0.55), (0.5, 0.5)):
        for d in (-0.006, 0.0, 0.006):
            x += g * sawtooth(2 * np.pi * f * mult * (1 + d) * t + rng.uniform(0, 6.28))
    bright = lp(x, 2600, order=3)
    dark = lp(x, 520, order=3)
    o = np.exp(-t / open_tau)
    y = np.tanh(2.2 * (o * bright + (1 - o) * dark) / 4)
    env = np.minimum(1, t / attack) * np.exp(-np.maximum(0, t - attack) / decay)
    return y * env


def strings(dur, notes, attack, release=0.05, trem=0.35):
    # 低い弦の刻み (トレモロ): 1 音を 5 本の少しずれたのこぎり波で、本ごとにばらばらの速さで揺らす
    n, t = T(dur)
    x = np.zeros(n)
    for f in notes:
        for d in np.linspace(-0.004, 0.004, 5):
            vib = 1 + 0.003 * np.sin(2 * np.pi * rng.uniform(4.8, 5.8) * t + rng.uniform(0, 6.28))
            am = 1 - trem * (0.5 + 0.5 * np.sin(2 * np.pi * rng.uniform(11, 15) * t + rng.uniform(0, 6.28)))
            x += am * sawtooth(2 * np.pi * np.cumsum(f * (1 + d) * vib) / SR + rng.uniform(0, 6.28))
    x = lp(x, 1800, order=3) + 0.3 * bp(x, 2000, 4000)
    env = (np.minimum(1, t / attack) ** 2) * np.minimum(1, (dur - t) / release).clip(0, 1)
    return x / (5 * len(notes)) * env


def male_choir(dur, notes, attack, release=0.1):
    # 低い男声の「おー」: 400 / 800Hz の山で削ったのこぎり波 (明るい「あ」の山は使わない)
    n, t = T(dur)
    x = np.zeros(n)
    for f in notes:
        for d in (-0.005, 0.0, 0.005):
            vib = 1 + 0.004 * np.sin(2 * np.pi * rng.uniform(4.5, 5.5) * t + rng.uniform(0, 6.28))
            x += sawtooth(2 * np.pi * np.cumsum(f * (1 + d) * vib) / SR + rng.uniform(0, 6.28))
    v = bp(x, 330, 480) + 0.55 * bp(x, 700, 900) + 0.12 * bp(x, 2400, 2800) + 0.4 * lp(x, 300)
    env = np.minimum(1, t / attack) * np.minimum(1, (dur - t) / release).clip(0, 1)
    return v / (3 * len(notes)) * env


def crash(dur=2.5):
    # シンバルの大きな一撃: 粗い金属の雑音 (高い所ほど早く消える)
    n, t = T(dur)
    x = bp(noise(n), 3000, 11000) * np.exp(-t / (dur * 0.25)) + bp(noise(n), 900, 3000) * np.exp(-t / (dur * 0.12))
    return x + 1.5 * bp(noise(n), 500, 4000) * np.exp(-t / 0.01)


def pre_echo(x, rt=0.5, tone=4000):
    # 一撃の響きだけを逆に鳴らす (一撃へ向かって吸い込まれる)。返り値の終わりを一撃の頭に合わせて置く
    ir_n, t = T(rt)
    ir = lp(noise(ir_n), tone) * np.exp(-t * 6.9 / rt)
    wet = fftconvolve(x[:int(0.3 * SR)], ir)[:ir_n]
    return wet[::-1] / (np.max(np.abs(wet)) + 1e-9)


def feather_slide(dur, size=1.0):
    # 羽根の束が擦れながら開く 1 回分: 羽根どうしの細かい摩擦の粒 (2〜9kHz・動きの速さで密度が増減) +
    # 羽根の塊が滑る中域の雑音 (不規則に引っかかる) + 羽軸と関節のきしみ
    # 動きは素早く開いて (全体の 2 割で最大)、ゆっくり収まる
    n, t = T(dur)
    u = t / dur
    motion = np.where(u < 0.2, (u / 0.2) ** 1.5, ((1 - u) / 0.8) ** 1.3)
    fr = grains(n, 900 * size, 1800, 7000, dur=0.004, tau=0.0009, t0=0.0, t1=dur,
                shape=lambda v: (v / 0.2) ** 1.5 if v < 0.2 else ((1 - v) / 0.8) ** 1.3)
    catch = (lp(noise(n), 35) * 9 + 0.3).clip(0, None) ** 1.4
    mass = bp(noise(n), 280, 1500) * catch * motion
    soft = bp(noise(n), 1200, 4000) * catch ** 0.7 * motion
    quill = groan(dur, 70, 140, ((480.0, 0.012, 1.0), (830.0, 0.01, 0.6), (1370.0, 0.006, 0.35)), 0.4)
    return 0.6 * fr + 1.0 * mass + 0.45 * soft + 0.25 * size * quill / (np.max(np.abs(quill)) + 1e-9)


def wing_spread(dur, size=1.0, events=4):
    # 翼が広がる: 根元から先へ羽根の束が順に開き (間が詰まりながら強まる)、開き切りで帆が張るように空気を押し出す
    # 開き切った後も羽根が数回ばらばらと収まる (1 つの雑音の塊に聞こえないよう、束ごとの頭を立てて間を空ける)
    n, t = T(dur + 1.2)
    out = np.zeros(n)
    for k in range(events):
        u = k / max(1, events - 1)
        at = dur * 0.8 * u ** 0.8 * rng.uniform(0.9, 1.0)
        d = max(0.07, dur * rng.uniform(0.35, 0.5))
        place(out, feather_slide(d, size), at, (0.5 + 0.5 * u) * rng.uniform(0.85, 1.0))
    end = dur
    at, gn = end + 0.1, 0.35
    for _ in range(3):
        place(out, feather_slide(rng.uniform(0.08, 0.14), size * 0.6), at, gn)
        at += rng.uniform(0.09, 0.16)
        gn *= 0.6
    # 開き切り: 張った羽根がばさっと鳴る (強い立ち上がりの中域) + 押し出された空気の低い「ぶわっ」+ 通り過ぎる風
    m, tm = T(0.6)
    taut = bp(noise(m), 120, 1400) * np.exp(-tm / 0.05) * np.minimum(1, tm / 0.004)
    place(out, taut, end - 0.01, 0.9 * size)
    place(out, grains(m, 2500, 2500, 9000, dur=0.003, tau=0.0007, t0=0.0, t1=0.12, shape=lambda v: (1 - v) ** 2),
          end - 0.01, 0.5 * size)
    m, tm = T(0.9)
    air = lp(noise(m), 160, order=3) * (tm / 0.06).clip(0, 1) * np.exp(-tm / 0.22)
    place(out, 3.0 * air, end - 0.04, size)
    place(out, whoosh(0.5, 140, 1100, 0.25), end - 0.12, 0.7 * size)
    return out


def whoosh(dur, lo, hi, peak=0.5):
    # 物が横切る風: 帯域が上がって下がる雑音。peak = 最も強い所 (長さに対する割合)
    n, t = T(dur)
    u = t / dur
    env = np.where(u < peak, (u / peak) ** 2, ((1 - u) / (1 - peak)) ** 1.5)
    x = noise(n)
    f = lo + (hi - lo) * env
    out = np.zeros(n)
    for i in range(6):
        fc = lo * (hi / lo) ** (i / 5)
        out += bp(x, fc * 0.7, fc * 1.4) * np.exp(-0.5 * ((f - fc) / (fc * 0.5)) ** 2)
    return out * env


def cloth_off(lo):
    # 着せ替えが引き剥がされて飛ぶ: 布の裂け + 風 + 小さな一押し
    n, t = T(0.3)
    x = tear(0.18, lo, lo * 4, 60)
    x = np.concatenate([x, np.zeros(n - len(x))])
    x += whoosh(0.3, lo * 0.5, lo * 3, 0.2)
    return x + 0.6 * lp(noise(n), 250) * np.exp(-t / 0.03) + 0.8 * bp(noise(n), lo, lo * 6) * np.exp(-t / 0.004)


def freeze(dur=0.6):
    # 時が凍る (青く反転してひびが走るコマ): 一気に走るひびの破裂 + 氷のように細かく鳴り続けるきしみ + ガラスの短い鳴り
    n, t = T(dur)
    x = np.zeros(n)
    place(x, crack(0.12, 1800, 9000), 0.0, 1.0)
    x += 0.5 * grains(n, 380, 2500, 9000, dur=0.004, tau=0.001, t0=0.0, t1=dur * 0.7, shape=lambda u: (1 - u) ** 2)
    x += 0.35 * modal(((2730.0, 0.05, 1.0), (3910.0, 0.04, 0.7), (5480.0, 0.03, 0.5)), dur, 0.002)
    return x


def burst(dur=0.7, warm=1.0):
    # 光が放射状に広がる: 鋭く開いた明るい風が外へ抜けながら暗くなり (高い所ほど早く消える)、外へ飛ぶ光の筋が横切る。
    # 押し出された空気の低い所は 40ms かけて立ち上げる (叩いた音にしない)。warm を上げると中域が厚く柔らかくなる
    n, t = T(dur)
    att = np.minimum(1, t / 0.006)
    x = hp(noise(n), 4000) * np.exp(-t / 0.05) + bp(noise(n), 1000, 4000) * np.exp(-t / 0.12)
    x += warm * bp(noise(n), 300, 1200) * np.exp(-t / 0.25)
    x *= att
    for _ in range(5):
        place(x, whoosh(rng.uniform(0.12, 0.25), 1500, 7000, 0.25), rng.uniform(0.0, 0.12), rng.uniform(0.2, 0.4))
    x += 3.0 * lp(noise(n), 200, order=3) * np.minimum(1, t / 0.04) * np.exp(-t / 0.25)
    return smooth_end(x, 0.3)


def shards(dur, rate=40):
    # ガラスの破片が散る: 1 つずつ高さの違う短い鳴りがまばらに続いて減る
    n, _ = T(dur)
    out = np.zeros(n)
    at = 0.0
    while at < dur * 0.8:
        f = rng.uniform(1500, 4500)
        place(out, modal(((f, 0.025, 1.0), (f * 1.53, 0.015, 0.5)), 0.08, 0.001), at,
              (1 - at / dur) ** 1.5 * rng.uniform(0.4, 1.0))
        at += rng.exponential(1.0 / rate)
    return out


# ── 時戻しの音 ─────────────────────────────────────────
# 発動者の演出を 1 つの調と 1 つの動機でつなぐ。持続音 (擦ったガラスの和音と息の多い高い声) は場面が
# 変わっても鳴り続け、時計の刻みは全編で同じ 1 打 (順向きに刻む → 凍って止まる → 逆に読んで戻る)。
# 和音は解決しない響き: 'A' = リディア (A E G# D#・明るい神秘)・'B' = 短調に長七度 (A C E G#・暗い神秘)。
# 動機は 4 音で、入りでは順向きに 3 音目まで鳴って時が凍り、巻き戻しで逆順に上り、最後の根音は REWIND の一撃で鳴る
REWIND_KEYS = {
    'A': dict(chord=(110.0, 164.81, 207.65, 311.13), high=(440.0, 659.26, 830.61, 1244.51),
              motif=(329.63, 311.13, 246.94, 220.0)),
    'B': dict(chord=(110.0, 130.81, 164.81, 207.65), high=(440.0, 523.25, 659.26, 830.61),
              motif=(329.63, 261.63, 207.65, 220.0)),
}
REWIND_KEY = 'A'
def glass_pad(dur, notes, shimmer=0.2):
    # 擦ったガラスの持続音: ほぼ正弦の基音を少しずれた 2 本でゆっくり唸らせ、弱い倍音と擦る息を足す。
    # 1 オクターブと 5 度上の薄い写しが上に浮く (金管や低い男声の重さとは別の、浮いた響き)
    n, t = T(dur)
    x = np.zeros(n)
    for f in notes:
        beat = rng.uniform(0.25, 0.7)
        amp = 0.75 + 0.25 * np.sin(2 * np.pi * rng.uniform(0.15, 0.4) * t + rng.uniform(0, 6.28))
        for d, g in ((-beat / 2, 1.0), (beat / 2, 0.8)):
            ph = rng.uniform(0, 6.28)
            fd = f + d
            x += amp * g * (np.sin(2 * np.pi * fd * t + ph) + 0.15 * np.sin(4 * np.pi * fd * t + ph))
            x += amp * g * shimmer * np.sin(2 * np.pi * 3 * fd * 1.0007 * t + rng.uniform(0, 6.28))
        x += 0.05 * amp * bp(noise(n), f * 0.92, f * 1.1)
    return x / len(notes)


def air_choir(dur, notes, attack, release=0.1):
    # 息の多い高い声の「うー」: 細いのこぎり波の上を削って柔らかくし、同じ高さの息を混ぜる。
    # 明るい「あ」の山は使わない (かわいく聞こえる)
    n, t = T(dur)
    x = np.zeros(n)
    for f in notes:
        for d in (-0.004, 0.0, 0.004):
            vib = 1 + 0.005 * np.sin(2 * np.pi * rng.uniform(4.5, 5.5) * t + rng.uniform(0, 6.28)) * np.minimum(1, t / 0.5)
            x += sawtooth(2 * np.pi * np.cumsum(f * (1 + d) * vib) / SR + rng.uniform(0, 6.28))
        x += 2.5 * bp(noise(n), f * 0.97, f * 1.03)
    v = lp(x, 1300, order=3) + 0.06 * bp(x, 2500, 3200)
    env = np.minimum(1, t / attack) * np.minimum(1, (dur - t) / release).clip(0, 1)
    return v / (3 * len(notes)) * env


def crystal(f, dur=2.5, decay=1.0):
    # 水晶の鈴: 倍音が整数比でない (1 : 2.76 : 5.40 : 8.93) ので高さが浮いて聞こえる。わずかにずれた 2 本で唸る
    n, t = T(dur)
    x = np.zeros(n)
    for r, tau, g in ((1.0, 1.6, 1.0), (2.76, 0.7, 0.45), (5.40, 0.35, 0.25), (8.93, 0.18, 0.12)):
        for d in (-0.0008, 0.0008):
            x += g * np.sin(2 * np.pi * f * r * (1 + d) * t + rng.uniform(0, 6.28)) * np.exp(-t / (tau * decay))
    return x + 0.3 * bp(noise(n), f * 2, min(f * 10, 14000)) * np.exp(-t / 0.003)


def reverse_bell(notes, dur=1.0, decay=0.6):
    # 鈴の響きの逆再生: ふくらんで、打った瞬間でぷつりと止まる (時が逆に流れる印)。返り値の終わりを合図に合わせて置く
    x = sum(crystal(f, dur, decay) for f in notes)
    x = hall(x, 1.4, 6000, 0.6)[:int(dur * SR)][::-1].copy()
    k = int(0.004 * SR)
    x[-k:] *= np.linspace(1, 0, k)
    return x


def tick_back(weight=0.8, pitch=1.0):
    # 逆に読んだ時計の 1 打 (胴鳴りが先にふくらみ、かちで終わる)。返り値の終わりを合図に合わせて置く
    x = clock_tick(weight, pitch)[:int(0.12 * SR)][::-1].copy()
    k = int(0.002 * SR)
    x[-k:] *= np.linspace(1, 0, k)
    return x


def grain_freeze(x, at, dur, win=0.12, grain=0.09):
    # 時が止まる: at の直前 win 秒のかけらを窓を掛けた粒で読み直し続け、鳴っていた音をその形のまま止めて鳴らし続ける
    src = x[max(0, int((at - win) * SR)):int(at * SR)]
    n, g = int(dur * SR), int(grain * SR)
    w = np.hanning(g)
    y = np.zeros(n + g)
    for i in range(0, n, g // 4):
        s = int(rng.integers(0, len(src) - g))
        y[i:i + g] += src[s:s + g] * w
    return y[:n] / 2.0


def scrub_by(x, v):
    # x を後ろから逆向きに読む。v = 標本ごとの読む速さ (速いほど高く鳴く)
    pos = len(x) - 1 - np.cumsum(v)
    assert pos[-1] > 0, '元の音が足りない (頭を越えると継ぎ目で鳴る)'
    i0 = pos.astype(int)
    fr = pos - i0
    return x[i0] * (1 - fr) + x[np.minimum(i0 + 1, len(x) - 1)] * fr


def hit_stack(key, weight=1.0, root=55.0, dur=4.0):
    # 決めの一撃を総奏で: 金管・低い男声・弦が調の和音で同時に鳴り出し、その上に息の多い高い声とガラスの和音が浮き、
    # 大鐘と動機の最後の根音の鈴とシンバルが光る。太鼓・衝撃・音程が落ちる低音 (短く鈍い「ドッ」) は入れない。
    # 重さは直前の無音との差と、鳴り続ける胴で出す
    n, t = T(dur)
    x = 1.0 * braam2(root, dur, 0.006, 0.9 * weight, 0.25)[:n]
    x += 0.4 * braam2(root * 2, dur, 0.006, 0.7 * weight, 0.2)[:n]
    x += 1.3 * male_choir(dur, list(key['chord']), 0.012, 0.05)[:n] * np.exp(-t / (1.1 * weight))
    x += 1.0 * strings(dur, [root, root * 2, key['chord'][1], key['chord'][2]], 0.008, 0.05, 0.5)[:n] * np.exp(-t / (0.9 * weight))
    x += 0.5 * thick_bass(t, root, 2.2) * np.minimum(1, t / 0.018) * np.exp(-t / (0.8 * weight))
    # 驚きを起こす広い帯域の立ち上がり (2ms で開いて 0.15 秒で抜ける空気)
    x += 0.5 * bp(noise(n), 300, 9000) * np.minimum(1, t / 0.002) * np.exp(-t / 0.15)
    x += 1.6 * air_choir(dur, list(key['high']), 0.01, 0.05)[:n] * np.exp(-t / (1.8 * weight))
    x += 0.7 * glass_pad(dur, list(key['high']), 0.3) * np.minimum(1, t / 0.01) * np.exp(-t / (2.2 * weight))
    place(x, big_bell(root * 2, dur, decay=weight, strike=0.4), 0.0, 0.45)
    place(x, crystal(key['motif'][3] * 2, dur, 1.2), 0.0, 0.5)
    place(x, crash(dur), 0.0, 0.25)
    return smooth_end(x, 0.4)


# 時戻しの発動者だけに出す全画面の演出 (30fps・137 コマ) の音。合図はコマ番号で持つ (絵を描き直したらここを揃える)
ULT_FPS = 30
ULT_FRAMES = 137
# 通しの絵のコマごとの明るさ (0..1) と動き (前のコマとの差)。音の量をこれで動かす (光ると響きがきらめき、動くと風が鳴る)
ULT_LUM = (
    .20, .20, .19, .18, .17, .16, .16, .19, .43, .56, .74, 1.0, 1.0, 1.0, .43, .45, .46, .48, .47, .47,
    .47, .46, .45, .45, .45, .45, .37, .38, .41, .43, .44, .44, .49, .49, .80, .97, .69, .58, .57, .51,
    .43, .42, .45, .47, .48, .50, .50, .51, .52, .51, .50, .49, .49, .50, .52, .50, .51, .50, .47, .50,
    .54, .54, .51, .42, .45, .47, .48, .48, .49, .50, .51, .54, .54, .56, .59, .58, .59, .80, .69, .65,
    .67, .68, .70, .70, .68, .68, .66, .66, .64, .62, .62, .60, .59, 1.0, .67, .37, .46, .57, .97, .79,
    .71, .66, .71, .66, .65, .64, .64, .63, .63, .62, .81, .74, .72, .44, .76, .74, 1.0, 1.0, 1.0, 1.0,
    .45, .42, .39, .40, .37, .36, .32, .32, .27, .24, .20, .20, .20, .20, 1.0, 1.0, 1.0)
ULT_MOT = (
    .00, .00, .02, .05, .07, .08, .06, .04, .24, .13, .19, .26, .00, .00, .57, .07, .08, .08, .08, .07,
    .07, .07, .06, .06, .05, .05, .23, .04, .07, .07, .07, .05, .13, .15, .41, .20, .28, .15, .10, .11,
    .11, .19, .05, .06, .07, .08, .08, .08, .08, .08, .08, .08, .08, .08, .09, .09, .07, .09, .12, .11,
    .12, .10, .14, .24, .04, .06, .07, .07, .07, .08, .07, .09, .08, .09, .16, .12, .10, .23, .13, .09,
    .08, .08, .08, .08, .07, .07, .07, .07, .08, .07, .07, .07, .07, .41, .32, .30, .14, .16, .40, .18,
    .13, .13, .12, .11, .08, .08, .08, .08, .08, .08, .31, .12, .09, .50, .50, .11, .26, .00, .00, .00,
    .55, .05, .04, .05, .03, .04, .05, .03, .05, .07, .07, .06, .06, .05, .80, .00, .00)


def ult_at(frame):
    return (frame - 1) / ULT_FPS


def ult_curve(values, n, smooth=0.06):
    # コマごとの値を標本ごとに引き伸ばし、コマの継ぎ目で段差が鳴らないよう smooth 秒でならす
    x = np.interp(np.arange(n) / SR * ULT_FPS, np.arange(len(values)), values)
    k = int(smooth * SR)
    return np.convolve(x, np.ones(k) / k, mode='same')


def clock_hand(f0=42, f1=63, step=30.0):
    # 時計の逆回転のカットの分針 (絵と同じ式: 1 コマの角度 = 8 + 70t² 度) が step 度回るごとの時刻
    times, ang, mark = [], 0.0, step
    for f in range(f0, f1 + 1):
        u = (f - f0) / (f1 - f0)
        sp = 8 + 70 * u * u
        while ang + sp >= mark:
            times.append(ult_at(f) + (mark - ang) / sp / ULT_FPS)
            mark += step
        ang += sp
    return times


def rewind_ult():
    # 入り (自分へ寄って時計が刻み、動機が順に 3 音 → 光へ吸い込まれて白) → 金の世界でガラスの和音と高い声が鳴り続ける →
    # 指を振り下ろすと世界が凍る (鳴っていた和音がその形のまま止まって鳴り続け、刻みは途中で止まる) → 時計の逆回転
    # (凍った和音を針の速さで逆に読み、刻みは針が 30 度回るごとに逆の 1 打・3 倍で回る小針が 1 周するごとに動機を逆順に上る) →
    # 変身 (着せ替えが 1 つ剥がれるごとに声が 1 つ増える・光の量で響きがきらめく・光の輪で鈴・翼が小さく開く) →
    # 翼の全開 → 動機の逆順と逆の刻みが速まって無音 → REWIND の総奏の一撃で動機の最後の根音が鳴る →
    # 着せ替えが逆再生で戻る → 最後の光が消える (131) と音も消える (その後の白と試合の巻き戻しには音を付けない)。
    # 太鼓のような短く鈍い一撃と指を鳴らす音は使わない。山場は REWIND の 1 回だけで、直前の無音との差で大きく聞かせる
    K = REWIND_KEYS[REWIND_KEY]
    F = ult_at
    snap, c0, c1 = F(35), F(42), F(64)
    n, t = T(F(ULT_FRAMES) + 0.6)
    lum = ult_curve(ULT_LUM, n)
    mot = ult_curve(ULT_MOT, n)
    glow = ((lum - 0.15) / 0.35).clip(0, 1) ** 1.2      # 和音の量
    sparkle = ((lum - 0.55) / 0.45).clip(0, 1) ** 2     # 白くなるほど上に浮く高い和音
    bed = np.zeros(n)
    ticks = np.zeros(n)
    out = np.zeros(n)
    tick_hits = []

    # 持続の和音 (全編で同じ物を 1 本・凍る所から逆回転の終わりまでだけ止めて、凍った音と逆読みに代わる)。
    # 和音は 1 オクターブ上で鳴らし、低い所は REWIND の一撃まで空けておく (一撃で初めて低音が戻る)
    pad = glass_pad(n / SR, [f * 2 for f in K['chord']])[:n]
    shim = glass_pad(n / SR, K['high'], 0.4)[:n]
    bed += pad * (0.05 + 0.3 * glow) + 0.35 * shim * sparkle
    # 金の世界で高い声が入る (15)。変身では着せ替えが 1 つ剥がれるごとに声が 1 つ増える (70・73・75)
    for fr, notes, gain in ((15, K['high'][:2], 0.4), (64, K['high'][:1], 0.35), (70, K['high'][1:2], 0.35),
                            (73, K['high'][2:3], 0.35), (75, K['high'][3:4], 0.3)):
        place(bed, air_choir(F(ULT_FRAMES) - F(fr), list(notes), 0.25, 0.2), F(fr), gain)

    # 入り・金の世界: 0.2 秒ごとの時計の刻み。動機を 1 打おきに順に鳴らし、3 音目で止まる (4 音目は凍って鳴らない)
    at, k = 0.0, 0
    while at < snap - 0.05:
        place(ticks, clock_tick(0.6 if at < F(12) else 0.9), at, 0.6 if at < F(12) else 0.8)
        tick_hits.append((at, 0.5))
        if k % 2 == 0 and k // 2 < 3:
            place(out, crystal(K['motif'][k // 2], 2.0, 0.9), at, 0.3)
        at += 0.2
        k += 1
    # 光が満ちて白 (8〜12): 和音の鈴の響きが逆にふくらんで白の瞬間で止まり、光の風
    rb = reverse_bell(K['high'][1:], F(12), 0.2)
    place(out, rb, 0.0, 0.35)
    place(out, burst(0.5, 0.6), F(8), 0.15)
    # 手を振り上げる袖の風
    place(out, whoosh(F(34) - F(27), 300, 2200, 0.8), F(27), 0.3)

    # 世界が凍る (35 で青く反転してひびが走る → 36 白 → 37〜40 光が放射状): 凍る前の世界を響きごと仕上げて凍った瞬間に
    # 止め、止まる直前の響きを粒で読み直し続けて、鳴っていた音をその形のまま鳴らし続ける (刻みと鈴の響きも途中で止まる)。
    # ひびと光は高い所だけで小さく鳴る。止まった音は 42 からの逆回転の元になる
    a, k = int(snap * SR), int(0.004 * SR)
    pre_bed = bed.copy()
    pre_bed[a:] = 0
    pre = out + pre_bed * duck(n, tick_hits, 0.5, 0.003, 0.07) + 1.5 * hall(ticks, 1.2, 2500, 0.35)[:n]
    pre = hall(pre, 2.2, 3500, 0.45)[:n]
    frozen = grain_freeze(pre, snap, 8.0)
    pre[a - k:a] *= np.linspace(1, 0, k)
    pre[a:] = 0
    out[:] = 0
    ticks[:] = 0
    tick_hits = []
    frz = np.zeros(n)
    m = int((c0 - snap) * SR)
    place(frz, frozen[:m] * np.linspace(0.9, 0.6, m), snap, 0.55)
    place(frz, freeze(0.6), snap, 0.3)
    place(frz, hp(burst(0.7, 0.3), 1200), F(36), 0.2)
    m2, tm = T(F(44) - F(36))
    ring = np.sin(2 * np.pi * 6200 * tm) + 0.5 * np.sin(2 * np.pi * 6230 * tm)
    place(frz, ring * np.minimum(1, tm / 0.05) * (1 - tm / tm[-1]) ** 2, F(36), 0.012)

    # 時計の逆回転 (42〜63): 凍った和音を針の速さで逆に読む (読む速さは針の 1 コマの角度に合わせて 1 → 4.5 倍)・
    # 刻みは針が 30 度回るごとに逆の 1 打 (終わりは 1 秒に 78 打で唸りになる)・小針が 1 周するごとに動機を逆順に上る
    m, tm = T(c1 - c0)
    u = (tm / (F(63) - c0)).clip(0, 1)
    sc = scrub_by(frozen, 1.0 + 3.5 * u * u)
    place(frz, smooth_end(hp(sc, 150), 0.04) * (0.35 + 0.65 * u), c0, 0.7)
    for at in clock_hand():
        u = (at - c0) / (F(63) - c0)
        tb = tick_back(0.6 + 0.4 * u, 1.0 + 0.06 * u)
        place(ticks, tb, at - len(tb) / SR, 0.75 - 0.35 * u)
        tick_hits.append((at, 0.4))
    notes = list(K['motif'][::-1]) + [K['motif'][3] * 2]
    for k, at in enumerate(clock_hand(step=120.0)):
        rb = reverse_bell([notes[k % len(notes)]], 0.5, 0.5)
        place(out, rb, at - len(rb) / SR, 0.4)
    # 稲妻と砕け: 空気が裂ける細い破裂 → ガラスの砕け → 中心へ吸い込む風 (渦の動きの量で強める)
    for k, fr in enumerate((55, 57)):
        place(out, crack(0.12, 1500, 9000), F(fr), 0.3 + 0.1 * k)
    place(out, crack(0.35, 2000, 9000), F(59), 0.45)
    place(out, shards(0.6), F(59), 0.35)
    r = riser(c1 - F(57), 200, 6000, 3.0)
    a = int(F(57) * SR)
    place(out, r * (0.3 + 3 * mot[a:a + len(r)]), F(57), 0.35)

    # 変身 (64〜93): 渦の中心から抜けると同じ和音が戻っている (上の bed)。着せ替えが剥がれて飛ぶ
    for fr, gn, lo in ((70, 0.3, 700), (73, 0.3, 900), (75, 0.45, 400)):
        place(out, cloth_off(lo), F(fr), gn)
    place(out, burst(0.9, 1.6), F(78), 0.3)
    # 光の輪が灯る (鈴) と翼が小さく開く
    place(out, crystal(K['high'][3] / 2, 2.5, 1.0), F(84), 0.25)
    place(out, wing_spread(F(88) - F(84), 0.55, 3), F(84), 0.4)

    # カット 4 (94〜99): 白の閃きへ和音の鈴の逆再生が吸い込まれて止まる → 渦の風 (動きの量) → 99 の白で翼の全開
    rb = reverse_bell(K['high'][:3], 0.6, 0.6)
    place(out, rb, F(94) - len(rb) / SR, 0.2)
    w = whoosh(F(99) - F(95), 200, 2500, 0.95)
    a = int(F(95) * SR)
    place(out, w * (0.5 + 2 * mot[a:a + len(w)]), F(95), 0.3)
    place(out, wing_spread(F(99) - F(94), 1.0, 5), F(94), 0.4)
    place(bed, air_choir(F(111) - F(99), list(K['high'][:2]) + [K['high'][0] * 2], 0.4, 0.05), F(99), 0.3)

    # REWIND へ (100〜107): 和音が満ち、逆さのシンバルがふくらみ、動機の逆順が逆の刻みと一緒に速まって上る。
    # ふくらみは一撃の 4 コマ前 (gap) でぷつりと切れ、無音を挟んでから一撃 (直前の無音があると一撃がずっと大きく聞こえる)
    rw = F(111)
    gap = rw - 4 / ULT_FPS
    m, tm = T(gap - F(100))
    place(out, crash(m / SR)[::-1] * (tm / tm[-1]) ** 2, F(100), 0.3)
    place(bed, glass_pad(m / SR, K['high'], 0.5) * (tm / tm[-1]) ** 1.5, F(100), 0.25)
    for before, f in zip((0.45, 0.28, 0.16, 0.07), K['motif'][::-1]):
        rb = reverse_bell([f], 0.4, 0.4)
        place(out, rb, gap - before - len(rb) / SR, 0.35)
        tb = tick_back(0.9, 1.0)
        place(ticks, tb, gap - before - len(tb) / SR, 0.6)
        tick_hits.append((gap - before, 0.6))

    # REWIND の決め: 一撃そのものの響きを逆に鳴らして無音の頭まで吸い込み → 無音 → 総奏の一撃で動機の根音が鳴る。
    # 114 は 35 と同じ青く反転するコマなので同じ凍るひびで追い打つ
    hit = hit_stack(K, 1.2, 55.0, 1.8)
    pe = pre_echo(hit, 0.6)
    place(out, pe, gap - len(pe) / SR, 0.55)
    place(out, hit, rw, 1.5)
    place(out, freeze(0.5), F(114), 0.5)

    # 出 (121〜): 着せ替えが逆再生で戻る (逆さの風が着く所で終わり、逆の刻みで留まる)
    for fr, lo in ((124, 700), (126, 900), (128, 450)):
        w = cloth_off(lo)[::-1]
        place(out, w, F(fr) - len(w) / SR, 0.3)
        tb = tick_back(0.4, 1.2)
        place(ticks, tb, F(fr) - len(tb) / SR, 0.45)

    # 和音は凍る前の分を上で仕上げたので、ここからは逆回転の終わり (64) で戻る所から鳴らす。縁は 30ms でつなぐ。
    # REWIND の一撃の後は一撃の響きを前に出し、和音と声は 3 割まで下げて残す
    e = np.zeros(n)
    b, k = int(c1 * SR), int(0.03 * SR)
    e[b:] = 1
    e[b:b + k] = np.linspace(0, 1, k)
    e[int(rw * SR):] = 0.3
    bed *= e
    # 刻みは時計とはっきり分かる大きさで前に出し、刻むたびに下の和音を沈める
    out = out + bed * duck(n, tick_hits, 0.5, 0.003, 0.07) + 1.5 * hall(ticks, 1.2, 2500, 0.35)[:n]
    out = pre + hall(out, 2.2, 3500, 0.45)[:n] + hall(frz, 1.5, 6000, 0.3)[:n]
    # REWIND の一撃の直前 4 コマは本当の無音 (響きの尾も残さない)。縁は 12ms の余弦で切る (ぷつ音を出さない)
    e = np.ones(n)
    a, b = int(gap * SR), int(rw * SR)
    k = int(0.012 * SR)
    e[a:a + k] = 0.5 + 0.5 * np.cos(np.linspace(0, np.pi, k))
    e[a + k:b] = 0
    out *= e
    # 最後の光が薄れる 126 から消え切る 131 までで音も消す
    e = np.ones(n)
    a, b = int(F(126) * SR), int(F(131) * SR)
    e[a:b] = 0.5 + 0.5 * np.cos(np.linspace(0, np.pi, b - a))
    e[b:] = 0
    return out * e


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
        emit('noise_burn_hit_' + mat, lambda m=mat: burn_hit(m), peak=0.85, drive=1.8, ratio=0.35)
        emit('noise_burn_crumble_' + mat, lambda m=mat: burn_crumble(m), drive=1.6, ratio=0.3)
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
    emit_loop('noise_drone_loop', drone_loop, peak=0.7)
    emit('noise_fire_ignite', fire_ignite, peak=0.85, drive=1.8, ratio=0.35)
    emit('noise_steam', steam, peak=0.75, drive=1.6, ratio=0.3)
    emit('noise_flare', flare, peak=0.95, drive=2.0)
    emit('noise_arc', arc, peak=0.7, drive=1.6, ratio=0.3)
    emit('noise_portal_open', portal_open, peak=0.8, drive=1.6, ratio=0.3, trim=True)
    emit('noise_portal_pass', portal_pass, peak=0.85, drive=1.8, ratio=0.35, trim=True)
    emit('noise_portal_close', portal_close, peak=0.8, drive=1.6, ratio=0.3, trim=True)
    # 身ぶりの音は打った瞬間の形を残すため圧縮をごく弱く
    for i, (res, spread) in enumerate([(1250, 0.0022), (1650, 0.0016), (1050, 0.0028)], 1):
        emit(f'noise_emote_clap_{i}', lambda r=res, sp=spread: emote_clap(r, sp), peak=0.9, drive=1.2, ratio=0.1, trim=True)
    for i, (gap, ring) in enumerate([(0.014, 410), (0.022, 360)], 1):
        emit(f'noise_emote_land_{i}', lambda g=gap, r=ring: emote_land(g, r), peak=0.9, drive=1.3, ratio=0.12, trim=True)
    emit('noise_emote_push', emote_push, peak=0.8, drive=1.2, ratio=0.1, trim=True)
    emit('noise_emote_swish_1', lambda: emote_swish(0.2, 1200, 6500), peak=0.8, drive=1.2, ratio=0.1)
    emit('noise_emote_swish_2', lambda: emote_swish(0.17, 1500, 7500), peak=0.8, drive=1.2, ratio=0.1)
    emit('noise_emote_rustle', emote_rustle, peak=0.7, drive=1.2, ratio=0.1)
    reseed('noise_end_spectate')
    save('noise_end_spectate', fade(master(fade(end_spectate()), 1.3, 0.15), r=0.3), SR, 0.7)
