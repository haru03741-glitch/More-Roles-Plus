using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Dev;
using MoreRolesPlus.Net;
using UnityEngine;
using UnityEngine.Events;

namespace MoreRolesPlus.Commands;

// モデレーター: ホストが一覧 (ModList.txt・/modlist・一覧の窓) に入れた人は、ホスト用のコマンドの一部を自分のチャットから使える。
// 打った人の端末はコマンドをホストへ送るだけで、モデレーターかどうかはホストの端末が一覧で確かめてから実行し、返事を本人へ返す。
// 使った事はホストのチャットにも出す。
// ホストは部屋の人それぞれに「モデレーターか」を知らせ、モデレーターの端末にはロビーの開始ボタンを出す (押すとホストへ /start を頼む。
// 開始までの数えの板を押すと /cancel)
internal static class Moderators
{
    // (客) 自分がモデレーターか。ホストが知らせる
    internal static bool Me { get; private set; }

    private static readonly RemoteCall<bool> State = new("Mod.State", Route.HostToAll,
        (w, on) => w.Write(on), r => r.ReadBoolean(), (_, on) => Me = on, perSecond: 4);

    private static readonly Dictionary<int, bool> Told = new(); // (ホスト) 誰に何を知らせたか
    private static float _next, _again;

    // 一覧が変わった: 次の確かめで全員に知らせ直す
    internal static void Changed() => Told.Clear();

    internal static void Tick()
    {
        var c = AmongUsClient.Instance;
        if (!c || c.GameState is not (InnerNet.InnerNetClient.GameStates.Joined or InnerNet.InnerNetClient.GameStates.Started))
        {
            Me = false;
            Told.Clear();
            return;
        }
        if (!c.AmHost) return;
        Me = false;
        float now = Time.unscaledTime;
        if (now < _next) return;
        _next = now + 1f;
        if (now >= _again)
        {
            _again = now + 15f; // 入ってすぐで受け取れなかった人のために、ときどき知らせ直す
            Told.Clear();
        }
        var clients = c.allClients;
        for (int i = 0; i < clients.Count; i++)
        {
            var cl = clients[i];
            if (cl == null || cl.Id == c.ClientId || !cl.Character) continue;
            bool on = JoinLists.IsModerator(cl.Id);
            if (Told.TryGetValue(cl.Id, out bool was) && was == on) continue;
            State.Send(on, cl.Id);
            Told[cl.Id] = on;
        }
    }

    // (モデレーターの客) ロビーの開始ボタンを出して、押したらホストへ頼む
    private static Object _hooked;
    private static UnityAction _press;

    internal static void ShowStart(GameStartManager gsm)
    {
        var c = AmongUsClient.Instance;
        if (!Me || !gsm || !c || c.AmHost || !gsm.StartButton) return;
        if (gsm.startState != GameStartManager.StartingStates.NotStarting) return;
        gsm.StartButton.gameObject.SetActive(true);
        if (gsm.StartButtonClient) gsm.StartButtonClient.gameObject.SetActive(false);
        if (_hooked == gsm.StartButton) return;
        _hooked = gsm.StartButton;
        Menu.ClientOptions.SetClick(gsm.StartButton, _press ??= (UnityAction)(() => Ask.Send("start")));
    }

    // (モデレーターの客) 数えの板を押した: ホストへ取りやめを頼む
    internal static bool AskCancel()
    {
        var c = AmongUsClient.Instance;
        if (!Me || !c || c.AmHost) return false;
        Ask.Send("cancel");
        return true;
    }

    // 任せるコマンド (ホストの設定を書き換える物・開発用は任せない)
    private static readonly HashSet<string> Allowed = new()
    {
        "kick", "ban", "banlist", "unban", "start", "cancel", "end", "廃村", "mt", "meet", "say",
    };

    // 打った人の端末: 任せられるコマンドならホストへ送って true
    internal static bool Forward(string name, string args)
    {
        var c = AmongUsClient.Instance;
        if (!Allowed.Contains(name) || !c || c.AmHost || !PlayerControl.LocalPlayer) return false;
        Ask.Send(name + (args.Length > 0 ? " " + args : ""));
        return true;
    }

    private static readonly RemoteCall<string> Ask = new("Mod.Command", Route.ToHost,
        (w, line) => w.Write(line), r => r.ReadString(), HostRun, perSecond: 2);

    private static readonly RemoteCall<string> Answer = new("Mod.Answer", Route.HostToAll,
        (w, text) => w.Write(text), r => r.ReadString(), (_, text) => DevChat.Notice(Vanilla.Chat, text));

    // ホスト: モデレーターなら実行して、返事を本人へ
    private static void HostRun(PlayerControl sender, string line)
    {
        var c = AmongUsClient.Instance;
        if (!c || !c.AmHost || !sender || line == null) return;
        var client = c.GetClientFromCharacter(sender);
        if (client == null) return;
        if (line.Length > 200) line = line[..200];
        int sp = line.IndexOf(' ');
        string name = sp < 0 ? line : line[..sp];
        string args = sp < 0 ? "" : line[(sp + 1)..];
        if (!JoinLists.IsModerator(client.Id))
        {
            Answer.Send(new Text("Moderators.NotModerator"), client.Id);
            return;
        }
        if (!Allowed.Contains(name)) return;
        string who = sender.Data != null ? sender.Data.PlayerName : "?";
        DevChat.Notice(Vanilla.Chat, new Text("Moderators.Used").With("name", who).With("cmd", "/" + line));
        Plugin.Logger.LogInfo($"moderator {who}: /{line}");
        if (!ChatCommands.TryRun(name, args, out string reply)) return;
        if (!string.IsNullOrEmpty(reply)) Answer.Send(reply, client.Id);
    }
}

[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
internal static class ModeratorStartPatch
{
    public static void Postfix(GameStartManager __instance)
    {
        try { Moderators.ShowStart(__instance); }
        catch (System.Exception e) { Plugin.Logger.LogError($"moderator start: {e}"); }
    }
}
