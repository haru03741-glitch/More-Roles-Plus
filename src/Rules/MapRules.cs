// Option set based on https://github.com/Gurge44/EndlessHostRoles Modules/OptionHolder.cs (GPL-3.0)
using System;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Rules;

// マップのルール。本編の「ゲーム設定」タブのマップ選びの下に並べる (GameTabRows)。MRP のタブには並べない。
// 親の項目が OFF の間は子の項目を出さない
[Settings(Tab.General, "MapRules.Title", order: -59)]
public static class MapRules
{
    // 本編の行と並ぶので、値は本編の書き方 (「25.0秒」「1.25x」)
    internal static string Seconds(float v) => VanillaGame.Format(StringNames.GameKillCooldown, v);
    private static string Times(float v) => VanillaGame.Format(StringNames.GamePlayerSpeed, v);

    // ランダムマップ (マップごとの出やすさ・Airship と Fungle は人数の下限)
    public static readonly BoolOpt RandomMap = new("MapRules.RandomMap", false) { OffTab = true };
    public static readonly IntOpt SkeldChance = Chance("MapRules.SkeldChance");
    public static readonly IntOpt MiraChance = Chance("MapRules.MiraChance");
    public static readonly IntOpt PolusChance = Chance("MapRules.PolusChance");
    public static readonly IntOpt AirshipChance = Chance("MapRules.AirshipChance");
    public static readonly IntOpt FungleChance = Chance("MapRules.FungleChance");
    public static readonly IntOpt AirshipMinPlayers = new("MapRules.AirshipMinPlayers", 0, 0, 15) { OffTab = true, Parent = RandomMap };
    public static readonly IntOpt FungleMinPlayers = new("MapRules.FungleMinPlayers", 0, 0, 15) { OffTab = true, Parent = RandomMap };

    // マップごとの歩く速さ (本編の「プレイヤーの移動速度」を試合の間だけ置き換える)
    public static readonly BoolOpt MapSpeed = new("MapRules.MapSpeed", false) { OffTab = true };
    public static readonly FloatOpt SkeldSpeed = Speed("MapRules.SkeldSpeed");
    public static readonly FloatOpt MiraSpeed = Speed("MapRules.MiraSpeed");
    public static readonly FloatOpt PolusSpeed = Speed("MapRules.PolusSpeed");
    public static readonly FloatOpt AirshipSpeed = Speed("MapRules.AirshipSpeed");
    public static readonly FloatOpt FungleSpeed = Speed("MapRules.FungleSpeed");

    // 湧く場所をばらばらにする (試合の始めと会議の後)
    public static readonly BoolOpt RandomSpawn = new("MapRules.RandomSpawn", false) { OffTab = true };
    public static readonly BoolOpt AirshipAllRooms = new("MapRules.AirshipAllRooms", false) { OffTab = true, Parent = RandomSpawn };

    // マップごとにキルのクールダウン時間を足す
    public static readonly BoolOpt ExtraKillCooldown = new("MapRules.ExtraKillCooldown", false) { OffTab = true };
    public static readonly FloatOpt PolusKillCooldown = Extra("MapRules.PolusKillCooldown");
    public static readonly FloatOpt AirshipKillCooldown = Extra("MapRules.AirshipKillCooldown");
    public static readonly FloatOpt FungleKillCooldown = Extra("MapRules.FungleKillCooldown");

    // 除染室の時間 (Mira HQ・Polus)
    public static readonly BoolOpt Decon = new("MapRules.Decon", false) { OffTab = true };
    public static readonly FloatOpt MiraDeconTime = Decontam("MapRules.MiraDeconTime");
    public static readonly FloatOpt MiraDeconDoorTime = Decontam("MapRules.MiraDeconDoorTime");
    public static readonly FloatOpt PolusDeconTime = Decontam("MapRules.PolusDeconTime");
    public static readonly FloatOpt PolusDeconDoorTime = Decontam("MapRules.PolusDeconDoorTime");

    // 会議のたびにドアを開け閉めし直す (Polus・Airship・Fungle)
    public static readonly BoolOpt ResetDoors = new("MapRules.ResetDoors", false) { OffTab = true };
    public static readonly ChoiceOpt ResetDoorsMode = new("MapRules.ResetDoorsMode", 0,
        new Text("MapRules.DoorsAllOpen"), new Text("MapRules.DoorsAllClosed"), new Text("MapRules.DoorsRandom")) { OffTab = true, Parent = ResetDoors };

    // Airship・Fungle の仕掛け
    public static readonly BoolOpt AirshipVariableElectrical = new("MapRules.AirshipVariableElectrical", false) { OffTab = true };
    public static readonly BoolOpt AirshipNoPlatform = new("MapRules.AirshipNoPlatform", false) { OffTab = true };
    public static readonly BoolOpt FungleNoSpores = new("MapRules.FungleNoSpores", false) { OffTab = true };

    // Fungle のジップライン
    public static readonly BoolOpt ZiplineRules = new("MapRules.ZiplineRules", false) { OffTab = true };
    public static readonly BoolOpt ZiplineNoDown = new("MapRules.ZiplineNoDown", false) { OffTab = true, Parent = ZiplineRules };
    public static readonly BoolOpt ZiplineNoUp = new("MapRules.ZiplineNoUp", false) { OffTab = true, Parent = ZiplineRules };
    public static readonly BoolOpt ZiplineNoCrew = new("MapRules.ZiplineNoCrew", false) { OffTab = true, Parent = ZiplineRules };
    public static readonly BoolOpt ZiplineNoImpostors = new("MapRules.ZiplineNoImpostors", false) { OffTab = true, Parent = ZiplineRules };
    public static readonly BoolOpt ZiplineNoNeutrals = new("MapRules.ZiplineNoNeutrals", false) { OffTab = true, Parent = ZiplineRules };

    // はしごを降りると落ちて死ぬことがある (Airship・Fungle)
    public static readonly BoolOpt LadderDeath = new("MapRules.LadderDeath", false) { OffTab = true };
    public static readonly IntOpt LadderDeathChance = new("MapRules.LadderDeathChance", 10, 5, 100, 5, "%") { OffTab = true, Parent = LadderDeath };

    // ゲーム設定のタブに並べる順
    public static Opt[] Rows => _rows ??= new Opt[]
    {
        RandomMap, SkeldChance, MiraChance, PolusChance, AirshipChance, FungleChance, AirshipMinPlayers, FungleMinPlayers,
        MapSpeed, SkeldSpeed, MiraSpeed, PolusSpeed, AirshipSpeed, FungleSpeed,
        RandomSpawn, AirshipAllRooms,
        ExtraKillCooldown, PolusKillCooldown, AirshipKillCooldown, FungleKillCooldown,
        Decon, MiraDeconTime, MiraDeconDoorTime, PolusDeconTime, PolusDeconDoorTime,
        ResetDoors, ResetDoorsMode,
        AirshipVariableElectrical, AirshipNoPlatform, FungleNoSpores,
        ZiplineRules, ZiplineNoDown, ZiplineNoUp, ZiplineNoCrew, ZiplineNoImpostors, ZiplineNoNeutrals,
        LadderDeath, LadderDeathChance,
    };
    private static Opt[] _rows;

    private static IntOpt Chance(string key) => new(key, 100, 0, 100, 10, "%") { OffTab = true, Parent = RandomMap };
    private static FloatOpt Speed(string key) => new(key, 1f, 0.5f, 3f, 0.25f) { OffTab = true, Parent = MapSpeed, Format = Times };
    private static FloatOpt Extra(string key) => new(key, 0f, 0f, 30f, 2.5f) { OffTab = true, Parent = ExtraKillCooldown, Format = Seconds };
    private static FloatOpt Decontam(string key) => new(key, 3f, 0.5f, 10f, 0.5f) { OffTab = true, Parent = Decon, Format = Seconds };

    // 今の試合のマップ (本編の設定のマップ番号: 0 Skeld・1 Mira・2 Polus・3 dlekS・4 Airship・5 Fungle)。
    // フリープレイは本編の設定でなく、選んだマップの番号を別に持つ
    internal static int MapId
    {
        get
        {
            if (TutorialManager.InstanceExists && AmongUsClient.Instance) return AmongUsClient.Instance.TutorialMapId;
            var o = GameOptionsManager.Instance?.CurrentGameOptions;
            return o != null ? o.MapId : 0;
        }
    }

    // 今のマップで足すキルのクールダウン時間
    // 船ができた時のマップ (毎フレーム読む所で本編の設定を読まない)
    internal static int ShipMap = -1;

    internal static float ExtraKillSeconds()
    {
        if (!ExtraKillCooldown || !Terrain.GameClock.ShipAlive) return 0f;
        return ShipMap switch
        {
            2 => PolusKillCooldown,
            4 => AirshipKillCooldown,
            5 => FungleKillCooldown,
            _ => 0f,
        };
    }

    internal static FloatOpt SpeedOf(int mapId) => mapId switch
    {
        0 or 3 => SkeldSpeed,
        1 => MiraSpeed,
        2 => PolusSpeed,
        4 => AirshipSpeed,
        5 => FungleSpeed,
        _ => null,
    };

    internal static IntOpt ChanceOf(int mapId) => mapId switch
    {
        0 => SkeldChance,
        1 => MiraChance,
        2 => PolusChance,
        4 => AirshipChance,
        5 => FungleChance,
        _ => null,
    };

    internal static int MinPlayersOf(int mapId) => mapId switch
    {
        4 => AirshipMinPlayers,
        5 => FungleMinPlayers,
        _ => 0,
    };

    internal static readonly int[] MapIds = { 0, 1, 2, 4, 5 };

    internal static bool Chance(Random rng, int percent) => percent > 0 && (percent >= 100 || rng.Next(100) < percent);
}
