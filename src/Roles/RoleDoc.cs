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

    // Assignable.Origin の鍵 → 表に出す mod の名前
    private static readonly Dictionary<string, string> Origins = new()
    {
        ["TOR"] = "<color=#FF0000>TheOtherRoles</color>",
        ["Jester Mod"] = "<color=#EC62A5>Jester</color>",
    };

    // 説明の先頭に置く起源の 1 行 (改行まで含む)。起源の無い役職は空
    internal static string OriginLine(Assignable a)
    {
        string origin = a.Origin;
        if (string.IsNullOrEmpty(origin)) return string.Empty;
        var sb = new StringBuilder("<color=").Append(LabelColor).Append(">From:</color> ");
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
    internal static string Head(Assignable a)
    {
        string id = a.Id;
        var sb = new StringBuilder(new Text(id + ".Summary").ToString());
        if (Tr.Has(id + ".WinCond"))
            Line(sb).Append("<color=").Append(LabelColor).Append('>').Append(new Text("RoleDoc.WinCond").ToString())
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

    private static StringBuilder Line(StringBuilder sb) => sb.Append('\n');
}
