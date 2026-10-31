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

    private static readonly Text NotInGame = new("HostActions.NotInGame");

    public static string PlayerList()
    {
        var data = GameData.Instance;
        if (!data) return new Text("HostActions.NotInARoom");
        var sb = new StringBuilder();
        var all = data.AllPlayers;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p == null || p.Disconnected) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(p.PlayerId).Append(": ").Append(p.PlayerName);
            if (p.IsDead) sb.Append(new Text("HostActions.Dead").ToString());
        }
        return sb.ToString();
    }

    public static string Start()
    {
        var gsm = Vanilla.StartManager;
        if (!gsm || ShipStatus.Instance) return new Text("HostActions.OnlyInTheLobby");
        if (SkipCountdown()) return new Text("HostActions.CountdownSkipped");
        // 数えが終わって読み込み中にもう一度始めると、ロビーの部品が壊れて毎フレーム例外が出る
        if (gsm.startState != GameStartManager.StartingStates.NotStarting
            || AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.Joined)
            return new Text("HostActions.AlreadyStarting");
        gsm.BeginGame();
        return new Text("HostActions.StartingTheGame");
    }

    // 開始の数えの途中なら 0 にする
    public static bool SkipCountdown()
    {
        var gsm = Vanilla.StartManager;
        if (!AmHost || !gsm || gsm.startState != GameStartManager.StartingStates.Countdown) return false;
        gsm.countDownTimer = 0f;
        return true;
    }

    // 開始の数えを取りやめてロビーへ戻す (本編の取りやめ。客の数えの表示も消える)
    public static string Cancel()
    {
        var gsm = Vanilla.StartManager;
        if (!gsm || ShipStatus.Instance) return new Text("HostActions.OnlyInTheLobby");
        if (gsm.startState != GameStartManager.StartingStates.Countdown) return new Text("HostActions.NotCountingDown");
        gsm.ResetStartState();
        return new Text("HostActions.StartCancelled");
    }

    public static string Abort()
    {
        if (!InGame) return NotInGame;
        return GameEnd.Abort() ? new Text("HostActions.GameAborted") : new Text("HostActions.TheGameIsAlreadyEnding");
    }

    // 会議中なら閉じる (誰も追放しない)。会議の外なら自分が会議を開く (ボタンの回数・待ち時間・妨害中かを問わない)
    public static string ToggleMeeting()
    {
        if (!InGame || GameEnd.Decided) return NotInGame;
        var meeting = MeetingHud.Instance;
        if (meeting)
        {
            meeting.RpcClose();
            return new Text("HostActions.MeetingClosed");
        }
        if (ExileController.Instance) return new Text("HostActions.WaitForEjection");
        // 始まりの場面やエアシップの出現場所選びの最中に開くと、会議がその下に隠れたまま進む
        var mg = Minigame.Instance;
        if (IntroCutscene.Instance || (mg && mg.TryCast<SpawnInMinigame>() != null))
            return new Text("HostActions.WaitForStart");
        var lp = PlayerControl.LocalPlayer;
        MeetingRoomManager.Instance.AssignSelf(lp, null);
        var hud = Vanilla.Hud;
        if (hud) hud.OpenMeetingRoom(lp);
        lp.RpcStartMeeting(null);
        return new Text("HostActions.MeetingCalled");
    }

    public static string SetRole(string args)
    {
        // ロビーでは予約にする (次の試合の配布で最優先で付く)
        bool lobby = !InGame && LobbyBehaviour.Instance && AmongUsClient.Instance && AmongUsClient.Instance.AmHost && PlayerControl.LocalPlayer;
        if (!InGame && !lobby) return NotInGame;
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
        if (proto == null) return new Text("HostActions.UnknownRole").With("roleNames", RoleNames());
        if (lobby)
        {
            RoleAssigner.Reserved[target.PlayerId] = Registry.Roles.IndexOf(proto);
            return new Text("HostActions.RoleReserved").With("name", target.Data.PlayerName).With("role", proto.Name);
        }
        RoleAssigner.Give(target, proto);
        return new Text("HostActions.RoleChanged").With("name", target.Data.PlayerName).With("role", proto.Name);
    }

    // 練習の相手 (ボット) を出す: /bot [samurai|impostor] [強さ 1〜9] ・ /bot off で全部止める
    public static string Bot(string args)
    {
        var words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && (words[0] == "off" || words[0] == "stop"))
            return new Text("HostActions.BotsStopped").With("n", DuelBot.Stop().ToString());
        if (!InGame) return NotInGame;
        if (!AmHost || Net.ServerKind.IsOfficial()) return new Text("HostActions.BotNotHere");
        if (MeetingHud.Instance || ExileController.Instance) return NotInGame;
        if (GameData.Instance.GetAvailableId() < 0) return new Text("HostActions.BotFull");
        var kind = DuelBot.Kind.Samurai;
        int level = 5;
        foreach (var w in words)
        {
            if (int.TryParse(w, out int lv)) level = Math.Clamp(lv, 1, 9);
            else if (w.StartsWith("i", StringComparison.OrdinalIgnoreCase) || w == "インポスター") kind = DuelBot.Kind.Impostor;
            else if (w.StartsWith("s", StringComparison.OrdinalIgnoreCase) || w == "サムライ") kind = DuelBot.Kind.Samurai;
        }
        string name = DuelBot.Add(kind, level);
        if (name == null) return new Text("HostActions.UnknownRole").With("roleNames", RoleNames());
        return new Text("HostActions.BotAdded").With("name", name).With("role", (kind == DuelBot.Kind.Samurai ? FindRole("Samurai") : FindRole("Impostor"))?.Name ?? "");
    }

    public static string Kill(string args)
    {
        if (!InGame) return NotInGame;
        var p = Target(args, out string err);
        if (!p) return err;
        if (p.Data.IsDead) return new Text("HostActions.AlreadyDead");
        p.RpcMurderPlayer(p, true);
        return new Text("HostActions.Killed").With("name", p.Data.PlayerName);
    }

    public static string Revive(string args)
    {
        if (!InGame) return NotInGame;
        var p = Target(args, out string err);
        if (!p) return err;
        if (!p.Data.IsDead) return new Text("HostActions.NotDead");
        DevCommands.Revive(p.PlayerId);
        return new Text("HostActions.Revived").With("name", p.Data.PlayerName);
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

    private static string NoPlayer(string arg) => new Text("HostActions.NoPlayerSeeId").With("arg", arg);

    private static PlayerControl Player(byte pid)
        => GameData.Instance ? GameData.Instance.GetPlayerById(pid)?.Object : null;

    // 役職を Id か表示名 (日本語・英語) で探す
    internal static RoleBase FindRole(string s)
    {
        foreach (var r in Registry.Roles)
        {
            if (r.Name.Matches(s, r.Id))
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
