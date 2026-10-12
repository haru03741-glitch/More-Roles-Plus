using System;

namespace MoreRolesPlus.Terrain;

// 刻みで動く計算 (減圧・物・水・火) を 1 刻みずつ揃えて進める。刻み s では 減圧 → 物の確定 → 水 → 火 の順に進め、
// 物と水は同じ刻み s の後の気圧と流れを読む。別々に追いつかせると、1 フレームに進める刻みの数が端末ごとに違う時に
// 先の刻みの流れを読んでしまい、全員の結果がずれる
internal static class TerrainStep
{
    private const int MaxStepsPerFrame = 20;
    private static int _failedGen = -1;

    // 区間ごとの時間 (water prof): 前置き・減圧・物・水・火
    internal static readonly long[] ProfTicks = new long[5];
    private static long _profT;
    private static void Prof(int i)
    {
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        ProfTicks[i] += t - _profT;
        _profT = t;
    }

    // 預けた水の刻みを終わらせて、その刻みの火を進める (刻みを外から進める・書き換える前にも呼ぶ)
    internal static void Settle()
    {
        int s = WaterSim.Settle();
        if (s >= 0) FireSim.AdvanceOne(s);
    }

    public static void Tick()
    {
        if (!Native.MrpNative.Loaded) return; // 地形は C++ の部品が前提
        _profT = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            Decompression.CheckShip();
            PropSim.CheckShip();
            WaterSim.Tick();
            FireSim.Tick();
            WaterSim.Spilled.Clear();
            WaterSim.Falls.Clear();
            WaterSim.GushFx.Clear();
            FireSim.Steam.Clear();
            FireSim.FlareFx.Clear();
            FireSim.Arcs.Clear();
            FireSim.Chars.Clear();
            Prof(0);
            if (_failedGen == GameClock.ShipGen) return;
            // 前のフレームで預けた水の刻みの後始末と、その刻みの火 (演出の記録は上で空にした後に積む)
            Settle();
            Prof(3);
            int target = GameClock.Now - WaterSim.Delay;
            // 物と水の刻みが違う間は 1 回に片方しか進まないので、回数は 2 倍まで (どれも 1 フレーム 20 刻みまで)
            for (int n = 0; n < MaxStepsPerFrame * 2; n++)
            {
                bool prop = PropSim.Drives, water = WaterSim.Running;
                int s = int.MaxValue;
                if (prop) s = PropSim.AuthStep;
                if (water && WaterSim.Step < s) s = WaterSim.Step;
                if (s >= target) break;
                Decompression.StepThrough(s);
                Prof(1);
                if (prop && PropSim.AuthStep == s) PropSim.AdvanceAuth();
                Prof(2);
                if (water && WaterSim.Step == s)
                {
                    // このフレームの最後の刻みは水の小刻みを預けて描画の裏で回す (火は次のフレームの頭で)
                    if (!WaterSim.AdvanceOne(s + 1 >= target && Native.MrpNative.Async)) { Prof(3); break; }
                    Prof(3);
                    FireSim.AdvanceOne(s);
                }
                Prof(4);
            }
            FireSim.IssueWallBurns();
            Prof(4);
        }
        catch (Exception e)
        {
            // 同じ船では同じ所で落ち続けるので、1 回だけ記録してこの船では止める
            _failedGen = GameClock.ShipGen;
            Plugin.Logger.LogError($"[TerrainStep] {e}");
        }
    }
}
