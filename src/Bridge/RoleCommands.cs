using AmongUs.GameOptions;
using System.Linq;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles;

namespace MoreRolesPlus.Bridge;

// 役職・設定の確認用コマンド
internal static class RoleCommands
{
    public static void Register()
    {
        TestBridge.Register("roles", "登録された役職と、今の割り当て", (_, reply) =>
        {
            string reg = string.Join(" ", Registry.Roles.Select(r => $"{r.Id}({r.Team},{r.Chance.Value}%x{r.Count.Value})"));
            string now = string.Join(" ", RoleState.All.Select(r => $"{r.PlayerId}={r.Id}"));
            string extra = "";
            var lp = PlayerControl.LocalPlayer;
            if (lp && ShipStatus.Instance && lp.Data != null)
                extra = $" vision={ShipStatus.Instance.CalculateLightRadius(lp.Data):0.###} killcd={GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.KillCooldown):0.#}/{GameManager.Instance.LogicOptions.GetKillCooldown():0.#}";
            reply($"OK roles reg=[{reg}] assigned=[{now}] local={RoleState.Local?.Id ?? "-"} fp={Registry.Fingerprint:X8}{extra}");
        });

        TestBridge.Register("endcheck", "[番号...] 生き残りの数で勝者が決まるかを見るだけ (終わらせない)。番号の人をキル役として数える", (args, reply) =>
        {
            if (!GameData.Instance) { reply("ERR endcheck not in game"); return; }
            GameEnd.TestKillers.Clear();
            foreach (var t in args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
                if (byte.TryParse(t, out byte pid)) GameEnd.TestKillers.Add(pid);
            var r = GameEnd.Decide(out var reason, out string info, true);
            GameEnd.TestKillers.Clear();
            string res = r == null ? "none" : $"team={r.Team?.ToString() ?? "-"} role={r.Role?.Id ?? (r.Winners.Count > 0 && r.Team == null ? "test" : "-")} winners=[{string.Join(",", r.Winners)}] reason={reason}";
            reply($"OK endcheck {info} -> {res}");
        });

        TestBridge.Register("winner", "最後に配られた試合の結果", (_, reply) =>
        {
            var r = GameEnd.Last;
            reply(r == null ? "OK winner none" : $"OK winner team={r.Team?.ToString() ?? "-"} role={r.Role?.Id ?? "-"} winners=[{string.Join(",", r.Winners)}] localWon={GameEnd.LocalWon} cached={EndGameResult.CachedWinners?.Count ?? -1}");
        });

        TestBridge.Register("gameflags", "試合の終わりに関わる本編の状態と、止めた終わりの数", (_, reply) =>
        {
            var gm = GameManager.Instance;
            reply($"OK gameflags check={(gm ? gm.ShouldCheckForGameEnd : false)} started={(gm ? gm.GameHasStarted : false)} state={AmongUsClient.Instance.GameState} blocked={GameEnd.Blocked} total={GameData.Instance?.TotalTasks} done={GameData.Instance?.CompletedTasks}");
        });

        TestBridge.Register("meeting", "自分が緊急会議を開く", (_, reply) =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp || !ShipStatus.Instance) { reply("ERR meeting not in game"); return; }
            lp.CmdReportDeadBody(null);
            reply("OK meeting");
        });

        TestBridge.Register("meetingtags", "会議の名札ごとの役職名の有無と位置", (_, reply) =>
        {
            var hud = MeetingHud.Instance;
            if (!hud) { reply("ERR meetingtags no meeting"); return; }
            var sb = new System.Text.StringBuilder();
            foreach (var pva in hud.playerStates)
            {
                var tag = pva.NameText.transform.parent.Find("MrpMeetingRole");
                var n = pva.NameText.transform;
                sb.Append($" [{pva.name} name={n.localPosition.x:0.##},{n.localPosition.y:0.##} s={n.localScale.x:0.##} tag={(tag ? $"{tag.localPosition.x:0.##},{tag.localPosition.y:0.##} act={tag.gameObject.activeInHierarchy}" : "-")}]");
            }
            reply($"OK meetingtags{sb}");
        });

        TestBridge.Register("vote", "<番号|skip> 会議で投票する", (args, reply) =>
        {
            var hud = MeetingHud.Instance;
            if (!hud) { reply("ERR vote no meeting"); return; }
            byte target = args.Trim() == "skip" ? (byte)253 : byte.Parse(args.Trim());
            hud.CmdCastVote(PlayerControl.LocalPlayer.PlayerId, target);
            reply($"OK vote {target}");
        });

        TestBridge.Register("proceed", "会議の投票結果の画面で「進む」を押す", (_, reply) =>
        {
            var hud = MeetingHud.Instance;
            if (!hud) { reply("ERR proceed no meeting"); return; }
            if (!hud.ProceedButton || !hud.ProceedButton.gameObject.activeInHierarchy) { reply("ERR proceed button not shown"); return; }
            hud.ProceedButton.OnClick.Invoke();
            reply("OK proceed");
        });

        TestBridge.Register("killbtn", "[0|1] キルボタンの状態 (表示・狙い・待ち時間)。1 で押す", (args, reply) =>
        {
            var hud = HudManager.InstanceExists ? HudManager.Instance : null;
            var lp = PlayerControl.LocalPlayer;
            if (!hud || !hud.KillButton || !lp) { reply("ERR killbtn no hud"); return; }
            var b = hud.KillButton;
            string state() => $"shown={b.isActiveAndEnabled} target={(b.currentTarget ? b.currentTarget.PlayerId : -1)} cooling={b.isCoolingDown} timer={lp.killTimer:0.#} canUse={lp.Data?.Role?.CanUseKillButton}";
            if (args.Trim() == "1")
            {
                string before = state();
                b.DoClick();
                reply($"OK killbtn pressed before=[{before}]");
            }
            else reply($"OK killbtn {state()}");
        });

        TestBridge.Register("exile", "<番号> その人に本編の追放の処理 (Exiled) を走らせる。自分の端末だけ", (args, reply) =>
        {
            var p = byte.TryParse(args.Trim(), out byte pid) && GameData.Instance ? GameData.Instance.GetPlayerById(pid)?.Object : null;
            if (!p) { reply($"ERR exile no player {args}"); return; }
            p.Exiled();
            reply($"OK exile {pid}");
        });

        TestBridge.Register("tasks", "自分のタスク欄の中身", (_, reply) =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp || lp.myTasks == null) { reply("ERR tasks"); return; }
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < lp.myTasks.Count && i < 4; i++)
            {
                var t = lp.myTasks[i];
                sb.Append('[').Append(t ? t.name : "null");
                var it = t ? t.TryCast<ImportantTextTask>() : null;
                if (it != null) sb.Append(':').Append(it.Text.Replace('\n', '/'));
                sb.Append(']');
            }
            reply($"OK tasks n={lp.myTasks.Count} total={GameData.Instance?.TotalTasks} done={GameData.Instance?.CompletedTasks} {sb}");
        });

        TestBridge.Register("opt", "<key> [番号] 設定項目を見る / 番号で変える (例 opt Lighter.Chance 10)", (args, reply) =>
        {
            var a = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 0)
            {
                reply("OK opts " + string.Join(" ", Registry.All.Select(o => $"{o.Key}={o.Display()}")));
                return;
            }
            if (!Registry.TryGet(a[0], out var opt)) { reply($"ERR opt unknown key {a[0]}"); return; }
            if (a.Length > 1 && int.TryParse(a[1], out int i)) opt.SetIndex(i);
            reply($"OK opt {opt.Key} index={opt.Index}/{opt.Count} value={opt.Display()}");
        });

        TestBridge.Register("editsettings", "ロビーでゲーム設定の画面を開く (ホストのみ)", (_, reply) =>
        {
            if (!GameStartManager.InstanceExists) { reply("ERR editsettings no lobby"); return; }
            GameStartManager.Instance.ClickEdit();
            reply("OK editsettings");
        });

        TestBridge.Register("settab", "<番号> 開いている設定画面のタブを切り替える (1=本編のゲーム設定・3〜=MRP)", (args, reply) =>
        {
            if (!GameSettingMenu.Instance) { reply("ERR settab menu not open"); return; }
            if (!int.TryParse(args.Trim(), out int n)) { reply("ERR settab number"); return; }
            GameSettingMenu.Instance.ChangeTab(n, false);
            reply($"OK settab {n}");
        });

        TestBridge.Register("pressrow", "<MRPタブ番号 0〜> <行> <+1|-1> MRP のタブの行のボタンを押す", (args, reply) =>
        {
            var a = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (a.Length < 3 || !int.TryParse(a[0], out int p) || !int.TryParse(a[1], out int r) || !int.TryParse(a[2], out int d))
            { reply("ERR pressrow args"); return; }
            reply($"OK pressrow {SettingsMenu.PressRow(p, r, d)}");
        });

        TestBridge.Register("clicktab", "<MRPタブ番号 0〜> MRP のタブのボタンを押す", (args, reply) =>
        {
            if (!int.TryParse(args.Trim(), out int p)) { reply("ERR clicktab number"); return; }
            reply($"OK clicktab {SettingsMenu.ClickTabButton(p)}");
        });

        TestBridge.Register("viewsettings", "[タブ 0=概要 1=役職 2=MRP] [件数] ロビーの設定を見る画面を開いてタブのボタンを押し、並んだ文字を返す (客でも可)", (args, reply) =>
        {
            var a = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            int tab = a.Length > 0 && int.TryParse(a[0], out int t) ? t : 2;
            int max = a.Length > 1 && int.TryParse(a[1], out int m) ? m : 12;
            reply($"OK viewsettings {LobbyView.Open(tab, max)}");
        });

        TestBridge.Register("rolemenu", "[q <語> | team <-1|0|1|2> | only | scroll <0〜1> | open <役職Id> | press <役職Id> <count|chance> <+1|-1>] 設定画面の役職タブを操作して、見えている行を返す", (args, reply) =>
            reply("OK rolemenu " + RoleMenu.Command(args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))));

        TestBridge.Register("uitree", "<chat|menu|page|GameObject のパス> [深さ=3] 画面の部品の階層 (位置・大きさ・部品の種類・有効か)", (args, reply) =>
        {
            var a = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 0) { reply("ERR uitree target"); return; }
            int depth = a.Length > 1 && int.TryParse(a[1], out int d) ? d : 3;
            UnityEngine.Transform root = a[0] switch
            {
                "chat" => HudManager.InstanceExists && HudManager.Instance.Chat ? HudManager.Instance.Chat.freeChatField?.transform : null,
                "menu" => GameSettingMenu.Instance ? GameSettingMenu.Instance.transform : null,
                "page" => SettingsMenu.OpenPage(),
                _ => UnityEngine.GameObject.Find(a[0])?.transform,
            };
            if (!root) { reply($"ERR uitree not found: {a[0]}"); return; }
            var sb = new System.Text.StringBuilder();
            void Walk(UnityEngine.Transform t, int level)
            {
                var p = t.localPosition; var s = t.localScale;
                sb.Append('\n').Append(' ', level * 2).Append(t.gameObject.activeSelf ? "" : "(off) ").Append(t.name)
                  .Append($" pos=({p.x:0.###},{p.y:0.###},{p.z:0.###}) scale=({s.x:0.###},{s.y:0.###})");
                foreach (var c in t.GetComponents<UnityEngine.Component>())
                {
                    if (c == null || c.TryCast<UnityEngine.Transform>() != null) continue;
                    sb.Append(' ').Append(c.GetIl2CppType().Name);
                    var sr = c.TryCast<UnityEngine.SpriteRenderer>();
                    if (sr != null) sb.Append($"[size={sr.size.x:0.##}x{sr.size.y:0.##} sprite={(sr.sprite ? sr.sprite.name : "-")} order={sr.sortingOrder}]");
                    var tmp = c.TryCast<TMPro.TextMeshPro>();
                    if (tmp != null) sb.Append($"[\"{tmp.text}\" fs={tmp.fontSize:0.##}]");
                    var bc = c.TryCast<UnityEngine.BoxCollider2D>();
                    if (bc != null) sb.Append($"[box={bc.size.x:0.##}x{bc.size.y:0.##}]");
                }
                if (level >= depth) return;
                for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), level + 1);
            }
            Walk(root, 0);
            reply("OK uitree" + sb);
        });

        TestBridge.Register("closesettings", "設定画面を閉じる", (_, reply) =>
        {
            if (!GameSettingMenu.Instance) { reply("ERR closesettings menu not open"); return; }
            GameSettingMenu.Instance.Close();
            reply("OK closesettings");
        });
    }
}
