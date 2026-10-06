using System;
using System.Text;
using MoreRolesPlus.Dev;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles;

namespace MoreRolesPlus.Commands;

// ホストがコマンドやショートカットで行う操作。返り値はチャット欄に出す結果の文
internal static class HostActions
{
    private static bool AmHost => AmongUsClient.Instance && AmongUsClient.Instance.AmHost;
    private static bool InGame => ShipStatus.Instance && GameData.Instance && PlayerControl.LocalPlayer;

    private static readonly Text NotInGame = new("試合中だけ使えます", "Only during a game");

    public static string PlayerList()
    {
        var data = GameData.Instance;
        if (!data) return new Text("部屋に入っていません", "Not in a room");
        var sb = new StringBuilder();
        var all = data.AllPlayers;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p == null || p.Disconnected) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(p.PlayerId).Append(": ").Append(p.PlayerName);
            if (p.IsDead) sb.Append(new Text(" (死亡)", " (dead)").ToString());
        }
        return sb.ToString();
    }

    public static string Start()
    {
        var gsm = GameStartManager.Instance;
        if (!gsm || ShipStatus.Instance) return new Text("ロビーでだけ使えます", "Only in the lobby");
        if (SkipCountdown()) return new Text("数えを飛ばしました", "Countdown skipped");
        // 数えが終わって読み込み中にもう一度始めると、ロビーの部品が壊れて毎フレーム例外が出る
        if (gsm.startState != GameStartManager.StartingStates.NotStarting
            || AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.Joined)
            return new Text("もう始まっています", "Already starting");
        gsm.BeginGame();
        return new Text("試合を始めます", "Starting the game");
    }

    // 開始の数えの途中なら 0 にする
    public static bool SkipCountdown()
    {
        var gsm = GameStartManager.Instance;
        if (!AmHost || !gsm || gsm.startState != GameStartManager.StartingStates.Countdown) return false;
        gsm.countDownTimer = 0f;
        return true;
    }

    public static string Abort()
    {
        if (!InGame) return NotInGame;
        return GameEnd.Abort() ? new Text("廃村にしました", "Game aborted") : new Text("もう試合が終わるところです", "The game is already ending");
    }

    // 会議中なら閉じる (誰も追放しない)。会議の外なら自分が会議を開く (ボタンの回数・待ち時間・妨害中かを問わない)
    public static string ToggleMeeting()
    {
        if (!InGame || GameEnd.Decided) return NotInGame;
        var meeting = MeetingHud.Instance;
        if (meeting)
        {
            meeting.RpcClose();
            return new Text("会議を閉じました", "Meeting closed");
        }
        if (ExileController.Instance) return new Text("追放の場面が終わってから使えます", "Wait until the ejection is over");
        // 始まりの場面やエアシップの出現場所選びの最中に開くと、会議がその下に隠れたまま進む
        var mg = Minigame.Instance;
        if (IntroCutscene.Instance || (mg && mg.TryCast<SpawnInMinigame>() != null))
            return new Text("始まりの場面が終わってから使えます", "Wait until the game has begun");
        var lp = PlayerControl.LocalPlayer;
        MeetingRoomManager.Instance.AssignSelf(lp, null);
        HudManager.Instance.OpenMeetingRoom(lp);
        lp.RpcStartMeeting(null);
        return new Text("会議を開きました", "Meeting called");
    }

    public static string SetRole(string args)
    {
        if (!InGame) return NotInGame;
        RoleBase proto = null;
        PlayerControl target = PlayerControl.LocalPlayer;
        foreach (var a in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (byte.TryParse(a, out byte pid))
            {
                target = Player(pid);
                if (!target) return NoPlayer(a);
            }
            else proto ??= FindRole(a);
        }
        if (proto == null) return new Text($"役職が見つかりません。使える役職: {RoleNames()}", $"Unknown role. Roles: {RoleNames()}");
        RoleAssigner.Give(target, proto);
        return new Text($"{target.Data.PlayerName} を {proto.Name.Ja} にしました", $"{target.Data.PlayerName} is now {proto.Name.En}");
    }

    public static string Kill(string args)
    {
        if (!InGame) return NotInGame;
        var p = Target(args, out string err);
        if (!p) return err;
        if (p.Data.IsDead) return new Text("もう死んでいます", "Already dead");
        p.RpcMurderPlayer(p, true);
        return new Text($"{p.Data.PlayerName} を倒しました", $"Killed {p.Data.PlayerName}");
    }

    public static string Revive(string args)
    {
        if (!InGame) return NotInGame;
        var p = Target(args, out string err);
        if (!p) return err;
        if (!p.Data.IsDead) return new Text("生きています", "Not dead");
        DevCommands.Revive(p.PlayerId);
        return new Text($"{p.Data.PlayerName} を生き返らせました", $"Revived {p.Data.PlayerName}");
    }

    // 引数の番号の人 (省略で自分)
    private static PlayerControl Target(string args, out string err)
    {
        err = null;
        if (args.Length == 0) return PlayerControl.LocalPlayer;
        var p = byte.TryParse(args, out byte pid) ? Player(pid) : null;
        if (!p || p.Data == null) err = NoPlayer(args);
        return p;
    }

    private static string NoPlayer(string arg) => new Text($"{arg} 番の人はいません (/id で番号を確認)", $"No player {arg} (see /id)");

    private static PlayerControl Player(byte pid)
        => GameData.Instance ? GameData.Instance.GetPlayerById(pid)?.Object : null;

    // 役職を Id か表示名 (日本語・英語) で探す
    private static RoleBase FindRole(string s)
    {
        foreach (var r in Registry.Roles)
        {
            if (string.Equals(r.Id, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.Name.Ja, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.Name.En, s, StringComparison.OrdinalIgnoreCase))
                return r;
        }
        return null;
    }

    private static string RoleNames()
    {
        var sb = new StringBuilder();
        foreach (var r in Registry.Roles)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(r.Name.ToString());
        }
        return sb.ToString();
    }
}
