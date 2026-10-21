// Based on https://github.com/tukasa0001/TownOfHost Patches/TaskAssignPatch.cs (GPL-3.0) via EndlessHostRoles (GPL-3.0)
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Rules;

// タスクの無効化。マップごとに、出さないタスクの種類を選ぶ (同じタスクでもマップごとに別に切れる)。
// 行は本編の「ゲーム設定」タブの終わりに並べる (GameTabRows)。マップの項目が OFF の間はそのマップのタスクの行を出さない
[Settings(Tab.General, "TaskFilter.Title", order: -58)]
public static class TaskFilter
{
    // マップごとのタスクの種類 (本編の船の表の共通 / 長い / 短い を合わせた物)
    private static readonly (int MapId, string Key, TaskTypes[] Tasks)[] Maps =
    {
        (0, "Skeld", new[]
        {
            TaskTypes.FixWiring, TaskTypes.SwipeCard,
            TaskTypes.AlignEngineOutput, TaskTypes.ClearAsteroids, TaskTypes.EmptyGarbage, TaskTypes.FuelEngines, TaskTypes.InspectSample,
            TaskTypes.StartReactor, TaskTypes.SubmitScan, TaskTypes.UploadData,
            TaskTypes.CalibrateDistributor, TaskTypes.ChartCourse, TaskTypes.CleanO2Filter, TaskTypes.DivertPower, TaskTypes.EmptyChute,
            TaskTypes.PrimeShields, TaskTypes.StabilizeSteering, TaskTypes.UnlockManifolds, TaskTypes.VentCleaning,
        }),
        (1, "Mira", new[]
        {
            TaskTypes.EnterIdCode, TaskTypes.FixWiring,
            TaskTypes.ClearAsteroids, TaskTypes.EmptyGarbage, TaskTypes.FuelEngines, TaskTypes.StartReactor, TaskTypes.SubmitScan, TaskTypes.WaterPlants,
            TaskTypes.AssembleArtifact, TaskTypes.BuyBeverage, TaskTypes.ChartCourse, TaskTypes.CleanO2Filter, TaskTypes.DivertPower,
            TaskTypes.MeasureWeather, TaskTypes.PrimeShields, TaskTypes.ProcessData, TaskTypes.RunDiagnostics, TaskTypes.SortSamples,
            TaskTypes.UnlockManifolds, TaskTypes.VentCleaning,
        }),
        (2, "Polus", new[]
        {
            TaskTypes.InsertKeys, TaskTypes.ScanBoardingPass, TaskTypes.SwipeCard, TaskTypes.FixWiring,
            TaskTypes.AlignTelescope, TaskTypes.FillCanisters, TaskTypes.FuelEngines, TaskTypes.InspectSample, TaskTypes.OpenWaterways,
            TaskTypes.RebootWifi, TaskTypes.ReplaceWaterJug, TaskTypes.StartReactor, TaskTypes.SubmitScan, TaskTypes.UploadData,
            TaskTypes.ChartCourse, TaskTypes.ClearAsteroids, TaskTypes.FixWeatherNode, TaskTypes.MonitorOxygen, TaskTypes.RecordTemperature,
            TaskTypes.RepairDrill, TaskTypes.StoreArtifacts, TaskTypes.UnlockManifolds, TaskTypes.EmptyGarbage,
        }),
        (4, "Airship", new[]
        {
            TaskTypes.EnterIdCode, TaskTypes.FixWiring,
            TaskTypes.DevelopPhotos, TaskTypes.EmptyGarbage, TaskTypes.FuelEngines, TaskTypes.RewindTapes, TaskTypes.StartFans,
            TaskTypes.CalibrateDistributor, TaskTypes.CleanToilet, TaskTypes.Decontaminate, TaskTypes.DivertPower, TaskTypes.DressMannequin,
            TaskTypes.MakeBurger, TaskTypes.PickUpTowels, TaskTypes.PolishRuby, TaskTypes.PutAwayPistols, TaskTypes.PutAwayRifles,
            TaskTypes.ResetBreakers, TaskTypes.SortRecords, TaskTypes.StabilizeSteering, TaskTypes.UnlockSafe, TaskTypes.UploadData,
            TaskTypes.FixShower, TaskTypes.VentCleaning,
        }),
        (5, "Fungle", new[]
        {
            TaskTypes.CollectSamples, TaskTypes.EnterIdCode, TaskTypes.ReplaceParts, TaskTypes.RoastMarshmallow,
            TaskTypes.CatchFish, TaskTypes.CollectVegetables, TaskTypes.ExtractFuel, TaskTypes.HelpCritter, TaskTypes.HoistSupplies,
            TaskTypes.PolishGem, TaskTypes.MineOres, TaskTypes.ReplaceWaterJug, TaskTypes.WaterPlants,
            TaskTypes.AssembleArtifact, TaskTypes.BuildSandcastle, TaskTypes.CollectShells, TaskTypes.CrankGenerator, TaskTypes.EmptyGarbage,
            TaskTypes.FixAntenna, TaskTypes.FixWiring, TaskTypes.LiftWeights, TaskTypes.MonitorMushroom, TaskTypes.PlayVideogame,
            TaskTypes.RecordTemperature, TaskTypes.TestFrisbee, TaskTypes.TuneRadio,
        }),
    };

    // マップの項目 (ON で、そのマップのタスクの行が開く) と、その後ろにそのマップのタスクの項目 (ON = 出さない)
    public static readonly OptList Off = new();

    private static readonly Dictionary<int, BoolOpt> MapOpt = new();
    private static readonly Dictionary<(int, TaskTypes), BoolOpt> TaskOpt = new();

    public static Opt[] Rows { get; }

    static TaskFilter()
    {
        var rows = new List<Opt>();
        foreach (var (mapId, key, tasks) in Maps)
        {
            var m = Off.Add(key, new BoolOpt("TaskFilter." + key, false) { OffTab = true });
            MapOpt[mapId] = m;
            rows.Add(m);
            foreach (var t in tasks)
            {
                var type = t;
                var o = Off.Add(key + "." + type, new BoolOpt(Text.Live(() => TaskName(type)), false) { OffTab = true, Parent = m });
                TaskOpt[(mapId, type)] = o;
                rows.Add(o);
            }
        }
        Rows = rows.ToArray();
    }

    private static string TaskName(TaskTypes t) =>
        TranslationController.InstanceExists ? TranslationController.Instance.GetString(t) : t.ToString();

    // 今のマップでそのタスクを出さないか
    internal static bool IsOff(int mapId, TaskTypes t)
    {
        if (mapId == 3) mapId = 0; // dlekS は Skeld と同じ表
        return MapOpt.TryGetValue(mapId, out var m) && m.Value && TaskOpt.TryGetValue((mapId, t), out var o) && o.Value;
    }

    internal static bool AnyOn()
    {
        foreach (var m in MapOpt.Values) if (m.Value) return true;
        return false;
    }
}

// ホストがタスクを配る時、本編が引く残りのタスクから無効にした物を抜く。
// 全部抜くと本編が引く物が無くなるので、その時は抜かない
[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.AddTasksFromList))]
internal static class TaskFilterPatch
{
    public static void Prefix([HarmonyArgument(4)] Il2CppSystem.Collections.Generic.List<NormalPlayerTask> unusedTasks)
    {
        if (unusedTasks == null || !AmongUsClient.Instance.AmHost || !TaskFilter.AnyOn()) return;
        int mapId = MapRules.MapId;
        var keep = new List<NormalPlayerTask>();
        int removed = 0;
        for (int i = 0; i < unusedTasks.Count; i++)
        {
            var t = unusedTasks[i];
            if (t && TaskFilter.IsOff(mapId, t.TaskType)) removed++;
            else keep.Add(t);
        }
        if (removed == 0) return;
        if (keep.Count == 0)
        {
            Plugin.Logger.LogWarning("task filter: every task of this kind is off — left as is");
            return;
        }
        unusedTasks.Clear();
        foreach (var t in keep) unusedTasks.Add(t);
        Plugin.Logger.LogInfo($"task filter: map {mapId} removed {removed}, left {keep.Count}");
    }
}
