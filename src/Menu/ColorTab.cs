namespace MoreRolesPlus.Menu;

// 確認用: 本編の着せ替え画面 (ロビーのノートパソコン) を開く・タブを切り替える・本編の色を押す・閉じる
internal static class ColorTab
{
    internal static string Simulate(string args)
    {
        var menu = PlayerCustomizationMenu.Instance;
        if (args == "use")
        {
            // 人が使うのと同じ道 (右下の使うボタン。近くのノートパソコンが狙われている時だけ開く)
            var hud = Vanilla.Hud;
            if (!hud || !hud.UseButton) return "ERR custmenu no hud";
            hud.UseButton.DoClick();
            return "OK custmenu use pressed";
        }
        if (!menu) return "ERR custmenu not open";
        if (args == "close") { menu.Close(true); return "OK custmenu closed"; }
        if (args.StartsWith("tab ") && int.TryParse(args[4..], out int n))
        {
            if (n < 0 || n >= menu.Tabs.Length) return $"ERR custmenu tab 0..{menu.Tabs.Length - 1}";
            menu.OpenTab(menu.Tabs[n].Tab);
        }
        else if (args.StartsWith("chip ") && int.TryParse(args[5..], out int id))
        {
            var tab = menu.Tabs[0].Tab ? menu.Tabs[0].Tab.TryCast<PlayerTab>() : null;
            if (!tab) return "ERR custmenu no color tab";
            tab.SelectColor(id);
        }
        else if (args.Length > 0) return "ERR custmenu [use | tab <番号> | chip <色番号> | close]";
        var sb = new System.Text.StringBuilder("OK custmenu tabs=");
        for (int i = 0; i < menu.Tabs.Length; i++)
        {
            var t = menu.Tabs[i];
            sb.Append(i).Append(':').Append(t.Tab ? t.Tab.GetType().Name + "/" + t.Tab.name : "-").Append(t.Tab && t.Tab.gameObject.activeSelf ? "*" : "").Append(' ');
        }
        sb.Append("item=[").Append(menu.itemName ? menu.itemName.text : "").Append(']');
        return sb.ToString();
    }
}
