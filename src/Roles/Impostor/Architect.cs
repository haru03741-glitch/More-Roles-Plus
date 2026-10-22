using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// インポスター: 設計台を置き、その前で設計図 (歩ける所を上から見た地図) を開く。設計図をなぞると、なぞった線に沿って
// 床から壁がせり上がる (人に掛かれば横へ押し出す)。壁はゲージを使い、一定時間で崩れる。
// 水回りの部屋の壁の近くを軽く押すと、ゲージを多めに使って壁の中の配管を裂き、水を噴き出させる。
// 設計台は全員に見える。設計図を開いている間、本体は設計台の前で立ち止まる
public sealed class Architect : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#3C7FD8";
    public override Text Name => new("Architect.Name");
    public override string Reading => "こうちくし";
    public override Text Blurb => new("Architect.Blurb");

    public static readonly IntOpt DeskCount = new("Architect.DeskCount", 2, 1, 5);
    public static readonly FloatOpt DeskCooldown = new("Architect.DeskCooldown", 10f, 0f, 60f, 2.5f, "s");
    public static readonly FloatOpt GaugeLength = new("Architect.GaugeLength", 12f, 4f, 40f, 2f);
    public static readonly FloatOpt GaugeRefill = new("Architect.GaugeRefill", 30f, 5f, 120f, 5f, "s");
    public static readonly FloatOpt WallLife = new("Architect.WallLife", 15f, 5f, 60f, 2.5f, "s");
    public static readonly FloatOpt PipeCost = new("Architect.PipeCost", 4f, 1f, 12f, 1f);

    public override TerrainPermit TerrainPermit => TerrainPermit.None.WithWall(TerrainPermit.MaxWallLength, push: true).WithWater();

    private const float MinGap = 3f;          // 設計台どうしの間
    internal const float DeskBack = 0.5f;     // 設計台は置いた人の足元からこれだけ奥 (置いた人が台の手前に立つ)
    // 足元が設計台の床の点からこの範囲 (左右・手前・奥) にあれば設計図を開ける
    private const float UseHalfW = 0.75f, UseFront = 1.0f, UseBack = 0.15f;
    private const int CheckEvery = 5;         // 設計図のボタンを点けるかを見直す間隔 (FixedUpdate の回数・0.1 秒)
    // 壁は本人 (= 設計台の前) からこの距離まで (地形の依頼の届く距離から、立ち位置のずれの分を引く)
    internal const float Reach = TerrainSync.MaxBlastDistance - 0.5f;
    // なぞった線を壁に切る: 1 枚の長さの上限・線が直線からこれだけ外れたら折る・端どうしを重ねる長さ
    private const float ChunkMax = 2.4f, ChunkBend = 0.18f, Joint = 0.3f, ChunkMin = 0.6f;
    private const float BuildGap = 0.12f;     // 1 回の線の壁を 1 枚ずつ立てる間隔 (同じフレームに何枚も作らない)
    private const float FloorProbe = 0.125f;  // なぞった線が床の上か壁の中かを調べる間隔
    private const int HostEvery = 10;         // ホストが崩す壁を見直す間隔 (FixedUpdate の回数)
    private const float PipeSearch = 1.2f;    // 押した所からこの距離までの壁の面で配管を裂く
    private const float PipeStep = 0.0625f;
    private const int PipeDirs = 16;
    private const float PipeInto = 0.06f;     // 歩ける所の地図は壁を太らせてあるので、面の点を壁の線の側へ寄せる

    // 置いた (持ち主 → 全員)。受け手は送り主が構築士か・数と間隔の決まりを自分で確かめる
    private static readonly RemoteCall<Vector2> DeskCall = new("Architect.Desk", Route.Anyone,
        (w, v) => { w.Write(v.x); w.Write(v.y); }, r => FxMath.V2(r.ReadSingle(), r.ReadSingle()),
        (sender, v) => Of(sender)?.TryAdd(v.x, v.y));

    // 立った壁 (全員の端末で記録・崩すのはホスト)。ホストが替わっても新しいホストが続きから崩す
    private sealed class Built
    {
        public string Name;
        public Vector2 At;
        public float Length, Since;
        public byte Owner;
    }
    private static readonly List<Built> Walls = new();
    private static bool _listening;
    private static int _wallsGen = -1;

    private readonly List<ArchitectDeskArt.Handle> _desks = new();
    private Ability _plan;
    private int _check, _hostCheck;
    private float _gauge, _dt;
    private int _shownGauge = -1;
    // なぞり終えた線を立てる順番待ち (真ん中・向き・長さ)
    private readonly Queue<(Vector2 At, Vector2 Dir, float Len)> _queue = new();
    private float _nextBuild;
    private int _built, _refused, _pipes;
    private string _lastPipe = "-";

    internal int DeskTotal => _desks.Count;
    internal float Gauge => _gauge;
    internal float GaugeMax => GaugeLength;

    public override void OnAssigned()
    {
        ArchitectDeskArt.Prewarm();
        if (!_listening) { TrickWall.Placed += OnWallPlaced; _listening = true; }
        _gauge = GaugeLength;
        _dt = Time.fixedDeltaTime;
        AddAbility(new Ability(new Text("Architect.Desk"), "desk")
        {
            Cooldown = () => DeskCooldown,
            MaxUses = () => DeskCount,
            OnUse = PlaceHere,
        });
        _plan = AddAbility(new Ability(new Text("Architect.Plan"), "blueprint")
        {
            CooldownAtStart = false,
            OnUse = OpenPlan,
        });
        Lifespan.OnRelease(() =>
        {
            foreach (var d in _desks) ArchitectDeskArt.Remove(d);
            _desks.Clear();
            _queue.Clear();
            if (IsLocal) ArchitectPlan.Close();
        });
    }

    private static Architect Of(PlayerControl sender) => sender == null ? null : RoleState.Of(sender.PlayerId) as Architect;

    private static void OnWallPlaced(string name, Vector2 at, float length, byte actor)
    {
        ArchitectPlan.MarkDirty();
        if (_wallsGen != GameClock.ShipGen) { Walls.Clear(); _wallsGen = GameClock.ShipGen; }
        if (RoleState.Of(actor) is not Architect) return;
        Walls.Add(new Built { Name = name, At = at, Length = length, Since = Time.time, Owner = actor });
    }

    // 壊れた・崩れた (全員の端末で)。設計図を開いていれば焼き直す
    private void OnTerrainBroken(TerrainBrokenEvent e)
    {
        if (IsLocal) ArchitectPlan.MarkDirty();
    }

    private bool PlaceHere()
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.inVent || lp.onLadder || lp.inMovingPlat) return false;
        Vector2 me = lp.GetTruePosition();
        float x = me.x, y = me.y + DeskBack;
        if (Crew.PortalMaker.NearFixture(FxMath.V2(x, y)) || (SolidMap.Valid && SolidMap.Solid(me.x, me.y))) return false;
        if (!TryAdd(x, y)) return false;
        DeskCall.Send(FxMath.V2(x, y));
        return true;
    }

    private bool TryAdd(float x, float y)
    {
        if (_desks.Count >= DeskCount || !float.IsFinite(x) || !float.IsFinite(y)) return false;
        foreach (var d in _desks)
        {
            float dx = x - d.X, dy = y - d.Y;
            if (dx * dx + dy * dy < MinGap * MinGap) return false;
        }
        var h = ArchitectDeskArt.Place(x, y);
        if (h == null) return false;
        _desks.Add(h);
        return true;
    }

    // 自分の足元から使える設計台 (いちばん近い 1 つ)。無ければ null
    private ArchitectDeskArt.Handle DeskInReach(float px, float py)
    {
        ArchitectDeskArt.Handle best = null;
        float bestD = float.MaxValue;
        foreach (var d in _desks)
        {
            float dx = px - d.X, dy = py - d.Y;
            if (MathF.Abs(dx) > UseHalfW || dy > UseBack || dy < -UseFront) continue;
            float dd = dx * dx + dy * dy;
            if (dd < bestD) { bestD = dd; best = d; }
        }
        return best;
    }

    private bool CanPlan(PlayerControl lp)
    {
        if (!lp || lp.Data == null || lp.Data.IsDead || !lp.CanMove || lp.inVent || lp.onLadder || lp.inMovingPlat) return false;
        if (MeetingHud.Instance || ArchitectPlan.IsOpen) return false;
        Vector2 me = lp.GetTruePosition();
        return DeskInReach(me.x, me.y) != null;
    }

    private bool OpenPlan()
    {
        var lp = PlayerControl.LocalPlayer;
        if (!CanPlan(lp)) return false;
        return ArchitectPlan.Open(this);
    }

    // ---- ゲージと壁 (自分の端末) ----

    // なぞった長さの分だけゲージを使う。足りなければ使わずに false
    internal bool TrySpend(float length)
    {
        if (_gauge < length) return false;
        _gauge -= length;
        return true;
    }

    // なぞり終えた線 (世界の座標の点の列) を、床の上を通る区間ごとに壁へ切って順番待ちへ
    // (壁の中・机・端末・ベント・扉などに掛かる所には立てない)。
    // 使った長さとの差 (壁の中を通った分など) は戻す
    internal void Build(List<Vector2> pts, float spent)
    {
        float used = 0f;
        var run = new List<Vector2>();
        for (int i = 1; i < pts.Count; i++)
        {
            Vector2 a = pts[i - 1], b = pts[i];
            int n = Math.Max(1, (int)MathF.Ceiling(Dist(a, b) / FloorProbe));
            for (int k = i == 1 ? 0 : 1; k <= n; k++)
            {
                float t = k / (float)n, qx = a.x + (b.x - a.x) * t, qy = a.y + (b.y - a.y) * t;
                var q = FxMath.V2(qx, qy);
                if (TrickWall.FreeForPushWall(q)) run.Add(q);
                else { used += Chunk(run); run.Clear(); }
            }
        }
        used += Chunk(run);
        // 端の重なりと最短の長さの分は払わない。払いすぎた分は戻す
        float back = spent - Math.Min(spent, used);
        _gauge = Math.Min(GaugeLength, _gauge + back);
    }

    // 床の上の 1 続きの点の列を、直線に近い区間ごとの壁に切る。切った長さの合計
    private float Chunk(List<Vector2> pts)
    {
        if (pts.Count < 2) return 0f;
        float used = 0f;
        int s = 0;
        for (int i = 2; i < pts.Count; i++)
        {
            if (Dist(pts[s], pts[i]) <= ChunkMax && MaxBend(pts, s, i) <= ChunkBend) continue;
            used += Enqueue(pts[s], pts[i - 1]);
            s = i - 1;
        }
        return used + Enqueue(pts[s], pts[^1]);
    }

    private float Enqueue(Vector2 a, Vector2 b)
    {
        float len = Dist(a, b);
        if (len < 0.05f) return 0f;
        float dx = (b.x - a.x) / len, dy = (b.y - a.y) / len;
        float wall = Math.Clamp(len + Joint, ChunkMin, TerrainPermit.MaxWallLength);
        _queue.Enqueue((FxMath.V2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f), FxMath.V2(dx, dy), wall));
        return len;
    }

    private static float Dist(Vector2 a, Vector2 b)
    {
        float dx = b.x - a.x, dy = b.y - a.y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // 点 s から e の直線から、間の点がいちばん離れている距離
    private static float MaxBend(List<Vector2> pts, int s, int e)
    {
        Vector2 a = pts[s], b = pts[e];
        float dx = b.x - a.x, dy = b.y - a.y, len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-4f) return 0f;
        float worst = 0f;
        for (int k = s + 1; k < e; k++)
            worst = MathF.Max(worst, MathF.Abs((pts[k].x - a.x) * dy - (pts[k].y - a.y) * dx) / len);
        return worst;
    }

    // 設計図を軽く押した (世界の座標)。水回りの部屋の中で、近くの壁の面の配管を裂く。裂けたら true
    internal bool Burst(Vector2 at)
    {
        float cost = PipeCost;
        if (_gauge < cost || !SolidMap.Valid || SolidMap.Solid(at.x, at.y) || !WaterLeak.IsWaterRoom(at)) { _lastPipe = "not here"; return false; }
        // いちばん近い壁の面 (歩ける所の地図の縁) を 16 方向に探す
        float best = float.MaxValue, bx = 0f, by = 0f;
        for (int k = 0; k < PipeDirs; k++)
        {
            float a = k * MathF.PI * 2f / PipeDirs, dx = MathF.Cos(a), dy = MathF.Sin(a);
            for (float d = PipeStep; d <= PipeSearch && d < best; d += PipeStep)
            {
                if (!SolidMap.Solid(at.x + dx * d, at.y + dy * d)) continue;
                best = d; bx = dx; by = dy;
                break;
            }
        }
        if (best == float.MaxValue) { _lastPipe = "no wall near"; return false; }
        var face = FxMath.V2(at.x + bx * (best + PipeInto), at.y + by * (best + PipeInto));
        var res = TerrainApi.BurstPipe(face, FxMath.V2(-bx, -by));
        _lastPipe = res.Ok ? $"burst ({face.x:0.00},{face.y:0.00})" : $"refused {res.Why}";
        if (!res.Ok) return false;
        _gauge -= cost;
        _pipes++;
        ArchitectPlan.MarkDirty();
        return true;
    }

    private void BuildNext()
    {
        if (_queue.Count == 0 || Time.time < _nextBuild) return;
        var w = _queue.Dequeue();
        _nextBuild = Time.time + BuildGap;
        var res = TerrainApi.PushWall(w.At, w.Dir, w.Len);
        if (res.Ok) { _built++; return; }
        _refused++;
        _gauge = Math.Min(GaugeLength, _gauge + Math.Max(0f, w.Len - Joint));
        Plugin.Logger.LogInfo($"Architect: wall refused {res.Why}");
    }

    public override void OnFixedUpdate()
    {
        if (IsLocal) LocalTick();
        if (++_hostCheck >= HostEvery)
        {
            _hostCheck = 0;
            if (AmongUsClient.Instance && AmongUsClient.Instance.AmHost) CrumbleOld();
        }
    }

    private void LocalTick()
    {
        ArchitectPlan.Tick();
        BuildNext();
        float max = GaugeLength;
        if (_gauge < max) _gauge = MathF.Min(max, _gauge + max / MathF.Max(1f, GaugeRefill) * _dt);
        if (++_check < CheckEvery) return;
        _check = 0;
        var b = _plan?.Button;
        if (b == null) return;
        b.SetUsable(CanPlan(PlayerControl.LocalPlayer));
        int shown = (int)_gauge;
        if (shown != _shownGauge) { _shownGauge = shown; b.SetUses(shown); }
    }

    // ホスト: この構築士の壁で、立ってから時間が経った物を崩す (先に壊れていれば崩す依頼は何もしない)
    private void CrumbleOld()
    {
        if (_wallsGen != GameClock.ShipGen || Walls.Count == 0 || MeetingHud.Instance) return;
        float now = Time.time, life = WallLife;
        for (int i = Walls.Count - 1; i >= 0; i--)
        {
            var w = Walls[i];
            if (w.Owner != PlayerId || now - w.Since < life) continue;
            // 先に壊れた壁は忘れる。崩れの依頼は真ん中の点の物に当たるので、別の壁が真ん中に重なっている間は次の周期に回す
            if (!BreakableProps.Intact(w.Name)) { Walls.RemoveAt(i); continue; }
            if (BreakableProps.IntactNameAt(w.At) != w.Name) continue;
            Walls.RemoveAt(i);
            var res = TerrainApi.WorldCrumble(w.At, w.Length);
            if (!res.Ok) Plugin.Logger.LogInfo($"Architect: crumble skipped {res.Why}");
        }
    }

    internal string Describe()
    {
        var sb = new System.Text.StringBuilder($"desks={_desks.Count}/{(int)DeskCount} gauge={_gauge:0.0}/{(float)GaugeLength:0} queue={_queue.Count} built={_built} refused={_refused} walls={Walls.Count} pipes={_pipes} pipe={_lastPipe} plan={ArchitectPlan.Describe()}");
        foreach (var d in _desks) sb.Append($" ({d.X:0.00},{d.Y:0.00})");
        return sb.ToString();
    }

    // テスト用: 自分の端末で (x, y) に置いた扱いにして全員へ配る
    internal bool PlaceForTest(float x, float y)
    {
        if (!TryAdd(x, y)) return false;
        DeskCall.Send(FxMath.V2(x, y));
        return true;
    }

    // テスト用: 自分の足元を設計台 i の手前へ移す
    internal bool GoForTest(int i)
    {
        if (i < 0 || i >= _desks.Count) return false;
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return false;
        Vector2 me = lp.GetTruePosition();
        var root = lp.transform.position;
        Teleport.Self(FxMath.V2(_desks[i].X + (root.x - me.x), _desks[i].Y - DeskBack + (root.y - me.y)));
        return true;
    }

    internal bool OpenForTest() => OpenPlan();

    // テスト用: 世界の座標の点の列をなぞった扱いにする (ゲージも使う)。使えた長さ
    internal float DrawForTest(List<Vector2> pts)
    {
        var kept = new List<Vector2> { pts[0] };
        float spent = 0f;
        for (int i = 1; i < pts.Count; i++)
        {
            float len = Dist(kept[^1], pts[i]);
            if (!TrySpend(len)) break;
            spent += len;
            kept.Add(pts[i]);
        }
        if (kept.Count >= 2) Build(kept, spent);
        return spent;
    }
}
