using System;
using HarmonyLib;
using MoreRolesPlus.Commands;
using MoreRolesPlus.Roles;

namespace MoreRolesPlus.Net;

// ホストが抜けて替わった時の始末。本編は新しいホストのまま試合を続けるので、MRP も役職の状態は全員の端末にあるまま続ける。
// 前のホストのメモリにしか無かった物 (放置と観戦の知らせ) は今のホストへ送り直し、設定の「ホストが抜けたら廃村」が入っていれば
// 新しいホストが勝者なしで打ち切る。設定は前のホストが配った値を持っているので、それを見る
internal static class HostChange
{
    private const int AbortDelayMs = 1000;   // 本編が引き継ぎと抜けた人の片付け (インポスターが抜けた時の終わりなど) を済ませるのを待つ
    private const int TakeOverDelayMs = 2500; // 試合から戻った直後は客がまだ終わりの画面にいるので、ロビーに着くのを待つ

    private static long _abortAt, _takeOverAt;
    private static int _announced = -1; // 知らせ済みのホスト (抜けた人と自分がホストになった知らせの両方から呼ばれても 1 回だけ)

    // 全員 (新しいホストも): ホストが替わった
    public static void OnChanged()
    {
        var client = AmongUsClient.Instance;
        if (client.HostId == _announced) return;
        _announced = client.HostId;
        string name = client.GetHost() is { } host ? host.PlayerName : "?";
        Plugin.Logger.LogInfo($"host changed: {name} (state={(client ? client.GameState.ToString() : "-")})");
        if (Vanilla.Hud is { } hud && hud.Notifier) hud.Notifier.AddDisconnectMessage(new Text("HostChange.NewHost").With("name", name));
        AfkGuard.OnHostChanged();
        Spectate.OnHostChanged();
        RoleAssigner.OnHostChanged();
        TeamSizes.Recheck();
    }

    // 新しいホストの側
    public static void OnBecomeHost()
    {
        var client = AmongUsClient.Instance;
        long now = Environment.TickCount64;
        if (client.GameState == InnerNet.InnerNetClient.GameStates.Started)
        {
            if (HostSettings.AbortWhenHostLeaves.Value) _abortAt = now + AbortDelayMs;
        }
        else if (LobbyBehaviour.Instance) _takeOverAt = now + TakeOverDelayMs;
    }

    // ホストを引き継いだまま試合からロビーへ戻った
    public static void OnLobby()
    {
        if (AmongUsClient.Instance.AmHost && OptionSync.HoldsHostValues) _takeOverAt = Environment.TickCount64 + TakeOverDelayMs;
    }

    public static void Tick()
    {
        if ((_abortAt | _takeOverAt) == 0) return;
        long now = Environment.TickCount64;
        var client = AmongUsClient.Instance;
        if (_abortAt != 0 && now >= _abortAt)
        {
            _abortAt = 0;
            bool started = client && client.AmHost && client.GameState == InnerNet.InnerNetClient.GameStates.Started;
            // 船の読み込み中・始まりの場面の途中で替わった時は、船ができるまで待ってから打ち切る
            if (started && !ShipStatus.Instance) _abortAt = now + AbortDelayMs;
            else if (started && GameEnd.Abort()) Plugin.Logger.LogInfo("host left: game aborted");
        }
        if (_takeOverAt != 0 && now >= _takeOverAt)
        {
            _takeOverAt = 0;
            // 部屋で使っていた値から自分の保存値へ戻し、変わった所を全員に知らせる
            if (client && client.AmHost && LobbyBehaviour.Instance && !ShipStatus.Instance && OptionSync.HoldsHostValues)
            {
                OptionSync.RestoreOwn();
                OptionSync.SendAll();
            }
        }
    }

    // 部屋を移った・抜けた時に前の部屋の予定を残さない
    public static void Clear()
    {
        _abortAt = _takeOverAt = 0;
        _announced = -1;
    }
}

[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
internal static class HostChangeLobbyPatch
{
    public static void Postfix()
    {
        try { HostChange.OnLobby(); }
        catch (Exception e) { Plugin.Logger.LogError($"host change: {e.Message}"); }
    }
}
