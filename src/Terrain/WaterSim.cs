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
// 閉じた扉の升は閉じる (下に隙間のある扉は除く)。扉の開け閉めはホストが刻みを押して配った記録 (Decompression の扉の記録) の刻みで入る。
// 床の高さ (塗った注釈 / はしごの上下で決めた島の順位) が違う所では水は高い方から低い方へだけ流れる。
// 高い床の縁の先 (壁・崖の向こう) に視界の影を挟まずに低い床があれば、縁を越えた水は落ちて、落ちる時間の後に下の升へ入る
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
    private const int Gain = 56;              // 高さの差 → 流れ (/256)
    private const int Damp = 236;             // 前の刻みの流れを残す割合 (/256)
    private const int DryHold = 48;           // 乾いた升へは、これより高い時だけ流れ出す (水たまりの縁が止まる)
    private const int WetHold = 3;            // 濡れた升の間で流れない小さな差
    private const int MinDepth = 6;           // これより浅い升は少しずつ乾く
    // 吸い出し: 減圧の流れ (引く強さ /1024 × 気圧) に比例して口へ向かう管に流れを足す (勢いが残るので足すのは少しずつ)。
    // 口の際 (道のり DrainDist 以内) の升の水は宇宙へ出て消える
    private const int BlowGain = 6;           // 引く強さ 1024 あたり 1 刻みに足す流れ (/256)
    private const int BlowMax = 12;
    private const int DrainDist = Decompression.UnitDist / 2;
    private const int DrainGain = 64;         // 引く強さ 1024 あたり 1 刻みに消える割合 (/256)
    // 落ちる: 縁の升の水のうちこれより上の分が、1 刻みに FallGain/256 ずつ縁を越える
    private const int FallLip = 24;
    private const int FallGain = 72;
    private const sbyte NoLevel = sbyte.MinValue;   // 高さが決まらない升 (落ちも登りもしない)
    private const sbyte VoidLevel = -2;              // 奈落 (落ちた水は消える)
    // 縁の先の低い床を探す距離 (升)。真上から斜めに見ているので、南 (−y) の崖は面が長く見える
    private static readonly int[] FallScan = { 8, 8, 4, 24 };
    private const int SideScan = 16;          // 横を向いた崖の下の床を、外へ何列まで探すか
    private const float SideVoidOut = 0.7f;   // 横の縁から奈落へ落ちる水の、絵の上で外へ出る距離
    private const int ShadowMask = (1 << 10) | (1 << 11) | (1 << 13); // 視界を遮る層 (本編の Constants.ShadowMask と同じ)

    // 衝撃 (爆発など): 半径の中の水に、中心から外へ向かう流れを 1 回だけ足す (勢いは Damp で減っていく)
    private const int ShockGain = 192;        // 中心の升に足す流れ = 水の量 × 強さ/256 × これ/256
    private const float ShockReach = 1.6f;    // 爆発の半径の何倍まで水を押すか
    private const float ShockExtra = 0.5f;
    // 跳ね: 衝撃の半径の内側の水を粒にして外へ飛ばす (中心の升は水の量 × これ/256 を失う)。粒は壁で止まり、落ちた升に水を足す
    private const int SplashFrac = 200;
    private const int SplashMin = 8;          // これより少ない量は飛ばさない
    private const int SplashHigh = 128;       // 飛び出す高さ (1/8 単位)
    private const int SplashStep = 224;       // 1 刻みに進む距離の上限 (升 ×256)
    private const int SplashFlightMax = 30;
    // 押し: 起点から向きの先の扇形 (根元の半幅 PushBase 升 ×256・広がり PushSpread/256 = 約 30°)。
    // 流れは向き 3/4 + 起点から外へ 1/4 (扇の端は少し外へ逸れる)
    private const int PushBase = 256;
    private const int PushSpread = 148;
    private const int PushGain = 256;         // 足す流れ = 水の量 × 強さ/256 × 近さ/256 × これ/256

    // 漏れ: 開始から FullSteps は全開・その後 FadeSteps で止まる。最初の BurstSteps は多く遠い
    public const int FullSteps = 300, FadeSteps = 150, BurstSteps = 15;
    // 浸水: 口の周り 3×3 升へ 1 刻みに 速さ × InflowMax/256 の水を入れる (速さ 256 = 1 秒に深さ 1 単位を 60 升ぶん)
    private const int InflowMax = Full * 2;
    // 扉の噴き出し: 開いた扉の両側 (扉の幅・奥行き GushBand 升) の平均の深さの差が GushMin を超えたら、高い側の帯と扉の升の水に
    // 低い側へ向かう流れ (差 × GushGain/256・その升の水の量まで) を 1 回だけ足す。勢いは Damp で残るので水の塊が扉から押し出される
    private const int GushMin = Full / 8;
    private const int GushGain = 160;
    private const int GushBand = 4;
    // 噴き出しの勢い (水の量の流れだけでは深い水ほど遅く読めるので、物と体を押す流れを別に足す)。扉から噴く向きへ
    // 速さ 差 × JetGain/Full (上限 JetMax・1/1024 単位/刻み)・長さ 6 升 + 差 1 単位ごとに 6 升 (上限 JetLenMax)・先へ行くほど弱く・
    // 外へ 1/3 の割合で広がる。高い側は扉へ吸い寄せる流れ (半分の速さ・JetIntake 升)。JetLife 刻みで弱まって消える
    private const int JetGain = 64, JetMax = 200, JetLenMax = 24, JetIntake = 10, JetLife = 45;
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
    private static readonly int[] Pm = new int[MaxParticles];       // 粒の水の量
    internal static int Particles { get; private set; }
    // 今の刻みに床へ落ちた粒の位置 (升 ×256)。絵が波紋を出す
    internal static readonly List<(int X, int Y)> Landed = new();
    // 宇宙へ出た水 (位置と量)。TerrainStep が毎フレームの始めに空にし、演出 (DecompFx) が同じフレームで読む
    internal static readonly List<(float X, float Y, int Amount)> Spilled = new();
    internal static long SpilledTotal { get; private set; }
    // 今のフレームに縁を越えた水 (縁の位置・着く位置・量・落ちる刻み)。TerrainStep が毎フレームの始めに空にし、絵 (WaterLeak) が同じフレームで読む
    internal static readonly List<(float X0, float Y0, float X1, float Y1, int Amount, int Delay, bool Void, int Dir)> Falls = new();
    internal static long FellTotal { get; private set; }  // 縁を越えた水の総量
    internal static long VoidTotal { get; private set; }  // 奈落へ落ちて消えた水
    internal static int FallLinks => FallTo.Count;
    internal static bool FallEdge(int k) => _fallMask != null && _fallMask[k] != 0;
    internal static int LevelOf(int k) => _lvl == null ? 0 : _lvl[k];
    internal static bool DoorGapBelow(int i) => _doorUnder != null && i < _doorUnder.Length && _doorUnder[i];

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

    private sealed class Inflow
    {
        public int K;               // 口の升
        public int End;             // 止まる刻み (0 = 止まらない)
        public int Rate;            // 1 刻みに入れる量
    }

    private struct Pending
    {
        public int Tick;
        public bool Leak;
        public bool Inflow;         // 浸水: (Cx, Cy) の升へ Sp ずつ Sr 刻みのあいだ入れる (Sr = 0 は止まらない)
        public Source Src;
        public int Cx, Cy, R;       // 升を作り直す範囲 (升)
        public bool Shock;          // 作り直しの後に衝撃を足す
        public int Sx, Sy, Sr;      // 衝撃の中心 (升 ×256) と半径 (升 ×256)
        public int Sp, Bx, By;      // 強さ (0..256) と偏り (長さ ≈256 × 力)
        public bool Push;           // 押し: 地形は変わらないので作り直さない。(Bx, By) = 向き (長さ 256)・Sr = 届く長さ
        public ushort Seed;
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
    private static sbyte[] _lvl;     // 升の床の高さ (NoLevel = 決まらない)
    private static byte[] _shut;     // 閉じた扉が掛かっている数
    private static byte[] _doorCell; // 扉の升 (落ちる先を探す時に越えない)
    private static byte[] _propCell; // 床に置かれた物 (岩・結晶・机) の当たり判定の中の升
    private static bool[] _doorUnder; // 下に隙間があって水が通る扉
    private static ulong _doorBits;  // 升に入れた扉の開き
    private static byte[] _fallMask; // 縁を越えて落ちる向き (ビット = Nx/Ny の並び)
    private static byte[] _shadowCut; // 作った時に影の線を挟んでいた縁の向き
    private static byte[] _ledge;     // 作った時に縁の際に段の影の線があったか (LedgeNear)
    private static readonly Dictionary<int, (int To, int Delay)> FallTo = new(); // 升 × 4 + 向き → 落ちる先の升 (−1 = 奈落) と刻み
    private static readonly List<(int K, int D, int Amount)> FallOut = new();
    private static readonly List<(int Due, int To, int Amount)> Flying = new(); // 落ちている水 (着く刻みの順)
    private static byte[] _inList;
    private static readonly List<int> Active = new();
    private static readonly List<Pending> Queue = new();
    private static readonly List<Source> Sources = new();
    private static readonly List<Inflow> Inflows = new();
    internal static int InflowCount => Inflows.Count;
    internal static long InflowTotal { get; private set; }
    internal static int Gushes { get; private set; }
    private sealed class Jet { public int A0, A1, Lo, Hi, D, V, Len, Start; public bool Wide; }
    private static readonly List<Jet> Jets = new();
    // 扉の噴き出し (絵だけ・見せる側が毎フレーム読んで TerrainStep が消す): 扉の真ん中・噴く向き・扉の幅 (単位)・水位の差 (Full = 1 単位)
    internal static readonly List<(Vector2 At, int Dx, int Dy, float Width, int Diff)> GushFx = new();
    private static int _step;
    private static bool _running;

    // 確認用
    internal static int Late { get; private set; }
    internal static int Steps { get; private set; }
    internal static int Shocks { get; private set; }
    internal static int Splashes { get; private set; }
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
    internal static bool Running => _running;
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
        if (r.Kind == DamageKind.Flood) { AddInflow(r); return; }
        if (!_running || r.Kind.IsFire() || r.Kind == DamageKind.Water) return; // 放水の口は WaterLeak から AddLeak で入る
        int tick = GameClock.Expand(r.Tick);
        float rad = r.Kind == DamageKind.Explosion ? r.Size + 0.6f : 2f;
        int cx = (int)MathF.Floor((r.Position.x - _org.x) / _cell), cy = (int)MathF.Floor((r.Position.y - _org.y) / _cell);
        var p = new Pending { Tick = tick, Cx = cx, Cy = cy, R = (int)(rad / _cell) + 2 };
        if (r.Kind == DamageKind.Push)
        {
            p.Push = true;
            p.Sx = (int)MathF.Floor((r.Position.x - _org.x) / _cell * 256f);
            p.Sy = (int)MathF.Floor((r.Position.y - _org.y) / _cell * 256f);
            p.Sr = (int)(r.Size / _cell * 256f);
            p.Sp = (int)MathF.Round(r.Force * 256f);
            p.Bx = (int)MathF.Round(r.Direction.x * 256f);
            p.By = (int)MathF.Round(r.Direction.y * 256f);
            p.Seed = r.Seed;
            Enqueue(p);
            return;
        }
        if (r.Kind == DamageKind.Explosion)
        {
            p.Shock = true;
            p.Sx = (int)MathF.Floor((r.Position.x - _org.x) / _cell * 256f);
            p.Sy = (int)MathF.Floor((r.Position.y - _org.y) / _cell * 256f);
            p.Sr = (int)((r.Size * ShockReach + ShockExtra) / _cell * 256f);
            p.Sp = 256;
            p.Bx = (int)MathF.Round(r.Direction.x * r.Force * 256f);
            p.By = (int)MathF.Round(r.Direction.y * r.Force * 256f);
            p.Seed = r.Seed;
            int reach = p.Sr / 256 + 2;
            if (reach > p.R) p.R = reach;
        }
        Enqueue(p);
    }

    // 火 (FireSim) から: 升を用意して刻みを回し始める (水が無くても火は水の升と刻みで動く)
    internal static bool Begin(int tick)
    {
        if (!EnsureGrid()) return false;
        if (!_running) { _running = true; _step = StartStep(tick); }
        return true;
    }

    // 火の熱で升の水を減らす (湯気)。減らした量
    internal static int Evaporate(int k, int amount)
    {
        int h = _hgt[k];
        if (amount > h) amount = h;
        if (amount <= 0) return 0;
        _hgt[k] = h - amount;
        Mark(k);
        return amount;
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
            _step = StartStep(tick);
        }
        PropSim.WakeForWater(tick);
        int dx = (int)MathF.Round(n.x * 256f), dy = (int)MathF.Round(n.y * 256f);
        int mx = (int)MathF.Floor((at.x - _org.x) / _cell * 256f) + dx * MouthQ / 4;
        int my = (int)MathF.Floor((at.y - _org.y) / _cell * 256f) + dy * MouthQ / 4;
        var src = new Source { Start = tick, Mx = mx, My = my, Dx = dx, Dy = dy, Both = both, At = at, N = n, Seed = seed, Rng = 0x9E3779B9u ^ seed * 2654435761u };
        Enqueue(new Pending { Tick = tick, Leak = true, Src = src });
    }

    private static void AddInflow(in ResolvedDamage r)
    {
        if (!EnsureGrid()) return;
        int tick = GameClock.Expand(r.Tick);
        if (!_running) { _running = true; _step = StartStep(tick); }
        PropSim.WakeForWater(tick);
        int cx = (int)MathF.Floor((r.Position.x - _org.x) / _cell), cy = (int)MathF.Floor((r.Position.y - _org.y) / _cell);
        if (cx < 1 || cy < 1 || cx >= _w - 1 || cy >= _h - 1) return;
        int dur = (int)MathF.Round(r.Size * 10f * GameClock.Hz);
        int rate = (int)((long)InflowMax * (int)MathF.Round(r.Force * 256f) / 256);
        if (rate <= 0) return;
        Enqueue(new Pending { Tick = tick, Inflow = true, Cx = cx, Cy = cy, Sr = dur, Sp = rate });
    }

    // 始める刻み。減圧が先へ進んでいたらそこから (古い刻みの気圧と流れは残っていないので、先の場を読まないように)。
    // それより前の出来事は遅れとして始めの刻みに入る
    private static int StartStep(int tick)
    {
        int s = Math.Min(tick, GameClock.Now - Delay);
        return Decompression.Running && Decompression.Step > s ? Decompression.Step : s;
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
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
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
        _shut = new byte[n];
        _doorCell = new byte[n];
        _propCell = new byte[n];
        _fallMask = new byte[n];
        _shadowCut = new byte[n];
        _ledge = new byte[n];
        FallTo.Clear();
        ListDoorCells();
        int props = ListPropCells();
        _tw = (_w + TileCells - 1) / TileCells;
        _th = (_h + TileCells - 1) / TileCells;
        _tileDirty = new bool[_tw * _th];
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        Rebuild(0, 0, _w - 1, _h - 1);
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        Levels();
        long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        Links(0, 0, _w - 1, _h - 1, true);
        long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
        double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Plugin.Logger.LogInfo($"[WaterSim] grid {_w}x{_h} alloc={(t1 - t0) * f:0.00}ms rebuild={(t2 - t1) * f:0.00} levels={(t3 - t2) * f:0.00} links={(t4 - t3) * f:0.00} n={FallTo.Count} props={props} hash={GridHash():x8}");
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
        var solid = SolidMap.OpenCells;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int bits = 0;
            for (int sy = 0, row = y * Sub * sw + x * Sub; sy < Sub; sy++, row += sw)
            for (int sx = 0; sx < Sub; sx++)
                if (solid[row + sx] != 0) bits |= 1 << (sy * Sub + sx);
            int k = y * _w + x;
            if (PopCount(bits) < 4 || !Connected(bits) || _shut[k] != 0) bits = 0;
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
            if (_ready) Mark(k); // 升を作る時の印は作り終えた所で捨てる
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

    // ── 扉 ─────────────────────────────────────────────────────────────

    // 扉の升 (Decompression と同じ一覧・同じ升)。下に隙間のある扉は水を止めない
    private static void ListDoorCells()
    {
        int n = Decompression.DoorCount;
        _doorUnder = new bool[n];
        _doorBits = Decompression.AllDoorsOpen;
        for (int i = 0; i < n; i++)
        {
            _doorUnder[i] = GapBelow(Decompression.DoorAtIndex(i));
            var (x0, y0, x1, y1) = Decompression.DoorRect(i);
            for (int y = Math.Max(0, y0); y <= Math.Min(_h - 1, y1); y++)
            for (int x = Math.Max(0, x0); x <= Math.Min(_w - 1, x1); x++) _doorCell[y * _w + x] = 1;
        }
    }

    // 升作りの結果 (物の升・影の線で切った向き・落ちる先) の指紋。作り方を変えても同じになることの確認用
    private static uint GridHash()
    {
        uint h = 2166136261;
        for (int i = 0; i < _propCell.Length; i++) h = (h ^ (uint)(_propCell[i] | _shadowCut[i] << 1 | _ledge[i] << 9)) * 16777619;
        uint sum = 0;
        foreach (var kv in FallTo) sum += (uint)(kv.Key * 73856093) ^ (uint)(kv.Value.To * 19349663) ^ (uint)(kv.Value.Delay * 83492791);
        return h ^ sum;
    }
    // 床に置かれた物: 壁の線と別に置かれた小さな閉じた当たり判定 (岩・結晶・机)。縁のすぐ隣がその中なら落ちない
    // (先を探すと物の中を素通りして、物の向こうの崖へ落ちる)。どの升も中の 5 点のどれかが当たり判定の中なら物の升。
    // 点は端末ごとの丸めで割れないよう 1/512 格子の間に置く
    // 形は歩ける所の地図 (SolidMap) を作った時に集めてある。そこから今切ってある物 (壊れた家具) を除く
    private static int ListPropCells()
    {
        if (!ShipStatus.Instance) return -1;
        int cols = 0;
        foreach (var (c, bx0, by0, bx1, by1, start, end) in SolidMap.SmallShapes)
        {
            if (!c || !c.enabled || !c.gameObject.activeInHierarchy) continue;
            cols++;
            MarkProp(SolidMap.SmallSegs, start, end, bx0, by0, bx1, by1);
        }
        var seg = new List<Vector2>();
        foreach (var c in SolidMap.LateShapes)
        {
            if (!c || !c.enabled || !c.gameObject.activeInHierarchy) continue;
            var b = c.bounds;
            float bx0 = b.min.x, by0 = b.min.y, bx1 = b.max.x, by1 = b.max.y;
            if (bx1 - bx0 > SolidMap.SmallShapeMax || by1 - by0 > SolidMap.SmallShapeMax || bx1 - bx0 + by1 - by0 < 0.05f) continue;
            cols++;
            seg.Clear();
            SolidMap.Segments(c, seg);
            MarkProp(seg, 0, seg.Count, bx0, by0, bx1, by1);
        }
        return cols;
    }

    private static void MarkProp(List<Vector2> seg, int start, int end, float bx0, float by0, float bx1, float by1)
    {
        const float nudge = 1f / 1024f;
        int x0 = Math.Max(0, (int)MathF.Floor((bx0 - _org.x) / _cell) - 1), x1 = Math.Min(_w - 1, (int)MathF.Floor((bx1 - _org.x) / _cell) + 1);
        int y0 = Math.Max(0, (int)MathF.Floor((by0 - _org.y) / _cell) - 1), y1 = Math.Min(_h - 1, (int)MathF.Floor((by1 - _org.y) / _cell) + 1);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            float px = _org.x + (x + 0.5f) * _cell + nudge, py = _org.y + (y + 0.5f) * _cell + nudge, q = _cell * 0.375f;
            if (InsideSegs(seg, start, end, px, py) || InsideSegs(seg, start, end, px - q, py) || InsideSegs(seg, start, end, px + q, py) ||
                InsideSegs(seg, start, end, px, py - q) || InsideSegs(seg, start, end, px, py + q))
                _propCell[y * _w + x] = 1;
        }
    }

    // 線分の組 (a, b, a, b, ...) で囲まれた中か (偶奇)
    private static bool InsideSegs(List<Vector2> seg, int start, int end, float x, float y)
    {
        bool inside = false;
        for (int i = start; i + 1 < end; i += 2)
        {
            float ax = seg[i].x, ay = seg[i].y, bx = seg[i + 1].x, by = seg[i + 1].y;
            if ((ay > y) != (by > y) && x < (bx - ax) * (y - ay) / (by - ay) + ax) inside = !inside;
        }
        return inside;
    }

    // 下に隙間があって水が通る扉 (エアシップのラウンジのトイレの個室の扉)
    private static bool GapBelow(OpenableDoor d) => d && d.name.Contains("bathroomdoor", StringComparison.OrdinalIgnoreCase);

    // 刻みの扉の開きを升へ入れる (変わった扉の升だけ作り直す)
    private static void ApplyDoors(ulong bits)
    {
        // 船の準備前に作った時は扉が 0 枚のままなので、扉が見えたら升を取り直す
        if (_doorUnder.Length == 0 && Decompression.DoorCount > 0) ListDoorCells();
        ulong changed = bits ^ _doorBits;
        _doorBits = bits;
        int n = Math.Min(_doorUnder.Length, Decompression.DoorCount);
        for (int i = 0; i < n; i++)
        {
            if ((changed >> i & 1) == 0 || _doorUnder[i]) continue;
            bool open = (bits >> i & 1) != 0;
            var (x0, y0, x1, y1) = Decompression.DoorRect(i);
            for (int y = Math.Max(0, y0); y <= Math.Min(_h - 1, y1); y++)
            for (int x = Math.Max(0, x0); x <= Math.Min(_w - 1, x1); x++)
            {
                int k = y * _w + x;
                if (open) { if (_shut[k] > 0) _shut[k]--; }
                else if (_shut[k] < 255) _shut[k]++;
            }
            Rebuild(x0 - 1, y0 - 1, x1 + 1, y1 + 1);
            if (open) Gush(x0, y0, x1, y1);
        }
    }

    // 開いた扉の両側の水位の差で噴き出す (ApplyDoors から・確定の刻みの中)
    private static void Gush(int x0, int y0, int x1, int y1)
    {
        // 扉の長い辺に沿って並ぶ升の列の外側が両側。横長の扉は上下へ、縦長の扉は左右へ噴く
        bool wide = x1 - x0 >= y1 - y0;
        int a0 = wide ? x0 : y0, a1 = wide ? x1 : y1;   // 扉の幅の向き
        int lo = wide ? y0 : x0, hi = wide ? y1 : x1;   // 扉の厚みの向き
        long sumA = 0, sumB = 0;
        int nA = 0, nB = 0;
        for (int a = a0; a <= a1; a++)
            for (int t = 1; t <= GushBand; t++)
            {
                int ka = wide ? CellAt(a, lo - t) : CellAt(lo - t, a), kb = wide ? CellAt(a, hi + t) : CellAt(hi + t, a);
                if (ka >= 0 && _open[ka] != 0) { sumA += _hgt[ka]; nA++; }
                if (kb >= 0 && _open[kb] != 0) { sumB += _hgt[kb]; nB++; }
            }
        if (nA == 0 || nB == 0) return;
        int hA = (int)(sumA / nA), hB = (int)(sumB / nB);
        int diff = hA - hB;
        if (Math.Abs(diff) < GushMin) return;
        // 噴く向き (Nx/Ny の並び): 横長の扉は +y (2) か −y (3)・縦長は +x (0) か −x (1)。A 側が高ければ B 側へ
        int d = wide ? (diff > 0 ? 2 : 3) : (diff > 0 ? 0 : 1);
        int kick = Math.Abs(diff) * GushGain / 256;
        int from = diff > 0 ? -GushBand : 0, to = diff > 0 ? 0 : GushBand;  // 高い側の帯 (扉の升を含む)
        int n = 0;
        for (int a = a0; a <= a1; a++)
            for (int t = lo + from; t <= hi + to; t++)
            {
                int k = wide ? CellAt(a, t) : CellAt(t, a);
                if (k < 0 || _open[k] == 0 || _hgt[k] <= 0) continue;
                _flux[k * 4 + d] += Math.Min(_hgt[k], kick);
                Wake(k);
                n++;
            }
        if (n == 0) return;
        Gushes++;
        int len = Math.Min(6 + Math.Abs(diff) * 6 / Full, JetLenMax);
        Jets.Add(new Jet { A0 = a0, A1 = a1, Lo = lo, Hi = hi, D = d, Wide = wide, V = Math.Min(Math.Abs(diff) * JetGain / Full, JetMax), Len = len, Start = _step });
        float cx = _org.x + ((x0 + x1 + 1) * 0.5f) * _cell, cy = _org.y + ((y0 + y1 + 1) * 0.5f) * _cell;
        GushFx.Add((FxMath.V2(cx, cy), Nx[d], Ny[d], (a1 - a0 + 1) * _cell, Math.Abs(diff)));
    }

    // 噴き出しの勢いをその升に足す (1/1024 単位/刻み)。噴流が無ければ何もしない
    private static void JetAt(int x, int y, ref int vx, ref int vy)
    {
        for (int i = 0; i < Jets.Count; i++)
        {
            var j = Jets[i];
            int age = _step - j.Start;
            if (age < 0 || age >= JetLife) continue;
            int a = j.Wide ? x : y, t = j.Wide ? y : x;
            // 扉からの距離 (噴く向きを +・扉の厚みの中は 0) と、扉の幅から横へはみ出した升数
            bool plus = j.D == 0 || j.D == 2;
            int along = plus ? (t > j.Hi ? t - j.Hi : t < j.Lo ? t - j.Lo : 0)
                             : (t < j.Lo ? j.Lo - t : t > j.Hi ? j.Hi - t : 0);
            int side = a < j.A0 ? j.A0 - a : a > j.A1 ? a - j.A1 : 0;
            int v;
            if (along >= 0)
            {
                if (along >= j.Len || side * 3 > along) continue;
                v = j.V * (j.Len - along) / j.Len;
            }
            else
            {
                if (-along >= JetIntake || side > -along) continue;
                v = j.V * (JetIntake + along) / (JetIntake * 2);
            }
            v = v * (JetLife - age) / JetLife;
            vx += Nx[j.D] * v;
            vy += Ny[j.D] * v;
        }
    }

    private static int CellAt(int x, int y) => x < 0 || y < 0 || x >= _w || y >= _h ? -1 : y * _w + x;

    // ── 床の高さと落ちる縁 ───────────────────────────────────────────────

    // 升の高さ: 注釈を塗ったマップは区域と注釈。塗っていない所と注釈の無いマップは、はしごの上下で決めた島の順位
    private static void Levels()
    {
        int n = _w * _h;
        _lvl = new sbyte[n];
        if (MapNotes.HasLevels)
        {
            HeightLevels.RasterZones(_lvl, _w, _h, _org, _cell);
            var zone = (sbyte[])_lvl.Clone();
            MapNotes.RasterLevels(_lvl, _w, _h, _org, _cell);
            var rank = IslandLevels();
            // 注釈は壁の上に引いた線 (越えられない縁・奈落は壊せない所の印も兼ねる) で、升に写すと隣の歩ける床へ 1〜3 升はみ出す。
            // はみ出した床が高い床になると周りの水が上れず、壁際に乾いた筋が残る。始めから歩ける升は区域 (無ければ島) の高さだけを使い、
            // 注釈の高さは壁の中の升 (壊して開いた時の縁) にだけ残す
            for (int k = 0; k < n; k++)
                if (_sub[k] != 0) _lvl[k] = zone[k] != 0 || rank[k] == NoLevel ? zone[k] : (sbyte)Math.Max((int)rank[k], VoidLevel + 1);
            return;
        }
        var lv = IslandLevels();
        Array.Copy(lv, _lvl, n);
    }

    // 升ごとの島の順位 (升の中の最初の歩ける升の島・はしごでつながらない島は NoLevel・歩けない升は 0)
    private static sbyte[] IslandLevels()
    {
        int n = _w * _h;
        var lvl = new sbyte[n];
        var isl = new byte[n];
        var area = new int[SolidMap.IslandCount + 1];
        int sw = SolidMap.W;
        for (int k = 0; k < n; k++)
        {
            int bits = _sub[k];
            if (bits == 0) continue;
            int b = System.Numerics.BitOperations.TrailingZeroCount(bits);
            int x = k % _w, y = k / _w;
            int id = SolidMap.IslandCell((y * Sub + b / Sub) * sw + x * Sub + b % Sub);
            if (id <= 0 || id >= area.Length) continue;
            isl[k] = (byte)id;
            area[id]++;
        }
        var rank = HeightLevels.IslandRanks(i => area[i]);
        for (int k = 0; k < n; k++)
        {
            int id = isl[k];
            lvl[k] = id == 0 ? (sbyte)0 : rank[id] == int.MinValue ? NoLevel : (sbyte)Math.Clamp(rank[id], -100, 100);
        }
        return lvl;
    }

    // 範囲の升の、縁を越えて落ちる向きと落ちる先を作り直す。縁 (隣へ流れられない向き) の先を FallScan 升まで進み、
    // 最初の開いた升が低ければ落ちる先 (奈落を先に通れば奈落)。扉の升を越える所と、視界の影の線を挟む所 (壁の向こうの部屋) は落ちない。
    // 奈落へ落ちる所の決め方は Target に
    // 影の線は壊れた壁と一緒に切られ、壊れの届き方は人によって先後があるので、線を読むのは作った時 (full) だけ。
    // 引き直しはその時の結果 (_shadowCut) を引く
    private static void Links(int x0, int y0, int x1, int y1, bool full = false)
    {
        Rooms.Clear();
        var ship = ShipStatus.Instance;
        if (ship)
            foreach (var r in ship.AllRooms)
                if (r && r.roomArea && Array.IndexOf(ChasmRooms, r.RoomId) >= 0) Rooms.Add(r.roomArea);
        bool airship = ship && ship.TryCast<AirshipStatus>() != null;
        _decks = airship ? AirshipDecks : NoDecks;
        _skyEdges = airship ? AirshipSkyEdges : NoDecks;
        x0 = Math.Max(1, x0); y0 = Math.Max(1, y0);
        x1 = Math.Min(_w - 2, x1); y1 = Math.Min(_h - 2, y1);
        // 高さをはしごの島で決めたマップ (注釈なし) では、高さの違う島の間は必ず崖なので屋外の影の線を見ない
        // (崖の上の縁に視界の影の線が引いてあり、それで切ると崖の大半から落ちなくなる)。部屋の影の線 (展望台の箱と柵の縁) は見る
        bool cliffs = !MapNotes.HasLevels;
        if (full) ListLadders(ship);
        _ledgeScan = full;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int k = y * _w + x;
            if (_fallMask[k] != 0)
            {
                for (int d = 0; d < 4; d++) FallTo.Remove(k * 4 + d);
                _fallMask[k] = 0;
            }
            int lk = _lvl[k];
            if (_open[k] == 0 || lk == NoLevel || lk == VoidLevel) continue;
            for (int d = 0; d < 4; d++)
            {
                int j = k + Nx[d] + Ny[d] * _w;
                if (_open[j] != 0 && Passable(k, d)) continue;
                if (Nx[d] != 0 && OnLadder(x, y)) continue;
                Target(x, y, d, lk, cliffs, out int to, out int dist, out int tx, out int ty);
                if (to == int.MinValue || to == -1 && OnLadder(x, y)) continue;
                // 空へ落とす縁は壁の影の線の外側へ落ちるので、影の線では切らない
                if (full && !(to == -1 && OnSkyEdge(x, y)) && ShadowBetween(x, y, tx, ty, cliffs)) _shadowCut[k] |= (byte)(1 << d);
                if ((_shadowCut[k] >> d & 1) != 0) continue;
                _fallMask[k] |= (byte)(1 << d);
                FallTo[k * 4 + d] = (to, 3 + 2 * ISqrt(dist));
            }
        }
        _ledgeScan = false;
    }

    // 最初に水が出た時の升作りで払っていた準備 (段の注釈の読み込み・部屋の範囲・影の線の判定の初回) を先に済ませる
    internal static void Warm()
    {
        _ = MapNotes.HasLevels;
        HeightLevels.Warm();
        var ship = ShipStatus.Instance;
        if (!ship) return;
        foreach (var r in ship.AllRooms)
            if (r && r.roomArea) { _ = r.roomArea.OverlapPoint(Vector2.zero); break; }
        _ = Physics2D.CircleCastAll(Vector2.zero, 0.01f, Vector2.right, 0.1f, ShadowMask).Length;
    }

    // はしごの通り道 (x0, y0, x1, y1)。はしごの上の床の切れ目の脇の升は横向きの縁になるが、そこから横へ落とすと
    // はしごを挟んで斜めの滝が 2 本出る。はしごの切れ目からは真下 (はしご沿い) の床にだけ落とす (奈落へは落とさない)
    private const float LadderHalf = 0.6f, LadderOver = 0.5f;
    private static readonly List<float> Ladders = new();
    private static void ListLadders(ShipStatus ship)
    {
        Ladders.Clear();
        if (!ship) return;
        foreach (var l in ship.GetComponentsInChildren<Ladder>(true))
        {
            if (!l || !l.IsTop || !l.Destination) continue;
            Vector3 a = l.transform.position, b = l.Destination.transform.position;
            Ladders.Add(MathF.Min(a.x, b.x) - LadderHalf);
            Ladders.Add(MathF.Min(a.y, b.y));
            Ladders.Add(MathF.Max(a.x, b.x) + LadderHalf);
            Ladders.Add(MathF.Max(a.y, b.y) + LadderOver);
        }
    }

    private static bool OnLadder(int x, int y)
    {
        float px = _org.x + (x + 0.5f) * _cell, py = _org.y + (y + 0.5f) * _cell;
        for (int i = 0; i + 3 < Ladders.Count; i += 4)
            if (px >= Ladders[i] && px <= Ladders[i + 2] && py >= Ladders[i + 1] && py <= Ladders[i + 3]) return true;
        return false;
    }

    private static readonly SystemTypes[] ChasmRooms = { SystemTypes.GapRoom, SystemTypes.Ventilation };
    private static readonly List<Collider2D> Rooms = new();
    private static bool InRoom(float ax, float ay, float bx, float by)
    {
        var a = new Vector2(ax, ay);
        var b = new Vector2(bx, by);
        foreach (var c in Rooms) if (c && c.OverlapPoint(a) && c.OverlapPoint(b)) return true;
        return false;
    }

    // 手すりだけで囲まれた屋外のデッキの床 (x0, y0, x1, y1)。エアシップの警備室の下 (扉の前の通路は両脇が壁なので入れない)。
    // 境目は端末ごとの丸めで割れないよう 1/512 格子の点から外す
    private static readonly float[] AirshipDecks = { 5.003f, -17.497f, 10.997f, -14.3f };
    private static readonly float[] NoDecks = { };
    private static float[] _decks = NoDecks;
    private static bool OnDeck(int x, int y) => OnDeck(_org.x + (x + 0.5f) * _cell, _org.y + (y + 0.5f) * _cell);
    internal static bool OnDeck(float px, float py) => InRects(_decks, px, py);
    private static bool InRects(float[] r, float px, float py)
    {
        for (int i = 0; i + 3 < r.Length; i += 4)
            if (px >= r[i] && px <= r[i + 2] && py >= r[i + 1] && py <= r[i + 3]) return true;
        return false;
    }

    // 縁の下が船の外の空なのに、奈落の注釈も段の影の線も無い床 (x0, y0, x1, y1)。ここの縁 (上向き以外) は先に床が無ければ空へ落とす。
    // エアシップの展望デッキ (扉の前の通路は両脇が壁なので入れない)
    private static readonly float[] AirshipSkyEdges = { -14.897f, -17.497f, -12.403f, -14.3f };
    private static float[] _skyEdges = NoDecks;
    private static bool OnSkyEdge(int x, int y) => InRects(_skyEdges, _org.x + (x + 0.5f) * _cell, _org.y + (y + 0.5f) * _cell);

    // 縁の升 (x, y) の向き d の落ちる先。to = 落ちる先の升・-1 = 奈落 (落ちた水は消える)・int.MinValue = 落ちない。返り値は理由 (確認用)。
    // 奈落の注釈は壊せない所の印も兼ねて外壁の外にも塗ってあるので、奈落へ落ちるのは谷のある部屋 (ChasmRooms) の中か、
    // 屋外のデッキの床 (_decks) の縁から真っすぐ届く所 (手すりの外の空) か、縁の際に段の影の線がある所 (足場の手すりの外) だけ。外壁には影の線も船体の塊も無い所
    // (コックピットのガラス窓・扉の脇の柱) があり、それでは手すりと見分けられない。
    // 高さをはしごの島で決めたマップでは、一番下の島より高い島の縁の先に何も無ければ崖として奈落へ落とす
    // (一番下の島の縁は浜や森との境なので落とさない・上向きの縁は落ちる絵が床に被るので落とさない)
    private static string Target(int x, int y, int d, int lk, bool cliffs, out int to, out int dist, out int tx, out int ty)
    {
        to = int.MinValue; dist = 0; tx = x; ty = y;
        string why = "scan";
        for (int i = 1; i <= FallScan[d]; i++)
        {
            int cx = x + Nx[d] * i, cy = y + Ny[d] * i;
            if (cx < 1 || cy < 1 || cx >= _w - 1 || cy >= _h - 1) { why = "border"; break; }
            int c = cy * _w + cx;
            if (_doorCell[c] != 0) return "door";
            if (i == 1 && _propCell[c] != 0 && _open[c] == 0) return "prop";
            int lc = _lvl[c];
            tx = cx; ty = cy; dist = i;
            if (_open[c] == 0)
            {
                if (lc == VoidLevel) { to = -1; why = "void"; break; }
                continue;
            }
            if (lc != NoLevel && lc < lk) { to = c; return "fall"; }
            return lc == NoLevel ? "nolevel" : "same";
        }
        bool seen = false;
        if (why == "scan" && Nx[d] != 0)
        {
            SideDrop(x, y, d, lk, ref to, ref dist, ref tx, ref ty, ref seen);
            if (to >= 0) return "side";
            if (to == -1) why = "side";
        }
        if (to == int.MinValue)
        {
            // 段の縁の先に床が無い (足場の手すりの外) = 虚空へ落ちる
            if (Ny[d] <= 0 && !seen && why != "door" && LedgeNear(x, y, d)) { to = -1; return "ledge"; }
            if (Ny[d] <= 0 && !seen && why == "scan" && OnSkyEdge(x, y)) { to = -1; return "sky"; }
            if (!cliffs || lk <= 0 || Ny[d] > 0 || seen || why == "door") return why;
            to = -1;
            return "cliff";
        }
        // 奈落へは上向き (画面の上) には落とさない (落ちる絵が縁の向こうの壁に被る)
        if (to == -1 && Ny[d] > 0) { to = int.MinValue; return "up"; }
        // 谷の奈落は縁と同じ部屋の中だけ (隣の部屋の谷へ壁越しに落とさない)
        if (InRoom(_org.x + (x + 0.5f) * _cell, _org.y + (y + 0.5f) * _cell, _org.x + (tx + 0.5f) * _cell, _org.y + (ty + 0.5f) * _cell)) return why;
        if (why == "void" && (OnDeck(x, y) || OnSkyEdge(x, y))) return "sky";
        if (Ny[d] <= 0 && LedgeNear(x, y, d)) return "ledge";
        to = int.MinValue;
        return "room";
    }

    // 横を向いた崖: 真横の先が壁の中のままなら、外へ 1 列ずつずらしながら画面の下へ探す。真上から斜めに見ているので、
    // 横を向いた崖の下の床は真横でなく斜め下に見える。いちばん近い低い床 (か谷の奈落) を落ちる先にする。
    // 列を下りて先に同じ高さ以上の床に当たった列は使わない (崖の上の床が下へ張り出している所)
    // seen = 探した中に開いた升が 1 つでもあった (崖ではない)
    private static void SideDrop(int x, int y, int d, int lk, ref int to, ref int dist, ref int tx, ref int ty, ref bool seen)
    {
        int best = int.MaxValue;
        for (int i = 1; i <= SideScan; i++)
        {
            int cx = x + Nx[d] * i;
            if (cx < 1 || cx >= _w - 1 || i * i >= best) break;
            for (int j = 1; j <= FallScan[3] && i * i + j * j < best; j++)
            {
                int cy = y - j;
                if (cy < 1) break;
                int c = cy * _w + cx;
                if (_doorCell[c] != 0) break;
                int lc = _lvl[c];
                if (_open[c] == 0)
                {
                    if (lc != VoidLevel) continue;
                    best = i * i + j * j; to = -1; dist = j; tx = cx; ty = cy;
                    break;
                }
                seen = true;
                if (lc != NoLevel && lc < lk) { best = i * i + j * j; to = c; dist = j; tx = cx; ty = cy; }
                break;
            }
        }
    }

    // 縁の升の真ん中から落ちる先の升の手前までに視界の影の線があるか (あれば壁の向こうの部屋)。
    // roomOnly = 屋外 (船の Outside の下) の影の線は数えず、部屋の影の線だけを縁の際 (升の半分手前から RoomShadowReach 升) で見る。
    // 部屋の縁の影の線は歩ける所の境とほぼ重なり、升の真ん中より奥にあることがある。先まで伸ばすと崖の下の別の部屋の線に当たる
    private const float RoomShadowReach = 2f;
    private static readonly Dictionary<int, bool> OutsideShadow = new();
    private static bool ShadowBetween(int x, int y, int tx, int ty, bool roomOnly)
    {
        float ax = _org.x + (x + 0.5f) * _cell, ay = _org.y + (y + 0.5f) * _cell;
        float dx = tx - x, dy = ty - y, len = MathF.Sqrt(dx * dx + dy * dy);
        float ux = dx / len, uy = dy / len;
        // Linecast は Android 版に無いので、細い CircleCastAll で代える
        if (!roomOnly)
        {
            foreach (var h in Physics2D.CircleCastAll(new Vector2(ax, ay), 0.01f, new Vector2(ux, uy), (len - 0.5f) * _cell, ShadowMask))
                if (h.collider && !IsLedge(h.collider)) return true;
            return false;
        }
        var hits = Physics2D.CircleCastAll(new Vector2(ax - ux * 0.5f * _cell, ay - uy * 0.5f * _cell), 0.01f, new Vector2(ux, uy),
                                           MathF.Min(len, RoomShadowReach) * _cell, ShadowMask);
        foreach (var h in hits)
        {
            var c = h.collider;
            if (!c || IsLedge(c)) continue;
            int id = c.GetInstanceID();
            if (!OutsideShadow.TryGetValue(id, out bool outside))
            {
                outside = false;
                for (var t = c.transform; t && !outside; t = t.parent) outside = t.name == "Outside";
                OutsideShadow[id] = outside;
            }
            if (!outside) return true;
        }
        return false;
    }

    private static string ShadowName(int x, int y, int tx, int ty)
    {
        float ax = _org.x + (x + 0.5f) * _cell, ay = _org.y + (y + 0.5f) * _cell;
        float dx = tx - x, dy = ty - y, len = MathF.Sqrt(dx * dx + dy * dy);
        var hits = Physics2D.CircleCastAll(new Vector2(ax, ay), 0.01f, new Vector2(dx / len, dy / len), (len - 0.5f) * _cell, ShadowMask);
        foreach (var h in hits)
            if (h.collider) return $"{(h.collider.transform.parent ? h.collider.transform.parent.name + "/" : "")}{h.collider.name}@{h.distance:0.00}";
        return "-";
    }

    // 段の影の線: 本編は足場や段の縁の視界の影の線を LedgeShadow と名付けている (壁の影の線とは別)。
    // 縁の升の真ん中の升半分手前から LedgeReach 升先までにあれば、その縁は段の縁
    private const float LedgeReach = 2f;
    private static readonly Dictionary<int, bool> LedgeShadow = new();
    private static bool IsLedge(Collider2D c)
    {
        int id = c.GetInstanceID();
        if (!LedgeShadow.TryGetValue(id, out bool ledge))
        {
            ledge = c.name.Contains("Ledge", StringComparison.OrdinalIgnoreCase);
            LedgeShadow[id] = ledge;
        }
        return ledge;
    }

    // 影の線は壊れた壁と一緒に切られ人によって先後があるので、読むのは作った時 (_ledgeScan) だけ。結果は _ledge に残し
    // (ビット d = 調べた・ビット 4 + d = 段の縁)、引き直しでは残した結果だけを引く
    private static bool _ledgeScan;
    private static bool LedgeNear(int x, int y, int d)
    {
        int k = y * _w + x;
        if ((_ledge[k] >> d & 1) != 0) return (_ledge[k] >> (4 + d) & 1) != 0;
        if (!_ledgeScan) return false;
        bool hit = LedgeCast(x, y, d);
        _ledge[k] |= (byte)(1 << d | (hit ? 1 << (4 + d) : 0));
        return hit;
    }

    private static bool LedgeCast(int x, int y, int d)
    {
        float ax = _org.x + (x + 0.5f - Nx[d] * 0.5f) * _cell, ay = _org.y + (y + 0.5f - Ny[d] * 0.5f) * _cell;
        foreach (var h in Physics2D.CircleCastAll(new Vector2(ax, ay), 0.01f, new Vector2(Nx[d], Ny[d]), (LedgeReach + 0.5f) * _cell, ShadowMask))
            if (h.collider && IsLedge(h.collider)) return true;
        return false;
    }

    // 確認用 (water why): Links と同じ探し方で、縁の升ごとに落ちる/落ちない理由を数える
    private static string WhyNoFall(float wx, float wy, float wr, string list = null)
    {
        var listed = new System.Text.StringBuilder();
        int nl = 0;
        var count = new Dictionary<string, int>();
        var sample = new Dictionary<string, string>();
        int cx = (int)MathF.Floor((wx - _org.x) / _cell), cy = (int)MathF.Floor((wy - _org.y) / _cell), r = (int)(wr / _cell);
        for (int y = Math.Max(1, cy - r); y <= Math.Min(_h - 2, cy + r); y++)
        for (int x = Math.Max(1, cx - r); x <= Math.Min(_w - 2, cx + r); x++)
        {
            int k = y * _w + x;
            int lk = _lvl[k];
            if (_open[k] == 0 || lk == NoLevel || lk == VoidLevel) continue;
            for (int d = 0; d < 4; d++)
            {
                int j = k + Nx[d] + Ny[d] * _w;
                if (_open[j] != 0 && Passable(k, d)) continue;
                int to = int.MinValue;
                int tx = x, ty = y;
                string why = Nx[d] != 0 && OnLadder(x, y) ? "ladder" : Target(x, y, d, lk, !MapNotes.HasLevels, out to, out _, out tx, out ty);
                if (to == -1 && OnLadder(x, y)) { to = int.MinValue; why = "ladder"; }
                if (to != int.MinValue && (_shadowCut[k] >> d & 1) != 0) why = "shadow";
                if (why == "shadow" && !sample.ContainsKey("+x-x+y-y".Substring(d * 2, 2) + ":" + why)) why += "[" + ShadowName(x, y, tx, ty) + "]";
                int lto = to >= 0 ? _lvl[to] : 0;
                string key = "+x-x+y-y".Substring(d * 2, 2) + ":" + why;
                count[key] = count.TryGetValue(key, out int n) ? n + 1 : 1;
                if (key == list && nl++ < 80) listed.Append($" ({_org.x + (x + 0.5f) * _cell:0.0},{_org.y + (y + 0.5f) * _cell:0.0})");
                if (!sample.ContainsKey(key)) sample[key] = $"({_org.x + (x + 0.5f) * _cell:0.0},{_org.y + (y + 0.5f) * _cell:0.0})L{lk}>{lto}";
            }
        }
        if (list != null) return $"OK water why {list} n={nl}{listed}";
        var sb = new System.Text.StringBuilder("OK water why");
        foreach (var kv in count) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value).Append(sample[kv.Key]);
        return sb.ToString();
    }

    // 2 つの升の境で向かい合う歩ける升の組の数。column = true なら a の列 ia と b の列 ib、false なら行
    internal static int Facing(int a, int b, int ia, int ib, bool column)
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

    internal static int PopCount(int v)
    {
        int c = 0;
        while (v != 0) { v &= v - 1; c++; }
        return c;
    }

    // 4×4 の歩ける升が上下左右で 1 つにつながっているか
    internal static bool Connected(int bits)
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
        int fx0 = int.MaxValue, fy0 = int.MaxValue, fx1 = int.MinValue, fy1 = int.MinValue;
        while (Queue.Count > 0 && Queue[0].Tick <= _step)
        {
            var p = Queue[0];
            Queue.RemoveAt(0);
            if (p.Leak) { Sources.Add(p.Src); continue; }
            if (p.Inflow) { Inflows.Add(new Inflow { K = p.Cy * _w + p.Cx, End = p.Sr > 0 ? p.Tick + p.Sr : 0, Rate = p.Sp }); continue; }
            if (p.Push) { Push(p.Sx, p.Sy, p.Sr, p.Sp, p.Bx, p.By, p.Seed); continue; }
            Rebuild(p.Cx - p.R, p.Cy - p.R, p.Cx + p.R, p.Cy + p.R);
            // 壁の穴は床マスクでも床になる (絵の損傷はもう書き込み済み)。この刻みの範囲をまとめて、タイルの床の形を 1 回だけ作り直す
            fx0 = Math.Min(fx0, p.Cx - p.R); fy0 = Math.Min(fy0, p.Cy - p.R);
            fx1 = Math.Max(fx1, p.Cx + p.R + 1); fy1 = Math.Max(fy1, p.Cy + p.R + 1);
            int reach = p.R + FallScan[3] + 1;
            Links(p.Cx - reach, p.Cy - reach, p.Cx + reach, p.Cy + reach);
            if (p.Shock) Shock(p.Sx, p.Sy, p.Sr, p.Sp, p.Bx, p.By, p.Seed);
        }
        if (fx0 <= fx1)
            WaterArt.FloorChanged(Rect.MinMaxRect(_org.x + fx0 * _cell, _org.y + fy0 * _cell, _org.x + fx1 * _cell, _org.y + fy1 * _cell));

        // 扉: この刻みの開き (ホストの記録)
        ulong doors = Decompression.DoorBitsAt(_step);
        if (doors != _doorBits) ApplyDoors(doors);

        // 落ちていた水が下の床に着く (着いた升が閉じていたら開いている隣へ)
        int fl = 0;
        while (fl < Flying.Count && Flying[fl].Due <= _step)
        {
            var (_, to, amt) = Flying[fl++];
            if (_open[to] == 0)
                for (int d = 0; d < 4; d++) { int j = to + Nx[d] + Ny[d] * _w; if (_open[j] != 0) { to = j; break; } }
            if (_open[to] != 0) { _hgt[to] += amt; Wake(to); Mark(to); }
            else VoidTotal += amt;
        }
        if (fl > 0) Flying.RemoveRange(0, fl);

        // 浸水: 口の周りの開いている升へ等分して入れる (余りは並びの先頭から 1 ずつ)
        for (int i = Inflows.Count - 1; i >= 0; i--)
        {
            var f = Inflows[i];
            if (f.End != 0 && _step >= f.End) { Inflows.RemoveAt(i); continue; }
            Pour3(f.K, f.Rate);
        }
        for (int i = Jets.Count - 1; i >= 0; i--)
            if (_step - Jets[i].Start >= JetLife) Jets.RemoveAt(i);

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

        // 吸い出し: 減圧の場はこの刻みの物 (TerrainStep が先に減圧をこの刻みまで進めている)
        bool blow = Decompression.Running && Decompression.Pulling;
        // 口の際の升の水は宇宙へ出る (流れを決める前に自分の升だけを減らすので順番に依らない)
        if (blow)
            for (int a = 0; a < Active.Count; a++) if (_hgt[Active[a]] > 0) Drain(Active[a]);

        // 奈落の升の水は消える (落ちていく)
        for (int a = 0; a < Active.Count; a++)
        {
            int k = Active[a];
            if (_lvl[k] != VoidLevel || _hgt[k] <= 0) continue;
            VoidTotal += _hgt[k];
            _hgt[k] = 0;
            Mark(k);
        }

        // 流れ: 各升が自分の 4 本の管の流れ出しを決める (読むのは前の刻みの高さだけ・順番に依らない)。
        // 低い床の隣へは向こうの水に押し返されずに流れ、高い床の隣へは流れない。縁を越えて落ちる分は FallOut へ
        FallOut.Clear();
        for (int a = 0; a < Active.Count; a++)
        {
            int k = Active[a];
            int h = _hgt[k];
            int sum = 0;
            int b0 = 0, b1 = 0, b2 = 0, b3 = 0;
            if (blow && h > 0) Blow(k, h, out b0, out b1, out b2, out b3);
            int lk = _lvl[k];
            int falls = FallOut.Count;
            for (int d = 0; d < 4; d++)
            {
                int j = k + Nx[d] + Ny[d] * _w;
                int f = 0;
                if (h > 0 && _open[j] != 0 && Passable(k, d))
                {
                    int lj = _lvl[j];
                    bool known = lk != NoLevel && lj != NoLevel;
                    if (!known || lj <= lk)
                    {
                        int hj = known && lj < lk ? 0 : _hgt[j];
                        int diff = h - hj - (hj == 0 ? DryHold : WetHold);
                        f = _flux[k * 4 + d] * Damp / 256 + (diff > 0 ? diff * Gain / 256 : 0);
                        if (f < 0) f = 0;
                        f += d == 0 ? b0 : d == 1 ? b1 : d == 2 ? b2 : b3;
                    }
                }
                else if (h > FallLip && (_fallMask[k] >> d & 1) != 0)
                {
                    int g = (h - FallLip) * FallGain / 256;
                    if (g <= 0) g = 1;
                    FallOut.Add((k, d, g));
                    sum += g;
                }
                _flux[k * 4 + d] = f;
                sum += f;
                if (f > 0) Wake(j);
            }
            if (sum > h)
            {
                for (int d = 0; d < 4; d++) _flux[k * 4 + d] = (int)((long)_flux[k * 4 + d] * h / sum);
                for (int i = falls; i < FallOut.Count; i++) { var fo = FallOut[i]; FallOut[i] = (fo.K, fo.D, (int)((long)fo.Amount * h / sum)); }
            }
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

        // 縁を越えた水: 縁の升から引き、落ちる時間の後に下の升へ (奈落なら消える)
        foreach (var (k, d, amt) in FallOut)
        {
            if (amt <= 0) continue;
            _hgt[k] -= amt;
            Mark(k);
            FellTotal += amt;
            var (to, delay) = FallTo[k * 4 + d];
            if (to < 0) VoidTotal += amt;
            else AddFlying(_step + delay, to, amt);
            FallFx(k, d, to, amt, delay);
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

    private static void Pour3(int k, int amt)
    {
        int n = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++) if (_open[k + dx + dy * _w] != 0) n++;
        if (n == 0) return;
        int each = amt / n, rest = amt - each * n;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int j = k + dx + dy * _w;
                if (_open[j] == 0) continue;
                int a = each + (rest > 0 ? 1 : 0);
                if (rest > 0) rest--;
                _hgt[j] += a;
                Wake(j);
                Mark(j);
            }
        InflowTotal += amt;
    }

    // 落ちている水を着く刻みの順に入れる (同じ刻みは入れた順)
    private static void AddFlying(int due, int to, int amt)
    {
        int i = Flying.Count;
        while (i > 0 && Flying[i - 1].Due > due) i--;
        Flying.Insert(i, (due, to, amt));
    }

    // 演出用の記録 (計算には戻らない)。縁の位置は升の縁の真ん中、着く位置は落ちる先の升の真ん中 (奈落は縁の先の下)
    private static void FallFx(int k, int d, int to, int amt, int delay)
    {
        int x = k % _w, y = k / _w;
        float x0 = _org.x + (x + 0.5f + Nx[d] * 0.5f) * _cell, y0 = _org.y + (y + 0.5f + Ny[d] * 0.5f) * _cell;
        float x1, y1;
        if (to >= 0) { x1 = _org.x + (to % _w + 0.5f) * _cell; y1 = _org.y + (to / _w + 0.5f) * _cell; }
        else { x1 = x0 + Nx[d] * (Nx[d] != 0 ? SideVoidOut : _cell); y1 = y0 + Ny[d] * _cell - 1.5f; } // 横の縁は外へ弧を描いて谷へ
        for (int i = Falls.Count - 1; i >= 0 && i >= Falls.Count - 8; i--)
        {
            var f = Falls[i];
            if (f.X0 != x0 || f.Y0 != y0) continue;
            Falls[i] = (x0, y0, x1, y1, f.Amount + amt, delay, to < 0, d);
            return;
        }
        Falls.Add((x0, y0, x1, y1, amt, delay, to < 0, d));
    }

    // 口の際 (道のり DrainDist 以内) の升の水を引く強さに比例して宇宙へ出す (量を消す)
    private static void Drain(int k)
    {
        int x = k % _w, y = k / _w;
        if (!Decompression.FlowAt(x * Sub, y * Sub, out int dist, out int press, out _, out _) || dist > DrainDist) return;
        int s = (int)((long)Decompression.Strength1024(dist) * press / Decompression.Full);
        if (s <= 0) return;
        int h = _hgt[k];
        int gone = (int)((long)h * Math.Min(256, s * DrainGain / 1024) / 256);
        if (gone < 1) gone = h;
        _hgt[k] = h - gone;
        Mark(k);
        SpilledTotal += gone;
        Spill(x, y, gone);
    }

    // 升 k (水 h) を口へ向かって押す流れ (向き 0..3 = Nx/Ny の並び)。
    // 減圧の「口へ向かう向き」(升の数) を縦横に分け、通れない向きの分はもう一方へ回す
    private static void Blow(int k, int h, out int b0, out int b1, out int b2, out int b3)
    {
        b0 = b1 = b2 = b3 = 0;
        int x = k % _w, y = k / _w;
        if (!Decompression.FlowAt(x * Sub, y * Sub, out int dist, out int press, out int fx, out int fy)) return;
        int s = (int)((long)Decompression.Strength1024(dist) * press / Decompression.Full);
        if (s <= 0) return;
        int ax = Math.Abs(fx), ay = Math.Abs(fy);
        if (ax + ay == 0) return;
        int dX = fx > 0 ? 0 : 1, dY = fy > 0 ? 2 : 3;
        bool okX = ax > 0 && _open[k + Nx[dX]] != 0 && Passable(k, dX);
        bool okY = ay > 0 && _open[k + Ny[dY] * _w] != 0 && Passable(k, dY);
        if (!okX && !okY) return;
        int all = (int)((long)h * Math.Min(BlowMax, s * BlowGain / 1024) / 256);
        if (all <= 0) return;
        int bx = okX && okY ? all * ax / (ax + ay) : okX ? all : 0;
        int by = all - bx;
        if (dX == 0) b0 = bx; else b1 = bx;
        if (dY == 2) b2 = by; else b3 = by;
    }

    // 衝撃: 中心 (sx, sy)・半径 sr (どれも升 ×256) の中の水に、外へ向かう流れを足す (強さ sp = 0..256・中心ほど強い)。
    // 向き (bx, by) は放射の向きに足す偏り。足すのは水のある升だけ (水の無い升の管に残ると後で来た水が勝手に流れる)
    internal static void Shock(int sx, int sy, int sr, int sp, int bx, int by, ushort seed)
    {
        if (sr <= 0 || sp <= 0) return;
        Shocks++;
        uint rng = 0x9E3779B9u ^ seed * 2654435761u ^ (uint)_step * 40503u;
        if (rng == 0) rng = 1;
        int x0 = Math.Max(1, (sx - sr) >> 8), x1 = Math.Min(_w - 2, (sx + sr) >> 8);
        int y0 = Math.Max(1, (sy - sr) >> 8), y1 = Math.Min(_h - 2, (sy + sr) >> 8);
        long rr = (long)sr * sr;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int k = y * _w + x;
            int h = _hgt[k];
            if (h <= 0 || _open[k] == 0) continue;
            int dx = x * 256 + 128 - sx, dy = y * 256 + 128 - sy;
            long d2 = (long)dx * dx + (long)dy * dy;
            if (d2 >= rr) continue;
            int fall = (int)((rr - d2) * 256 / rr);                       // 中心 256 → 縁 0
            int dist = ISqrt(d2);
            h = Splash(k, x, y, dx, dy, dist, sr, fall * sp / 256, bx, by, ref rng);
            if (h <= 0) continue;
            int all = (int)((long)h * sp / 256 * fall / 256 * ShockGain / 256);
            if (all <= 0) continue;
            // 放射の向き (長さ ≈256 に揃える) + 偏り
            int ad = Math.Abs(dx) + Math.Abs(dy);
            int fx = (ad == 0 ? 0 : dx * 256 / ad) + bx, fy = (ad == 0 ? 0 : dy * 256 / ad) + by;
            int ax = Math.Abs(fx), ay = Math.Abs(fy);
            if (ax + ay == 0) continue;
            int dX = fx > 0 ? 0 : 1, dY = fy > 0 ? 2 : 3;
            bool okX = ax > 0 && _open[k + Nx[dX]] != 0 && Passable(k, dX);
            bool okY = ay > 0 && _open[k + Ny[dY] * _w] != 0 && Passable(k, dY);
            if (!okX && !okY) continue;
            int px = okX && okY ? all * ax / (ax + ay) : okX ? all : 0;
            _flux[k * 4 + dX] += px;
            _flux[k * 4 + dY] += all - px;
            Wake(k);
        }
    }

    // 押し: 起点 (sx, sy) から向き (ux, uy) (長さ 256) の先 len まで (どれも升 ×256) の扇形の水を向きへ押す。
    // 起点に近いほど強く (強さ sp = 0..256)、水の一部は粒になって向きの先へ飛ぶ
    internal static void Push(int sx, int sy, int len, int sp, int ux, int uy, ushort seed)
    {
        if (len <= 0 || sp <= 0 || (ux | uy) == 0) return;
        Shocks++;
        uint rng = 0x85EBCA6Bu ^ seed * 2654435761u ^ (uint)_step * 40503u;
        if (rng == 0) rng = 1;
        int wide = PushBase + len * PushSpread / 256;
        int ex = sx + ux * len / 256, ey = sy + uy * len / 256;
        int x0 = Math.Max(1, (Math.Min(sx, ex) - wide) >> 8), x1 = Math.Min(_w - 2, (Math.Max(sx, ex) + wide) >> 8);
        int y0 = Math.Max(1, (Math.Min(sy, ey) - wide) >> 8), y1 = Math.Min(_h - 2, (Math.Max(sy, ey) + wide) >> 8);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int k = y * _w + x;
            int h = _hgt[k];
            if (h <= 0 || _open[k] == 0) continue;
            int dx = x * 256 + 128 - sx, dy = y * 256 + 128 - sy;
            int along = (int)(((long)dx * ux + (long)dy * uy) / 256);
            if (along < 0 || along >= len) continue;
            int across = (int)(Math.Abs((long)dx * uy - (long)dy * ux) / 256);
            if (across > PushBase + along * PushSpread / 256) continue;
            int fall = (int)((long)(len - along) * 256 / len);                 // 起点 256 → 先 0
            h = Splash(k, x, y, 0, 0, along, len, fall * sp / 256, ux, uy, ref rng);
            if (h <= 0) continue;
            int all = (int)((long)h * sp / 256 * fall / 256 * PushGain / 256);
            if (all <= 0) continue;
            int ad = Math.Abs(dx) + Math.Abs(dy);
            int fx = ux * 3, fy = uy * 3;
            if (ad > 0) { fx += dx * 256 / ad; fy += dy * 256 / ad; }
            int ax = Math.Abs(fx), ay = Math.Abs(fy);
            if (ax + ay == 0) continue;
            int dX = fx > 0 ? 0 : 1, dY = fy > 0 ? 2 : 3;
            bool okX = ax > 0 && _open[k + Nx[dX]] != 0 && Passable(k, dX);
            bool okY = ay > 0 && _open[k + Ny[dY] * _w] != 0 && Passable(k, dY);
            if (!okX && !okY) continue;
            int px = okX && okY ? all * ax / (ax + ay) : okX ? all : 0;
            _flux[k * 4 + dX] += px;
            _flux[k * 4 + dY] += all - px;
            Wake(k);
        }
    }

    // 升 k の水の一部を粒にして外へ飛ばし、残りの水の量を返す。dx, dy, dist = 中心からの向きと距離 (升 ×256)。
    // 飛ぶ距離 = 半径の外までの残り × 0.8〜1.3 倍 + 少し。向きは放射 + 偏り ± 約 20°
    private static int Splash(int k, int x, int y, int dx, int dy, int dist, int sr, int fall, int bx, int by, ref uint rng)
    {
        int h = _hgt[k];
        int take = (int)((long)h * fall / 256 * SplashFrac / 256);
        if (take < SplashMin || Particles >= MaxParticles) return h;
        int ux = dist > 0 ? dx * 256 / dist : 0, uy = dist > 0 ? dy * 256 / dist : 0;
        ux += bx; uy += by;
        int ul = ISqrt((long)ux * ux + (long)uy * uy);
        rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
        if (ul == 0)
        {
            // 真ん中で偏りも無い: 縦横のどれかへ
            int q = (int)(rng >> 20 & 3u);
            ux = q == 0 ? 256 : q == 1 ? -256 : 0; uy = q == 2 ? 256 : q == 3 ? -256 : 0; ul = 256;
        }
        ux = ux * 256 / ul; uy = uy * 256 / ul;
        int a = 4 + (int)(rng % 7u);                     // ±15°
        int c = FanCos[a], sn = FanSin[a];
        int rx = (ux * c - uy * sn) / 256, ry = (ux * sn + uy * c) / 256;
        rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
        int reach = (sr - dist) * (205 + (int)(rng % 128u)) / 256 + 64 + (int)(rng >> 8 & 127u);
        rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
        int flight = FlightMin + (int)(rng % (uint)(FlightMax - FlightMin + 1));
        // 1 刻みに 1 升を超えて進むと壁の判定 (CanCross) が隣の升しか見ないので、遠くへ飛ぶ粒は長く飛ばす
        if (reach / flight > SplashStep) flight = Math.Min(SplashFlightMax, reach / SplashStep + 1);
        int spd = Math.Min(SplashStep, reach / flight);
        int p = Particles++;
        Px[p] = Ox[p] = x * 256 + 128; Py[p] = Oy[p] = y * 256 + 128;
        Ph[p] = Oh[p] = SplashHigh;
        Vx[p] = rx * spd / 256;
        Vy[p] = ry * spd / 256;
        Vh[p] = (Gravity * flight * flight / 2 - SplashHigh) / flight;
        Stuck[p] = false;
        Pm[p] = take;
        _hgt[k] = h - take;
        Mark(k);
        Splashes++;
        return h - take;
    }

    private static int ISqrt(long v)
    {
        if (v <= 0) return 0;
        long r = (long)Math.Sqrt(v);    // 近い値から整数で合わせる (端末で浮動小数の丸めが違っても同じ値になる)
        while (r * r > v) r--;
        while ((r + 1) * (r + 1) <= v) r++;
        return (int)r;
    }

    // 演出用の記録 (計算には戻らない)。同じ升は直近の記録へまとめる
    private static void Spill(int x, int y, int amount)
    {
        float px = _org.x + (x + 0.5f) * _cell, py = _org.y + (y + 0.5f) * _cell;
        for (int i = Spilled.Count - 1; i >= 0 && i >= Spilled.Count - 8; i--)
            if (Spilled[i].X == px && Spilled[i].Y == py) { Spilled[i] = (px, py, Spilled[i].Amount + amount); return; }
        if (Spilled.Count < 256) Spilled.Add((px, py, amount));
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
            Pm[p] = Mass;
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
            if (_open[k] != 0) { _hgt[k] += Pm[p]; Wake(k); Mark(k); }
            Landed.Add((nx, ny));
            int last = --Particles;
            if (p != last)
            {
                Px[p] = Px[last]; Py[p] = Py[last]; Ph[p] = Ph[last];
                Ox[p] = Ox[last]; Oy[p] = Oy[last]; Oh[p] = Oh[last];
                Vx[p] = Vx[last]; Vy[p] = Vy[last]; Vh[p] = Vh[last];
                Stuck[p] = Stuck[last];
                Pm[p] = Pm[last];
                p--; // 詰めた粒 (まだこの刻みで動かしていない) を同じ番号でもう一度回す
            }
        }
    }

    // 升の順番に依らない指紋 (刻みの番号も混ぜる)
    internal static uint Digest()
    {
        uint acc = (uint)_step * 2654435761u + (uint)Particles * 40503u + (uint)Flying.Count * 0x27D4EB2Fu;
        foreach (var (due, to, amt) in Flying) acc += (uint)due * 0x9E3779B1u ^ (uint)to * 0x85EBCA77u ^ (uint)amt;
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
        foreach (var f in Flying) v += f.Amount;
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

    // 水の升の範囲 (両端を含む) に扉の升があるか
    internal static bool DoorInBox(int x0, int y0, int x1, int y1)
    {
        if (!_ready || _doorCell == null) return false;
        x0 = Math.Max(x0, 0); y0 = Math.Max(y0, 0); x1 = Math.Min(x1, _w - 1); y1 = Math.Min(y1, _h - 1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if (_doorCell[y * _w + x] != 0) return true;
        return false;
    }

    // 水の升 (x, y) が机・岩などの当たり判定の中か (作った時の形)
    internal static bool UnderProp(int x, int y) => _ready && x >= 0 && y >= 0 && x < _w && y < _h && _propCell[y * _w + x] != 0;

    // 水の升 (x, y) の深さ (Full = 1 単位) と流れの速さ (1/1024 単位/刻み)。整数だけ (物の確定の計算から読む)
    internal static bool FlowCell(int x, int y, out int h, out int vx, out int vy)
    {
        h = vx = vy = 0;
        if (!_ready || !_running || x < 1 || y < 1 || x >= _w - 1 || y >= _h - 1) return false;
        int k = y * _w + x;
        h = _hgt[k];
        if (h <= 0) return false;
        int qx = _flux[k * 4] - _flux[(k + 1) * 4 + 1] + _flux[(k - 1) * 4] - _flux[k * 4 + 1];
        int qy = _flux[k * 4 + 2] - _flux[(k + _w) * 4 + 3] + _flux[(k - _w) * 4 + 2] - _flux[k * 4 + 3];
        // 升 1 辺 = Unit/4・面 2 つの平均 → Unit/8。浅い縁では 1/8 単位より浅く数えない (CurrentAt と同じ)
        int d = Math.Max(h, Full / 8);
        vx = (int)((long)qx * (Unit / 8) / d);
        vy = (int)((long)qy * (Unit / 8) / d);
        if (Jets.Count > 0) JetAt(x, y, ref vx, ref vy);
        return true;
    }

    // その点の水の深さ (単位) と流れの速さ (単位/秒)。升の 4 つの面を通る正味の流れの平均 ÷ 深さ。
    // 見せる・自分の体を動かす用 (端末ごとでよい物) なので小数で返す。水の升が無ければ 0
    internal static float CurrentAt(Vector2 p, out float vx, out float vy)
    {
        vx = vy = 0f;
        if (!_ready || !_running) return 0f;
        int x = (int)MathF.Floor((p.x - _org.x) / _cell), y = (int)MathF.Floor((p.y - _org.y) / _cell);
        if (x < 1 || y < 1 || x >= _w - 1 || y >= _h - 1) return 0f;
        int k = y * _w + x;
        int h = _hgt[k];
        if (h <= 0) return 0f;
        // +x へ: 東の面 (自分→東 − 東→自分) と西の面 (西→自分 − 自分→西) の平均。y も同じ
        int qx = _flux[k * 4] - _flux[(k + 1) * 4 + 1] + _flux[(k - 1) * 4] - _flux[k * 4 + 1];
        int qy = _flux[k * 4 + 2] - _flux[(k + _w) * 4 + 3] + _flux[(k - _w) * 4 + 2] - _flux[k * 4 + 3];
        // 浅い縁では 量 ÷ 深さ が跳ねるので、深さは 1/8 単位より浅く数えない
        float f = 0.5f * _cell * GameClock.Hz / Math.Max(h, Full / 8);
        vx = qx * f;
        vy = qy * f;
        if (Jets.Count > 0)
        {
            int jx = 0, jy = 0;
            JetAt(x, y, ref jx, ref jy);
            vx += jx * (GameClock.Hz / (float)Unit);
            vy += jy * (GameClock.Hz / (float)Unit);
        }
        return h / (float)Full;
    }

    // ── 毎フレーム ─────────────────────────────────────────────────────

    public static void Tick()
    {
        try { TickCore(); }
        catch (Exception e) { Fail("tick", e); }
    }

    // 船が替わったら片付ける。刻みは TerrainStep が減圧・物と揃えて進める (AdvanceOne)
    private static void TickCore()
    {
        if (GameClock.ShipGen != _shipGen) { _shipGen = GameClock.ShipGen; Reset(); }
    }

    // 1 刻み進める (TerrainStep から・_step の刻み)
    internal static void AdvanceOne()
    {
        try
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            StepOnce();
            _step++;
            LastStepMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (LastStepMs > MaxStepMs) MaxStepMs = LastStepMs;
        }
        catch (Exception e) { Fail("step", e); }
    }

    internal static double MaxStepMs { get; private set; }

    // 確認用: 開いている升を全部深さ depth の水で満たす (自分の手元だけ・同期しない)
    private static int Flood(int depth)
    {
        if (!EnsureGrid()) return -1;
        if (!_running) { _running = true; _step = StartStep(GameClock.Now - Delay); }
        PropSim.WakeForWater(GameClock.Now - Delay);
        int n = 0;
        for (int y = 1; y < _h - 1; y++)
        for (int x = 1; x < _w - 1; x++)
        {
            int k = y * _w + x;
            if (_open[k] == 0) continue;
            _hgt[k] = depth;
            Wake(k);
            Mark(k);
            n++;
        }
        MaxStepMs = 0;
        return n;
    }

    // 確認用: 円の中の升だけに水を置く (自分の手元だけ・水の縁の見た目を見る)
    private static int Pour(float px, float py, float r, int depth)
    {
        if (!EnsureGrid()) return -1;
        if (!_running) { _running = true; _step = StartStep(GameClock.Now - Delay); }
        PropSim.WakeForWater(GameClock.Now - Delay);
        int cx = (int)MathF.Floor((px - _org.x) / _cell), cy = (int)MathF.Floor((py - _org.y) / _cell), cr = (int)MathF.Ceiling(r / _cell);
        float rr = r * r;
        int n = 0;
        for (int y = Math.Max(1, cy - cr); y <= Math.Min(_h - 2, cy + cr); y++)
        for (int x = Math.Max(1, cx - cr); x <= Math.Min(_w - 2, cx + cr); x++)
        {
            int k = y * _w + x;
            float dx = (x + 0.5f) * _cell + _org.x - px, dy = (y + 0.5f) * _cell + _org.y - py;
            if (_open[k] == 0 || dx * dx + dy * dy > rr) continue;
            _hgt[k] += depth;
            Wake(k);
            Mark(k);
            n++;
        }
        return n;
    }

    internal static void Reset()
    {
        _ready = false;
        _running = false;
        _open = null; _sub = null; _edge = null; _hgt = null; _flux = null; _inList = null; _tileDirty = null;
        _lvl = null; _shut = null; _doorCell = null; _propCell = null; OutsideShadow.Clear(); LedgeShadow.Clear(); _doorUnder = null; _fallMask = null; _shadowCut = null; _ledge = null;
        FallTo.Clear();
        FallOut.Clear();
        Flying.Clear();
        Falls.Clear();
        FellTotal = 0;
        VoidTotal = 0;
        DirtyTiles.Clear();
        Active.Clear();
        Queue.Clear();
        Sources.Clear();
        Inflows.Clear();
        InflowTotal = 0;
        Gushes = 0;
        Jets.Clear();
        GushFx.Clear();
        Particles = 0;
        Landed.Clear();
        _step = 0;
        Late = 0;
        Steps = 0;
        Shocks = 0;
        Splashes = 0;
        LastDigest = 0;
        Spilled.Clear();
        SpilledTotal = 0;
        MaxStepMs = 0;
    }

    internal static void Register()
    {
        TestBridge.Register("water", "[reset | flood [depth] | pour x y r [depth] | inflow x y [秒 (0=止まらない)] [速さ 0..1] | show [r] | hide] 水の計算 (show = 自分の周りに判定を色で重ねる: 赤 = 水の升が閉じている・橙 = 歩けない所・黄 = 家具で水を見せない所): 刻み・遅れ・指紋・量 (reset = 水を全部消す)", (args, reply) =>
        {
            string a = args.Trim();
            if (a == "reset") Reset();
            if (a.StartsWith("flood"))
            {
                int depth = a.Length > 5 && int.TryParse(a.Substring(5).Trim(), out int dd) ? dd : Full / 4;
                int n = Flood(depth);
                reply(n < 0 ? "ERR water flood: no map" : $"OK water flood cells={n} depth={depth}");
                return;
            }
            if (a.StartsWith("pour"))
            {
                var q = a.Substring(4).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                if (q.Length < 3 || !float.TryParse(q[0], System.Globalization.NumberStyles.Float, ic, out float px) || !float.TryParse(q[1], System.Globalization.NumberStyles.Float, ic, out float py)
                    || !float.TryParse(q[2], System.Globalization.NumberStyles.Float, ic, out float pr)) { reply("ERR water pour x y r [depth]"); return; }
                int pd = q.Length > 3 && int.TryParse(q[3], out int dd2) ? dd2 : Full / 4;
                int pn = Pour(px, py, pr, pd);
                reply(pn < 0 ? "ERR water pour: no map" : $"OK water pour cells={pn} depth={pd}");
                return;
            }
            if (a.StartsWith("inflow"))
            {
                // 浸水の口 (同期する・ホストか一人の時だけ): 秒数 0 = 止まらない
                var q = a.Substring(6).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                var ns = System.Globalization.NumberStyles.Float;
                if (q.Length < 2 || !float.TryParse(q[0], ns, ic, out float ix) || !float.TryParse(q[1], ns, ic, out float iy)) { reply("ERR water inflow x y [seconds] [rate 0..1]"); return; }
                float isec = q.Length > 2 && float.TryParse(q[2], ns, ic, out float s2) ? s2 : 30f;
                float irate = q.Length > 3 && float.TryParse(q[3], ns, ic, out float r2) ? r2 : 0.5f;
                var res = TerrainApi.WorldFlood(new Vector2(ix, iy), isec, irate);
                reply(res.Ok ? $"OK water inflow {res.Why}" : $"ERR water inflow {res.Why}");
                return;
            }
            if (a.StartsWith("show")) { reply(WaterDebug.Show(a.Length > 4 ? a.Substring(4).Trim() : "")); return; }
            if (a == "hide") { WaterDebug.Hide(); reply("OK water hide"); return; }
            if (a.StartsWith("line"))
            {
                // 確認用: その点を通る横一列 (左右 n 升) の深さ
                var q = a.Substring(4).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (!_ready || q.Length < 2 || !float.TryParse(q[0], out float lx) || !float.TryParse(q[1], out float ly)) { reply("ERR water line x y [n]"); return; }
                int n = q.Length > 2 && int.TryParse(q[2], out int nn) ? nn : 12;
                int cx = (int)MathF.Floor((lx - _org.x) / _cell), cy = (int)MathF.Floor((ly - _org.y) / _cell);
                var sb = new System.Text.StringBuilder();
                for (int x = cx - n; x <= cx + n; x++)
                    sb.Append(x < 0 || x >= _w || cy < 0 || cy >= _h ? "-" : _hgt[cy * _w + x].ToString()).Append(x == cx ? "|" : " ");
                reply($"OK water line step={_step} {sb}");
                return;
            }
            if (a == "doors") { WaterDebug.ListDoors(reply); return; }
            if (a == "links")
            {
                // 確認用: 落ちる縁 (縁の升の真ん中・向き・落ちる先 / void)
                if (!_ready) { reply("ERR water links: no grid"); return; }
                var sb = new System.Text.StringBuilder();
                int c = 0;
                foreach (var kv in FallTo)
                {
                    int k = kv.Key / 4, d = kv.Key % 4, to = kv.Value.To;
                    sb.Append($"({_org.x + (k % _w + 0.5f) * _cell:0.0},{_org.y + (k / _w + 0.5f) * _cell:0.0}){"+x-x+y-y".Substring(d * 2, 2)}");
                    sb.Append(to < 0 ? ">void " : $">({_org.x + (to % _w + 0.5f) * _cell:0.0},{_org.y + (to / _w + 0.5f) * _cell:0.0}) ");
                    if (++c % 8 == 0) { reply("LINKS " + sb); sb.Clear(); }
                }
                if (sb.Length > 0) reply("LINKS " + sb);
                reply($"OK water links n={FallTo.Count}");
                return;
            }
            if (a.StartsWith("why"))
            {
                // 確認用: 範囲の縁の升が落ちない理由を向きごとに数える (4 つ目に "-y:scan" などを渡すとその升の位置を並べる。scan = 先に開いた升が無い・same = 先が低くない・door・room = 谷の外で外壁の向こうの奈落・shadow = 影の線。prop = すぐ隣が床に置かれた物・落ちる側は fall/side/void/sky = 手すりの外の空/cliff = 何も無い崖の下)
                var q = a.Substring(3).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                if (!_ready || q.Length < 3 || !float.TryParse(q[0], System.Globalization.NumberStyles.Float, ic, out float wx) || !float.TryParse(q[1], System.Globalization.NumberStyles.Float, ic, out float wy)
                    || !float.TryParse(q[2], System.Globalization.NumberStyles.Float, ic, out float wr)) { reply("ERR water why x y r"); return; }
                reply(WhyNoFall(wx, wy, wr, q.Length > 3 ? q[3] : null));
                return;
            }
            if (a.StartsWith("furn")) { WaterDebug.ListFurniture(a.Length > 4 ? a.Substring(4).Trim() : "", reply); return; }
            if (!_ready) { reply($"OK water off {GameClock.Describe()}"); return; }
            reply($"OK water step={_step} target={GameClock.Now - Delay} late={Late} shocks={Shocks} splashes={Splashes} queue={Queue.Count} sources={Sources.Count} inflows={Inflows.Count} inflowed={InflowTotal} gushes={Gushes} particles={Particles} active={Active.Count} volume={Volume()} spilled={SpilledTotal} fell={FellTotal} void={VoidTotal} flying={Flying.Count} links={FallTo.Count} doors={System.Numerics.BitOperations.PopCount(_doorBits)}/{_doorUnder?.Length ?? 0} digest={Digest():x8} stepMs={LastStepMs:0.000} maxStepMs={MaxStepMs:0.000} grid={_w}x{_h} {GameClock.Describe()}");
        });
    }
}
