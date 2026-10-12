using System;

namespace MoreRolesPlus.Terrain;

// 刻みで動く計算 (減圧・物・水・火) を 1 刻みずつ揃えて進める。刻み s では 減圧 → 物の確定 → 水 → 火 の順に進め、
// 物と水は同じ刻み s の後の気圧と流れを読む。別々に追いつかせると、1 フレームに進める刻みの数が端末ごとに違う時に
// 先の刻みの流れを読んでしまい、全員の結果がずれる
internal static class TerrainStep
{
    private const int MaxStepsPerFrame = 20;
    private static int _failedGen = -1;

    // 区間ごとの時間 (water prof): 前置き・減圧・物・水・火・壁の焼け
    internal static readonly long[] ProfTicks = new long[6];
    // 重かったフレームの内訳を記録に出す (water spikes on/off・閾値 ms)
    internal static double SpikeMs;
    private static readonly long[] _spike0 = new long[6 + 8 + 3];
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
        _profT = System.Diagnostics.Stopwatch.GetTimestamp();
        long t0 = _profT, steps0 = WaterSim.ProfSteps;
        if (SpikeMs > 0) SpikeBegin();
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
            Prof(5);
        }
        catch (Exception e)
        {
            // 同じ船では同じ所で落ち続けるので、1 回だけ記録してこの船では止める
            _failedGen = GameClock.ShipGen;
            Plugin.Logger.LogError($"[TerrainStep] {e}");
        }
        if (SpikeMs > 0) SpikeEnd(t0, steps0);
    }

    // 焼けが壁まで届いて適用された (届かなかった打は Apply の記録が前の打の物のまま)
    private static bool BurnApplied()
    {
        string w = FireSim.LastWallHit;
        return w != null && (w.StartsWith("blunt hit") || w.StartsWith("blunt breach"));
    }

    private static void SpikeBegin()
    {
        Array.Copy(ProfTicks, 0, _spike0, 0, 6);
        Array.Copy(WaterSim.ProfTicks, 0, _spike0, 6, 8);
        Array.Copy(WaterSim.LinkProf, 0, _spike0, 14, 3);
    }

    private static void SpikeEnd(long t0, long steps0)
    {
        double k = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * k;
        if (ms < SpikeMs) return;
        var sb = new System.Text.StringBuilder($"spike {ms:0.00}ms steps={WaterSim.ProfSteps - steps0} |");
        string[] a = { "pre", "decomp", "prop", "water", "fire", "burn" };
        for (int i = 0; i < 6; i++) sb.Append($" {a[i]}={(ProfTicks[i] - _spike0[i]) * k:0.00}");
        string[] w = { "events", "doors", "jets", "blow", "sub", "subfalls", "falls", "tidy" };
        sb.Append(" | w");
        for (int i = 0; i < 8; i++) sb.Append($" {w[i]}={(WaterSim.ProfTicks[i] - _spike0[6 + i]) * k:0.00}");
        sb.Append($" | link scan={(WaterSim.LinkProf[0] - _spike0[14]) * k:0.00} ray={(WaterSim.LinkProf[1] - _spike0[15]) * k:0.00} post={(WaterSim.LinkProf[2] - _spike0[16]) * k:0.00}");
        if ((ProfTicks[5] - _spike0[5]) * k > 0.5) sb.Append(" | burn ").Append(BurnApplied() ? TerrainDamage.MarksText() + ' ' + TerrainDamage.Breakdown() : $"why={FireSim.LastWallHit}");
        Bridge.TestBridge.Log(sb.ToString());
    }
}
