// Based on https://github.com/Gurge44/EndlessHostRoles Modules/OptionHolder.cs (AutoSetFactionMinMaxSettings) と
// Patches/OnGameStartedPatch.cs (インポスターの数を最少〜最多から引く) (GPL-3.0)
using System;
using AmongUs.GameOptions;
using HarmonyLib;
using MoreRolesPlus.Dev;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles;

// 部屋の人数で陣営の数を決める。人数の帯 (「N 人から」・0 = 使わない) ごとにインポスターの最少〜最多・
// 第三陣営の最少〜最多・そのうちキル役の最多を持ち、今の人数以下でいちばん大きい帯を使う。
// どの帯にも届かない時と切ってある時は、本編のインポスター数と「配り方」の設定のまま。
// - インポスター: 開始の数えが始まった時にホストが帯の範囲から引いて本編の設定へ書き、全員へ同期する
// - 第三陣営: 配り方 (AssignPlan) が帯の最少〜最多を使う (0 = 出さない)
// - ロビーで人数が増減して帯が替わったら、ホストの手元に今の帯を出す
[Settings(Tab.General, "TeamSizes.Title", order: -97)]
public static class TeamSizes
{
    public static readonly BoolOpt Enabled = new("TeamSizes.Enabled", false);

    public readonly struct Band
    {
        public readonly int Players, MinImpostors, MaxImpostors, MinNeutrals, MaxNeutrals, MaxNeutralKillers;

        public Band(int players, int minImp, int maxImp, int minNeu, int maxNeu, int maxKill)
        {
            Players = players;
            MinImpostors = Math.Max(1, minImp);
            MaxImpostors = Math.Max(MinImpostors, maxImp);
            MinNeutrals = minNeu;
            MaxNeutrals = Math.Max(minNeu, maxNeu);
            MaxNeutralKillers = Math.Min(maxKill, MaxNeutrals);
        }
    }

    private static TeamSizeBandOpts[] _bands;

    // 帯の設定項目 (帯ごとの static class の項目を 1 つの表に。static の初期化が互いを待たないように最初に使う時に作る)
    private static TeamSizeBandOpts[] Bands => _bands ??= new[]
    {
        new TeamSizeBandOpts(TeamSizeBand1.Players, TeamSizeBand1.MinImpostors, TeamSizeBand1.MaxImpostors, TeamSizeBand1.MinNeutrals, TeamSizeBand1.MaxNeutrals, TeamSizeBand1.MaxNeutralKillers),
        new TeamSizeBandOpts(TeamSizeBand2.Players, TeamSizeBand2.MinImpostors, TeamSizeBand2.MaxImpostors, TeamSizeBand2.MinNeutrals, TeamSizeBand2.MaxNeutrals, TeamSizeBand2.MaxNeutralKillers),
        new TeamSizeBandOpts(TeamSizeBand3.Players, TeamSizeBand3.MinImpostors, TeamSizeBand3.MaxImpostors, TeamSizeBand3.MinNeutrals, TeamSizeBand3.MaxNeutrals, TeamSizeBand3.MaxNeutralKillers),
        new TeamSizeBandOpts(TeamSizeBand4.Players, TeamSizeBand4.MinImpostors, TeamSizeBand4.MaxImpostors, TeamSizeBand4.MinNeutrals, TeamSizeBand4.MaxNeutrals, TeamSizeBand4.MaxNeutralKillers),
        new TeamSizeBandOpts(TeamSizeBand5.Players, TeamSizeBand5.MinImpostors, TeamSizeBand5.MaxImpostors, TeamSizeBand5.MinNeutrals, TeamSizeBand5.MaxNeutrals, TeamSizeBand5.MaxNeutralKillers),
    };

    // 人数 players で使う帯 (切ってある・どの帯にも届かない時は null)
    public static Band? For(int players)
    {
        if (!Enabled) return null;
        TeamSizeBandOpts best = null;
        foreach (var b in Bands)
        {
            int p = b.Players.Value;
            if (p > 0 && p <= players && (best == null || p > best.Players.Value)) best = b;
        }
        return best?.ToBand();
    }

    // 役職を配る人数 (観戦の人と抜けた人を除く)
    public static int PlayerCount()
    {
        var gd = GameData.Instance;
        if (!gd) return 0;
        int n = 0;
        foreach (var p in gd.AllPlayers)
            if (p != null && !p.Disconnected && !Spectate.IsChosen(p.PlayerId)) n++;
        return n;
    }

    // ---- インポスター (ホスト・開始の数えが始まった時) ----

    private static readonly Random Rng = new();
    private static int _savedImpostors = -1; // 帯で書き換える前のインポスター数 (次のロビーで戻す)

    internal static void OnBegin()
    {
        var client = AmongUsClient.Instance;
        if (!client || !client.AmHost) return;
        int players = PlayerCount();
        if (For(players) is not { } band) return;
        var opts = GameOptionsManager.Instance?.CurrentGameOptions;
        if (opts == null) return;
        int max = Math.Max(1, ImpostorCap.MaxFor(ImpostorCap.RoomSize(opts)));
        int n = Math.Clamp(Rng.Next(band.MinImpostors, band.MaxImpostors + 1), 1, max);
        Plugin.Logger.LogInfo($"team sizes: players={players} band={band.Players} impostors {band.MinImpostors}-{band.MaxImpostors} => {n}");
        if (opts.NumImpostors == n) return;
        if (_savedImpostors < 0) _savedImpostors = opts.NumImpostors;
        opts.SetInt(Int32OptionNames.NumImpostors, n);
        if (GameManager.Instance) GameManager.Instance.LogicOptions.SyncOptions();
    }

    // ---- ロビーの知らせ (ホスト) ----

    private const long SettleMs = 2000; // 入った直後は人の情報が揃わないので少し待つ
    private static long _checkAt;
    private static int _shownBand = -1;
    private static bool _newLobby;

    // 人数か帯の設定が変わったかもしれない (ロビーに着いた・誰かが入った / 出た・設定画面を閉じた・ホストが替わった)
    internal static void Recheck(bool newLobby = false)
    {
        if (newLobby)
        {
            _shownBand = -1;
            _newLobby = true;
        }
        _checkAt = Environment.TickCount64 + SettleMs;
    }

    public static void Tick()
    {
        if (_checkAt == 0 || Environment.TickCount64 < _checkAt) return;
        _checkAt = 0;
        var client = AmongUsClient.Instance;
        if (!client || !client.AmHost || !LobbyBehaviour.Instance) return;
        // 試合から戻ったロビーでは、帯で書き換えたインポスター数を部屋の設定へ戻す (帯を切った後に残らないように)
        if (_newLobby)
        {
            _newLobby = false;
            RestoreImpostors();
        }
        if (!Enabled)
        {
            _shownBand = -1; // 入れ直したら出す
            return;
        }
        int players = PlayerCount();
        var band = For(players);
        // 帯が替わった時と、同じ帯の数を設定画面で変えた時に出す
        int key = band is { } k ? HashCode.Combine(k.Players, k.MinImpostors, k.MaxImpostors, k.MinNeutrals, k.MaxNeutrals, k.MaxNeutralKillers) : 0;
        if (key == _shownBand) return;
        _shownBand = key;
        string text = band is { } b
            ? new Text("TeamSizes.Applied").With("players", players).With("band", b.Players)
                .With("imp", Range(b.MinImpostors, b.MaxImpostors)).With("neu", Range(b.MinNeutrals, b.MaxNeutrals)).With("kill", b.MaxNeutralKillers)
            : new Text("TeamSizes.NoBand").With("players", players);
        DevChat.Notice(Vanilla.Chat, text);
        Plugin.Logger.LogInfo($"team sizes: {text}");
    }

    private static void RestoreImpostors()
    {
        int saved = _savedImpostors;
        _savedImpostors = -1;
        var opts = GameOptionsManager.Instance?.CurrentGameOptions;
        if (saved < 0 || opts == null || opts.NumImpostors == saved) return;
        opts.SetInt(Int32OptionNames.NumImpostors, Math.Clamp(saved, 1, Math.Max(1, ImpostorCap.MaxFor(ImpostorCap.RoomSize(opts)))));
        if (GameManager.Instance) GameManager.Instance.LogicOptions.SyncOptions();
        Plugin.Logger.LogInfo($"team sizes: impostors back to {opts.NumImpostors}");
    }

    private static string Range(int min, int max) => min == max ? min.ToString() : $"{min}-{max}";
}

internal sealed class TeamSizeBandOpts
{
    public readonly IntOpt Players, MinImpostors, MaxImpostors, MinNeutrals, MaxNeutrals, MaxNeutralKillers;

    public TeamSizeBandOpts(IntOpt players, IntOpt minImp, IntOpt maxImp, IntOpt minNeu, IntOpt maxNeu, IntOpt maxKill)
    {
        Players = players;
        MinImpostors = minImp;
        MaxImpostors = maxImp;
        MinNeutrals = minNeu;
        MaxNeutrals = maxNeu;
        MaxNeutralKillers = maxKill;
    }

    public TeamSizes.Band ToBand() => new(Players, MinImpostors, MaxImpostors, MinNeutrals, MaxNeutrals, MaxNeutralKillers);

    // 帯の項目の作り方 (5 つの帯で同じ幅)
    public static IntOpt PlayersOpt(int def) => new("TeamSizes.Players", def, 0, Crowded.MaxPlayers);
    public static IntOpt ImpostorOpt(string key, int def) => new(key, def, 1, ImpostorCap.CapFor(Crowded.MaxPlayers));
    public static IntOpt NeutralOpt(string key, int def) => new(key, def, 0, Crowded.MaxPlayers);
}

// 人数の帯。既定は 4 / 7 / 10 / 13 / 16 人から (全体の ON/OFF は既定で切ってある)
[Settings(Tab.General, "TeamSizes.Band1", order: -96)]
public static class TeamSizeBand1
{
    public static readonly IntOpt Players = TeamSizeBandOpts.PlayersOpt(4);
    public static readonly IntOpt MinImpostors = TeamSizeBandOpts.ImpostorOpt("TeamSizes.MinImpostors", 1);
    public static readonly IntOpt MaxImpostors = TeamSizeBandOpts.ImpostorOpt("TeamSizes.MaxImpostors", 1);
    public static readonly IntOpt MinNeutrals = TeamSizeBandOpts.NeutralOpt("TeamSizes.MinNeutrals", 0);
    public static readonly IntOpt MaxNeutrals = TeamSizeBandOpts.NeutralOpt("TeamSizes.MaxNeutrals", 1);
    public static readonly IntOpt MaxNeutralKillers = TeamSizeBandOpts.NeutralOpt("TeamSizes.MaxNeutralKillers", 0);
}

[Settings(Tab.General, "TeamSizes.Band2", order: -95)]
public static class TeamSizeBand2
{
    public static readonly IntOpt Players = TeamSizeBandOpts.PlayersOpt(7);
    public static readonly IntOpt MinImpostors = TeamSizeBandOpts.ImpostorOpt("TeamSizes.MinImpostors", 1);
    public static readonly IntOpt MaxImpostors = TeamSizeBandOpts.ImpostorOpt("TeamSizes.MaxImpostors", 2);
    public static readonly IntOpt MinNeutrals = TeamSizeBandOpts.NeutralOpt("TeamSizes.MinNeutrals", 0);
    public static readonly IntOpt MaxNeutrals = TeamSizeBandOpts.NeutralOpt("TeamSizes.MaxNeutrals", 1);
    public static readonly IntOpt MaxNeutralKillers = TeamSizeBandOpts.NeutralOpt("TeamSizes.MaxNeutralKillers", 1);
}

[Settings(Tab.General, "TeamSizes.Band3", order: -94)]
public static class TeamSizeBand3
{
    public static readonly IntOpt Players = TeamSizeBandOpts.PlayersOpt(10);
    public static readonly IntOpt MinImpostors = TeamSizeBandOpts.ImpostorOpt("TeamSizes.MinImpostors", 2);
    public static readonly IntOpt MaxImpostors = TeamSizeBandOpts.ImpostorOpt("TeamSizes.MaxImpostors", 2);
    public static readonly IntOpt MinNeutrals = TeamSizeBandOpts.NeutralOpt("TeamSizes.MinNeutrals", 1);
    public static readonly IntOpt MaxNeutrals = TeamSizeBandOpts.NeutralOpt("TeamSizes.MaxNeutrals", 2);
    public static readonly IntOpt MaxNeutralKillers = TeamSizeBandOpts.NeutralOpt("TeamSizes.MaxNeutralKillers", 1);
}

[Settings(Tab.General, "TeamSizes.Band4", order: -93)]
public static class TeamSizeBand4
{
    public static readonly IntOpt Players = TeamSizeBandOpts.PlayersOpt(13);
    public static readonly IntOpt MinImpostors = TeamSizeBandOpts.ImpostorOpt("TeamSizes.MinImpostors", 2);
    public static readonly IntOpt MaxImpostors = TeamSizeBandOpts.ImpostorOpt("TeamSizes.MaxImpostors", 3);
    public static readonly IntOpt MinNeutrals = TeamSizeBandOpts.NeutralOpt("TeamSizes.MinNeutrals", 1);
    public static readonly IntOpt MaxNeutrals = TeamSizeBandOpts.NeutralOpt("TeamSizes.MaxNeutrals", 2);
    public static readonly IntOpt MaxNeutralKillers = TeamSizeBandOpts.NeutralOpt("TeamSizes.MaxNeutralKillers", 1);
}

[Settings(Tab.General, "TeamSizes.Band5", order: -92)]
public static class TeamSizeBand5
{
    public static readonly IntOpt Players = TeamSizeBandOpts.PlayersOpt(16);
    public static readonly IntOpt MinImpostors = TeamSizeBandOpts.ImpostorOpt("TeamSizes.MinImpostors", 3);
    public static readonly IntOpt MaxImpostors = TeamSizeBandOpts.ImpostorOpt("TeamSizes.MaxImpostors", 4);
    public static readonly IntOpt MinNeutrals = TeamSizeBandOpts.NeutralOpt("TeamSizes.MinNeutrals", 2);
    public static readonly IntOpt MaxNeutrals = TeamSizeBandOpts.NeutralOpt("TeamSizes.MaxNeutrals", 3);
    public static readonly IntOpt MaxNeutralKillers = TeamSizeBandOpts.NeutralOpt("TeamSizes.MaxNeutralKillers", 2);
}

// 開始の数えが始まった時 (船ができる前・役職を配る前) にインポスターの数を決めて全員へ
[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.ReallyBegin))]
internal static class TeamSizesBeginPatch
{
    [HarmonyPriority(Priority.First)]
    public static void Prefix()
    {
        try { TeamSizes.OnBegin(); }
        catch (Exception e) { Plugin.Logger.LogError($"team sizes: {e}"); }
    }
}

[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
internal static class TeamSizesLobbyPatch
{
    public static void Postfix() => TeamSizes.Recheck(newLobby: true);
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerJoined))]
internal static class TeamSizesJoinPatch
{
    public static void Postfix() => TeamSizes.Recheck();
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerLeft))]
internal static class TeamSizesLeftPatch
{
    public static void Postfix() => TeamSizes.Recheck();
}
