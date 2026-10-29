using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Roles.Neutral;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// サムライの抜いた刀 (全員の画面) と振った軌跡。刀は体の子にして一緒に動かす。
// 振っている間は刀が体の中心から振る向きへ伸び、切っ先と刀身の元を毎フレーム控えて帯 (軌跡) で描く。
// 自分の刀はなぞり (SwipeInput) の向きを追い、他人の刀はホストから届いた一振りを同じ時間で再生する。
// 毎フレーム書くのは抜く・納める・振る間だけ (構えて止まっている間は見え方と向きを数フレームに 1 回見るだけ)。
// 構えの位置は体の中心からのワールドの長さ (右向き)。左を向いている間は根を鏡に映す
internal sealed unsafe class SamuraiBlade
{
    // 刀の絵 (Resources/Icons/katana_held.png・440x110): 握る所 (78, 68)。全長が約 0.95 単位になる大きさ
    private const float Ppu = 460f;
    private const float GripPx = 78f, GripPy = 68f, SpriteW = 440f, SpriteH = 110f;
    private const float RestX = 0.30f, RestY = -0.14f, RestRot = -28f; // 構え: 体の右で切っ先を前の下へ
    private const float SheathRot = 95f;                             // 抜く途中: 腰から上へ立てた所
    private const float SwingGrip = 0.16f;                           // 振る間の体の中心から握る所までの長さ
    private const float TipLen = 0.78f, BaseLen = 0.22f;             // 握る所から切っ先・刀身の元まで
    private const float ReplayHold = 0.08f;                          // 再生の終わりに振り抜いた形で止める秒
    private const int CheckEvery = 6;

    // 軌跡の帯 (切っ先と刀身の元を毎フレーム控え、古い物から薄れる)
    private const int TrailN = 18;
    private const float TrailLife = 0.16f;

    private static readonly List<SamuraiBlade> Live = new();
    private static int _frame;
    private static Material _trailMat;

    // 自分の刀 (なぞりの向きを追う)
    internal static SamuraiBlade LocalFollow;

    private readonly Samurai _role;
    private GameObject _root, _trail;
    private Transform _rootTf, _gripTf;
    private SpriteRenderer _sr;
    private bool _drawn, _hidden, _flip, _fallenTint, _swinging;
    private float _drawT = -10f;    // 抜く/納める動作を始めた時刻
    private float _inv = 1f;
    private int _tick;
    // 他人の一振りの再生 (始めた時刻・始めの角度・掃引角・秒・修羅の色)
    private float _repT = -10f, _repA0, _repSweep, _repDur;
    private bool _repFallen;

    private readonly float[] _tx = new float[TrailN * 2], _ty = new float[TrailN * 2], _tt = new float[TrailN];
    private readonly bool[] _tStroke = new bool[TrailN]; // その控えが一振りの最中か (遅い所は薄く)
    private int _tHead, _tCount;
    private Mesh _trailMesh;
    private Il2CppStructArray<Vector3> _tVerts;
    private Il2CppStructArray<Color32> _tCols;

    internal SamuraiBlade(Samurai role) => _role = role;

    internal void SetDrawn(bool drawn)
    {
        _drawn = drawn;
        _drawT = Time.time;
        if (drawn) Ensure();
        if (_root) Track();
    }

    internal void SetFallen(bool tint)
    {
        _fallenTint = tint;
        if (_sr) _sr.color = Tint(1f);
    }

    // 他人の一振りを再生する (a0 度から sweep 度を seconds で)
    internal void Replay(float a0, float sweep, float seconds, bool fallen)
    {
        if (!_drawn || !Ensure()) return;
        _repT = Time.time;
        _repA0 = a0;
        _repSweep = sweep;
        _repDur = FxMath.Max(seconds, 0.05f);
        _repFallen = fallen;
        Track();
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
        if (_trail) Object.Destroy(_trail);
        if (_trailMesh) Object.Destroy(_trailMesh);
        _root = _trail = null;
        _trailMesh = null;
        _tCount = 0;
        if (LocalFollow == this) LocalFollow = null;
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

    private void Track()
    {
        if (!Live.Contains(this)) Live.Add(this);
    }

    private Color Tint(float a) => _fallenTint ? FxMath.Rgba(1f, 0.45f, 0.5f, a) : FxMath.Rgba(1f, 1f, 1f, a);

    // 毎フレーム (動いている刀だけ)
    internal static void TickAll()
    {
        var lf = LocalFollow;
        if (lf != null && (SwipeInput.Dragging || lf._swinging || lf._tCount > 0) && lf._root) lf.Track();
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
            if (hidden && _trail) _trail.SetActive(false);
        }
        if (!_swinging) _flip = pc.cosmetics && pc.cosmetics.FlipX;
    }

    // 動きが終わり、構えたまま (か納め終えた) なら毎フレーム書くのをやめる
    private bool Idle(float now) => !_swinging && _tCount == 0 && now - _drawT > Samurai.DrawTime && now - _repT > _repDur + ReplayHold;

    // 今の振る向き (度)。振っていなければ false
    private bool SwingAngle(float now, out float angle, out bool inStroke)
    {
        angle = 0f;
        inStroke = false;
        if (this == LocalFollow && SwipeInput.Dragging)
        {
            angle = SwipeInput.Wrap(SwipeInput.Angle);
            inStroke = SwipeInput.InStroke;
            return true;
        }
        float t = now - _repT;
        if (t < 0f || t > _repDur + ReplayHold) return false;
        float k = FxMath.Clamp01(t / _repDur);
        // 振りは始めが速く終わりで止まる
        k = 1f - (1f - k) * (1f - k);
        angle = SwipeInput.Wrap(_repA0 + _repSweep * k);
        inStroke = t <= _repDur;
        return true;
    }

    // t の姿を書く。刀が見えていれば true
    private bool Apply(float now)
    {
        float d = FxMath.Clamp01((now - _drawT) / Samurai.DrawTime);
        float show = _drawn ? d : 1f - d;
        if (show <= 0f)
        {
            _swinging = false;
            return false;
        }
        if (!_root.activeSelf) _root.SetActive(true);
        // 抜く: 腰から立てた刀を構えへ下ろす (納めるのは逆)
        float e = show * show * (3f - 2f * show);
        float rot = FxMath.Lerp(SheathRot, RestRot, e);
        float x = FxMath.Lerp(0.12f, RestX, e), y = FxMath.Lerp(-0.30f, RestY, e);
        bool flip = _flip;
        float gsy = 1f;
        float a = 0f;
        bool inStroke = false;
        bool swing = _drawn && d >= 1f && SwingAngle(now, out a, out inStroke);
        if (swing)
        {
            // 刀は体の中心から振る向きへ伸びる。左半分では刃を下に保つため上下を返す (根は返さない)
            float ar = a * FxMath.Deg2Rad;
            flip = false;
            rot = a;
            gsy = a > 90f || a < -90f ? -1f : 1f;
            x = FxMath.Cos(ar) * SwingGrip;
            y = FxMath.Sin(ar) * SwingGrip - 0.05f;
            Sample(now, x, y, a, inStroke);
        }
        if (_swinging && !swing && _role.Player) _flip = flip = _role.Player.cosmetics && _role.Player.cosmetics.FlipX;
        _swinging = swing;
        float sx = flip ? -_inv : _inv;
        _rootTf.localScale = FxMath.V3(sx, _inv, 1f);
        // 体より少し手前 (本編のクルーは y/1000 で奥行き)
        _gripTf.localPosition = FxMath.V3(x, y, -0.002f);
        _gripTf.localRotation = FxMath.RotZ(rot);
        _gripTf.localScale = FxMath.V3(1f, gsy, 1f);
        _sr.color = Tint(FxMath.Clamp01(show * 1.5f));
        DrawTrail(now);
        return true;
    }

    // 今の切っ先と刀身の元をワールドで控える
    private void Sample(float now, float gx, float gy, float deg, bool inStroke)
    {
        var pc = _role.Player;
        Vector3 c = pc ? pc.transform.position : default;
        float ar = deg * FxMath.Deg2Rad, co = FxMath.Cos(ar), si = FxMath.Sin(ar);
        float bx = c.x + gx, by = c.y + gy;
        int i = _tHead;
        _tx[i * 2] = bx + co * TipLen; _ty[i * 2] = by + si * TipLen;
        _tx[i * 2 + 1] = bx + co * BaseLen; _ty[i * 2 + 1] = by + si * BaseLen;
        _tt[i] = now;
        _tStroke[i] = inStroke;
        _tHead = (_tHead + 1) % TrailN;
        if (_tCount < TrailN) _tCount++;
    }

    // 軌跡: 控えを古い順に帯でつなぐ。控えは TrailLife で消える
    private void DrawTrail(float now)
    {
        while (_tCount > 0)
        {
            int oldest = (_tHead - _tCount + TrailN) % TrailN;
            if (now - _tt[oldest] <= TrailLife) break;
            _tCount--;
        }
        if (_tCount < 2 || _hidden)
        {
            if (_trail && _trail.activeSelf) _trail.SetActive(false);
            return;
        }
        if (!EnsureTrail()) return;
        if (!_trail.activeSelf) _trail.SetActive(true);
        var pc = _role.Player;
        float z = pc ? pc.transform.position.y / 1000f - 0.015f : 0f;
        bool red = this == LocalFollow ? _fallenTint : _repFallen;
        float* vp = (float*)((byte*)_tVerts.Pointer + 4 * IntPtr.Size);
        byte* cp = (byte*)_tCols.Pointer + 4 * IntPtr.Size;
        int start = (_tHead - _tCount + TrailN) % TrailN;
        for (int k = 0; k < TrailN; k++)
        {
            // 使っていない欄は最後の控えに重ねて潰す
            int kk = k < _tCount ? k : _tCount - 1;
            int i = (start + kk) % TrailN;
            float age = FxMath.Clamp01((now - _tt[i]) / TrailLife);
            float fade = (1f - age) * (_tStroke[i] ? 1f : 0.25f) * (k < _tCount ? 1f : 0f);
            float t = (float)kk / (TrailN - 1);
            for (int e = 0; e < 2; e++)
            {
                int vi = k * 2 + e;
                vp[vi * 3] = _tx[i * 2 + e];
                vp[vi * 3 + 1] = _ty[i * 2 + e];
                vp[vi * 3 + 2] = z;
                byte* col = cp + vi * 4;
                if (red) { col[0] = 200; col[1] = 20; col[2] = 50; }
                else { col[0] = (byte)(74 + 181 * t); col[1] = (byte)(140 - 45 * t); col[2] = (byte)(255 - 87 * t); }
                // 切っ先の側を濃く・元の側を薄く
                col[3] = (byte)(255f * fade * (e == 0 ? 0.95f : 0.15f));
            }
        }
        _trailMesh.vertices = _tVerts;
        _trailMesh.colors32 = _tCols;
    }

    private bool EnsureTrail()
    {
        if (_trail) return true;
        var pc = _role.Player;
        if (!pc) return false;
        _trail = new GameObject("MrpKatanaTrail") { layer = pc.gameObject.layer };
        _tVerts = new Il2CppStructArray<Vector3>((long)(TrailN * 2));
        _tCols = new Il2CppStructArray<Color32>((long)(TrailN * 2));
        var idx = new Il2CppStructArray<int>((long)((TrailN - 1) * 6));
        for (int k = 0; k < TrailN - 1; k++)
        {
            int a = k * 2, b = a + 1, c = a + 2, d = a + 3, o = k * 6;
            idx[o] = a; idx[o + 1] = c; idx[o + 2] = b;
            idx[o + 3] = b; idx[o + 4] = c; idx[o + 5] = d;
        }
        _trailMesh = new Mesh { name = "MrpKatanaTrail" };
        _trailMesh.vertices = _tVerts;
        _trailMesh.colors32 = _tCols;
        _trailMesh.triangles = idx;
        _trailMesh.bounds = new Bounds(Vector3.zero, FxMath.V3(1000f, 1000f, 10f));
        _trail.AddComponent<MeshFilter>().mesh = _trailMesh;
        var mr = _trail.AddComponent<MeshRenderer>();
        if (!_trailMat)
        {
            // 見つからない端末では刀の絵の材質 (同じ Sprites/Default) を写す
            var shader = Shader.Find("Sprites/Default");
            _trailMat = shader ? new Material(shader) : new Material(_sr.sharedMaterial);
            _trailMat.hideFlags = HideFlags.HideAndDontSave;
        }
        mr.sharedMaterial = _trailMat;
        return true;
    }

    internal string Describe() =>
        $"root={(_root ? (_root.activeSelf ? "on" : "off") : "none")} drawn={_drawn} hidden={_hidden} flip={_flip} live={Live.Contains(this)} swing={_swinging} trail={_tCount}";
}
