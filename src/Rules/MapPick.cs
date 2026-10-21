// Based on https://github.com/tukasa0001/TownOfHost and https://github.com/EnhancedNetwork/TownofHost-Enhanced Patches/GameStartManagerPatch.cs (GPL-3.0)
// via EndlessHostRoles (GPL-3.0)
using System;
using AmongUs.GameOptions;
using HarmonyLib;
using MoreRolesPlus.Roles;

namespace MoreRolesPlus.Rules;

// ランダムマップとマップごとの歩く速さ (ホスト・開始の数えが始まった時)。
// 本編の設定のマップと移動速度を書き換えて全員へ配り、開始を取りやめた時と次のロビーで元へ戻す
internal static class MapPick
{
    private static readonly Random Rng = new();
    private static int _savedMap = -1;
    private static float _savedSpeed = -1f;

    private static IGameOptions Options => GameOptionsManager.Instance?.CurrentGameOptions;

    internal static void OnBegin()
    {
        var client = AmongUsClient.Instance;
        var opts = Options;
        if (!client || !client.AmHost || opts == null) return;
        bool changed = false;
        if (MapRules.RandomMap)
        {
            int map = Pick(TeamSizes.PlayerCount());
            if (map >= 0 && map != opts.MapId)
            {
                if (_savedMap < 0) _savedMap = opts.MapId;
                opts.SetByte(ByteOptionNames.MapId, (byte)map);
                changed = true;
            }
            Plugin.Logger.LogInfo($"map rules: random map => {map}");
        }
        if (MapRules.MapSpeed && MapRules.SpeedOf(opts.MapId) is { } speed)
        {
            float now = opts.GetFloat(FloatOptionNames.PlayerSpeedMod);
            if (Math.Abs(now - speed) > 0.001f)
            {
                if (_savedSpeed < 0f) _savedSpeed = now;
                opts.SetFloat(FloatOptionNames.PlayerSpeedMod, speed);
                changed = true;
            }
        }
        if (changed && GameManager.Instance) GameManager.Instance.LogicOptions.SyncOptions();
    }

    // 出やすさの重みで 1 つ引く (人数の下限に届かないマップと重み 0 は除く)。どれも無ければ -1
    private static int Pick(int players)
    {
        int total = 0;
        foreach (int id in MapRules.MapIds) total += Weight(id, players);
        if (total <= 0) return -1;
        int r = Rng.Next(total);
        foreach (int id in MapRules.MapIds)
        {
            r -= Weight(id, players);
            if (r < 0) return id;
        }
        return -1;
    }

    private static int Weight(int id, int players)
    {
        var c = MapRules.ChanceOf(id);
        if (c == null) return 0;
        int min = MapRules.MinPlayersOf(id);
        return min > 0 && players < min ? 0 : c.Value;
    }

    // 開始を取りやめた・次のロビー: 書き換えたマップと速さを戻す
    internal static void Restore()
    {
        var client = AmongUsClient.Instance;
        var opts = Options;
        if (!client || !client.AmHost || opts == null) { _savedMap = -1; _savedSpeed = -1f; return; }
        bool changed = false;
        if (_savedMap >= 0)
        {
            opts.SetByte(ByteOptionNames.MapId, (byte)_savedMap);
            changed = true;
        }
        if (_savedSpeed >= 0f)
        {
            opts.SetFloat(FloatOptionNames.PlayerSpeedMod, _savedSpeed);
            changed = true;
        }
        _savedMap = -1;
        _savedSpeed = -1f;
        if (changed && GameManager.Instance) GameManager.Instance.LogicOptions.SyncOptions();
    }
}

[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.ReallyBegin))]
internal static class MapPickBeginPatch
{
    public static void Prefix()
    {
        try { MapPick.OnBegin(); }
        catch (Exception e) { Plugin.Logger.LogError($"map pick: {e}"); }
    }
}

[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.ResetStartState))]
internal static class MapPickCancelPatch
{
    public static void Prefix(GameStartManager __instance, out bool __state) =>
        __state = __instance.startState == GameStartManager.StartingStates.Countdown;

    public static void Postfix(bool __state)
    {
        if (!__state || ShipStatus.Instance) return;
        try { MapPick.Restore(); }
        catch (Exception e) { Plugin.Logger.LogError($"map pick: {e}"); }
    }
}

[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
internal static class MapPickLobbyPatch
{
    public static void Postfix()
    {
        try { MapPick.Restore(); }
        catch (Exception e) { Plugin.Logger.LogError($"map pick: {e}"); }
    }
}
