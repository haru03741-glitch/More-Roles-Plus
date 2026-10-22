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
internal static partial class FireSim
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
    // 壁の焼け落ち: 燃えている升の熱が壁の材質の下限を越えた分を壁ごとに貯め、1 段の熱が貯まるたびに耐久を 1 削る。
    // 壁の材質 = ポーラスは全部岩とコンクリート・ほかは部屋の床の材質から (木の部屋 = 木の壁・草地と屋外 = 崖の岩・それ以外 = 金属)。
    // 目安 (3 段で崩れるまで): 木の壁は木の火で約 10 秒・金属の壁は油の火で約 25 秒 (木の火では傷む程度)・岩は油でも 1 分近く
    internal enum WallMat : byte { Wood, Metal, Stone }
    internal static readonly int[] WallMin   = {    600,   1050,   1250 }; // 壁が傷み始める熱
    internal static readonly int[] WallStage = { 120000, 160000, 260000 }; // 耐久 1 段ぶんの熱
    private const float BurnGap = 0.35f;      // 焼け落ちの依頼の最小間隔 (秒・長い壁沿いの火でも一度に出さない)

    internal enum Mat : byte { Unknown = 0, None, Metal, Wood, Grass, Fuel, Electric }
    //                                        Unknown None  Metal  Wood  Grass  Fuel  Electric
    private static readonly int[] MFuel   = {   0,     0,    24,  900,   150, 1200,   450 };
    private static readonly int[] MIgnite = { 9999,  9999, 1500,  650,   450,  380,   750 };
    private static readonly int[] MBurn   = {   0,     0,     1,    1,     1,    2,     1 };
    private static readonly int[] MFlame  = {   0,     0,  1300, 1500,  1400, 1800,  1450 };

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
    private static byte[] _tileMat;          // 材質を決めたタイル
    private static byte[] _tileDirty;
    // 熱か炎のある升の一覧 (並びは加えた順・C++ の部品が書き足すので配列で持つ)
    private static int[] _act = Array.Empty<int>();
    private static int _actN;
    private static readonly ActiveView Active = new();
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
    internal static void Clean(int t) => _tileDirty[t] = 0;

    private sealed class ActiveView : IReadOnlyList<int>
    {
        public int Count => _actN;
        public int this[int i] => _act[i];
        public IEnumerator<int> GetEnumerator() { for (int i = 0; i < _actN; i++) yield return _act[i]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
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
    private static bool _polus;

    internal static WallMat WallOf(int k) => _polus ? WallMat.Stone : (Mat)_base[k] switch
    {
        Mat.Wood => WallMat.Wood,
        Mat.Grass or Mat.None => WallMat.Stone,
        _ => WallMat.Metal,
    };

    // 確かめ用: 壁の材質ごとの下限と 1 段の熱を変える
    internal static string TuneWall(string name, int min, int stage)
    {
        if (!Enum.TryParse(name, true, out WallMat m)) return $"ERR fire wall: unknown {name} (wood|metal|stone)";
        if (min > 0) WallMin[(int)m] = min;
        if (stage > 0) WallStage[(int)m] = stage;
        return $"OK fire wall {m} min={WallMin[(int)m]} stage={WallStage[(int)m]}";
    }
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

    // 時戻し: 戻す刻み target 以後の出来事で、まだ処理していない物を捨てる
    internal static void RewindPurge(int target) => Queue.RemoveAll(p => p.Tick >= target);

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
        _tileMat = new byte[_tw * _th];
        _tileDirty = new byte[_tw * _th];
        _act = new int[n];
        _actN = 0;
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
        _polus = polus;
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
        EnsureTileIndex(y / TileCells * _tw + x / TileCells);
    }

    private static void EnsureTileIndex(int t)
    {
        if (_tileMat[t] != 0) return;
        _tileMat[t] = 1;
        int x0 = t % _tw * TileCells, y0 = t / _tw * TileCells;
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
            if (TerrainRewind.On) RewindHead();
            StepOnce();
            _step = s + 1;
            LastStepMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (LastStepMs > MaxStepMs) MaxStepMs = LastStepMs;
        }
        catch (Exception e) { Fail("step", e); }
    }

    private static readonly int[] Nx = { 1, -1, 0, 0 };
    private static readonly int[] Ny = { 0, 0, 1, -1 };

    // 升の計算の出来事 (C++ の部品と C# の答え合わせ版が同じ順で積み、計算の後に C# が流す)。種類・升・値の 3 個ずつ
    private const int EvSteam = 1, EvFlare = 2, EvArc = 3, EvChar = 4, EvSear = 5;

    // C++ の部品 (native/src/fire_sim.cpp) と同じ並び
    private unsafe struct StepArgs
    {
        public int* T, Dt, Fuel; public byte* Mat, Flag, Burn, InList, FlareAt, ArcAt; public ushort* Char;
        public byte* TileMat, TileDirty; public int* Act;
        public byte* Open, Edge; public int* Hgt;
        public ushort* PDist; public int* PComp, PPar, PPress;
        public int* Ev, Dirty, Missing;
        public int W, H, Tw, Th, ActN, Step, Pull, Decides, Wind, PW, PH, PressN;
        public int EvCap, EvN, DirtyCap, DirtyN, MissCap, MissN, Burning, Mode, SearMin;
    }

    private static int[] _ev = Array.Empty<int>(), _dirtyOut = Array.Empty<int>(), _missing = Array.Empty<int>(), _press;
    private static readonly List<int> EvCs = new(), DirtyCs = new();
    internal static long VerifySteps, VerifyDiff;

    private static void StepOnce()
    {
        while (Queue.Count > 0 && Queue[0].Tick <= _step)
        {
            var p = Queue[0];
            Queue.RemoveAt(0);
            Apply(p);
        }
        if (_actN == 0) { Steps++; return; }

        bool pull = Decompression.Running && Decompression.Pulling;
        ushort[] pdist = null; int[] pcomp = null, ppar = null; int pw = 0, ph = 0;
        if (pull) pull = Decompression.FlowMaps(out pdist, out pcomp, out ppar, out pw, out ph, ref _press);

        // 熱の移動・升ごとの計算 (C++)。熱が新しく届く升のタイルの材質がまだなら先に決めて (Unity の当たり判定を引く) やり直す
        int need = Math.Min(_actN * 5, _w * _h) * 6 * 3;
        if (_ev.Length < need) _ev = new int[Math.Max(need, _ev.Length * 2)];
        if (_dirtyOut.Length < _tw * _th) { _dirtyOut = new int[_tw * _th]; _missing = new int[_tw * _th]; }
        Snapshot before = Native.MrpNative.Verify ? Snapshot.Take() : null;
        int evN, dirtyN;
        for (;;)
        {
            int miss = RunNative(0, pull, pdist, pcomp, ppar, pw, ph, out evN, out dirtyN);
            if (miss == 0) break;
            for (int i = 0; i < miss; i++) EnsureTileIndex(_missing[i]);
            before = Native.MrpNative.Verify ? Snapshot.Take() : null;
        }
        if (before != null) VerifyStep(before, pull, evN, dirtyN);

        for (int i = 0; i < dirtyN; i++) DirtyTiles.Add(_dirtyOut[i]);
        Replay(_ev, evN);

        // 噴き上がり (升の計算の後にまとめて・並びは升の一覧の順)
        foreach (int k in Flares) Flare(k);

        // 熱も炎も無い升を外す (C++)
        RunNative(1, pull, pdist, pcomp, ppar, pw, ph, out _, out _);
        Steps++;
        if (_step % 300 == 0 && _actN > 0) Plugin.Logger.LogInfo($"[FireSim] step={_step} digest={Digest():x8} active={_actN} burning={BurningCount}");
    }

    private static unsafe int RunNative(int mode, bool pull, ushort[] pdist, int[] pcomp, int[] ppar, int pw, int ph, out int evN, out int dirtyN)
    {
        int[] hgt = WaterSim.HeightCells;
        fixed (int* t = _t, dt = _dt, fuel = _fuel, act = _act, h = hgt, ev = _ev, dirty = _dirtyOut, missing = _missing)
        fixed (byte* mat = _mat, flag = _flag, burn = _burn, inList = _inList, flareAt = _flareAt, arcAt = _arcAt, tileMat = _tileMat, tileDirty = _tileDirty)
        fixed (byte* open = WaterSim.OpenCells, edge = WaterSim.EdgeCells)
        fixed (ushort* ch = _char, pd = pdist)
        fixed (int* pc = pcomp, pp = ppar, pr = _press)
        {
            var a = new StepArgs
            {
                T = t, Dt = dt, Fuel = fuel, Mat = mat, Flag = flag, Burn = burn, InList = inList, FlareAt = flareAt, ArcAt = arcAt, Char = ch,
                TileMat = tileMat, TileDirty = tileDirty, Act = act, Open = open, Edge = edge, Hgt = h,
                PDist = pd, PComp = pc, PPar = pp, PPress = pr, Ev = ev, Dirty = dirty, Missing = missing,
                W = _w, H = _h, Tw = _tw, Th = _th, ActN = _actN, Step = _step, Pull = pull ? 1 : 0, Decides = _decides ? 1 : 0,
                Wind = Decompression.Wind ? 1 : 0, PW = pw, PH = ph, PressN = pull ? Decompression.RegionCount : 0,
                EvCap = _ev.Length, DirtyCap = _dirtyOut.Length, MissCap = _missing.Length, Mode = mode, SearMin = SearMin(),
            };
            Native.MrpNative.FireStep(&a);
            evN = a.EvN; dirtyN = a.DirtyN;
            if (a.MissN > 0) return a.MissN;
            _actN = a.ActN;
            if (mode == 0) BurningCount = a.Burning;
            return 0;
        }
    }

    // 出来事を計算の順に流す: 水を湯気にする (量は計算の時の深さから決めてあるので同じ量が減る)・噴き上がり・火花・焦げ・壁に熱
    private static void Replay(int[] ev, int n)
    {
        Flares.Clear();
        for (int i = 0; i < n; i += 3)
        {
            int k = ev[i + 1], v = ev[i + 2];
            switch (ev[i])
            {
                case EvSteam:
                    WaterSim.Evaporate(k, v);
                    SteamTotal += v;
                    var c = Center(k);
                    Steam.Add((c.X, c.Y, v));
                    break;
                case EvFlare: Flares.Add(k); break;
                case EvArc: Arcs.Add(Center(k)); break;
                case EvChar: CharFx(k, CharAmount[v]); break;
                case EvSear: Sear(k, v); break;
            }
        }
    }

    // ── C# の答え合わせ版 (native verify on の時だけ・C++ と同じ結果になるか比べる) ──

    private sealed class Snapshot
    {
        public int[] T, Dt, Fuel, Act, Hgt; public byte[] Flag, Burn, InList, FlareAt, ArcAt, TileDirty; public ushort[] Char; public int ActN;
        public static Snapshot Take() => new()
        {
            T = (int[])_t.Clone(), Dt = (int[])_dt.Clone(), Fuel = (int[])_fuel.Clone(), Act = (int[])_act.Clone(), Hgt = (int[])WaterSim.HeightCells.Clone(),
            Flag = (byte[])_flag.Clone(), Burn = (byte[])_burn.Clone(), InList = (byte[])_inList.Clone(), FlareAt = (byte[])_flareAt.Clone(),
            ArcAt = (byte[])_arcAt.Clone(), TileDirty = (byte[])_tileDirty.Clone(), Char = (ushort[])_char.Clone(), ActN = _actN,
        };
        public void Restore()
        {
            T.CopyTo(_t, 0); Dt.CopyTo(_dt, 0); Fuel.CopyTo(_fuel, 0); Act.CopyTo(_act, 0); Flag.CopyTo(_flag, 0); Burn.CopyTo(_burn, 0);
            InList.CopyTo(_inList, 0); FlareAt.CopyTo(_flareAt, 0); ArcAt.CopyTo(_arcAt, 0); TileDirty.CopyTo(_tileDirty, 0); Char.CopyTo(_char, 0);
            _actN = ActN;
        }
    }

    // C++ の結果を取っておき、前の状態へ戻して C# 版を回し、全部の升と出来事を比べてから C++ の結果へ戻す
    private static void VerifyStep(Snapshot before, bool pull, int evN, int dirtyN)
    {
        var after = Snapshot.Take();
        int burnN = BurningCount;
        before.Restore();
        EvCs.Clear(); DirtyCs.Clear();
        StepCs(pull, before.Hgt);
        long diff = 0;
        diff += Diff(_t, after.T) + Diff(_dt, after.Dt) + Diff(_fuel, after.Fuel) + Diff(_flag, after.Flag) + Diff(_burn, after.Burn)
            + Diff(_inList, after.InList) + Diff(_flareAt, after.FlareAt) + Diff(_arcAt, after.ArcAt) + Diff(_tileDirty, after.TileDirty);
        for (int k = 0; k < _char.Length; k++) if (_char[k] != after.Char[k]) diff++;
        if (_actN != after.ActN) diff++;
        for (int i = 0; i < Math.Min(_actN, after.ActN); i++) if (_act[i] != after.Act[i]) diff++;
        if (BurningCount != burnN) diff++;
        if (EvCs.Count != evN) diff++;
        for (int i = 0; i < Math.Min(evN, EvCs.Count); i++) if (EvCs[i] != _ev[i]) diff++;
        if (DirtyCs.Count != dirtyN) diff++;
        for (int i = 0; i < Math.Min(dirtyN, DirtyCs.Count); i++) if (DirtyCs[i] != _dirtyOut[i]) diff++;
        VerifySteps++;
        VerifyDiff += diff;
        if (diff > 0) Plugin.Logger.LogWarning($"[FireSim] native diff={diff} step={_step} act={after.ActN}/{_actN} ev={evN}/{EvCs.Count} dirty={dirtyN}/{DirtyCs.Count}");
        after.Restore();
        BurningCount = burnN;
    }

    private static int Diff(int[] a, int[] b) { int n = 0; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++; return n; }
    private static int Diff(byte[] a, byte[] b) { int n = 0; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++; return n; }

    // C++ の mode 0 と同じ計算 (水の深さは計算の前の物を読む・水を減らすのは出来事を流す時)
    private static void StepCs(bool pull, int[] hgt)
    {
        // 熱の移動: 熱い側の升が差に比例して隣へ渡す (読むのは前の熱だけ・足し算なので升の順番に依らない)
        int count = _actN;
        for (int a = 0; a < count; a++)
        {
            int k = _act[a];
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
        int burning = 0;
        for (int a = 0; a < _actN; a++)
        {
            int k = _act[a];
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
                    if (ch < CharStep[i] && _char[k] >= CharStep[i]) { MarkCs(k); Ev(EvChar, k, i); }
                if (_decides && Math.Min(t, MFlame[m] + 128) > SearMin() && HasWall(k)) Ev(EvSear, k, t);
                if (_fuel[k] <= 0)
                {
                    _fuel[k] = 0;
                    on = false;
                }
            }
            else if (_fuel[k] > 0 && t >= MIgnite[m] + jit) on = true;
            t -= t * Cool / 1024 + 1;

            // 水: 熱で湯気になり冷える。油の火は水で消えずに噴き上がる。電気の火は消えにくい
            int depth = hgt[k];
            if (depth > 0 && t > SteamT)
            {
                if (on && m == (int)Mat.Fuel)
                {
                    Ev(EvSteam, k, depth);
                    if ((byte)(_step - _flareAt[k]) >= FlareCool || _flareAt[k] == 0) { _flareAt[k] = (byte)Math.Max(1, _step & 255); Ev(EvFlare, k, 0); }
                }
                else
                {
                    int want = (t - SteamT) / QuenchDiv + QuenchBase;
                    int e = Math.Min(depth, want);
                    Ev(EvSteam, k, e);
                    t -= e * QuenchHeat / (m == (int)Mat.Electric && on ? 2 : 1);
                    if (on && m == (int)Mat.Electric && (byte)(_step - _arcAt[k]) >= ArcCool) { _arcAt[k] = (byte)_step; Ev(EvArc, k, 0); }
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
            if (nb != b || oldGlow != t >> 6) MarkCs(k);
            _burn[k] = (byte)nb;
            if (on) burning++;
        }
        BurningCount = burning;
    }

    // 壁に熱を貯めるのは、どの材質の壁の下限も越えない升と壁に接していない升を除く (Sear が何もしない升)
    private static int SearMin() => Math.Min(WallMin[0], Math.Min(WallMin[1], WallMin[2]));

    private static bool HasWall(int k)
    {
        for (int d = 0; d < 4; d++)
            if (!WaterSim.Open(k + Nx[d] + Ny[d] * _w) || !WaterSim.Passable(k, d)) return true;
        return false;
    }

    private static void Ev(int kind, int k, int v) { EvCs.Add(kind); EvCs.Add(k); EvCs.Add(v); }

    // Mark と同じタイルを、DirtyTiles の代わりに答え合わせの一覧へ
    private static void MarkCs(int k)
    {
        int x = k % _w, y = k / _w;
        int tx = x / TileCells, ty = y / TileCells;
        MarkTileCs(tx, ty);
        int lx = x % TileCells, ly = y % TileCells;
        if (lx == 0) MarkTileCs(tx - 1, ty);
        else if (lx == TileCells - 1) MarkTileCs(tx + 1, ty);
        if (ly == 0) MarkTileCs(tx, ty - 1);
        else if (ly >= TileCells - 2) MarkTileCs(tx, ty + 1);
    }

    private static void MarkTileCs(int tx, int ty)
    {
        if (tx < 0 || ty < 0 || tx >= _tw || ty >= _th) return;
        int t = ty * _tw + tx;
        if (_tileDirty[t] != 0) return;
        _tileDirty[t] = 1;
        DirtyCs.Add(t);
    }

    // 升に接した壁の面を焦がす。北の壁は面が線の上に立って描かれるので、面の上の方まで焦がす (升の絵の描き直しは計算の側で印を付けてある)
    private static void CharFx(int k, float a)
    {
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
        int wm = (int)WallOf(k);
        int heat = Math.Min(t, MFlame[_mat[k]] + 128) - WallMin[wm];
        if (heat <= 0) return;
        int stage = WallStage[wm];
        float cx = _org.x + (k % _w + 0.5f) * _cell, cy = _org.y + (k / _w + 0.5f) * _cell;
        for (int d = 0; d < 4; d++)
        {
            int j = k + Nx[d] + Ny[d] * _w;
            if (WaterSim.Open(j) && WaterSim.Passable(k, d)) continue;
            long key = WallDurability.CellKey(cx + Nx[d] * _cell * 0.5f, cy + Ny[d] * _cell * 0.5f);
            if (!Scalds.TryGetValue(key, out var w)) Scalds[key] = w = new Scald();
            if (w.Done) continue;
            w.Heat += heat;
            if (w.Queued || w.Heat < (w.Stage + 1) * stage) continue;
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
        if (BurnQueue.Count == 0 || !_ready || TerrainRewind.Playing) return; // 逆再生の間は焼かない (段に取り消されるかが割れる)
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
        var res = TerrainApi.WorldBurn(new Vector2(c.X, c.Y), new Vector2(Nx[w.D], Ny[w.D]));
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
        _act[_actN++] = k;
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
        if (_tileDirty[t] != 0) return;
        _tileDirty[t] = 1;
        DirtyTiles.Add(t);
    }

    // 升の順番に依らない指紋
    internal static uint Digest()
    {
        if (!_ready) return 0;
        uint acc = (uint)_step * 2654435761u;
        for (int a = 0; a < _actN; a++)
        {
            int k = _act[a];
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
        _actN = 0;
        Queue.Clear();
        Flares.Clear();
        DirtyTiles.Clear();
        Rooms.Clear();
        Steam.Clear(); FlareFx.Clear(); Arcs.Clear(); Chars.Clear();
        _step = 0;
        Steps = 0; Late = 0; BurningCount = 0; SteamTotal = 0; FlareTotal = 0; MaxStepMs = 0; VerifySteps = VerifyDiff = 0;
    }

    internal static string Describe() =>
        !_ready ? $"OK fire off {GameClock.Describe()}"
        : $"OK fire step={_step} water={WaterSim.Step} late={Late} queue={Queue.Count} active={Active.Count} burning={BurningCount} steam={SteamTotal} flares={FlareTotal} walls={Scalds.Count} wallHits={WallHits} lastWall={LastWallHit} digest={Digest():x8} stepMs={LastStepMs:0.000} maxStepMs={MaxStepMs:0.000} verify={VerifySteps}/{VerifyDiff} {GameClock.Describe()}";

    internal static string CellInfo(Vector2 p)
    {
        if (!_ready) return "ERR fire off";
        int x = (int)MathF.Floor((p.x - _org.x) / _cell), y = (int)MathF.Floor((p.y - _org.y) / _cell);
        if (x < 1 || y < 1 || x >= _w - 1 || y >= _h - 1) return "ERR fire out of map";
        int k = y * _w + x;
        EnsureTile(k);
        return $"OK fire at ({x},{y}) mat={(Mat)_mat[k]} t={_t[k]} fuel={_fuel[k]} burning={Burning(k)} burn={_burn[k]} char={_char[k]} base={(Mat)_base[k]} wall={WallOf(k)} open={WaterSim.Open(k)} water={WaterSim.Height(k)}";
    }
}
