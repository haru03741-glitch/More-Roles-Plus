using System;

namespace MoreRolesPlus.Terrain;

// 刻みで動く計算 (減圧・物・水) を 1 刻みずつ揃えて進める。刻み s では 減圧 → 物の確定 → 水 の順に進め、
// 物と水は同じ刻み s の後の気圧と流れを読む。別々に追いつかせると、1 フレームに進める刻みの数が端末ごとに違う時に
// 先の刻みの流れを読んでしまい、全員の結果がずれる
internal static class TerrainStep
{
    private const int MaxStepsPerFrame = 20;
    private static int _failedGen = -1;

    public static void Tick()
    {
        try
        {
            Decompression.CheckShip();
            PropSim.CheckShip();
            WaterSim.Tick();
            WaterSim.Spilled.Clear();
            WaterSim.Falls.Clear();
            if (_failedGen == GameClock.ShipGen) return;
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
                if (prop && PropSim.AuthStep == s) PropSim.AdvanceAuth();
                if (water && WaterSim.Step == s) WaterSim.AdvanceOne();
            }
        }
        catch (Exception e)
        {
            // 同じ船では同じ所で落ち続けるので、1 回だけ記録してこの船では止める
            _failedGen = GameClock.ShipGen;
            Plugin.Logger.LogError($"[TerrainStep] {e}");
        }
    }
}
