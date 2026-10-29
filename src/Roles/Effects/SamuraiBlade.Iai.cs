using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 居合の見た目。溜め (本人の画面だけ): 拳を腰の鯉口に添えて沈み、震えながらオーラが大きく白く焼ける。溜め切ると拳が光り、画面が脈打って暗む。
// 放つ (全員の画面): 抜いた所で鯉口が光り、一振りは溜めた分だけ大きく前へ踏み出して太い三日月を残し、振り抜いた形で長く止まって切っ先が光る
internal sealed partial class SamuraiBlade
{
    private const float IaiHold = 0.25f;   // 溜め切った居合で、振り抜いた形に足す止めの秒

    private float _iaiPow, _strokePow, _auraHot;
    private int _iaiTier = 1, _strokeTier = 1;
    private bool _powArmed, _wasInStroke, _charging, _chargeFull;

    // 納めたまま押している (自分の刀): 毎フレーム書く列に入れる
    internal void Charging()
    {
        if (Ensure()) Track();
    }

    // 居合で抜いた。pow = 溜め (0〜1)・tier = 段 (1〜4)。続く一振りがこの強さになる
    internal void ArmIai(float pow, int tier)
    {
        if (!Ensure()) return;
        EndCharge();
        _iaiPow = FxMath.Clamp01(pow);
        _iaiTier = tier < 1 ? 1 : tier > 4 ? 4 : tier;
        _lastArmedTier = _iaiTier;
        _powArmed = true;
        Glint(0.06f, 0.06f, 0.10f + 0.06f * _iaiPow, 0.30f + 0.5f * _iaiPow);
        Track();
    }

    private void BeginStroke()
    {
        _strokePow = _powArmed ? _iaiPow : 0f;
        _strokeTier = _powArmed ? _iaiTier : 1;
        _powArmed = false;
        _iaiPow = 0f;
        _iaiTier = 1;
        _auraHot = _strokePow * 0.8f;
    }

    // 振り抜いた: 居合なら切っ先が光り、段に応じた斬線を出す
    private void EndStroke()
    {
        if (_strokePow > 0.05f) Glint(TipLen - 0.04f, TipLen - 0.04f, 0.14f + 0.10f * _strokePow, 0.30f + 0.55f * _strokePow);
        if (_strokeTier >= 2) StrikeRelease(_strokeTier);
        _strokeTier = 1;
    }

    // 居合の一振り: 振る向きへ大きく踏み出して浮き、前へ深く傾く
    private void IaiLunge(ref Pose p, float angle, float progress)
    {
        float k = _strokePow;
        float bulge = FxMath.Sin(FxMath.Clamp01(progress) * FxMath.PI);
        float co = FxMath.Cos(angle * FxMath.Deg2Rad);
        p.Dx += co * 0.30f * k * bulge;
        p.Hop += 0.05f * k * bulge;
        p.Lean *= 1f + 0.5f * k;
        p.Fs += 0.15f * k;
        p.Aura = FxMath.Max(p.Aura, 0.8f + 0.4f * k);
    }

    // 溜めの姿 (自分の刀で、納めたまま押している間だけ)。溜めていなければ false
    private bool ChargePose(float now, out Pose p)
    {
        p = default;
        if (this != LocalFollow || !SwipeInput.Dragging || SwipeInput.InStroke) return false;
        float c = _role.Charge;
        _charging = true;
        // 30 コマ/秒で左右に震える (溜めるほど大きく)
        float shake = (FxMath.Floor(now * 30f) % 2f == 0f ? 1f : -1f) * 0.010f * c;
        p = new Pose
        {
            Lean = -5f * c, Sx = 1f + 0.10f * c, Sy = 1f - 0.12f * c, Turn = 1f, Hop = 0f, Dx = shake,
            Fx = -0.06f, Fy = -0.15f, Fs = FxMath.Clamp01(0.3f + 4f * c), Blade = 200f, BladeA = 0f,
            Aura = 0.2f + 0.8f * c,
        };
        bool full = c >= 1f;
        _auraHot = full ? 0.85f + 0.15f * FxMath.Sin(now * 25f) : c * 0.7f;
        if (full && !_chargeFull) Glint(0f, 0f, 0.18f, 0.55f);
        _chargeFull = full;
        SamuraiScreen.Pulse(full);
        return true;
    }

    private void EndCharge()
    {
        if (!_charging) return;
        _charging = _chargeFull = false;
        _auraHot = 0f;
        SamuraiScreen.Pulse(false);
    }

    // ---- 弾き合い・鍔迫り合い ----

    private bool _lockOn;
    private float _lockAngle, _lockSparkT, _lockSign, _lockMx, _lockMy;
    private PlayerControl _lockOpp;
    // 鍔迫り合いで刀が交わる点 = 二人の真ん中から LockLift 上・交わるのは刀の真ん中
    internal const float LockLift = 0.08f;
    private const float LockCross = 0.5f, LockTilt = 35f;

    // 鍔迫り合い: 相手 (other) の刀と二人の間の一点で X 字に交わるよう押し付けたまま震える
    internal void SetLock(bool on, byte other)
    {
        if (on && !Ensure()) return;
        _lockOn = on;
        _lockOpp = on && GameData.Instance ? GameData.Instance.GetPlayerById(other)?.Object : null;
        if (on)
        {
            _strokePow = 0f;
            var pc = _role.Player;
            if (pc && _lockOpp)
            {
                // 刀を傾ける向き: 番号の小さい方から見て相手が右なら上へ・相手は鏡に映した向き (二人とも切っ先が上の X になる)
                Vector3 a = pc.transform.position, b = _lockOpp.transform.position;
                _lockAngle = FxMath.Atan2(b.y - a.y, b.x - a.x) * FxMath.Rad2Deg;
                bool low = _role.PlayerId < other;
                float lowDx = low ? b.x - a.x : a.x - b.x;
                float sLow = lowDx >= 0f ? 1f : -1f;
                _lockSign = low ? sLow : -sLow;
            }
            else
            {
                _lockOpp = null;
                _lockSign = 1f;
            }
        }
        Track();
    }

    // 弾かれた: 刀が相手と反対へ跳ね上がった形で止め、構えへ戻る (相手は angle 度の方)
    internal void Recoil(float angle)
    {
        if (!_drawn || !Ensure()) return;
        float ar = angle * FxMath.Deg2Rad, co = FxMath.Cos(ar), si = FxMath.Sin(ar);
        bool right = co >= 0f;
        _endPose = new Pose
        {
            Fx = -co * 0.12f, Fy = -si * 0.12f + SwingDrop, Fs = 1f, Blade = angle + (right ? 125f : -125f), BladeA = 1f,
            Lean = right ? 14f : -14f, Sx = 1.06f, Sy = 0.94f, Turn = 1f, Hop = 0.03f, Dx = -co * 0.08f, Aura = 0.8f,
        };
        _endT = Time.time;
        _endWorld = true;
        Glint(TipLen * 0.6f, TipLen * 0.6f, 0.16f, 0.5f);
        Track();
    }

    // 刀が交わる点へ向けて刀を傾け、拳はその刀の握りの所 (遠ければ拳が体から離れて浮く)。
    // 二人とも同じ点を通る直線の上に刀を置くので、間合いに関わらず必ずぶつかる
    private Pose LockPose(float now, PlayerControl pc)
    {
        // 押し合いで 30 コマ/秒に震える (刀の向きに押し引きするので交わる点はずれない)
        float shake = (FxMath.Floor(now * 30f) % 2f == 0f ? 1f : -1f) * 0.012f;
        var opp = _lockOpp;
        if (opp)
        {
            Vector3 a = pc.transform.position, b = opp.transform.position;
            _lockMx = (b.x - a.x) * 0.5f;
            _lockMy = (b.y - a.y) * 0.5f + LockLift;
        }
        else
        {
            float ar = _lockAngle * FxMath.Deg2Rad;
            _lockMx = FxMath.Cos(ar) * 0.5f;
            _lockMy = FxMath.Sin(ar) * 0.5f + LockLift;
        }
        bool right = _lockMx >= 0f;
        float th = FxMath.Atan2(_lockMy, _lockMx) * FxMath.Rad2Deg + LockTilt * _lockSign;
        float tr = th * FxMath.Deg2Rad, back = TipLen * LockCross - shake;
        float dx = right ? 0.06f : -0.06f;
        return new Pose
        {
            Fx = _lockMx - FxMath.Cos(tr) * back - dx, Fy = _lockMy - FxMath.Sin(tr) * back, Fs = 1.1f, Blade = th, BladeA = 1f,
            Lean = right ? -11f : 11f, Sx = 1.04f, Sy = 0.95f + shake, Turn = 1f, Hop = 0f, Dx = dx, Aura = 1f,
        };
    }

    // 刀が交わる所から小さな火花が散り続ける
    private void LockSparks(float now, PlayerControl pc)
    {
        if (now - _lockSparkT < 0.12f || _hidden) return;
        _lockSparkT = now;
        Vector3 at = pc.transform.position;
        DuelHitFx.Clash(FxMath.V3(at.x + _lockMx, at.y + _lockMy, at.z - 0.02f), pc.gameObject.layer, 0.3f);
    }

    // 刀の上の光 (拳から from → to 単位・秒・大きさ)
    private void Glint(float from, float to, float dur, float size)
    {
        _glT = Time.time;
        _glFrom = from;
        _glTo = to;
        _glDur = dur;
        _glSize = size;
        _gl2T = -10f;
    }
}

// 自分の画面だけの全画面の明滅 (本編の FullScreen を借りる)。溜め切った間の脈打ちと、斬り合いの当たりの 1 コマの暗転。
// 本編やサボタージュの点滅が使っている間は借りない
internal static class SamuraiScreen
{
    private static bool _own, _pulse;
    private static float _flashUntil = -10f, _flashA;

    internal static void Pulse(bool on) => _pulse = on;

    internal static string Describe() => $"own={_own} pulse={_pulse} flash={(Time.time < _flashUntil ? "on" : "off")}";

    internal static void Flash(float alpha, float seconds)
    {
        _flashA = alpha;
        _flashUntil = Time.time + seconds * SamuraiBlade.SlowMo;
    }

    // 毎フレーム。使っていなければ比較だけで帰る
    internal static void Tick()
    {
        float now = Time.time;
        if (!_own && !_pulse && now >= _flashUntil) return;
        var hud = Vanilla.Hud;
        var fs = hud ? hud.FullScreen : null;
        if (!fs) { _own = _pulse = false; return; }
        float a = now < _flashUntil ? _flashA : _pulse && FxMath.Repeat(now * 4f, 1f) < 0.35f ? 0.22f : 0f;
        if (a <= 0f)
        {
            if (_own) fs.gameObject.SetActive(false);
            _own = false;
            return;
        }
        if (!_own)
        {
            if (fs.gameObject.activeSelf) return;
            _own = true;
            fs.gameObject.SetActive(true);
        }
        fs.color = FxMath.Rgba(0.04f, 0.01f, 0.08f, a);
    }
}
