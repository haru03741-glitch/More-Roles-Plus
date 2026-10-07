using System;

namespace MoreRolesPlus.Terrain;

// 試合の刻み (毎秒 Hz 回)。ホストが壊した結果に押し、全員が同じ刻みで水の計算を始めるために使う。
// 刻み = 自分の時計 (試合の船が出てからの実時間) + Offset。最初のホストは Offset = 0。
// 客は届いた刻みから Offset を見積もる: 届いた時の自分の時計との差のうち一番大きいもの (= 通信の遅れが一番少なかった時)。
// 実際のホストの刻みより少し遅れた値になる (遅れる側に外れる方が安全)。
// 客がホストを引き継いだ時も同じ Offset のまま刻みを押すので、全員が最初のホストの数え方を使い続ける。
// 実時間 (Environment.TickCount64) で数えるので、アプリが裏に回っても止まらない。
//
// 試合の船の見張りもここ 1 か所でする (ShipGen / ShipAlive)。ShipStatus.Instance はフリープレイを抜けた後も
// 壊れた船を指したまま残ることがあり (実測: 抜けてロビーへ行っても水の計算が回り続けた)、参照の比較だけでは消えたことに気づけない。
// 生きているかの確かめ (Unity の bool) は重いので AliveCheckMs ごと。消えた船と同じ場所に次の船が作られても (アドレスの使い回し)
// 世代番号は進むので、各所は ShipGen を比べればよい
internal static class GameClock
{
    public const int Hz = 30;
    private const long AliveCheckMs = 250;

    private static IntPtr _ship, _dead;
    private static long _aliveCheckedMs;
    private static long _startMs;
    private static int _offset;
    private static bool _known;

    // 確認用: 届いた刻みの数と、Offset を一番最近広げた量・壊れた船の参照に気づいた回数
    internal static int Observed { get; private set; }
    internal static int LastRaise { get; private set; }
    internal static int StaleShips { get; private set; }

    // 船が替わる・消えるたびに 1 進む番号と、今の試合の船があるか (各所の片付けの合図)
    internal static int ShipGen { get; private set; }
    internal static bool ShipAlive => _ship != IntPtr.Zero;

    private static int Local
    {
        get
        {
            Ensure();
            return (int)((Environment.TickCount64 - _startMs) * Hz / 1000);
        }
    }

    // 今の試合の刻み (見積もり)
    public static int Now => Local + _offset;

    // 今の刻みの中の進み (0〜1)。刻みで動く物の絵を 1 刻み前と今の間で補間する
    public static float Frac => (int)((Environment.TickCount64 - _startMs) * Hz % 1000) / 1000f;

    // ホスト・一人: 結果に押す刻み
    public static ushort Stamp => unchecked((ushort)Now);

    // 客: 届いた結果の刻みで Offset を見積もり直す
    public static void Observe(ushort tick)
    {
        int local = Local;
        int full = _known ? Expand(tick) : tick;
        int off = full - local;
        Observed++;
        if (!_known) { _offset = off; _known = true; LastRaise = 0; return; }
        if (off > _offset) { LastRaise = off - _offset; _offset = off; }
    }

    // 16 ビットの刻みを、今の見積もりに一番近い 32 ビットの刻みへ戻す (36 分で一周するため)
    public static int Expand(ushort tick)
    {
        int now = Now;
        return now + (short)(tick - unchecked((ushort)now));
    }

    // 毎フレーム (船を見る各所より先に): 船が替わった瞬間から数え始める (最初に読まれた時からでは遅れる)
    public static void Tick() => Ensure();

    private static void Ensure()
    {
        var ship = ShipStatus.Instance;
        IntPtr sp = ReferenceEquals(ship, null) ? IntPtr.Zero : ship.Pointer;
        if (sp != IntPtr.Zero && (sp == _ship || sp == _dead))
        {
            long now = Environment.TickCount64;
            if (now - _aliveCheckedMs >= AliveCheckMs)
            {
                _aliveCheckedMs = now;
                bool alive = ship;
                if (sp == _ship && !alive) { _dead = sp; StaleShips++; }
                else if (sp == _dead && alive) _dead = IntPtr.Zero; // 消えた船と同じ場所に作られた次の船
            }
            if (sp == _dead) sp = IntPtr.Zero;
        }
        if (sp == _ship) return;
        _ship = sp;
        ShipGen++;
        _startMs = Environment.TickCount64;
        _offset = 0;
        _known = false;
        Observed = 0;
        LastRaise = 0;
    }

    internal static string Describe() => $"tick={Now} offset={_offset} known={_known} observed={Observed} raise={LastRaise} shipGen={ShipGen} alive={ShipAlive} stale={StaleShips}";
}
