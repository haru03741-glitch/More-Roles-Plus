using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 跳躍の姿 (全員の画面): 深く屈む → 縦に伸びて跳び上がる → 宙で体を丸めて前へ 1 回転 (残像を引く) → 着地で潰れて土煙。
// 体の位置は本編の同期で動くので、ここでは体の絵を持ち上げて回すだけ。回転は体の真ん中を軸にする (足元を軸に回すと宙で大きく振れる)。
// 抜いていれば刀は背へ流して引きずり、納めていれば拳も刀も出さない
internal sealed partial class SamuraiBlade
{
    internal const float LeapCrouch = 0.07f; // 屈む秒 (この間は動かない)
    private const float LeapLand = 0.14f;    // 着地で潰れて戻る秒
    private const float LeapCenter = 0.33f;  // 足元から体の真ん中までの高さ (回転の軸)

    private float _lpT = -10f, _lpAir = 0.4f, _lpH;
    private int _lpDir = 1;
    private bool _lpLanded;

    // air = 宙にいる秒・height = 跳ぶ高さ (絵の上での持ち上げ)・dir = 右へ 1 / 左へ -1
    internal void Leap(float air, float height, int dir)
    {
        if (!Ensure()) return;
        _lpT = Time.time;
        _lpAir = FxMath.Max(air, 0.1f);
        _lpH = height;
        _lpDir = dir >= 0 ? 1 : -1;
        _lpLanded = false;
        if (!_hidden) SamuraiSound.Dash(_role.Player);
        Track();
    }

    private bool LeapBusy(float now) => now - _lpT < LeapCrouch + _lpAir + LeapLand;

    private Pose LeapPose(float now, PlayerControl pc)
    {
        float t = now - _lpT;
        int d = _lpDir;
        // オーラは体と一緒に回らないので宙では弱める。納めている間は普通のクルーと同じに出さない
        float aura = _drawn ? 0.6f : 0f;
        var p = new Pose { Sx = 1f, Sy = 1f, Turn = 1f, Aura = aura };
        if (t < LeapCrouch)
        {
            // 屈む: 横に潰れて前へ傾く
            float k = t / LeapCrouch;
            p.Sx = 1f + 0.22f * k;
            p.Sy = 1f - 0.26f * k;
            p.Lean = -d * 10f * k;
        }
        else if (t < LeapCrouch + _lpAir)
        {
            float u = (t - LeapCrouch) / _lpAir;
            float hop = _lpH * 4f * u * (1f - u);
            // 跳び上がる所で縦に伸び、宙では丸まる
            float stretch = FxMath.Clamp01(1f - u / 0.18f);
            float tuck = FxMath.Sin(FxMath.Clamp01((u - 0.1f) / 0.75f) * FxMath.PI);
            p.Sx = 1f - 0.18f * stretch - 0.12f * tuck;
            p.Sy = 1f + 0.28f * stretch - 0.14f * tuck;
            // 前へ 1 回転 (始めと終わりはゆっくり)
            float r = FxMath.Clamp01((u - 0.12f) / 0.7f);
            r = r * r * (3f - 2f * r);
            float spin = -d * 360f * r;
            float sr = spin * FxMath.Deg2Rad, co = FxMath.Cos(sr), si = FxMath.Sin(sr);
            p.Lean = spin;
            // 足元でなく体の真ん中を軸に回す分だけずらす
            p.Dx = si * LeapCenter;
            p.Hop = hop + LeapCenter - co * LeapCenter;
            p.Aura = aura * 0.4f;
            SpawnGhost(now);
        }
        else
        {
            if (!_lpLanded)
            {
                _lpLanded = true;
                Landed(pc);
            }
            float k = FxMath.Clamp01((t - LeapCrouch - _lpAir) / LeapLand);
            float e = 1f - (1f - k) * (1f - k);
            p.Sx = 1.28f - 0.28f * e;
            p.Sy = 0.72f + 0.28f * e;
            p.Aura = aura;
        }
        if (_drawn)
        {
            // 刀は背へ流す
            p.Fx = -d * 0.16f;
            p.Fy = 0.02f; // 体と一緒に持ち上がる (WritePose が Hop を足す)
            p.Fs = 1f;
            p.Blade = d > 0 ? 205f : -25f;
            p.BladeA = 1f;
        }
        return p;
    }

    // 着地: 足元の土煙・本人の画面だけ小さく揺らす
    private void Landed(PlayerControl pc)
    {
        if (_hidden) return;
        Vector2 feet = pc.GetTruePosition();
        Terrain.DustCloud.Kick(feet, 0.6f, (ushort)(pc.PlayerId * 131 + Time.frameCount));
        SamuraiSound.Dodge(pc);
        if (pc.AmOwner)
        {
            var hud = Vanilla.Hud;
            if (hud && hud.PlayerCam) hud.PlayerCam.ShakeScreen(0.08f, 1.4f);
        }
    }
}
