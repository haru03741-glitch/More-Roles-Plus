using System;
using System.Collections.Generic;
using MoreRolesPlus.Bridge;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 床の上の火の計算。升は水 (WaterSim) と同じ 0.25 単位で、刻みも水と同じ (TerrainStep が 水 → 火 の順に進める)。
// 升ごとに熱 (0..MaxT)・燃料・材質を持つ。燃えている升は材質の炎の熱へ寄り、熱は隣の升へ差に比例して移る。
// 熱が材質の着火点 (升ごとに少しゆらす) を越えた升は燃え始め、燃料が尽きると焦げて消える。
// 材質は部屋から決める (燃えにくい金属の床・よく燃える木や草・油・電気の配線)。
// 水のある升は熱で水が湯気になって冷える。油の火は水で消えずに噴き上がり、電気の火は消えにくく水が電気を帯びる。
// 船に穴が開いて空気が抜けている所は、口の際では炎が吹き消され、それ以外では炎が口の方へなびく。
// 燃えた床と、炎の接した壁の面は燃やした量に応じて段階的に焦げる。壁に長く炎が当たると、ホストが壁を叩いた時と同じ破壊を
// 起こして (耐久を 1 ずつ削る = ひび → 大きなひび → 崩れる) 焼け落ちる。外壁は打撃と同じく抜けない。
//
// 全員の手元でぴったり同じ結果にするため、水と同じく整数だけで計算し、ゆらぎは升の番号のハッシュから作る。
// 出来事 (点火・油をまく・爆発の熱) はホストが刻みを押して配った物を、その刻みで入れる
internal static class FireSim
{
    public const int MaxT = 2047;
    private const int Cond = 40;              // 隣へ移る熱 = 差 × これ/1024
    private const int Cool = 30;              // 冷え = 熱 × これ/1024 + 1
    private const int Rise = 48;              // 燃えている升が炎の熱へ寄る割合 (/256)
    private const int FadeFuel = 60;          // 燃料がこれを切ると炎が小さくなっていく
    private const int BurnUp = 40, BurnDown = 14; // 絵の炎の大きさの増え方・減り方 (1 刻み /255)
    // 水: 熱 100 を越えた升の水を (熱 − 100)/QuenchDiv + QuenchBase だけ湯気にし、湯気にした量 × QuenchHeat だけ冷える
    private const int SteamT = 100;
    private const int QuenchDiv = 6, QuenchBase = 20, QuenchHeat = 6;
    // 油の噴き上がり: 半径 FlareR 升へ熱 MaxT と浮いた油。同じ升はしばらく (FlareCool 刻み) 噴かない
    private const int FlareR = 3, FlareCool = 45;
    private const int ArcCool = 15;           // 電気の火花の間隔 (刻み)
    // 吸い出し: 口へ引く強さ (/1024) がこれを越える升は炎が吹き消される・気圧がこれより低い升は燃え始めない
    private const int BlowOut = 900;
    private const int ThinAir = Decompression.Full / 2;
    private const int Lean = 48;              // 口の方へ熱が余分に移る (引く強さ 1024 あたり /1024)
    // 爆発の熱: 爆心で BlastHeat + BlastBase・縁で BlastBase
    private const int BlastHeat = 1100, BlastBase = 300;
    private const int MaxFuel = 4000;
    // 焦げ: 燃やした量がこの段を越えるたびに、その升に接した壁の面を濃く焦がす (床の焦げは FireArt が燃やした量から描く)
    private static readonly int[] CharStep = { 20, 160, 600 };
    private static readonly float[] CharAmount = { 0.5f, 0.8f, 1f };
    // 壁の焼け落ち: 燃えている升の熱が材質の下限を越えた分を壁ごとに貯め、StageHeat 貯まるたびに耐久を 1 削る
    private const int StageHeat = 120000;     // 木の部屋で 1 段約 4 秒 (3 段で約 12 秒)・油の金属の部屋で約 6 秒
    private const float BurnGap = 0.35f;      // 焼け落ちの依頼の最小間隔 (秒・長い壁沿いの火でも一度に出さない)

    internal enum Mat : byte { Unknown = 0, None, Metal, Wood, Grass, Fuel, Electric }
    //                                        Unknown None  Metal  Wood  Grass  Fuel  Electric
    private static readonly int[] MFuel   = {   0,     0,    24,  900,   150, 1200,   450 };
    private static readonly int[] MIgnite = { 9999,  9999, 1500,  650,   450,  380,   750 };
    private static readonly int[] MBurn   = {   0,     0,     1,    1,     1,    2,     1 };
    private static readonly int[] MFlame  = {   0,     0,  1300, 1500,  1400, 1800,  1450 };
    private static readonly int[] MWallMin = { 9999, 9999,  1050,  600,   900, 1050,  1050 }; // 壁が傷み始める熱 (部屋の材質で引く)

    private const byte FBurning = 1;

    private struct Pending
    {
        public int Tick;
        public DamageKind Kind;
        public int Sx, Sy, Sr;     // 中心と半径 (升 ×256)
        public int Power;          // 0..256
    }

    private static int _shipGen;
    private static bool _ready, _running;
    private static int _w, _h, _tw, _th;
    private static Vector2 _org;
    private static float _cell;
    private static int[] _t, _dt;
    private static int[] _fuel;
    private static byte[] _mat, _flag, _burn, _inList, _flareAt, _arcAt;
    private static byte[] _base;             // 部屋から決めた材質 (油をまいても変わらない。壁の燃えやすさに使う)
    private static ushort[] _char;           // 燃やした燃料の量 (焦げの濃さ)
    private static bool[] _tileMat;          // 材質を決めたタイル
    private static bool[] _tileDirty;
    private static readonly List<int> Active = new();
    private static readonly List<Pending> Queue = new();
    private static readonly List<int> Flares = new();
    private static int _step;
    private static readonly List<(Collider2D Col, Mat M, float X0, float Y0, float X1, float Y1)> Rooms = new();
    private static Mat _outdoor = Mat.Metal, _indoor = Mat.Metal;

    // 絵 (FireArt / FireFx) が読む
    public const int TileCells = WaterSim.TileCells;
    internal static readonly List<int> DirtyTiles = new();
    internal static bool Ready => _ready;
    internal static int W => _w;
    internal static int H => _h;
    internal static int TilesW => _tw;
    internal static Vector2 Origin => _org;
    internal static float Cell => _cell;
    internal static int Heat(int k) => _t[k];
    internal static int Burn(int k) => _burn[k];
    internal static bool Burning(int k) => (_flag[k] & FBurning) != 0;
    internal static int Oil(int k) => _mat[k] == (byte)Mat.Fuel && (_flag[k] & FBurning) == 0 ? _fuel[k] : 0;
    internal static int Charred(int k) => _char[k];
    internal static Mat MatOf(int k) => (Mat)_mat[k];
    internal static IReadOnlyList<int> ActiveCells => Active;
    internal static void Clean(int t) => _tileDirty[t] = false;
    // 今のフレームの出来事 (TerrainStep がフレームの始めに空にし、FireFx が同じフレームで読む)
    internal static readonly List<(float X, float Y, int Amount)> Steam = new();
    internal static readonly List<(float X, float Y)> FlareFx = new();
    internal static readonly List<(float X, float Y)> Arcs = new();
    internal static readonly List<(float X, float Y, float A)> Chars = new(); // 壁の面に焦げを付ける点と濃さ (0..1)

    // 壁の焼け落ち (ホストと一人の時だけ数える。全員の計算の指紋には入れない)
    private sealed class Scald { public int Heat, Stage, K, D; public bool Done, Queued; }
    private static readonly Dictionary<long, Scald> Scalds = new();
    private static readonly List<Scald> BurnQueue = new();
    private static bool _decides;
    private static int _decideFrame;
    private static float _burnAt;
    internal static int WallHits { get; private set; }
    internal static string LastWallHit = "-";

    // 確認用
    internal static int Steps { get; private set; }
    internal static int Late { get; private set; }
    internal static int BurningCount { get; private set; }
    internal static long SteamTotal { get; private set; }
    internal static int FlareTotal { get; private set; }
    internal static double LastStepMs { get; private set; }
    internal static double MaxStepMs { get; private set; }
    internal static bool Running => _running;
    internal static int Step => _step;

    // ── 入口 ───────────────────────────────────────────────────────────

    // 全ての破壊の結果 (全員の手元)。点火・油・爆発の熱
    public static void OnApplied(in ResolvedDamage r)
    {
        try
        {
            if (r.Kind != DamageKind.Ignite && r.Kind != DamageKind.Spill && r.Kind != DamageKind.Explosion) return;
            int tick = GameClock.Expand(r.Tick);
            if (!Begin(tick)) return;
            var p = new Pending
            {
                Tick = tick,
                Kind = r.Kind,
                Sx = (int)MathF.Floor((r.Position.x - _org.x) / _cell * 256f),
                Sy = (int)MathF.Floor((r.Position.y - _org.y) / _cell * 256f),
                Sr = (int)(r.Size / _cell * 256f),
                Power = r.Kind == DamageKind.Explosion ? 256 : (int)MathF.Round(r.Force * 256f),
            };
            if (p.Tick < _step) { Late++; p.Tick = _step; }
            int i = Queue.Count;
            while (i > 0 && Queue[i - 1].Tick > p.Tick) i--;
            Queue.Insert(i, p);
        }
        catch (Exception e) { Fail("apply", e); }
    }

    private static void Fail(string where, Exception e)
    {
        Plugin.Logger.LogError($"[FireSim] {where}: {e}");
        Reset();
    }

    // 水の升を借りて、水の刻みに合わせて回し始める
    private static bool Begin(int tick)
    {
        if (!WaterSim.Begin(tick)) return false;
        if (_ready && (_w != WaterSim.W || _h != WaterSim.H)) Reset();
        if (!_ready && !Build()) return false;
        if (!_running) { _running = true; _step = WaterSim.Step; }
        return true;
    }

    private static bool Build()
    {
        _w = WaterSim.W; _h = WaterSim.H;
        _org = WaterSim.Origin; _cell = WaterSim.Cell;
        int n = _w * _h;
        _t = new int[n]; _dt = new int[n]; _fuel = new int[n];
        _mat = new byte[n]; _flag = new byte[n]; _burn = new byte[n]; _inList = new byte[n];
        _flareAt = new byte[n]; _arcAt = new byte[n]; _char = new ushort[n]; _base = new byte[n];
        _tw = (_w + TileCells - 1) / TileCells;
        _th = (_h + TileCells - 1) / TileCells;
        _tileMat = new bool[_tw * _th];
        _tileDirty = new bool[_tw * _th];
        ListRooms();
        _ready = true;
        return true;
    }

    // ── 材質 ───────────────────────────────────────────────────────────

    private static void ListRooms()
    {
        Rooms.Clear();
        var ship = ShipStatus.Instance;
        if (!ship) return;
        bool fungle = ship.TryCast<FungleShipStatus>() != null;
        bool polus = !fungle && ship.TryCast<PolusShipStatus>() != null;
        _outdoor = fungle ? Mat.Grass : polus ? Mat.None : Mat.Metal;
        _indoor = fungle ? Mat.Wood : Mat.Metal;
        // 材質の決まった部屋を先に、それ以外の部屋 (屋内の既定) を後ろに並べる (重なった所は決まった方を取る)
        for (int pass = 0; pass < 2; pass++)
            foreach (var r in ship.AllRooms)
            {
                if (!r || !r.roomArea) continue;
                Mat m = RoomMat(r.RoomId, fungle, polus);
                if ((m == Mat.Unknown) != (pass == 1)) continue;
                var b = r.roomArea.bounds;
                Rooms.Add((r.roomArea, m == Mat.Unknown ? _indoor : m, b.min.x, b.min.y, b.max.x, b.max.y));
            }
    }

    private static Mat RoomMat(SystemTypes id, bool fungle, bool polus)
    {
        switch (id)
        {
            case SystemTypes.Electrical: return Mat.Electric;
            case SystemTypes.Greenhouse:
            case SystemTypes.Jungle:
            case SystemTypes.Highlands: return Mat.Grass;
            case SystemTypes.Beach:
            case SystemTypes.MiningPit:
            case SystemTypes.Showers: return Mat.None;
            case SystemTypes.Records:
            case SystemTypes.Lounge:
            case SystemTypes.FishingDock:
            case SystemTypes.Lookout: return Mat.Wood;
            case SystemTypes.Outside: return polus ? Mat.None : fungle ? Mat.Grass : Mat.Metal;
            case SystemTypes.Dropship:
            case SystemTypes.Laboratory:
            case SystemTypes.Reactor:
            case SystemTypes.Comms: return fungle ? Mat.Metal : Mat.Unknown;
        }
        return fungle ? Mat.Wood : Mat.Unknown;
    }

    // タイルの升の材質と燃料を決める (最初に熱か油が届いた時)。部屋の外は屋外の材質。
    // 決める時刻は端末ごとに違うので、動かない物 (部屋の範囲) だけから決める (家具は動いたり壊れたりして端末ごとに割れる)
    private static void EnsureTile(int k)
    {
        int x = k % _w, y = k / _w;
        int t = y / TileCells * _tw + x / TileCells;
        if (_tileMat[t]) return;
        _tileMat[t] = true;
        int x0 = x / TileCells * TileCells, y0 = y / TileCells * TileCells;
        for (int cy = y0; cy < Math.Min(_h, y0 + TileCells); cy++)
        for (int cx = x0; cx < Math.Min(_w, x0 + TileCells); cx++)
        {
            int c = cy * _w + cx;
            // 升の真ん中から少しずらして引く (部屋の縁が升の真ん中に乗ると端末ごとの丸めで割れる)
            Mat m = RoomAt(_org.x + (cx + 0.5f) * _cell + 0.0013f, _org.y + (cy + 0.5f) * _cell + 0.0017f);
            _base[c] = (byte)m;
            if (_mat[c] == (byte)Mat.Fuel) continue; // 先にまかれた油
            _mat[c] = (byte)m;
            _fuel[c] = MFuel[(int)m];
        }
    }

    private static Mat RoomAt(float x, float y)
    {
        foreach (var r in Rooms)
        {
            if (x < r.X0 || x > r.X1 || y < r.Y0 || y > r.Y1 || !r.Col) continue;
            if (r.Col.OverlapPoint(new Vector2(x, y))) return r.M;
        }
        return _outdoor;
    }

    // ── 1 刻み ─────────────────────────────────────────────────────────

    // 1 刻み進める (TerrainStep から・水の刻みを進めた直後に同じ刻みの番号で)
    internal static void AdvanceOne(int s)
    {
        if (!_running) return;
        if (!WaterSim.Ready) { Reset(); return; }
        try
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _step = s;
            StepOnce();
            _step = s + 1;
            LastStepMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (LastStepMs > MaxStepMs) MaxStepMs = LastStepMs;
        }
        catch (Exception e) { Fail("step", e); }
    }

    private static readonly int[] Nx = { 1, -1, 0, 0 };
    private static readonly int[] Ny = { 0, 0, 1, -1 };

    private static void StepOnce()
    {
        while (Queue.Count > 0 && Queue[0].Tick <= _step)
        {
            var p = Queue[0];
            Queue.RemoveAt(0);
            Apply(p);
        }
        if (Active.Count == 0) { Steps++; return; }

        bool pull = Decompression.Running && Decompression.Pulling;

        // 熱の移動: 熱い側の升が差に比例して隣へ渡す (読むのは前の熱だけ・足し算なので升の順番に依らない)
        int count = Active.Count;
        for (int a = 0; a < count; a++)
        {
            int k = Active[a];
            int tk = _t[k];
            if (tk <= 0) continue;
            int leanD = -1, leanS = 0;
            if (pull && Burning(k)) Leaning(k, out leanD, out leanS);
            for (int d = 0; d < 4; d++)
            {
                int j = k + Nx[d] + Ny[d] * _w;
                if (!WaterSim.Open(j) || !WaterSim.Passable(k, d)) continue;
                int diff = tk - _t[j];
                if (diff <= 0) continue;
                int c = d == leanD ? Cond + leanS * Lean / 1024 : Cond;
                int f = diff * c / 1024;
                if (f <= 0) continue;
                _dt[k] -= f;
                _dt[j] += f;
                Wake(j);
            }
        }

        // 升ごと: 燃える・冷える・水・空気
        Flares.Clear();
        int burning = 0;
        for (int a = 0; a < Active.Count; a++)
        {
            int k = Active[a];
            int t = _t[k] + _dt[k];
            _dt[k] = 0;
            int m = _mat[k];
            byte flag = _flag[k];
            bool on = (flag & FBurning) != 0;
            if (!WaterSim.Open(k)) { on = false; t = 0; }
            int jit = Jitter(k);
            if (on)
            {
                _fuel[k] -= MBurn[m];
                int ch = _char[k];
                _char[k] = (ushort)Math.Min(ushort.MaxValue, ch + MBurn[m]);
                t += (MFlame[m] + jit * 2 - t) * Rise / 256;
                for (int i = 0; i < CharStep.Length; i++)
                    if (ch < CharStep[i] && _char[k] >= CharStep[i]) Char(k, CharAmount[i]);
                if (_decides) Sear(k, t);
                if (_fuel[k] <= 0)
                {
                    _fuel[k] = 0;
                    on = false;
                }
            }
            else if (_fuel[k] > 0 && t >= MIgnite[m] + jit) on = true;
            t -= t * Cool / 1024 + 1;

            // 水: 熱で湯気になり冷える。油の火は水で消えずに噴き上がる。電気の火は消えにくい
            int depth = WaterSim.Height(k);
            if (depth > 0 && t > SteamT)
            {
                if (on && m == (int)Mat.Fuel)
                {
                    int e = WaterSim.Evaporate(k, depth);
                    SteamTotal += e;
                    if (e > 0) { var c = Center(k); Steam.Add((c.X, c.Y, e)); }
                    if ((byte)(_step - _flareAt[k]) >= FlareCool || _flareAt[k] == 0) { _flareAt[k] = (byte)Math.Max(1, _step & 255); Flares.Add(k); }
                }
                else
                {
                    int want = (t - SteamT) / QuenchDiv + QuenchBase;
                    int e = WaterSim.Evaporate(k, Math.Min(depth, want));
                    if (e > 0)
                    {
                        SteamTotal += e;
                        var c = Center(k);
                        Steam.Add((c.X, c.Y, e));
                        t -= e * QuenchHeat / (m == (int)Mat.Electric && on ? 2 : 1);
                        if (on && m == (int)Mat.Electric && (byte)(_step - _arcAt[k]) >= ArcCool) { _arcAt[k] = (byte)_step; Arcs.Add(Center(k)); }
                    }
                }
            }

            // 空気: 口の際は吹き消され、薄い所では燃え始めない
            if (pull && (on || t > 0))
            {
                int x = k % _w, y = k / _w;
                if (Decompression.FlowAt(x * WaterSim.Sub, y * WaterSim.Sub, out int dist, out int press, out _, out _))
                {
                    int s = (int)((long)Decompression.Strength1024(dist) * press / Decompression.Full);
                    if (s > BlowOut) { on = false; t -= t / 4; }
                    else if (press < ThinAir && !((flag & FBurning) != 0)) on = false;
                }
            }

            if (t < 0) t = 0;
            if (t > MaxT) t = MaxT;
            int oldGlow = _t[k] >> 6;
            _t[k] = t;
            _flag[k] = on ? (byte)(flag | FBurning) : (byte)(flag & ~FBurning);
            int target = !on ? 0 : _fuel[k] >= FadeFuel ? 255 : 64 + 191 * _fuel[k] / FadeFuel;
            int b = _burn[k];
            int nb = b < target ? Math.Min(target, b + BurnUp) : Math.Max(target, b - BurnDown);
            if (nb != b || oldGlow != t >> 6) Mark(k);
            _burn[k] = (byte)nb;
            if (on) burning++;
        }
        BurningCount = burning;

        // 噴き上がり (升の計算の後にまとめて・並びは升の一覧の順)
        foreach (int k in Flares) Flare(k);

        // 熱も炎も無い升を外す
        int o = 0;
        for (int a = 0; a < Active.Count; a++)
        {
            int k = Active[a];
            if (_t[k] == 0 && _burn[k] == 0 && (_flag[k] & FBurning) == 0) { _inList[k] = 0; continue; }
            Active[o++] = k;
        }
        Active.RemoveRange(o, Active.Count - o);
        Steps++;
        if (_step % 300 == 0 && Active.Count > 0) Plugin.Logger.LogInfo($"[FireSim] step={_step} digest={Digest():x8} active={Active.Count} burning={BurningCount}");
    }

    // 升に接した壁の面を焦がす。北の壁は面が線の上に立って描かれるので、面の上の方まで焦がす
    private static void Char(int k, float a)
    {
        Mark(k);
        var c = Center(k);
        float h = _cell * 0.5f;
        for (int d = 0; d < 4; d++)
        {
            int j = k + Nx[d] + Ny[d] * _w;
            if (WaterSim.Open(j) && WaterSim.Passable(k, d)) continue;
            float x = c.X + Nx[d] * (h + 0.06f), y = c.Y + Ny[d] * (h + 0.06f);
            Chars.Add((x, y, a));
            if (d == 2) { Chars.Add((x, y + 0.28f, a * 0.85f)); Chars.Add((x, y + 0.56f, a * 0.6f)); }
        }
    }

    // 燃えている升の熱を、接した壁に貯める (ホストと一人の時)。同じ壁の耐久の格子に接した升は 1 つの壁として数える
    private static void Sear(int k, int t)
    {
        // 点火の直後は熱が上限まで跳ねるので、材質の炎の熱で頭を抑える (点けた瞬間に壁が崩れない)
        int heat = Math.Min(t, MFlame[_mat[k]] + 128) - MWallMin[_base[k]];
        if (heat <= 0) return;
        float cx = _org.x + (k % _w + 0.5f) * _cell, cy = _org.y + (k / _w + 0.5f) * _cell;
        for (int d = 0; d < 4; d++)
        {
            int j = k + Nx[d] + Ny[d] * _w;
            if (WaterSim.Open(j) && WaterSim.Passable(k, d)) continue;
            long key = WallDurability.CellKey(cx + Nx[d] * _cell * 0.5f, cy + Ny[d] * _cell * 0.5f);
            if (!Scalds.TryGetValue(key, out var w)) Scalds[key] = w = new Scald();
            if (w.Done) continue;
            w.Heat += heat;
            if (w.Queued || w.Heat < (w.Stage + 1) * StageHeat) continue;
            w.Queued = true;
            w.K = k; w.D = d;
            BurnQueue.Add(w);
        }
    }

    // 貯まった壁を 1 つずつ叩く (刻みを進め終えた後に TerrainStep から。打撃は地形・水の升をその場で変えるので刻みの途中では出さない)
    internal static void IssueWallBurns()
    {
        try { IssueWallBurn(); }
        catch (Exception e) { Plugin.Logger.LogError($"[FireSim] wall burn: {e}"); BurnQueue.Clear(); }
    }

    private static void IssueWallBurn()
    {
        if (BurnQueue.Count == 0 || !_ready) return;
        if (!_decides)
        {
            foreach (var q in BurnQueue) q.Queued = false;
            BurnQueue.Clear();
            return;
        }
        float now = Environment.TickCount64 / 1000f;
        if (now - _burnAt < BurnGap) return;
        _burnAt = now;
        var w = BurnQueue[0];
        BurnQueue.RemoveAt(0);
        w.Queued = false;
        var c = Center(w.K);
        var res = TerrainApi.WorldHit(new Vector2(c.X, c.Y), new Vector2(Nx[w.D], Ny[w.D]), 0.5f);
        WallHits++;
        LastWallHit = res.Why;
        // 壁が無い・外壁・壊れない物は以後叩かない。抜けたらその壁は終わり (奥の壁まで続けて焼かない)。
        // ホストでなくなった等の一時的な断りは、次に貯まった時にまた叩く
        if (!res.Ok && res.Why == "host only") return;
        if (!res.Ok || res.Why != null && res.Why.Contains("breach")) w.Done = true;
        else if (++w.Stage >= WallDurability.MaxHp) w.Done = true;
    }

    // 燃えている升の熱が口の方へなびく向き (Nx/Ny の並び) と引く強さ
    private static void Leaning(int k, out int dir, out int s)
    {
        dir = -1; s = 0;
        int x = k % _w, y = k / _w;
        if (!Decompression.FlowAt(x * WaterSim.Sub, y * WaterSim.Sub, out int dist, out int press, out int fx, out int fy)) return;
        s = (int)((long)Decompression.Strength1024(dist) * press / Decompression.Full);
        if (s <= 0 || fx == 0 && fy == 0) return;
        dir = Math.Abs(fx) >= Math.Abs(fy) ? (fx > 0 ? 0 : 1) : (fy > 0 ? 2 : 3);
    }

    // 油の火に水: 周りへ熱を飛ばし、その升の油の半分を周りの水の上へ浮かせて散らす (4 分の 1 は噴き上がりで燃え尽きる)。
    // 油は増やさない (増やすと水が流れ込み続ける間ずっと噴き上がり続けた)
    private static void Flare(int k)
    {
        FlareTotal++;
        FlareFx.Add(Center(k));
        int x = k % _w, y = k / _w;
        int wet = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            int share = pass == 0 ? 0 : _fuel[k] / 2 / Math.Max(1, wet);
            for (int dy = -FlareR; dy <= FlareR; dy++)
            for (int dx = -FlareR; dx <= FlareR; dx++)
            {
                if (dx * dx + dy * dy > FlareR * FlareR) continue;
                int cx = x + dx, cy = y + dy;
                if (cx < 1 || cy < 1 || cx >= _w - 1 || cy >= _h - 1) continue;
                int j = cy * _w + cx;
                if (!WaterSim.Open(j)) continue;
                bool floats = j != k && WaterSim.Height(j) > 0;
                if (pass == 0) { if (floats) wet++; continue; }
                EnsureTile(j);
                _t[j] = MaxT;
                if (floats && share > 0)
                {
                    if (_mat[j] != (byte)Mat.Fuel) { _mat[j] = (byte)Mat.Fuel; _fuel[j] = 0; }
                    _fuel[j] = Math.Min(MaxFuel, _fuel[j] + share);
                }
                Wake(j);
                Mark(j);
            }
        }
        _fuel[k] -= wet > 0 ? _fuel[k] / 2 + _fuel[k] / 4 : _fuel[k] / 4;
    }

    private static void Apply(in Pending p)
    {
        if (p.Kind == DamageKind.Ignite) FireFx.Ignited.Add((_org.x + p.Sx / 256f * _cell, _org.y + p.Sy / 256f * _cell));
        int r = Math.Max(1, p.Sr / 256 + 1);
        int cx = p.Sx / 256, cy = p.Sy / 256;
        long r2 = (long)p.Sr * p.Sr;
        for (int y = Math.Max(1, cy - r); y <= Math.Min(_h - 2, cy + r); y++)
        for (int x = Math.Max(1, cx - r); x <= Math.Min(_w - 2, cx + r); x++)
        {
            int k = y * _w + x;
            if (!WaterSim.Open(k)) continue;
            long ddx = x * 256 + 128 - p.Sx, ddy = y * 256 + 128 - p.Sy;
            long d2 = ddx * ddx + ddy * ddy;
            if (d2 > r2 && !(x == cx && y == cy)) continue;
            int near = p.Sr <= 0 ? 256 : 256 - (int)(ISqrt(d2) * 256 / Math.Max(1, p.Sr)); // 中心 256・縁 0
            if (near < 0) near = 0;
            EnsureTile(k);
            switch (p.Kind)
            {
                case DamageKind.Ignite:
                    _t[k] = Math.Min(MaxT, _t[k] + MaxT * p.Power / 256 * (128 + near / 2) / 256);
                    break;
                case DamageKind.Spill:
                    if (_mat[k] != (byte)Mat.Fuel) { _mat[k] = (byte)Mat.Fuel; _fuel[k] = 0; }
                    _fuel[k] = Math.Min(MaxFuel, _fuel[k] + 900 * p.Power / 256 * (128 + near / 2) / 256);
                    break;
                case DamageKind.Explosion:
                    _t[k] = Math.Min(MaxT, _t[k] + BlastBase + BlastHeat * near / 256);
                    break;
            }
            Wake(k);
            Mark(k);
        }
    }

    private static long ISqrt(long v)
    {
        if (v <= 0) return 0;
        long r = (long)Math.Sqrt(v);
        while (r * r > v) r--;
        while ((r + 1) * (r + 1) <= v) r++;
        return r;
    }

    // 升ごとの固定のゆらぎ (0..127)
    private static int Jitter(int k)
    {
        uint h = (uint)k * 2654435761u;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
        return (int)(h >> 25);
    }

    private static (float X, float Y) Center(int k) => (_org.x + (k % _w + 0.5f) * _cell, _org.y + (k / _w + 0.5f) * _cell);

    private static void Wake(int k)
    {
        if (_inList[k] != 0) return;
        int x = k % _w, y = k / _w;
        if (x < 1 || y < 1 || x >= _w - 1 || y >= _h - 1) return;
        EnsureTile(k);
        _inList[k] = 1;
        Active.Add(k);
    }

    // 升の絵が変わった: その升と縁を共有するタイルを描き直す
    private static void Mark(int k)
    {
        int x = k % _w, y = k / _w;
        int tx = x / TileCells, ty = y / TileCells;
        MarkTile(tx, ty);
        int lx = x % TileCells, ly = y % TileCells;
        if (lx == 0) MarkTile(tx - 1, ty);
        else if (lx == TileCells - 1) MarkTile(tx + 1, ty);
        if (ly == 0) MarkTile(tx, ty - 1);
        else if (ly >= TileCells - 2) MarkTile(tx, ty + 1); // 炎は上のタイルの下の縁にも掛かる
    }

    private static void MarkTile(int tx, int ty)
    {
        if (tx < 0 || ty < 0 || tx >= _tw || ty >= _th) return;
        int t = ty * _tw + tx;
        if (_tileDirty[t]) return;
        _tileDirty[t] = true;
        DirtyTiles.Add(t);
    }

    // 升の順番に依らない指紋
    internal static uint Digest()
    {
        if (!_ready) return 0;
        uint acc = (uint)_step * 2654435761u;
        foreach (int k in Active)
        {
            uint v = (uint)k * 0x9E3779B1u ^ (uint)_t[k] * 0x85EBCA77u ^ (uint)_fuel[k] * 0x27D4EB2Fu ^ _flag[k];
            v ^= v >> 15; v *= 0xC2B2AE3Du; v ^= v >> 13;
            acc += v;
        }
        return acc;
    }

    // ── 毎フレーム ─────────────────────────────────────────────────────

    public static void Tick()
    {
        try
        {
            if (GameClock.ShipGen != _shipGen) { _shipGen = GameClock.ShipGen; Reset(); }
            // ホストかどうかは 15 フレームに 1 回だけ見る (Unity の生存確認を毎フレーム重ねない)
            if (_running && (++_decideFrame >= 15 || _decideFrame == 1)) { _decideFrame = 1; _decides = !TerrainSync.IsGuest(); }
        }
        catch (Exception e) { Fail("tick", e); }
    }

    // その点の熱と燃えているか (役職・確認用)
    internal static int HeatAt(Vector2 p, out bool burning)
    {
        burning = false;
        if (!_ready) return 0;
        int x = (int)MathF.Floor((p.x - _org.x) / _cell), y = (int)MathF.Floor((p.y - _org.y) / _cell);
        if (x < 0 || y < 0 || x >= _w || y >= _h) return 0;
        int k = y * _w + x;
        burning = Burning(k);
        return _t[k];
    }

    internal static void Reset()
    {
        _ready = false;
        _running = false;
        _t = _dt = _fuel = null;
        _mat = _flag = _burn = _inList = _flareAt = _arcAt = null;
        _char = null; _base = null;
        Scalds.Clear(); BurnQueue.Clear(); WallHits = 0; LastWallHit = "-";
        _tileMat = _tileDirty = null;
        Active.Clear();
        Queue.Clear();
        Flares.Clear();
        DirtyTiles.Clear();
        Rooms.Clear();
        Steam.Clear(); FlareFx.Clear(); Arcs.Clear(); Chars.Clear();
        _step = 0;
        Steps = 0; Late = 0; BurningCount = 0; SteamTotal = 0; FlareTotal = 0; MaxStepMs = 0;
    }

    internal static string Describe() =>
        !_ready ? $"OK fire off {GameClock.Describe()}"
        : $"OK fire step={_step} water={WaterSim.Step} late={Late} queue={Queue.Count} active={Active.Count} burning={BurningCount} steam={SteamTotal} flares={FlareTotal} walls={Scalds.Count} wallHits={WallHits} lastWall={LastWallHit} digest={Digest():x8} stepMs={LastStepMs:0.000} maxStepMs={MaxStepMs:0.000} {GameClock.Describe()}";

    internal static string CellInfo(Vector2 p)
    {
        if (!_ready) return "ERR fire off";
        int x = (int)MathF.Floor((p.x - _org.x) / _cell), y = (int)MathF.Floor((p.y - _org.y) / _cell);
        if (x < 1 || y < 1 || x >= _w - 1 || y >= _h - 1) return "ERR fire out of map";
        int k = y * _w + x;
        EnsureTile(k);
        return $"OK fire at ({x},{y}) mat={(Mat)_mat[k]} t={_t[k]} fuel={_fuel[k]} burning={Burning(k)} burn={_burn[k]} char={_char[k]} base={(Mat)_base[k]} open={WaterSim.Open(k)} water={WaterSim.Height(k)}";
    }
}
