using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Roles;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 鉄板: アルケミストが足元の近くの瓦礫 1 つを錬金して、いちばん近い壁の穴をふさぐ仕掛け壁。
// 立てるのは仕掛け壁と同じ地形の出来事 (DamageKind.Wall) で、配る結果の瓦礫の欄の先頭に印 (Mark) と使った瓦礫の位置を載せる。
// 穴の一覧は壁が抜けた時に全員の端末で積み (時戻しで取り消した件の穴は外す)、ふさがっているかは生きている鉄板で見る。
// 外壁の穴の口を覆えば吸い出しも止まり (減圧の刻みに積む)、壊れると口が開き直す。耐久は強い振り 1 発・刀で斬れる
internal static class IronPlate
{
    public const byte Mark = 0xFE;             // 配る結果の瓦礫の欄の先頭の Rank がこれなら鉄板
    internal const float RubbleReach = 1.5f;   // 足元からこの距離以内の瓦礫を使う
    internal const float HoleReach = 3f;       // 足元からこの距離以内の穴をふさぐ
    private const float CoverNear = 0.9f;      // 穴の点からこの距離以内に向きの揃った鉄板があればふさがっている
    private const float CoverDot = 0.8f;
    private const float MinFace = 0.3f;        // これより短い切り口は穴に数えない
    private const float BodyLift = 0.4f;       // 刀の当たりの中心を足元から上げる高さ (板の面の中ほど)
    private const float BodyRadiusMax = 0.9f;

    private readonly struct Hole
    {
        public readonly Vector2 At, Along;
        public Hole(Vector2 at, Vector2 along) { At = at; Along = along; }
    }

    private sealed class Plate
    {
        public string Name;
        public Vector2 C, T;
        public float Len;
        public Body Body;
        public bool Broken;
    }

    private static readonly List<Hole> Holes = new();
    private static readonly List<Plate> Plates = new();
    private static int _gen = -1;

    internal static readonly BodyKind PlateKind = new("IronPlate", BodyMaterial.Metal, BodySize.Medium, 1f);

    private static void Check()
    {
        if (_gen == GameClock.ShipGen) return;
        _gen = GameClock.ShipGen;
        Holes.Clear();
        foreach (var p in Plates) Bodies.Remove(p.Body);
        Plates.Clear();
    }

    internal static bool IsPlate(in ResolvedDamage r) =>
        r.Kind == DamageKind.Wall && r.Landings is { Length: > 0 } l && l[0].Rank == Mark;

    // ---- 穴の一覧 (全員・適用の中で) ----

    // 打撃で抜けた穴: 抜いた形の真ん中と元の壁の向き
    internal static void NoteHole(Vector2 center, Vector2 along, float width)
    {
        if (width < MinFace) return;
        Add(new Hole(center, along));
    }

    // 爆発で抜けた穴: 切り取った壁の区間 (2 点ずつ) ごとに、その真ん中と向き
    internal static void NoteFaces(List<Vector2> removed)
    {
        for (int i = 0; i + 1 < removed.Count; i += 2)
        {
            Vector2 a = removed[i], b = removed[i + 1], d = b - a;
            float len = d.magnitude;
            if (len < MinFace) continue;
            Add(new Hole((a + b) * 0.5f, d / len));
        }
    }

    private static void Add(Hole h)
    {
        Check();
        Holes.Add(h);
        TerrainUndo.OnUndo(() => { int k = Holes.LastIndexOf(h); if (k >= 0) Holes.RemoveAt(k); });
    }

    private static bool Covered(Vector2 at, Vector2 along)
    {
        foreach (var p in Plates)
        {
            if (p.Broken) continue;
            if (MathF.Abs(p.T.x * along.x + p.T.y * along.y) < CoverDot) continue;
            // 穴の点が板の長さの範囲の横 (壁の厚みほどの距離まで) にあれば覆われている。板の端の先の穴は別の穴
            float dx = at.x - p.C.x, dy = at.y - p.C.y;
            float u = dx * p.T.x + dy * p.T.y, v = -dx * p.T.y + dy * p.T.x;
            if (MathF.Abs(u) <= p.Len * 0.5f && MathF.Abs(v) <= CoverNear) return true;
        }
        return false;
    }

    // ---- 置き場所 (ホストの決定と手元の確かめで同じ) ----

    private static readonly float[] MouthShift = { 0.1f, 0f, 0.2f, -0.1f };       // 口の線から宇宙の側へ
    private static readonly float[] HoleShift = { 0f, 0.15f, -0.15f, 0.3f, -0.3f }; // 穴の点から壁の厚みの向きへ

    // from の近くの、ふさげる穴に置く鉄板の形。外壁の開いた口を先に (吸い出しを止める)、無ければ近い穴から
    internal static bool FindSpot(Vector2 from, out Vector2 c, out Vector2 t, out float len, out string why)
    {
        c = t = default;
        len = 0f;
        why = "no hole near";
        Check();
        foreach (var (mid, along, away) in Decompression.OpenMouthsNear(from, HoleReach))
            foreach (float s in MouthShift)
                if (TrickWall.FitPlate(mid + away * s, along, out c, out t, out len, out why)) return true;
        var order = new List<(float D, int I)>();
        for (int i = 0; i < Holes.Count; i++)
        {
            float d = (Holes[i].At - from).sqrMagnitude;
            if (d <= HoleReach * HoleReach) order.Add((d, i));
        }
        order.Sort((x, y) => x.D.CompareTo(y.D));
        foreach (var (_, i) in order)
        {
            var h = Holes[i];
            if (Covered(h.At, h.Along)) continue;
            var n = new Vector2(-h.Along.y, h.Along.x);
            foreach (float s in HoleShift)
            {
                if (!TrickWall.FitPlate(h.At + n * s, h.Along, out c, out t, out len, out why)) continue;
                // 外壁の穴の外側 (宇宙へ抜けた喉の中の切り口) には立てない。外壁は上の口の道でふさぐ
                if (SolidMap.NearOutside(c.x, c.y)) { why = "outside"; continue; }
                return true;
            }
        }
        return false;
    }

    // 手元の確かめ (ボタンを押した時): 使える瓦礫とふさげる穴があるか
    internal static string Why(Vector2 from)
    {
        if (RubbleBlocks.NearestFree(from.x, from.y, RubbleReach) == null) return "no rubble";
        return FindSpot(from, out _, out _, out _, out string why) ? null : why;
    }

    // ホスト: 鉄板の依頼 (起点 = 頼んだ人の足元) を仕掛け壁の結果に決める
    internal static bool TryResolve(in DamageEvent e, out ResolvedDamage r, out string why)
    {
        r = default;
        var rubble = RubbleBlocks.NearestFree(e.Position.x, e.Position.y, RubbleReach);
        if (rubble == null) { why = "plate no rubble"; return false; }
        if (!FindSpot(e.Position, out var c, out var t, out float len, out why)) { why = "plate " + why; return false; }
        var used = new RubbleLanding(Mark, TerrainWire.Q(new Vector2(rubble.X, rubble.Y)), 0f);
        r = new ResolvedDamage(DamageKind.Wall, c, Vector2.zero, t, 0f, len, 0, e.Seed, new[] { used });
        return true;
    }

    // ---- 立った・壊れた (全員・適用の中で) ----

    // 鉄板が立った: 瓦礫を使い切り、外壁の口を覆っていればふさぎ、刀の当たりを置く。返すのはログの欄
    internal static string OnPlaced(string name, Transform root, Vector2 c, Vector2 t, float len, in ResolvedDamage r)
    {
        Check();
        string used = " rubble=missing";
        var at = r.Landings[0].Position;
        var b = RubbleBlocks.FreeAt(at.x, at.y);
        if (b != null)
        {
            RubbleBlocks.Consume(b);
            DustCloud.Kick(FxMath.V2(b.X, b.Y), 0.45f, (ushort)(r.Seed + 7)); // 瓦礫が錬金されて崩れ消える
            used = $" rubble=({b.X:0.00},{b.Y:0.00})";
        }
        var p = new Plate { Name = name, C = c, T = t, Len = len };
        p.Body = AddBody(p);
        Plates.Add(p);
        TerrainUndo.OnUndo(() => { Plates.Remove(p); Bodies.Remove(p.Body); });
        // 外壁の口と比べる箱 (傾いた板を囲む四角の半分)
        float hx = MathF.Abs(t.x) * len * 0.5f + MathF.Abs(t.y) * TrickWall.Thickness * 0.5f;
        float hy = MathF.Abs(t.y) * len * 0.5f + MathF.Abs(t.x) * TrickWall.Thickness * 0.5f;
        Decompression.OnPlate(TerrainSync.ApplyingTick, name, c, FxMath.V2(hx, hy));
        AlchemyFx(c, t, len, r.Seed);
        return used;
    }

    // 点 p (崩れの依頼の起点) が鉄板の真ん中か (壊れた後も覚えている)
    internal static bool IsPlateAt(Vector2 p)
    {
        Check();
        foreach (var pl in Plates)
            if ((pl.C - p).sqrMagnitude <= 0.3f * 0.3f) return true;
        return false;
    }

    // 鉄板が壊れた (叩かれた・爆発・崩れ・斬られた)。ふさいでいた口を開け直し、刀の当たりを外す
    internal static void OnBroken(string name)
    {
        Check();
        foreach (var p in Plates)
        {
            if (p.Broken || p.Name != name) continue;
            p.Broken = true;
            Bodies.Remove(p.Body);
            TerrainUndo.OnUndo(() => { p.Broken = false; if (!Bodies.Contains(p.Body)) Bodies.Add(p.Body); });
            Decompression.OnPlateBroken(TerrainSync.ApplyingTick, name, TerrainSync.ApplyingActor);
            return;
        }
    }

    private static Body AddBody(Plate p) => Bodies.Add(new Body
    {
        Kind = PlateKind,
        Fixed = true,
        X = p.C.x,
        Y = p.C.y + BodyLift,
        Radius = MathF.Min(p.Len * 0.5f, BodyRadiusMax),
        Ignore = Bodies.AllBut(StrikeElement.Slash), // 叩く・爆発・弾・火は地形の側で当たる
        Judges = () => true,
        OnStrike = (b, res, s) => OnStrike(p, b, res, s),
    });

    private static bool OnStrike(Plate p, Body b, StrikeResult res, Strike s)
    {
        var by = PortKit.ById(s.By);
        bool samurai = by ? RoleState.Of(by) is Roles.Neutral.Samurai : Bodies.FromTest(null);
        if (res == StrikeResult.Glance || (res == StrikeResult.Cut && !samurai))
        {
            Bodies.Spark(b.X, b.Y, s);
            return true;
        }
        if (res != StrikeResult.Cut && res != StrikeResult.Break) return false;
        Bodies.Spark(b.X, b.Y, s, false);
        if (Bodies.Reports(s)) Roles.Crew.Alchemist.ReportCut(p.C, s.By);
        return true;
    }

    // ホスト: 刀で斬られた鉄板を崩す (斬った人が届く所にいるかは呼ぶ側が見る)。at = 鉄板の真ん中
    internal static bool HostCut(Vector2 at, byte by)
    {
        Check();
        foreach (var p in Plates)
        {
            if (p.Broken || (p.C - at).sqrMagnitude > 0.25f * 0.25f) continue;
            string res = TerrainSync.RequestAs(new DamageEvent(DamageKind.Crumble, p.C, Vector2.up, p.Len, 0f, (ushort)UnityEngine.Random.Range(0, 65536)), by, out bool ok);
            Plugin.Logger.LogInfo($"[IronPlate] cut {p.Name} by {by}: {res}");
            return ok;
        }
        return false;
    }

    // 立つ時の見た目: 板の面に沿って細かい粉が舞う (錬金で金属が寄り集まる)
    private static void AlchemyFx(Vector2 c, Vector2 t, float len, ushort seed)
    {
        int n = Math.Max(2, (int)(len / 0.8f));
        for (int i = 0; i < n; i++)
        {
            float u = ((i + 0.5f) / n - 0.5f) * len;
            DustCloud.Kick(c + t * u, 0.35f, (ushort)(seed + 11 + i));
        }
    }

    internal static string Describe(Vector2 from)
    {
        Check();
        var sb = new System.Text.StringBuilder();
        foreach (var (mid, along, away) in Decompression.OpenMouthsNear(from, HoleReach))
        {
            sb.Append($" m({mid.x:0.00},{mid.y:0.00}");
            foreach (float s in MouthShift)
                sb.Append(' ').Append(TrickWall.FitPlate(mid + away * s, along, out _, out _, out float l, out string w) ? $"fits {l:0.00}" : w);
            sb.Append(')');
        }
        sb.Append($" holes={Holes.Count} plates={Plates.Count}");
        foreach (var h in Holes)
        {
            string fit = Covered(h.At, h.Along) ? "covered" : TrickWall.FitPlate(h.At, h.Along, out _, out _, out float l, out string why) ? $"fits len={l:0.00}" : why;
            sb.Append($" h({h.At.x:0.00},{h.At.y:0.00} along={h.Along.x:0.00},{h.Along.y:0.00} {fit})");
        }
        foreach (var p in Plates) sb.Append($" p[{p.Name} ({p.C.x:0.00},{p.C.y:0.00}) len={p.Len:0.00}{(p.Broken ? " broken" : "")}]");
        return sb.ToString();
    }
}
