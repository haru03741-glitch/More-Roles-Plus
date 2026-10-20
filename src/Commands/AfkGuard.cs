// Based on the AFK detection in https://github.com/Gurge44/EndlessHostRoles (GPL-3.0)
using System;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Commands;

[Settings(Tab.General, "AfkSettings.Title", order: -75)]
public static class AfkSettings
{
    public static readonly ChoiceOpt Action = new("AfkSettings.Action", 0,
        new Text("AfkSettings.LeaveThem"), new Text("AfkSettings.TellTheHost"), new Text("AfkSettings.KillThem"), new Text("AfkSettings.KickThem"));
    public static readonly IntOpt Seconds = new("AfkSettings.Seconds", 120, 60, 600, 30, "s");
}

// 試合中の放置。位置だけで見ると、作業中や監視カメラを見ている人まで放置になるので、各自の端末が自分を見て決める:
// 「キーや画面を押した (PC)・動いた・作業やカメラの画面を開いている」のどれも無いまま決めた秒数が経ったら、ホストへ知らせる。
// 会議・追放の場面・始まりの場面の間は数えない。見るのは 1 秒に 1 回 (キーの押し下げはチャット欄の処理が毎フレーム見ている物を使う)
internal static class AfkGuard
{
    internal static bool Touched; // この 1 秒の間に何か押した

    private static readonly RemoteCall<byte> Report = new("Afk.Report", Route.ToHost,
        (w, v) => w.Write(v), r => r.ReadByte(), (sender, _) => OnReport(sender));

    private static long _nextAt;
    private static int _idle;
    private static bool _warned, _reported;
    private static Vector3 _lastPos;
    private static bool _hasPos;

    private static void Reset()
    {
        _idle = 0;
        _warned = _reported = false;
        _hasPos = false;
    }

    public static void Tick()
    {
        long now = Environment.TickCount64;
        if (now < _nextAt) return;
        _nextAt = now + 1000;
        // PC はチャット欄の処理が見ているキーの押し下げ・Android はこの時に画面に指が触れているか
        bool touched = Touched || (OperatingSystem.IsAndroid() && Input.touchCount > 0);
        Touched = false;
        if (AfkSettings.Action.Value == 0) { Reset(); return; }
        var me = PlayerControl.LocalPlayer;
        if (!me || !ShipStatus.Instance || me.Data == null || me.Data.IsDead
            || MeetingHud.Instance || ExileController.Instance || IntroCutscene.Instance)
        {
            Reset();
            return;
        }
        var pos = me.transform.position;
        bool moved = _hasPos && (Math.Abs(pos.x - _lastPos.x) > 0.05f || Math.Abs(pos.y - _lastPos.y) > 0.05f);
        _lastPos = pos;
        _hasPos = true;
        if (touched || moved || Minigame.Instance)
        {
            if (_reported) Notify(new Text("AfkGuard.YoureBack"));
            Reset();
            _hasPos = true;
            return;
        }
        _idle++;
        int limit = AfkSettings.Seconds.Value;
        if (!_warned && _idle >= limit - 20)
        {
            _warned = true;
            Notify(new Text("AfkGuard.Warning"));
        }
        if (!_reported && _idle >= limit)
        {
            _reported = true;
            Report.Send(0);
        }
    }

    private static void Notify(string text)
    {
        var hud = Vanilla.Hud;
        if (hud && hud.Notifier) hud.Notifier.AddDisconnectMessage(text);
        Plugin.Logger.LogInfo($"afk: {text}");
    }

    // ホスト: 放置の知らせを受けた
    private static void OnReport(PlayerControl p)
    {
        // 1 秒ごとの知らせは会議が始まった後に届くことがある。会議と追放の場面では受けない
        if (!p || p.Data == null || p.Data.IsDead || !ShipStatus.Instance || Roles.GameEnd.Decided || MeetingHud.Instance || ExileController.Instance) return;
        string name = p.Data.PlayerName;
        switch (AfkSettings.Action.Value)
        {
            case 1:
                Notify(new Text("AfkGuard.IsAFK").With("name", name));
                break;
            case 2:
                p.RpcMurderPlayer(p, true);
                Notify(new Text("AfkGuard.WasKilledForBeingAFK").With("name", name));
                break;
            case 3:
                if (p.AmOwner) { Notify(new Text("AfkGuard.TheHostCantBeKicked")); break; }
                AmongUsClient.Instance.KickPlayer(p.OwnerId, false);
                Notify(new Text("AfkGuard.WasKickedForBeingAFK").With("name", name));
                break;
        }
    }

    internal static string Describe() =>
        $"OK afk action={AfkSettings.Action.Value} limit={AfkSettings.Seconds.Value} idle={_idle} warned={_warned} reported={_reported}";
}
