using System;
using System.Collections.Generic;
using System.Text;
using MoreRolesPlus.Bridge;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 水の計算の漏れをマップ全体で洗う確認用のコマンド (`water sweep`)。
// start = 歩ける島ごとに升を区切って浸水の口を置く (止まらない)。しばらく回した後の check で、
//   ・歩ける升なのに水が届いていない所 (DRY)
//   ・歩けるのに水の升として閉じている所 (CLOSED)
//   ・縁の先が低いのに落ちない所 (LOWER:理由) と、何も無い所へ落ちる縁 (EDGE:理由)
// を塊にまとめて Screens/watersweep_<船>.txt と .ppm に書く。試合は水浸しになるので最後に使う
internal static partial class WaterSim
{
    private static int _sweepStart = -1;
    private static int _sweepSeeds;

    private static void Sweep(string a, Action<string> reply)
    {
        var q = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string mode = q.Length > 0 ? q[0] : "";
        if (mode == "start") { reply(SweepStart(q.Length > 1 && int.TryParse(q[1], out int r) ? r : 4, q.Length > 2 && int.TryParse(q[2], out int g) ? g : 24)); return; }
        if (mode == "check") { SweepCheck(reply); return; }
        reply("ERR water sweep start [rate=4 (1 刻みに入れる深さ)] [grid=24 (口の間隔・升)] (今ある浸水は消して置き直す) | check");
    }

    // 升ごとの歩ける島 (升の中の最初の歩ける升の島)
    private static byte[] IslandOf()
    {
        int n = _w * _h, sw = SolidMap.W;
        var isl = new byte[n];
        for (int k = 0; k < n; k++)
        {
            int bits = _sub[k];
            if (bits == 0) continue;
            int b = System.Numerics.BitOperations.TrailingZeroCount(bits);
            int x = k % _w, y = k / _w;
            int id = SolidMap.IslandCell((y * Sub + b / Sub) * sw + x * Sub + b % Sub);
            isl[k] = (byte)Math.Clamp(id, 0, 255);
        }
        return isl;
    }

    private static string SweepStart(int rate, int grid)
    {
        if (!EnsureGrid()) return "ERR water sweep: no map";
        if (grid < 4) grid = 4;
        if (rate < 1) rate = 1;
        var isl = IslandOf();
        // 島ごとに多い高さ (その高さの升にだけ口を置く。高い床の筋に口が乗ると、そこが濡れて漏れが隠れる)
        var mode = new Dictionary<int, Dictionary<int, int>>();
        for (int k = 0; k < _w * _h; k++)
        {
            if (isl[k] == 0 || _lvl[k] == VoidLevel) continue;   // 高さの決まらない島 (はしごでつながらない区画) にも口を置く
            if (!mode.TryGetValue(isl[k], out var m)) mode[isl[k]] = m = new Dictionary<int, int>();
            m[_lvl[k]] = m.TryGetValue(_lvl[k], out int c) ? c + 1 : 1;
        }
        var top = new Dictionary<int, int>();
        foreach (var kv in mode)
        {
            int bl = 0, bn = -1;
            foreach (var lv in kv.Value) if (lv.Value > bn) { bn = lv.Value; bl = lv.Key; }
            top[kv.Key] = bl;
        }
        // 升を grid × grid の区画に切り、区画ごと・島ごとに区画の中心へいちばん近い口を 1 つ
        var best = new Dictionary<long, (int K, int D2)>();
        for (int y = 1; y < _h - 1; y++)
        for (int x = 1; x < _w - 1; x++)
        {
            int k = y * _w + x;
            int id = isl[k];
            if (id == 0 || _open[k] == 0 || !top.TryGetValue(id, out int tl) || _lvl[k] != tl) continue;
            bool inner = true;
            for (int d = 0; d < 4 && inner; d++) inner = _open[k + Nx[d] + Ny[d] * _w] != 0 && Passable(k, d);
            if (!inner) continue;
            int bx = x / grid, by = y / grid;
            int cx = bx * grid + grid / 2, cy = by * grid + grid / 2;
            int d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
            long key = ((long)id << 40) | ((long)by << 20) | (uint)bx;
            if (!best.TryGetValue(key, out var cur) || d2 < cur.D2) best[key] = (k, d2);
        }
        if (!_running) { _running = true; _step = StartStep(GameClock.Now - Delay); }
        PropSim.WakeForWater(GameClock.Now - Delay);
        Inflows.Clear();
        foreach (var kv in best) Inflows.Add(new Inflow { K = kv.Value.K, End = 0, Rate = rate * Full });
        _sweepStart = _step;
        _sweepSeeds = best.Count;
        MaxStepMs = 0;
        return $"OK water sweep start islands={top.Count} seeds={best.Count} rate={rate} grid={grid} step={_step} grid={_w}x{_h} hash={GridHash():x8}";
    }

    private sealed class Cluster { public string Type; public int N; public long Sx, Sy; public int X0 = int.MaxValue, Y0 = int.MaxValue, X1 = -1, Y1 = -1; public int Lvl, Lvl2 = int.MinValue, Isl; }

    // 印の付いた升 (4 方向でつながる塊) をまとめる。lvl2 = 塊の縁の先の高さ (LOWER 用・無ければ MinValue)
    private static void Clusters(byte[] mark, string type, byte[] isl, sbyte[] lvl2, List<Cluster> into, byte[] seen, List<int> queue)
    {
        Array.Clear(seen, 0, seen.Length);
        for (int s = 0; s < mark.Length; s++)
        {
            if (mark[s] == 0 || seen[s] != 0) continue;
            var c = new Cluster { Type = type, Lvl = _lvl[s], Isl = isl[s] };
            queue.Clear(); queue.Add(s); seen[s] = 1;
            for (int qi = 0; qi < queue.Count; qi++)
            {
                int k = queue[qi];
                int x = k % _w, y = k / _w;
                c.N++; c.Sx += x; c.Sy += y;
                if (x < c.X0) c.X0 = x; if (x > c.X1) c.X1 = x; if (y < c.Y0) c.Y0 = y; if (y > c.Y1) c.Y1 = y;
                if (lvl2 != null && lvl2[k] != NoLevel && (c.Lvl2 == int.MinValue || lvl2[k] < c.Lvl2)) c.Lvl2 = lvl2[k];
                for (int d = 0; d < 4; d++)
                {
                    int j = k + Nx[d] + Ny[d] * _w;
                    if (j < 0 || j >= mark.Length || mark[j] == 0 || seen[j] != 0) continue;
                    seen[j] = 1; queue.Add(j);
                }
            }
            into.Add(c);
        }
    }

    private static string RoomNameAt(float px, float py)
    {
        var ship = ShipStatus.Instance;
        if (!ship) return "-";
        var p = new Vector2(px, py);
        foreach (var r in ship.AllRooms)
            if (r && r.roomArea && r.roomArea.OverlapPoint(p)) return r.RoomId.ToString();
        return "-";
    }

    private static void SweepCheck(Action<string> reply)
    {
        if (!_ready) { reply("ERR water sweep: water is off (start first)"); return; }
        int n = _w * _h, sw = SolidMap.W;
        var isl = IslandOf();
        var solid = SolidMap.OpenCells;
        var dry = new byte[n];
        var closed = new byte[n];
        var lower = new Dictionary<string, byte[]>();
        var edge = new Dictionary<string, byte[]>();
        var lowerLvl = new sbyte[n];
        var count = new Dictionary<string, int>();
        int open = 0, wet = 0;
        for (int y = 1; y < _h - 1; y++)
        for (int x = 1; x < _w - 1; x++)
        {
            int k = y * _w + x;
            if (_open[k] == 0)
            {
                // 歩ける升があるのに閉じている (4 升未満・つながっていない)。扉の升は除く
                if (_doorCell[k] == 0 && _propCell[k] == 0)
                {
                    bool any = false;
                    for (int sy = 0, row = y * Sub * sw + x * Sub; sy < Sub && !any; sy++, row += sw)
                    for (int sx = 0; sx < Sub; sx++) if (solid[row + sx] != 0) { any = true; break; }
                    if (any) closed[k] = 1;
                }
                continue;
            }
            open++;
            if (_hgt[k] > 0) wet++;
            int lk = _lvl[k];
            if (lk == VoidLevel) continue;
            if (_hgt[k] == 0) dry[k] = 1;
            if (lk == NoLevel) continue;
            for (int d = 0; d < 4; d++)
            {
                int j = k + Nx[d] + Ny[d] * _w;
                if (_open[j] != 0 && Passable(k, d)) continue;
                int to = int.MinValue;
                string why = Nx[d] != 0 && OnLadder(x, y) ? "ladder" : Target(x, y, d, lk, !MapNotes.HasLevels, out to, out _, out _, out _);
                if (to == -1 && OnLadder(x, y)) { to = int.MinValue; why = "ladder"; }
                if (to != int.MinValue && (_shadowCut[k] >> d & 1) != 0) why = "shadow";
                string key = "+x-x+y-y".Substring(d * 2, 2) + ":" + why;
                count[key] = count.TryGetValue(key, out int c) ? c + 1 : 1;
                if (why == "shadow")
                {
                    // 影の線で切った縁 (落ちない)。先が低いか奈落なら候補
                    if (!lower.TryGetValue(why, out var sm)) lower[why] = sm = new byte[n];
                    sm[k] = 1;
                    lowerLvl[k] = to >= 0 ? _lvl[to] : VoidLevel;
                    continue;
                }
                if (to == -1)
                {
                    // 何も無い所へ落ちる縁 (奈落・空・崖の下)
                    if (!edge.TryGetValue(why, out var m)) edge[why] = m = new byte[n];
                    m[k] = 1;
                    continue;
                }
                if (to >= 0) continue;
                // 落ちない縁: 止めた物を無視して先を見て、最初の開いた升が低ければ候補
                for (int i = 1; i <= FallScan[d]; i++)
                {
                    int cx = x + Nx[d] * i, cy = y + Ny[d] * i;
                    if (cx < 1 || cy < 1 || cx >= _w - 1 || cy >= _h - 1) break;
                    int cc = cy * _w + cx;
                    if (_open[cc] == 0) continue;
                    int lc = _lvl[cc];
                    if (lc != NoLevel && lc < lk)
                    {
                        if (!lower.TryGetValue(why, out var m)) lower[why] = m = new byte[n];
                        m[k] = 1;
                        lowerLvl[k] = (sbyte)lc;
                    }
                    break;
                }
            }
        }
        var list = new List<Cluster>();
        var seen = new byte[n];
        var queue = new List<int>();
        Clusters(dry, "DRY", isl, null, list, seen, queue);
        Clusters(closed, "CLOSED", isl, null, list, seen, queue);
        foreach (var kv in lower) Clusters(kv.Value, "LOWER:" + kv.Key, isl, lowerLvl, list, seen, queue);
        foreach (var kv in edge) Clusters(kv.Value, "EDGE:" + kv.Key, isl, null, list, seen, queue);
        list.Sort((p, q) => q.N - p.N);

        string ship = ShipStatus.Instance ? ShipStatus.Instance.name.Replace("(Clone)", "") : "none";
        var sb = new StringBuilder();
        sb.Append($"SWEEP ship={ship} grid={_w}x{_h} cell={_cell:0.####} origin=({_org.x:0.###},{_org.y:0.###}) hash={GridHash():x8} step={_step} ran={(_sweepStart >= 0 ? _step - _sweepStart : -1)} seeds={_sweepSeeds} open={open} wet={wet} volume={Volume()} fell={FellTotal} void={VoidTotal} digest={Digest():x8} maxStepMs={MaxStepMs:0.000}\n");
        foreach (var kv in count) sb.Append("COUNT ").Append(kv.Key).Append(' ').Append(kv.Value).Append('\n');
        int dryCells = 0, closedCells = 0;
        foreach (var c in list)
        {
            if (c.Type == "DRY") dryCells += c.N; else if (c.Type == "CLOSED") closedCells += c.N;
            if (c.N < 2) continue;   // 1 升の塊は数だけ (細い壁の脇の既知の升)
            float ax = _org.x + ((float)c.Sx / c.N + 0.5f) * _cell, ay = _org.y + ((float)c.Sy / c.N + 0.5f) * _cell;
            sb.Append(c.Type).Append(" n=").Append(c.N)
              .Append($" at=({ax:0.00},{ay:0.00}) box=({_org.x + c.X0 * _cell:0.00},{_org.y + c.Y0 * _cell:0.00})-({_org.x + (c.X1 + 1) * _cell:0.00},{_org.y + (c.Y1 + 1) * _cell:0.00})")
              .Append(" lvl=").Append(c.Lvl);
            if (c.Lvl2 != int.MinValue) sb.Append('>').Append(c.Lvl2);
            sb.Append(" isl=").Append(c.Isl).Append(" room=").Append(RoomNameAt(ax, ay)).Append('\n');
        }
        string dir = TestBridge.ScreensDir;
        string txt = System.IO.Path.Combine(dir, $"watersweep_{ship}.txt");
        System.IO.File.WriteAllText(txt, sb.ToString());
        string ppm = System.IO.Path.Combine(dir, $"watersweep_{ship}.ppm");
        try { SweepImage(ppm, dry, closed, lower, edge); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[WaterSim] sweep image: {e.Message}"); ppm = "-"; }
        int shown = 0;
        foreach (var c in list)
        {
            if (c.N < 2 || shown++ >= 12) continue;
            float ax = _org.x + ((float)c.Sx / c.N + 0.5f) * _cell, ay = _org.y + ((float)c.Sy / c.N + 0.5f) * _cell;
            reply($"SWEEP {c.Type} n={c.N} at=({ax:0.0},{ay:0.0}) room={RoomNameAt(ax, ay)}");
        }
        reply($"OK water sweep check open={open} wet={wet} dry={dryCells} closed={closedCells} clusters={list.Count} ran={(_sweepStart >= 0 ? _step - _sweepStart : -1)} digest={Digest():x8} txt={txt} ppm={ppm}");
    }

    // 船の絵の上に判定を重ねる: 濡れた升 = 青・乾いた歩ける升 = 赤・閉じた升 = 紫・落ちない低い縁 = 橙・何も無い所へ落ちる縁 = 水色
    private static void SweepImage(string path, byte[] dry, byte[] closed, Dictionary<string, byte[]> lower, Dictionary<string, byte[]> edge)
    {
        var rgb = TerrainReview.RenderShip(1, out int w, out int h, out float ppu, out Vector2 o);
        int px = Math.Max(1, (int)MathF.Round(_cell * ppu));
        void Fill(int k, int r, int g, int b, int pct)
        {
            int x0 = (int)MathF.Round((_org.x + (k % _w) * _cell - o.x) * ppu), y0 = (int)MathF.Round((_org.y + (k / _w) * _cell - o.y) * ppu);
            for (int y = y0; y < y0 + px; y++)
            for (int x = x0; x < x0 + px; x++)
            {
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                int i = (y * w + x) * 3;
                rgb[i] = (byte)((rgb[i] * (100 - pct) + r * pct) / 100);
                rgb[i + 1] = (byte)((rgb[i + 1] * (100 - pct) + g * pct) / 100);
                rgb[i + 2] = (byte)((rgb[i + 2] * (100 - pct) + b * pct) / 100);
            }
        }
        for (int k = 0; k < _w * _h; k++)
        {
            if (_open[k] != 0 && _hgt[k] > 0) Fill(k, 40, 90, 255, 35);
            if (dry[k] != 0) Fill(k, 255, 30, 30, 75);
            if (closed[k] != 0) Fill(k, 220, 40, 255, 75);
        }
        foreach (var m in edge.Values) for (int k = 0; k < m.Length; k++) if (m[k] != 0) Fill(k, 0, 230, 230, 80);
        foreach (var m in lower.Values) for (int k = 0; k < m.Length; k++) if (m[k] != 0) Fill(k, 255, 150, 0, 85);
        TerrainReview.WriteRgb(path, rgb, w, h);
    }
}
