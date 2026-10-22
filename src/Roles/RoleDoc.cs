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
    private const string LabelColor = "#A8A8B4";

    internal static bool Has(Assignable a) => Tr.Has(a.Id + ".Summary");

    internal static string Compose(Assignable a)
    {
        string id = a.Id;
        var sb = new StringBuilder(new Text(id + ".Summary").ToString());
        if (Tr.Has(id + ".WinCond"))
            Line(sb).Append("<color=").Append(LabelColor).Append('>').Append(new Text("RoleDoc.WinCond").ToString())
                .Append("</color>  ").Append(new Text(id + ".WinCond").ToString());
        var sections = a.HelpSections;
        if (sections != null)
            foreach (var key in sections)
            {
                Line(sb).Append("<b><color=").Append(a.Color).Append(">■ ").Append(new Text(key).ToString()).Append("</color></b>");
                if (Tr.Has(key + ".Help")) sb.Append('\n').Append(new Text(key + ".Help").ToString());
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
