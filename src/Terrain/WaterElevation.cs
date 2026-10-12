using System;
using System.Collections.Generic;
using System.Text;
using MoreRolesPlus.Bridge;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 床の本当の高さ (1024 = 1 単位)。本編の絵は壁や崖やはしごを「高さ : 画面の縦の長さ = 1 : 1」で描くので、画面の縦の長さから島どうしの差を測る。
//   1. 南向きの崖: 上の島の縁から画面の下へ、面を越えて下の島の床に着くまでの長さ。島の組ごとに真ん中の値 (十分に測れた組)
//   2. はしご: 上の端と下の端の縦の長さ (端は立ち位置なので少し長く出る)
//   3. どちらでもつながらない島 (扉の奥の部屋など) は、同じ段の島の高さの真ん中の値・それも無ければ段 × はしごの長さの真ん中の値
// いちばん広い島 = 0。島の中の段 (階段でつながる低い床・一段高い床) は段の数 × 段差の高さ。
// 測るのは試合の始めの地形の全体の作り直しの時だけ (全員同じ地形から測る・長さは 1/512 格子へ寄せてから整数)
internal static partial class WaterSim
{
    private const int ZDefault = Full;      // はしごが 1 本も無いマップの 1 段
    private const int ZMinStep = Full / 8;  // 崖の面の長さの下限 (面の無い縁で 0 にならないように)
    private const int FaceSure = 8;         // 崖の面の測りをはしごより先に使う数

    private static byte[] _cellIsl;         // 升の島 (IslandLevels が作る)
    private static int[] _islRank;          // 島の段 (int.MinValue = 決まらない)
    private static int[] _islArea;
    private static int[] _islZ = Array.Empty<int>(); // 島 → 高さ (int.MinValue = 決まらない)
    private static int[] _islBase = Array.Empty<int>(); // 島 → 高さ − 島の段 × 段差 (升の高さ = これ + 升の段 × 段差・C++ の流れが読む)
    // 床の高さで水を流す (C++ の部品がある時だけ・無ければ段の順位の古いルール)。確認用に手元だけ切れる
    internal static bool ZFlow = true;
    private static int _cellZ;              // 升 1 つの長さ (1024/単位)
    private static string _zInfo = "";
    private static readonly Dictionary<int, List<int>> ZSamples = new(); // 上の島 × 256 + 下の島 → 崖の面の長さ

    private static int ToZ(float units) => (int)MathF.Round(units * 512f) * 2;

    private static void ZBegin()
    {
        ZSamples.Clear();
        _cellZ = ToZ(_cell);
    }

    // 縁の升 k から画面の下へ face 升の面を越えて升 to の床に着いた
    private static void ZSample(int k, int to, int face)
    {
        if (_cellIsl == null) return;
        int u = _cellIsl[k], v = _cellIsl[to];
        if (u == 0 || v == 0 || u == v) return;
        int key = u * 256 + v;
        if (!ZSamples.TryGetValue(key, out var list)) ZSamples[key] = list = new List<int>();
        list.Add(Math.Max(ZMinStep, face * _cellZ));
    }

    private static int Median(List<int> v)
    {
        v.Sort();
        return v[v.Count / 2];
    }

    private static void ZSolve()
    {
        int n = _islRank == null ? 0 : _islRank.Length;
        _islZ = new int[n];
        Array.Fill(_islZ, int.MinValue);
        _islBase = new int[n];
        Array.Fill(_islBase, int.MinValue);
        var sb = new System.Text.StringBuilder();
        if (n <= 1 || _cellIsl == null) { _zInfo = "no islands"; ZSamples.Clear(); return; }
        // 上の島, 下の島, 差
        var ladders = new List<(int U, int V, int D)>();
        var faces = new List<(int U, int V, int D)>();
        var ship = ShipStatus.Instance;
        if (ship)
            foreach (var l in ship.GetComponentsInChildren<Ladder>(true))
            {
                if (!l || !l.IsTop || !l.Destination) continue;
                Vector3 a = l.transform.position, b = l.Destination.transform.position;
                int u = SolidMap.IslandAt(a.x, a.y), v = SolidMap.IslandAt(b.x, b.y);
                if (u <= 0 || v <= 0 || u == v || u >= n || v >= n) continue;
                ladders.Add((u, v, ToZ(MathF.Abs(a.y - b.y))));
            }
        var weak = new List<(int U, int V, int D)>();
        foreach (var kv in ZSamples)
        {
            int u = kv.Key / 256, v = kv.Key % 256;
            if (u < n && v < n) (kv.Value.Count >= FaceSure ? faces : weak).Add((u, v, Median(kv.Value)));
        }
        // 辞書の順は端末で変わりうるので、島の番号で並べてから広げる
        Comparison<(int U, int V, int D)> byIsland = (p, q) => p.U != q.U ? p.U.CompareTo(q.U) : p.V.CompareTo(q.V);
        // 崖の面は測れた数の多い組から使う (少ない組は面の下の別の物に着いた測りが混ざりやすい)
        Comparison<(int U, int V, int D)> byCount = (p, q) =>
        {
            int c = ZSamples[q.U * 256 + q.V].Count.CompareTo(ZSamples[p.U * 256 + p.V].Count);
            return c != 0 ? c : byIsland(p, q);
        };
        faces.Sort(byCount);
        weak.Sort(byCount);
        int big = 1;
        for (int i = 2; i < n; i++) if (_islArea[i] > _islArea[big]) big = i;
        // 段の順位が同じで、水が段も縁も無しにそのまま行き来する島どうしは同じ高さ (扉の奥の部屋・水だけが届く升の島)。
        // 崖の面の測りより先に、組にまとめて 1 つの島として扱う
        FlatGroups(n);
        _islZ[Grp(big)] = 0;
        // はしごの端は床の縁より内側の立ち位置なので、縦の長さは段の差より長く出る。崖の面を十分に測れた組を先に使い、
        // 次にはしご、最後に測りの少ない崖 (先に決まった差は後の測りで上書きしない)
        for (bool more = true; more;) more = Spread(faces) | Spread(ladders) | Spread(weak);
        for (int i = 1; i < n; i++) _islZ[i] = _islZ[Grp(i)];
        // 残りの島: 同じ段の島の高さの真ん中の値・無ければ段 × はしごの長さの真ん中の値
        var lad = new List<int>();
        foreach (var x in ladders) lad.Add(x.D);
        int step = lad.Count > 0 ? Median(lad) : ZDefault;
        var byRank = new Dictionary<int, List<int>>();
        for (int i = 1; i < n; i++)
            if (_islZ[i] != int.MinValue && _islRank[i] != int.MinValue)
            {
                if (!byRank.TryGetValue(_islRank[i], out var list)) byRank[_islRank[i]] = list = new List<int>();
                list.Add(_islZ[i]);
            }
        int guessed = 0;
        for (int i = 1; i < n; i++)
        {
            if (_islZ[i] != int.MinValue || _islRank[i] == int.MinValue) continue;
            _islZ[i] = byRank.TryGetValue(_islRank[i], out var list) ? Median(list) : _islRank[i] * step;
            guessed++;
        }
        sb.Append("ladders:");
        foreach (var x in ladders) sb.Append(' ').Append(x.U).Append('>').Append(x.V).Append('=').Append((x.D / (float)Full).ToString("0.00"));
        sb.Append(" faces:");
        faces.AddRange(weak);
        foreach (var x in faces) sb.Append(' ').Append(x.U).Append('>').Append(x.V).Append('=').Append((x.D / (float)Full).ToString("0.00")).Append('×').Append(ZSamples[x.U * 256 + x.V].Count);
        // 島ごとの高さ・升の数・真ん中 (確認用)
        var cx = new long[n]; var cy = new long[n]; var cnt = new int[n];
        for (int k = 0; k < _w * _h; k++) { int i = _cellIsl[k]; if (i > 0 && i < n) { cx[i] += k % _w; cy[i] += k / _w; cnt[i]++; } }
        sb.Append(" islands:");
        for (int i = 1; i < n; i++)
            if (cnt[i] > 0)
                sb.Append(' ').Append(i).Append('=').Append(_islZ[i] == int.MinValue ? "?" : (_islZ[i] / (float)Full).ToString("0.00"))
                  .Append('#').Append(cnt[i]).Append("@(").Append((_org.x + (cx[i] / (float)cnt[i] + 0.5f) * _cell).ToString("0.0")).Append(',')
                  .Append((_org.y + (cy[i] / (float)cnt[i] + 0.5f) * _cell).ToString("0.0")).Append(')');
        sb.Append(" guessed=").Append(guessed);
        _zInfo = sb.ToString();
        ZSamples.Clear();
        for (int i = 1; i < n; i++)
            if (_islZ[i] != int.MinValue && _islRank[i] != int.MinValue) _islBase[i] = _islZ[i] - Math.Clamp(_islRank[i], -100, 100) * StepHeight;
    }

    // 片側の高さが決まっている組 (島の組どうし) から、もう片側を決める。1 つでも決めたら true
    private static int[] _grp = Array.Empty<int>();
    private static int Grp(int i)
    {
        while (_grp[i] != i) i = _grp[i] = _grp[_grp[i]];
        return i;
    }

    private static void FlatGroups(int n)
    {
        _grp = new int[n];
        for (int i = 0; i < n; i++) _grp[i] = i;
        for (int k = 0; k < _w * _h; k++)
        {
            if (_open[k] == 0) continue;
            int u = _cellIsl[k];
            if (u == 0 || u >= n || _islRank[u] == int.MinValue) continue;
            for (int d = 0; d < 4; d += 2)
            {
                // 右と上の隣 (落ちる縁・段差・海の向きは開いていないので Passable で外れる)
                int j = k + Nx[d] + Ny[d] * _w;
                if (_open[j] == 0 || !Passable(k, d)) continue;
                int v = _cellIsl[j];
                if (v == 0 || v >= n || v == u || _islRank[v] != _islRank[u]) continue;
                int a = Grp(u), b = Grp(v);
                if (a != b) _grp[Math.Max(a, b)] = Math.Min(a, b); // 小さい番号を代表に (端末で同じ結果)
            }
        }
    }

    private static bool Spread(List<(int U, int V, int D)> rel)
    {
        bool any = false;
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var (u0, v0, d) in rel)
            {
                int u = Grp(u0), v = Grp(v0);
                if (u == v) continue;
                // 1 つ決めたら先頭から見直す (並びの前の組ほど先に効く)
                if (_islZ[v] != int.MinValue && _islZ[u] == int.MinValue) { _islZ[u] = _islZ[v] + d; changed = true; break; }
                if (_islZ[u] != int.MinValue && _islZ[v] == int.MinValue) { _islZ[v] = _islZ[u] - d; changed = true; break; }
            }
            any |= changed;
        }
        return any;
    }

    // 升の床の高さ (1024/単位)。閉じた升・決まらない升・奈落は int.MinValue
    internal static int GroundZ(int k)
    {
        if (_open[k] == 0 || _cellIsl == null) return int.MinValue;
        int l = _lvl[k];
        if (l == NoLevel || l == VoidLevel) return int.MinValue;
        int i = _cellIsl[k];
        int z = i != 0 && i < _islZ.Length && _islZ[i] != int.MinValue && _islRank[i] != int.MinValue
            ? _islZ[i] + (l - Math.Clamp(_islRank[i], -100, 100)) * StepHeight
            : l * ZDefault;
        return z + _bed[k] + _furn[k];
    }

    // 確認用: 今の縁の種類 (段差・滝・海・隣どうしの一方通行) を高さの差で分け直した時に食い違う所を数える。
    // 段差 = 差が段の高さ以下・滝 = それより低い。食い違いの種類ごとに数と最初の数か所 (座標・差) を出す
    private static string ZEdges()
    {
        if (!_ready || _cellIsl == null) return "ERR water sweep zedge: no grid";
        var count = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var where = new Dictionary<string, StringBuilder>();
        void Add(string kind, int k, int dz)
        {
            count[kind] = count.TryGetValue(kind, out int c) ? c + 1 : 1;
            if (!where.TryGetValue(kind, out var sb)) where[kind] = sb = new StringBuilder();
            if (c < 6) sb.Append(" (").Append((_org.x + (k % _w + 0.5f) * _cell).ToString("0.0")).Append(',')
                .Append((_org.y + (k / _w + 0.5f) * _cell).ToString("0.0")).Append(")Δ").Append(dz == int.MinValue ? "?" : (dz / (float)Full).ToString("0.00"));
        }
        for (int k = 0; k < _w * _h; k++)
        {
            int zk = GroundZ(k);
            if (_open[k] == 0 || zk == int.MinValue) continue;
            for (int d = 0; d < 4; d++)
            {
                int bit = 1 << d;
                if ((_stepMask[k] & bit) != 0)
                {
                    int zt = GroundZ(_stepTo[EdgeKey(k, d)]);
                    int dz = zt == int.MinValue ? int.MinValue : zk - zt;
                    Add(dz == int.MinValue ? "step:noZ" : Math.Abs(dz) <= StepHeight ? "step:ok" : dz > 0 ? "step>fall" : "step>wall", k, dz);
                }
                else if ((_fallMask[k] & bit) != 0)
                {
                    int to = FallTo.TryGetValue(k * 4 + d, out var f) ? f.To : -1;
                    if (to < 0) { Add("fall:void", k, 0); continue; }
                    int zt = GroundZ(to);
                    int dz = zt == int.MinValue ? int.MinValue : zk - zt;
                    Add(dz == int.MinValue ? "fall:noZ" : dz > StepHeight ? "fall:ok" : dz > 0 ? "fall>step" : "fall>uphill", k, dz);
                }
                else if ((_seaMask[k] & bit) != 0) Add("sea", k, 0);
                else
                {
                    int j = k + Nx[d] + Ny[d] * _w;
                    // 隣どうしは右と上だけ見て 1 回ずつ数える
                    if (d == 1 || d == 3 || _open[j] == 0 || !Passable(k, d)) continue;
                    int zj = GroundZ(j);
                    if (zj == int.MinValue) continue;
                    int lk = _lvl[k], lj = _lvl[j];
                    bool gate = lk != lj && lk != NoLevel && lj != NoLevel;
                    int dz = zk - zj;
                    if (_cellIsl[k] != _cellIsl[j] && !gate && dz != _bed[k] + _furn[k] - _bed[j] - _furn[j]) Add("open:island", k, dz);
                    else if (Math.Abs(dz) > StepHeight) Add(gate ? "open:cliff(rank)" : "open:cliff", k, dz);
                    else if (gate) Add("open:rankgate", k, dz);
                }
            }
        }
        var o = new StringBuilder("OK water sweep zedge");
        foreach (var kv in count) o.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
        foreach (var kv in count)
            if (!kv.Key.EndsWith(":ok") && kv.Key != "sea" && kv.Key != "fall:void") o.Append(" | ").Append(kv.Key).Append(where[kv.Key]);
        return o.ToString();
    }

    // 確認用: 升の床の高さを色で重ねる (低い = 青 → 高い = 赤・決まらない = 紫)。落ちる縁 = 白の枠
    private static string ZImage()
    {
        if (!_ready) return "ERR water sweep z: no grid";
        var rgb = TerrainReview.RenderShip(1, out int w, out int h, out float ppu, out Vector2 o);
        int px = Math.Max(1, (int)MathF.Round(_cell * ppu));
        int zMin = int.MaxValue, zMax = int.MinValue;
        for (int k = 0; k < _w * _h; k++)
        {
            int z = GroundZ(k);
            if (z == int.MinValue) continue;
            zMin = Math.Min(zMin, z); zMax = Math.Max(zMax, z);
        }
        int span = Math.Max(1, zMax - zMin);
        for (int k = 0; k < _w * _h; k++)
        {
            if (_open[k] == 0) continue;
            int z = GroundZ(k), r, g, b;
            if (z == int.MinValue) { r = 200; g = 0; b = 255; }
            else
            {
                int t = Math.Clamp((z - zMin) * 255 / span, 0, 255);
                r = t; g = 255 - Math.Abs(t * 2 - 255); b = 255 - t;
            }
            bool edge = _fallMask[k] != 0;
            int x0 = (int)MathF.Round((_org.x + (k % _w) * _cell - o.x) * ppu), y0 = (int)MathF.Round((_org.y + (k / _w) * _cell - o.y) * ppu);
            for (int y = y0; y < y0 + px; y++)
            for (int x = x0; x < x0 + px; x++)
            {
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                int i = (y * w + x) * 3;
                bool rim = edge && (x == x0 || y == y0 || x == x0 + px - 1 || y == y0 + px - 1);
                int pr = rim ? 255 : r, pg = rim ? 255 : g, pb = rim ? 255 : b, pct = rim ? 90 : 50;
                rgb[i] = (byte)((rgb[i] * (100 - pct) + pr * pct) / 100);
                rgb[i + 1] = (byte)((rgb[i + 1] * (100 - pct) + pg * pct) / 100);
                rgb[i + 2] = (byte)((rgb[i + 2] * (100 - pct) + pb * pct) / 100);
            }
        }
        string ship = ShipStatus.Instance ? ShipStatus.Instance.name.Replace("(Clone)", "") : "none";
        string path = System.IO.Path.Combine(TestBridge.ScreensDir, $"waterz_{ship}.ppm");
        TerrainReview.WriteRgb(path, rgb, w, h);
        return $"OK water sweep z min={zMin / (float)Full:0.00} max={zMax / (float)Full:0.00} {_zInfo} ppm={path}";
    }
}
