using System;
using System.Collections.Generic;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 爆発・打撃で船の小物と家具が動く (自分の絵を持つ物だけ。部屋の絵に描き込まれた物は動かない)。
// - 小物 (幅 SmallMax 以下で倒れる物でない) = 吹き飛んで回りながら床を滑り、壁で跳ね返る。止まった所にずっと残る。
// - 椅子・台・箱と中くらいの物 (MediumMax 以下) = 強く押されると、縦長の物は足元を軸に横倒しになって少し滑り、
//   横長の物は押されてずれながら少しねじれる。弱いと揺れる。
// - 物の上に載っている物 = その場で揺れるだけ。当たり判定のある家具は動かさない (歩ける所を変えない)。
//   表で揺れると決めた家具 (温室のプランター) だけは当たり判定ごと揺れる (動かないので歩ける所は戻る)。
// 全員の手元で同じ所に止まるように、水 (WaterSim) と同じく試合の刻みの Delay 遅れで整数だけで動かす (確定の状態 A)。
// 見せるのは A の写し (D) を今の刻みまで先に進めた物: 爆発と同時に吹き飛ぶ。結果が届くたびに A から今まで計算し直すので、
// 遅れて届いた客の手元でも A が追いついた時に D と同じになる。
// 位置は SolidMap の原点からの 1/1024 単位。壁は SolidMap の升 (壊した穴も通る)・通気口と端末の周りも通さない。
// 絵は 1 刻み前と今の間を GameClock.Frac で補間して、動いている物・揺れている物だけ毎フレーム書く。
// 外壁の穴 (Decompression) が開いている間は流れに引かれる: 小物と中くらいの物は気圧 30% 以上の間・重い物は口から 5 単位以内だけ (引かれている間は見た目だけ震える)。
// 口に着いたら、口より小さい物は宇宙へ消え、大きい物は口をふさぐ (確定側の刻みで Decompression へ知らせる)
internal static class PropSim
{
    private const int Delay = WaterSim.Delay;
    private const int Unit = 1024;
    private const int MaxStepsPerFrame = 20;
    private const float SmallMax = 0.7f, MediumMax = 1.6f;
    private const int MaxActive = 40;

    private const int Speed0 = 205;           // 爆心での初速 (6 単位/秒 = 1 刻み 0.2 単位)
    private const int StrikeSpeed = 100;      // 打撃の初速 (3 単位/秒)
    private const float BlastReach = 1.5f;    // 爆発の半径の何倍まで届くか
    private const float StrikeReach = 0.8f;   // 打撃の当たった点からの距離
    // 押し: 起点から向きの先の扇形 (根元の半幅 PushBase・広がり PushSpread/256 = 約 30°)
    private const int PushBase = 410;         // 0.4 単位 (1/1024)
    private const int PushSpread = 148;
    private const int Friction = 9;           // 1 刻みの速さの減り (8 単位/秒²)
    private const int Bounce = 2;             // 壁で跳ね返る速さ = Bounce / 5
    private const int SpinMin = 96, SpinMax = 192; // 回る速さ (1/16 度/刻み・180〜360 度/秒)
    private const int TipSpeed = 176;         // 倒れる速さ (1/16 度/刻み・11 度)
    private const int TipAt = 64;             // この強さ (/256) 以上で中くらいの物が倒れる
    private const int SlideMedium = 90;       // 倒れた物が滑る速さの割合 (/256)
    private const int SlideWide = 150;        // 横長の物 (倒さずに押してずらす) が滑る速さの割合 (/256)
    private const int TwistMin = 240, TwistMax = 400; // 横長の物が押されてねじれる角度 (1/16 度・15〜25 度)
    private const int FootR = 96;             // 足元の当たりの半径 (0.09 単位)
    private const float ObstacleR = 0.3f;     // 通気口・端末の周りの通さない半径
    private const int WobbleSteps = 24;       // 揺れが収まるまで (0.8 秒)
    private const int WobbleMax = 160;        // 揺れの最大 (1/16 度・10 度)
    private const int HeavySlide = 141;       // 部屋の絵から持ち上げた家具が滑る速さの割合 (/256・0.55 倍)
    private const int HeavyFriction = 4;      // 同じく 1 刻みの速さの減り (重いので一度動くと滑る)
    private const int HeavyTwist = 160;       // 同じくねじれる角度の最大 (1/16 度・10 度)
    private const float HeavyBox = 0.85f;     // 重い物の壁の当たり: 範囲の半分のこの割合の箱の 8 点
    // 流れ (外壁の穴): 速さ = Flow × 引く強さ × 気圧 × 種類の割合 (/256) へ 1 刻みに 1/PullEase ずつ寄る。引かれている間は床の摩擦なし
    private const int Flow = 100;             // 1 刻みの速さ (1/1024 単位・約 2.9 単位/秒) = 満タンの口の際で約 4.7 単位/秒
    private const int PullSmall = 256, PullMedium = 180, PullHeavy = 154;
    private const int PullEase = 4;
    private const int PullStart = 12;         // 止まっている物はこの速さ (0.35 単位/秒) 以上で動き出す
    private const int PullMinPress = Decompression.Full * 3 / 10; // 小物と中くらいの物は気圧 30% 以上で引かれる
    private const float RattleMove = 0.025f, RattleTurn = 1.2f; // 引かれている家具の震え (単位・度)
    private const int HeavyReach = 5;         // 重い物は口からこの道のり (単位) 以内だけ
    private const int ResyncPull = 15;        // 引いている間に見せる用を確定から計算し直す間隔 (0.5 秒)
    // ぶつかる音: 壁 (家具どうし) へ向かう速さがこれ以上の時だけ。同じ物は BumpGap 刻み空ける・1 フレーム BumpPerFrame まで
    private const int BumpMin = 40;           // 1.2 単位/秒
    private const int BumpFull = 200;
    private const int BumpGap = 12;
    private const int BumpPerFrame = 3;

    // 動かす物の絵の名前 (部屋の絵と別の絵を持つ小物と家具)
    private static readonly HashSet<string> Movable = new()
    {
        "pumpkin1", "pumpkin2", "pumpkin3", "pumpkin4", "pumpkin5", "candyBowl", "candle", "halls_spraybottle", "medbay_scale",
        "lounge_stool", "records_chair", "cockpit_chair", "security_chair", "shower_bench1", "shower_bench2", "showers_bench3",
        "rock1", "rock2", "rock3", "snowman", "crate", "croppedBoxes", "croppedBARRELS", "croppedWatercooler", "croppedCoffeetable",
        "obj_recRoom_floatieHead", "storagebox", "sample1", "sample2", "sample3", "sample4", "sample5",
        "vault_goldhat", "vault_purplejacket", "vault_shako", "vault_sword", "vault_gun", "vault_rubyglow",
    };

    // 倒れる物 (椅子・台・箱)。ほかは転がる小物
    private static readonly HashSet<string> Tippable = new()
    {
        "medbay_scale", "lounge_stool", "records_chair", "cockpit_chair", "security_chair", "shower_bench1", "shower_bench2", "showers_bench3",
        "snowman", "crate", "croppedBoxes", "croppedBARRELS", "croppedWatercooler", "croppedCoffeetable", "storagebox", "candle",
    };

    private enum Kind : byte { Small, Medium, Fixed, Heavy }

    // 動きの状態 (確定 A と見せる用 D で同じ形)
    private sealed class St
    {
        public int Px, Py, Vx, Vy, Spin;
        public int Ang, Tip;        // 回り (1/16 度)。Tip = 倒れる先 (0 = 倒れない)
        public int WobStart = int.MinValue, WobAmp;
        public bool Active, Moved;
        public bool Pulled;         // この刻みで流れに引かれている (摩擦なし)
        public bool Rattle;         // 流れに引かれて動いている (重い物を震わせる・見た目だけで刻みの計算には使わない)
        public bool Gone, Stuck;    // 口から宇宙へ消えた / 口に着いて止まった (大きい物はふさいでいる)
        public uint Rng;

        public void CopyFrom(St o)
        {
            Px = o.Px; Py = o.Py; Vx = o.Vx; Vy = o.Vy; Spin = o.Spin; Ang = o.Ang; Tip = o.Tip;
            WobStart = o.WobStart; WobAmp = o.WobAmp; Active = o.Active; Moved = o.Moved; Rng = o.Rng;
            Pulled = o.Pulled; Rattle = o.Rattle; Gone = o.Gone; Stuck = o.Stuck;
        }
    }

    private sealed class Prop
    {
        public Transform Tr;
        public Kind Kind;
        public string Name;
        public Vector3 T0;          // 元の transform の位置
        public float Rot0;          // 元の z の回転
        public Vector2 F0, Pivot0;  // 元の足元・回す軸 (ワールド)
        public bool Tall, Solid;
        public bool Sway;           // 当たり判定があっても揺らす (表の Wobble)
        public FurnitureLift.Lift Lift; // 重い物 (部屋の絵から持ち上げる家具)
        public Rect Bounds0;            // 重い物の元の当たり判定の範囲
        public Collider2D Col;          // 重い物の当たり判定 (上に載っている物を拾う)
        public int Hx, Hy, Rad;          // 重い物の壁の当たりの箱の半分・押される距離に足す半径 (1/1024)
        public List<Rider> Riders;       // 重い物の上に載っていて一緒に動く絵
        public float Z;
        public int SizeU;               // 長い辺 (1/1024)。口の幅より大きいと口をふさぐ
        public bool Hidden;             // 消えた絵を隠している
        public readonly St A = new(), D = new();
        public int Ox, Oy, OAng;    // D の 1 刻み前 (補間)
        public bool Path;           // 影の中の焼いた絵に写ったかもしれない範囲 (見せる用の足元が通った四角) を持っている
        public int Lx0, Ly0, Lx1, Ly1;
        public int Index;
        public int BumpStep = -BumpGap; // 見せる用で最後にぶつかる音を鳴らした刻み
    }

    // 重い物 (家具) の上に載っている絵。Prop = 動く物として数えている物 (自分が押されて動いたら家具から離れる)・
    // null = 飾りなど名前の一覧に無い絵 (家具と一緒に動くだけ)
    private sealed class Rider
    {
        public Transform Tr;
        public Vector3 T0;
        public float Rot0;
        public Prop Prop;
    }

    // 刻みを進める側 (確定 / 見せる用)。同じ手順を別の状態に掛ける
    private sealed class World
    {
        public bool Display;
        public int Step;
        public readonly List<Prop> Act = new(), Wob = new();
        public St S(Prop p) => Display ? p.D : p.A;
    }

    private struct Pending
    {
        public int Tick;
        public bool Blast;
        public bool Push;
        public int Force;           // 押しの強さ (0..256)
        public int Cx, Cy, Reach;   // 1/1024
        public int Dx, Dy;          // 打撃の向き (長さ 256)
        public float ReachF;
        public ushort Seed;
    }

    private static readonly List<Prop> Props = new();
    private static readonly List<Prop> Heavies = new(); // Props のうち重い物 (重い物どうしの当たり)
    private static readonly World Auth = new(), Disp = new() { Display = true };
    private static readonly List<Prop> Shown = new();
    // 止まった家具の水の型抜きの作り直し待ち (元の場所・止まった所・半径・時刻)
    private static readonly List<(Vector2 A, Vector2 B, float R, long Due)> Settled = new();
    private const long FurnitureSettleMs = 100;
    private static readonly List<Pending> Queue = new();
    private static byte[] _block;    // 通気口・端末の周り (SolidMap と同じ升)
    private static int _w, _h, _cellU;
    private static Vector2 _org;
    private static bool _ready, _running, _failed;
    private static int _shipGen;
    private static bool _resync;
    private static bool _hitHeavy;   // 直前の Blocked が別の重い物に当たった
    private static int _heardThrough = int.MinValue; // 見せる用のこの刻みまではぶつかる音を鳴らし済み (計算し直しで同じ所を鳴らさない)
    private static int _bumpsThisFrame;
    internal static int Bumps { get; private set; }
    private static (string Clip, Prop P, int Speed, float Vol, float Pitch, float X, float Y) _lastBump;
    private static string LastBump => _lastBump.Clip == null ? "-" : $"{_lastBump.Clip} {_lastBump.P.Name} speed={_lastBump.Speed} vol={_lastBump.Vol:0.00} pitch={_lastBump.Pitch:0.00} at {_lastBump.X:0.00},{_lastBump.Y:0.00}";
    internal static int Late { get; private set; }
    internal static int Kicks { get; private set; }
    internal static int Lost { get; private set; }     // 宇宙へ消えた数 (確定)
    internal static int Plugs { get; private set; }    // 口をふさいだ数 (確定)
    // 確定の刻みを進めている (Decompression はこちらに揃えて進む)
    internal static bool Drives => _running && !_failed;
    internal static int AuthStep => Auth.Step;

    // 確定を 1 刻み進める (TerrainStep から・減圧はこの刻みまで進んでいる)
    internal static void AdvanceAuth()
    {
        try
        {
            StepOnce(Auth);
            Auth.Step++;
            // 見せる用は今の流れで先回りしているので、引いている間は確定から時々計算し直す
            if (Decompression.Pulling && Auth.Step % ResyncPull == 0) _resync = true;
        }
        catch (Exception e) { Fail("step", e); }
    }

    // 船が替わったら片付ける (TerrainStep が刻みを進める前に呼ぶ)
    internal static void CheckShip()
    {
        if (GameClock.ShipGen != _shipGen) { _shipGen = GameClock.ShipGen; Reset(); _failed = false; }
    }

    // ── 入口 ───────────────────────────────────────────────────────────

    public static void OnApplied(in ResolvedDamage r)
    {
        try { Enqueue(r); }
        catch (Exception e) { Fail("apply", e); }
    }

    private static void Fail(string where, Exception e)
    {
        Plugin.Logger.LogError($"[PropSim] {where}: {e}");
        // 動かした物を元の所へ戻してこの船では止める (拾い直すと動いた後の位置を元の位置と取り違える)
        foreach (var p in Props)
        {
            try
            {
                if (p.Riders != null)
                    foreach (var q in p.Riders)
                        if (q.Tr) { q.Tr.position = q.T0; q.Tr.rotation = FxMath.RotZ(q.Rot0); }
                if (p.Hidden && p.Tr) p.Tr.gameObject.SetActive(true);
                if (p.Lift != null) FurnitureLift.Restore(p.Lift);
                else if (p.D.Moved || p.D.WobStart != int.MinValue) { p.Tr.position = p.T0; p.Tr.rotation = FxMath.RotZ(p.Rot0); }
            }
            catch { }
        }
        Reset();
        _failed = true;
    }

    private static void Enqueue(in ResolvedDamage r)
    {
        if (!Ensure()) return;
        int tick = GameClock.Expand(r.Tick);
        if (!_running) { _running = true; Auth.Step = Disp.Step = Math.Min(tick, GameClock.Now - Delay); }
        var p = new Pending { Tick = tick, Seed = r.Seed, Cx = ToU(r.Position.x - _org.x), Cy = ToU(r.Position.y - _org.y) };
        if (r.Kind == DamageKind.Explosion)
        {
            p.Blast = true;
            p.ReachF = r.Size * BlastReach;
        }
        else if (r.Kind == DamageKind.Push)
        {
            p.Push = true;
            p.ReachF = r.Size;
            p.Force = (int)MathF.Round(r.Force * 256f);
            p.Dx = (int)MathF.Round(r.Direction.x * 256f);
            p.Dy = (int)MathF.Round(r.Direction.y * 256f);
        }
        else
        {
            p.ReachF = StrikeReach;
            p.Dx = (int)MathF.Round(r.Direction.x * 256f);
            p.Dy = (int)MathF.Round(r.Direction.y * 256f);
        }
        p.Reach = ToU(p.ReachF);
        if (p.Tick < Auth.Step) { Late++; p.Tick = Auth.Step; }
        int i = Queue.Count;
        while (i > 0 && Later(Queue[i - 1], p)) i--;
        Queue.Insert(i, p);
        _resync = true;
    }

    // 刻みの順。同じ刻みは着いた順 (人ごとに違う) でなく種と位置で並べる
    // 試合の始めの準備 (TerrainWarm): 動く物と家具を先に集める (最初の破壊で払うと 1 フレームが長く止まる)
    internal static void Warm()
    {
        try { Ensure(); }
        catch (Exception e) { Fail("warm", e); }
    }

    private static bool Later(in Pending a, in Pending b) =>
        a.Tick != b.Tick ? a.Tick > b.Tick : a.Seed != b.Seed ? a.Seed > b.Seed : a.Cx != b.Cx ? a.Cx > b.Cx : a.Cy > b.Cy;

    private static int ToU(float v) => (int)MathF.Floor(v * Unit);

    // ── 船の物を集める (最初の破壊の時に 1 回) ───────────────────────────

    private static bool Ensure()
    {
        if (_ready) return true;
        if (_failed) return false;
        var ship = ShipStatus.Instance;
        if (!ship || !SolidMap.Ensure() || !SolidMap.Valid) return false;
        _w = SolidMap.W; _h = SolidMap.H; _org = SolidMap.Origin;
        _cellU = (int)MathF.Round(Unit / SolidMap.Ppu);
        _block = new byte[_w * _h];
        FurnitureSplit.Apply(ship); // 1 枚の絵にまとめて描かれた家具を家具ごとに分けてから集める
        var own = OwnFurniture(ship);
        foreach (var v in ship.AllVents) if (v) Stamp(v.transform.position);
        // 家具に載った端末は家具と一緒に動くので、周りを塞がない (塞ぐと家具が自分の端末に引っ掛かる)
        foreach (var c in ship.AllConsoles) if (c && !OnOwnFurniture(c.transform, own)) Stamp(c.transform.position);
        Props.Clear();
        Heavies.Clear();
        var decor = new List<SpriteRenderer>();
        foreach (var sr in ship.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (!sr.gameObject.activeInHierarchy || !sr.sprite) continue;
            if (own.TryGetValue(sr.gameObject.GetInstanceID(), out var ok))
            {
                var made = MakeOwn(sr, ok.Col, ok.Kind);
                if (made != null) Props.Add(made);
                continue;
            }
            if (OnOwnFurniture(sr.transform, own)) continue; // 家具の子 (端末など) は親と一緒に動く
            if (!Movable.Contains(sr.sprite.name)) { decor.Add(sr); continue; }
            var prop = Make(sr);
            if (prop != null) Props.Add(prop);
        }
        var lifts = new List<FurnitureLift.Lift>();
        FurnitureLift.Collect(ship, lifts);
        int plain = Props.Count;
        // 飾りの候補 (小さな絵) の真ん中を先に 1 回だけ引く
        var decorAt = new List<(SpriteRenderer, Vector2)>();
        if (lifts.Count > 0)
            foreach (var sr in decor)
            {
                var b = sr.bounds;
                if (b.size.x <= 1.2f && b.size.y <= 1.2f) decorAt.Add((sr, (Vector2)b.center));
            }
        foreach (var l in lifts) Props.Add(MakeHeavy(l));
        for (int i = 0; i < Props.Count; i++) Props[i].Index = i;
        // 家具 (重い物) の上に載っている絵 (動く物と飾り) は家具と一緒に動かす。
        // 当たり判定が重なった家具 (分けた箱の山の手前の箱と奥の台) では、先に並んだ家具 (手前) だけが載せる
        var claimed = new HashSet<int>();
        foreach (var h in Props)
        {
            if (h.Kind != Kind.Heavy || !h.Col) continue;
            foreach (var q in Props)
            {
                if (q == h || q.Kind == Kind.Heavy || q.Tr == null || !h.Col.OverlapPoint(q.T0)) continue;
                if (!claimed.Add(q.Tr.GetInstanceID())) continue;
                (h.Riders ??= new List<Rider>()).Add(new Rider { Tr = q.Tr, T0 = q.T0, Rot0 = q.Rot0, Prop = q });
            }
            foreach (var (sr, c) in decorAt)
                if (h.Col.OverlapPoint(c) && IsDecor(sr) && claimed.Add(sr.transform.GetInstanceID()))
                    (h.Riders ??= new List<Rider>()).Add(new Rider { Tr = sr.transform, T0 = sr.transform.position, Rot0 = sr.transform.eulerAngles.z });
        }
        Heavies.Clear();
        foreach (var h in Props) if (h.Kind == Kind.Heavy) Heavies.Add(h);
        _ready = true;
        return true;
    }

    private static void Stamp(Vector3 at)
    {
        int r = (int)MathF.Ceiling(ObstacleR * SolidMap.Ppu);
        int cx = (int)MathF.Floor((at.x - _org.x) * SolidMap.Ppu), cy = (int)MathF.Floor((at.y - _org.y) * SolidMap.Ppu);
        for (int y = cy - r; y <= cy + r; y++)
            for (int x = cx - r; x <= cx + r; x++)
            {
                if (x < 0 || y < 0 || x >= _w || y >= _h) continue;
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r) _block[y * _w + x] = 1;
            }
    }

    // 動かしてよい物か: 絵と当たり判定のほかに部品が無い・子に当たり判定や部品が無い (listed = 表に載った家具は端末などの子ごと動かす)
    private static Prop Make(SpriteRenderer sr, bool listed = false)
    {
        var go = sr.gameObject;
        bool solid = false;
        if (!listed) foreach (var c in go.GetComponentsInChildren<Component>(true))
        {
            string tn = c.GetIl2CppType().Name;
            if (tn == "Transform" || tn == "SpriteRenderer") continue;
            if (tn.EndsWith("Collider2D") && c.gameObject == go)
            {
                var col = c.TryCast<Collider2D>();
                if (col && !col.isTrigger) solid = true;
                continue;
            }
            return null;
        }
        var tr = sr.transform;
        var e = tr.eulerAngles;
        if (MathF.Abs(e.x) > 0.01f || MathF.Abs(e.y) > 0.01f) return null;
        var b = sr.bounds;
        Vector2 size = b.size;
        float big = Math.Max(size.x, size.y);
        var p = new Prop
        {
            Tr = tr, Name = sr.sprite.name, T0 = tr.position, Rot0 = e.z, Z = tr.position.z, Tall = size.y > size.x * 1.1f,
            F0 = FxMath.V2(b.center.x, b.min.y + Math.Min(0.15f, size.y * 0.2f)),
        };
        p.Solid = solid;
        p.SizeU = ToU(big);
        p.Kind = solid || big > MediumMax ? Kind.Fixed : Tippable.Contains(p.Name) || big > SmallMax ? Kind.Medium : Kind.Small;
        var a = p.A;
        a.Px = ToU(p.F0.x - _org.x);
        a.Py = ToU(p.F0.y - _org.y);
        // 壁際の物は足元が太らせた壁の線に掛かるので、近く (FreeSearch 升まで) の通れる所から動かし始める。
        // 見つからない物 (台の上に載っている物など) は揺れるだけ
        if (p.Kind != Kind.Fixed && Blocked(a.Px, a.Py))
        {
            if (FindFree(a.Px, a.Py, out int fx, out int fy))
            {
                a.Px = fx; a.Py = fy;
                p.F0 = FxMath.V2(_org.x + fx / (float)Unit, _org.y + fy / (float)Unit);
            }
            else p.Kind = Kind.Fixed;
        }
        // 小物は真ん中で回り、中くらいの物は足元を軸に倒れる
        p.Pivot0 = p.Kind == Kind.Small ? (Vector2)b.center : p.F0;
        p.D.CopyFrom(a);
        p.Ox = a.Px; p.Oy = a.Py;
        return p;
    }

    private const int FreeSearch = 5; // 0.31 単位

    private static bool FindFree(int x, int y, out int fx, out int fy)
    {
        for (int r = 1; r <= FreeSearch; r++)
            for (int a = 0; a < 8; a++)
            {
                fx = x + DirX[a] * r * _cellU / 256;
                fy = y + DirY[a] * r * _cellU / 256;
                if (!Blocked(fx, fy)) return true;
            }
        fx = x; fy = y;
        return false;
    }

    // 部屋の絵に描き込まれた家具: 範囲の真ん中を足元とし、壁の当たりは範囲を縮めた箱の 8 点で見る (始めから壁に掛かる時はさらに縮める)
    private static Prop MakeHeavy(FurnitureLift.Lift l)
    {
        Vector2 c = l.Bounds.center;
        var p = new Prop { Kind = Kind.Heavy, Name = l.Name, Lift = l, Col = l.Col, F0 = c, Pivot0 = c, T0 = FxMath.V3(c.x, c.y, 0f) };
        InitHeavy(p, l.Bounds);
        return p;
    }

    private static void InitHeavy(Prop p, Rect b)
    {
        p.Bounds0 = b;
        p.SizeU = ToU(Math.Max(b.width, b.height));
        p.Rad = ToU(Math.Min(b.width, b.height) * 0.4f);
        // 自分の絵を持つ家具が壁の線に半分埋まって置かれている時 (Polus の崖際の箱の山) は、真ん中が壁の向こうで全方向が塞がる。
        // 範囲のうち歩ける升の重心を足元に (囲む四角の真ん中は斜めの壁の線の上に来やすい)、絵を回す軸も同じ所へ移す
        // (絵は軸からのずれで置くので動く前の見た目は変わらない)
        if (!FitHeavy(p, b.center, b.size) && p.Lift == null && OpenPart(b, out int cx, out int cy, out var size))
        {
            // 足元は整数の升のまま使う (float へ戻して丸め直すと端末で 1 升ずれうる)
            if (FitHeavy(p, cx, cy, size)) p.F0 = p.Pivot0 = FxMath.V2(_org.x + cx / (float)Unit, _org.y + cy / (float)Unit);
            else FitHeavy(p, b.center, b.size); // 歩ける所でも塞がる時は元の範囲の一番小さい箱に戻す
        }
        p.D.CopyFrom(p.A);
        p.Ox = p.A.Px; p.Oy = p.A.Py;
    }

    // c を足元に、壁の当たりの箱 (size の半分の割合) を縮めながら塞がらない大きさを探す (見つからなければ一番小さい箱のまま false)
    private static bool FitHeavy(Prop p, Vector2 c, Vector2 size) => FitHeavy(p, ToU(c.x - _org.x), ToU(c.y - _org.y), size);

    private static bool FitHeavy(Prop p, int px, int py, Vector2 size)
    {
        var a = p.A;
        a.Px = px;
        a.Py = py;
        for (float k = HeavyBox; k > 0.2f; k -= 0.15f)
        {
            p.Hx = ToU(size.x * 0.5f * k);
            p.Hy = ToU(size.y * 0.5f * k);
            if (!Blocked(p, a.Px, a.Py)) return true;
        }
        return false;
    }

    // 範囲 b のうち歩ける升 (足の 4 点が塞がっていない所) の重心と、それを囲む四角の大きさ
    private static bool OpenPart(Rect b, out int cx, out int cy, out Vector2 size)
    {
        int x0 = ToU(b.xMin - _org.x), x1 = ToU(b.xMax - _org.x), y0 = ToU(b.yMin - _org.y), y1 = ToU(b.yMax - _org.y);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue, n = 0;
        long sx = 0, sy = 0;
        for (int y = y0; y <= y1; y += _cellU)
            for (int x = x0; x <= x1; x += _cellU)
            {
                if (Blocked(x, y)) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                sx += x; sy += y; n++;
            }
        cx = 0; cy = 0; size = default;
        if (n == 0) return false;
        cx = (int)(sx / n);
        cy = (int)(sy / n);
        size = FxMath.V2((maxX - minX) / (float)Unit, (maxY - minY) / (float)Unit);
        return true;
    }

    // 自分の絵と当たり判定 (層 12 か船の層 9) を持つ家具のうち、動かし方の表 (FurnitureKinds) に載っている物 (絵の GameObject → 種類と当たり判定)。
    // 載っている端末 (子) は家具と一緒に動く
    private static Dictionary<int, (FurnitureKind Kind, Collider2D Col)> OwnFurniture(ShipStatus ship)
    {
        var map = new Dictionary<int, (FurnitureKind, Collider2D)>();
        string shipName = FurnitureKinds.ShipName(ship);
        foreach (var col in ship.GetComponentsInChildren<Collider2D>(true))
        {
            if (!col || !col.enabled || col.isTrigger) continue;
            int layer = col.gameObject.layer;
            if (layer != 12 && layer != 9) continue;
            if (!FurnitureKinds.TryGet(col.transform, shipName, out var kind, out var owner)) continue;
            // 当たり判定が絵の子にある家具は絵の GameObject で引く (絵と同じ GameObject の当たり判定があればそちら)
            int id = owner.gameObject.GetInstanceID();
            if (owner == col.transform || !map.ContainsKey(id)) map[id] = (kind, col);
        }
        return map;
    }

    // tr が表の家具の子孫か (家具そのものは含めない)
    private static bool OnOwnFurniture(Transform tr, Dictionary<int, (FurnitureKind Kind, Collider2D Col)> own)
    {
        if (own.Count == 0) return false;
        for (var t = tr.parent; t; t = t.parent)
            if (own.ContainsKey(t.gameObject.GetInstanceID())) return true;
        return false;
    }

    // 表に載っている家具: 押してずらす物は重い物 (当たり判定の範囲の真ん中が足元)・倒れる物は縦長なら倒す。
    // 当たり判定は絵と同じ GameObject にあるので、絵を動かすと一緒に動く (揺れは当たり判定ごと回るので無し)
    private static Prop MakeOwn(SpriteRenderer sr, Collider2D col, FurnitureKind kind)
    {
        var p = Make(sr, true);
        if (p == null) return null;
        p.Solid = true;
        var b = col.bounds;
        var rect = Rect.MinMaxRect(b.min.x, b.min.y, b.max.x, b.max.y);
        if (kind == FurnitureKind.Wobble)
        {
            // 動かさずに絵の下の縁の真ん中を軸に揺れるだけ
            var sb = sr.bounds;
            p.Kind = Kind.Fixed;
            p.Sway = true;
            p.Pivot0 = FxMath.V2(sb.center.x, sb.min.y);
            p.Rad = ToU(Math.Max(b.size.x, b.size.y) * 0.5f); // 横に長いので爆発は縁から届く
        }
        else if (kind == FurnitureKind.Shove)
        {
            p.Kind = Kind.Heavy;
            p.Col = col;
            p.F0 = p.Pivot0 = rect.center;
            InitHeavy(p, rect);
        }
        else
        {
            // 倒れる物: 横に長い絵 (壁際の大きな棚など) は 90° 倒すと通路をふさぐので、ほかの横長の物と同じく滑ってねじれる
            Vector2 size = sr.bounds.size;
            p.Kind = Kind.Medium;
            p.Tall = size.x <= size.y * 1.5f;
            p.Pivot0 = p.F0;
        }
        return p;
    }

    // 家具の上の飾り (真ん中が家具の当たり判定の中の小さな絵) のうち、絵だけの物 (部屋の絵・本編の部品付きの物は除く)
    internal static bool IsDecor(SpriteRenderer sr)
    {
        var m = sr.sharedMaterial;
        if (m && m.shader && m.shader.name == "Unlit/MaskShader") return false;
        foreach (var c in sr.GetComponents<Component>())
        {
            string tn = c.GetIl2CppType().Name;
            if (tn != "Transform" && tn != "SpriteRenderer") return false;
        }
        return sr.transform.childCount == 0;
    }

    // 動かす時の当たり: 壁と、重い物どうし (当たりの箱のめり込みが今より深くなる動きだけ止める。
    // 1 枚の絵から分けた家具は始めから箱が重なっているので、重なり自体は許す)
    private static bool Blocked(Prop p, St st, World w, int x, int y)
    {
        if (Blocked(p, x, y)) return true;
        if (p.Kind != Kind.Heavy) return false;
        foreach (var q in Heavies)
        {
            if (q == p) continue;
            var qs = w.S(q);
            int pen = Pen(p, q, x - qs.Px, y - qs.Py);
            if (pen > 0 && pen > Pen(p, q, st.Px - qs.Px, st.Py - qs.Py)) { _hitHeavy = true; return true; }
        }
        return false;
    }

    // 2 つの重い物の当たりの箱のめり込みの深さ (重ならなければ 0)
    private static int Pen(Prop p, Prop q, int dx, int dy)
    {
        int ox = p.Hx + q.Hx - Math.Abs(dx), oy = p.Hy + q.Hy - Math.Abs(dy);
        return ox > 0 && oy > 0 ? Math.Min(ox, oy) : 0;
    }

    private static bool Blocked(Prop p, int x, int y)
    {
        if (p.Kind != Kind.Heavy) return Blocked(x, y);
        for (int i = 0; i < 8; i++)
        {
            int ox = i < 3 ? -p.Hx : i < 5 ? 0 : p.Hx;
            int oy = i == 0 || i == 3 || i == 5 ? -p.Hy : i == 1 || i == 6 ? 0 : p.Hy;
            if (Blocked(x + ox, y + oy)) return true;
        }
        return false;
    }

    private static bool Blocked(int x, int y)
    {
        for (int i = 0; i < 4; i++)
        {
            int sx = x + (i == 0 ? FootR : i == 1 ? -FootR : 0), sy = y + (i == 2 ? FootR : i == 3 ? -FootR : 0);
            if (sx < 0 || sy < 0) return true;
            int cx = sx / _cellU, cy = sy / _cellU;
            if (cx >= _w || cy >= _h) return true;
            int k = cy * _w + cx;
            if (!SolidMap.OpenCell(k) || _block[k] != 0 || Decompression.DoorAt(cx, cy)) return true;
        }
        return false;
    }

    // ── 刻み ───────────────────────────────────────────────────────────

    // 1 刻み: その刻みの破壊で押してから動かす。確定側は使った破壊を列から外す
    private static void StepOnce(World w)
    {
        if (w.Display)
        {
            for (int i = 0; i < Queue.Count && Queue[i].Tick <= w.Step; i++)
                if (Queue[i].Tick == w.Step) Kick(w, Queue[i]);
        }
        else
        {
            while (Queue.Count > 0 && Queue[0].Tick <= w.Step)
            {
                Kick(w, Queue[0]);
                Queue.RemoveAt(0);
            }
        }
        if (Decompression.Pulling) Pull(w);
        for (int i = w.Act.Count - 1; i >= 0; i--)
        {
            var p = w.Act[i];
            var st = w.S(p);
            if (w.Display) { p.Ox = st.Px; p.Oy = st.Py; p.OAng = st.Ang; Trace(p, st); }
            Move(p, st, w);
            if (w.Display) Trace(p, st);
            bool done = st.Vx == 0 && st.Vy == 0 && st.Spin == 0 && st.Ang == st.Tip;
            // 前後の z は部屋の絵を全部見るので 4 刻みに 1 回と止まった時だけ (見た目だけ)
            if (w.Display && p.Kind != Kind.Heavy && (done || (w.Step & 3) == 0)) p.Z = SortZ(_org.x + st.Px / (float)Unit, _org.y + st.Py / (float)Unit);
            if (!done) continue;
            st.Active = false;
            st.Rattle = false;
            w.Act.RemoveAt(i);
            if (w.Display)
            {
                p.Ox = st.Px; p.Oy = st.Py; p.OAng = st.Ang; Draw(p, 1f);
                if (p.Kind == Kind.Heavy)
                {
                    // 水の型抜きと、影の中の焼いた絵 (元の場所と止まった所) を作り直す (自分の絵を持つ家具は水の型抜きに入らないが同じ扱い)。
                    // 型抜きは当たり判定を引くので、動かした Transform が物理に写ってから (FurnitureSettleMs 後)
                    var b0 = p.Bounds0;
                    var now = FxMath.V2(_org.x + st.Px / (float)Unit, _org.y + st.Py / (float)Unit);
                    float r = Math.Max(b0.width, b0.height) * 0.5f + FurnitureLift.Pad;
                    Settled.Add((b0.center, now, r, Environment.TickCount64 + FurnitureSettleMs));
                    ShadowPatch.NoteMoved(p.Tr);
                    ShadowPatch.MarkDirtyLater(b0.center, r);
                    ShadowPatch.MarkDirtyLater(now, r);
                }
                else Rebake(p);
            }
        }
    }

    // 見せる用を確定の状態から今の刻みまで計算し直す (破壊の結果が届いた時)
    private static void Resync(int now)
    {
        Shown.Clear();
        Shown.AddRange(Disp.Act);
        Shown.AddRange(Disp.Wob);
        Disp.Act.Clear();
        Disp.Wob.Clear();
        Disp.Step = Auth.Step;
        foreach (var p in Props)
        {
            p.D.CopyFrom(p.A);
            p.Ox = p.A.Px; p.Oy = p.A.Py; p.OAng = p.A.Ang;
            if (p.D.WobStart != int.MinValue) Disp.Wob.Add(p);
        }
        Disp.Act.AddRange(Auth.Act);
        int n = 0;
        while (Disp.Step < now && n < Delay + MaxStepsPerFrame)
        {
            StepOnce(Disp);
            Disp.Step++;
            n++;
        }
        // 先回りで動かしていたが計算し直したら動いていない物は、今の状態の所へ置き直す
        foreach (var p in Shown)
            if (!Disp.Act.Contains(p) && !Disp.Wob.Contains(p)) { Draw(p, 1f); if (p.Kind != Kind.Heavy) Rebake(p); }
    }

    private static uint Next(St s)
    {
        uint x = s.Rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        s.Rng = x;
        return x;
    }

    // 押し: 扇形の中の物を向きへ押す (起点に近いほど強い)。倒れ方・回り方は Kick と同じ規則
    private static void PushOne(World w, Prop p, St st, int i, long dx, long dy, in Pending e)
    {
        long along = (dx * e.Dx + dy * e.Dy) / 256, across = Math.Abs(dx * e.Dy - dy * e.Dx) / 256;
        if (along < -p.Rad || along >= e.Reach + p.Rad) return;
        if (across > PushBase + Math.Max(0, along) * PushSpread / 256 + p.Rad) return;
        int f = 256 - (int)(Math.Max(0, along - p.Rad) * 256 / Math.Max(1, e.Reach));
        f = f * e.Force / 256;
        if (f <= 0) return;
        // 向き = 押す向き 3/4 + 起点から外へ 1/4 (扇の端の物は少し外へ逸れる)
        long d = (long)Math.Sqrt(dx * dx + dy * dy);
        int ux = e.Dx * 3, uy = e.Dy * 3;
        if (d >= 16) { ux += (int)(dx * 256 / d); uy += (int)(dy * 256 / d); }
        ux /= 4; uy /= 4;
        int ul = (int)Math.Sqrt((long)ux * ux + (long)uy * uy);
        if (ul == 0) { ux = e.Dx; uy = e.Dy; ul = 256; }
        st.Rng = 0x9E3779B9u ^ (uint)e.Seed * 2654435761u ^ (uint)i * 40503u;
        Next(st);
        Shove(w, p, st, f, ux, uy, ul, Speed0 * f / 256);
    }

    private static void Kick(World w, in Pending e)
    {
        for (int i = 0; i < Props.Count; i++)
        {
            var p = Props[i];
            var st = w.S(p);
            if (st.Gone) continue;
            long dx = st.Px - e.Cx, dy = st.Py - e.Cy;
            if (e.Push) { PushOne(w, p, st, i, dx, dy, e); continue; }
            long d2 = dx * dx + dy * dy;
            long lim = (long)e.Reach + p.Rad;
            if (d2 >= lim * lim) continue;
            int d = (int)Math.Sqrt(d2);
            int f = 256 - Math.Max(0, d - p.Rad) * 256 / Math.Max(1, e.Reach); // 強さ 0〜256 (近いほど強い・大きな家具は縁までの距離)
            st.Rng = 0x9E3779B9u ^ (uint)e.Seed * 2654435761u ^ (uint)i * 40503u;
            Next(st);
            // 向き (長さ 256): 爆発 = 爆心から外へ・打撃 = 振った向きと当たった点から外へを半々
            int ux, uy;
            if (d < 16) { int a = (int)(Next(st) % 8u); ux = DirX[a]; uy = DirY[a]; }
            else { ux = (int)(dx * 256 / d); uy = (int)(dy * 256 / d); }
            if (!e.Blast && (e.Dx != 0 || e.Dy != 0)) { ux = (ux + e.Dx) / 2; uy = (uy + e.Dy) / 2; }
            int ul = (int)Math.Sqrt((long)ux * ux + (long)uy * uy);
            if (ul == 0) { ux = 256; uy = 0; ul = 256; }
            int sp = (e.Blast ? Speed0 : StrikeSpeed) * f / 256;
            Shove(w, p, st, f, ux, uy, ul, sp);
        }
    }

    // 強さ f (0..256)・向き (ux, uy) (長さ ul)・初速 sp で物を押す。物の種類ごとに揺れる/倒れる/ずれる/回る
    private static void Shove(World w, Prop p, St st, int f, int ux, int uy, int ul, int sp)
    {
        if (!w.Display) Kicks++;
        if (p.Kind == Kind.Fixed || (p.Kind == Kind.Medium && f < TipAt))
        {
            Wobble(w, p, st, f, ux);
            return;
        }
        if (p.Kind == Kind.Heavy)
        {
            // 部屋の絵から持ち上げた家具は重い: 少しだけずれて少しねじれる (倒れない)
            sp = sp * HeavySlide / 256;
            int tw = (int)(Next(st) % (uint)(HeavyTwist + 1)) * f / 256;
            st.Tip += (Next(st) & 1) != 0 ? tw : -tw;
        }
        else if (p.Kind == Kind.Medium && p.Tall)
        {
            // 縦長の物 (椅子・ろうそく・樽) は押された向きへ足元を軸に倒れる
            sp = sp * SlideMedium / 256;
            if (st.Tip == 0) st.Tip = ux > 32 ? -1440 : ux < -32 ? 1440 : (Next(st) & 1) != 0 ? 1440 : -1440;
        }
        else if (p.Kind == Kind.Medium)
        {
            // 横長の物 (長椅子・台・箱) は倒さずに押してずらし、少しねじれる
            sp = sp * SlideWide / 256;
            int tw = (TwistMin + (int)(Next(st) % (uint)(TwistMax - TwistMin + 1))) * f / 256;
            st.Tip += (Next(st) & 1) != 0 ? tw : -tw;
        }
        else
        {
            int spin = SpinMin + (int)(Next(st) % (uint)(SpinMax - SpinMin + 1));
            spin = spin * f / 256;
            st.Spin = (Next(st) & 1) != 0 ? spin : -spin;
            st.Tip = int.MinValue; // 小物は回ったまま止まる
        }
        st.Vx += ux * sp / ul;
        st.Vy += uy * sp / ul;
        if (!st.Active)
        {
            if (w.Act.Count >= MaxActive) { Wobble(w, p, st, f, ux); return; }
            st.Active = true;
            w.Act.Add(p);
        }
        st.Moved = true;
    }

    private static readonly int[] DirX = { 256, 181, 0, -181, -256, -181, 0, 181 };
    private static readonly int[] DirY = { 0, 181, 256, 181, 0, -181, -256, -181 };

    // 流れに引く (Decompression の確定の刻みと揃っている時だけ全員で同じ)
    private static void Pull(World w)
    {
        for (int i = 0; i < Props.Count; i++)
        {
            var p = Props[i];
            if (p.Kind == Kind.Fixed) continue;
            var st = w.S(p);
            st.Pulled = false;
            st.Rattle = false;
            if (st.Gone || st.Stuck) continue;
            int sx = st.Px / _cellU, sy = st.Py / _cellU;
            if (!Decompression.FlowAt(sx, sy, out int dist, out int press, out int fx, out int fy)) continue;
            bool heavy = p.Kind == Kind.Heavy;
            if (heavy ? dist > HeavyReach * Decompression.UnitDist : press < PullMinPress) continue;
            // 口に着いた: 重い物は真ん中から縁までの分だけ手前で着く
            int reach = heavy ? (Math.Min(p.Hx, p.Hy) * Decompression.UnitDist + Unit / 2) / Unit : 0;
            if (dist <= reach && !ShortOfMouth(p, st, sx, sy)) { Arrive(w, p, st, sx, sy); continue; }
            int sp = (int)((long)Flow * Decompression.Strength1024(dist) / 1024 * press / Decompression.Full);
            sp = sp * (heavy ? PullHeavy : p.Kind == Kind.Medium ? PullMedium : PullSmall) / 256;
            if (sp <= 0 || (!st.Active && sp < PullStart)) continue;
            int fl = (int)Math.Sqrt((long)fx * fx + (long)fy * fy);
            if (fl == 0) continue;
            if (!st.Active)
            {
                if (w.Act.Count >= MaxActive) continue;
                st.Active = true;
                w.Act.Add(p);
            }
            if (st.Rng == 0) st.Rng = 0x9E3779B9u ^ (uint)i * 40503u;
            st.Vx += (fx * sp / fl - st.Vx) / PullEase;
            st.Vy += (fy * sp / fl - st.Vy) / PullEase;
            if (p.Kind == Kind.Small && st.Spin == 0)
            {
                int spin = SpinMin / 2 + (int)(Next(st) % (uint)(SpinMin / 2 + 1));
                st.Spin = (Next(st) & 1) != 0 ? spin : -spin;
                st.Tip = int.MinValue;
            }
            st.Pulled = true;
            st.Rattle = true;
            st.Moved = true;
        }
    }

    // 口より大きい物が、まだ口を覆えていない (口の線に触れて幅の大半を覆うまでは着いたことにせず、引かれ続けて壁に沿って口へ寄る)
    private static bool ShortOfMouth(Prop p, St st, int sx, int sy)
    {
        int b = Decompression.MouthOf(sx, sy, out int w256);
        if (b < 0 || (long)p.SizeU * 256 <= (long)w256 * Unit) return false;
        int hx = p.Kind == Kind.Heavy ? p.Hx : p.SizeU / 2, hy = p.Kind == Kind.Heavy ? p.Hy : p.SizeU / 2;
        return !Decompression.Covers(b, _org.x + st.Px / (float)Unit, _org.y + st.Py / (float)Unit, hx / (float)Unit, hy / (float)Unit);
    }

    // 口に着いた物: 口より小さい物は宇宙へ消え、大きい物は止まって口をふさぐ (重い物で口より小さい物は止まるだけ)
    private static void Arrive(World w, Prop p, St st, int sx, int sy)
    {
        int b = Decompression.MouthOf(sx, sy, out int w256);
        if (b < 0) return;
        st.Vx = 0; st.Vy = 0; st.Spin = 0;
        if (p.Kind == Kind.Small) st.Tip = st.Ang;
        if ((long)p.SizeU * 256 > (long)w256 * Unit)
        {
            st.Stuck = true;
            if (!w.Display) { Plugs++; Decompression.SealByProp(b); }
        }
        else if (p.Kind == Kind.Heavy) st.Stuck = true;
        else
        {
            st.Gone = true;
            if (!w.Display) Lost++;
        }
        st.Moved = true;
    }

    private static void Wobble(World w, Prop p, St st, int f, int ux)
    {
        if (p.Solid && !p.Sway) return; // 当たり判定ごと回ると歩ける所が揺れる
        st.WobStart = w.Step;
        int amp = WobbleMax * f / 256;
        st.WobAmp = ux >= 0 ? -amp : amp;
        if (w.Display && !w.Wob.Contains(p)) w.Wob.Add(p);
    }

    private static void Move(Prop p, St st, World w)
    {
        if (st.Vx != 0 || st.Vy != 0)
        {
            int n = Math.Max(Math.Abs(st.Vx), Math.Abs(st.Vy)) / (_cellU / 2) + 1;
            int sx = st.Vx / n, sy = st.Vy / n;
            for (int s = 0; s < n; s++)
            {
                int nx = st.Px + sx, ny = st.Py + sy;
                _hitHeavy = false;
                if (!Blocked(p, st, w, nx, ny)) { st.Px = nx; st.Py = ny; continue; }
                bool clash = _hitHeavy;
                if (sx != 0 && !Blocked(p, st, w, nx, st.Py)) { if (w.Display) Bump(p, st, w, Math.Abs(st.Vy), clash); st.Px = nx; st.Vy = -st.Vy * Bounce / 5; sy = -sy * Bounce / 5; st.Spin = -st.Spin; continue; }
                if (sy != 0 && !Blocked(p, st, w, st.Px, ny)) { if (w.Display) Bump(p, st, w, Math.Abs(st.Vx), clash); st.Py = ny; st.Vx = -st.Vx * Bounce / 5; sx = -sx * Bounce / 5; st.Spin = -st.Spin; continue; }
                if (w.Display) Bump(p, st, w, Math.Max(Math.Abs(st.Vx), Math.Abs(st.Vy)), clash);
                st.Vx = -st.Vx * Bounce / 5; st.Vy = -st.Vy * Bounce / 5; st.Spin = -st.Spin;
                break;
            }
            long v2 = (long)st.Vx * st.Vx + (long)st.Vy * st.Vy;
            int sp = (int)Math.Sqrt(v2);
            int fr = st.Pulled ? 0 : p.Kind == Kind.Heavy ? HeavyFriction : Friction;
            if (fr == 0) { }
            else if (sp <= fr) { st.Vx = 0; st.Vy = 0; st.Spin = 0; }
            else
            {
                int ns = sp - fr;
                st.Vx = st.Vx * ns / sp; st.Vy = st.Vy * ns / sp;
                st.Spin = st.Spin * ns / sp;
            }
        }
        else st.Spin = 0;
        if (p.Kind == Kind.Small)
        {
            st.Ang += st.Spin;
            if (st.Spin == 0) st.Tip = st.Ang;
        }
        else if (st.Ang != st.Tip)
        {
            int d = st.Tip - st.Ang;
            st.Ang += Math.Abs(d) <= TipSpeed ? d : Math.Sign(d) * TipSpeed;
        }
        st.Pulled = false; // 次の刻みも引かれるなら Pull がまた立てる (流れが止まったら摩擦が戻る)
    }

    // 見せる用で壁・別の家具に当たった音 (speed = 当たった面へ向かう速さ)。clip の番号と高さは刻みと物の番号で決め、St の乱数は使わない (確定とずれる)
    private static void Bump(Prop p, St st, World w, int speed, bool clash)
    {
        if (speed < BumpMin || w.Step <= _heardThrough || w.Step - p.BumpStep < BumpGap || _bumpsThisFrame >= BumpPerFrame) return;
        p.BumpStep = w.Step;
        _bumpsThisFrame++;
        int pick = (p.Index + w.Step) & 0xffff;
        float k = 0.35f + 0.65f * Math.Min(1f, (speed - BumpMin) / (float)(BumpFull - BumpMin));
        string clip;
        float vol, pitch = 0.94f + (pick % 5) * 0.03f;
        if (p.Kind == Kind.Small) { clip = SmallBumps[pick % SmallBumps.Length]; vol = 0.5f; }
        else if (p.Kind == Kind.Medium) { clip = HeavyBumps[pick % HeavyBumps.Length]; vol = 0.5f; pitch += 0.18f; }
        else if (clash) { clip = ClashBumps[pick % ClashBumps.Length]; vol = 0.9f; }
        else { clip = HeavyBumps[pick % HeavyBumps.Length]; vol = 0.85f; }
        float x = _org.x + st.Px / (float)Unit, y = _org.y + st.Py / (float)Unit;
        BreakNoise.PlayAt(clip, x, y, 18f, 8f, vol * k, pitch);
        Bumps++;
        _lastBump = (clip, p, speed, vol * k, pitch, x, y);
    }

    private static readonly string[] SmallBumps = { "noise_bump_small_1", "noise_bump_small_2", "noise_bump_small_3" };
    private static readonly string[] HeavyBumps = { "noise_bump_heavy_1", "noise_bump_heavy_2" };
    private static readonly string[] ClashBumps = { "noise_bump_clash_1", "noise_bump_clash_2" };

    // 見せる用の足元が通った所を覚える (影の中の焼いた絵は、焼いた時の姿のまま物を写している)
    private static void Trace(Prop p, St st)
    {
        if (p.Kind == Kind.Heavy) return;
        if (!p.Path) { p.Path = true; p.Lx0 = p.Lx1 = st.Px; p.Ly0 = p.Ly1 = st.Py; return; }
        if (st.Px < p.Lx0) p.Lx0 = st.Px; else if (st.Px > p.Lx1) p.Lx1 = st.Px;
        if (st.Py < p.Ly0) p.Ly0 = st.Py; else if (st.Py > p.Ly1) p.Ly1 = st.Py;
    }

    // 止まった小物の、動き始めから今までに通った所の焼いた絵を作り直す (元の位置や途中の姿が影の中に残らないように)。
    // 足元から絵の端までは長い辺より短いので、それだけ広げる
    private static void Rebake(Prop p)
    {
        Trace(p, p.D);
        float r = p.SizeU / (float)Unit + 0.1f;
        ShadowPatch.MarkBaked(_org.x + p.Lx0 / (float)Unit - r, _org.y + p.Ly0 / (float)Unit - r,
            _org.x + p.Lx1 / (float)Unit + r, _org.y + p.Ly1 / (float)Unit + r);
        p.Path = false;
    }

    // ── 絵 ─────────────────────────────────────────────────────────────

    // t = 1 刻み前 (0) から今 (1) の間
    private static void Draw(Prop p, float t)
    {
        if (p.Kind == Kind.Heavy) { DrawHeavy(p, t); return; }
        if (!p.Tr) return;
        var st = p.D;
        if (st.Gone != p.Hidden)
        {
            p.Hidden = st.Gone;
            p.Tr.gameObject.SetActive(!st.Gone);
        }
        if (st.Gone) return;
        float x = p.Ox + (st.Px - p.Ox) * t, y = p.Oy + (st.Py - p.Oy) * t;
        float ang = (p.OAng + (st.Ang - p.OAng) * t) / 16f;
        if (st.WobStart != int.MinValue)
        {
            float w = (Disp.Step - st.WobStart - 1 + t) / WobbleSteps;
            if (w < 1f) ang += st.WobAmp / 16f * (1f - w) * (1f - w) * FxMath.Sin(w * 3f * 2f * FxMath.PI);
        }
        float fx = _org.x + x / Unit, fy = _org.y + y / Unit;
        float qx = fx + (p.Pivot0.x - p.F0.x), qy = fy + (p.Pivot0.y - p.F0.y);
        var o = FxMath.RotateZ(ang, p.T0.x - p.Pivot0.x, p.T0.y - p.Pivot0.y);
        p.Tr.position = FxMath.V3(qx + o.x, qy + o.y, p.Z);
        p.Tr.rotation = FxMath.RotZ(p.Rot0 + ang);
    }

    // 部屋の絵から持ち上げた家具: 初めて動く時に切り抜き、絵と当たり判定を一緒に動かす
    private static void DrawHeavy(Prop p, float t)
    {
        var st = p.D;
        if (!st.Moved) return;
        if (p.Lift != null && !FurnitureLift.Ensure(p.Lift)) return;
        float x = p.Ox + (st.Px - p.Ox) * t, y = p.Oy + (st.Py - p.Oy) * t;
        float ang = (p.OAng + (st.Ang - p.OAng) * t) / 16f;
        float cx = _org.x + x / Unit, cy = _org.y + y / Unit;
        if (st.Rattle)
        {
            // 流れに引かれている家具はガタガタ震える (見た目だけ・確定の位置は変えない)
            float tt = (Environment.TickCount64 & 0xFFFFF) / 1000f + p.Pivot0.x;
            cx += FxMath.Sin(tt * 61f) * RattleMove;
            cy += FxMath.Sin(tt * 53f + 1.3f) * RattleMove;
            ang += FxMath.Sin(tt * 47f + 0.7f) * RattleTurn;
        }
        if (p.Lift != null) FurnitureLift.Place(p.Lift, p.Pivot0, cx, cy, ang);
        else if (p.Tr)
        {
            // 自分の絵を持つ家具: 当たり判定の範囲の真ん中を軸に絵を回して動かす (当たり判定も一緒に動く)
            var o = FxMath.RotateZ(ang, p.T0.x - p.Pivot0.x, p.T0.y - p.Pivot0.y);
            p.Tr.position = FxMath.V3(cx + o.x, cy + o.y, p.Z);
            p.Tr.rotation = FxMath.RotZ(p.Rot0 + ang);
        }
        if (p.Riders == null) return;
        foreach (var q in p.Riders)
        {
            if (!q.Tr || (q.Prop != null && (q.Prop.D.Moved || q.Prop.D.WobStart != int.MinValue))) continue;
            var o = FxMath.RotateZ(ang, q.T0.x - p.Pivot0.x, q.T0.y - p.Pivot0.y);
            q.Tr.position = FxMath.V3(cx + o.x, cy + o.y, q.T0.z);
            q.Tr.rotation = FxMath.RotZ(q.Rot0 + ang);
        }
    }

    // 動いた物はクルーと同じく足元の高さで前後を決める (部屋の絵がクルーと同じ奥行きの所はその手前)
    private static float SortZ(float x, float y)
    {
        float z = y / 1000f + 0.0005f;
        float front = DamageMap.FrontZ(FxMath.V2(x, y));
        return front < z ? front - 0.004f * DamageMap.ZScale(front) : z;
    }

    // ── 毎フレーム ─────────────────────────────────────────────────────

    public static void Tick()
    {
        try { TickCore(); }
        catch (Exception e) { Fail("tick", e); }
    }

    private static void TickCore()
    {
        if (GameClock.ShipGen != _shipGen) { _shipGen = GameClock.ShipGen; Reset(); _failed = false; }
        if (!_running) return;
        if (Settled.Count > 0)
        {
            long ms = Environment.TickCount64;
            for (int i = Settled.Count - 1; i >= 0; i--)
            {
                if (ms < Settled[i].Due) continue;
                WaterArt.FurnitureMoved(Settled[i].A, Settled[i].B, Settled[i].R);
                Settled.RemoveAt(i);
            }
        }
        int now = GameClock.Now;
        int n = 0;
        _bumpsThisFrame = 0;
        if (_heardThrough == int.MinValue) _heardThrough = now - 1; // 始めの追いつきの刻みでは鳴らさない
        // 確定の刻みは TerrainStep が減圧・水と揃えて進める (AdvanceAuth)
        if (_resync) { _resync = false; Resync(now); }
        else
        {
            n = 0;
            while (Disp.Step < now && n < MaxStepsPerFrame)
            {
                StepOnce(Disp);
                Disp.Step++;
                n++;
            }
        }
        _heardThrough = Disp.Step - 1;
        if (Disp.Act.Count == 0 && Disp.Wob.Count == 0) return;
        float t = Disp.Step < now ? 1f : GameClock.Frac;
        for (int i = 0; i < Disp.Act.Count; i++) Draw(Disp.Act[i], t);
        for (int i = Disp.Wob.Count - 1; i >= 0; i--)
        {
            var p = Disp.Wob[i];
            if (Disp.Step - p.D.WobStart > WobbleSteps + 1)
            {
                p.D.WobStart = int.MinValue;
                Disp.Wob.RemoveAt(i);
                Draw(p, 1f);
                if (!p.D.Active && p.Kind != Kind.Heavy) Rebake(p);
                continue;
            }
            if (!p.D.Active) Draw(p, t);
        }
    }

    internal static void Reset()
    {
        _ready = false;
        _running = false;
        Props.Clear();
        Heavies.Clear();
        FurnitureLift.Clear();
        Settled.Clear();
        Auth.Act.Clear(); Auth.Wob.Clear(); Auth.Step = 0;
        _heardThrough = int.MinValue;
        _lastBump = default;
        Disp.Act.Clear(); Disp.Wob.Clear(); Disp.Step = 0;
        Shown.Clear();
        _resync = false;
        Queue.Clear();
        _block = null;
        Late = 0;
        Kicks = 0;
        Lost = 0;
        Plugs = 0;
    }

    internal static uint Digest()
    {
        uint acc = 0;
        for (int i = 0; i < Props.Count; i++)
        {
            var a = Props[i].A;
            if (!a.Moved) continue;
            uint v = (uint)i * 0x9E3779B1u ^ (uint)a.Px * 0x85EBCA77u ^ (uint)a.Py * 0xC2B2AE3Du ^ (uint)a.Ang * 0x27D4EB2Fu
                ^ (a.Gone ? 0x165667B1u : 0u) ^ (a.Stuck ? 0xD3A2646Cu : 0u);
            v ^= v >> 15; v *= 0xC2B2AE3Du; v ^= v >> 13;
            acc += v;
        }
        return acc;
    }

    internal static void Register()
    {
        TestBridge.Register("props", "[list] 動く小物と家具: 数・動いている数・指紋 (list = 拾った物の名前・種類・足元)", (args, reply) =>
        {
            if (!_ready && !Ensure()) { reply("ERR props no ship"); return; }
            int s = 0, m = 0, f = 0, moved = 0, diff = 0;
            foreach (var p in Props)
            {
                if (p.Kind == Kind.Small) s++; else if (p.Kind == Kind.Medium) m++; else f++;
                if (p.A.Moved) moved++;
                if (p.A.Px != p.D.Px || p.A.Py != p.D.Py || p.A.Ang != p.D.Ang) diff++;
            }
            if (args.Trim() == "list")
                foreach (var p in Props)
                    reply($"PROP {p.Name} {p.Kind} foot={TestBridge.F(_org.x + p.A.Px / (float)Unit)},{TestBridge.F(_org.y + p.A.Py / (float)Unit)} ang={p.A.Ang / 16} moved={p.A.Moved} size={p.SizeU / (float)Unit:0.00}{(p.A.Gone ? " gone" : "")}{(p.A.Stuck ? " stuck" : "")}");
            reply($"OK props n={Props.Count} small={s} medium={m} fixed={f} moved={moved} active={Auth.Act.Count}/{Disp.Act.Count} wobbling={Disp.Wob.Count} kicks={Kicks} step={Auth.Step}/{Disp.Step} late={Late} shownDiff={diff} lost={Lost} plugs={Plugs} digest={Digest():x8} bumps={Bumps} lastBump={LastBump}");
        });
    }
}
