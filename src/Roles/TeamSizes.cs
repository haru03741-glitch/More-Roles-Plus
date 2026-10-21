// Based on https://github.com/Gurge44/EndlessHostRoles Modules/OptionHolder.cs (AutoSetFactionMinMaxSettings) と
// Patches/OnGameStartedPatch.cs (インポスターの数を最少〜最多から引く) (GPL-3.0)
using System;
using AmongUs.GameOptions;
using HarmonyLib;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles;

// インポスターの数の最少。最多は本編のインポスター数 (NumImpostors) をそのまま使い、開始の時にこの範囲から引く。
// 行は本編の「ゲーム設定」タブのインポスター数の行の上に並べる (GameTabRows)
[Settings(Tab.General, "ImpostorRange.Title", order: -99)]
public static class ImpostorRange
{
    public static readonly IntOpt Min = new("ImpostorRange.Min", 1, 1, ImpostorCap.CapFor(Crowded.MaxPlayers)) { OffTab = true };
    public static readonly Text MaxLabel = new("ImpostorRange.Max");
}

// 部屋の人数で陣営の数を決める。人数の帯 (「N 人から」・0 = 使わない) ごとにインポスターの最少〜最多・
// 第三陣営の最少〜最多・そのうちキル役の最多を持ち、今の人数以下でいちばん大きい帯を使う。
// 使っている帯の値はゲーム設定の行 (インポスターの最少・本編のインポスター数・第三陣営の数) へ写し、
// ゲーム設定の行を変えた時は使っている帯へ書き戻す (どこを見ても同じ数になるように)。
// - インポスター: 開始の数えが始まった時にホストが最少〜最多から引いて本編の設定へ書き、全員へ同期する
// - 第三陣営: 配り方 (AssignPlan) が最少〜最多を使う (0 = 出さない)
[Settings(Tab.General, "TeamSizes.Title", order: -96)]
public static class TeamSizes
{
    public static readonly BoolOpt Enabled = new("TeamSizes.Enabled", false) { OffTab = true };

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
    internal static TeamSizeBandOpts[] Bands => _bands ??= new[]
    {
        new TeamSizeBandOpts(TeamSizeBand1.Players, TeamSizeBand1.MinImpostors, TeamSizeBand1.MaxImpostors, TeamSizeBand1.MinNeutrals, TeamSizeBand1.MaxNeutrals, TeamSizeBand1.MaxNeutralKillers),
        new TeamSizeBandOpts(TeamSizeBand2.Players, TeamSizeBand2.MinImpostors, TeamSizeBand2.MaxImpostors, TeamSizeBand2.MinNeutrals, TeamSizeBand2.MaxNeutrals, TeamSizeBand2.MaxNeutralKillers),
        new TeamSizeBandOpts(TeamSizeBand3.Players, TeamSizeBand3.MinImpostors, TeamSizeBand3.MaxImpostors, TeamSizeBand3.MinNeutrals, TeamSizeBand3.MaxNeutrals, TeamSizeBand3.MaxNeutralKillers),
        new TeamSizeBandOpts(TeamSizeBand4.Players, TeamSizeBand4.MinImpostors, TeamSizeBand4.MaxImpostors, TeamSizeBand4.MinNeutrals, TeamSizeBand4.MaxNeutrals, TeamSizeBand4.MaxNeutralKillers),
        new TeamSizeBandOpts(TeamSizeBand5.Players, TeamSizeBand5.MinImpostors, TeamSizeBand5.MaxImpostors, TeamSizeBand5.MinNeutrals, TeamSizeBand5.MaxNeutrals, TeamSizeBand5.MaxNeutralKillers),
    };

    // 人数 players で使う帯の項目 (切ってある・どの帯にも届かない時は null)
    private static TeamSizeBandOpts Active(int players)
    {
        if (!Enabled) return null;
        TeamSizeBandOpts best = null;
        foreach (var b in Bands)
        {
            int p = b.Players.Value;
            if (p > 0 && p <= players && (best == null || p > best.Players.Value)) best = b;
        }
        return best;
    }

    public static Band? For(int players) => Active(players)?.ToBand();

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

    private static bool AmHost => AmongUsClient.Instance && AmongUsClient.Instance.AmHost;
    private static IGameOptions Options => GameOptionsManager.Instance?.CurrentGameOptions;
    private static int ImpostorCapOf(IGameOptions o) => Math.Max(1, ImpostorCap.MaxFor(ImpostorCap.RoomSize(o)));

    // ---- 帯 ⇔ ゲーム設定の行 (ホスト) ----

    private static bool IsCountRow(Opt opt) =>
        opt == ImpostorRange.Min || opt == NeutralKillSettings.MinNeutrals || opt == NeutralKillSettings.MaxNeutrals || opt == NeutralKillSettings.MaxKillers
        || opt is Builtin.VanillaOpt { IsImpostorCount: true };

    private static bool IsBandOpt(Opt opt)
    {
        if (opt == null) return false;
        foreach (var b in Bands)
            if (opt == b.Players || opt == b.MinImpostors || opt == b.MaxImpostors || opt == b.MinNeutrals || opt == b.MaxNeutrals || opt == b.MaxNeutralKillers)
                return true;
        return false;
    }

    // 帯の値をゲーム設定の行へ写す。変わったら true・本編のインポスター数を変えたら vanilla = true
    private static bool Reflect(TeamSizeBandOpts bo, out bool vanilla)
    {
        vanilla = false;
        var o = Options;
        if (o == null) return false;
        var band = bo.ToBand();
        int max = Math.Clamp(band.MaxImpostors, 1, ImpostorCapOf(o));
        if (o.NumImpostors != max)
        {
            o.SetInt(Int32OptionNames.NumImpostors, max);
            vanilla = true;
        }
        bool changed = ImpostorRange.Min.SetValue(Math.Min(band.MinImpostors, max));
        changed |= NeutralKillSettings.MinNeutrals.SetValue(band.MinNeutrals);
        changed |= NeutralKillSettings.MaxNeutrals.SetValue(band.MaxNeutrals);
        changed |= NeutralKillSettings.MaxKillers.SetValue(band.MaxNeutralKillers);
        return changed || vanilla;
    }

    // ゲーム設定の行の値を帯へ書き戻す
    private static void WriteBack(TeamSizeBandOpts bo, IGameOptions o)
    {
        bo.MinImpostors.SetValue(ImpostorRange.Min.Value);
        bo.MaxImpostors.SetValue(o.NumImpostors);
        bo.MinNeutrals.SetValue(NeutralKillSettings.MinNeutrals.Value);
        bo.MaxNeutrals.SetValue(NeutralKillSettings.MaxNeutrals.Value);
        bo.MaxNeutralKillers.SetValue(NeutralKillSettings.MaxKillers.Value);
    }

    // 最少が最多を超えないように、変えた方に合わせてもう片方を動かす (edited = null は本編のインポスター数)。
    // 本編のインポスター数を変えたら true
    private static bool Normalize(Opt edited, IGameOptions o)
    {
        bool vanilla = false;
        var min = ImpostorRange.Min;
        if (min.Value > o.NumImpostors)
        {
            if (edited == min)
            {
                int max = Math.Min(min.Value, ImpostorCapOf(o));
                o.SetInt(Int32OptionNames.NumImpostors, max);
                min.SetValue(max);
                vanilla = true;
            }
            else min.SetValue(o.NumImpostors);
        }
        var minN = NeutralKillSettings.MinNeutrals;
        var maxN = NeutralKillSettings.MaxNeutrals;
        var kill = NeutralKillSettings.MaxKillers;
        if (edited == maxN)
        {
            if (minN.Value > maxN.Value) minN.SetValue(maxN.Value);
            if (kill.Value > maxN.Value) kill.SetValue(maxN.Value);
        }
        else if (edited == minN && minN.Value > maxN.Value) maxN.SetValue(minN.Value);
        else if (edited == kill && kill.Value > maxN.Value) maxN.SetValue(kill.Value);
        return vanilla;
    }

    // 設定画面で項目が押された後 (MRP の行・検索の結果の行)。vanillaImpostors = 本編のインポスター数の行が押された
    internal static void OnEdited(Opt opt, bool vanillaImpostors = false)
    {
        if (!AmHost || !LobbyBehaviour.Instance) return;
        var o = Options;
        if (o == null) return;
        bool row = vanillaImpostors || IsCountRow(opt);
        if (!row && opt != Enabled && !IsBandOpt(opt)) return;
        bool vanilla = row && Normalize(vanillaImpostors ? null : opt, o);
        var bo = Active(PlayerCount());
        if (bo != null)
        {
            if (row) WriteBack(bo, o);
            else
            {
                Reflect(bo, out bool v);
                vanilla |= v;
            }
        }
        if (vanilla) SyncVanilla();
        RefreshMenu();
    }

    private static void SyncVanilla()
    {
        if (GameManager.Instance) GameManager.Instance.LogicOptions.SyncOptions();
    }

    // 開いている設定画面の行を値に合わせて描き直す (MRP の行と本編のインポスター数の行)
    private static void RefreshMenu()
    {
        var menu = GameSettingMenu.Instance;
        if (!menu) return;
        SettingsMenu.RefreshAll();
        GameTabRows.RefreshVanillaImpostors(menu.GameSettingsTab);
    }

    // ---- インポスター (ホスト・開始の数えが始まった時) ----

    private static readonly Random Rng = new();
    private static int _savedImpostors = -1; // 引いた数を書く前のインポスター数 (= 最多。数えを取りやめた時と次のロビーで戻す)

    internal static void OnBegin()
    {
        if (!AmHost) return;
        var opts = Options;
        if (opts == null) return;
        int players = PlayerCount();
        if (Active(players) is { } bo && Reflect(bo, out _)) OptionSync.SendAll();
        // 前に引いた数が戻らないまま数えがもう一度始まった時も、最多は引く前の数
        int max = Math.Clamp(_savedImpostors >= 0 ? _savedImpostors : opts.NumImpostors, 1, ImpostorCapOf(opts));
        int min = Math.Clamp(ImpostorRange.Min.Value, 1, max);
        int n = Rng.Next(min, max + 1);
        Plugin.Logger.LogInfo($"team sizes: players={players} impostors {min}-{max} => {n}");
        if (opts.NumImpostors != n)
        {
            if (_savedImpostors < 0) _savedImpostors = opts.NumImpostors;
            opts.SetInt(Int32OptionNames.NumImpostors, n);
        }
        SyncVanilla();
    }

    // 開始の数えを取りやめた (ロビーのまま・船がまだ無い)
    internal static void OnCancel()
    {
        if (_savedImpostors < 0 || !AmHost || !LobbyBehaviour.Instance || ShipStatus.Instance) return;
        if (AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.Joined) return;
        RestoreImpostors();
        RefreshMenu();
    }

    // ---- ロビー (ホスト): 人数が変わったら帯を写す ----

    private const long SettleMs = 2000; // 入った直後は人の情報が揃わないので少し待つ
    private static long _checkAt;
    private static bool _newLobby;

    // 人数か帯の設定が変わったかもしれない (ロビーに着いた・誰かが入った / 出た・設定画面を閉じた・ホストが替わった)
    internal static void Recheck(bool newLobby = false)
    {
        if (newLobby) _newLobby = true;
        _checkAt = Environment.TickCount64 + SettleMs;
    }

    public static void Tick()
    {
        if (_checkAt == 0 || Environment.TickCount64 < _checkAt) return;
        _checkAt = 0;
        if (!AmHost || !LobbyBehaviour.Instance) return;
        // 試合から戻ったロビーでは、引いたインポスター数を最多へ戻す
        if (_newLobby)
        {
            _newLobby = false;
            RestoreImpostors();
        }
        int players = PlayerCount();
        var bo = Active(players);
        if (bo == null || !Reflect(bo, out bool vanilla)) return;
        Plugin.Logger.LogInfo($"team sizes: players={players} band={bo.Players.Value} reflected");
        if (vanilla) SyncVanilla();
        OptionSync.SendAll();
        RefreshMenu();
    }

    private static void RestoreImpostors()
    {
        int saved = _savedImpostors;
        _savedImpostors = -1;
        var opts = Options;
        if (saved < 0 || opts == null || opts.NumImpostors == saved) return;
        opts.SetInt(Int32OptionNames.NumImpostors, Math.Clamp(saved, 1, ImpostorCapOf(opts)));
        SyncVanilla();
        Plugin.Logger.LogInfo($"team sizes: impostors back to {opts.NumImpostors}");
    }
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
    public static IntOpt PlayersOpt(int def) => new("TeamSizes.Players", def, 0, Crowded.MaxPlayers) { OffTab = true };
    public static IntOpt ImpostorOpt(string key, int def) => new(key, def, 1, ImpostorCap.CapFor(Crowded.MaxPlayers)) { OffTab = true };
    public static IntOpt NeutralOpt(string key, int def) => new(key, def, 0, Crowded.MaxPlayers) { OffTab = true };
}

// 人数の帯。既定は 4 / 7 / 10 / 13 / 16 人から (全体の ON/OFF は既定で切ってある)。行はゲーム設定のタブの終わりに並べる
[Settings(Tab.General, "TeamSizes.Band1", order: -95)]
public static class TeamSizeBand1
{
    public static readonly IntOpt Players = TeamSizeBandOpts.PlayersOpt(4);
    public static readonly IntOpt MinImpostors = TeamSizeBandOpts.ImpostorOpt("TeamSizes.MinImpostors", 1);
    public static readonly IntOpt MaxImpostors = TeamSizeBandOpts.ImpostorOpt("TeamSizes.MaxImpostors", 1);
    public static readonly IntOpt MinNeutrals = TeamSizeBandOpts.NeutralOpt("TeamSizes.MinNeutrals", 0);
    public static readonly IntOpt MaxNeutrals = TeamSizeBandOpts.NeutralOpt("TeamSizes.MaxNeutrals", 1);
    public static readonly IntOpt MaxNeutralKillers = TeamSizeBandOpts.NeutralOpt("TeamSizes.MaxNeutralKillers", 0);
}

[Settings(Tab.General, "TeamSizes.Band2", order: -94)]
public static class TeamSizeBand2
{
    public static readonly IntOpt Players = TeamSizeBandOpts.PlayersOpt(7);
    public static readonly IntOpt MinImpostors = TeamSizeBandOpts.ImpostorOpt("TeamSizes.MinImpostors", 1);
    public static readonly IntOpt MaxImpostors = TeamSizeBandOpts.ImpostorOpt("TeamSizes.MaxImpostors", 2);
    public static readonly IntOpt MinNeutrals = TeamSizeBandOpts.NeutralOpt("TeamSizes.MinNeutrals", 0);
    public static readonly IntOpt MaxNeutrals = TeamSizeBandOpts.NeutralOpt("TeamSizes.MaxNeutrals", 1);
    public static readonly IntOpt MaxNeutralKillers = TeamSizeBandOpts.NeutralOpt("TeamSizes.MaxNeutralKillers", 1);
}

[Settings(Tab.General, "TeamSizes.Band3", order: -93)]
public static class TeamSizeBand3
{
    public static readonly IntOpt Players = TeamSizeBandOpts.PlayersOpt(10);
    public static readonly IntOpt MinImpostors = TeamSizeBandOpts.ImpostorOpt("TeamSizes.MinImpostors", 2);
    public static readonly IntOpt MaxImpostors = TeamSizeBandOpts.ImpostorOpt("TeamSizes.MaxImpostors", 2);
    public static readonly IntOpt MinNeutrals = TeamSizeBandOpts.NeutralOpt("TeamSizes.MinNeutrals", 1);
    public static readonly IntOpt MaxNeutrals = TeamSizeBandOpts.NeutralOpt("TeamSizes.MaxNeutrals", 2);
    public static readonly IntOpt MaxNeutralKillers = TeamSizeBandOpts.NeutralOpt("TeamSizes.MaxNeutralKillers", 1);
}

[Settings(Tab.General, "TeamSizes.Band4", order: -92)]
public static class TeamSizeBand4
{
    public static readonly IntOpt Players = TeamSizeBandOpts.PlayersOpt(13);
    public static readonly IntOpt MinImpostors = TeamSizeBandOpts.ImpostorOpt("TeamSizes.MinImpostors", 2);
    public static readonly IntOpt MaxImpostors = TeamSizeBandOpts.ImpostorOpt("TeamSizes.MaxImpostors", 3);
    public static readonly IntOpt MinNeutrals = TeamSizeBandOpts.NeutralOpt("TeamSizes.MinNeutrals", 1);
    public static readonly IntOpt MaxNeutrals = TeamSizeBandOpts.NeutralOpt("TeamSizes.MaxNeutrals", 2);
    public static readonly IntOpt MaxNeutralKillers = TeamSizeBandOpts.NeutralOpt("TeamSizes.MaxNeutralKillers", 1);
}

[Settings(Tab.General, "TeamSizes.Band5", order: -91)]
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

// 開始の数えを取りやめた時は、引いたインポスター数を最多へ戻す (ゲーム設定の最多の行が引いた数のまま残らないように)。
// 数えている途中から戻した時だけ (呼ばれた後は状態が戻っていて見分けられないので、前に控える)
[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.ResetStartState))]
internal static class TeamSizesCancelPatch
{
    public static void Prefix(GameStartManager __instance, out bool __state) =>
        __state = __instance.startState == GameStartManager.StartingStates.Countdown;

    public static void Postfix(bool __state)
    {
        if (!__state) return;
        try { TeamSizes.OnCancel(); }
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
