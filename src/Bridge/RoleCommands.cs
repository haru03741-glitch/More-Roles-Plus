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

        TestBridge.Register("giverole", "<役職Id> 自分にその役職を付ける (ホスト / フリープレイ。土台の役職も合わせる)", (args, reply) =>
        {
            var proto = Registry.Roles.FirstOrDefault(r => string.Equals(r.Id, args.Trim(), System.StringComparison.OrdinalIgnoreCase));
            var lp = PlayerControl.LocalPlayer;
            if (proto == null) { reply($"ERR giverole unknown {args}"); return; }
            if (!lp || !AmongUsClient.Instance.AmHost) { reply("ERR giverole needs host in game"); return; }
            RoleAssigner.GiveLocal(proto);
            reply($"OK giverole {proto.Id} base={lp.Data.Role.Role}");
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

        TestBridge.Register("closesettings", "設定画面を閉じる", (_, reply) =>
        {
            if (!GameSettingMenu.Instance) { reply("ERR closesettings menu not open"); return; }
            GameSettingMenu.Instance.Close();
            reply("OK closesettings");
        });
    }
}
