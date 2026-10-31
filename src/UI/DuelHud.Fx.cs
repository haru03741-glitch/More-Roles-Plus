using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.UI;

// 立ち合いの HUD の燃える演出: 体力のバーから立つ炎 (MRP/UiFire のシェーダ・動きはシェーダの中の時刻で決まる)・
// 火の粉 (昇る粒と飛び散る火花)・横切る光の筋・残りが少ない時の脈打ち・削られた所の白い光・∞ の後ろの炎の光・題字の火の粉・
// 鍔迫り合いの境目の火花。粒は Embers 個を使い回し、毎フレーム書くのは生きている粒と、動いている光だけ
// (炎の板は残りと熱さが変わった時だけ色を書く)
internal static partial class DuelHud
{
    private const int Embers = 40;
    private const float LowHp = 0.35f, SheenEvery = 2.6f, SheenTime = 0.55f, CutFlash = 0.18f;
    private const float EmberZ = -0.6f;
    private const float FireH = 1.0f;

    // 粒 (0 = 昇る火の粉・1 = 飛び散る火花)
    private static readonly SpriteRenderer[] _emSr = new SpriteRenderer[Embers];
    private static readonly Transform[] _emTf = new Transform[Embers];
    private static readonly float[] _emX = new float[Embers], _emY = new float[Embers], _emVx = new float[Embers], _emVy = new float[Embers];
    private static readonly float[] _emT0 = new float[Embers], _emLife = new float[Embers], _emSize = new float[Embers], _emPh = new float[Embers], _emRed = new float[Embers];
    private static readonly byte[] _emKind = new byte[Embers];
    private static readonly bool[] _emOn = new bool[Embers];
    private static int _emLive, _emNext;

    // 体力のバーごと (0 = 左・1 = 右)
    private static readonly SpriteRenderer[] _sheen = new SpriteRenderer[2], _cut = new SpriteRenderer[2];
    private static readonly float[] _cutT = { -10f, -10f }, _cutA = new float[2], _cutB = new float[2], _emitCarry = new float[2];
    private static readonly int[] _pulseOn = { 0, 0 };
    private static readonly bool[] _sheenOn = new bool[2];
    private static readonly SpriteRenderer[] _fire = new SpriteRenderer[2];
    private static readonly int[] _fireKey = { -1, -1 };
    private static bool _fireMat;
    private static SpriteRenderer _centerGlow;
    private static float _lockEdge, _lockCarry, _centerCarry, _barY;
    private static Sprite _dot, _soft;

    private static void BuildFx(Sprite white, int layer, float y)
    {
        _dot ??= FxSprites.Rect(32, 32, FxSprites.Dot, "MrpDuelEmber");
        _soft ??= FxSprites.Rect(64, 64, FxSprites.Soft, "MrpDuelGlow");
        _barY = y;
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f, mid = side * (BarGap + _barW * 0.5f);
            // 炎の板はバーの上の縁から上へ。右のバーは左右を返して、板の左端 (uv.x = 0) をいつも外側の端にする
            // 根元はバーの上の縁 (バーに重ねるとバーの色が光で塗りつぶされる)
            _fire[i] = Quad("Fire", white, mid, y + BarH * 0.25f + FireH * 0.5f, -0.32f, _barW * -side, FireH, FxMath.Rgba(1f, 0f, 0f, 0f), layer);
            _fire[i].enabled = false;
            _fireKey[i] = -1;
            // 光の筋と削られた所の光は体力より手前
            _sheen[i] = Quad("Sheen", _soft, mid, y, -0.35f, 0.35f, BarH * 1.3f, FxMath.Rgba(1f, 0.95f, 0.8f, 0f), layer);
            _sheen[i].enabled = false;
            _cut[i] = Quad("Cut", white, mid, y, -0.36f, 0.1f, BarH * 0.9f, FxMath.Rgba(1f, 1f, 1f, 0f), layer);
            _cut[i].enabled = false;
            _cutT[i] = -10f;
            _pulseOn[i] = 0;
            _sheenOn[i] = false;
        }
        _fireMat = false;
        _centerGlow = Quad("CenterGlow", _soft, 0f, y, 0.1f, 0.95f, 0.95f, FxMath.Rgba(1f, 0.55f, 0.18f, 0.4f), layer);
        for (int i = 0; i < Embers; i++)
        {
            var sr = Quad("Ember", _dot, 0f, 0f, EmberZ, 0.1f, 0.1f, FxMath.Rgba(1f, 1f, 1f, 1f), layer);
            sr.enabled = false;
            _emSr[i] = sr;
            _emTf[i] = sr.transform;
            _emOn[i] = false;
        }
        _emLive = 0;
    }

    // 火の粉を 1 つ出す (HUD の座標)。kind 0 = 昇る・1 = 飛び散る火花 (vx, vy の向きへ伸びる)。red = 赤みの強さ 0〜1
    internal static void Emit(float x, float y, byte kind, float vx, float vy, float red = 0f)
    {
        if (!_shown || _emSr[0] == null) return;
        int i = -1;
        for (int k = 0; k < Embers; k++)
        {
            int j = (_emNext + k) % Embers;
            if (!_emOn[j]) { i = j; break; }
        }
        // 空きが無ければ一番古い物を使う
        if (i < 0) i = _emNext;
        _emNext = (i + 1) % Embers;
        if (!_emOn[i]) _emLive++;
        _emOn[i] = true;
        _emX[i] = x;
        _emY[i] = y;
        _emVx[i] = vx;
        _emVy[i] = vy;
        _emT0[i] = Time.time;
        _emKind[i] = kind;
        _emRed[i] = red;
        _emPh[i] = FxMath.Range(0f, 6.28f);
        _emLife[i] = kind == 0 ? FxMath.Range(0.6f, 1.1f) : FxMath.Range(0.25f, 0.4f);
        _emSize[i] = kind == 0 ? FxMath.Range(0.09f, 0.17f) : FxMath.Range(0.06f, 0.1f);
        var tf = _emTf[i];
        if (kind == 1)
        {
            // 火花は飛ぶ向きに伸ばす (向きは出す時に 1 回だけ)
            tf.localRotation = FxMath.RotZ(FxMath.Atan2(vy, vx) * FxMath.Rad2Deg);
        }
        else tf.localRotation = FxMath.RotZ(0f);
        _emSr[i].enabled = true;
    }

    // 点 (x, y) の周りへ n 個の火花を放射する
    internal static void Burst(float x, float y, int n, float speed, float red = 0f)
    {
        for (int k = 0; k < n; k++)
        {
            float a = (k + FxMath.Range(0f, 0.8f)) / n * 6.2832f, v = speed * FxMath.Range(0.6f, 1.1f);
            Emit(x, y, 1, FxMath.Cos(a) * v, FxMath.Sin(a) * v, red);
        }
    }

    // 体力が削られた (HUD の左右の大きなバー)。削られた所が白く光り、そこから火花が散る
    private static void FxCut(int i, float before, float after)
    {
        if (_cut[i] == null || after >= before) return;
        float side = i == 0 ? -1f : 1f, outer = side * (BarGap + _barW);
        float a = outer - side * _barW * after, b = outer - side * _barW * before;
        _cutA[i] = a;
        _cutB[i] = b;
        _cutT[i] = Time.time;
        var tf = _cut[i].transform;
        tf.localPosition = FxMath.V3((a + b) * 0.5f, _barY, -0.36f);
        tf.localScale = FxMath.V3(FxMath.Max(FxMath.Abs(b - a), 0.04f), BarH * 0.9f, 1f);
        _cut[i].enabled = true;
        for (int k = 0; k < 16; k++)
        {
            float x = FxMath.Lerp(a, b, FxMath.Range(0f, 1f));
            float ang = FxMath.Range(20f, 160f) * FxMath.Deg2Rad, v = FxMath.Range(2f, 3.5f);
            Emit(x, _barY + FxMath.Range(-0.05f, 0.05f), 1, FxMath.Cos(ang) * v, FxMath.Sin(ang) * v * (k % 3 == 0 ? -0.6f : 1f), 0.4f);
        }
    }

    // 題字が出た: 題字の周りへ火の粉
    private static void FxBanner()
    {
        for (int k = 0; k < 30; k++)
        {
            float x = FxMath.Range(-2.6f, 2.6f), y = 0.55f + FxMath.Range(-0.45f, 0.45f);
            if (k % 2 == 0) Emit(x, y, 0, FxMath.Range(-0.3f, 0.3f), FxMath.Range(0.6f, 1.4f), 0.2f);
            else Emit(x, y, 1, FxMath.Range(-3f, 3f), FxMath.Range(-1.5f, 2.5f), 0.3f);
        }
    }

    private static void FxTick(float now)
    {
        if (_emSr[0] == null) return;
        // 炎のシェーダはバンドルの読み込みの後に届く (届くまでは炎の板を出さない)
        if (!_fireMat && MrpBundle.UiFireMaterial)
        {
            _fireMat = true;
            for (int i = 0; i < 2; i++)
            {
                _fire[i].sharedMaterial = MrpBundle.UiFireMaterial;
                _fire[i].enabled = true;
            }
        }
        float dt = Time.deltaTime;
        BarFx(0, _leftHp, now, dt);
        BarFx(1, _rightHp, now, dt);
        // ∞ の後ろの炎の光と、そこから昇る火の粉
        float f = 0.5f + 0.5f * FxMath.Sin(now * 9f) * FxMath.Sin(now * 5.3f + 1f);
        _centerGlow.color = FxMath.Rgba(1f, 0.5f + 0.1f * f, 0.15f, 0.32f + 0.18f * f);
        _centerCarry += 3f * dt;
        while (_centerCarry >= 1f)
        {
            _centerCarry -= 1f;
            Emit(FxMath.Range(-0.2f, 0.2f), _barY + 0.1f, 0, FxMath.Range(-0.15f, 0.15f), FxMath.Range(0.5f, 0.9f));
        }
        // 鍔迫り合いの境目の火花
        if (_lockOn)
        {
            _lockCarry += 25f * dt;
            while (_lockCarry >= 1f)
            {
                _lockCarry -= 1f;
                float ang = FxMath.Range(0f, 6.2832f), v = FxMath.Range(1.8f, 3.4f);
                Emit(_lockEdge, _lockY, 1, FxMath.Cos(ang) * v, FxMath.Sin(ang) * v * 0.8f + 0.6f, 0.2f);
            }
        }
        if (_emLive > 0) EmberTick(now, dt);
    }

    // 体力のバー i: にじみ・横切る光の筋・残りが少ない時の脈打ちと火の粉・削られた所の光
    private static void BarFx(int i, float hp, float now, float dt)
    {
        float side = i == 0 ? -1f : 1f, outer = side * (BarGap + _barW), w = _barW * FxMath.Clamp01(hp);
        bool low = hp > 0f && hp < LowHp;
        // 炎の板: 燃える長さ = 残り・熱さ = 残りが少ないほど高い (値が変わった時だけ書く)
        if (_fireMat)
        {
            float heat = hp <= 0f ? 0f : low ? 0.75f + 0.25f * (1f - hp / LowHp) : 0.25f + 0.25f * (1f - hp);
            int key = (int)(hp * 200f) * 64 + (int)(heat * 63f);
            if (key != _fireKey[i])
            {
                _fireKey[i] = key;
                _fire[i].color = FxMath.Rgba(FxMath.Clamp01(hp), heat, 0f, hp > 0f ? 1f : 0f);
            }
        }
        // 残りが少ない: 赤 ↔ 橙に脈打つ (戻ったら一度だけ本来の色に書き戻す)
        var fill = i == 0 ? _leftFill : _rightFill;
        if (low)
        {
            float p = 0.5f + 0.5f * FxMath.Sin(now * 22f);
            fill.color = FxMath.Rgba(0.95f + 0.05f * p, 0.3f + 0.35f * p, 0.2f + 0.05f * p, 1f);
            _pulseOn[i] = 1;
        }
        else if (_pulseOn[i] != 0)
        {
            _pulseOn[i] = 0;
            fill.color = HpColor(hp);
        }
        // 残っている所の上から昇る火の粉
        if (w > 0.05f)
        {
            _emitCarry[i] += (low ? 24f : 8f) * (w / _barW + (low ? 0.6f : 0f)) * dt;
            while (_emitCarry[i] >= 1f)
            {
                _emitCarry[i] -= 1f;
                float x = outer - side * FxMath.Range(0f, w);
                Emit(x, _barY + BarH * 0.4f, 0, FxMath.Range(-0.2f, 0.2f), FxMath.Range(0.5f, 1.1f), low ? 0.6f : 0f);
            }
        }
        // 横切る光の筋 (外側の端から内へ)
        float st = FxMath.Repeat(now + i * 1.3f, SheenEvery);
        bool sheen = st < SheenTime && w > 0.3f;
        if (sheen)
        {
            float u = st / SheenTime, bell = FxMath.Sin(u * 3.1416f);
            float x = outer - side * (w * u);
            _sheen[i].transform.localPosition = FxMath.V3(x, _barY, -0.35f);
            _sheen[i].color = FxMath.Rgba(1f, 0.96f, 0.82f, 0.5f * bell);
        }
        if (sheen != _sheenOn[i])
        {
            _sheenOn[i] = sheen;
            _sheen[i].enabled = sheen;
        }
        // 削られた所の白い光
        if (_cutT[i] > 0f)
        {
            float u = (now - _cutT[i]) / CutFlash;
            if (u >= 1f)
            {
                _cutT[i] = -10f;
                _cut[i].enabled = false;
            }
            else _cut[i].color = FxMath.Rgba(1f, 1f, 0.92f, (1f - u) * (1f - u));
        }
    }

    private static void EmberTick(float now, float dt)
    {
        for (int i = 0; i < Embers; i++)
        {
            if (!_emOn[i]) continue;
            float u = (now - _emT0[i]) / _emLife[i];
            if (u >= 1f)
            {
                _emOn[i] = false;
                _emSr[i].enabled = false;
                _emLive--;
                continue;
            }
            float x, y, sx, sy;
            if (_emKind[i] == 0)
            {
                // 昇りながら速さを増し、横にゆらゆら
                _emVy[i] += 0.4f * dt;
                _emVx[i] *= 1f - 1.5f * dt;
                _emX[i] += _emVx[i] * dt;
                _emY[i] += _emVy[i] * dt;
                x = _emX[i] + 0.12f * FxMath.Sin(now * 8f + _emPh[i]) * u;
                y = _emY[i];
                sx = sy = _emSize[i] * (1f - 0.4f * u);
            }
            else
            {
                // 火花は速く飛んで遅くなり、少し落ちる。伸びは速さの分だけ
                float drag = 1f - 4f * dt;
                _emVx[i] *= drag;
                _emVy[i] = _emVy[i] * drag - 2.5f * dt;
                _emX[i] += _emVx[i] * dt;
                _emY[i] += _emVy[i] * dt;
                x = _emX[i];
                y = _emY[i];
                float sp = FxMath.Sqrt(_emVx[i] * _emVx[i] + _emVy[i] * _emVy[i]);
                sy = _emSize[i];
                sx = sy * (1f + FxMath.Min(sp * 1.6f, 4f));
            }
            _emTf[i].localPosition = FxMath.V3(x, y, EmberZ);
            _emTf[i].localScale = FxMath.V3(sx, sy, 1f);
            // 白黄 → 橙 → 赤 → 暗い赤。明るさは細かく揺らぐ
            float r = _emRed[i];
            float g = u < 0.25f ? FxMath.Lerp(0.95f, 0.7f, u / 0.25f) : u < 0.6f ? FxMath.Lerp(0.7f, 0.38f, (u - 0.25f) / 0.35f) : FxMath.Lerp(0.38f, 0.08f, (u - 0.6f) / 0.4f);
            float b = u < 0.25f ? FxMath.Lerp(0.75f, 0.25f, u / 0.25f) : FxMath.Lerp(0.25f, 0.04f, FxMath.Clamp01((u - 0.25f) / 0.75f));
            float red = u < 0.6f ? 1f : FxMath.Lerp(1f, 0.6f, (u - 0.6f) / 0.4f);
            float flick = 0.7f + 0.3f * FxMath.Sin(now * 37f + _emPh[i]);
            float a = (1f - u) * (1f - u * 0.5f) * flick;
            _emSr[i].color = FxMath.Rgba(red, g * (1f - 0.5f * r), b * (1f - 0.6f * r), a);
        }
    }

    private static void FxHide()
    {
        for (int i = 0; i < Embers; i++)
        {
            if (!_emOn[i]) continue;
            _emOn[i] = false;
            if (_emSr[i]) _emSr[i].enabled = false;
        }
        _emLive = 0;
        for (int i = 0; i < 2; i++)
        {
            _cutT[i] = -10f;
            if (_cut[i]) _cut[i].enabled = false;
            _sheenOn[i] = false;
            if (_sheen[i]) _sheen[i].enabled = false;
            _pulseOn[i] = 0;
        }
    }

    internal static string DescribeFx() => $"fire={_fireMat} embers={_emLive}/{Embers} cut={_cutT[0] > 0f}/{_cutT[1] > 0f} low={_pulseOn[0]}/{_pulseOn[1]}";
}
