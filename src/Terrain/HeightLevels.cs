using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 床の高さの違い。マップには、壁の向こうに床が描かれていても高さが違う (はしごや階段の上と下) 所がある。
// そこを同じ高さの壁と同じに壊すと、壁を登れてしまう。高さが違う壁を壊した時は、見た目と視界だけ抜いて、
// 元の壁の面に壊れない縁 (動きの層だけ) を張り直す (向こうの床は見えるが歩いては行けない)。
// 高さが違う = 次のどちらか:
//   1. 試合の始めの歩ける所で、歩いて行き来できない (はしご・ジップライン・動く足場でしかつながらない) = 島が違う
//   2. 両側の部屋の組が段差の組 (はしごの上と下の部屋・マップごとの一覧)。階段でつながる段差は島では分からないので一覧で持つ
//   3. 両側の高さの区域が違う (マップの絵に塗った注釈とマップごとの範囲。同じ部屋の中で階段を挟んで高さが変わる所)
//   4. 壁の上に高さの色の線が引いてある (塗った注釈)
internal static class HeightLevels
{
    public const string LedgeName = "MrpLedge";
    private const int ShipLayer = 9;
    private const float Probe = 2.6f;   // 壁の面から床を探す深さ (奥の面の深さと同じ)
    private const float Piece = 0.25f;  // 縁を決める区間の長さ
    private const float ZoneInset = 0.4f; // 高さの区域を床の縁からどれだけ内側で見るか
    private const float MarkReach = 0.2f; // 壁の上に引いた線を、壁の線からどれだけ離れていても拾うか

    // マップごとの、はしごを使わずにつながる段差の部屋の組 (どちら向きでもよい)。はしごの上と下の部屋は自動で足す
    private static readonly (SystemTypes, SystemTypes)[] AirshipPairs = Array.Empty<(SystemTypes, SystemTypes)>();

    // マップごとの高さの区域 (範囲・高さ)。どの区域にも入らない所は高さ 0
    private static readonly (Rect Area, int Level)[] AirshipZones =
    {
        (Rect.MinMaxRect(-2.2f, -3.6f, 2.4f, -1.86f), -1), // エンジン室の南の一段低い床 (真ん中の階段で通路とつながる)
    };

    private static ShipStatus _ship;
    private static (Rect Area, int Level)[] _zones = Array.Empty<(Rect, int)>();
    private static readonly HashSet<(SystemTypes, SystemTypes)> Pairs = new();

    internal static void Warm() => Ensure();

    private static void Ensure()
    {
        var ship = ShipStatus.Instance;
        if (_ship == ship) return;
        _ship = ship;
        Pairs.Clear();
        _zones = Array.Empty<(Rect, int)>();
        if (!ship) return;
        if (ship.TryCast<AirshipStatus>() != null)
        {
            foreach (var (a, b) in AirshipPairs) AddPair(a, b);
            _zones = AirshipZones;
        }
        // はしごの上と下の部屋 (同じ部屋の中のはしごは組にならない)
        foreach (var l in ship.GetComponentsInChildren<Ladder>(true))
        {
            if (!l || !l.Destination) continue;
            var ra = RoomAt(l.transform.position);
            var rb = RoomAt(l.Destination.transform.position);
            if (ra != null && rb != null && ra.RoomId != rb.RoomId) AddPair(ra.RoomId, rb.RoomId);
        }
    }

    private static void AddPair(SystemTypes a, SystemTypes b)
    {
        Pairs.Add((a, b));
        Pairs.Add((b, a));
    }

    internal static string Describe()
    {
        Ensure();
        var list = new List<string>();
        foreach (var (a, b) in Pairs) if (string.CompareOrdinal(a.ToString(), b.ToString()) < 0) list.Add($"{a}|{b}");
        return $"ledgePairs={string.Join(",", list)} zones={_zones.Length}";
    }

    // 壁の線の上の点 m の両側 (法線 n の向きと逆向き) の床の高さが違うか。片側に床が無い (外壁・船体の塊) 時は、線が引いてある時だけ true
    internal static bool Differs(Vector2 m, Vector2 n)
    {
        Ensure();
        bool fa = FloorAlong(m, n, out int ia, out Vector2 pa), fb = FloorAlong(m, -n, out int ib, out Vector2 pb);
        // 区域は床の縁から少し内側で見る (塗った範囲が壁の線を少しはみ出しても、届かなくても同じ結果になるように)
        int za = fa ? ZoneLevel(pa + n * ZoneInset) : 0, zb = fb ? ZoneLevel(pb - n * ZoneInset) : 0;
        // 壁の上に高さの色の線が引いてある = 越えられない壁。片側が船体の塊 (厚い壁) でも掘り進めないように、床の有無より先に見る。
        // 両側の床が同じ色で塗ってある所 (塗りつぶした範囲の中の壁) は線とみなさない
        bool inside = za != 0 && za == zb;
        if (!inside && (MapNotes.Level(m) != 0 || MapNotes.Level(m + n * MarkReach) != 0 || MapNotes.Level(m - n * MarkReach) != 0)) return true;
        if (!fa || !fb) return false;
        if (ia != ib || za != zb) return true;
        if (Pairs.Count == 0) return false;
        var ra = RoomAt(pa);
        var rb = RoomAt(pb);
        return ra != null && rb != null && Pairs.Contains((ra.RoomId, rb.RoomId));
    }

    // 切り取った動きの層の壁の区間 (両端を 2 つずつ) のうち、両側の高さが違う所に壊れない縁を張る。張った本数を返す。
    // 蓋を作った後・歩ける所の地図を塗り足す前に呼ぶ (縁の向こうを塗らないように)
    public static int Build(List<Vector2> shipRemoved)
    {
        var ship = ShipStatus.Instance;
        if (!ship || !SolidMap.Valid || shipRemoved.Count < 2) return 0;
        GameObject go = null;
        int made = 0;
        var run = new List<Vector2>();
        for (int i = 0; i + 1 < shipRemoved.Count; i += 2)
        {
            Vector2 a = shipRemoved[i], b = shipRemoved[i + 1], d = b - a;
            float len = d.magnitude;
            if (len < 1e-4f) continue;
            Vector2 dir = d / len, n = new(-dir.y, dir.x);
            int parts = Math.Max(1, (int)MathF.Ceiling(len / Piece));
            run.Clear();
            for (int p = 0; p <= parts; p++)
            {
                bool on = p < parts && Differs(Snap(a + dir * (len * (p + 0.5f) / parts)), n);
                if (on)
                {
                    if (run.Count == 0) run.Add(a + dir * (len * p / parts));
                    continue;
                }
                if (run.Count > 0)
                {
                    run.Add(a + dir * (len * p / parts));
                    go ??= NewHolder(ship);
                    Add(go, run);
                    made++;
                    run.Clear();
                }
            }
        }
        return made;
    }

    private static GameObject NewHolder(ShipStatus ship)
    {
        var go = new GameObject(LedgeName) { layer = ShipLayer };
        go.transform.SetParent(ship.transform, true);
        return go;
    }

    private static void Add(GameObject go, List<Vector2> run)
    {
        var t = go.transform;
        var pts = new Vector2[run.Count];
        var world = new List<Vector2>(run.Count);
        for (int i = 0; i < run.Count; i++)
        {
            var p = Snap(run[i]);
            world.Add(p);
            pts[i] = t.InverseTransformPoint(p);
        }
        TerrainDigest.Chain(world);
        go.AddComponent<EdgeCollider2D>().points = pts;
    }

    // 1/64 にそろえる (端末ごとの計算の末尾の差で、床を探し始める点の升が変わらないように)
    private static Vector2 Snap(Vector2 p) => new(MathF.Round(p.x * 64f) / 64f, MathF.Round(p.y * 64f) / 64f);

    // 線の片側へ進んで最初に出会う床 (島の番号とその点)
    private static bool FloorAlong(Vector2 from, Vector2 dir, out int island, out Vector2 at)
    {
        const float step = 1f / 32f;
        for (float d = step; d <= Probe; d += step)
        {
            at = from + dir * d;
            island = SolidMap.IslandAt(at);
            if (island != 0) return true;
        }
        island = 0;
        at = default;
        return false;
    }

    // 高さの区域 (マップごとの範囲) を升に塗る。注釈 (MapNotes.RasterLevels) はこの後に上から塗る
    internal static void RasterZones(sbyte[] lvl, int w, int h, Vector2 org, float cell)
    {
        Ensure();
        foreach (var (r, level) in _zones)
        {
            int x0 = Math.Max(0, (int)MathF.Ceiling((r.xMin - org.x) / cell - 0.5f)), x1 = Math.Min(w - 1, (int)MathF.Floor((r.xMax - org.x) / cell - 0.5f));
            int y0 = Math.Max(0, (int)MathF.Ceiling((r.yMin - org.y) / cell - 0.5f)), y1 = Math.Min(h - 1, (int)MathF.Floor((r.yMax - org.y) / cell - 0.5f));
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++) lvl[y * w + x] = (sbyte)level;
        }
    }

    // 島 (SolidMap の番号) ごとの高さの順位。はしごの上の島は下の島より 1 段高い。いちばん広い島を 0 として、
    // はしごでつながる島だけ順位が決まる (決まらない島は int.MinValue)。はしごは 2 本で上下の組になる
    internal static int[] IslandRanks(Func<int, int> areaOf)
    {
        int n = SolidMap.IslandCount;
        var rank = new int[n + 1];
        Array.Fill(rank, int.MinValue);
        var ship = ShipStatus.Instance;
        if (!ship || n == 0) return rank;
        var pairs = new List<(int Top, int Bottom)>();
        foreach (var l in ship.GetComponentsInChildren<Ladder>(true))
        {
            if (!l || !l.IsTop || !l.Destination) continue;
            int a = SolidMap.IslandAt(l.transform.position), b = SolidMap.IslandAt(l.Destination.transform.position);
            if (a > 0 && b > 0 && a != b) pairs.Add((a, b));
        }
        int big = 1;
        for (int i = 2; i <= n; i++) if (areaOf(i) > areaOf(big)) big = i;
        rank[big] = 0;
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var (t, b) in pairs)
            {
                if (rank[b] != int.MinValue && rank[t] == int.MinValue) { rank[t] = rank[b] + 1; changed = true; }
                else if (rank[t] != int.MinValue && rank[b] == int.MinValue) { rank[b] = rank[t] - 1; changed = true; }
            }
        }
        return rank;
    }

    private static int ZoneLevel(Vector2 p)
    {
        int painted = MapNotes.Level(p); // マップの絵に塗った注釈を先に
        if (painted != 0) return painted;
        foreach (var (area, level) in _zones)
            if (area.Contains(p)) return level;
        return 0;
    }

    private static PlainShipRoom RoomAt(Vector2 p)
    {
        var ship = ShipStatus.Instance;
        if (!ship) return null;
        foreach (var r in ship.AllRooms)
            if (r && r.roomArea && r.roomArea.OverlapPoint(p)) return r;
        return null;
    }
}
