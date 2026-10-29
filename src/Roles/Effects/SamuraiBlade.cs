using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Roles.Neutral;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// サムライの抜いた刀 (全員の画面) と振った三日月。刀は体の子にして一緒に動かし、
// 毎フレーム書くのは抜く・納める・振る動きの間だけ (止まっている間は向きと見え方を数フレームに 1 回見るだけ)。
// 位置は体の中心からのワールドの長さ (右向き)。左を向いている間は根を鏡に映す
internal sealed class SamuraiBlade
{
    // 刀の絵 (Resources/Icons/katana_held.png・440x110): 握る所 (78, 68)。全長が約 0.95 単位になる大きさ
    private const float Ppu = 460f;
    private const float GripPx = 78f, GripPy = 68f, SpriteW = 440f, SpriteH = 110f;
    // 三日月の絵 (katana_arc*.png・512x900): 円の中心 (16, 450)・半径 480 px = 2 単位
    private const float ArcPpu = 240f, ArcR = 2f;
    private const float RestX = 0.30f, RestY = -0.14f, RestRot = -28f; // 構え: 体の右で切っ先を前の下へ
    private const float SheathRot = 95f;                             // 抜く途中: 腰から上へ立てた所
    private const float SwingHit = 0.07f, SwingBack = 0.25f, ArcLife = 0.22f;
    private const int CheckEvery = 6;

    private static readonly List<SamuraiBlade> Live = new();
    private static int _frame;

    private readonly Samurai _role;
    private GameObject _root, _arc;
    private Transform _rootTf, _gripTf, _arcTf;
    private SpriteRenderer _sr, _arcSr;
    private bool _drawn, _hidden, _flip, _fallenTint, _arcFallen;
    private float _drawT = -10f;    // 抜く/納める動作を始めた時刻
    private float _swingT = -10f;   // 振りを始めた時刻
    private float _swingDeg;        // 振る向き (ワールドの角度)
    private float _inv = 1f;
    private int _tick;

    internal SamuraiBlade(Samurai role) => _role = role;

    internal void SetDrawn(bool drawn)
    {
        _drawn = drawn;
        _drawT = Time.time;
        if (drawn) Ensure();
        if (_root) Track(true);
    }

    internal void SetFallen(bool tint)
    {
        _fallenTint = tint;
        if (_sr) _sr.color = Tint(1f);
    }

    // 振る (溜め → 斬る → 戻す)。fallen = 修羅の色の三日月
    internal void Swing(Vector2 dir, bool fallen)
    {
        if (!Ensure()) return;
        _swingT = Time.time;
        _swingDeg = FxMath.Atan2(dir.y, dir.x) * FxMath.Rad2Deg;
        _arcFallen = fallen;
        Track(true);
    }

    // FixedUpdate ごと (役職から)。数回に 1 回だけ、死んだら片付け、構えたまま止まっている刀の見え方と向きを拾い直す
    internal void Tick()
    {
        if (++_tick < CheckEvery) return;
        _tick = 0;
        if (!_root) return;
        var pc = _role.Player;
        if (!pc || pc.Data == null || pc.Data.IsDead) { Dispose(); return; }
        if (!_drawn || Live.Contains(this)) return;
        bool flip = _flip;
        Check();
        if (flip != _flip) Apply(Time.time);
    }

    internal void Dispose()
    {
        Live.Remove(this);
        if (_root) Object.Destroy(_root);
        if (_arc) Object.Destroy(_arc);
        _root = _arc = null;
    }

    private bool Ensure()
    {
        if (_root) return true;
        var pc = _role.Player;
        if (!pc) return false;
        var sprite = ButtonIcons.Load("katana_held", Ppu, FxMath.V2(GripPx / SpriteW, 1f - GripPy / SpriteH));
        if (!sprite) return false;
        int layer = pc.gameObject.layer;
        _root = new GameObject("MrpKatana") { layer = layer };
        _rootTf = _root.transform;
        _rootTf.SetParent(pc.transform, false);
        float scale = pc.transform.localScale.x;
        _inv = scale > 0.01f ? 1f / scale : 1f;
        var grip = new GameObject("Grip") { layer = layer };
        _gripTf = grip.transform;
        _gripTf.SetParent(_rootTf, false);
        _sr = grip.AddComponent<SpriteRenderer>();
        _sr.sprite = sprite;
        _sr.color = Tint(0f);
        _flip = pc.cosmetics && pc.cosmetics.FlipX;
        Apply(Time.time);
        return true;
    }

    private void Track(bool on)
    {
        if (on && !Live.Contains(this)) Live.Add(this);
    }

    private Color Tint(float a) => _fallenTint ? FxMath.Rgba(1f, 0.45f, 0.5f, a) : FxMath.Rgba(1f, 1f, 1f, a);

    // 毎フレーム (動いている刀だけ)
    internal static void TickAll()
    {
        if (Live.Count == 0) return;
        bool check = ++_frame % CheckEvery == 0;
        float now = Time.time;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var b = Live[i];
            if (!b._root) { Live.RemoveAt(i); continue; }
            if (check) b.Check();
            if (!b.Apply(now) && !b._drawn) b._root.SetActive(false);
            if (b.Idle(now)) Live.RemoveAt(i);
        }
    }

    // 向きと見え方 (ベントの中・透明・停電中のカメラの暗視では隠す)
    private void Check()
    {
        var pc = _role.Player;
        if (!pc) return;
        bool hidden = !pc.Visible || Rules.NightVisionCams.Active;
        if (hidden != _hidden)
        {
            _hidden = hidden;
            _sr.enabled = !hidden;
        }
        if (Time.time - _swingT > SwingHit + SwingBack) _flip = pc.cosmetics && pc.cosmetics.FlipX;
    }

    // 動きが終わり、構えたまま (か納め終えた) なら毎フレーム書くのをやめる。向きは役職の FixedUpdate で拾い直す
    private bool Idle(float now) =>
        now - _drawT > Samurai.DrawTime && now - _swingT > Samurai.WindUp + SwingHit + SwingBack && (!_arc || !_arc.activeSelf);

    // t の姿を書く。刀が見えていれば true
    private bool Apply(float now)
    {
        float d = FxMath.Clamp01((now - _drawT) / Samurai.DrawTime);
        float show = _drawn ? d : 1f - d;
        if (show <= 0f) return false;
        if (!_root.activeSelf) _root.SetActive(true);
        // 抜く: 腰から立てた刀を構えへ下ろす (納めるのは逆)
        float e = show * show * (3f - 2f * show);
        float rot = FxMath.Lerp(SheathRot, RestRot, e);
        float x = FxMath.Lerp(0.12f, RestX, e), y = FxMath.Lerp(-0.30f, RestY, e);
        bool flip = _flip;

        float st = now - _swingT;
        float total = Samurai.WindUp + SwingHit + SwingBack;
        if (st >= 0f && st < total)
        {
            // 振りの間は振る向きの側を向く。角度は右向きの刀の角度 (左向きは根の鏡で写す)
            flip = _swingDeg > 90f || _swingDeg < -90f;
            float a = flip ? 180f - _swingDeg : _swingDeg;
            float raised = a + 110f, cut = a - 75f;
            if (st < Samurai.WindUp) rot = FxMath.Lerp(RestRot, raised, Ease(st / Samurai.WindUp));
            else if (st < Samurai.WindUp + SwingHit) rot = FxMath.Lerp(raised, cut, (st - Samurai.WindUp) / SwingHit);
            else rot = FxMath.Lerp(cut, RestRot, Ease((st - Samurai.WindUp - SwingHit) / SwingBack));
            if (st >= Samurai.WindUp && (!_arc || !_arc.activeSelf) && st < Samurai.WindUp + SwingHit) ShowArc();
        }
        float sx = flip ? -_inv : _inv;
        _rootTf.localScale = FxMath.V3(sx, _inv, 1f);
        // 体より少し手前 (本編のクルーは y/1000 で奥行き)
        _gripTf.localPosition = FxMath.V3(x, y, -0.002f);
        _gripTf.localRotation = FxMath.RotZ(rot);
        _sr.color = Tint(FxMath.Clamp01(show * 1.5f));
        ArcTick(now);
        return true;
    }

    private static float Ease(float t)
    {
        t = FxMath.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    private void ShowArc()
    {
        var pc = _role.Player;
        if (!pc) return;
        var sprite = ButtonIcons.Load(_arcFallen ? "katana_arc_fallen" : "katana_arc", ArcPpu, FxMath.V2(16f / 512f, 0.5f));
        if (!sprite) return;
        if (!_arc)
        {
            _arc = new GameObject("MrpKatanaArc") { layer = pc.gameObject.layer };
            _arcTf = _arc.transform;
            _arcSr = _arc.AddComponent<SpriteRenderer>();
        }
        _arcSr.sprite = sprite;
        _arc.SetActive(true);
        Vector3 at = pc.transform.position;
        _arcTf.position = FxMath.V3(at.x, at.y, at.y / 1000f - 0.02f);
        _arcTf.rotation = FxMath.RotZ(_swingDeg);
        _arcStart = Time.time;
    }

    private float _arcStart;

    private void ArcTick(float now)
    {
        if (!_arc || !_arc.activeSelf) return;
        float t = (now - _arcStart) / ArcLife;
        if (t >= 1f || _hidden)
        {
            _arc.SetActive(false);
            return;
        }
        float s = Samurai.SlashRange / ArcR * (0.9f + 0.15f * t);
        _arcTf.localScale = FxMath.V3(s, s, 1f);
        _arcSr.color = FxMath.Rgba(1f, 1f, 1f, 1f - t * t);
    }

    internal string Describe() => $"root={(_root ? (_root.activeSelf ? "on" : "off") : "none")} drawn={_drawn} hidden={_hidden} flip={_flip} live={Live.Contains(this)}";
}
