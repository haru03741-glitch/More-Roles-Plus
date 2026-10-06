using System;
using System.Collections.Generic;
using System.Text;

namespace MoreRolesPlus.Options;

// 設定画面の検索の一致判定。
// - 比べる前に、全角英数を半角に・カタカナをひらがなに・英字を小文字にそろえ、空白と中黒を除く
//   (「きる」でも「キル」でも「ＫＩＬＬ」でも同じに当たる。漢字の読みまでは見ない)。
// - 空白で区切った語はすべて含まれている時だけ当たる (AND)。
// 名前は起動時に一度だけそろえて持っておき、打つたびに作り直さない。
internal static class RoleSearch
{
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char c0 in s)
        {
            char c = c0;
            if (c >= '！' && c <= '～') c = (char)(c - 0xFEE0);   // 全角英数記号 → 半角
            else if (c >= 'ァ' && c <= 'ヶ') c = (char)(c - 0x60); // カタカナ → ひらがな
            if (c == ' ' || c == '　' || c == '・' || c == '･' || c == '\t') continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    // 検索語を語ごとに分けてそろえる。空なら 0 個
    public static string[] Tokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<string>();
        var parts = query.Split(new[] { ' ', '　', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            string n = Normalize(p);
            if (n.Length > 0) list.Add(n);
        }
        return list.ToArray();
    }

    // 日本語名と英語名をまとめて 1 つの探す対象にする
    public static string Haystack(Text t) => Normalize(t.Ja) + "\n" + Normalize(t.En);

    public static bool AnyIn(string[] tokens, string hay)
    {
        foreach (var t in tokens)
            if (hay.Contains(t, StringComparison.Ordinal)) return true;
        return false;
    }
}
