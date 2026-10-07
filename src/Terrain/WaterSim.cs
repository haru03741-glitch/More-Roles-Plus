using System;
using System.Collections.Generic;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 床の上の水の計算 (真上から見た浅い水・「仮想の管」型)。升 = 0.25 単位 (SolidMap の 4×4 升)。
// 升ごとに水の高さを持ち、隣との高さの差で管に流れが溜まって次の刻みで水が移る (勢いが残るので押し寄せて跳ね返る)。
// 壁 (歩けない升) へは流れない。壊した穴は SolidMap が開くので、その刻みで升を作り直すと水が通る。
// 机などの家具は床の上に立っているので水は下を通る (絵だけ型抜きする)。
//
// 全員の手元でぴったり同じ結果にするため:
// - 整数だけで計算する (三角関数・端末で丸めの違う小数は使わない)。向きは整数の回転の表、乱数は使わない。
// - 試合の刻み (GameClock) の Delay 刻み遅れで回し、壊した結果に押された刻みが来た時に水を入れる・升を作り直す。
//   通信の遅れが Delay 以内なら全員同じ刻みで入る。遅れて着いた物は着いた刻みで入れて数える (Late)。
// - 刻みの番号でだけ進める (1 フレームに MaxStepsPerFrame 刻みまで追いつく)。会議・画面の更新・Time を読まない。
// 水のある升だけを計算し、無くなった升は外す。指紋 (Digest) は升の順番に依らない足し算のハッシュ
internal static class WaterSim
{
    public const int Delay = 18;              // 0.6 秒 (GameClock.Hz = 30)
    public const int Sub = 4;                 // 水の升 1 辺 = SolidMap の升 Sub 個
    public const int Full = 1024;             // 深さ 1 単位分の水の量
    private const int MaxStepsPerFrame = 20;
    private const int Gain = 56;              // 高さの差 → 流れ (/256)
    private const int Damp = 236;             // 前の刻みの流れを残す割合 (/256)
    private const int DryHold = 48;           // 乾いた升へは、これより高い時だけ流れ出す (水たまりの縁が止まる)
    private const int WetHold = 3;            // 濡れた升の間で流れない小さな差
    private const int MinDepth = 6;           // これより浅い升は少しずつ乾く

    // 漏れ: 開始から FullSteps は全開・その後 FadeSteps で止まる。最初の BurstSteps は多く遠い
    public const int FullSteps = 300, FadeSteps = 150, BurstSteps = 15;
    private const int MouthQ = 1;             // 口を壁の線から出す距離 (1/4 升)

    // 噴き出しの粒 (整数・升 = 256・1 単位 = 1024)。裂け目から扇の範囲へ放物線で飛び、壁に当たると止まって垂れ、
    // 床に落ちた升に水 Mass を足す。乱数は漏れごとの種から作る xorshift (全員同じ並び)
    public const int Unit = 1024;
    public const int MouthH = 256;            // 裂け目の高さ (0.25 単位)
    private const int Gravity = 12;           // 1 刻みあたりの縦の速さの減り
    private const int FlightMin = 10, FlightMax = 15; // 床に落ちるまでの刻み
    private const int ReachMin = 384, ReachMax = 1536; // 届く距離 (0.375〜1.5 単位・勢い 0〜1)
    private const int PerStep = 6;            // 全開で 1 刻みに出す粒 (片側)
    private const int Mass = 18;              // 粒 1 つの水の量
    public const int MaxParticles = 1024;
    // 扇の向き: −35°〜+35° を 5° 刻み (cos/sin ×256)。普段は ±20° まで・破裂の間は ±35° まで
    private static readonly int[] FanCos = { 210, 222, 232, 241, 247, 252, 255, 256, 255, 252, 247, 241, 232, 222, 210 };
    private static readonly int[] FanSin = { -147, -128, -108, -88, -66, -44, -22, 0, 22, 44, 66, 88, 108, 128, 147 };

    // 粒 (絵は WaterSpray が読む)。Px/Py/Ph = 今・Ox/Oy/Oh = 1 刻み前・Src = どの漏れか
    internal static readonly int[] Px = new int[MaxParticles], Py = new int[MaxParticles], Ph = new int[MaxParticles];
    internal static readonly int[] Ox = new int[MaxParticles], Oy = new int[MaxParticles], Oh = new int[MaxParticles];
    private static readonly int[] Vx = new int[MaxParticles], Vy = new int[MaxParticles], Vh = new int[MaxParticles];
    internal static readonly bool[] Stuck = new bool[MaxParticles];
    internal static int Particles { get; private set; }
    // 今の刻みに床へ落ちた粒の位置 (升 ×256)。絵が波紋を出す
    internal static readonly List<(int X, int Y)> Landed = new();

    private static readonly int[] Nx = { 1, -1, 0, 0 };
    private static readonly int[] Ny = { 0, 0, 1, -1 };
    private static readonly int[] Opp = { 1, 0, 3, 2 };

    private sealed class Source
    {
        public int Start;
        public uint Rng;
        public int Acc;
        public int Mx, My;          // 口の位置 (升 ×256)
        public int Dx, Dy;          // 向き (長さ ≈256)
        public bool Both;           // 両側へ噴く
        public Vector2 At, N;       // 絵のための元の位置と向き
        public ushort Seed;
        public bool Shown;
    }

    private struct Pending
    {
        public int Tick;
        public bool Leak;
        public Source Src;
        public int Cx, Cy, R;       // 升を作り直す範囲 (升)
    }

    private static int _shipGen;
    private static bool _ready;
    private static int _w, _h;
    private static Vector2 _org;
    private static float _cell;      // 升の大きさ (単位)
    private static byte[] _open;
    private static ushort[] _sub;    // 升の中の歩ける升 (4×4 のビット)
    private static byte[] _edge;     // 隣へ流れてよい向き (ビット 0..3 = Nx/Ny の並び)
    private static int[] _hgt;
    private static int[] _flux;      // 升 × 4 向きの流れ出し
    private static byte[] _inList;
    private static readonly List<int> Active = new();
    private static readonly List<Pending> Queue = new();
    private static readonly List<Source> Sources = new();
    private static int _step;
    private static bool _running;

    // 確認用
    internal static int Late { get; private set; }
    internal static int Steps { get; private set; }
    internal static uint LastDigest { get; private set; }
    internal static double LastStepMs { get; private set; }

    // 絵 (WaterArt) が読む
    internal static int W => _w;
    internal static int H => _h;
    internal static Vector2 Origin => _org;
    internal static float Cell => _cell;
    internal static int Height(int k) => _hgt[k];
    internal static bool Open(int k) => _open[k] != 0;
    internal static bool Ready => _ready;
    internal static int Step => _step;
    // 絵のタイル (TileCells 升四方) の描き直しの印
    public const int TileCells = 16;
    private static int _tw, _th;
    private static bool[] _tileDirty;
    internal static readonly List<int> DirtyTiles = new();
    internal static int TilesW => _tw;
    internal static event Action<Vector2, Vector2, ushort, bool> LeakStarted; // 噴き出しを見せ始める

    // ── 入口 ───────────────────────────────────────────────────────────

    // 全ての破壊の結果 (全員の手元)。その刻みで水の升を作り直す
    public static void OnApplied(in ResolvedDamage r)
    {
        try { Enqueue(r); }
        catch (Exception e) { Fail("apply", e); }
    }

    private static void Enqueue(in ResolvedDamage r)
    {
        if (!_running) return;
        int tick = GameClock.Expand(r.Tick);
        float rad = r.Kind == DamageKind.Explosion ? r.Size + 0.6f : 2f;
        int cx = (int)MathF.Floor((r.Position.x - _org.x) / _cell), cy = (int)MathF.Floor((r.Position.y - _org.y) / _cell);
        Enqueue(new Pending { Tick = tick, Cx = cx, Cy = cy, R = (int)(rad / _cell) + 2 });
    }

    // 漏れ (WaterLeak から)。at = 壊れた壁の線の上・n = 噴く側の法線・both = 両側へ
    public static void AddLeak(ushort stamp, Vector2 at, Vector2 n, bool both, ushort seed)
    {
        try { Add(stamp, at, n, both, seed); }
        catch (Exception e) { Fail("leak", e); }
    }

    // 計算が例外で止まったら水を全部片付けて止める (毎フレーム同じ例外が出続けないように)
    private static void Fail(string where, Exception e)
    {
        Plugin.Logger.LogError($"[WaterSim] {where}: {e}");
        Reset();
    }

    private static void Add(ushort stamp, Vector2 at, Vector2 n, bool both, ushort seed)
    {
        if (!EnsureGrid()) return;
        int tick = GameClock.Expand(stamp);
        if (!_running)
        {
            _running = true;
            _step = Math.Min(tick, GameClock.Now - Delay);
        }
        int dx = (int)MathF.Round(n.x * 256f), dy = (int)MathF.Round(n.y * 256f);
        int mx = (int)MathF.Floor((at.x - _org.x) / _cell * 256f) + dx * MouthQ / 4;
        int my = (int)MathF.Floor((at.y - _org.y) / _cell * 256f) + dy * MouthQ / 4;
        var src = new Source { Start = tick, Mx = mx, My = my, Dx = dx, Dy = dy, Both = both, At = at, N = n, Seed = seed, Rng = 0x9E3779B9u ^ seed * 2654435761u };
        Enqueue(new Pending { Tick = tick, Leak = true, Src = src });
    }

    private static void Enqueue(Pending p)
    {
        if (p.Tick < _step) { Late++; p.Tick = _step; }
        // 刻みの順 (同じ刻みは着いた順 = 連番の順)
        int i = Queue.Count;
        while (i > 0 && Queue[i - 1].Tick > p.Tick) i--;
        Queue.Insert(i, p);
    }

    // ── 升 ─────────────────────────────────────────────────────────────

    private static bool EnsureGrid()
    {
        if (_ready) return true;
        if (!SolidMap.Ensure() || !SolidMap.Valid) return false;
        _w = SolidMap.W / Sub;
        _h = SolidMap.H / Sub;
        _org = SolidMap.Origin;
        _cell = Sub / SolidMap.Ppu;
        int n = _w * _h;
        _open = new byte[n];
        _sub = new ushort[n];
        _edge = new byte[n];
        _hgt = new int[n];
        _flux = new int[n * 4];
        _inList = new byte[n];
        _tw = (_w + TileCells - 1) / TileCells;
        _th = (_h + TileCells - 1) / TileCells;
        _tileDirty = new bool[_tw * _th];
        Rebuild(0, 0, _w - 1, _h - 1);
        DirtyTiles.Clear();
        Array.Clear(_tileDirty);
        _ready = true;
        return true;
    }

    // 升の 4×4 の SolidMap の升 (歩けるか) を 16 ビットに詰め、4 つ以上が歩けて 1 つにつながっていれば開いている
    // (壁の際まで水が届く)。SolidMap の壁の線は 1 升ほどの細さなので、升の中を壁が通って歩ける所が分かれた升は閉じる
    // (開けると壁の両側が 1 つの升でつながり、水が壁をすり抜ける)。
    // 隣の升へ流れてよいのは、境を挟んで向かい合う歩ける升が 2 組以上ある時だけ (_edge のビット = Nx/Ny の向き)。
    // 地図の端の升は閉じる
    private static void Rebuild(int x0, int y0, int x1, int y1)
    {
        x0 = Math.Max(1, x0); y0 = Math.Max(1, y0);
        x1 = Math.Min(_w - 2, x1); y1 = Math.Min(_h - 2, y1);
        int sw = SolidMap.W;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int bits = 0;
            for (int sy = 0; sy < Sub; sy++)
            for (int sx = 0; sx < Sub; sx++)
                if (SolidMap.OpenCell((y * Sub + sy) * sw + x * Sub + sx)) bits |= 1 << (sy * Sub + sx);
            if (PopCount(bits) < 4 || !Connected(bits)) bits = 0;
            int k = y * _w + x;
            _sub[k] = (ushort)bits;
            byte open = bits != 0 ? (byte)1 : (byte)0;
            if (open == _open[k]) continue;
            _open[k] = open;
            if (open == 0 && _hgt[k] != 0)
            {
                // 閉じた升の水は消さずに開いている隣へ寄せる (量を保つ)
                int to = -1;
                for (int d = 0; d < 4 && to < 0; d++) { int j = k + Nx[d] + Ny[d] * _w; if (_open[j] != 0) to = j; }
                if (to >= 0) { _hgt[to] += _hgt[k]; Wake(to); }
                _hgt[k] = 0;
            }
            for (int d = 0; d < 4; d++) _flux[k * 4 + d] = 0;
            if (open != 0) Wake(k);
            for (int d = 0; d < 4; d++) Wake(k + Nx[d] + Ny[d] * _w);
            Mark(k);
        }
        // 境の通れる向き (作り直した範囲と、その外周 1 升)
        for (int y = Math.Max(1, y0 - 1); y <= Math.Min(_h - 2, y1 + 1); y++)
        for (int x = Math.Max(1, x0 - 1); x <= Math.Min(_w - 2, x1 + 1); x++)
        {
            int k = y * _w + x;
            int e = 0;
            int a = _sub[k];
            if (a != 0)
            {
                if (Facing(a, _sub[k + 1], 3, 0, true) >= 2) e |= 1;        // +x
                if (Facing(a, _sub[k - 1], 0, 3, true) >= 2) e |= 2;        // −x
                if (Facing(a, _sub[k + _w], 3, 0, false) >= 2) e |= 4;      // +y
                if (Facing(a, _sub[k - _w], 0, 3, false) >= 2) e |= 8;      // −y
            }
            _edge[k] = (byte)e;
        }
    }

    // 2 つの升の境で向かい合う歩ける升の組の数。column = true なら a の列 ia と b の列 ib、false なら行
    private static int Facing(int a, int b, int ia, int ib, bool column)
    {
        int n = 0;
        for (int i = 0; i < Sub; i++)
        {
            int pa = column ? i * Sub + ia : ia * Sub + i;
            int pb = column ? i * Sub + ib : ib * Sub + i;
            if ((a >> pa & 1) != 0 && (b >> pb & 1) != 0) n++;
        }
        return n;
    }

    private static int PopCount(int v)
    {
        int c = 0;
        while (v != 0) { v &= v - 1; c++; }
        return c;
    }

    // 4×4 の歩ける升が上下左右で 1 つにつながっているか
    private static bool Connected(int bits)
    {
        int first = bits & -bits;
        int seen = first, frontier = first;
        while (frontier != 0)
        {
            int grow = 0;
            for (int i = 0; i < Sub * Sub; i++)
            {
                if ((frontier >> i & 1) == 0) continue;
                int x = i % Sub, y = i / Sub;
                if (x > 0) grow |= 1 << (i - 1);
                if (x < Sub - 1) grow |= 1 << (i + 1);
                if (y > 0) grow |= 1 << (i - Sub);
                if (y < Sub - 1) grow |= 1 << (i + Sub);
            }
            grow &= bits & ~seen;
            seen |= grow;
            frontier = grow;
        }
        return seen == bits;
    }

    // k から向き d (Nx/Ny の並び) の隣へ流れてよいか
    internal static bool Passable(int k, int d) => (_edge[k] >> d & 1) != 0 && (_edge[k + Nx[d] + Ny[d] * _w] >> Opp[d] & 1) != 0;

    // 升の水が変わった: その升と縁を共有するタイルを描き直す (絵は隣の升も見て縁をなめらかにする)
    private static void Mark(int k)
    {
        int x = k % _w, y = k / _w;
        MarkTile(x / TileCells, y / TileCells);
        int lx = x % TileCells, ly = y % TileCells;
        if (lx == 0) MarkTile(x / TileCells - 1, y / TileCells);
        else if (lx == TileCells - 1) MarkTile(x / TileCells + 1, y / TileCells);
        if (ly == 0) MarkTile(x / TileCells, y / TileCells - 1);
        else if (ly == TileCells - 1) MarkTile(x / TileCells, y / TileCells + 1);
    }

    private static void MarkTile(int tx, int ty)
    {
        if (tx < 0 || ty < 0 || tx >= _tw || ty >= _th) return;
        int t = ty * _tw + tx;
        if (_tileDirty[t]) return;
        _tileDirty[t] = true;
        DirtyTiles.Add(t);
    }

    // 絵が描き直したタイルの印を消す
    internal static void Clean(int t) => _tileDirty[t] = false;

    // 地図の端の升 (外周 1 升) は計算に入れない (隣を読む時に配列の外へ出る)
    private static void Wake(int k)
    {
        if (_inList[k] != 0) return;
        int x = k % _w, y = k / _w;
        if (x < 1 || y < 1 || x >= _w - 1 || y >= _h - 1) return;
        _inList[k] = 1;
        Active.Add(k);
    }

    // ── 1 刻み ─────────────────────────────────────────────────────────

    private static void StepOnce()
    {
        // この刻みの出来事
        while (Queue.Count > 0 && Queue[0].Tick <= _step)
        {
            var p = Queue[0];
            Queue.RemoveAt(0);
            if (p.Leak) { Sources.Add(p.Src); continue; }
            Rebuild(p.Cx - p.R, p.Cy - p.R, p.Cx + p.R, p.Cy + p.R);
        }

        // 噴き出し: 粒を出して動かし、床に落ちた粒の水を升に足す
        Landed.Clear();
        for (int i = Sources.Count - 1; i >= 0; i--)
        {
            var src = Sources[i];
            int age = _step - src.Start;
            if (age >= FullSteps + FadeSteps) { Sources.RemoveAt(i); continue; }
            if (!src.Shown) { src.Shown = true; LeakStarted?.Invoke(src.At, src.N, src.Seed, src.Both); }
            int k256 = age < FullSteps ? 256 : 256 * (FullSteps + FadeSteps - age) / FadeSteps;
            Emit(src, k256, age < BurstSteps);
        }
        MoveParticles();

        // 流れ: 各升が自分の 4 本の管の流れ出しを決める (読むのは前の刻みの高さだけ・順番に依らない)
        for (int a = 0; a < Active.Count; a++)
        {
            int k = Active[a];
            int h = _hgt[k];
            int sum = 0;
            for (int d = 0; d < 4; d++)
            {
                int j = k + Nx[d] + Ny[d] * _w;
                int f = 0;
                if (h > 0 && _open[j] != 0 && Passable(k, d))
                {
                    int hj = _hgt[j];
                    int diff = h - hj - (hj == 0 ? DryHold : WetHold);
                    f = _flux[k * 4 + d] * Damp / 256 + (diff > 0 ? diff * Gain / 256 : 0);
                    if (f < 0) f = 0;
                }
                _flux[k * 4 + d] = f;
                sum += f;
                if (f > 0) Wake(j);
            }
            if (sum > h)
                for (int d = 0; d < 4; d++) _flux[k * 4 + d] = (int)((long)_flux[k * 4 + d] * h / sum);
        }

        // 高さ: 入ってくる流れ − 出ていく流れ
        for (int a = 0; a < Active.Count; a++)
        {
            int k = Active[a];
            int dh = 0;
            for (int d = 0; d < 4; d++)
            {
                dh -= _flux[k * 4 + d];
                int j = k + Nx[d] + Ny[d] * _w;
                dh += _flux[j * 4 + Opp[d]];
            }
            if (dh != 0) { _hgt[k] += dh; Mark(k); }
            // 浅すぎる升は 4 刻みに 1 ずつ乾く
            if (_hgt[k] > 0 && _hgt[k] < MinDepth && (_step & 3) == 0) { _hgt[k]--; Mark(k); }
        }

        // 水も流れも無い升を外す
        int o = 0;
        for (int a = 0; a < Active.Count; a++)
        {
            int k = Active[a];
            bool idle = _hgt[k] == 0;
            if (idle)
                for (int d = 0; d < 4; d++) if (_flux[k * 4 + d] != 0) { idle = false; break; }
            if (idle) { _inList[k] = 0; continue; }
            Active[o++] = k;
        }
        Active.RemoveRange(o, Active.Count - o);

        Steps++;
        if (_step % 300 == 0) { LastDigest = Digest(); Plugin.Logger.LogInfo($"[WaterSim] step={_step} digest={LastDigest:x8} active={Active.Count} volume={Volume()}"); }
    }

    private static uint Next(Source s)
    {
        uint x = s.Rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        s.Rng = x;
        return x;
    }

    // 1 刻みぶんの粒を出す (勢い k256 = 0〜256)。両側に噴く時は交互の向きへ
    private static void Emit(Source s, int k256, bool burst)
    {
        int sides = s.Both ? 2 : 1;
        s.Acc += PerStep * sides * k256 * (burst ? 7 : 4) / 4;
        while (s.Acc >= 256)
        {
            s.Acc -= 256;
            if (Particles >= MaxParticles) continue;
            bool back = s.Both && (Next(s) & 1) != 0;
            int dx = back ? -s.Dx : s.Dx, dy = back ? -s.Dy : s.Dy;
            int mx = back ? s.Mx - 2 * s.Dx * MouthQ / 4 : s.Mx, my = back ? s.My - 2 * s.Dy * MouthQ / 4 : s.My;
            // 向き: 普段は ±20° (表の 3〜11)・破裂の間は ±35° (0〜14)。真ん中ほど出やすい (2 つの和)
            int span = burst ? 15 : 9, lo = burst ? 0 : 3;
            int a = lo + (int)((Next(s) % (uint)span + Next(s) % (uint)span) / 2);
            int c = FanCos[a], sn = FanSin[a];
            int rx = (dx * c - dy * sn) / 256, ry = (dx * sn + dy * c) / 256;
            int reach = ReachMin + (ReachMax - ReachMin) * k256 / 256;
            if (burst) reach = reach * 13 / 10;
            int dist = reach * (154 + (int)(Next(s) % 115u)) / 256;            // 0.6〜1.05 倍
            int flight = FlightMin + (int)(Next(s) % (uint)(FlightMax - FlightMin + 1));
            int sp = dist / flight;                                            // 1 刻みに進む距離 (単位 1/1024)
            int p = Particles++;
            // 位置・速さ・高さはどれも 1/1024 単位 (升 ×256 と同じ)。向き (長さ 256) × 速さ / 256
            Px[p] = Ox[p] = mx; Py[p] = Oy[p] = my;
            Ph[p] = Oh[p] = MouthH;
            Vx[p] = rx * sp / 256;
            Vy[p] = ry * sp / 256;
            Vh[p] = (Gravity * flight * flight / 2 - MouthH) / flight;
            Stuck[p] = false;
        }
    }

    // 粒が升 (ax, ay) から (bx, by) へ進めるか (隣か斜め隣まで。斜めはどちらかの回り道が通れればよい)
    private static bool CanCross(int ax, int ay, int bx, int by)
    {
        int dx = bx - ax, dy = by - ay;
        if (dx == 0 && dy == 0) return true;
        if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1) return false;
        int k = ay * _w + ax;
        int dX = dx > 0 ? 0 : 1, dY = dy > 0 ? 2 : 3;
        if (dy == 0) return Passable(k, dX);
        if (dx == 0) return Passable(k, dY);
        int kx = k + dx, ky = k + dy * _w;
        return (Passable(k, dX) && Passable(kx, dY)) || (Passable(k, dY) && Passable(ky, dX));
    }

    // 粒を 1 刻み動かす。壁 (閉じた升) に入ったら手前へ戻して横の動きを止める (壁を伝って垂れる)。
    // 床に落ちたらその升に水を足して消す (最後の粒を空いた所へ詰める)
    private static void MoveParticles()
    {
        for (int p = 0; p < Particles; p++)
        {
            Ox[p] = Px[p]; Oy[p] = Py[p]; Oh[p] = Ph[p];
            int nx = Px[p] + Vx[p], ny = Py[p] + Vy[p];
            int cx = nx >> 8, cy = ny >> 8;
            if (cx < 1 || cy < 1 || cx >= _w - 1 || cy >= _h - 1 || _open[cy * _w + cx] == 0 || !CanCross(Px[p] >> 8, Py[p] >> 8, cx, cy))
            {
                Vx[p] = 0; Vy[p] = 0; Stuck[p] = true;
                nx = Px[p]; ny = Py[p];
            }
            Px[p] = nx; Py[p] = ny;
            Vh[p] -= Gravity;
            Ph[p] += Vh[p];
            if (Ph[p] > 0) continue;
            int k = (ny >> 8) * _w + (nx >> 8);
            if (_open[k] != 0) { _hgt[k] += Mass; Wake(k); Mark(k); }
            Landed.Add((nx, ny));
            int last = --Particles;
            if (p != last)
            {
                Px[p] = Px[last]; Py[p] = Py[last]; Ph[p] = Ph[last];
                Ox[p] = Ox[last]; Oy[p] = Oy[last]; Oh[p] = Oh[last];
                Vx[p] = Vx[last]; Vy[p] = Vy[last]; Vh[p] = Vh[last];
                Stuck[p] = Stuck[last];
                p--; // 詰めた粒 (まだこの刻みで動かしていない) を同じ番号でもう一度回す
            }
        }
    }

    // 升の順番に依らない指紋 (刻みの番号も混ぜる)
    internal static uint Digest()
    {
        uint acc = (uint)_step * 2654435761u + (uint)Particles * 40503u;
        foreach (int k in Active)
        {
            uint v = (uint)k * 0x9E3779B1u ^ (uint)_hgt[k] * 0x85EBCA77u;
            v ^= v >> 15; v *= 0xC2B2AE3Du; v ^= v >> 13;
            acc += v;
        }
        return acc;
    }

    internal static long Volume()
    {
        long v = 0;
        foreach (int k in Active) v += _hgt[k];
        return v;
    }

    // その点の水の深さ (Full = 1 単位)
    internal static int DepthAt(Vector2 p)
    {
        if (!_ready) return 0;
        int x = (int)MathF.Floor((p.x - _org.x) / _cell), y = (int)MathF.Floor((p.y - _org.y) / _cell);
        if (x < 0 || y < 0 || x >= _w || y >= _h) return 0;
        return _hgt[y * _w + x];
    }

    // ── 毎フレーム ─────────────────────────────────────────────────────

    public static void Tick()
    {
        try { TickCore(); }
        catch (Exception e) { Fail("tick", e); }
    }

    private static void TickCore()
    {
        if (GameClock.ShipGen != _shipGen) { _shipGen = GameClock.ShipGen; Reset(); }
        if (!_running) return;
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

    internal static void Reset()
    {
        _ready = false;
        _running = false;
        _open = null; _sub = null; _edge = null; _hgt = null; _flux = null; _inList = null; _tileDirty = null;
        DirtyTiles.Clear();
        Active.Clear();
        Queue.Clear();
        Sources.Clear();
        Particles = 0;
        Landed.Clear();
        _step = 0;
        Late = 0;
        Steps = 0;
        LastDigest = 0;
    }

    internal static void Register()
    {
        TestBridge.Register("water", "[reset | show [r] | hide] 水の計算 (show = 自分の周りに判定を色で重ねる: 赤 = 水の升が閉じている・橙 = 歩けない所・黄 = 家具で水を見せない所): 刻み・遅れ・指紋・量 (reset = 水を全部消す)", (args, reply) =>
        {
            string a = args.Trim();
            if (a == "reset") Reset();
            if (a.StartsWith("show")) { reply(WaterDebug.Show(a.Length > 4 ? a.Substring(4).Trim() : "")); return; }
            if (a == "hide") { WaterDebug.Hide(); reply("OK water hide"); return; }
            if (a.StartsWith("furn")) { WaterDebug.ListFurniture(a.Length > 4 ? a.Substring(4).Trim() : "", reply); return; }
            if (!_ready) { reply($"OK water off {GameClock.Describe()}"); return; }
            reply($"OK water step={_step} target={GameClock.Now - Delay} late={Late} queue={Queue.Count} sources={Sources.Count} particles={Particles} active={Active.Count} volume={Volume()} digest={Digest():x8} stepMs={LastStepMs:0.000} grid={_w}x{_h} {GameClock.Describe()}");
        });
    }
}
