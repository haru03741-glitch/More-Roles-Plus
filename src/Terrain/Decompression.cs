using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 外壁が宇宙まで抜けた時の気圧と流れ (スケルド)。升 = 0.25 単位 (WaterSim と同じ SolidMap の 4×4 升・同じ開き方の判定)。
// 穴の口から閉じた扉を通らずに歩いて行ける所が 1 つの範囲で、範囲ごとに気圧を持つ。
// - 気圧: dP = −K·幅·P / 範囲の広さ (全体 389 単位² で幅 1.0 なら約 18 秒で 1 割まで)。口が全部ふさがると 10 秒で戻る。
// - 流れ: 口からの道のり (升の最短路・縦横 2・斜め 3) を、穴・扉・ふさがりが変わった時だけ作り直す。
//   引く強さ (満タン時・歩く速さの倍) = 口の際 1.6 / 3 単位 0.6 / 6 単位 0.15 / 10 単位より先 0。気圧に比例。
// - 補修フォーム: 開通から FoamDelay 後に縁から膨らみ FoamTime でふさがる (引く強さ 0・気圧が戻り始める)。
// エアシップ (Wind): 空へ開いても気圧は減らず、口から 3 単位まで歩く速さの 0.4 倍・4 単位で 0 の風が吹き続ける。
// 全員の手元で同じにするため WaterSim と同じく試合の刻みの Delay 遅れで整数だけで進める。開通は地形の件の刻みで入る (新しい電文なし)。
// 扉の開け閉めは本編の同期で端末ごとに届く時刻がずれるので、ホスト (一人の時は自分) だけが DoorPoll ごとに読み、
// 変わった刻みと開きを地形の電文で配る (TerrainSync.BroadcastDoors)。全員その記録 (DoorLog) の刻みで反映する。
// 物 (PropSim) は確定の刻みをこちらと 1 刻みずつ揃えて進め (StepThrough)、整数の流れ (FlowAt) を読む。
// クルーは PlayerPhysics.FixedUpdate の後で自分の体の速さに流れを足す (位置は普通の移動の同期で他人へ届く)
internal static class Decompression
{
    private const int Delay = WaterSim.Delay;
    private const int Sub = 4;
    private const int MaxStepsPerFrame = 20;
    public const int Full = 1 << 16;          // 気圧 1.0
    private const int MinWidth256 = 154;      // 吸い出しが起きる口の幅 0.6 (×256)
    private const int MaxWidth256 = 768;      // 流れに効く口の幅の上限 3.0
    private const float MergeReach = 1f;      // 開いている口からこの距離以内の新しい口は同じ穴を広げた物
    private const float SeedReach = 0.35f;    // 口の線からこの距離以内の升が道のりの出発点
    // 抜ける速さ: 1 刻みに P·幅256·KNum / (KDen·升の数)。389 単位² (= 6224 升)・幅 1.0 で 540 刻み (18 秒) で 1 割まで
    private const long KNum = 796, KDen = 7680;
    private const int Recover = Full / 300;   // 口が全部ふさがった範囲は 10 秒で戻る
    private const int OpenFloor = Full * 2 / 5; // 口が開いている間はここより下がらない (泡でふさがるまで吹き飛ばし続ける)
    // エアシップの風: 口から 3 単位まで歩く速さの 0.4 倍・4 単位で 0
    private const float WindMul = 0.4f;
    private const float SealDepth = 0.55f;    // 泡でふさいだ口の視界の線の奥行き (宇宙までの距離が測れない時)
    private const int Wind1024 = 410;
    internal static bool Wind => SolidMap.SkyHull;
    public const int FoamDelay = 180, FoamTime = 75; // 開通 6 秒後に膨らみ始め 2.5 秒でふさがる
    private static int[] _open1024 = Array.Empty<int>(); // 範囲ごとの口の開き具合 (泡が狭めていく・/1024)
    private const int DoorPoll = 15;          // ホストが扉を読む間隔 (0.5 秒)
    // 道のり (縦横 2・斜め 3 = 1 升 0.25 単位が 2) の上限。10 単位より先は引かない
    private const int PerUnit = 8;
    private const int Cap = 10 * PerUnit + 4;
    private const ushort Far = ushort.MaxValue;

    private static readonly int[] Nx = { 1, -1, 0, 0, 1, -1, 1, -1 };
    private static readonly int[] Ny = { 0, 0, 1, -1, 1, 1, -1, -1 };

    internal sealed class Breach
    {
        public int Start;
        public readonly List<(Vector2 A, Vector2 B)> Lines = new();
        public int W256;
        public Vector2 Away;      // 宇宙への向き (喉を掘った向き・演出用)
        public bool Sealed;
        public bool ByProp;       // 物がふさいだ
        public int Comp = -1;     // 口の升がいる範囲 (作り直すたびに書く)
    }

    private struct Pending
    {
        public int Tick;
        public bool Rebuild;      // 爆発で歩ける所が変わった (升を作り直す)
        public List<(Vector2 A, Vector2 B)> Lines; // 開通した口 (Rebuild でない時・曲がった壁では数本)
        public Vector2 Away;
    }

    private static int _shipGen;
    private static bool _running;
    private static int _step;
    private static int _w, _h;
    private static Vector2 _org;
    private static float _cell;
    private static ushort[] _sub;
    private static byte[] _edge;
    private static byte[] _blocked;           // 閉じた扉の升
    private static byte[] _furn;              // 家具の当たり判定の升 (空気は上を通るので範囲には入れ、道のりだけ回り込ませる)
    private static int[] _comp;               // 範囲の番号 (-1 = 閉じた升)
    private static int[] _compCells;
    private static int[] _press;              // 範囲ごとの気圧
    private static ushort[] _dist;            // 口からの道のり (Far = 届かない)
    private static int[] _par;                // 口へ向かう次の升 (−1 = 口の際)
    private static int[] _seed;               // 口の際の升の口の番号 + 1 (0 = 口の際でない)
    private static readonly List<(int X0, int Y0, int X1, int Y1)> DoorCells = new();
    private static readonly List<Plain> Doors = new();
    private static ulong _doorBits;
    // 扉の記録 (刻み順)。最初の記録より前は全部開き。ホストは読んで足して配り、客は届いた物を足す
    private static readonly List<(int Tick, ulong Bits)> DoorLog = new();
    private static bool _doorsListed;
    private static int _nextPoll;
    private static bool _hold;                // 確認用: 自分の手元だけ口をふさがず気圧を満タンのままにする
    private static bool _sealDirty;           // 物が口をふさいだ (次の刻みで道のりを作り直す)
    private static readonly List<Breach> Breaches = new();
    private static readonly List<Pending> Queue = new();
    // つかめる所 (流れの中の壁・扉の枠・家具の出っ張った角)。道のりを作り直す時だけ作る (自分の端末だけで使う)
    private static readonly List<Vector2> Grips = new();
    private const float GripSpacing = 0.6f;

    private readonly struct Plain
    {
        public readonly OpenableDoor Door;
        public Plain(OpenableDoor d) { Door = d; }
    }

    // 確認用
    internal static int Late { get; private set; }
    internal static int LateDoors { get; private set; }
    internal static bool Running => _running;
    internal static int Step => _step;
    internal static double LastStepMs { get; private set; }
    internal static double LastFieldMs { get; private set; }
    internal static double LastFurnMs { get; private set; }
    internal static int Fields { get; private set; }
    // クルーを引くか (毎フレームの入口で読む・il2cpp を触らない)
    internal static bool Pulling { get; private set; }

    // ── 入口 ───────────────────────────────────────────────────────────

    // 全ての破壊の結果 (全員の手元・TerrainDamage.Apply の直後)。爆発で外壁が宇宙まで抜けたら口を足す
    public static void OnApplied(in ResolvedDamage r)
    {
        try
        {
            if (r.Kind != DamageKind.Explosion || !SolidMap.Breachable) return;
            int tick = GameClock.Expand(r.Tick);
            float w = TerrainDamage.LastBreach;
            if (_running) Enqueue(new Pending { Tick = tick, Rebuild = true });
            if (w <= 0f) return;
            var lines = new List<(Vector2, Vector2)>(TerrainDamage.LastMouths);
            var away = TerrainDamage.LastAway;
            // 口がどれも壁の中 (内側も歩けない所) なら、部屋は外へつながっていない (前の穴の奥の蓋を抜いただけ)
            bool room = false;
            for (int i = 0; i < lines.Count && !room; i++)
            {
                var (a, b) = lines[i];
                var aw = TerrainDamage.LastMouthAway[i];
                room = !SolidMap.Solid((a + b) * 0.5f - aw.normalized * 0.3f);
            }
            if (!room) return;
            Open(tick, lines, away);
            if (w * 256f >= MinWidth256) DecompFx.OnBreach(tick, lines, w, away);
        }
        catch (Exception e) { Fail("apply", e); }
    }

    private static void Open(int tick, List<(Vector2, Vector2)> lines, Vector2 away)
    {
        if (!_running)
        {
            if (!SolidMap.Ensure() || !SolidMap.Valid) return;
            _running = true;
            _step = Math.Min(tick, GameClock.Now - Delay);
            Build();
        }
        Enqueue(new Pending { Tick = tick, Lines = lines, Away = away });
    }

    private static void Enqueue(Pending p)
    {
        if (p.Tick < _step) { Late++; p.Tick = _step; }
        int i = Queue.Count;
        while (i > 0 && Queue[i - 1].Tick > p.Tick) i--;
        Queue.Insert(i, p);
    }

    private static void Fail(string where, Exception e)
    {
        Plugin.Logger.LogError($"[Decompression] {where}: {e}");
        Reset();
    }

    // ── 升 ─────────────────────────────────────────────────────────────

    private static void Build()
    {
        _w = SolidMap.W / Sub;
        _h = SolidMap.H / Sub;
        _org = SolidMap.Origin;
        _cell = Sub / SolidMap.Ppu;
        int n = _w * _h;
        _sub = new ushort[n];
        _edge = new byte[n];
        _blocked = new byte[n];
        _furn = new byte[n];
        _comp = new int[n];
        _dist = new ushort[n];
        _par = new int[n];
        _compCells = Array.Empty<int>();
        _press = Array.Empty<int>();
        _open1024 = Array.Empty<int>();
        _seed = new int[n];
        ListDoors();
        _doorBits = BitsAt(_step);
        Grid();
        Furniture();
        Regions(null, null);
    }

    private static int CellX(float x) => (int)MathF.Floor((x - _org.x) / _cell);
    private static int CellY(float y) => (int)MathF.Floor((y - _org.y) / _cell);

    // 扉の一覧 (AllDoors の順・64 まで)。升は SolidMap と同じ原点の 0.25 単位
    private static void ListDoors()
    {
        if (_doorsListed) return;
        var ship = ShipStatus.Instance;
        if (!ship || ship.AllDoors == null || !SolidMap.Valid) return;
        _doorsListed = true;
        Doors.Clear();
        DoorCells.Clear();
        var org = SolidMap.Origin;
        float cell = Sub / SolidMap.Ppu;
        foreach (var d in ship.AllDoors)
        {
            if (!d) continue;
            var col = d.GetComponent<Collider2D>();
            if (!col) continue;
            var bb = col.bounds;
            Doors.Add(new Plain(d));
            DoorCells.Add(((int)MathF.Floor((bb.min.x - org.x) / cell), (int)MathF.Floor((bb.min.y - org.y) / cell),
                (int)MathF.Floor((bb.max.x - org.x) / cell), (int)MathF.Floor((bb.max.y - org.y) / cell)));
            if (Doors.Count == 64) break;
        }
    }

    private static ulong AllOpen => Doors.Count >= 64 ? ulong.MaxValue : (1UL << Doors.Count) - 1;

    // 刻み s の扉 (記録の s 以前で最後の物)
    private static ulong BitsAt(int s)
    {
        for (int i = DoorLog.Count - 1; i >= 0; i--)
            if (DoorLog[i].Tick <= s) return DoorLog[i].Bits;
        return AllOpen;
    }

    // 客: ホストから扉の記録が届いた
    internal static void OnDoors(int tick, ulong bits)
    {
        try
        {
            if (GameClock.ShipGen != _shipGen) { _shipGen = GameClock.ShipGen; ResetShip(); }
            if (_running && tick < _step) LateDoors++;
            AddDoorLog(tick, bits);
        }
        catch (Exception e) { Fail("doors", e); }
    }

    private static void AddDoorLog(int tick, ulong bits)
    {
        int i = DoorLog.Count;
        while (i > 0 && DoorLog[i - 1].Tick > tick) i--;
        DoorLog.Insert(i, (tick, bits));
    }

    // ホストと一人の時: 外壁を掘り抜ける船 (スケルド) で扉を読み、変わったら刻みを押して記録し配る
    private static void PollDoors()
    {
        if (!SolidMap.Breachable) return;
        int now = GameClock.Now;
        if (now < _nextPoll) return;
        if (TerrainSync.IsGuest()) { _nextPoll = now + DoorPoll; return; }
        _nextPoll = now + DoorPoll;
        ListDoors();
        if (Doors.Count == 0) return;
        ulong bits = ReadDoors();
        ulong last = DoorLog.Count > 0 ? DoorLog[DoorLog.Count - 1].Bits : AllOpen;
        if (bits == last) return;
        ushort stamp = GameClock.Stamp;
        AddDoorLog(GameClock.Expand(stamp), bits);
        TerrainSync.BroadcastDoors(stamp, bits);
    }

    private static ulong ReadDoors()
    {
        ulong bits = 0;
        for (int i = 0; i < Doors.Count; i++)
        {
            var d = Doors[i].Door;
            if (d && d.IsOpen) bits |= 1UL << i;
        }
        return bits;
    }

    // 歩ける升と隣へ通れる向き (WaterSim.Rebuild と同じ判定)。閉じた扉の升は閉じる。地図の端の升は閉じる
    private static void Grid()
    {
        Array.Clear(_blocked);
        for (int i = 0; i < DoorCells.Count; i++)
        {
            if ((_doorBits >> i & 1) != 0) continue;
            var (x0, y0, x1, y1) = DoorCells[i];
            for (int y = Math.Max(0, y0); y <= Math.Min(_h - 1, y1); y++)
            for (int x = Math.Max(0, x0); x <= Math.Min(_w - 1, x1); x++)
                _blocked[y * _w + x] = 1;
        }
        int sw = SolidMap.W;
        for (int y = 0; y < _h; y++)
        for (int x = 0; x < _w; x++)
        {
            int k = y * _w + x, bits = 0;
            if (x > 0 && y > 0 && x < _w - 1 && y < _h - 1 && _blocked[k] == 0)
            {
                for (int sy = 0; sy < Sub; sy++)
                for (int sx = 0; sx < Sub; sx++)
                    if (SolidMap.OpenCell((y * Sub + sy) * sw + x * Sub + sx)) bits |= 1 << (sy * Sub + sx);
                if (WaterSim.PopCount(bits) < 4 || !WaterSim.Connected(bits)) bits = 0;
            }
            _sub[k] = (ushort)bits;
        }
        for (int y = 1; y < _h - 1; y++)
        for (int x = 1; x < _w - 1; x++)
        {
            int k = y * _w + x, a = _sub[k], e = 0;
            if (a != 0)
            {
                if (WaterSim.Facing(a, _sub[k + 1], 3, 0, true) >= 2) e |= 1;
                if (WaterSim.Facing(a, _sub[k - 1], 0, 3, true) >= 2) e |= 2;
                if (WaterSim.Facing(a, _sub[k + _w], 3, 0, false) >= 2) e |= 4;
                if (WaterSim.Facing(a, _sub[k - _w], 0, 3, false) >= 2) e |= 8;
            }
            _edge[k] = (byte)e;
        }
    }

    private const int FurnitureLayer = 12;

    // 家具の当たり判定 (層 12) の内側の升。クルーが引っかかるので流れはこれを回り込む。開通と爆発の時だけ作り直す
    private static void Furniture()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Array.Clear(_furn);
        // 地図全体を覆う円で集める (Android の本編の libunity には箱の版が無い)
        float hw = _w * _cell * 0.5f, hh = _h * _cell * 0.5f;
        var mid = new Vector2(_org.x + hw, _org.y + hh);
        foreach (var col in Physics2D.OverlapCircleAll(mid, MathF.Sqrt(hw * hw + hh * hh), 1 << FurnitureLayer))
        {
            if (!col || col.isTrigger) continue;
            var b = col.bounds;
            int x0 = Math.Max(1, CellX(b.min.x)), x1 = Math.Min(_w - 2, CellX(b.max.x));
            int y0 = Math.Max(1, CellY(b.min.y)), y1 = Math.Min(_h - 2, CellY(b.max.y));
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int k = y * _w + x;
                if (_sub[k] == 0 || _furn[k] != 0) continue;
                if (col.OverlapPoint(new Vector2(_org.x + (x + 0.5f) * _cell, _org.y + (y + 0.5f) * _cell))) _furn[k] = 1;
            }
        }
        LastFurnMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static bool Pass(int k, int d)
    {
        int j = k + Nx[d] + Ny[d] * _w;
        int back = d ^ 1;
        return (_edge[k] >> d & 1) != 0 && (_edge[j] >> back & 1) != 0;
    }

    // 斜め: どちらかの回り道が縦横で通れる時だけ
    private static bool PassDiag(int k, int dx, int dy)
    {
        int ix = dx > 0 ? 0 : 1, iy = dy > 0 ? 2 : 3;
        return (Pass(k, ix) && Pass(k + dx, iy)) || (Pass(k, iy) && Pass(k + dy * _w, ix));
    }

    // 範囲を塗り直し、前の範囲の気圧を升の数で混ぜて移す。oldComp/oldPress = 作り直す前 (無ければ全部満タン)
    private static void Regions(int[] oldComp, int[] oldPress)
    {
        int n = _w * _h;
        Array.Fill(_comp, -1);
        var cells = new List<int>();
        var stack = new Stack<int>();
        for (int s = 0; s < n; s++)
        {
            if (_sub[s] == 0 || _comp[s] >= 0) continue;
            int id = cells.Count, count = 0;
            _comp[s] = id;
            stack.Push(s);
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                count++;
                for (int d = 0; d < 4; d++)
                {
                    if (!Pass(k, d)) continue;
                    int j = k + Nx[d] + Ny[d] * _w;
                    if (_comp[j] >= 0) continue;
                    _comp[j] = id;
                    stack.Push(j);
                }
            }
            cells.Add(count);
        }
        _compCells = cells.ToArray();
        var press = new int[_compCells.Length];
        if (oldComp == null) Array.Fill(press, Full);
        else
        {
            var sum = new long[press.Length];
            var cnt = new int[press.Length];
            for (int k = 0; k < n; k++)
            {
                int c = _comp[k], o = oldComp[k];
                if (c < 0 || o < 0) continue;
                sum[c] += oldPress[o];
                cnt[c]++;
            }
            for (int c = 0; c < press.Length; c++) press[c] = cnt[c] > 0 ? (int)(sum[c] / cnt[c]) : Full;
        }
        _press = press;
        Field();
    }

    // 開いている口からの道のり (縦横 2・斜め 3 の最短路を道のりの値ごとの桶で)。Cap より先は塗らない
    private static void Field()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Array.Fill(_dist, Far);
        Array.Fill(_par, -1);
        Array.Clear(_seed);
        var buckets = new List<int>[Cap + 4];
        for (int i = 0; i < buckets.Length; i++) buckets[i] = new List<int>();
        bool any = false;
        for (int bi = 0; bi < Breaches.Count; bi++)
        {
            var br = Breaches[bi];
            br.Comp = -1;
            if (br.Sealed) continue;
            foreach (var (a, b) in br.Lines)
            {
                int x0 = CellX(MathF.Min(a.x, b.x) - SeedReach), x1 = CellX(MathF.Max(a.x, b.x) + SeedReach);
                int y0 = CellY(MathF.Min(a.y, b.y) - SeedReach), y1 = CellY(MathF.Max(a.y, b.y) + SeedReach);
                for (int y = Math.Max(1, y0); y <= Math.Min(_h - 2, y1); y++)
                for (int x = Math.Max(1, x0); x <= Math.Min(_w - 2, x1); x++)
                {
                    int k = y * _w + x;
                    if (_sub[k] == 0 || _dist[k] == 0) continue;
                    var c = new Vector2(_org.x + (x + 0.5f) * _cell, _org.y + (y + 0.5f) * _cell);
                    if (SegDist(c, a, b) > SeedReach) continue;
                    _dist[k] = 0;
                    _seed[k] = bi + 1;
                    buckets[0].Add(k);
                    if (br.Comp < 0) br.Comp = _comp[k];
                    any = true;
                }
            }
        }
        if (any)
            for (int v = 0; v <= Cap; v++)
            {
                var list = buckets[v];
                for (int i = 0; i < list.Count; i++)
                {
                    int k = list[i];
                    if (_dist[k] != v) continue;
                    // 家具の上: 空気は通るので家具の升へは伸ばす (載っている物を引く) が、家具を抜けて床へは伸ばさない (クルーは回り込む)
                    bool onFurn = _furn[k] != 0 && v > 0;
                    for (int d = 0; d < 8; d++)
                    {
                        int dx = Nx[d], dy = Ny[d];
                        bool diag = d >= 4;
                        if (diag ? !PassDiag(k, dx, dy) : !Pass(k, d)) continue;
                        int j = k + dx + dy * _w, nv = v + (diag ? 3 : 2);
                        if (nv > Cap || nv >= _dist[j] || (onFurn && _furn[j] == 0)) continue;
                        _dist[j] = (ushort)nv;
                        _par[j] = k;
                        buckets[nv].Add(j);
                    }
                }
            }
        Grips.Clear();
        if (any) FindGrips();
        Fields++;
        LastFieldMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static bool Solid(int x, int y)
    {
        if (x < 0 || y < 0 || x >= _w || y >= _h) return true;
        int k = y * _w + x;
        return _sub[k] == 0 || _furn[k] != 0;
    }

    // 出っ張った角: 流れの届く升に面した塞がった升のうち、周り 7×7 升の塞がりが少ない物 (まっすぐな壁は約 57%・直角の角は約 33%)。
    // 爆発で縁がギザギザ・斜めでも拾えるように面の形でなく塞がりの割合で見る。尖った物から順に GripSpacing 以上離して取る
    private const int GripR = 3, GripMaxSolid = 20;

    private static void FindGrips()
    {
        var cand = new List<(int N, int K)>();
        for (int y = GripR; y < _h - GripR; y++)
        for (int x = GripR; x < _w - GripR; x++)
        {
            if (!Solid(x, y)) continue;
            bool face = false;
            for (int d = 0; d < 4 && !face; d++)
            {
                int j = (y + Ny[d]) * _w + x + Nx[d];
                face = !Solid(x + Nx[d], y + Ny[d]) && _dist[j] != Far;
            }
            if (!face) continue;
            int n = 0;
            for (int dy = -GripR; dy <= GripR; dy++)
            for (int dx = -GripR; dx <= GripR; dx++)
                if (Solid(x + dx, y + dy)) n++;
            if (n >= 4 && n <= GripMaxSolid) cand.Add((n, y * _w + x)); // 4 升未満は爆発の後の塵
        }
        cand.Sort((a, b) => a.N != b.N ? a.N.CompareTo(b.N) : a.K.CompareTo(b.K));
        foreach (var (_, k) in cand)
        {
            float px = _org.x + (k % _w + 0.5f) * _cell, py = _org.y + (k / _w + 0.5f) * _cell;
            bool near = false;
            foreach (var g in Grips)
                if ((g.x - px) * (g.x - px) + (g.y - py) * (g.y - py) < GripSpacing * GripSpacing) { near = true; break; }
            if (!near) Grips.Add(new Vector2(px, py));
        }
    }

    private static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        float abx = b.x - a.x, aby = b.y - a.y, len = abx * abx + aby * aby;
        float t = len > 1e-6f ? Math.Clamp(((p.x - a.x) * abx + (p.y - a.y) * aby) / len, 0f, 1f) : 0f;
        float qx = a.x + abx * t - p.x, qy = a.y + aby * t - p.y;
        return MathF.Sqrt(qx * qx + qy * qy);
    }

    private static float SegSeg(Vector2 a, Vector2 b, Vector2 c, Vector2 d) =>
        MathF.Min(MathF.Min(SegDist(a, c, d), SegDist(b, c, d)), MathF.Min(SegDist(c, a, b), SegDist(d, a, b)));

    // ── 1 刻み ─────────────────────────────────────────────────────────

    private static void StepOnce()
    {
        bool rebuild = false, field = _sealDirty;
        _sealDirty = false;
        while (Queue.Count > 0 && Queue[0].Tick <= _step)
        {
            var p = Queue[0];
            Queue.RemoveAt(0);
            if (!p.Rebuild) AddMouth(p.Lines, p.Away);
            rebuild = true;
        }
        ulong bits = BitsAt(_step);
        if (bits != _doorBits) { _doorBits = bits; rebuild = true; }
        foreach (var br in Breaches)
            if (!br.Sealed && !_hold && _step - br.Start >= FoamDelay + FoamTime) { br.Sealed = true; field = true; SealSight(br); }
        if (rebuild)
        {
            var oc = (int[])_comp.Clone();
            var op = _press;
            Grid();
            Regions(oc, op);
        }
        else if (field) Field();

        // 気圧: 開いている口のある範囲は抜け、口が無い範囲は戻る
        Span<int> width = _press.Length <= 256 ? stackalloc int[_press.Length] : new int[_press.Length];
        if (_open1024.Length != _press.Length) _open1024 = new int[_press.Length];
        Array.Clear(_open1024);
        foreach (var br in Breaches)
        {
            if (br.Sealed || br.Comp < 0) continue;
            width[br.Comp] = Math.Min(MaxWidth256, width[br.Comp] + br.W256);
            // 泡が吹き付け始めてからふさがるまで、引く強さを開きの残りに比例して 0 へ
            int foam = _step - br.Start - FoamDelay;
            int open = _hold || foam <= 0 ? 1024 : Math.Max(0, 1024 - foam * 1024 / FoamTime);
            _open1024[br.Comp] = Math.Max(_open1024[br.Comp], open);
        }
        bool pulling = false;
        for (int c = 0; c < _press.Length; c++)
        {
            int p = _press[c];
            if (width[c] > 0 && Wind)
            {
                pulling = true; // エアシップは空へ開いても気圧は減らず、口のまわりに風が吹き続ける
            }
            else if (width[c] > 0)
            {
                long dp = (long)p * width[c] * KNum / (KDen * _compCells[c]);
                if (dp < 1 && p > 0) dp = 1;
                _press[c] = _hold ? Full : p > OpenFloor ? (int)Math.Max(OpenFloor, p - dp) : p;
                pulling = true;
            }
            else if (p < Full) _press[c] = Math.Min(Full, p + Recover);
        }
        Pulling = pulling;
        if (_step % 300 == 0) Plugin.Logger.LogInfo($"[Decompression] step={_step} digest={Digest():x8} breaches={Breaches.Count} {PressureText()}");
    }

    // 開いている口の近くなら同じ穴を広げた物 (幅 = 口の端どうしのいちばん遠い 2 点)。それ以外は新しい穴
    private static void AddMouth(List<(Vector2 A, Vector2 B)> lines, Vector2 away)
    {
        if (lines == null || lines.Count == 0) return;
        foreach (var br in Breaches)
        {
            if (br.Sealed) continue;
            bool near = false;
            foreach (var (a, b) in lines)
            {
                foreach (var (c, d) in br.Lines) if (SegSeg(a, b, c, d) <= MergeReach) { near = true; break; }
                if (near) break;
            }
            if (!near) continue;
            br.Lines.AddRange(lines);
            br.W256 = Math.Min(MaxWidth256, (int)(Span(br.Lines) * 256f));
            return;
        }
        int w = (int)(Span(lines) * 256f);
        if (w < MinWidth256) return;
        var nb = new Breach { Start = _step, W256 = Math.Min(MaxWidth256, w), Away = away };
        nb.Lines.AddRange(lines);
        Breaches.Add(nb);
    }

    // 口の線の端どうしのいちばん遠い 2 点の距離
    private static float Span(List<(Vector2 A, Vector2 B)> lines)
    {
        float best = 0f;
        for (int i = 0; i < lines.Count * 2; i++)
        for (int j = i + 1; j < lines.Count * 2; j++)
        {
            Vector2 p = (i & 1) == 0 ? lines[i >> 1].A : lines[i >> 1].B, q = (j & 1) == 0 ? lines[j >> 1].A : lines[j >> 1].B;
            best = MathF.Max(best, (p - q).sqrMagnitude);
        }
        return MathF.Sqrt(best);
    }

    internal static uint Digest()
    {
        uint acc = (uint)_step * 2654435761u + (uint)Breaches.Count * 40503u ^ (uint)_doorBits ^ (uint)(_doorBits >> 32) * 0x27D4EB2Fu;
        for (int c = 0; c < _press.Length; c++)
        {
            if (_press[c] == Full) continue;
            uint v = (uint)c * 0x9E3779B1u ^ (uint)_press[c] * 0x85EBCA77u;
            v ^= v >> 15; v *= 0xC2B2AE3Du; v ^= v >> 13;
            acc += v;
        }
        foreach (var br in Breaches) acc = acc * 31u + (uint)br.W256 + (br.Sealed ? 7u : 0u);
        return acc;
    }

    private static string PressureText()
    {
        var sb = new System.Text.StringBuilder();
        for (int c = 0; c < _press.Length; c++)
        {
            if (_press[c] == Full) continue;
            sb.Append($" r{c}={_press[c] * 100f / Full:0.0}%/{_compCells[c] / 16f:0}u2");
        }
        return sb.Length == 0 ? "all full" : sb.ToString().Trim();
    }

    // ── 引く強さ ───────────────────────────────────────────────────────

    // 道のり (1 単位 = PerUnit) → 満タン時の引く強さ (歩く速さの倍)。口の際 1.6 / 3 で 0.6 / 6 で 0.15 / 10 で 0
    private static float Strength(int d)
    {
        float u = d / (float)PerUnit;
        if (Wind) return u <= 3f ? WindMul : u <= 4f ? WindMul * (4f - u) : 0f;
        if (u <= 3f) return 1.6f - u * (1.0f / 3f);
        if (u <= 6f) return 0.6f - (u - 3f) * (0.45f / 3f);
        if (u <= 10f) return 0.15f - (u - 6f) * (0.15f / 4f);
        return 0f;
    }

    // 引く強さに効く気圧 = 気圧 × 口の開き具合 (泡が狭めた分だけ弱まる)
    private static int EffPress(int c) => c < _open1024.Length ? (int)((long)_press[c] * _open1024[c] >> 10) : _press[c];

    // 口から吹き出す強さ (0..1・演出用)。ふさがった口は 0
    internal static float MouthPull(Breach br) =>
        br.Sealed || br.Comp < 0 || _press == null || br.Comp >= _press.Length ? 0f : (float)EffPress(br.Comp) / Full;

    // 口の開き具合 (1 = 全開・泡が吹き付け始めてからふさがるまでに 0 へ・演出用)
    internal static float Open(Breach br)
    {
        if (br.Sealed) return 0f;
        int foam = _step - br.Start - FoamDelay;
        return _hold || foam <= 0 ? 1f : Math.Max(0f, 1f - foam / (float)FoamTime);
    }

    // 点での流れ (向き × 強さ・歩く速さの倍)。引かない所は false
    internal static bool PullAt(Vector2 p, out Vector2 dir, out float mul, out int dist)
    {
        dir = default; mul = 0f; dist = Far;
        if (!_running || !Pulling) return false;
        int x = CellX(p.x), y = CellY(p.y);
        if (x < 1 || y < 1 || x >= _w - 1 || y >= _h - 1) return false;
        int k = y * _w + x;
        dist = _dist[k];
        if (dist == Far || _comp[k] < 0) return false;
        mul = Strength(dist) * EffPress(_comp[k]) / Full;
        if (mul <= 0f) return false;
        // 向き: 口へ向かう道を 3 升先まで辿った点へ。口の際なら口の線のいちばん近い点へ
        int t = k;
        for (int i = 0; i < 3 && _par[t] >= 0; i++) t = _par[t];
        Vector2 to;
        if (_par[t] < 0 && _dist[t] == 0) to = NearestMouth(p);
        else to = FxMath.V2(_org.x + (t % _w + 0.5f) * _cell, _org.y + (t / _w + 0.5f) * _cell);
        float dx = to.x - p.x, dy = to.y - p.y, len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-4f) return false;
        dir = FxMath.V2(dx / len, dy / len);
        return true;
    }

    // 開いている口の線までのいちばん近い距離と点 (口が無ければ float.MaxValue)
    internal static float MouthDist(Vector2 p, out Vector2 at)
    {
        at = NearestMouth(p);
        bool any = false;
        foreach (var br in Breaches) if (!br.Sealed) { any = true; break; }
        if (!_running || !any) return float.MaxValue;
        float dx = at.x - p.x, dy = at.y - p.y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    internal static List<Vector2> GripPoints => Grips;
    internal static List<Breach> OpenedBreaches => Breaches;

    // 船の中の歩ける升か (口の線のどちら側が船の中かを見る用)
    internal static bool InsideAt(Vector2 p)
    {
        if (!_running) return false;
        int x = CellX(p.x), y = CellY(p.y);
        if (x < 0 || y < 0 || x >= _w || y >= _h) return false;
        return _sub[y * _w + x] != 0;
    }

    private static Vector2 NearestMouth(Vector2 p)
    {
        float best = float.MaxValue;
        Vector2 q = p;
        foreach (var br in Breaches)
        {
            if (br.Sealed) continue;
            foreach (var (a, b) in br.Lines)
            {
                float abx = b.x - a.x, aby = b.y - a.y, len = abx * abx + aby * aby;
                float t = len > 1e-6f ? Math.Clamp(((p.x - a.x) * abx + (p.y - a.y) * aby) / len, 0f, 1f) : 0f;
                var c = FxMath.V2(a.x + abx * t, a.y + aby * t);
                float d = (c.x - p.x) * (c.x - p.x) + (c.y - p.y) * (c.y - p.y);
                if (d < best) { best = d; q = c; }
            }
        }
        return q;
    }

    // ── 毎フレーム ─────────────────────────────────────────────────────

    public static void Tick()
    {
        try { TickCore(); }
        catch (Exception e) { Fail("tick", e); }
    }

    // 船が替わったら片付ける (TerrainStep が刻みを進める前にも呼ぶ)
    internal static void CheckShip()
    {
        if (GameClock.ShipGen != _shipGen) { _shipGen = GameClock.ShipGen; ResetShip(); }
    }

    private static void TickCore()
    {
        CheckShip();
        PollDoors();
        // 物や水が動いている間は TerrainStep がそれらと 1 刻みずつ揃えて進める (物が口をふさいだ刻み・水が読む場を全員で同じにする)
        if (!_running || PropSim.Drives || WaterSim.Running) return;
        int target = GameClock.Now - Delay;
        if (_step >= target) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int n = 0;
        while (_step < target && n < MaxStepsPerFrame)
        {
            StepOnce();
            _step++;
            n++;
        }
        LastStepMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / n;
    }

    // 物の確定の刻み s の前に呼ぶ: s まで進めて、s の後の気圧と流れにする
    internal static void StepThrough(int s)
    {
        try
        {
            while (_running && _step <= s)
            {
                StepOnce();
                _step++;
            }
        }
        catch (Exception e) { Fail("step", e); }
    }

    // ── 物から読む (整数だけ・全員で同じ) ───────────────────────────────

    internal const int UnitDist = PerUnit;

    // SolidMap の升 (sx, sy) の流れ: 道のり・気圧・口へ向かう向き (升の数)。引かない所は false
    internal static bool FlowAt(int sx, int sy, out int dist, out int press, out int dx, out int dy)
    {
        dist = Far; press = 0; dx = 0; dy = 0;
        if (!_running || !Pulling) return false;
        int x = sx / Sub, y = sy / Sub;
        if (sx < 0 || sy < 0 || x < 1 || y < 1 || x >= _w - 1 || y >= _h - 1) return false;
        int k = y * _w + x;
        if (_dist[k] == Far || _comp[k] < 0) return false;
        dist = _dist[k];
        press = EffPress(_comp[k]);
        int t = k;
        for (int i = 0; i < 3 && _par[t] >= 0; i++) t = _par[t];
        dx = t % _w - x;
        dy = t / _w - y;
        return true;
    }

    // 道のり → 満タン時の引く強さ (/1024)。Strength と同じ折れ線
    internal static int Strength1024(int d)
    {
        if (Wind) return d <= 3 * PerUnit ? Wind1024 : d <= 4 * PerUnit ? Wind1024 * (4 * PerUnit - d) / PerUnit : 0;
        if (d <= 3 * PerUnit) return 1638 - d * 1024 / (3 * PerUnit);
        if (d <= 6 * PerUnit) return 614 - (d - 3 * PerUnit) * 461 / (3 * PerUnit);
        if (d <= 10 * PerUnit) return 154 - (d - 6 * PerUnit) * 154 / (4 * PerUnit);
        return 0;
    }

    // 升から口の際まで道を辿り、その口の番号と幅 (×256)。開いた口が無ければ −1
    internal static int MouthOf(int sx, int sy, out int w256)
    {
        w256 = 0;
        if (!_running) return -1;
        int x = sx / Sub, y = sy / Sub;
        if (sx < 0 || sy < 0 || x < 1 || y < 1 || x >= _w - 1 || y >= _h - 1) return -1;
        int k = y * _w + x;
        if (_dist[k] == Far) return -1;
        for (int i = 0; i < Cap && _par[k] >= 0; i++) k = _par[k];
        int b = _seed[k] - 1;
        if (b < 0 || Breaches[b].Sealed) return -1;
        w256 = Breaches[b].W256;
        return b;
    }

    private const float CoverTouch = 0.25f;   // 物の箱が口の線からこの距離以内なら触れている
    private const float CoverShare = 0.85f;   // 口の幅のこの割合以上を覆えば口をふさぐ

    // 中心 (cx, cy)・半分の大きさ (hx, hy) の箱が口 b の線に触れて、幅の大半を覆っているか
    internal static bool Covers(int b, float cx, float cy, float hx, float hy)
    {
        if (b < 0 || b >= Breaches.Count) return false;
        float total = 0f, covered = 0f;
        foreach (var (a, e) in Breaches[b].Lines)
        {
            float dx = e.x - a.x, dy = e.y - a.y, len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1e-4f) continue;
            float ux = dx / len, uy = dy / len;
            float along = hx * MathF.Abs(ux) + hy * MathF.Abs(uy);
            float across = hx * MathF.Abs(uy) + hy * MathF.Abs(ux);
            float rx = cx - a.x, ry = cy - a.y;
            total += len;
            if (MathF.Abs(rx * -uy + ry * ux) - across > CoverTouch) continue;
            float t = rx * ux + ry * uy;
            covered += MathF.Max(0f, MathF.Min(len, t + along) - MathF.Max(0f, t - along));
        }
        return total > 0f && covered >= total * CoverShare;
    }

    // 泡でふさがった口で視界を止める (泡の奥まで。泡の絵と同じく、宇宙まで・床が先なら床の手前まで)
    private static void SealSight(Breach br)
    {
        foreach (var (a, b) in br.Lines)
        {
            float dx = b.x - a.x, dy = b.y - a.y, len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 0.05f) continue;
            var n = new Vector2(-dy / len, dx / len);
            var mid = new Vector2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f);
            if (!SolidMap.SkyAhead(mid, n, TerrainDamage.BreachReach) && SolidMap.SkyAhead(mid, -n, TerrainDamage.BreachReach)) n = -n;
            float depth = SolidMap.SkyDistance(mid.x, mid.y, n.x, n.y, TerrainDamage.HullReach + 1f);
            float reach = depth < 0f ? -depth - 0.2f : depth > 0f ? depth : SealDepth;
            WallBody.BuildSeal(a, b, n, Math.Max(0.1f, reach));
        }
    }

    // 口より大きい物が口に着いた (確定側だけ)。次の刻みから引かない
    internal static void SealByProp(int b)
    {
        if (b < 0 || b >= Breaches.Count || Breaches[b].Sealed) return;
        Breaches[b].Sealed = true;
        Breaches[b].ByProp = true;
        _sealDirty = true;
    }

    // 閉じた扉の升か (SolidMap の升)。物は扉を抜けない
    internal static bool DoorAt(int sx, int sy)
    {
        if (!_running) return false;
        int x = sx / Sub, y = sy / Sub;
        return x < _w && y < _h && _blocked[y * _w + x] != 0;
    }

    // 船が替わった: 扉の記録も捨てる
    private static void ResetShip()
    {
        Reset();
        DoorLog.Clear();
        _nextPoll = 0;
        LateDoors = 0;
        _hold = false;
    }

    internal static void Reset()
    {
        _running = false;
        Pulling = false;
        _sub = null; _edge = null; _blocked = null; _furn = null; _comp = null; _dist = null; _par = null; _seed = null;
        _compCells = Array.Empty<int>();
        _press = Array.Empty<int>();
        _open1024 = Array.Empty<int>();
        Doors.Clear();
        DoorCells.Clear();
        _doorsListed = false;
        _sealDirty = false;
        Breaches.Clear();
        Queue.Clear();
        Grips.Clear();
        _step = 0;
        Late = 0;
        Fields = 0;
        CrewPull.Speed = 0f;
    }

    internal static void Register()
    {
        TestBridge.Register("decomp", "[probe [x y] | area [x y] | breach x1 y1 x2 y2 | doors open|close | fill | hold [off] | reset] 船外への吸い出しの気圧と流れ: 刻み・範囲ごとの気圧・口 (probe = その点 (省略で自分) の道のり・引く強さ・向き / area = その点の範囲の広さ・扉は今の開け閉め / breach = 爆発なしに口を自分の手元だけに置く / doors = 全部の扉を自分の手元だけで開け閉め / fill = 気圧を自分の手元だけで満タンに戻す / hold = 自分の手元だけ口をふさがず気圧を満タンのまま)", (args, reply) =>
        {
            var a = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string cmd = a.Length > 0 ? a[0] : "";
            if (cmd == "reset") { Reset(); reply("OK decomp reset"); return; }
            if (cmd == "hold")
            {
                _hold = a.Length < 2 || a[1] != "off";
                reply($"OK decomp hold={_hold} (this device only)");
                return;
            }
            if (cmd == "fill")
            {
                for (int c = 0; c < _press.Length; c++) _press[c] = Full;
                reply($"OK decomp fill regions={_press.Length} (this device only)");
                return;
            }
            if (cmd == "doors")
            {
                var ship = ShipStatus.Instance;
                if (!ship || a.Length < 2) { reply("ERR decomp doors open|close"); return; }
                bool open = a[1] == "open";
                foreach (var d in ship.AllDoors) if (d) d.SetDoorway(open);
                reply($"OK decomp doors {(open ? "open" : "close")} n={ship.AllDoors.Count}");
                return;
            }
            if (cmd == "breach")
            {
                if (a.Length < 5) { reply("ERR decomp breach x1 y1 x2 y2"); return; }
                var p1 = new Vector2(Num(a[1]), Num(a[2]));
                var p2 = new Vector2(Num(a[3]), Num(a[4]));
                Open(GameClock.Now, new List<(Vector2, Vector2)> { (p1, p2) }, default);
                reply(_running ? $"OK decomp breach queued at {GameClock.Now} width={(p2 - p1).magnitude:0.00}" : "ERR decomp no solid map");
                return;
            }
            if (cmd is "probe" or "area")
            {
                Vector2 p;
                if (a.Length >= 3) p = new Vector2(Num(a[1]), Num(a[2]));
                else if (PlayerControl.LocalPlayer) p = PlayerControl.LocalPlayer.GetTruePosition();
                else { reply("ERR decomp no player"); return; }
                if (cmd == "area") { reply(Area(p)); return; }
                if (!_running) { reply("OK decomp off"); return; }
                bool on = PullAt(p, out var dir, out float mul, out int dist);
                int x = CellX(p.x), y = CellY(p.y), k = y * _w + x, c = x >= 0 && y >= 0 && x < _w && y < _h ? _comp[k] : -1;
                reply($"OK decomp probe at {TestBridge.F(p.x)} {TestBridge.F(p.y)} region={c} furn={(c >= 0 && _furn[k] != 0)} press={(c >= 0 ? _press[c] * 100f / Full : 0f):0.0}% dist={(dist == Far ? "far" : (dist / (float)PerUnit).ToString("0.00"))} pull={on} mul={mul:0.000} dir={dir.x:0.00},{dir.y:0.00} speed={CrewPull.Speed:0.00}");
                return;
            }
            if (!_running) { reply($"OK decomp off doorLog={DoorLog.Count} breachable={SolidMap.BreachableHull} {GameClock.Describe()}"); return; }
            var sb = new System.Text.StringBuilder();
            foreach (var br in Breaches)
            {
                var (l0, l1) = br.Lines[0];
                sb.Append($" [start={br.Start} age={_step - br.Start} w={br.W256 / 256f:0.00} lines={br.Lines.Count} first={l0.x:0.00},{l0.y:0.00}-{l1.x:0.00},{l1.y:0.00} region={br.Comp} sealed={br.Sealed}{(br.ByProp ? "(prop)" : "")}]");
            }
            reply($"OK decomp step={_step} target={GameClock.Now - Delay} late={Late} lateDoors={LateDoors} doorLog={DoorLog.Count} queue={Queue.Count} pulling={Pulling} doors={Doors.Count} open={System.Numerics.BitOperations.PopCount(_doorBits)} regions={_press.Length} fields={Fields} fieldMs={LastFieldMs:0.00} furnMs={LastFurnMs:0.00} stepMs={LastStepMs:0.000} digest={Digest():x8} {PressureText()} breaches={Breaches.Count}{sb}");
        });
    }

    private static float Num(string s) =>
        float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;

    // その点の範囲の広さ (単位²)。動いていない時は今の地図と扉で作って捨てる
    private static string Area(Vector2 p)
    {
        if (!_running)
        {
            if (!SolidMap.Ensure() || !SolidMap.Valid) return "ERR decomp no solid map";
            _step = GameClock.Now;
            Build();
            string s = AreaText(p);
            Reset();
            return s;
        }
        return AreaText(p);
    }

    private static string AreaText(Vector2 p)
    {
        int x = CellX(p.x), y = CellY(p.y);
        if (x < 0 || y < 0 || x >= _w || y >= _h) return "ERR decomp outside the map";
        int c = _comp[y * _w + x];
        int total = 0;
        foreach (int n in _compCells) total += n;
        return $"OK decomp area region={c} cells={(c >= 0 ? _compCells[c] : 0)} area={(c >= 0 ? _compCells[c] / 16f : 0f):0.0}u2 regions={_compCells.Length} allOpen={total / 16f:0.0}u2 doors={Doors.Count} open={System.Numerics.BitOperations.PopCount(_doorBits)}";
    }
}

// 流れに乗る自分の体: 本編の歩行 (入力 → 速さ) の後で流れの速さを足す。当たり判定は物理が見るので壁や口で止まる
[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.FixedUpdate))]
internal static class CrewPull
{
    internal static float Speed; // 歩く速さ (引き始めに 1 回読む)

    public static void Postfix(PlayerPhysics __instance)
    {
        if (!Decompression.Pulling && !CrewGrip.Active) return;
        if (!__instance.AmOwner) return;
        var pc = __instance.myPlayer;
        if (!pc || !pc.CanMove || pc.inVent || pc.Data == null || pc.Data.IsDead)
        {
            CrewGrip.Cancel();
            return;
        }
        var pos = pc.GetTruePosition();
        bool on = Decompression.PullAt(pos, out var dir, out float mul, out int dist);
        if (Speed <= 0f) Speed = __instance.TrueSpeed;
        var body = __instance.body;
        if (CrewGrip.Physics(pc, body, pos, on, dir, mul, dist) || !on) return;
        var v = body.velocity;
        float s = mul * Speed;
        body.velocity = FxMath.V2(v.x + dir.x * s, v.y + dir.y * s);
    }
}
