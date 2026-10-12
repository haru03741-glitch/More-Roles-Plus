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
internal static partial class WaterSim
{
    public const int Delay = 18;              // 0.6 秒 (GameClock.Hz = 30)
    public const int Sub = 4;                 // 水の升 1 辺 = SolidMap の升 Sub 個
    public const int Full = 1024;             // 深さ 1 単位分の水の量
    private const int Gain = 56;              // 高さの差 → 流れ (/256)
    private const int Damp = 236;             // 前の刻みの流れを残す割合 (/256)
    // 1 刻みを SubN 回の小刻みに分けて流す。流れ (_flux) の単位は 1 刻みあたりのままで、小刻みには 1/SubN ずつ運ぶ。
    // 安定の条件は小刻みごとに掛かるので、流れの伸び (SubGain) を Gain より大きくでき、強い注水でも水面が傾きにくい。
    // SubDamp は勢いの残り方が 1 刻みあたり Damp と同じになる値
    // SubGain = Gain × SubN が小刻みあたりの安定の目安。この 2 倍では升ごとに水面がぎざぎざに揺れた (実測)
    private const int SubN = 4, SubGain = Gain * SubN, SubDamp = 256 - (256 - Damp) / SubN;
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
    // 越える量は堰の式 (越えた高さの 1.5 乗) で、越えた高さ 1/4 単位の時に FallGain/256 になるよう合わせる。1 刻みに越えた分の半分まで
    private const int FallLip = 24;
    private const int FallGain = 72;
    private const int WeirDiv = 64 * 256 / FallGain;   // e × √(16e) / これ = e=256 (√(16e)=64) で e × FallGain/256
    // ガラスの手すり (LowWalls): 縁の先が空でも手すりの高さまでは溜まり、越えた分が手すりの外へ落ちる
    private const int GlassDrop = 4;          // 手すりの外の空へ落ちる時間を決める距離 (升)
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
    internal static readonly List<(float X0, float Y0, float X1, float Y1, int Amount, int Delay, bool Void, int Dir, float Lip, float Depth, int BarB, float BarH, int K)> Falls = new();
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
    private static byte[] _doorFloor; // 歩ける所の地図では壁だが扉が開けば通れる升 (除染室の扉の通り道。閉じている間は _shut で閉じる)
    private static byte[] _propCell; // 床に置かれた物 (岩・結晶・机) の当たり判定の中の升
    private static bool[] _doorUnder; // 下に隙間があって水が通る扉
    private static ulong _doorBits;  // 升に入れた扉の開き
    private static byte[] _fallMask; // 縁を越えて落ちる向き (ビット = Nx/Ny の並び)
    private static byte[] _stepMask; // 小さな段差の縁の向き (滝にせず、段の高さぶんずらして両向きに流す)
    private static readonly Dictionary<int, int> StepTo = new(); // 升 × 4 + 向き → 段の向こうの升
    private static int[] _stepTo;    // StepTo と同じ (縁の枠・_stepMask の立っている向きだけ読む)
    private static byte[] _shadowCut; // 作った時に影の線を挟んでいた縁の向き
    private static readonly Dictionary<int, (int To, int Delay)> FallTo = new(); // 升 × 4 + 向き → 落ちる先の升 (−1 = 奈落) と刻み
    // この小刻みに縁を越える量 (升, 向き, 量 の 3 つ組を並べる)
    private static int[] _fo = Array.Empty<int>();
    private static int _foN;
    private static int[] _fs = Array.Empty<int>(); // 1 刻みの間に縁を越えた量 (升・向き・量の 3 個ずつ・升 × 向きごとに合計)
    private static int _fsN;
    private static int[] _fallAt;    // 縁の枠 → _fs の番号 + 1 (0 = まだ無い)
    private static int[] _move;      // この小刻みに升 × 4 向きへ運ぶ量
    private static readonly List<(int Due, int To, int Amount)> Flying = new(); // 落ちている水 (着く刻みの順)
    private static byte[] _inList;
    private static byte[] _stir;     // この刻みに起こされた (高さか地形が自分か隣で変わった)。片付けで眠らせない
    private static bool _blowing;    // 前の刻みが吸い出しの最中だった
    private static bool _noSleep;    // 確認用: 眠らせない (water sleep off・自分の手元だけ)
    // 水か流れのある升 (Wake で足す・刻みの終わりに外す)。升ごとに 1 回までなので長さは升の数
    private static int[] _act = Array.Empty<int>();
    private static int _actN;
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
    internal static int Height(int k) { EnsureIdle(); return _hgt[k]; }

    // 床の高さで描く水の深さ: 家具の足元の升 (当たり判定の四角なので絵の外の床も含む) は、床から見えるのは家具の周りの水なので、
    // 天板の上に水があれば天板からの深さを足し、無ければ家具の外の一番近い升の深さ (縦横 FurnScan 升まで) を使う
    // 天板に残った薄い膜 (流れ落ちきらない分) は天板の上の水として数えない: 膜でも天板の高さ + 膜の水面になり、
    // 周りの水よりずっと高い水面が壁の面に這い上がって尖る
    private const int FurnScan = 8;
    internal const int TopFilm = Full * 3 / 100;
    internal static int ShownDepth(int k)
    {
        EnsureIdle();
        int f = _furn == null ? 0 : _furn[k], h = _hgt[k];
        if (f == 0) return h;
        if (h > TopFilm) return h + f;
        int best = 0;
        for (int d = 0; d < 4; d++)
        {
            int j = k, step = Nx[d] + Ny[d] * _w;
            for (int i = 0; i < FurnScan; i++)
            {
                j += step;
                if (j < 0 || j >= _hgt.Length || _open[j] == 0) break;
                if (_furn[j] != 0) continue;
                best = Math.Max(best, _hgt[j]);
                break;
            }
        }
        return Math.Min(best, f);
    }
    internal static bool Open(int k) => _open[k] != 0;
    // C++ の部品へ渡す用 (読むだけ)
    internal static byte[] OpenCells => _open;
    internal static byte[] EdgeCells => _edge;
    internal static short[] FurnCells => _furn;
    internal static int[] HeightCells { get { EnsureIdle(); return _hgt; } }
    internal static bool Ready => _ready;
    internal static int Step => _step;
    internal static bool Running => _running;
    // 絵のタイル (TileCells 升四方) の描き直しの印
    public const int TileCells = 16;
    private static int _tw, _th;
    private static byte[] _tileDirty;
    internal static readonly List<int> DirtyTiles = new();
    internal static int TilesW => _tw;
    internal static int TilesH => _th;
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
        EnsureIdle();
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
        _platFloor = new byte[n];
        _edge = new byte[n];
        _hgt = new int[n];
        _flux = new int[n * 4];
        _move = new int[n * 4];
        _inList = new byte[n];
        _stir = new byte[n];
        _act = new int[n];
        _actN = 0;
        _sortedN = 0;
        _fo = Array.Empty<int>(); // 縁の数に合わせて Links で広げる
        _foN = 0;
        _shut = new byte[n];
        _doorCell = new byte[n];
        _doorFloor = new byte[n];
        _propCell = new byte[n];
        _fallMask = new byte[n];
        _stepMask = new byte[n];
        _shadowCut = new byte[n];
        _lipMask = new byte[n];
        InitSlots(n);
        _furn = new short[n];
        PropSim.ForgetTops(); // 新しい格子へ家具の床を刻み直す
        FallTo.Clear();
        StepTo.Clear();
        ListDoorCells();
        int props = ListPropCells();
        _tw = (_w + TileCells - 1) / TileCells;
        _th = (_h + TileCells - 1) / TileCells;
        _tileDirty = new byte[_tw * _th];
        LoadWet();
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        Rebuild(0, 0, _w - 1, _h - 1);
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        Levels();
        LoadPlatform();
        long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        Links(0, 0, _w - 1, _h - 1, true);
        long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
        double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Plugin.Logger.LogInfo($"[WaterSim] grid {_w}x{_h} alloc={(t1 - t0) * f:0.00}ms rebuild={(t2 - t1) * f:0.00} levels={(t3 - t2) * f:0.00} links={(t4 - t3) * f:0.00} n={FallTo.Count} steps={CountBits(_stepMask)} z=[{_zInfo}] props={props} wet={WetStats} hash={GridHash():x8}");
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
        if (!Native.MrpNative.Verify) { RebuildNative(x0, y0, x1, y1); return; }
        VerifyCellsBefore(x0, y0, x1, y1);
        int sw = SolidMap.W;
        var solid = SolidMap.OpenCells;
        var wet = _wet;
        int changes = 0;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int bits = 0;
            for (int sy = 0, row = y * Sub * sw + x * Sub; sy < Sub; sy++, row += sw)
            for (int sx = 0; sx < Sub; sx++)
                if (solid[row + sx] != 0 || wet != null && wet[row + sx] != 0) bits |= 1 << (sy * Sub + sx);
            int k = y * _w + x;
            if (_doorFloor[k] != 0 || _platFloor[k] != 0) bits = 0xFFFF;
            if (PopCount(bits) < 4 || !Connected(bits) || _shut[k] != 0) bits = 0;
            _sub[k] = (ushort)bits;
            byte open = bits != 0 ? (byte)1 : (byte)0;
            if (open == _open[k]) continue;
            changes++;
            OpenChanged(k, open);
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
        VerifyCellsAfter(x0, y0, x1, y1, changes);
    }

    // 升の開閉が変わった時の後始末。範囲の升の番号順に呼ぶ (閉じた升の水の寄せ先は、先に変わった升の開閉を見る)
    private static void OpenChanged(int k, byte open)
    {
        _open[k] = open;
        if (open == 0 && _hgt[k] != 0)
        {
            // 閉じた升の水は消さずに開いている隣へ寄せる (量を保つ)
            int to = -1;
            for (int d = 0; d < 4 && to < 0; d++) { int j = k + Nx[d] + Ny[d] * _w; if (_open[j] != 0) to = j; }
            if (to >= 0) { _hgt[to] += _hgt[k]; WakeAround(to); }
            _hgt[k] = 0;
        }
        for (int d = 0; d < 4; d++) { _flux[k * 4 + d] = 0; _move[k * 4 + d] = 0; }
        if (open != 0) Wake(k);
        for (int d = 0; d < 4; d++) Wake(k + Nx[d] + Ny[d] * _w);
        if (_ready) Mark(k); // 升を作る時の印は作り終えた所で捨てる
    }

    // Rebuild の C++ 版 (升の形と境の向きを sub/edge へ書き、開閉が変わった升を返す)。後始末は C# が同じ順に行う
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal unsafe struct CellsArgs
    {
        public byte* Solid; public byte* Wet;
        public int Sw, W, H, Sub;
        public int X0, Y0, X1, Y1;
        public byte* DoorFloor; public byte* PlatFloor; public byte* Shut; public byte* Open;
        public ushort* SubOut; public byte* EdgeOut;
        public int* Changed; public int ChangedN;
    }

    private static int[] _cellChanged = Array.Empty<int>();

    private static unsafe int CellsNative(int x0, int y0, int x1, int y1, ushort[] sub, byte[] edge)
    {
        int need = Math.Max(1, (x1 - x0 + 1) * (y1 - y0 + 1));
        if (_cellChanged.Length < need) _cellChanged = new int[need];
        var wet = _wet;
        fixed (byte* solid = SolidMap.OpenCells) fixed (byte* wp = wet) fixed (byte* df = _doorFloor) fixed (byte* pf = _platFloor)
        fixed (byte* shut = _shut) fixed (byte* open = _open) fixed (ushort* so = sub) fixed (byte* eo = edge) fixed (int* ch = _cellChanged)
        {
            var a = new CellsArgs
            {
                Solid = solid, Wet = wet != null ? wp : null, Sw = SolidMap.W, W = _w, H = _h, Sub = Sub,
                X0 = x0, Y0 = y0, X1 = x1, Y1 = y1,
                DoorFloor = df, PlatFloor = pf, Shut = shut, Open = open, SubOut = so, EdgeOut = eo, Changed = ch,
            };
            Native.MrpNative.WaterCells(&a);
            return a.ChangedN;
        }
    }

    private static void RebuildNative(int x0, int y0, int x1, int y1)
    {
        int n = CellsNative(x0, y0, x1, y1, _sub, _edge);
        for (int i = 0; i < n; i++)
        {
            int k = _cellChanged[i];
            OpenChanged(k, _sub[k] != 0 ? (byte)1 : (byte)0);
        }
    }

    // 照合 (native verify): C++ の答えを写しの配列へ作り、C# で作り直した後の配列と範囲 (境の向きは外周 1 升も) を比べる
    internal static long VerifyCells, VerifyCellDiff;
    private static ushort[] _vSub;
    private static byte[] _vEdge;
    private static int _vChanged;

    private static void VerifyCellsBefore(int x0, int y0, int x1, int y1)
    {
        if (_vSub == null || _vSub.Length != _sub.Length) { _vSub = new ushort[_sub.Length]; _vEdge = new byte[_edge.Length]; }
        Array.Copy(_sub, _vSub, _sub.Length);
        Array.Copy(_edge, _vEdge, _edge.Length);
        _vChanged = CellsNative(x0, y0, x1, y1, _vSub, _vEdge);
    }

    private static void VerifyCellsAfter(int x0, int y0, int x1, int y1, int changes)
    {
        VerifyCells++;
        long diff = _vChanged != changes ? 1 : 0;
        for (int y = Math.Max(1, y0 - 1); y <= Math.Min(_h - 2, y1 + 1); y++)
        for (int x = Math.Max(1, x0 - 1); x <= Math.Min(_w - 2, x1 + 1); x++)
        {
            int k = y * _w + x;
            if (_vSub[k] != _sub[k] || _vEdge[k] != _edge[k]) diff++;
        }
        VerifyCellDiff += diff;
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
            foreach (int k in Decompression.DoorFloor(i)) _doorFloor[k] = 1;
        }
    }

    // 升作りの結果 (物の升・影の線で切った向き・落ちる先) の指紋。作り方を変えても同じになることの確認用
    private static uint GridHash()
    {
        uint h = 2166136261;
        // 床の高さと海の縁は表の無いマップでは 0 (今までと同じ指紋)
        for (int i = 0; i < _propCell.Length; i++) h = (h ^ (uint)(_propCell[i] | _shadowCut[i] << 1 | _seaMask[i] << 17 | (_bed[i] & 0x7ff) << 21)) * 16777619;
        uint sum = 0;
        foreach (var kv in FallTo) sum += (uint)(kv.Key * 73856093) ^ (uint)(kv.Value.To * 19349663) ^ (uint)(kv.Value.Delay * 83492791);
        foreach (var kv in StepTo) sum += (uint)(kv.Key * 2654435761u) ^ (uint)(kv.Value * 40503);
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
        LoadBed();
        LoadAir();
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
            var ship = ShipStatus.Instance;
            if (ship && ship.TryCast<AirshipStatus>() != null) RaiseCells(AirshipRaised, 1);
            return;
        }
        var lv = IslandLevels();
        Array.Copy(lv, _lvl, n);
    }

    // 升ごとの島の順位 (升の中の最初の歩ける升の島・水だけが届く升は隣の島・はしごでつながらない島は NoLevel・閉じた升は 0)
    private static sbyte[] IslandLevels()
    {
        int n = _w * _h;
        var lvl = new sbyte[n];
        var isl = new byte[n];
        var area = new int[SolidMap.IslandCount + 1];
        int sw = SolidMap.W;
        var solid = SolidMap.OpenCells;
        var wetOnly = new byte[n];
        for (int k = 0; k < n; k++)
        {
            int bits = _sub[k];
            if (bits == 0) continue;
            int x = k % _w, y = k / _w, s = -1;
            // 最初の歩ける升 (水だけが届く升は島を持たない)
            while (bits != 0)
            {
                int b = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                int c = (y * Sub + b / Sub) * sw + x * Sub + b % Sub;
                if (solid[c] != 0) { s = c; break; }
            }
            if (s < 0) { wetOnly[k] = 1; continue; }
            int id = SolidMap.IslandCell(s);
            if (id <= 0 || id >= area.Length) continue;
            isl[k] = (byte)id;
            area[id]++;
        }
        SpreadWetIslands(isl, wetOnly);
        var rank = HeightLevels.IslandRanks(i => area[i]);
        DoorRanks(isl, rank);
        _cellIsl = isl; _islRank = rank; _islArea = area; // 島ごとの高さ (WaterElevation) 用
        for (int k = 0; k < n; k++)
        {
            int id = isl[k];
            lvl[k] = id == 0 ? (sbyte)0 : rank[id] == int.MinValue ? NoLevel : (sbyte)Math.Clamp(rank[id], -100, 100);
        }
        return lvl;
    }

    // はしごでつながらない島は、扉を挟んで隣り合う順位の決まった島と同じ段にする (扉の奥の部屋)。
    // 扉の升の周り DoorReach 升にある島どうしを組にして、決まった側から決まらない側へ広げる
    private const int DoorReach = 3;
    private static void DoorRanks(byte[] isl, int[] rank)
    {
        var near = new List<int>();
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int c = 0; c < _doorCell.Length; c++)
            {
                if (_doorCell[c] == 0) continue;
                int cx = c % _w, cy = c / _w;
                near.Clear();
                for (int dy = -DoorReach; dy <= DoorReach; dy++)
                for (int dx = -DoorReach; dx <= DoorReach; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= _w || y >= _h) continue;
                    int id = isl[y * _w + x];
                    if (id != 0 && id < rank.Length && !near.Contains(id)) near.Add(id);
                }
                int known = int.MinValue;
                foreach (int id in near) if (rank[id] != int.MinValue) { known = rank[id]; break; }
                if (known == int.MinValue) continue;
                foreach (int id in near) if (rank[id] == int.MinValue) { rank[id] = known; changed = true; }
            }
        }
    }

    // 範囲の升の、縁を越えて落ちる向きと落ちる先を作り直す。縁 (隣へ流れられない向き) の先を FallScan 升まで進み、
    // 最初の開いた升が低ければ落ちる先 (奈落を先に通れば奈落)。扉の升を越える所と、視界の影の線を挟む所 (壁の向こうの部屋) は落ちない。
    // 奈落へ落ちる所の決め方は Target に
    // 影の線は壊れた壁と一緒に切られ、壊れの届き方は人によって先後があるので、線を読むのは作った時 (full) だけ。
    // 引き直しはその時の結果 (_shadowCut) を引く
    private static void Links(int x0, int y0, int x1, int y1, bool full = false)
    {
        var ship = ShipStatus.Instance;
        bool airship = ship && ship.TryCast<AirshipStatus>() != null;
        _decks = airship ? AirshipDecks : NoDecks;
        bool mira = ship && ship.TryCast<MiraShipStatus>() != null;
        LowWalls.Load(mira);
        x0 = Math.Max(1, x0); y0 = Math.Max(1, y0);
        x1 = Math.Min(_w - 2, x1); y1 = Math.Min(_h - 2, y1);
        // 高さをはしごの島で決めたマップ (注釈なし) では、高さの違う島の間は必ず崖なので屋外の影の線を見ない
        // (崖の上の縁に視界の影の線が引いてあり、それで切ると崖の大半から落ちなくなる)。部屋の影の線 (展望台の箱と柵の縁) は見る
        bool cliffs = !MapNotes.HasLevels;
        // 床の本当の高さで縁を決めるのは C++ の部品がある時だけ (照合の時は C# と同じ段の順位)。
        // 作った時は先に段の順位で崖の面を測って島の高さを決め、その高さで引き直す
        bool zray = ZFlow && !Native.MrpNative.Verify;
        if (full)
        {
            ListLadders(ship);
            ZBegin();
            LinkPass(x0, y0, x1, y1, true, cliffs, false);
            ZSolve();
            if (zray) LinkPass(x0, y0, x1, y1, true, cliffs, true);
        }
        else LinkPass(x0, y0, x1, y1, false, cliffs, zray);
        WakeWet(x0 - 1, y0 - 1, x1 + 1, y1 + 1);
        EnsureFo();
    }

    // 縁を越える水の記録 (_fo) は 1 小刻みに縁 1 本につき 1 個まで。落ちる縁・段差・海の縁の数に合わせて広げる
    private static void EnsureFo()
    {
        int sea = 0;
        foreach (byte b in _seaMask) if (b != 0) sea += System.Numerics.BitOperations.PopCount(b);
        int need = (FallTo.Count + StepTo.Count + sea) * 3;
        if (_fo.Length < need) _fo = new int[Math.Max(need + need / 2, 768)];
    }

    // 範囲の縁を消して、光線を引き直して登録する。measure = 崖の面の長さを島の高さの測りに足す (作った時の 1 回目)
    private static void LinkPass(int x0, int y0, int x1, int y1, bool full, bool cliffs, bool useZ)
    {
        EnsureIdle(); // 落ちる縁の地図は作業スレッドの床作りも読む
        bool measure = full && !useZ;
        _ledgeScan = full;
        _rayN = 0;
        // 作った時の 2 回目は行き先が変わりうるので、影の線の判定も引き直す
        if (full && useZ)
            for (int y = y0; y <= y1; y++) Array.Clear(_shadowCut, y * _w + x0, x1 - x0 + 1);
        long l0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Scan(x0, y0, x1, y1);
        long l1 = System.Diagnostics.Stopwatch.GetTimestamp();
        // ガラスの手すりの縁を除いて光線を引き、元の並びへ戻す
        int all = _rayN, n = 0;
        if (_rayAll.Length < all) _rayAll = new int[_rayQ.Length];
        Array.Copy(_rayQ, _rayAll, all);
        for (int i = 0; i < all; i++) if (_rayAll[i] >= 0) _rayQ[n++] = _rayAll[i];
        _rayN = n;
        RayRun(useZ);
        long l2 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0, r = 0; i < all; i++)
        {
            int key = _rayAll[i], to, dist, tx, ty;
            string why;
            if (key < 0)
            {
                key = -1 - key;
                why = "glass"; to = -1; dist = GlassDrop; tx = key / 4 % _w; ty = key / 4 / _w;
                OnGlassRail(tx, ty, key & 3); // _glass をこの縁の物にする
            }
            else
            {
                why = RayWhy[_rayOut[r * 5]]; to = _rayOut[r * 5 + 1]; dist = _rayOut[r * 5 + 2]; tx = _rayOut[r * 5 + 3]; ty = _rayOut[r * 5 + 4];
                r++;
            }
            LinkEdge(key >> 2, key & 3, why, to, dist, tx, ty, full, measure, cliffs, useZ);
        }
        _ledgeScan = false;
        if (!full)
        {
            long l3 = System.Diagnostics.Stopwatch.GetTimestamp();
            LinkProf[0] += l1 - l0; LinkProf[1] += l2 - l1; LinkProf[2] += l3 - l2;
            LinkPasses++; LinkEdges += all;
        }
    }

    // 部分の引き直しの区間の時間 (water prof): 走査・光線・登録
    internal static readonly long[] LinkProf = new long[3];
    internal static long LinkPasses, LinkEdges;

    private static int[] _rayAll = Array.Empty<int>();

    private static void LinkEdge(int k, int d, string why, int to, int dist, int tx, int ty, bool full, bool measure, bool cliffs, bool useZ)
    {
        int x = k % _w, y = k / _w;
        // 画面の下へ面を越えて下の床に着いた長さ = 段の差 (横を向いた崖も、下へ探した長さ)
        if (measure && to >= 0 && (why == "fall" && d == 3 || why == "side")) ZSample(k, to, dist - 1);
        // 小さな段差の下の床から上の床へ (上から下へは落ちる縁の方で登録する)。両側から別々に登録するので、
        // 部分の作り直しで片側だけが範囲に入っても対で残る
        if (why == "same" && StepBetween(k, ty * _w + tx, dist, useZ, 1))
        {
            UseSlot(k);
            StepTo[k * 4 + d] = _stepTo[EdgeKey(k, d)] = ty * _w + tx;
            _stepMask[k] |= (byte)(1 << d);
            return;
        }
        if (to == int.MinValue && (why == "scan" || why == "border") && SeaEdge(x, y, d))
        {
            _seaMask[k] |= (byte)(1 << d);
            _seaListDirty = true;
            return;
        }
        if (to == int.MinValue || to == -1 && OnLadder(x, y)) return;
        // 空へ落とす縁とガラスの手すりは壁の影の線の外側へ落ちるので、影の線では切らない (塗った空も同じ)
        if (full && why != "glass" && why != "air" && why != "airside" && ShadowBetween(x, y, tx, ty, cliffs)) _shadowCut[k] |= (byte)(1 << d);
        if ((_shadowCut[k] >> d & 1) != 0) return;
        // 段差は滝にしない (段の高さぶんずらして流すだけ・床の絵にも落ちる縁を描かない)
        if (to >= 0 && StepBetween(k, to, dist, useZ, -1))
        {
            UseSlot(k);
            StepTo[k * 4 + d] = _stepTo[EdgeKey(k, d)] = to;
            _stepMask[k] |= (byte)(1 << d);
            return;
        }
        UseSlot(k);
        _fallMask[k] |= (byte)(1 << d);
        if (why == "glass") { _lipMask[k] |= (byte)(1 << d); _lipH[EdgeKey(k, d)] = (short)(_glass.Height * Full); }
        else if (PlatformRimAt(k, to)) { _lipMask[k] |= (byte)(1 << d); _lipH[EdgeKey(k, d)] = PlatformRim; }
        FallTo[k * 4 + d] = (to, 3 + 2 * ISqrt(dist));
        EdgeBarrier(x, y, d, k);
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
        if (!_ready)
        {
            FallTo[-1] = (0, 0); FallTo.Remove(-1);
            StepTo[-1] = 0; StepTo.Remove(-1);
            // 島の高さを決める時の並べ替えと測りの表 (型ごとの初回のコンパイル)
            var rel = new List<(int U, int V, int D)> { (2, 1, 3), (1, 2, 3) };
            rel.Sort((p, q) => p.U != q.U ? p.U.CompareTo(q.U) : p.V.CompareTo(q.V));
            foreach (var (u, v, d) in rel) _ = u + v + d;
            ZSamples[-1] = new List<int> { 2, 1 };
            _ = Median(ZSamples[-1]);
            ZSamples.Clear();
            _ = new System.Text.StringBuilder().Append(1.5f.ToString("0.00"));
        }
    }

    // 縁の柵の地図を読み、影の線の判定 (段の影の線か・屋外か) を船を横切る 1 本で通しておく (Warm の次のフレーム)
    internal static void WarmEdges()
    {
        if (!ShipStatus.Instance) return;
        WaterBarrier.Ensure();
        foreach (var h in Physics2D.CircleCastAll(new Vector2(-50f, 0f), 0.01f, Vector2.right, 100f, ShadowMask))
        {
            var c = h.collider;
            if (!c || IsLedge(c) || OutsideShadow.ContainsKey(c.GetInstanceID())) continue;
            bool outside = false;
            for (var t = c.transform; t && !outside; t = t.parent) outside = t.name == "Outside";
            OutsideShadow[c.GetInstanceID()] = outside;
        }
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

    // 手すりだけで囲まれた屋外のデッキの床 (x0, y0, x1, y1)。エアシップの警備室の下 (扉の前の通路は両脇が壁なので入れない)。
    // 境目は端末ごとの丸めで割れないよう 1/512 格子の点から外す
    private static readonly float[] AirshipDecks = { 5.003f, -17.497f, 10.997f, -14.3f };
    private static readonly float[] NoDecks = { };
    // 島の順位では低く出るが、絵では一段高い床 (高さを上書きする・歩ける升だけ)。
    // エアシップの機関室の南の出現位置の帯は、白い鉄板の通路より一段高い桃色の床
    private static readonly float[] AirshipRaised = { -2.197f, -3.297f, 2.397f, -1.747f };

    private static void RaiseCells(float[] r, int level)
    {
        for (int k = 0; k < _w * _h; k++)
            if (_sub[k] != 0 && InRects(r, _org.x + (k % _w + 0.5f) * _cell, _org.y + (k / _w + 0.5f) * _cell)) _lvl[k] = (sbyte)level;
    }

    // 階段より低い段差 (滝にしない縁): 歩いてつながる同じ島の中で段が 1 つ違う隣 (段差の面は StepScan 升まで)。
    // はしごや崖でしかつながらない島どうしの段は滝
    private const int StepHeight = Full / 4; // 段の高さ (低い側はこの深さまで溜まってから高い床とつながる)
    private const int StepScan = 4;
    private static bool StepEdge(int k, int c, int dist) =>
        dist <= StepScan && _cellIsl != null && _cellIsl[k] != 0 && _cellIsl[k] == _cellIsl[c];
    private static float[] _decks = NoDecks;
    private static bool OnDeck(int x, int y) => OnDeck(_org.x + (x + 0.5f) * _cell, _org.y + (y + 0.5f) * _cell);
    internal static bool OnDeck(float px, float py) => InRects(_decks, px, py);
    private static bool InRects(float[] r, float px, float py)
    {
        for (int i = 0; i + 3 < r.Length; i += 4)
            if (px >= r[i] && px <= r[i + 2] && py >= r[i + 1] && py <= r[i + 3]) return true;
        return false;
    }

    private static byte[] _lipMask;   // 縁の向きのうち、低い壁 (手すり) の高さまで溜める向き
    private static short[] _lipH;     // その高さ (縁の枠・水の深さの物差し)
    private static LowWalls.Entry _glass; // 直前に OnGlassRail が見つけた低い壁
    private static bool OnGlassRail(int x, int y, int d) =>
        LowWalls.At(_org.x + (x + 0.5f) * _cell, _org.y + (y + 0.5f) * _cell, d, out _glass);

    // 縁 (k, d) を越え始める高さ: 低い壁ならその高さ・それ以外は床の縁の盛り上がり
    private static int LipOf(int k, int d) => (_lipMask[k] >> d & 1) != 0 ? _lipH[EdgeKey(k, d)] : FallLip;

    // 縁の上に立つ物 (柵の柱・網・手すり) で止まる割合 (/256) と、その物の高さ (水の深さの物差し)。
    // 止まる所の水はその高さを越えた分だけ、抜ける所 (柱の間・網の目) の水は縁の盛り上がりを越えた分だけ落ちる
    private static byte[] _barB;
    private static short[] _barH;
    private const int BarMax = Full;          // これより高い物は越えない (壁と同じ)

    private static void EdgeBarrier(int x, int y, int d, int k)
    {
        int key = EdgeKey(k, d);
        if ((_lipMask[k] >> d & 1) != 0)
        {
            // 低い壁の表 (部屋の絵に描き込まれた手すり・動く床の枠) は縁の全部を止める
            _barB[key] = 255;
            _barH[key] = _lipH[key];
        }
        float ex0 = _org.x + x * _cell, ey0 = _org.y + y * _cell, ex1 = ex0 + _cell, ey1 = ey0 + _cell;
        if (d == 0) ex0 = ex1; else if (d == 1) ex1 = ex0; else if (d == 2) ey0 = ey1; else ey1 = ey0;
        if (!WaterBarrier.Edge(ex0, ey0, ex1, ey1, d, out int b, out float hgt) || b == 0) return;
        int h = Math.Min(BarMax, (int)(hgt * Full));
        if (_barB[key] == 255) { _barH[key] = (short)Math.Max(_barH[key], h); return; }
        _barB[key] = (byte)Math.Min(255, b);
        _barH[key] = (short)h;
    }

    // 堰の式: 越えた高さ e の 1.5 乗 (1 刻みに越えた分の半分まで)
    private static int Weir(int e) => e <= 0 ? 0 : Math.Min(e / 2, (int)((long)e * ISqrt(16L * e) / WeirDiv));

    // 縁 (k, d) から 1 刻みに落ちる量。抜ける所と止まる所を別々の堰として足す
    private static int FallRate(int k, int d, int h)
    {
        int key = EdgeKey(k, d), b = _barB[key];
        if (b == 0) return Weir(h - LipOf(k, d));
        int open = b == 255 ? 0 : Weir(h - FallLip) * (256 - b);
        int over = _barH[key] >= BarMax ? 0 : Weir(h - Math.Max(FallLip, (int)_barH[key])) * (b == 255 ? 256 : b);
        return (open + over) / 256;
    }

    // 升の中の SolidMap の升に船の外が 1 つでもある
    private static bool SkyIn(int cx, int cy)
    {
        int sw = SolidMap.W;
        for (int b = 0; b < Sub * Sub; b++)
            if (SolidMap.OutsideAt((cy * Sub + b / Sub) * sw + cx * Sub + b % Sub)) return true;
        return false;
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

    // 段の影の線: 本編は足場や段の縁の視界の影の線を LedgeShadow と名付けている (壁の影の線とは別・壁の向こうの判定に数えない)
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

    // 影の線は壊れた壁と一緒に切られ人によって先後があるので、読むのは作った時 (_ledgeScan) だけ
    private static bool _ledgeScan;

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
                string why = Nx[d] != 0 && OnLadder(x, y) ? "ladder" : Target(x, y, d, lk, out to, out _, out tx, out ty);
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
        // 高さが変わった升の隣は流れが変わりうるので、眠っていれば起こす
        if (x >= 1 && y >= 1 && x < _w - 1 && y < _h - 1) WakeAround(k);
    }

    // 乾いた隣は流れ出さない (水が入る所で起こされる) ので、起こすのは濡れた隣だけ
    private static void WakeAround(int k)
    {
        Wake(k);
        for (int d = 0; d < 4; d++)
        {
            int j = k + Nx[d] + Ny[d] * _w;
            if (_hgt[j] > 0) Wake(j);
        }
    }

    // 範囲の濡れた升を起こす (流れの決まり方 (縁・段差・落ちる縁) を作り直した所)
    private static void WakeWet(int x0, int y0, int x1, int y1)
    {
        x0 = Math.Max(1, x0); y0 = Math.Max(1, y0);
        x1 = Math.Min(_w - 2, x1); y1 = Math.Min(_h - 2, y1);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
            if (_hgt[y * _w + x] > 0) Wake(y * _w + x);
    }

    private static void MarkTile(int tx, int ty)
    {
        if (tx < 0 || ty < 0 || tx >= _tw || ty >= _th) return;
        int t = ty * _tw + tx;
        if (_tileDirty[t] != 0) return;
        _tileDirty[t] = 1;
        DirtyTiles.Add(t);
    }

    // 絵が描き直したタイルの印を消す
    internal static void Clean(int t) { EnsureIdle(); _tileDirty[t] = 0; }

    // 地図の端の升 (外周 1 升) は計算に入れない (隣を読む時に配列の外へ出る)
    // 中の詰まった家具の足元の床 (水の深さの物差し・床の高さ _bed に足す)。PropSim が刻みの始めに付け替える
    private static short[] _furn;
    internal static int FurnAt(int k) => _furn == null ? 0 : _furn[k];

    internal static void SetTops(int x0, int y0, int x1, int y1, int top) => Tops(x0, y0, x1, y1, top, false);
    internal static void RaiseTops(int x0, int y0, int x1, int y1, int top) => Tops(x0, y0, x1, y1, top, true);

    // 床が変わった升と隣を起こす (上がった升の水は天板の上に押し上げられ、下がった升へは隣から流れ込む)
    private static void Tops(int x0, int y0, int x1, int y1, int top, bool raise)
    {
        if (_furn == null) return;
        x0 = Math.Max(1, x0); y0 = Math.Max(1, y0);
        x1 = Math.Min(_w - 2, x1); y1 = Math.Min(_h - 2, y1);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int k = y * _w + x;
            int v = raise ? Math.Max(_furn[k], top) : top;
            if (v == _furn[k]) continue;
            _furn[k] = (short)v;
            Wake(k);
            for (int d = 0; d < 4; d++) Wake(k + Nx[d] + Ny[d] * _w);
            Mark(k);
        }
    }

    // 床マスクに出る落ちる縁 (StampLips と同じ判定) の、出来事の前の写し。範囲は両端を含む
    private static byte[] _lipSnap = Array.Empty<byte>();
    private static int _snapX0, _snapY0, _snapX1, _snapY1;

    private static void SnapLips(int x0, int y0, int x1, int y1)
    {
        _snapX0 = Math.Max(0, x0); _snapY0 = Math.Max(0, y0);
        _snapX1 = Math.Min(_w - 1, x1); _snapY1 = Math.Min(_h - 1, y1);
        int sw = _snapX1 - _snapX0 + 1, sh = _snapY1 - _snapY0 + 1;
        if (sw <= 0 || sh <= 0) return;
        if (_lipSnap.Length < sw * sh) _lipSnap = new byte[sw * sh];
        for (int y = _snapY0, i = 0; y <= _snapY1; y++)
            for (int x = _snapX0, k = y * _w + _snapX0; x <= _snapX1; x++, k++, i++)
                _lipSnap[i] = (_fallMask[k] & ~_lipMask[k]) != 0 ? (byte)1 : (byte)0;
    }

    // 写しと今を比べ、変わった升の範囲を (x0, y0)〜(x1, y1) (x1, y1 は含まない) へ足す
    private static void LipsChanged(ref int x0, ref int y0, ref int x1, ref int y1)
    {
        for (int y = _snapY0, i = 0; y <= _snapY1; y++)
            for (int x = _snapX0, k = y * _w + _snapX0; x <= _snapX1; x++, k++, i++)
            {
                byte now = (_fallMask[k] & ~_lipMask[k]) != 0 ? (byte)1 : (byte)0;
                if (now == _lipSnap[i]) continue;
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y);
                x1 = Math.Max(x1, x + 1); y1 = Math.Max(y1, y + 1);
            }
    }

    // 回す升に入れる。濡れていて回す升に居ない升は「眠っている」= 流れ出しが 0 のまま変わらない升 (片付けで外す)。
    // 自分か隣の高さ・地形が変わる所は必ずここを通して起こす
    private static void Wake(int k)
    {
        if (_inList[k] != 0) { _stir[k] = 1; return; }
        int x = k % _w, y = k / _w;
        if (x < 1 || y < 1 || x >= _w - 1 || y >= _h - 1) return;
        _inList[k] = 1;
        _stir[k] = 1;
        _act[_actN++] = k;
    }

    // ── 1 刻み ─────────────────────────────────────────────────────────

    // 1 刻みの区間ごとの時間 (water prof)。出来事・扉と着水・噴き出し・吸い出しと海・小刻み・縁を越える量・落ちる水・片付け
    internal static readonly long[] ProfTicks = new long[8];
    internal static long ProfSteps;
    private static long _profT;
    private static void Prof(int i)
    {
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        ProfTicks[i] += t - _profT;
        _profT = t;
    }

    // defer = 小刻みを預ける (作業スレッドで回して次のフレームの頭で後始末)。預けたら真
    private static bool StepOnce(bool defer = false)
    {
        _profT = System.Diagnostics.Stopwatch.GetTimestamp();
        ProfSteps++;
        // この刻みの出来事
        int fx0 = int.MaxValue, fy0 = int.MaxValue, fx1 = int.MinValue, fy1 = int.MinValue;
        while (Queue.Count > 0 && Queue[0].Tick <= _step)
        {
            var p = Queue[0];
            Queue.RemoveAt(0);
            if (p.Leak) { Sources.Add(p.Src); continue; }
            if (p.Inflow) { Inflows.Add(new Inflow { K = p.Cy * _w + p.Cx, End = p.Sr > 0 ? p.Tick + p.Sr : 0, Rate = p.Sp }); continue; }
            if (p.Push) { Push(p.Sx, p.Sy, p.Sr, p.Sp, p.Bx, p.By, p.Seed); continue; }
            int reach = p.R + FallScan[3] + 1;
            SnapLips(p.Cx - reach, p.Cy - reach, p.Cx + reach, p.Cy + reach);
            Rebuild(p.Cx - p.R, p.Cy - p.R, p.Cx + p.R, p.Cy + p.R);
            Links(p.Cx - reach, p.Cy - reach, p.Cx + reach, p.Cy + reach);
            // 壁の穴は歩ける所が変わるので、水の写し元をその周りだけ決め直す。
            // 穴が床マスクに出る分は壁の絵の側 (HeightChanged) がタイルの床を作り直す
            WaterArt.FillChanged(Rect.MinMaxRect(_org.x + (p.Cx - p.R) * _cell, _org.y + (p.Cy - p.R) * _cell,
                _org.x + (p.Cx + p.R + 1) * _cell, _org.y + (p.Cy + p.R + 1) * _cell));
            // 落ちる縁は床の形にも出る (StampLips) ので、縁が本当に変わった升の範囲だけタイルの床を作り直す
            LipsChanged(ref fx0, ref fy0, ref fx1, ref fy1);
            if (p.Shock) Shock(p.Sx, p.Sy, p.Sr, p.Sp, p.Bx, p.By, p.Seed);
        }
        // 中の詰まった家具の足元の床 (この刻みの確定した家具の姿)
        PropSim.StampTops();
        if (fx0 <= fx1)
            WaterArt.FloorChanged(Rect.MinMaxRect(_org.x + fx0 * _cell, _org.y + fy0 * _cell, _org.x + fx1 * _cell, _org.y + fy1 * _cell));
        Prof(0);

        // 扉: この刻みの開き (ホストの記録)
        ulong doors = Decompression.DoorBitsAt(_step);
        if (doors != _doorBits) ApplyDoors(doors);
        StepPlatform();

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

        // 浸水の口の寿命 (入れるのは小刻みごと)
        for (int i = Inflows.Count - 1; i >= 0; i--)
        {
            var f = Inflows[i];
            if (f.End != 0 && _step >= f.End) Inflows.RemoveAt(i);
        }
        for (int i = Jets.Count - 1; i >= 0; i--)
            if (_step - Jets[i].Start >= JetLife) Jets.RemoveAt(i);

        Prof(1);
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
        Prof(2);

        // 吸い出し: 減圧の場はこの刻みの物 (TerrainStep が先に減圧をこの刻みまで進めている)
        bool blow = Decompression.Running && Decompression.Pulling;
        // 吸い出しの場は升ごとに流れを足すので、吸い出しの間は眠らせない (始まった刻みに全部起こす)
        if (blow && !_blowing) WakeWet(1, 1, _w - 2, _h - 2);
        _blowing = blow;
        // 口の際の升の水は宇宙へ出る (流れを決める前に自分の升だけを減らすので順番に依らない)
        if (blow)
        {
            for (int a = 0; a < _actN; a++) if (_hgt[_act[a]] > 0) Drain(_act[a]);
            BlowTable();
        }
        SeaInflow();
        Prof(3);

        if (defer && !Native.MrpNative.Verify) { Defer(_step, blow); return true; }
        RunSubsteps(blow, _step);
        PostStep(_step, blow);
        return false;
    }

    // 小刻み SubN 回を C++ でまとめて 1 回で
    private static void RunSubsteps(bool blow, int step)
    {
        if (_fs.Length < _fallAt.Length * 3) _fs = new int[_fallAt.Length * 3];
        if (Native.MrpNative.Verify) VerifyStep(step, blow);
        else if (!StepNative(false, step, blow)) NativeRefused();
        Prof(4);
    }

    // 吸い出しの表 (C++ の小刻みが読む・升ごと): 満タン時の引く強さ (/1024・0 = 引かない)・範囲・向き ((dx+4) | (dy+4) << 4)。
    // 場が作り直された時だけ作り直し、範囲ごとの気圧は刻みごとに写す。刻みを預ける前に作るので、作業スレッドが回す間に場が変わっても混ざらない
    private static ushort[] _blowS = Array.Empty<ushort>();
    private static short[] _blowComp = Array.Empty<short>();
    private static byte[] _blowDir = Array.Empty<byte>();
    private static int[] _blowPress = Array.Empty<int>();
    private static int _blowGen = -1;

    private static void BlowTable()
    {
        int n = _w * _h;
        if (_blowGen != Decompression.FieldGen || _blowS.Length != n)
        {
            _blowGen = Decompression.FieldGen;
            if (_blowS.Length != n) { _blowS = new ushort[n]; _blowComp = new short[n]; _blowDir = new byte[n]; }
            for (int y = 0; y < _h; y++)
            for (int x = 0; x < _w; x++)
            {
                int k = y * _w + x;
                if (!Decompression.PullAt(x * Sub, y * Sub, out int s1024, out int comp, out int dx, out int dy) || s1024 <= 0)
                {
                    _blowS[k] = 0;
                    continue;
                }
                _blowS[k] = (ushort)s1024;
                _blowComp[k] = (short)comp;
                _blowDir[k] = (byte)((dx + 4) | (dy + 4) << 4);
            }
        }
        int rc = Decompression.RegionCount;
        if (_blowPress.Length < rc) _blowPress = new int[Math.Max(rc, 8)];
        for (int c = 0; c < rc; c++) _blowPress[c] = Decompression.PressOf(c);
    }

    // C++ が刻みを断った (定数が C# と違う = 部品の作り違い)。C# で代わりに回さず、その刻みは水を動かさない
    private static bool _refusedLogged;
    private static void NativeRefused()
    {
        if (_refusedLogged) return;
        _refusedLogged = true;
        Plugin.Logger.LogError($"[WaterSim] native step refused (constants differ) {Native.MrpNative.Status}");
    }

    // 小刻みの後: 落ちる水・眠らせる升の片付け (刻み step)
    private static void PostStep(int step, bool blow)
    {
        // 縁を越えた水: 落ちる時間の後に下の升へ (奈落なら消える)。1 刻みの合計で 1 回
        for (int i = 0; i < _fsN; i++)
        {
            int k = _fs[i * 3], d = _fs[i * 3 + 1], amt = _fs[i * 3 + 2];
            _fallAt[EdgeKey(k, d)] = 0;
            FellTotal += amt;
            var (to, delay) = FallTo[k * 4 + d];
            if (to < 0) VoidTotal += amt;
            else AddFlying(step + delay, to, amt);
            FallFx(k, d, to, amt, delay);
        }
        _fsN = 0;
        Prof(6);

        // 水も流れも無い升を外す。濡れていても、この刻みに起こされず流れ出しが全部 0 の升は眠らせる
        // (流れ出しは自分と隣の高さと地形だけで決まり、それが変わる所は全部 Wake を通るので、起こされるまで 0 のまま)。
        // 浅くて乾いていく升・段差と海の縁 (離れた升や海面を読む) は眠らせない
        int o = 0, kept = 0;
        for (int a = 0; a < _actN; a++)
        {
            int k = _act[a];
            bool still = true;
            for (int d = 0; d < 4; d++) if (_flux[k * 4 + d] != 0 || _move[k * 4 + d] != 0) { still = false; break; }
            bool stir = _stir[k] != 0;
            _stir[k] = 0;
            int h = _hgt[k];
            if (still && (h == 0 || !stir && !blow && !_noSleep && h >= MinDepth && _stepMask[k] == 0 && _seaMask[k] == 0)) { _inList[k] = 0; continue; }
            _act[o++] = k;
            if (a < _sortedN) kept++;
        }
        _actN = o;
        // 升の番号順に並べておく (隣の升を続けて読むので速い。C# 版と C++ 版で同じ順)。
        // 前の刻みの終わりに並べた分は外しても番号順のままなので、後から起こした分だけ並べて後ろから混ぜる
        SortTail(kept);
        _sortedN = _actN;
        if (Native.MrpNative.Verify)
            for (int a = 1; a < _actN; a++) if (_act[a - 1] >= _act[a]) VerifyDiff++;
        Prof(7);

        Steps++;
        if (step % 300 == 0) { LastDigest = Digest(step); Plugin.Logger.LogInfo($"[WaterSim] step={step} digest={LastDigest:x8} active={_actN} volume={Volume()}"); }
    }

    // C# 版の小刻み SubN 回 (浸水の口へ注ぐ → DropVoid → Flow → Heights → 縁を越えた量)。吸い出しは最初の小刻みだけ
    private static void StepManaged(bool blow, int step)
    {
        for (int s = 0; s < SubN; s++)
        {
            // 浸水: 口の周りの開いている升へ等分して入れる (1 刻みの量を小刻みに分ける)
            for (int i = 0; i < Inflows.Count; i++)
            {
                int amt = Share(Inflows[i].Rate, s);
                if (amt > 0) Pour3(Inflows[i].K, amt);
            }
            DropVoid();
            Flow(s, blow && s == 0);
            Heights(s == SubN - 1 && (step & 3) == 0);
            Prof(4);
            SubFalls();
            Prof(5);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct FlowArgs
    {
        public int* Hgt, Flux, Move;
        public byte* Open, Edge; public sbyte* Lvl;
        public short* Bed, Furn;
        public byte* StepMask, FallMask, SeaMask, LipMask;
        public short* LipH; public byte* BarB; public short* BarH; public int* StepTo;
        public int* Act; public byte* InList; public int* Fo; public byte* TileDirty; public int* DirtyOut;
        public int ActN, FoN, DirtyN;
        public int W, H, Tw, Th, TileCells;
        public int Sub, SubN, Dry, SeaLevel;
        public long VoidAdd;
        public byte* Stir;
        public int CheckSleep, SleepViol;
        public byte* Isl; public int* IslBase;
        public int IslN, UseZ;
        public int FoCap, FoOver;
        public int* Slot;
        public ushort* BlowS; public short* BlowComp; public byte* BlowDir; public int* BlowPress;
        public int Blow, BlowPressN;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct StepArgs
    {
        public FlowArgs F;
        public int* InfK, InfRate; public int InfN;
        public int DryStep;
        public int* FallStep; public int FsN, FsCap, FsOver;
        public int* FallAt;
        public long InflowAdd, SeaOutAdd;
    }

    private static int[] _dirtyBuf = Array.Empty<int>();
    private static int[] _infK = Array.Empty<int>(), _infRate = Array.Empty<int>();

    // C++ 版の小刻み SubN 回 (その場で)。check = 照合 (C# と同じ古いルールで回し、眠っている升の起こし忘れも数える)
    private static unsafe bool StepNative(bool check, int step, bool blow)
    {
        var a = Args();
        Fill(a, check, step, blow);
        int r;
        try { r = Native.MrpNative.WaterStep(a); }
        finally { Unpin(); }
        if (r == 0) return false;
        Apply(a);
        return true;
    }

    // 答え合わせ (native verify): 同じ刻みの小刻みを C++ と C# の両方で回して、升の水・流れ・回す升・縁を越える量・描き直すタイルを比べる。続きは C# の結果で
    internal static long VerifySteps, VerifyDiff;
    internal static long VerifySleepViol; // 眠っている升が流れ出すはずだった・浅かった数 (起こし忘れ)

    private static void VerifyStep(int step, bool blow)
    {
        int n = _hgt.Length;
        // 前の状態を取っておく
        var hgt0 = (int[])_hgt.Clone(); var flux0 = (int[])_flux.Clone(); var move0 = (int[])_move.Clone();
        var act0 = (int[])_act.Clone(); var in0 = (byte[])_inList.Clone(); var dirty0 = (byte[])_tileDirty.Clone(); var stir0 = (byte[])_stir.Clone();
        var fo0 = (int[])_fo.Clone(); var fs0 = (int[])_fs.Clone(); var fallAt0 = (int[])_fallAt.Clone();
        int actN0 = _actN, foN0 = _foN, fsN0 = _fsN, dirtyN0 = DirtyTiles.Count;
        long void0 = VoidTotal, inflow0 = InflowTotal, seaOut0 = SeaOutTotal;
        if (!StepNative(true, step, blow)) { NativeRefused(); return; }
        var vHgt = (int[])_hgt.Clone(); var vFlux = (int[])_flux.Clone(); var vMove = (int[])_move.Clone();
        var vAct = (int[])_act.Clone(); var vIn = (byte[])_inList.Clone(); var vStir = (byte[])_stir.Clone();
        var vFo = (int[])_fo.Clone(); var vFs = (int[])_fs.Clone(); var vFallAt = (int[])_fallAt.Clone();
        var vDirty = DirtyTiles.GetRange(dirtyN0, DirtyTiles.Count - dirtyN0);
        int nActN = _actN, nFoN = _foN, nFsN = _fsN;
        long nVoid = VoidTotal, nInflow = InflowTotal, nSeaOut = SeaOutTotal;
        // 戻して C# で
        Array.Copy(hgt0, _hgt, n); Array.Copy(flux0, _flux, n * 4); Array.Copy(move0, _move, n * 4);
        Array.Copy(act0, _act, n); Array.Copy(in0, _inList, n); Array.Copy(dirty0, _tileDirty, dirty0.Length); Array.Copy(stir0, _stir, n);
        Array.Copy(fo0, _fo, fo0.Length); Array.Copy(fs0, _fs, fs0.Length); Array.Copy(fallAt0, _fallAt, fallAt0.Length);
        _actN = actN0; _foN = foN0; _fsN = fsN0; VoidTotal = void0; InflowTotal = inflow0; SeaOutTotal = seaOut0;
        DirtyTiles.RemoveRange(dirtyN0, DirtyTiles.Count - dirtyN0);
        StepManaged(blow, step);
        VerifySteps++;
        long diff = 0;
        if (nActN != _actN) diff += 1000000;
        if (nFoN != _foN) diff += 1000000;
        if (nFsN != _fsN) diff += 1000000;
        if (nVoid != VoidTotal || nInflow != InflowTotal || nSeaOut != SeaOutTotal) diff += 1000000;
        if (vDirty.Count != DirtyTiles.Count - dirtyN0) diff += 1000000;
        else for (int i = 0; i < vDirty.Count; i++) if (vDirty[i] != DirtyTiles[dirtyN0 + i]) diff++;
        for (int i = 0; i < n; i++) { if (vHgt[i] != _hgt[i]) diff++; if (vIn[i] != _inList[i]) diff++; if (vStir[i] != _stir[i]) diff++; }
        for (int i = 0; i < n * 4; i++) { if (vFlux[i] != _flux[i]) diff++; if (vMove[i] != _move[i]) diff++; }
        for (int i = 0; i < Math.Min(nActN, _actN); i++) if (vAct[i] != _act[i]) diff++;
        for (int i = 0; i < Math.Min(nFoN, _foN) * 3; i++) if (vFo[i] != _fo[i]) diff++;
        for (int i = 0; i < Math.Min(nFsN, _fsN) * 3; i++) if (vFs[i] != _fs[i]) diff++;
        for (int i = 0; i < _fallAt.Length; i++) if (vFallAt[i] != _fallAt[i]) diff++;
        VerifyDiff += diff;
    }

    // 1 刻みの量 f のうち小刻み s に運ぶ分 (SubN 回の合計がちょうど f)
    private static int Share(int f, int s) => (int)((long)f * (s + 1) / SubN - (long)f * s / SubN);

    // 奈落の升の水は消える (落ちていく)
    private static void DropVoid()
    {
        for (int a = 0; a < _actN; a++)
        {
            int k = _act[a];
            if (_lvl[k] != VoidLevel || _hgt[k] <= 0) continue;
            VoidTotal += _hgt[k];
            _hgt[k] = 0;
            Mark(k);
        }
    }

    // 流れ: 各升が自分の 4 本の管の流れ出しを決める (読むのは前の小刻みの高さだけ・順番に依らない)。
    // 低い床の隣へは向こうの水に押し返されずに流れ、高い床の隣へは流れない。縁を越えて落ちる分は FallOut へ。
    // 勢いの無い流れ (段差・堰・海) の量は 1 刻みあたりで決めて小刻みに分ける
    private static void Flow(int s, bool blow)
    {
        _foN = 0;
        for (int a = 0; a < _actN; a++)
        {
            int k = _act[a];
            int h = _hgt[k];
            int sum = 0;
            int b0 = 0, b1 = 0, b2 = 0, b3 = 0;
            if (blow && h > 0) Blow(k, h, out b0, out b1, out b2, out b3);
            int lk = _lvl[k];
            int falls = _foN;
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
                        // 同じ段は水面 (床の高さ + 深さ) の差で流れる。低い段へは向こうの水に押し返されない
                        int hj = _hgt[j];
                        int diff = known && lj < lk ? h - DryHold : h + _bed[k] + _furn[k] - hj - _bed[j] - _furn[j] - (hj == 0 ? DryHold : WetHold);
                        f = _flux[k * 4 + d] * SubDamp / 256 + (diff > 0 ? diff * SubGain / 256 : 0);
                        if (f < 0) f = 0;
                        f += d == 0 ? b0 : d == 1 ? b1 : d == 2 ? b2 : b3;
                    }
                }
                else if (h > 0 && (_stepMask[k] >> d & 1) != 0)
                {
                    // 小さな段差: 下の床の水面は段の高さぶん低い。上からは下の水面 (段より上に出た分) との差で、
                    // 下からは段を越えた分と上の水との差で流れる
                    int t = _stepTo[EdgeKey(k, d)], ht = _hgt[t];
                    int diff = _lvl[t] < lk
                        ? h - Math.Max(0, ht - StepHeight) - (ht <= StepHeight ? DryHold : WetHold)
                        : h - StepHeight - ht - (ht == 0 ? DryHold : WetHold);
                    if (diff > 0) sum += SubOut(k, d, diff * Gain / 256, s);
                }
                else if ((_fallMask[k] >> d & 1) != 0 && h > FallLip)
                {
                    int g = FallRate(k, d, h);
                    if (g > 0) sum += SubOut(k, d, g, s);
                }
                else if (h > 0 && (_seaMask[k] >> d & 1) != 0)
                {
                    // 海へ: 水面が海面より高い分だけ出ていく
                    int diff = h + _bed[k] + _furn[k] - SeaLevel - WetHold;
                    if (diff > 0) sum += SubOut(k, d, diff * Gain / 256, s);
                }
                _flux[k * 4 + d] = f;
                int q = Share(f, s);
                _move[k * 4 + d] = q;
                sum += q;
                if (f > 0) Wake(j);
            }
            if (sum > h)
            {
                for (int d = 0; d < 4; d++)
                {
                    _flux[k * 4 + d] = (int)((long)_flux[k * 4 + d] * h / sum);
                    _move[k * 4 + d] = (int)((long)_move[k * 4 + d] * h / sum);
                }
                for (int i = falls; i < _foN; i++) _fo[i * 3 + 2] = (int)((long)_fo[i * 3 + 2] * h / sum);
            }
        }
    }

    // 勢いの無い流れ出し g (1 刻みあたり・最低 1) のうちこの小刻みの分を FallOut へ
    private static int SubOut(int k, int d, int g, int s)
    {
        if (g <= 0) g = 1;
        int q = Share(g, s);
        if (q > 0) { _fo[_foN * 3] = k; _fo[_foN * 3 + 1] = d; _fo[_foN * 3 + 2] = q; _foN++; }
        return q;
    }

    // 高さ: 入ってくる流れ − 出ていく流れ
    private static void Heights(bool dry)
    {
        int n = _actN; // 途中で起こした升は流れ込みが無い (流れ込みは Flow で起こす) ので、この回では回さない
        for (int a = 0; a < n; a++)
        {
            int k = _act[a];
            int dh = 0;
            for (int d = 0; d < 4; d++)
            {
                dh -= _move[k * 4 + d];
                int j = k + Nx[d] + Ny[d] * _w;
                dh += _move[j * 4 + Opp[d]];
            }
            if (dh != 0) { _hgt[k] += dh; Mark(k); }
            // 浅すぎる升は 4 刻みに 1 ずつ乾く
            if (dry && _hgt[k] > 0 && _hgt[k] < MinDepth) { _hgt[k]--; Mark(k); }
        }
    }

    // 縁を越えた水を縁の升から引く。段差と海へはすぐ移し、落ちる分は 1 刻みの合計 (_fs) へ足す
    private static void SubFalls()
    {
        for (int i = 0; i < _foN; i++)
        {
            int k = _fo[i * 3], d = _fo[i * 3 + 1], a0 = _fo[i * 3 + 2];
            // 量は高さを決める前の水で決めてあり、間に浅い升が乾いて 1 減ることがある
            int amt = Math.Min(a0, _hgt[k]);
            if (amt <= 0) continue;
            _hgt[k] -= amt;
            Mark(k);
            if ((_seaMask[k] >> d & 1) != 0) { SeaOutTotal += amt; continue; }
            if ((_stepMask[k] >> d & 1) != 0)
            {
                int t = _stepTo[EdgeKey(k, d)];
                _hgt[t] += amt;
                Mark(t);
                Wake(t);
                continue;
            }
            int key = EdgeKey(k, d), at = _fallAt[key];
            if (at == 0) { _fs[_fsN * 3] = k; _fs[_fsN * 3 + 1] = d; _fs[_fsN * 3 + 2] = amt; _fallAt[key] = ++_fsN; }
            else _fs[(at - 1) * 3 + 2] += amt;
        }
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
        // ガラスの手すりは越えた水が手すりの上の縁からこぼれる (絵では高さは上へずれる)
        // 低い壁 (手すり) は越えた水が絵の上の縁からこぼれる (奥/手前の縁は絵の根元が升の縁とずれている所がある)
        float lip = 0f;
        if ((_lipMask[k] >> d & 1) != 0 && LowWalls.At(_org.x + (x + 0.5f) * _cell, _org.y + (y + 0.5f) * _cell, d, out var lw))
        {
            lip = lw.Height;
            y0 = Ny[d] != 0 ? lw.Top : y0 + lip;
        }
        float x1, y1;
        if (to >= 0) { x1 = _org.x + (to % _w + 0.5f) * _cell; y1 = _org.y + (to / _w + 0.5f) * _cell; }
        else { x1 = x0 + Nx[d] * (Nx[d] != 0 ? SideVoidOut : _cell); y1 = y0 + Ny[d] * _cell - 1.5f; } // 横の縁は外へ弧を描いて谷へ
        // 縁の水の深さと、縁に立つ物 (止まる割合・高さ)。絵は水面から落ち始め、物の無い所からは水面のまま、物のある所からは物の上を越えて落ちる
        float depth = _hgt[k] / (float)Full;
        int barB = _barB[EdgeKey(k, d)];
        float barH = _barH[EdgeKey(k, d)] / (float)Full;
        for (int i = Falls.Count - 1; i >= 0 && i >= Falls.Count - 8; i--)
        {
            var f = Falls[i];
            if (f.X0 != x0 || f.Y0 != y0) continue;
            Falls[i] = (x0, y0, x1, y1, f.Amount + amt, delay, to < 0, d, lip, depth, barB, barH, k);
            return;
        }
        Falls.Add((x0, y0, x1, y1, amt, delay, to < 0, d, lip, depth, barB, barH, k));
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
    private static string ArtProf()
    {
        double k = WaterArt.ProfTiles > 0 ? 1000.0 / System.Diagnostics.Stopwatch.Frequency / WaterArt.ProfTiles : 0;
        var t = WaterArt.ProfTicks;
        return $"raw={t[0] * k:0.000} face={t[1] * k:0.000} prep={t[2] * k:0.000} pix={t[3] * k:0.000} upload={t[4] * k:0.000} flow={t[5] * k:0.000} (face={t[6] * k:0.000} spread={t[7] * k:0.000}) thread={WaterArt.ProfThreadTiles}/{(WaterArt.ProfThreadTiles > 0 ? ArtThreadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / WaterArt.ProfThreadTiles : 0):0.000} foamThread={FoamThreadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0}ms fallThread={FallThreadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0}ms";
    }

    private static int _sortedN;     // _act の先頭のこの数までは番号順 (前の刻みの終わりに並べた分)
    private static int[] _sortTmp = Array.Empty<int>();

    private static void SortTail(int head)
    {
        int n = _actN - head;
        if (n <= 0) return;
        Array.Sort(_act, head, n);
        if (head == 0 || _act[head - 1] < _act[head]) return;
        if (_sortTmp.Length < n) _sortTmp = new int[Math.Max(n, 1024)];
        Array.Copy(_act, head, _sortTmp, 0, n);
        int i = head - 1, j = n - 1, w = _actN - 1;
        while (j >= 0)
        {
            if (i >= 0 && _act[i] > _sortTmp[j]) _act[w--] = _act[i--];
            else _act[w--] = _sortTmp[j--];
        }
    }

    // 指紋と量に数える升 = 回す升と眠っている升 (縁の升は回さないので数えない)
    private static bool Counted(int k)
    {
        if (_inList[k] != 0) return true;
        if (_hgt[k] == 0) return false;
        int x = k % _w, y = k / _w;
        return x >= 1 && y >= 1 && x < _w - 1 && y < _h - 1;
    }

    internal static uint Digest() => Digest(_step);

    // step = 刻みの番号 (預けた刻みの後始末では _step がもう次へ進んでいるので、同期で回した時と同じ番号を渡す)
    private static uint Digest(int step)
    {
        EnsureIdle();
        uint acc = (uint)step * 2654435761u + (uint)Particles * 40503u + (uint)Flying.Count * 0x27D4EB2Fu;
        foreach (var (due, to, amt) in Flying) acc += (uint)due * 0x9E3779B1u ^ (uint)to * 0x85EBCA77u ^ (uint)amt;
        for (int k = 0; _inList != null && k < _inList.Length; k++)
        {
            if (!Counted(k)) continue;
            uint v = (uint)k * 0x9E3779B1u ^ (uint)_hgt[k] * 0x85EBCA77u;
            v ^= v >> 15; v *= 0xC2B2AE3Du; v ^= v >> 13;
            acc += v;
        }
        return acc;
    }

    internal static long Volume()
    {
        EnsureIdle();
        long v = 0;
        for (int k = 0; _inList != null && k < _inList.Length; k++) if (Counted(k)) v += _hgt[k];
        foreach (var f in Flying) v += f.Amount;
        return v;
    }

    // その点の水の深さ (Full = 1 単位)
    internal static int DepthAt(Vector2 p)
    {
        EnsureIdle();
        if (!_ready) return 0;
        int x = (int)MathF.Floor((p.x - _org.x) / _cell), y = (int)MathF.Floor((p.y - _org.y) / _cell);
        if (x < 0 || y < 0 || x >= _w || y >= _h) return 0;
        return _hgt[y * _w + x];
    }

    // 点 (x, y0) から画面の下 (手前) へ y1 まで升を見て、最初に当たる床の絵の上の縁の y。外へ落ちる水はこれより下では手前の床の裏に隠れる。
    // その床の奥の縁に低い壁があれば壁の上の縁か、壁の上に盛り上がった水面の高いほう。床が無ければ float.MinValue
    internal static float FrontCut(float x, float y0, float y1)
    {
        EnsureIdle();
        if (!_ready) return float.MinValue;
        int cx = (int)MathF.Floor((x - _org.x) / _cell);
        if (cx < 0 || cx >= _w) return float.MinValue;
        int cy = (int)MathF.Floor((y0 - _org.y) / _cell), end = (int)MathF.Floor((y1 - _org.y) / _cell);
        if (cy >= _h) cy = _h - 1;
        for (; cy >= 0 && cy >= end; cy--)
        {
            int c = cy * _w + cx;
            if (_open[c] == 0) continue;
            float top = _org.y + (cy + 1) * _cell;
            if (LowWalls.At(_org.x + (cx + 0.5f) * _cell, _org.y + (cy + 0.5f) * _cell, 2, out var e))
                top = MathF.Max(e.Top, e.Base + _hgt[c] * (1f / Full));
            return top;
        }
        return float.MinValue;
    }

    // 床のマスクに縁を越えて落ちる升 (低い壁で溜める向きを除く) を床として足す。画素 k の真ん中 = x + k × step。
    // 床の絵は縁の手前で切れている所 (縁取りの帯) があり、水はその帯の上も縁まで流れているので、水たまりの絵を縁まで見せる
    // C++ の部品へ渡す手前の床の地図 (FrontCut と同じ物)。まだ升が無ければ false
    internal static bool CutMaps(out byte[] open, out int[] hgt, out int w, out int h, out float ox, out float oy)
    {
        open = _open; hgt = _hgt; w = _w; h = _h; ox = _org.x; oy = _org.y;
        return _ready && _open != null && _hgt != null;
    }

    // C++ の部品へ渡す落ちる縁の地図 (StampLips と同じ物)。まだ升が無ければ false
    internal static bool LipMaps(out byte[] fall, out byte[] lip, out int w, out int h, out float ox, out float oy, out float cell)
    {
        fall = _fallMask; lip = _lipMask; w = _w; h = _h; ox = _org.x; oy = _org.y; cell = _cell;
        return _ready && _fallMask != null;
    }

    // 落ちる縁の升を床にする (水が縁の線まで届く)。ただし落ちる縁から内へ LipReach (滝の膜が崖の縁を探す幅と同じ) までに
    // 床マスクの床があれば塗らない: 水の絵は床マスクの形で崖の縁まで届き、膜はそこから始まる (C++ の mrp_floor_lips と同じ)
    private const float LipInset = 0.02f, LipReach = 0.3f;
    private const int LipSteps = 15;

    private static bool LipCovered(int bits, int cx, int cy, byte[] mb, int mw, int mh, float mox, float moy, float mppu)
    {
        if (mb == null) return false;
        for (int d = 0; d < 4; d++)
        {
            if ((bits >> d & 1) == 0) continue;
            bool hit = false;
            for (int s = 0; s <= LipSteps && !hit; s++)
            {
                float r = 0.5f * _cell - (LipInset + (LipReach - LipInset) * s / LipSteps);
                float ex = _org.x + (cx + 0.5f) * _cell + Nx[d] * r;
                float ey = _org.y + (cy + 0.5f) * _cell + Ny[d] * r;
                int mx = (int)MathF.Floor((ex - mox) * mppu), my = (int)MathF.Floor((ey - moy) * mppu);
                if (mx < 0 || my < 0 || mx >= mw || my >= mh) continue;
                int i = my * mw + mx;
                hit = (mb[i >> 3] & (1 << (i & 7))) != 0;
            }
            if (!hit) return false;
        }
        return true;
    }

    internal static void StampLips(float x, float y, float step, int n, byte[] into, byte[] mb, int mw, int mh, float mox, float moy, float mppu)
    {
        if (!_ready || _fallMask == null) return;
        float inv = 1f / _cell;
        for (int py = 0; py < n; py++)
        {
            int cy = (int)MathF.Floor((y + py * step - _org.y) * inv);
            if (cy < 0 || cy >= _h) continue;
            int row = py * n, baseK = cy * _w;
            for (int px = 0; px < n; px++)
            {
                if (into[row + px] != 0) continue;
                int cx = (int)MathF.Floor((x + px * step - _org.x) * inv);
                if (cx < 0 || cx >= _w) continue;
                int k = baseK + cx;
                int b = _fallMask[k] & ~_lipMask[k];
                if (b != 0 && !LipCovered(b, cx, cy, mb, mw, mh, mox, moy, mppu)) into[row + px] = 255;
            }
        }
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
        EnsureIdle();
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
    // 絵用: 升 k を通る流れ (量/刻み・CurrentAt と同じ 2 面の和)。縁を越えて落ちる分は流れ (_flux) に入らないので、
    // 堰の式の量をその向きに足す (手すりへ向かう水が縁で止まって見えないように)
    internal static void FlowThrough(int k, out int qx, out int qy)
    {
        EnsureIdle();
        qx = _flux[k * 4] - _flux[(k + 1) * 4 + 1] + _flux[(k - 1) * 4] - _flux[k * 4 + 1];
        qy = _flux[k * 4 + 2] - _flux[(k + _w) * 4 + 3] + _flux[(k - _w) * 4 + 2] - _flux[k * 4 + 3];
        int fm = _fallMask[k];
        if (fm == 0) return;
        int h = _hgt[k];
        for (int d = 0; d < 4; d++)
        {
            if ((fm >> d & 1) == 0) continue;
            int g = FallRate(k, d, h);
            if (g <= 0) continue;
            qx += Nx[d] * g;
            qy += Ny[d] * g;
        }
    }

    internal static float CurrentAt(Vector2 p, out float vx, out float vy)
    {
        EnsureIdle();
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
        PollPlatform();
    }

    // 1 刻み進める (TerrainStep から・_step の刻み)。defer = 小刻みを預けてよい。預けたら偽 (続きは Settle)
    internal static bool AdvanceOne(bool defer = false)
    {
        bool deferred = false;
        try
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            deferred = StepOnce(defer);
            _step++;
            LastStepMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (LastStepMs > MaxStepMs) MaxStepMs = LastStepMs;
        }
        catch (Exception e) { Fail("step", e); }
        return !deferred;
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

    // 水の網が作れない理由 (確認口の返事用)
    private static string NoGrid() => "no map";

    internal static void Reset()
    {
        DropDeferred();
        _ready = false;
        _running = false;
        _open = null; _sub = null; _edge = null; _hgt = null; _flux = null; _inList = null; _stir = null; _tileDirty = null;
        _lvl = null; _shut = null; _doorCell = null; _doorFloor = null; _propCell = null; OutsideShadow.Clear(); LedgeShadow.Clear(); _doorUnder = null; _fallMask = null; _stepMask = null; _shadowCut = null; _air = null; _platFloor = null; _plat = false; _lipMask = null; ClearSlots(); _furn = null; _cellIsl = null; _islRank = null; _islArea = null; _islBase = Array.Empty<int>(); _rim = null; LowWalls.Clear();
        FallTo.Clear();
        StepTo.Clear();
        _foN = 0;
        Flying.Clear();
        Falls.Clear();
        FellTotal = 0;
        VoidTotal = 0;
        _bed = null; _seaCell = null; _seaMask = null; _seaCut = null; SeaCells.Clear(); _hasSea = false; SeaLevel = 0;
        SeaOutTotal = 0;
        SeaInTotal = 0;
        DirtyTiles.Clear();
        _actN = 0;
        _sortedN = 0;
        _blowing = false;
        _blowGen = -1;
        _noSleep = false;
        ZFlow = true;
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
        TestBridge.Register("water", "[reset | sweep start|check | spikes [ms|off] | flood [depth] | pour x y r [depth] | inflow x y [秒 (0=止まらない)] [速さ 0..1] | cell x0 x1 y | show [r] | hide] 水の計算 (show = 自分の周りに判定を色で重ねる: 赤 = 水の升が閉じている・橙 = 歩けない所・黄 = 家具で水を見せない所): 刻み・遅れ・指紋・量 (reset = 水を全部消す)", (args, reply) =>
        {
            string a = args.Trim();
            // 預けた刻みを終わらせてから (確認用の操作は水の配列を直に読み書きする)
            TerrainStep.Settle();
            if (a == "reset") Reset();
            if (a.StartsWith("sleep"))
            {
                if (a.EndsWith("off")) _noSleep = true;
                else if (a.EndsWith("on")) _noSleep = false;
                reply($"OK water sleep={!_noSleep} (this device only・水を消すと on に戻る)");
                return;
            }
            if (a.StartsWith("zflow"))
            {
                if (a.EndsWith("off")) ZFlow = false;
                else if (a.EndsWith("on")) ZFlow = true;
                reply($"OK water zflow={ZFlow} native={Native.MrpNative.Loaded} (this device only・水を消すと on に戻る)");
                return;
            }
            if (a.StartsWith("floorcheck"))
            {
                reply($"OK water floorcheck {WaterArt.FloorCheck()}");
                return;
            }
            if (a.StartsWith("spikes"))
            {
                // 刻みの計算が閾値 (ms・既定 1) を超えたフレームの区間の内訳を記録に出す。off で止める
                var q = a.Substring(6).Trim();
                TerrainStep.SpikeMs = q == "off" ? 0 : double.TryParse(q, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sm) ? sm : 1.0;
                reply($"OK water spikes {(TerrainStep.SpikeMs > 0 ? $"{TerrainStep.SpikeMs:0.##}ms" : "off")}");
                return;
            }
            if (a.StartsWith("prof"))
            {
                double k = ProfSteps > 0 ? 1000.0 / System.Diagnostics.Stopwatch.Frequency / ProfSteps : 0;
                var t = ProfTicks;
                int sleeping = 0;
                for (int c = 0; _inList != null && c < _inList.Length; c++) if (_inList[c] == 0 && Counted(c)) sleeping++;
                reply($"OK water prof steps={ProfSteps} active={_actN} sleeping={sleeping} ms/step events={t[0] * k:0.000} doors={t[1] * k:0.000} jets={t[2] * k:0.000} blow={t[3] * k:0.000} sub={t[4] * k:0.000} subfalls={t[5] * k:0.000} falls={t[6] * k:0.000} tidy={t[7] * k:0.000} | per step pre={TerrainStep.ProfTicks[0] * k:0.000} decomp={TerrainStep.ProfTicks[1] * k:0.000} prop={TerrainStep.ProfTicks[2] * k:0.000} water={TerrainStep.ProfTicks[3] * k:0.000} fire={TerrainStep.ProfTicks[4] * k:0.000} burn={TerrainStep.ProfTicks[5] * k:0.000} | art tiles/step={(ProfSteps > 0 ? (double)WaterArt.ProfTiles / ProfSteps : 0):0.00} ms/tile {ArtProf()} | thread async={AsyncSteps} sync={SyncSteps} wait={JoinWaitTicks * k:0.000} early={EarlyJoins} | links n={LinkPasses} edges={LinkEdges} ms/pass scan={(LinkPasses > 0 ? LinkProf[0] * 1000.0 / System.Diagnostics.Stopwatch.Frequency / LinkPasses : 0):0.000} ray={(LinkPasses > 0 ? LinkProf[1] * 1000.0 / System.Diagnostics.Stopwatch.Frequency / LinkPasses : 0):0.000} post={(LinkPasses > 0 ? LinkProf[2] * 1000.0 / System.Diagnostics.Stopwatch.Frequency / LinkPasses : 0):0.000} | floor queue={WaterArt.FloorFromQueue} dirty={WaterArt.FloorFromDirty} ms={WaterArt.FloorTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} thread={WaterArt.FloorFromThread}/{WaterArt.FloorThreadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0}ms");
                if (a.Contains("reset")) { Array.Clear(ProfTicks); Array.Clear(TerrainStep.ProfTicks); ProfSteps = 0; Array.Clear(WaterArt.ProfTicks); WaterArt.ProfTiles = 0; WaterArt.ProfThreadTiles = 0; ArtThreadTicks = FoamThreadTicks = FallThreadTicks = 0; AsyncSteps = SyncSteps = JoinWaitTicks = EarlyJoins = 0; Array.Clear(LinkProf); LinkPasses = LinkEdges = 0; WaterArt.FloorFromQueue = WaterArt.FloorFromDirty = WaterArt.FloorTicks = WaterArt.FloorFromThread = WaterArt.FloorThreadTicks = 0; }
                return;
            }
            if (a.StartsWith("flood"))
            {
                int depth = a.Length > 5 && int.TryParse(a.Substring(5).Trim(), out int dd) ? dd : Full / 4;
                int n = Flood(depth);
                reply(n < 0 ? $"ERR water flood: {NoGrid()}" : $"OK water flood cells={n} depth={depth}");
                return;
            }
            if (a.StartsWith("flow"))
            {
                // 確認用: 横一列 (step 升おき) の深さと流れの速さ (単位/秒・縁を越える分を含む・絵と同じ値)
                var q = a.Substring(4).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                var ns = System.Globalization.NumberStyles.Float;
                if (_open == null || q.Length < 3 || !float.TryParse(q[0], ns, ic, out float f0) || !float.TryParse(q[1], ns, ic, out float f1) || !float.TryParse(q[2], ns, ic, out float fy))
                { reply("ERR water flow x0 x1 y [step]"); return; }
                int st = q.Length > 3 && int.TryParse(q[3], out int st2) ? Math.Max(1, st2) : 2;
                var sb = new System.Text.StringBuilder("OK water flow");
                int yy = (int)MathF.Floor((fy - _org.y) / _cell);
                for (int xx = (int)MathF.Floor((f0 - _org.x) / _cell); xx <= (int)MathF.Floor((f1 - _org.x) / _cell); xx += st)
                {
                    if (xx < 1 || yy < 1 || xx >= _w - 1 || yy >= _h - 1) continue;
                    int k = yy * _w + xx;
                    FlowThrough(k, out int qx, out int qy);
                    float fs = 0.5f * _cell * GameClock.Hz / Math.Max(_hgt[k], Full / 8);
                    sb.Append($" | {_org.x + (xx + 0.5f) * _cell:0.00}:h{_hgt[k]} v({qx * fs:0.00},{qy * fs:0.00})");
                }
                reply(sb.ToString());
                return;
            }
            if (a.StartsWith("cell"))
            {
                // 横一列の升の中身: 開いているか・段・床の高さ・水・通れる縁 (右左上下の順・1 = 通れる)
                var q = a.Substring(4).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                if (_open == null || q.Length < 3 || !float.TryParse(q[0], System.Globalization.NumberStyles.Float, ic, out float c0)
                    || !float.TryParse(q[1], System.Globalization.NumberStyles.Float, ic, out float c1) || !float.TryParse(q[2], System.Globalization.NumberStyles.Float, ic, out float cy))
                { reply("ERR water cell x0 x1 y (no grid?)"); return; }
                var sb = new System.Text.StringBuilder("OK water cell");
                int yy = (int)MathF.Floor((cy - _org.y) / _cell);
                for (int xx = (int)MathF.Floor((c0 - _org.x) / _cell); xx <= (int)MathF.Floor((c1 - _org.x) / _cell); xx++)
                {
                    if (xx < 1 || yy < 1 || xx >= _w - 1 || yy >= _h - 1) continue;
                    int k = yy * _w + xx;
                    sb.Append($" | {_org.x + (xx + 0.5f) * _cell:0.##}:o{_open[k]}{(SkyIn(xx, yy) ? "s" : "")} l{_lvl[k]} b{_bed[k]}{(_furn[k] != 0 ? $" f{_furn[k]}" : "")} h{_hgt[k]} e");
                    for (int d = 0; d < 4; d++) sb.Append(Passable(k, d) ? '1' : '0');
                    if (_open[k] == 0) sb.Append(" F").Append(WaterArt.FillOf(k));
                    if (_open[k] != 0)
                        for (int d = 0; d < 4; d++)
                            if (!Passable(k, d)) sb.Append(' ').Append(d).Append('=').Append(Target(xx, yy, d, _lvl[k], out int to, out _, out _, out _)).Append(to == -1 ? "!" : "");
                }
                reply(sb.ToString());
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
                reply(pn < 0 ? $"ERR water pour: {NoGrid()}" : $"OK water pour cells={pn} depth={pd}");
                return;
            }
            if (a.StartsWith("bench"))
            {
                // 確認用: 水を消して丸く注ぎ、刻みを n 回その場で回して指紋と時間を返す (刻みの番号は 0 から・終わったら水を消す)。
                // 時刻に左右されないので、同じ地形なら端末や版をまたいで同じ指紋になる
                var q = a.Substring(5).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                var ns = System.Globalization.NumberStyles.Float;
                if (q.Length < 4 || !int.TryParse(q[0], out int bn) || !float.TryParse(q[1], ns, ic, out float bx) || !float.TryParse(q[2], ns, ic, out float by)
                    || !float.TryParse(q[3], ns, ic, out float br)) { reply("ERR water bench n x y r [depth]"); return; }
                int bd = q.Length > 4 && int.TryParse(q[4], out int bd2) ? bd2 : Full;
                bool bz = ZFlow;
                Reset();
                ZFlow = bz;
                if (Pour(bx, by, br, bd) < 0) { reply($"ERR water bench: {NoGrid()}"); return; }
                _step = 0;
                Array.Clear(ProfTicks); ProfSteps = 0;
                long b0 = System.Diagnostics.Stopwatch.GetTimestamp();
                long bact = 0;
                for (int i = 0; i < bn; i++)
                {
                    bact += _actN;
                    // 作業スレッドを使う時はゲーム中と同じく預けて投げて受け取る
                    bool deferred = StepOnce(Native.MrpNative.Async);
                    _step++;
                    if (deferred) { Kick(); Settle(); }
                }
                double bms = (System.Diagnostics.Stopwatch.GetTimestamp() - b0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                double pk = 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, bn);
                reply($"OK water bench steps={bn} digest={Digest():x8} volume={Volume()} active={_actN} avgActive={bact / Math.Max(1, bn)} fell={FellTotal} void={VoidTotal} native={Native.MrpNative.Loaded} zflow={ZFlow} ms/step={bms / Math.Max(1, bn):0.000} sub={ProfTicks[4] * pk:0.000} tidy={ProfTicks[7] * pk:0.000}");
                Reset();
                ZFlow = bz;
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
            if (a.StartsWith("barrier"))
            {
                // 確認用: barrier [x y] [r] = 止める絵と落ちる縁の止まる割合を Screens/barrier.ppm へ (既定は自分の周り 4)
                var q = a.Substring(7).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                var lp = PlayerControl.LocalPlayer;
                Vector2 at = lp ? (Vector2)lp.transform.position : Vector2.zero;
                float br = 4f;
                if (q.Length >= 2) at = new Vector2(float.Parse(q[0], ic), float.Parse(q[1], ic));
                if (q.Length == 1 || q.Length >= 3) br = float.Parse(q[q.Length == 1 ? 0 : 2], ic);
                reply(BarrierView(at.x, at.y, br));
                return;
            }
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
            if (a.StartsWith("sea"))
            {
                // 確認用 (自分の手元だけ・同期しない): 海面を変える (水の深さの物差し・Full = 1 単位)
                if (a.Length > 3 && int.TryParse(a.Substring(3).Trim(), out int lv)) SeaLevel = lv;
                reply($"OK water sea level={SeaLevel} out={SeaOutTotal} in={SeaInTotal} edges={(_seaMask == null ? 0 : CountBits(_seaMask))} bed={BedStats}");
                return;
            }
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
                sb.Clear();
                foreach (var kv in StepTo)
                {
                    int k = kv.Key / 4, d = kv.Key % 4;
                    sb.Append($"({_org.x + (k % _w + 0.5f) * _cell:0.00},{_org.y + (k / _w + 0.5f) * _cell:0.00}){"+x-x+y-y".Substring(d * 2, 2)} ");
                }
                reply("STEPS " + sb);
                uint hl = 2166136261, hi = 2166136261;
                for (int k = 0; k < _w * _h; k++) { hl = (hl ^ (byte)_lvl[k]) * 16777619; hi = (hi ^ (_cellIsl == null ? 0u : _cellIsl[k])) * 16777619; }
                // 升ごとの段・島・開き・縁を書き出す (端末の突き合わせ用)
                var dump = new byte[_w * _h * 4];
                for (int k = 0; k < _w * _h; k++) { dump[k * 4] = (byte)_lvl[k]; dump[k * 4 + 1] = _cellIsl == null ? (byte)0 : _cellIsl[k]; dump[k * 4 + 2] = _open[k]; dump[k * 4 + 3] = _edge[k]; }
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(MoreRolesPlus.Bridge.TestBridge.ScreensDir, "waterlinks.bin"), dump);
                reply($"OK water links n={FallTo.Count} steps={StepTo.Count} lvl={hl:x8} isl={hi:x8} grid={GridHash():x8} w={_w} h={_h} org={_org.x:0.0000},{_org.y:0.0000} cell={_cell:0.000000}");
                return;
            }
            if (a.StartsWith("sweep")) { Sweep(a.Substring(5).Trim(), reply); return; }
            if (a == "platuse")
            {
                // 確認用: 昇降機を動かす (自分が乗る)
                var pf = FindPlatform();
                if (!pf) { reply("ERR no platform"); return; }
                pf.Use();
                reply($"OK water platuse left={pf.IsLeft}");
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
            reply($"OK water native={Native.MrpNative.Loaded} step={_step} target={GameClock.Now - Delay} late={Late} latePlatform={LatePlatform} platform={(_plat ? _pfOff : -1)}/{PlatLog.Count} shocks={Shocks} splashes={Splashes} queue={Queue.Count} sources={Sources.Count} inflows={Inflows.Count} inflowed={InflowTotal} gushes={Gushes} particles={Particles} active={_actN} volume={Volume()} spilled={SpilledTotal} fell={FellTotal} void={VoidTotal} flying={Flying.Count} links={FallTo.Count} doors={System.Numerics.BitOperations.PopCount(_doorBits)}/{_doorUnder?.Length ?? 0} digest={Digest():x8} stepMs={LastStepMs:0.000} maxStepMs={MaxStepMs:0.000} grid={_w}x{_h} {GameClock.Describe()}");
        });
    }
}
