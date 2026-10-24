using System.Collections.Generic;
using System.Text;

namespace MoreRolesPlus.Roles;

// 役職の長い説明を文のファイルの部品から組み立てる。試合の情報 (右上の「?」) のカードと /r のチャットの両方に出すので、
// 大きさの指定 (<size>) は使わず <b>・<i>・<color> だけにする。部品の鍵 (<Id> は役職のクラス名):
//   <Id>.Summary        「〜できる<陣営>です。」の 1 文 (これが無ければ組み立てない)
//   <Id>.WinCond        普通と違う勝ち方の時だけ
//   <Id>.<節>.Help      節の本文。節の見出しは <Id>.<節> の文 (ボタンの名前の鍵をそのまま使う)。並びは Assignable.HelpSections
//   <Id>.Tips           立ち回り・ほかの役職との関わり
//   <Id>.Quote          最後の決め台詞
// 設定の値は試合の情報が下に並べるので、本文には書かない
internal static class RoleDoc
{
    internal const string LabelColor = "#A8A8B4";

    // チャットの吹き出しは白地なので、字を濃く・補足を濃いめの灰色に (カードの灰色は白地では薄すぎる)
    private const string ChatInk = "#2E2E38";
    private const string ChatMuted = "#6C6C78";

    // Assignable.Origin の鍵 → 表に出す mod の名前
    private static readonly Dictionary<string, string> Origins = new()
    {
        ["TOR"] = "<color=#FF0000>TheOtherRoles</color>",
        ["Jester Mod"] = "<color=#EC62A5>Jester</color>",
    };

    // 説明の先頭に置く起源の 1 行 (改行まで含む)。起源の無い役職は空
    internal static string OriginLine(Assignable a, string label = LabelColor)
    {
        string origin = a.Origin;
        if (string.IsNullOrEmpty(origin)) return string.Empty;
        var sb = new StringBuilder("<color=").Append(label).Append(">From:</color> ");
        string[] keys = origin.Split(',');
        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i].Trim();
            if (i > 0) sb.Append(", ");
            sb.Append(Origins.TryGetValue(key, out string shown) ? shown : key);
        }
        return sb.Append('\n').ToString();
    }

    internal static bool Has(Assignable a) => Tr.Has(a.Id + ".Summary");

    // 要約と勝利条件 (試合の情報のカードでは、この下に節を 1 行ずつ並べる)
    internal static string Head(Assignable a, string label = LabelColor)
    {
        string id = a.Id;
        var sb = new StringBuilder(new Text(id + ".Summary").ToString());
        if (Tr.Has(id + ".WinCond"))
            Line(sb).Append("<color=").Append(label).Append('>').Append(new Text("RoleDoc.WinCond").ToString())
                .Append("</color>  ").Append(new Text(id + ".WinCond").ToString());
        return sb.ToString();
    }

    internal static string Compose(Assignable a)
    {
        string id = a.Id;
        var sb = new StringBuilder(Head(a));
        var sections = a.HelpSections;
        if (sections != null)
            foreach (var s in sections)
            {
                Line(sb).Append("<b><color=").Append(a.Color).Append(">■ ").Append(new Text(s.Key).ToString()).Append("</color></b>");
                if (Tr.Has(s.Key + ".Help")) sb.Append('\n').Append(new Text(s.Key + ".Help").ToString());
            }
        if (Tr.Has(id + ".Tips"))
            Line(sb).Append("<color=").Append(LabelColor).Append('>').Append(new Text("RoleDoc.Tips").ToString())
                .Append("</color>  ").Append(new Text(id + ".Tips").ToString());
        if (Tr.Has(id + ".Quote"))
            Line(sb).Append("<i><color=").Append(LabelColor).Append('>').Append(new Text(id + ".Quote").ToString()).Append("</color></i>");
        return sb.ToString();
    }

    // チャット (/r・/m) に出す説明 = 試合の情報のカードと同じ中身: 節は「ボタンの絵 + 名前 + 短い 1 行」で、
    // 押すと (Menu.ChatRoleDoc) その節だけ詳しい文に開く。open = 開いている節の鍵 (節の鍵か "Tips")
    internal static string Chat(Assignable a, ICollection<string> open = null)
    {
        // 本編の役職の設定の行は、設定画面か試合の情報を開くまで作られない
        Builtin.VanillaRoles.EnsureSettings();
        string opts = Menu.MatchInfoExtension.OptionLine(a);
        var sb = new StringBuilder();
        if (!Has(a))
        {
            sb.Append(a.Help.ToString());
            if (opts.Length > 0) sb.Append('\n').Append("<size=85%><color=").Append(ChatMuted).Append('>').Append(opts).Append("</color></size>");
            return sb.ToString();
        }

        string id = a.Id;
        string color = ChatColor(a.Color);
        sb.Append(OriginLine(a, ChatMuted)).Append(Head(a, ChatMuted));
        var sections = a.HelpSections;
        if (sections != null)
            foreach (var s in sections)
            {
                string icon = Menu.ChatIcons.Tag(a.SectionIcon(s));
                string name = (icon.Length > 0 ? icon + " " : "■ ") + new Text(s.Key);
                string help = Tr.Has(s.Key + ".Help") ? new Text(s.Key + ".Help").ToString() : null;
                string brief = Tr.Has(s.Key + ".Short") ? new Text(s.Key + ".Short").ToString() : null;
                ChatRow(sb, s.Key, $"<b><color={color}>{name}</color></b>", brief, help, open);
            }
        if (Tr.Has(id + ".Tips"))
            ChatRow(sb, "Tips", $"<b><color={ChatMuted}>{new Text("RoleDoc.Tips")}</color></b>", null, new Text(id + ".Tips").ToString(), open);
        if (Tr.Has(id + ".Quote"))
            Line(sb).Append("<i><color=").Append(ChatMuted).Append('>').Append(new Text(id + ".Quote").ToString()).Append("</color></i>");
        // 決め台詞は灰色の斜体なので、空の行を挟んで設定の値と分ける
        if (opts.Length > 0) sb.Append("\n\n").Append("<size=85%><color=").Append(ChatMuted).Append('>').Append(opts).Append("</color></size>");
        string body = sb.ToString();
        if (open == null) Menu.ChatRoleDoc.Pending(a, body);
        return body;
    }

    // 1 節の行。詳しい文がある節は行全体を押せる (<link> の鍵 = 節の鍵)。閉じている時は短い文、開いている時は詳しい文
    private static void ChatRow(StringBuilder sb, string key, string name, string brief, string help, ICollection<string> open)
    {
        bool isOpen = help != null && open != null && open.Contains(key);
        Line(sb);
        if (help != null) sb.Append("<link=\"").Append(key).Append("\">");
        sb.Append(name);
        if (help != null) sb.Append(" <color=").Append(ChatMuted).Append('>').Append(isOpen ? "-" : "+").Append("</color>");
        if (!isOpen && brief != null) sb.Append("  <size=90%><color=").Append(ChatMuted).Append('>').Append(brief).Append("</color></size>");
        if (isOpen) sb.Append('\n').Append(help);
        if (help != null) sb.Append("</link>");
    }

    // チャットの見出しの 1 行: 役職名 (役職の色を白地で読める濃さに) と陣営など
    internal static string ChatTitle(Assignable a, string tail) =>
        $"<size=115%><b><color={ChatColor(a.Color)}>{a.Name}</color></b></size>" + (tail != null ? $" <color={ChatMuted}>({tail})</color>" : "");

    // チャットの字全体を濃い色で包む (本編のお知らせの吹き出しは赤字なので)
    internal static string ChatInked(string text) => $"<color={ChatInk}>{text}</color>";

    // 役職の色が明るすぎる (白地で読めない) 時は、色味を残して明るさだけ落とす
    internal static string ChatColor(string hex)
    {
        if (hex == null || hex.Length != 7 || hex[0] != '#') return hex;
        try
        {
            int r = System.Convert.ToInt32(hex.Substring(1, 2), 16), g = System.Convert.ToInt32(hex.Substring(3, 2), 16), b = System.Convert.ToInt32(hex.Substring(5, 2), 16);
            float lum = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
            const float MaxLum = 0.5f;
            if (lum <= MaxLum) return hex;
            float k = MaxLum / lum;
            return $"#{(int)(r * k):X2}{(int)(g * k):X2}{(int)(b * k):X2}";
        }
        catch (System.FormatException) { return hex; }
    }

    private static StringBuilder Line(StringBuilder sb) => sb.Append('\n');
}
