using HarmonyLib;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Rules;

// Based on https://github.com/tukasa0001/TownOfHost Patches/RandomSpawnPatch.cs (GPL-3.0) via https://github.com/Gurge44/EndlessHostRoles (GPL-3.0)
// 湧く場所をばらばらにする (設定は MapRules.RandomSpawn)。全員が MRP を入れているので、各端末が自分だけを
// MRP の TP (Teleport.Self) で部屋の 1 つへ飛ばす (ホストが全員へ本編の TP を一斉に送らない)。
// - 試合の始め (イントロが終わった後) と会議の後。生きている人だけ
// - Airship は本編の湧き場所選びがあるので、選んで降りた後に飛ばす (全部の部屋か、本編の湧き場所の 6 つから)
internal static class RandomSpawn
{
    private static readonly System.Random Rng = new();

    // 部屋の真ん中あたりの床 (本編の向きの座標)
    private static readonly Vector2[] Skeld =
    {
        new(-1.0f, 3.0f), new(9.3f, 1.0f), new(6.5f, -3.8f), new(16.5f, -4.8f), new(9.3f, -12.3f), new(4.0f, -15.5f), new(-1.5f, -15.5f),
        new(4.5f, -7.9f), new(-7.5f, -8.8f), new(-17.0f, -13.5f), new(-17.0f, -1.3f), new(-13.5f, -5.5f), new(-20.5f, -5.5f), new(-9.0f, -4.0f),
    };

    private static readonly Vector2[] Mira =
    {
        new(25.5f, 2.0f), new(24.0f, -2.0f), new(19.5f, 4.0f), new(17.8f, 11.5f), new(15.3f, 3.8f), new(15.5f, -0.5f), new(9.0f, 1.0f),
        new(6.1f, 6.0f), new(9.5f, 12.0f), new(2.5f, 10.5f), new(-4.5f, 2.0f), new(21.0f, 17.5f), new(15.0f, 19.0f), new(17.8f, 23.0f),
    };

    private static readonly Vector2[] Polus =
    {
        new(19.5f, -18.0f), new(26.0f, -17.0f), new(24.0f, -22.5f), new(12.5f, -16.0f), new(12.0f, -23.5f), new(2.3f, -24.0f), new(2.0f, -17.5f),
        new(9.5f, -12.5f), new(3.0f, -12.0f), new(16.7f, -3.0f), new(20.5f, -12.0f), new(26.7f, -8.5f), new(36.5f, -7.5f), new(34.0f, -10.0f),
        new(36.5f, -22.0f),
    };

    // 先頭の 6 つは本編の湧き場所選びに出る部屋
    private static readonly Vector2[] Airship =
    {
        new(-0.7f, 8.5f), new(-0.7f, -1.0f), new(-7.0f, -11.5f), new(33.5f, -1.5f), new(20.0f, 10.5f), new(15.5f, 0.0f),
        new(6.3f, 2.5f), new(17.1f, 14.9f), new(12.0f, 8.5f), new(-8.9f, 12.2f), new(-13.3f, 1.3f), new(-23.5f, -1.6f), new(-10.3f, -5.9f),
        new(-13.7f, -12.6f), new(5.8f, -10.8f), new(16.3f, -8.8f), new(29.0f, -6.2f), new(30.9f, 6.8f), new(21.2f, -0.8f),
    };
    private const int AirshipVanillaSpots = 6;

    private static readonly Vector2[] Fungle =
    {
        new(-9.8f, 3.4f), new(-7.8f, 10.6f), new(-16.4f, 7.3f), new(-15.6f, -1.8f), new(-22.8f, -0.6f), new(-15.5f, -7.5f), new(-23.1f, -7.0f),
        new(1.7f, 4.4f), new(-3.0f, -2.6f), new(2.6f, -1.3f), new(-4.3f, -8.6f), new(0.8f, -11.7f), new(9.3f, -9.8f), new(22.3f, -7.0f),
        new(9.5f, 1.2f), new(12.6f, 9.8f), new(22.4f, 3.4f), new(22.2f, 13.7f),
    };

    // 自分を部屋の 1 つへ飛ばす
    internal static void Go(string why)
    {
        if (!MapRules.RandomSpawn) return;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.Data == null || lp.Data.IsDead || !ShipStatus.Instance) return;
        int map = MapRules.MapId;
        var spots = map switch
        {
            0 or 3 => Skeld,
            1 => Mira,
            2 => Polus,
            4 => Airship,
            5 => Fungle,
            _ => null,
        };
        if (spots == null) return;
        int n = map == 4 && !MapRules.AirshipAllRooms ? AirshipVanillaSpots : spots.Length;
        var p = spots[Rng.Next(n)];
        // dlekS は Skeld を左右に返した船・MRP の反転はさらに左右に返す
        bool flip = (map == 3) != MapFlip.Flipped;
        var to = new Vector2(flip ? -p.x : p.x, p.y);
        Teleport.Self(to);
        Plugin.Logger.LogInfo($"random spawn ({why}): ({to.x:0.#}, {to.y:0.#})");
    }

    // ---- 試合の始め (Airship 以外。会議の後は RulesHooks から) ----

    private static int _shipGen = -1;

    internal static void Tick()
    {
        if (!MapRules.RandomSpawn || _shipGen == Terrain.GameClock.ShipGen || !Terrain.GameClock.ShipAlive || !ShipStatus.Instance) return;
        if (MapRules.MapId == 4) { _shipGen = Terrain.GameClock.ShipGen; return; } // Airship は湧き場所選びの後
        // イントロが終わって動けるようになってから
        var lp = PlayerControl.LocalPlayer;
        if (IntroCutscene.Instance || !lp || !lp.CanMove) return;
        _shipGen = Terrain.GameClock.ShipGen;
        Go("start");
    }

    internal static void AfterMeeting()
    {
        if (MapRules.MapId != 4) Go("meeting");
    }

    // ---- Airship: 湧き場所選びが閉じて動けるようになった後 ----

    private static bool _spawnIn;

    internal static void OnSpawnIn() => _spawnIn = MapRules.RandomSpawn;

    internal static void TickAirship()
    {
        if (!_spawnIn) return;
        var lp = PlayerControl.LocalPlayer;
        if (!ShipStatus.Instance || !lp) { _spawnIn = false; return; }
        if (Minigame.Instance || !lp.CanMove) return;
        _spawnIn = false;
        Go("airship");
    }
}

// Airship: 湧き場所選びが開いた (閉じ方は自動で選ばれる時もあるので、閉じた後は Tick で見る)
[HarmonyPatch(typeof(SpawnInMinigame), nameof(SpawnInMinigame.Begin))]
internal static class RandomSpawnAirshipPatch
{
    public static void Postfix() => RandomSpawn.OnSpawnIn();
}
