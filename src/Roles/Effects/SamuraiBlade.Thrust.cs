using System;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Roles.Neutral;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 突きの見た目 (全員の画面): 拳を引いて縮み (2 コマ) → 突いた向きへ拳ごと刀を真っすぐ伸ばし、体は前へ傾いて伸びる →
// 伸ばした形で止めて構えへ戻る (止めと戻りは振りと同じ道)。突いた所には細く遠くまで届く筋 (三日月の網目を直線に並べ直す) が残る
internal sealed unsafe partial class SamuraiBlade
{
    private const float ThWind = 0.05f, ThShoot = 0.07f;   // 引く秒・伸ばす秒
    private const float ThFist = 0.48f;                    // 伸ばした拳の体の中心からの長さ
    private const float ThRoot = 0.12f;                    // 筋の根元 (体の中心から)
    private const float ThWidth = 0.11f;                   // 筋のいちばん太い所の半分の幅

    private float _thT = -10f, _thA, _thLen;
    private bool _thGlint;

    // 突く (a = 向き・度)。居合で抜いていれば溜めの分だけ強く見せる
    internal void Thrust(float a, bool fallen)
    {
        if (!_drawn || !Ensure()) return;
        _thT = Time.time;
        _thA = a;
        // 当たりの届く所 (ホストの設定の斬れる距離から) の少し手前まで
        _thLen = Samurai.SlashRange + Samurai.ThrustExtra - 0.2f;
        _thGlint = false;
        _repFallen = fallen;
        _repT = _endT = -10f;
        _strokePow = _powArmed ? _iaiPow : 0f;
        _powArmed = false;
        _iaiPow = 0f;
        _iaiTier = 1;
        _auraHot = _strokePow * 0.8f;
        if (!_hidden) SamuraiSound.Swing(_role.Player, _strokePow);
        Track();
    }

    private bool ThrustBusy(float now) => (now - _thT) / SlowMo < ThWind + ThShoot + 0.02f;

    // 突いている間の姿 (ワールドの向き)。突いていなければ false
    private bool ThrustPose(float now, out Pose p)
    {
        p = default;
        float t = (now - _thT) / SlowMo;
        if (t < 0f || t > ThWind + ThShoot) return false;
        float ar = _thA * FxMath.Deg2Rad, co = FxMath.Cos(ar), si = FxMath.Sin(ar);
        float k = _strokePow;
        float r, lean, sx, sy, dx, hop, ext;
        if (t < ThWind)
        {
            // 引く: 拳を体へ寄せて後ろへ反り、縮む (止め絵の 2 コマ)
            float u = FxMath.Floor(t / Frame) * Frame / ThWind;
            r = SwingGrip + (0.06f - SwingGrip) * u;
            lean = co * 8f * u;
            sx = 1f + 0.08f * u;
            sy = 1f - 0.08f * u;
            dx = -co * 0.04f * u;
            hop = 0f;
            ext = 0f;
        }
        else
        {
            // 伸ばす: 始めが速く終わりで止まる
            float u = FxMath.Clamp01((t - ThWind) / ThShoot);
            u = 1f - (1f - u) * (1f - u) * (1f - u);
            r = 0.06f + (ThFist + 0.12f * k - 0.06f) * u;
            lean = -co * (16f + 10f * k) * u;
            sx = 1.08f - 0.18f * u;
            sy = 0.92f + 0.18f * u;
            dx = co * (0.16f + 0.12f * k) * u;
            hop = 0.03f * u;
            ext = u;
        }
        p = new Pose
        {
            Fx = co * r, Fy = si * r + SwingDrop, Fs = 1.1f + 0.15f * k, Blade = _thA, BladeA = 1f,
            Lean = lean, Sx = sx, Sy = sy, Hop = hop, Dx = dx, Turn = 1f, Aura = 0.7f + 0.4f * k + 0.2f * ext,
        };
        if (ext > 0f)
        {
            DrawThrustSmear(_thA, ThRoot + (_thLen * (1f + 0.15f * k) - ThRoot) * ext);
            SpawnGhost(now);
            if (ext >= 1f && !_thGlint)
            {
                _thGlint = true;
                if (k > 0.05f) Glint(ThFist + TipLen - 0.04f, ThFist + TipLen - 0.04f, 0.14f + 0.10f * k, 0.30f + 0.55f * k);
            }
        }
        return true;
    }

    // 突きの筋を出す/伸ばす (a = 向き・len = 体の中心から先までの長さ)
    private void DrawThrustSmear(float a, float len)
    {
        if (_hidden) return;
        var pc = _role.Player;
        if (!pc || !EnsureSmear(pc)) return;
        var at = pc.transform.position;
        _smCx = at.x;
        _smCy = at.y + SwingDrop + _last.Hop;
        _smZ = at.y / 1000f - 0.004f;
        _smA0 = _smA1 = a;
        _smLen = len;
        _smStr = 1f;
        _smPow = _strokePow;
        _smRed = this == LocalFollow ? _fallenTint : _repFallen;
        _smThrust = true;
        _smT = -10f;
        _smOn = true;
        if (!_smearGo.activeSelf) _smearGo.SetActive(true);
        BuildSmear(0f);
    }

    // 横の輪ごとの中心からの離れ (幅に対する割合)・濃さ・白の混ぜ (真ん中が白い芯)
    private static readonly float[] ThSide = { -1.25f, -0.6f, -0.15f, 0.15f, 0.6f, 1.25f };
    private static readonly float[] ThSideA = { 0f, 1f, 1f, 1f, 1f, 0f };
    private static readonly float[] ThSideW = { 0f, 0.25f, 1f, 1f, 0.25f, 0f };

    // 直線の筋: 根元は細く、先の手前で最も太く、先は尖る。振り終えると根元から食われ、少し太って薄れる
    private void BuildThrust(float fade)
    {
        float ar = _smA1 * FxMath.Deg2Rad, co = FxMath.Cos(ar), si = FxMath.Sin(ar);
        float eat = fade * 0.85f;
        float alpha = (1f - fade) * (1f - fade) * _smStr;
        float wide = ThWidth * (1f + 0.8f * _smPow) * (1f + 0.3f * fade) / 1.9f;
        float* vp = (float*)((byte*)_smV.Pointer + 4 * IntPtr.Size);
        byte* cp = (byte*)_smC.Pointer + 4 * IntPtr.Size;
        for (int i = 0; i < SmN; i++)
        {
            float u = (float)i / (SmN - 1);               // 0 = 根元・1 = 先
            float d = ThRoot + (_smLen - ThRoot) * u;
            float w = wide * 4f * FxMath.Pow(u, 0.6f) * FxMath.Sqrt(1f - u);
            float au = FxMath.Pow(u, 0.3f) * (u < eat ? 0f : FxMath.Clamp01((u - eat) / 0.12f)) * alpha;
            float cr, cg, cb;
            if (_smRed) { cr = 0.75f + 0.25f * u; cg = 0.06f + 0.25f * u; cb = 0.15f + 0.15f * u; }
            else { cr = 0.95f - 0.77f * u; cg = 0.22f + 0.20f * u; cb = 0.62f + 0.38f * u; }
            float bx = _smCx + co * d, by = _smCy + si * d;
            for (int r = 0; r < SmR; r++)
            {
                int vi = i * SmR + r;
                float off = ThSide[r] * w;
                vp[vi * 3] = bx - si * off;
                vp[vi * 3 + 1] = by + co * off;
                vp[vi * 3 + 2] = _smZ;
                float wh = ThSideW[r] + (1f - ThSideW[r]) * 0.45f * _smPow * ThSideA[r];
                byte* c = cp + vi * 4;
                c[0] = (byte)(255f * (cr + (1f - cr) * wh));
                c[1] = (byte)(255f * (cg + (1f - cg) * wh));
                c[2] = (byte)(255f * (cb + (1f - cb) * wh));
                c[3] = (byte)(255f * FxMath.Clamp01(ThSideA[r] * au));
            }
        }
        _smearMesh.vertices = _smV;
        _smearMesh.colors32 = _smC;
    }
}
