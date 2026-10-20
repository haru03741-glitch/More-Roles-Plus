// Based on https://github.com/waffle-ful/Aeterna-End-K-not Modules/ExplosionFxPortalWindow.cs・Modules/ExplosionFxRoles6.cs (行き先を映す窓と床の装置・GPL-3.0)
using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// ポータルの絵: 足元に立つ縦長の渦。暗い下地 → 青緑のにじみ → 逆向きに回る渦 2 枚 → 白い芯 → 吸い込まれる光の粒。
// 開く時は縦の線から横へ広がり、閉じる時は縦の線に縮む。人が通った時は光が膨らんで縦に抜ける。
// 絵は人と同じ奥行きの決め方 (足元の y に体の高さを足して /1000) なので、手前を通る人と上に立った人はポータルの前に出る。
// 2 つ揃うと、楕円の中に相手のポータルの周りの景色が映る (行き先ごとに小さなカメラで写す)。
// ポータルは床に置いた装置 (紫の円盤に脚 3 本・真ん中に水色のレンズ) の上に浮かび、レンズから光が立ちのぼる。
// 毎フレーム触るのは近くにあるポータルだけ (遠い物と視界の外は影に隠れるので動かさない)
internal static class PortalArt
{
    private const float W = 0.9f, H = 1.3f;        // 楕円の幅と高さ
    private const float Lift = 0.6f;               // 足元から楕円の中心まで (下の端は装置のレンズの少し上)
    internal const float MachineDrop = 0.3f;       // 足元から装置の真ん中まで (下へ)
    internal const float MachineW = 1.2f;
    private const float MachineBack = 0.9f;        // 装置は床に置いた物として、足元がこの y より下の人の後ろに描く
    private const float BodyLift = 0.3636f;        // 人の奥行きは体の y/1000 で、体は足元よりこれだけ上
    private const float LayerStep = 0.00001f;      // 重ねる絵どうしの奥行きの差 (人との前後は変えない程度)
    private const float SinkBehind = 0.25f;        // ポータルの上に立った人 (着いた人) が絵に隠れないよう、この y の分だけ奥に置く
    private const float OpenTime = 0.35f, CloseTime = 0.3f, PassTime = 0.4f;
    private const float AnimRange = 14f;           // これより遠いポータルは毎フレーム動かさない
    private const int Motes = 4;
    private const int MaxBursts = 12;              // 通った光が同時にこれより多ければ足さない
    // ポータル自身の絵の層。窓のカメラはこの層を写さない (窓の中にポータルが映り込まない)。メインカメラは写す
    private const int PortalLayer = 1;
    private const float MachineStep = 0.00001f;    // 装置の部品どうしの奥行きの差 (床と水の板の間 0.0004 に 10 枚が収まる)
    private const float LipHeight = 0.08f;         // 楕円の下の縁の床からの高さ (水位がこれを越えると縁に泡が立つ)

    private static readonly Color Deep = FxMath.Rgba(0.02f, 0.05f, 0.16f, 0.95f);
    private static readonly Color Teal = FxMath.Rgba(0.2f, 0.95f, 0.85f, 1f);
    private static readonly Color Blue = FxMath.Rgba(0.15f, 0.45f, 1f, 1f);
    private static readonly Color Violet = FxMath.Rgba(0.62f, 0.48f, 1f, 1f);
    private static readonly Color White = FxMath.Rgba(0.9f, 1f, 1f, 1f);
    private static readonly Color MachineOutline = FxMath.Rgba(0.25f, 0.13f, 0.42f, 1f);
    private static readonly Color MachineBody = FxMath.Rgba(0.68f, 0.52f, 0.9f, 1f);
    private static readonly Color MachineTop = FxMath.Rgba(0.82f, 0.7f, 0.98f, 1f);
    private static readonly Color MachineBand = FxMath.Rgba(0.42f, 0.27f, 0.68f, 1f);
    private static readonly Color MachineLens = FxMath.Rgba(0.7f, 0.9f, 1f, 1f);

    // 窓: 描き先の大きさ・写す範囲 (楕円の半分の高さの何倍か。1 より大きいと少し引いた景色)・内側の楕円の大きさ
    private const int WindowTexW = 160, WindowTexH = 240;
    private const float WindowZoom = 1.35f;
    private const float WindowInner = 0.88f;
    // 窓に写さない層: 5 = UI / 8 = 人 / 10 = 影 / 14 = 幽霊 / ポータル自身。視界の外の人が窓越しに見えないように。
    // 水・火・壊れた所などの絵 (層 0) は写す。死体も層 0 なので、写す間だけ消す
    private const int WindowHiddenLayers = (1 << 5) | (1 << 8) | (1 << 10) | (1 << 14) | (1 << PortalLayer);

    internal sealed class Handle
    {
        internal GameObject Root;
        internal Transform Body, SwirlA, SwirlB, Machine;
        internal SpriteRenderer Halo, Core, Floor, SwirlASr, SwirlBSr, Beam, LensGlow, Foam, Ripple;
        internal Camera Cam;
        internal RenderTexture Rt;
        internal Material WinMat;
        internal MeshRenderer Win;
        internal bool WinOn;
        internal float Water, WaterShown; // 足元の水の深さ (単位) と、見せている深さ (なめらかに追う)
        internal bool FoamOn;
        internal readonly Transform[] Mote = new Transform[Motes];
        internal readonly SpriteRenderer[] MoteSr = new SpriteRenderer[Motes];
        internal float X, Y, Born, Closing = -1f, Spin;
    }

    private sealed class Burst
    {
        internal GameObject Root;
        internal Transform Glow, Column;
        internal SpriteRenderer GlowSr, ColumnSr;
        internal float Born;
    }

    private static readonly List<Handle> Live = new();
    private static readonly List<Burst> Bursts = new();
    private static GameObject _container;
    private static int _gen = -1;
    private static Sprite _glow, _swirl, _disc, _mBody, _mTop, _mBand, _mLens, _mStar;
    private static Mesh _windowMesh;
    private static int _meetingPoll;
    private static bool _meeting;
    private static int _waterPoll;
    private static int _renderTurn;
    private static int _bodyPoll;
    private static float _bodyWatchUntil; // 誰かが倒された後しばらくは、窓を描くたびに死体を拾い直す (倒れた直後の死体を映さない)
    private static readonly List<SpriteRenderer> BodyArt = new();
    private static readonly List<SpriteRenderer> BodyHidden = new();

    internal static int LiveCount => Live.Count;

    // 足元 (x, y) にポータルを開く
    public static Handle Open(float x, float y)
    {
        var parent = Container();
        if (!parent) return null;
        var h = new Handle { X = x, Y = y, Born = Time.time, Spin = FxMath.Range(0f, 360f) };
        h.Root = new GameObject("MrpPortal") { layer = PortalLayer };
        h.Root.transform.SetParent(parent.transform, false);
        h.Root.transform.position = FxMath.V3(x, y + Lift, (y + SinkBehind + BodyLift) / 1000f);

        BuildMachine(h, parent.transform, x, y);

        var body = new GameObject("body") { layer = PortalLayer };
        body.transform.SetParent(h.Root.transform, false);
        body.transform.localScale = FxMath.V3(0.02f, H, 1f);
        h.Body = body.transform;
        // にじみは穴の後ろ (外へはみ出た分だけ見える)。窓は穴と渦の間
        h.Halo = Layer(h.Body, "halo", _glow, FxMath.V3(0f, 0f, LayerStep), FxMath.V3(1.6f, 1.35f, 1f), FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0.4f));
        Layer(h.Body, "deep", _disc, FxMath.V3(0f, 0f, 0f), FxMath.V3(0.97f, 0.97f, 1f), Deep);
        h.SwirlASr = Layer(h.Body, "swirlA", _swirl, FxMath.V3(0f, 0f, -LayerStep * 2f), FxMath.V3(1f, 1f, 1f), FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0.95f));
        h.SwirlA = h.SwirlASr.transform;
        h.SwirlBSr = Layer(h.Body, "swirlB", _swirl, FxMath.V3(0f, 0f, -LayerStep * 3f), FxMath.V3(0.78f, 0.78f, 1f), FxMath.Rgba(Violet.r, Violet.g, Violet.b, 0.7f));
        h.SwirlB = h.SwirlBSr.transform;
        h.Core = Layer(h.Body, "core", _glow, FxMath.V3(0f, 0f, -LayerStep * 4f), FxMath.V3(0.42f, 0.36f, 1f), FxMath.Rgba(White.r, White.g, White.b, 0.85f));
        // 水位の線の泡と、そこから広がる波 (楕円より手前・水が無い間は透明)
        h.Foam = Layer(h.Root.transform, "foam", _glow, FxMath.V3(0f, -Lift, -LayerStep * 6f), FxMath.V3(0.6f, 0.1f, 1f), FxMath.Rgba(White.r, White.g, White.b, 0f));
        h.Ripple = Layer(h.Root.transform, "ripple", _glow, FxMath.V3(0f, -Lift, -LayerStep * 5.5f), FxMath.V3(0.6f, 0.1f, 1f), FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0f));
        for (int i = 0; i < Motes; i++)
        {
            h.MoteSr[i] = Layer(h.Body, "mote", _glow, FxMath.V3(0f, 0f, -LayerStep * 5f), FxMath.V3(0.09f, 0.065f, 1f), FxMath.Rgba(White.r, White.g, White.b, 0f));
            h.Mote[i] = h.MoteSr[i].transform;
        }
        Live.Add(h);
        Pulse(x, y, 0.7f);
        BreakNoise.PlayAt("noise_portal_open", x, y, 9f, 5f, 0.8f, FxMath.Range(0.95f, 1.05f));
        return h;
    }

    // 縦の線に縮めて消す
    public static void Close(Handle h)
    {
        if (h == null || h.Closing >= 0f) return;
        h.Closing = Time.time;
        Pulse(h.X, h.Y, 0.6f);
        BreakNoise.PlayAt("noise_portal_close", h.X, h.Y, 9f, 5f, 0.8f, FxMath.Range(0.95f, 1.05f));
    }

    // 装置が壊れた: 光が弾けて、装置ごと縮んで閉じる
    public static void Break(Handle h)
    {
        if (h == null || h.Closing >= 0f) return;
        h.Closing = Time.time;
        Pulse(h.X, h.Y, 1.5f);
        BreakNoise.PlayAt("noise_hit_metal", h.X, h.Y, 9f, 5f, 1f, FxMath.Range(0.85f, 0.95f));
        BreakNoise.PlayAt("noise_portal_close", h.X, h.Y, 9f, 5f, 0.9f, FxMath.Range(0.75f, 0.85f));
    }

    // 演出なしですぐ消す (役職が外れた時)
    public static void Remove(Handle h)
    {
        if (h == null) return;
        Live.Remove(h);
        Drop(h);
    }

    // 2 つ揃った: それぞれの楕円に相手の周りを映す
    public static void Link(Handle a, Handle b)
    {
        if (a == null || b == null || !a.Root || !b.Root) return;
        BuildWindow(a, b.X, b.Y + Lift);
        BuildWindow(b, a.X, a.Y + Lift);
    }

    // 人が通った (入った側と出た側の両方で呼ぶ)
    public static void Pass(float x, float y)
    {
        Pulse(x, y, 1f);
        BreakNoise.PlayAt("noise_portal_pass", x, y, 9f, 5f, 0.9f, FxMath.Range(0.95f, 1.08f));
    }

    private static void Pulse(float x, float y, float size)
    {
        if (Bursts.Count >= MaxBursts) return;
        var parent = Container();
        if (!parent) return;
        var b = new Burst { Born = Time.time };
        b.Root = new GameObject("MrpPortalBurst") { layer = PortalLayer };
        b.Root.transform.SetParent(parent.transform, false);
        // 人より少し手前に出す (光が人にかぶさる)
        b.Root.transform.position = FxMath.V3(x, y + Lift, (y + BodyLift) / 1000f - 0.002f);
        b.Root.transform.localScale = FxMath.V3(size, size, 1f);
        b.GlowSr = Layer(b.Root.transform, "glow", _glow, FxMath.V3(0f, 0f, 0f), FxMath.V3(0.4f, 0.5f, 1f), FxMath.Rgba(White.r, White.g, White.b, 0f));
        b.Glow = b.GlowSr.transform;
        b.ColumnSr = Layer(b.Root.transform, "column", _glow, FxMath.V3(0f, 0.2f, -LayerStep), FxMath.V3(0.3f, 0.6f, 1f), FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0f));
        b.Column = b.ColumnSr.transform;
        Bursts.Add(b);
    }

    public static void Tick()
    {
        if (Live.Count == 0 && Bursts.Count == 0) return;
        if (GameClock.ShipGen != _gen) { DropAll(); _container = null; return; }
        float now = Time.time, dt = Time.deltaTime;
        // 会議中は窓を写さない (10 回に 1 回だけ見る)
        if (--_meetingPoll <= 0) { _meetingPoll = 10; _meeting = MeetingHud.Instance; }
        // 足元の水は 6 回に 1 回だけ読む (泡はなめらかに追うので粗くてよい)
        bool readWater = --_waterPoll <= 0;
        if (readWater) _waterPoll = 6;
        bool water = readWater && WaterSim.Running && WaterSim.Ready;
        float mx = 0f, my = 0f;
        bool haveMe = false;
        var lp = PlayerControl.LocalPlayer;
        if (lp)
        {
            var p = lp.transform.position;
            mx = p.x; my = p.y; haveMe = true;
        }

        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var h = Live[i];
            if (!h.Root) { Live.RemoveAt(i); continue; }
            float age = now - h.Born;
            if (h.Closing >= 0f)
            {
                float k = (now - h.Closing) / CloseTime;
                if (k >= 1f) { Live.RemoveAt(i); Drop(h); continue; }
                float q = 1f - k;
                h.Body.localScale = FxMath.V3(W * q * q, H * (1f + 0.15f * k), 1f);
                h.Floor.color = FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0.3f * q);
                h.Machine.localScale = FxMath.V3(q, q, 1f);
                ShowWindow(h, false);
                continue;
            }
            float dx = h.X - mx, dy = h.Y - my;
            bool near = !haveMe || dx * dx + dy * dy <= AnimRange * AnimRange;
            ShowWindow(h, near && !_meeting);
            if (!near && age > OpenTime) continue;

            if (age < OpenTime + 0.05f)
            {
                // 縦の線から横へ広がり、少し行き過ぎて戻る
                float k = FxMath.Clamp01(age / OpenTime);
                float e = 1f - (1f - k) * (1f - k) * (1f - k);
                h.Body.localScale = FxMath.V3(W * FxMath.Max(e * (1f + 0.06f * FxMath.Sin(FxMath.PI * k)), 0.02f), H, 1f);
                float m = 0.6f + 0.4f * e;
                h.Machine.localScale = FxMath.V3(m, m, 1f);
            }

            h.Spin += dt;
            float s = h.Spin;
            h.SwirlA.localRotation = FxMath.RotZ(-110f * s);
            h.SwirlB.localRotation = FxMath.RotZ(70f * s + 40f);
            // にじみと芯は呼吸する (周期の違う 2 つの波を足して単調にしない)
            float breathe = 0.5f + 0.3f * FxMath.Sin(s * 2.3f) + 0.2f * FxMath.Sin(s * 3.7f + 1f);
            h.Halo.color = FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0.3f + 0.2f * breathe);
            // 窓がある時は芯を控えめにして行き先を見せる
            float core = h.Cam != null ? 0.25f + 0.2f * breathe : 0.65f + 0.3f * breathe;
            h.Core.color = FxMath.Rgba(White.r, White.g, White.b, core);
            h.Beam.color = FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0.45f + 0.25f * breathe);
            h.LensGlow.color = FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0.35f + 0.25f * breathe);
            if (readWater) h.Water = water ? WaterSim.DepthAt(FxMath.V2(h.X, h.Y)) / 1024f : 0f;
            ShowFoam(h, s, dt);

            // 光の粒: 縁から渦を巻いて芯へ吸い込まれる
            for (int m = 0; m < Motes; m++)
            {
                float t = FxMath.Repeat(s * 0.55f + m / (float)Motes, 1f);
                float r = (1f - t) * 0.46f;
                float a = m * (FxMath.PI * 2f / Motes) - t * FxMath.PI * 3f;
                h.Mote[m].localPosition = FxMath.V3(r * FxMath.Cos(a), r * FxMath.Sin(a), -LayerStep * 5f);
                h.MoteSr[m].color = FxMath.Rgba(White.r, White.g, White.b, FxMath.Sin(FxMath.PI * t) * 0.9f);
            }
        }

        for (int i = Bursts.Count - 1; i >= 0; i--)
        {
            var b = Bursts[i];
            float k = (now - b.Born) / PassTime;
            if (k >= 1f || !b.Root)
            {
                Bursts.RemoveAt(i);
                if (b.Root) UnityEngine.Object.Destroy(b.Root);
                continue;
            }
            // 一瞬で明るくなり、膨らみながら薄れる。縦の光は上へ伸びて抜ける
            float rise = k < 0.12f ? k / 0.12f : 1f - (k - 0.12f) / 0.88f;
            float grow = 1f - (1f - k) * (1f - k);
            b.Glow.localScale = FxMath.V3(0.4f + 1.4f * grow, 0.5f + 1.9f * grow, 1f);
            b.GlowSr.color = FxMath.Rgba(FxMath.Lerp(White.r, Teal.r, k), FxMath.Lerp(White.g, Teal.g, k), FxMath.Lerp(White.b, Teal.b, k), 0.9f * rise);
            b.Column.localScale = FxMath.V3(0.3f * (1f - 0.5f * k), 0.6f + 2.2f * grow, 1f);
            b.Column.localPosition = FxMath.V3(0f, 0.2f + 0.9f * grow, -LayerStep);
            b.ColumnSr.color = FxMath.Rgba(Blue.r * 0.5f + 0.5f * Teal.r, Teal.g, Teal.b, 0.6f * rise);
        }
    }

    // 船ごとに 1 つの入れ物 (船と一緒に消える)。船の倍率と反転は打ち消す
    private static GameObject Container()
    {
        var ship = ShipStatus.Instance;
        if (!ship || !GameClock.ShipAlive) return null;
        if (_gen == GameClock.ShipGen && _container) return _container;
        DropAll();
        EnsureSprites();
        _container = new GameObject("MrpPortals");
        _container.transform.SetParent(ship.transform, false);
        Vector3 ls = ship.transform.lossyScale;
        _container.transform.localScale = FxMath.V3(1f / ls.x, 1f / ls.y, 1f / ls.z);
        _gen = GameClock.ShipGen;
        return _container;
    }

    private static SpriteRenderer Layer(Transform parent, string name, Sprite sp, Vector3 pos, Vector3 scale, Color color)
    {
        var go = new GameObject(name) { layer = PortalLayer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        sr.color = color;
        return sr;
    }

    // 絵は起動中に 1 回だけ作り、試合をまたいで使い回す (白で作り、色は頂点色で付ける)
    private static void EnsureSprites()
    {
        _glow ??= Make(64, Glow);
        _swirl ??= Make(128, Swirl);
        _disc ??= Make(64, Disc);
        _mBody ??= MakeXY(256, 192, MachineBodyAlpha);
        _mTop ??= MakeXY(256, 192, MachineTopAlpha);
        _mBand ??= MakeXY(128, 128, RoundAlpha);
        _mLens ??= MakeXY(128, 128, LensAlpha);
        _mStar ??= MakeXY(128, 128, StarAlpha);
    }

    private static void Drop(Handle h)
    {
        ReleaseWindow(h);
        if (h.Machine) UnityEngine.Object.Destroy(h.Machine.gameObject);
        if (h.Root) UnityEngine.Object.Destroy(h.Root);
    }

    // 船が替わった・入れ物を作り直す: 絵は船と一緒に消えるが、描き先と材質は自分で片付ける
    private static void DropAll()
    {
        for (int i = 0; i < Live.Count; i++) ReleaseWindow(Live[i]);
        Live.Clear();
        Bursts.Clear();
    }

    // ── 床の装置 (斜め上から見た円盤と脚 3 本・レンズを囲む濃い帯・水色のレンズに白い星) ──

    private static void BuildMachine(Handle h, Transform parent, float x, float y)
    {
        var root = new GameObject("MrpPortalMachine") { layer = PortalLayer };
        root.transform.SetParent(parent, false);
        // 装置は床に置いた低い物なので、本編のベントと同じく床と水の板の間に置く (水が来ると沈み、人は必ずその上に立つ)
        float my = y - MachineDrop;
        float front = DamageMap.FrontZ(FxMath.V2(x, my));
        float zs = DamageMap.ZScale(front);
        root.transform.position = FxMath.V3(x, my, front - 0.0002f * zs);
        h.Machine = root.transform;
        var t = root.transform;
        float w = MachineW, d = MachineW * 0.75f, st = MachineStep * zs;
        // 床の照り返しと影は部品の奥
        h.Floor = Layer(t, "floor", _glow, FxMath.V3(0f, 0f, st * 2f), FxMath.V3(1.9f, 0.6f, 1f), FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0.3f));
        Layer(t, "shade", _glow, FxMath.V3(0f, -0.1f, st), FxMath.V3(w * 1.5f, d * 0.9f, 1f), FxMath.Rgba(0f, 0f, 0f, 0.45f));
        Part(t, _mBody, 0f, -0.01f, w * 1.1f, d * 1.12f, MachineOutline, st, 1);
        Part(t, _mBody, 0f, 0f, w, d, MachineBody, st, 2);
        Part(t, _mTop, 0f, 0.01f, w, d, MachineTop, st, 3);
        Part(t, _mBand, 0f, 0.05f, w * 0.5f, d * 0.45f, MachineOutline, st, 4);
        Part(t, _mBand, 0f, 0.05f, w * 0.46f, d * 0.41f, MachineBand, st, 5);
        Part(t, _mLens, 0f, 0.05f, w * 0.36f, d * 0.31f, MachineLens, st, 6);
        Part(t, _mStar, 0f, 0.05f, w * 0.34f, d * 0.3f, White, st, 7);
        // レンズの照りと、レンズから楕円の下の端へ立ちのぼる光
        h.LensGlow = Layer(t, "lensGlow", _glow, FxMath.V3(0f, 0.06f, -st * 8f), FxMath.V3(0.7f, 0.35f, 1f), FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0.5f));
        float beamLen = MachineDrop + Lift - H * 0.5f + 0.15f;
        h.Beam = Layer(t, "beam", _glow, FxMath.V3(0f, 0.05f + beamLen * 0.5f, -st * 9f), FxMath.V3(0.3f, beamLen * 2.4f, 1f), FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0.55f));
    }

    // 絵の縦横比は MakeXY の ppu (= 幅) で縦が h/w 倍になっているので、縦の倍率で戻す
    private static void Part(Transform parent, Sprite sp, float ox, float oy, float sx, float sy, Color color, float step, int order)
    {
        Rect r = sp.rect;
        Layer(parent, "part", sp, FxMath.V3(ox, oy, -step * order), FxMath.V3(sx, sy * r.width / r.height, 1f), color);
    }

    // 楕円の下の縁より水位が高い時、水位の線に泡を立て、波を外へ広げる (水はポータルを通らない)
    private static void ShowFoam(Handle h, float s, float dt)
    {
        h.WaterShown += (h.Water - h.WaterShown) * FxMath.Min(1f, dt * 4f);
        float sub = h.WaterShown - LipHeight;
        if (sub <= 0.01f || sub >= H)
        {
            if (h.FoamOn) { h.FoamOn = false; h.Foam.color = FxMath.Rgba(White.r, White.g, White.b, 0f); h.Ripple.color = FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0f); }
            return;
        }
        h.FoamOn = true;
        // 楕円の中心からの高さと、その高さでの楕円の幅
        float ly = -H * 0.5f + sub;
        float q = ly / (H * 0.5f);
        float chord = W * FxMath.Sqrt(FxMath.Max(0f, 1f - q * q));
        float wob = 0.5f + 0.5f * FxMath.Sin(s * 5.3f) * FxMath.Sin(s * 2.1f + 0.7f);
        h.Foam.transform.localPosition = FxMath.V3(0f, ly + 0.01f * FxMath.Sin(s * 3.1f), -LayerStep * 6f);
        h.Foam.transform.localScale = FxMath.V3(chord * (1.15f + 0.08f * wob), 0.09f + 0.03f * wob, 1f);
        h.Foam.color = FxMath.Rgba(White.r, White.g, White.b, 0.55f + 0.25f * wob);
        // 波: 1.4 秒ごとに縁から外へ広がって薄れる
        float k = FxMath.Repeat(s / 1.4f, 1f);
        h.Ripple.transform.localPosition = FxMath.V3(0f, ly, -LayerStep * 5.5f);
        h.Ripple.transform.localScale = FxMath.V3(chord * (1.1f + 1.4f * k), 0.12f + 0.22f * k, 1f);
        h.Ripple.color = FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0.45f * (1f - k));
    }

    private static float Ell(float x, float y, float cx, float cy, float rx, float ry, float soft)
    {
        float dx = (x - cx) / rx, dy = (y - cy) / ry;
        return FxMath.Clamp01((1f - FxMath.Sqrt(dx * dx + dy * dy)) * FxMath.Min(rx, ry) / soft);
    }

    // 円盤 + 左・右・手前の脚 (外へ向かって細くなる丸い楔)。y は奥行きで縮んでいる
    private static float MachineBodyAlpha(float x, float y)
    {
        float a = Ell(x, y, 0f, 0.05f, 0.64f, 0.5f, 0.03f);
        a = FxMath.Max(a, Foot(x, y, -1f, -0.05f));
        a = FxMath.Max(a, Foot(x, y, 1f, -0.05f));
        return FxMath.Max(a, Foot(x, y, 0f, -1f));
    }

    private static float Foot(float x, float y, float dx, float dy)
    {
        float len = FxMath.Sqrt(dx * dx + dy * dy);
        dx /= len; dy /= len;
        float yy = y / 0.75f;
        float along = x * dx + yy * dy, perp = FxMath.Abs(-x * dy + yy * dx);
        if (along < 0.3f || along > 0.88f) return 0f;
        float half = 0.36f - 0.16f * (along - 0.3f) / 0.58f;
        float tip = FxMath.Clamp01((0.88f - along) / 0.06f);
        return FxMath.Clamp01((half - perp) / 0.03f) * tip;
    }

    private static float MachineTopAlpha(float x, float y) => Ell(x, y, 0f, 0.12f, 0.56f, 0.4f, 0.03f);

    private static float RoundAlpha(float x, float y) => Ell(x, y, 0f, 0f, 0.98f, 0.98f, 0.03f);

    private static float LensAlpha(float x, float y) => Ell(x, y, 0f, 0f, 0.98f, 0.98f, 0.04f);

    // レンズの白い星: 長短 8 本の筋 + 芯
    private static float StarAlpha(float x, float y)
    {
        float r = FxMath.Sqrt(x * x + y * y);
        if (r > 1f) return 0f;
        float ang = FxMath.Atan2(y, x);
        float a = FxMath.Clamp01((0.22f - r) / 0.06f);
        for (int k = 0; k < 8; k++)
        {
            float t = ang - k * FxMath.PI / 4f;
            if (FxMath.Cos(t) <= 0f) continue;
            float reach = k % 2 == 0 ? 0.92f : 0.6f;
            float w = (k % 2 == 0 ? 0.09f : 0.07f) * (1f - r / reach);
            float d = FxMath.Abs(FxMath.Sin(t)) * r;
            if (r < reach) a = FxMath.Max(a, FxMath.Clamp01((w - d) / 0.025f));
        }
        return a;
    }

    // ── 行き先を映す窓 ──

    private static void BuildWindow(Handle h, float vx, float vy)
    {
        ReleaseWindow(h);
        Camera main = Camera.main;
        Shader shader = Shader.Find("UI/Default");
        if (!main || !shader || !_container) return;
        EnsureWindowMesh();

        // 部屋の床と壁はステンシルで描くので、ステンシル付きの深度 (24) が要る
        h.Rt = new RenderTexture(WindowTexW, WindowTexH, 24) { name = "MrpPortalWindow" };
        h.Rt.hideFlags |= HideFlags.HideAndDontSave;

        var camGo = new GameObject("MrpPortalWindowCam");
        camGo.transform.SetParent(_container.transform, false);
        h.Cam = camGo.AddComponent<Camera>();
        h.Cam.CopyFrom(main);
        // 影の板 (メインカメラの前に付いていて、自分の視界の暗さを描く) も写さない。行き先がメインカメラの写す範囲に重なると、
        // その板まで写り込んで窓の中身が自分の位置で変わってしまう
        int hidden = WindowHiddenLayers;
        var hud = Vanilla.Hud;
        if (hud && hud.ShadowQuad) hidden |= 1 << hud.ShadowQuad.gameObject.layer;
        h.Cam.cullingMask = main.cullingMask & ~hidden;
        h.Cam.clearFlags = CameraClearFlags.SolidColor;
        h.Cam.backgroundColor = main.backgroundColor;
        h.Cam.orthographic = true;
        h.Cam.orthographicSize = H * 0.5f * WindowZoom;
        h.Cam.aspect = W / H;
        h.Cam.depth = main.depth - 2f;
        h.Cam.targetTexture = h.Rt;
        // 自分の手で描く (2 つの窓を 1 フレームずつ交代で・死体を消してから)
        h.Cam.enabled = false;
        camGo.transform.position = FxMath.V3(vx, vy, main.transform.position.z);

        var win = new GameObject("window") { layer = PortalLayer };
        win.transform.SetParent(h.Body, false);
        win.transform.localPosition = FxMath.V3(0f, 0f, -LayerStep);
        win.transform.localScale = FxMath.V3(0.5f * WindowInner, 0.5f * WindowInner, 1f);
        win.AddComponent<MeshFilter>().sharedMesh = _windowMesh;
        h.WinMat = new Material(shader) { name = "MrpPortalWindow" };
        h.WinMat.hideFlags |= HideFlags.HideAndDontSave;
        // 描き先の透明度は使わない (床の絵が半透明でも窓は不透明に見せる)
        h.WinMat.SetVector("_TextureSampleAdd", new Vector4(0f, 0f, 0f, 1f));
        h.WinMat.mainTexture = h.Rt;
        h.Win = win.AddComponent<MeshRenderer>();
        h.Win.sharedMaterial = h.WinMat;
        h.WinOn = true;
        // 渦は薄くして景色を透かす
        h.SwirlASr.color = FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0.42f);
        h.SwirlBSr.color = FxMath.Rgba(Violet.r, Violet.g, Violet.b, 0.2f);
    }

    private static void ShowWindow(Handle h, bool on)
    {
        if (h.Cam == null || h.WinOn == on) return;
        h.WinOn = on;
        h.Win.enabled = on;
    }

    // 見えている窓を 1 フレームに 1 つだけ描く (2 つあれば交代で・それぞれ 2 フレームに 1 回)。
    // 死体はこの mod の地形の絵と同じ層にあるので、描く間だけ消す (窓越しに視界の外の死体を見つけられないように)
    public static void LateTick()
    {
        if (Live.Count == 0 || GameClock.ShipGen != _gen) return;
        RenderWindow();
    }

    // 誰かが倒された: 死体はキルの動きの後に出るので、しばらく毎回拾い直す
    public static void WatchBodies() => _bodyWatchUntil = Time.time + 3f;

    private static void RenderWindow()
    {
        int n = Live.Count;
        if (n == 0) return;
        Handle pick = null;
        for (int i = 0; i < n; i++)
        {
            var h = Live[(_renderTurn + i) % n];
            if (h.Cam == null || !h.WinOn || h.Closing >= 0f) continue;
            pick = h;
            _renderTurn = (_renderTurn + i + 1) % n;
            break;
        }
        if (pick == null || !pick.Cam) return;
        if (--_bodyPoll <= 0 || Time.time < _bodyWatchUntil) { _bodyPoll = 15; FindBodies(); }
        BodyHidden.Clear();
        try
        {
            for (int i = 0; i < BodyArt.Count; i++)
            {
                var r = BodyArt[i];
                if (!r || !r.enabled) continue;
                r.enabled = false;
                BodyHidden.Add(r);
            }
            pick.Cam.Render();
        }
        finally
        {
            // 描くのに失敗しても死体は必ず戻す (戻し損ねると、元から消えていた絵と見分けられず二度と出ない)
            for (int i = 0; i < BodyHidden.Count; i++) if (BodyHidden[i]) BodyHidden[i].enabled = true;
            BodyHidden.Clear();
        }
    }

    // 死体の絵を拾い直す (死体は増えるのも消えるのもまれなので、窓を描く 15 回に 1 回だけ)
    private static void FindBodies()
    {
        BodyArt.Clear();
        foreach (var body in UnityEngine.Object.FindObjectsOfType<DeadBody>())
        {
            if (!body) continue;
            foreach (var r in body.GetComponentsInChildren<SpriteRenderer>(true)) if (r) BodyArt.Add(r);
        }
    }

    private static void ReleaseWindow(Handle h)
    {
        if (h.Cam != null)
        {
            if (h.Cam) { h.Cam.targetTexture = null; UnityEngine.Object.Destroy(h.Cam.gameObject); }
            h.Cam = null;
        }
        if (h.WinMat != null)
        {
            if (h.WinMat) UnityEngine.Object.Destroy(h.WinMat);
            h.WinMat = null;
        }
        if (h.Rt != null)
        {
            if (h.Rt) { h.Rt.Release(); UnityEngine.Object.Destroy(h.Rt); }
            h.Rt = null;
        }
        h.Win = null;
    }

    // 半径 1 の楕円 (板の大きさで縦横を決める)。芯はくっきり、縁の外側 1 割で透明へ溶かす。色はわずかに青緑へ寄せる
    private static void EnsureWindowMesh()
    {
        if (_windowMesh) return;
        const int seg = 32;
        const float inner = 0.88f;
        var verts = new Vector3[1 + seg * 2];
        var uvs = new Vector2[verts.Length];
        var cols = new Color[verts.Length];
        var tris = new int[seg * 9];
        Color core = FxMath.Rgba(0.85f, 1f, 1f, 0.95f), edge = FxMath.Rgba(0.6f, 1f, 1f, 0f);
        verts[0] = FxMath.V3(0f, 0f, 0f);
        uvs[0] = FxMath.V2(0.5f, 0.5f);
        cols[0] = core;
        for (int k = 0; k < seg; k++)
        {
            float a = k * 2f * FxMath.PI / seg;
            float cx = FxMath.Cos(a), cy = FxMath.Sin(a);
            verts[1 + k] = FxMath.V3(cx * inner, cy * inner, 0f);
            uvs[1 + k] = FxMath.V2(0.5f + 0.5f * cx * inner, 0.5f + 0.5f * cy * inner);
            cols[1 + k] = core;
            verts[1 + seg + k] = FxMath.V3(cx, cy, 0f);
            uvs[1 + seg + k] = FxMath.V2(0.5f + 0.5f * cx, 0.5f + 0.5f * cy);
            cols[1 + seg + k] = edge;
            int n = (k + 1) % seg, t = k * 9;
            tris[t] = 0; tris[t + 1] = 1 + n; tris[t + 2] = 1 + k;
            tris[t + 3] = 1 + k; tris[t + 4] = 1 + n; tris[t + 5] = 1 + seg + k;
            tris[t + 6] = 1 + n; tris[t + 7] = 1 + seg + n; tris[t + 8] = 1 + seg + k;
        }
        _windowMesh = new Mesh { name = "MrpPortalWindow" };
        _windowMesh.vertices = verts;
        _windowMesh.uv = uvs;
        _windowMesh.colors = cols;
        _windowMesh.triangles = tris;
        _windowMesh.RecalculateBounds();
        _windowMesh.hideFlags |= HideFlags.HideAndDontSave;
    }

    // 真ん中から縁へなめらかに薄れる丸
    private static void Glow(float u, float ang, out float value, out float alpha)
    {
        float q = FxMath.Clamp01(1f - u);
        value = 1f;
        alpha = q * q * (3f - 2f * q);
    }

    // 縁だけぼかした塗りつぶしの丸 (ポータルの奥の暗い穴)
    private static void Disc(float u, float ang, out float value, out float alpha)
    {
        float q = FxMath.Clamp01((1f - u) / 0.2f);
        value = 1f;
        alpha = q * q * (3f - 2f * q);
    }

    // 3 本の腕の渦と明るい縁。真ん中は芯の絵に任せて薄くする
    private static void Swirl(float u, float ang, out float value, out float alpha)
    {
        float arm = 0.5f + 0.5f * FxMath.Cos(3f * ang + 7.5f * u);
        arm = arm * arm * arm;
        float inner = FxMath.Clamp01((u - 0.08f) / 0.3f);
        float outer = 1f - FxMath.Clamp01((u - 0.84f) / 0.16f);
        float rimD = (u - 0.88f) / 0.07f;
        float rim = FxMath.Exp(-rimD * rimD);
        value = FxMath.Clamp01(0.55f + 0.45f * arm + 0.3f * rim);
        alpha = FxMath.Clamp01(arm * inner * outer * 0.95f + rim * 0.9f + 0.12f * inner * outer);
    }

    private delegate void Shade(float u, float ang, out float value, out float alpha);

    // 縦横の違う白い絵 (x, y は -1〜1)。ppu は幅に合わせる (幅 1・縦 h/w)
    private static unsafe Sprite MakeXY(int w, int h, Func<float, float, float> alpha)
    {
        var px = new byte[w * h * 4];
        float hx = (w - 1) * 0.5f, hy = (h - 1) * 0.5f;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = 255;
            px[i + 3] = (byte)(255f * FxMath.Clamp01(alpha((x - hx) / hx, (y - hy) / hy)));
        }
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "MrpPortalMachine", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        fixed (byte* b = px) tex.LoadRawTextureData((IntPtr)b, px.Length);
        tex.Apply(false, true);
        tex.hideFlags |= HideFlags.HideAndDontSave;
        var sp = UnityEngine.Sprite.Create(tex, new Rect(0, 0, w, h), FxMath.V2(0.5f, 0.5f), w, 0, SpriteMeshType.FullRect);
        sp.hideFlags |= HideFlags.HideAndDontSave;
        return sp;
    }

    private static unsafe Sprite Make(int n, Shade shade)
    {
        var px = new byte[n * n * 4];
        float c = (n - 1) * 0.5f, rad = n * 0.5f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float dx = x - c, dy = y - c;
            float u = FxMath.Sqrt(dx * dx + dy * dy) / rad;
            float value = 0f, alpha = 0f;
            if (u < 1f) shade(u, FxMath.Atan2(dy, dx), out value, out alpha);
            int i = (y * n + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = (byte)(255f * value);
            px[i + 3] = (byte)(255f * alpha);
        }
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "MrpPortal", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        fixed (byte* b = px) tex.LoadRawTextureData((IntPtr)b, px.Length);
        tex.Apply(false, true);
        tex.hideFlags |= HideFlags.HideAndDontSave;
        var sp = UnityEngine.Sprite.Create(tex, new Rect(0, 0, n, n), FxMath.V2(0.5f, 0.5f), n, 0, SpriteMeshType.FullRect);
        sp.hideFlags |= HideFlags.HideAndDontSave;
        return sp;
    }
}
