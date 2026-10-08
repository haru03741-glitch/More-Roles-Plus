using System;
using System.Globalization;
using System.Linq;
using AmongUs.GameOptions;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles;
using UnityEngine;

namespace MoreRolesPlus.Dev;

// 開発者コンソールとテスト用ブリッジで使う操作。どちらも同じ表 (TestBridge.Run) から呼ぶ。
// 他の人に効く操作はホストだけが行い、結果は本編の RPC か HostToAll の電文で全員に届ける
// (他の人の手元のビルドには開発者の名簿が無いので、送り主が開発者かを受け手は確かめられない)。
internal static class DevCommands
{
    private static readonly RemoteCall<byte> ReviveCall = new("Dev.Revive", Route.HostToAll,
        (w, pid) => w.Write(pid), r => r.ReadByte(), (_, pid) => ReviveLocal(pid));

    public static void Register()
    {
        TestBridge.Register("tp", "<x> <y> | <番号> 自分を座標かその人の所へ移動 (オンラインでは全員に届く)", (args, reply) =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp) { reply("ERR tp no local player"); return; }
            string[] p = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Vector2 to;
            if (p.Length == 1 && byte.TryParse(p[0], out byte pid))
            {
                var t = Player(pid);
                if (!t) { reply($"ERR tp no player {pid}"); return; }
                to = t.GetTruePosition();
            }
            else if (p.Length >= 2 && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                                   && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                to = new Vector2(x, y);
            else { reply("ERR tp needs <x> <y> or <番号>"); return; }

            var nt = lp.NetTransform;
            if (AmongUsClient.Instance.NetworkMode == NetworkModes.FreePlay)
                // 手元だけ動かす。同じ sequence id だと古い位置として捨てられるので進めておく
                nt.SnapTo(to, (ushort)(nt.lastSequenceId + 328));
            else
                nt.RpcSnapTo(to);
            reply($"OK tp {TestBridge.F(to.x)} {TestBridge.F(to.y)}");
        });

        TestBridge.Register("setrole", "<役職Id> [番号] その人 (省略で自分) に役職を付けて全員に配る (ホストのみ。土台の役職も合わせる)", GiveRole);
        TestBridge.Register("giverole", "setrole と同じ", GiveRole);
        TestBridge.Register("assignsim", "<人数> [回数=1000] [種=1] [インポスター数=2] 今の設定で配り方を何度も回し、役職・アドオンごとの付いた回数を出す (本編の物は読まない)", (args, reply) =>
        {
            var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a.Length < 1 || !int.TryParse(a[0], out int players)) { reply("ERR assignsim <players> [runs] [seed] [impostors]"); return; }
            int runs = a.Length > 1 && int.TryParse(a[1], out int r) ? Math.Clamp(r, 1, 100000) : 1000;
            int seed = a.Length > 2 && int.TryParse(a[2], out int sd) ? sd : 1;
            int imps = a.Length > 3 && int.TryParse(a[3], out int im) ? im : Math.Min(2, players);
            foreach (var line in AssignPlan.Simulate(Math.Clamp(players, 1, 15), Math.Clamp(imps, 0, players), runs, seed).Split('\n')) reply("SIM " + line);
            reply("OK assignsim");
        });

        TestBridge.Register("kill", "<番号> その人を倒す (ホストのみ)", (args, reply) =>
        {
            if (!AmongUsClient.Instance.AmHost || !byte.TryParse(args.Trim(), out byte pid)) { reply("ERR kill needs host and number"); return; }
            var p = Player(pid);
            if (!p) { reply($"ERR kill no player {pid}"); return; }
            p.RpcMurderPlayer(p, true);
            reply($"OK kill {pid}");
        });

        TestBridge.Register("revive", "[番号] その人 (省略で自分) を生き返らせる (ホストのみ)", (args, reply) =>
        {
            if (!AmongUsClient.Instance.AmHost || !GameData.Instance) { reply("ERR revive needs host in game"); return; }
            byte pid = byte.TryParse(args.Trim(), out byte v) ? v : PlayerControl.LocalPlayer.PlayerId;
            var p = Player(pid);
            if (!p || p.Data == null) { reply($"ERR revive no player {pid}"); return; }
            if (!p.Data.IsDead) { reply($"ERR revive {pid} is alive"); return; }
            Revive(pid);
            reply($"OK revive {pid}");
        });

        TestBridge.Register("forcewin", "<crew|imp|役職Id> その勝ちで試合を終わらせる (ホストのみ)", (args, reply) =>
        {
            if (!AmongUsClient.Instance.AmHost || !GameData.Instance) { reply("ERR forcewin needs host in game"); return; }
            string a = args.Trim();
            if (a == "crew") GameEnd.End(GameEnd.TeamResult(Team.Crew), GameOverReason.CrewmatesByVote);
            else if (a == "imp") GameEnd.End(GameEnd.TeamResult(Team.Impostor), GameOverReason.ImpostorsByKill);
            else
            {
                var role = RoleState.All.FirstOrDefault(r => string.Equals(r.Id, a, StringComparison.OrdinalIgnoreCase));
                if (role == null) { reply($"ERR forcewin nobody has {a}"); return; }
                GameEnd.Win(role);
            }
            reply($"OK forcewin {a}");
        });

        TestBridge.Register("dummy", "[人数=1] 自分の所にダミーを出す (フリープレイかローカルの部屋のホストのみ)", (args, reply) =>
        {
            var client = AmongUsClient.Instance;
            if (client.NetworkMode == NetworkModes.OnlineGame || !client.AmHost || !GameData.Instance || !PlayerControl.LocalPlayer)
            { reply("ERR dummy needs host of a freeplay/local game"); return; }
            int n = int.TryParse(args.Trim(), out int c) ? Math.Clamp(c, 1, 14) : 1;
            int made = 0;
            for (int i = 0; i < n; i++)
            {
                if (GameData.Instance.GetAvailableId() < 0) break;
                Dummy.Spawn(made);
                made++;
            }
            reply($"OK dummy spawned={made}");
        });

        TestBridge.Register("god", "[0|1] 全員の役職を名前の上と会議で見る (自分の画面だけ)", (args, reply) =>
        {
            string a = args.Trim();
            bool on = a == "1" || (a != "0" && !DevGod.On);
            DevGod.Set(on);
            reply($"OK god {(on ? 1 : 0)}");
        });

        TestBridge.Register("pinfo", "<番号> その人の表示の状態 (見えているか・体の絵・位置)", (args, reply) =>
        {
            var p = byte.TryParse(args.Trim(), out byte pid) ? Player(pid) : null;
            if (!p) { reply($"ERR pinfo no player {args}"); return; }
            var body = p.cosmetics ? p.cosmetics.currentBodySprite : null;
            var pos = p.transform.position;
            reply($"OK pinfo {pid} active={p.gameObject.activeInHierarchy} visible={p.Visible} body={(body != null && body.BodySprite ? body.BodySprite.enabled.ToString() : "-")} pos={TestBridge.F(pos.x)},{TestBridge.F(pos.y)},{TestBridge.F(pos.z)} layer={p.gameObject.layer} dummy={p.isDummy} netT={p.NetTransform.enabled} incomplete={(p.Data != null ? p.Data.IsIncomplete.ToString() : "nodata")} client={(p.Data != null ? p.Data.ClientId : -9)}");
        });

        TestBridge.Register("poutfit", "<番号> その人の見た目のデータ", (args, reply) =>
        {
            var d = byte.TryParse(args.Trim(), out byte pid) && GameData.Instance ? GameData.Instance.GetPlayerById(pid) : null;
            if (d == null) { reply($"ERR poutfit no player {args}"); return; }
            foreach (var kv in d.Outfits)
            {
                var o = kv.Value;
                var sb = new System.Text.StringBuilder();
                foreach (var pr in o.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (pr.GetIndexParameters().Length > 0 || pr.Name is "Pointer" or "ObjectClass" or "WasCollected") continue;
                    object v;
                    try { v = pr.GetValue(o); } catch { v = "?"; }
                    sb.Append(' ').Append(pr.Name).Append('=').Append(v);
                }
                reply($"OUTFIT {kv.Key}{sb}");
            }
            var sd = new System.Text.StringBuilder();
            foreach (var pr in d.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (pr.GetIndexParameters().Length > 0 || pr.Name is "Pointer" or "ObjectClass" or "WasCollected") continue;
                object v;
                try { v = pr.GetValue(d); } catch { v = "?"; }
                if (v != null && pr.PropertyType != typeof(string) && !pr.PropertyType.IsPrimitive && !pr.PropertyType.IsEnum && !(v is string)) v = "obj";
                sd.Append(' ').Append(pr.Name).Append('=').Append(v);
            }
            reply($"DATA{sd}");
            reply($"OK poutfit {pid} incomplete={d.IsIncomplete}");
        });

        TestBridge.Register("console", "<0|1|行> 開発者コンソールを開け閉めする / 行を打ち込む (画面の確認用)", (args, reply) =>
        {
            string a = args.Trim();
            if (a == "0" || a == "1") DevConsole.BridgeOpen(a == "1");
            else DevConsole.BridgeType(a);
            reply($"OK console open={DevConsole.IsOpen}");
        });

        TestBridge.Register("chat", "[0|1] 試合中もチャット欄を出す / 隠す (自分の画面だけ。/ を付けたコマンドもここから打てる)", (args, reply) =>
        {
            var hud = Vanilla.Hud;
            if (!hud || !hud.Chat) { reply("ERR chat no hud"); return; }
            string a = args.Trim();
            bool on = a == "1" || (a != "0" && !hud.Chat.gameObject.activeSelf);
            DevChat.SetShown(on);
            reply($"OK chat {(on ? 1 : 0)}");
        });

        TestBridge.Register("chatsend", "<文> チャット欄に文を入れて送信を押したのと同じにする (チャットからのコマンドの確認用)", (args, reply) =>
        {
            var hud = Vanilla.Hud;
            if (!hud || !hud.Chat) { reply("ERR chatsend no hud"); return; }
            hud.Chat.freeChatField.textArea.SetText(args, "");
            hud.Chat.SendChat();
            reply($"OK chatsend left=[{hud.Chat.freeChatField.Text}]");
        });

        TestBridge.Register("chatraw", "<文> 自分の発言として全員へ普通のチャットを送る (コマンドとして拾わない・荒らし対策の確認用)", (args, reply) =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp) { reply("ERR chatraw no player"); return; }
            lp.RpcSendChat(args);
            reply("OK chatraw");
        });

        TestBridge.Register("chattoggle", "チャット欄の窓を開く / 閉じる (チャットのボタンを押したのと同じ)", (_, reply) =>
        {
            var hud = Vanilla.Hud;
            if (!hud || !hud.Chat) { reply("ERR chattoggle no hud"); return; }
            hud.Chat.Toggle();
            reply("OK chattoggle");
        });

        TestBridge.Register("players", "全員の番号・名前・生死・役職", (_, reply) =>
        {
            if (!GameData.Instance) { reply("ERR players no game"); return; }
            foreach (var d in GameData.Instance.AllPlayers)
            {
                if (d == null) continue;
                var role = RoleState.Of(d.PlayerId);
                reply($"PLAYER {d.PlayerId} {d.PlayerName} {(d.IsDead ? "dead" : "alive")} {(d.Role ? d.Role.Role.ToString() : "-")} {role?.Id ?? "-"}");
            }
            reply("OK players");
        });
    }

    private static void GiveRole(string args, Action<string> reply)
    {
        var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var proto = a.Length > 0 ? Registry.Roles.FirstOrDefault(r => string.Equals(r.Id, a[0], StringComparison.OrdinalIgnoreCase)) : null;
        if (proto == null) { reply($"ERR setrole unknown {args} (roles で一覧)"); return; }
        if (!PlayerControl.LocalPlayer || !AmongUsClient.Instance.AmHost) { reply("ERR setrole needs host in game"); return; }
        var target = a.Length > 1 && byte.TryParse(a[1], out byte pid) ? Player(pid) : PlayerControl.LocalPlayer;
        if (!target) { reply($"ERR setrole no player {a[1]}"); return; }
        RoleAssigner.Give(target, proto);
        reply($"OK setrole {proto.Id} to={target.PlayerId} base={target.Data.Role.Role} neutral={RoleState.AnyNeutral}");
    }

    // 死んだ時に本編がタスク欄の先頭に足す「死亡した。…」の行を外す
    private static void RemoveGhostHint(PlayerControl p)
    {
        if (p.myTasks == null) return;
        var tc = TranslationController.Instance;
        string a = tc.GetString(StringNames.GhostDoTasks), b = tc.GetString(StringNames.GhostIgnoreTasks), c = tc.GetString(StringNames.GhostImpostor);
        for (int i = p.myTasks.Count - 1; i >= 0; i--)
        {
            var t = p.myTasks[i];
            var it = t ? t.TryCast<ImportantTextTask>() : null;
            if (it == null || it.Text == null) continue;
            if (it.Text.Contains(a) || it.Text.Contains(b) || it.Text.Contains(c))
            {
                p.myTasks.RemoveAt(i);
                UnityEngine.Object.Destroy(it.gameObject);
            }
        }
    }

    private static PlayerControl Player(byte pid)
        => GameData.Instance ? GameData.Instance.GetPlayerById(pid)?.Object : null;

    // ホスト: その人を生き返らせて全員に知らせる
    internal static void Revive(byte pid)
    {
        ReviveLocal(pid);
        ReviveCall.Send(pid);
    }

    // 生きていた時の役職 (NetworkedPlayerInfo.RoleWhenAlive)。interop の Nullable の読み出しは中身を取り違えるので、
    // 欄の中身 { bool hasValue; RoleTypes value; } を直接読む
    // 位置は初めて使う時に引く (本編の更新で名前が変わって引けなくても、役職を戻さないだけにする)
    private static IntPtr _roleWhenAliveField;
    private static int _hasValueAt = -1, _valueAt = -1;

    internal static unsafe RoleTypes? RoleWhenAlive(NetworkedPlayerInfo data)
    {
        if (_valueAt < 0)
        {
            _roleWhenAliveField = Il2CppInterop.Runtime.IL2CPP.GetIl2CppField(
                Il2CppInterop.Runtime.Il2CppClassPointerStore<NetworkedPlayerInfo>.NativeClassPtr, "RoleWhenAlive");
            if (_roleWhenAliveField == IntPtr.Zero) return null;
            var klass = Il2CppInterop.Runtime.Il2CppClassPointerStore<Il2CppSystem.Nullable<RoleTypes>>.NativeClassPtr;
            var hv = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(klass, "hasValue");
            var v = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(klass, "value");
            if (hv == IntPtr.Zero || v == IntPtr.Zero) return null;
            // 値型の欄の位置は箱の頭 (ポインタ 2 つ分) を含むので引く
            int header = IntPtr.Size * 2;
            _hasValueAt = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(hv) - header;
            _valueAt = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(v) - header;
        }
        byte* field = (byte*)Il2CppInterop.Runtime.IL2CPP.Il2CppObjectBaseToPtrNotNull(data)
                      + Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(_roleWhenAliveField);
        return field[_hasValueAt] != 0 ? (RoleTypes)(*(ushort*)(field + _valueAt)) : null;
    }

    // 本編の Revive は手元の状態だけ戻すので、全員がそれぞれ呼ぶ
    private static void ReviveLocal(byte pid)
    {
        var p = Player(pid);
        if (!p || p.Data == null || !p.Data.IsDead) return;
        p.Revive();
        // 死ぬと本編が幽霊の役職に替えるので、生きていた時の役職に戻す
        var alive = RoleWhenAlive(p.Data);
        // 読めない時や幽霊側の役職が入っている時 (フリープレイなど) は、陣営の基本の役職に戻す
        if (!alive.HasValue || RoleManager.IsGhostRole(alive.Value))
            alive = p.Data.Role && p.Data.Role.TeamType == RoleTeamTypes.Impostor ? RoleTypes.Impostor : RoleTypes.Crewmate;
        RoleManager.Instance.SetRole(p, alive.Value);
        Plugin.Logger.LogInfo($"revive {pid}: alive role={alive?.ToString() ?? "-"} now={p.Data.Role?.Role}");
        if (p.AmOwner) RemoveGhostHint(p);
        // 倒れた体が残ると通報できてしまうので片付ける
        foreach (var body in UnityEngine.Object.FindObjectsOfType<DeadBody>())
            if (body && body.ParentId == pid) UnityEngine.Object.Destroy(body.gameObject);
        if (DevGod.On) DevGod.Refresh();
    }
}
