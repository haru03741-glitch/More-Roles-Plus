using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 役職の説明の先頭の「From: 〜」に出す mod の名前と絵。Resources/Origins/<鍵>.txt と <鍵>.png から読む
// (鍵 = 役職のコードの Origin => "鍵")。どちらか片方だけでもよい。
//   <鍵>.txt   name: 出す名前 / color: #RRGGBB (どちらも省いてよい)。「#」で始まる行は書き置き。
//              name: も color: も無ければ、ファイルの字をそのまま出す (<color> などの飾りも使える)
//   <鍵>.png   名前の前に字の高さで出す絵 (横長のロゴでもよい)
// ファイルの無い鍵は鍵の字をそのまま出す。1 回読んだら起動中は持ち続ける
internal static class Origins
{
    private const string Prefix = "MoreRolesPlus.Resources.Origins.";
    private const float ImagePixelsPerUnit = 100f;

    private sealed class Entry
    {
        public string Text;     // 名前 (色付き)。無ければ null
        public Sprite Image;    // 絵。無ければ null
    }

    private static Dictionary<string, Entry> _all;

    // 鍵 → 出す字 (絵は <sprite> で字の中に)
    public static string Shown(string key)
    {
        var all = Load();
        if (!all.TryGetValue(key, out var e)) return key;
        string img = e.Image ? Menu.ChatIcons.Tag(e.Image, 1.15f) : "";
        string text = e.Text ?? (img.Length > 0 ? "" : key);
        return img.Length > 0 && text.Length > 0 ? img + " " + text : img + text;
    }

    // 入っている鍵の一覧 (確かめ用)
    public static IEnumerable<string> Keys => Load().Keys;

    private static Dictionary<string, Entry> Load()
    {
        if (_all != null) return _all;
        var all = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var asm = Assembly.GetExecutingAssembly();
        foreach (string res in asm.GetManifestResourceNames())
        {
            if (!res.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            string file = res.Substring(Prefix.Length);
            string ext = Path.GetExtension(file).ToLowerInvariant();
            string key = Path.GetFileNameWithoutExtension(file);
            if (!all.TryGetValue(key, out var e)) all[key] = e = new Entry();
            try
            {
                using var s = asm.GetManifestResourceStream(res);
                if (s == null) continue;
                if (ext == ".txt")
                {
                    using var r = new StreamReader(s, System.Text.Encoding.UTF8);
                    e.Text = ParseText(r.ReadToEnd());
                }
                else if (ext == ".png")
                {
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    e.Image = ButtonIcons.FromBytes(ms.ToArray(), "origin_" + key, ImagePixelsPerUnit, new Vector2(0.5f, 0.5f));
                    if (!e.Image) Plugin.Logger.LogWarning($"origin image unreadable: {file}");
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"origin {file}: {ex.Message}"); }
        }
        return _all = all;
    }

    private static string ParseText(string body)
    {
        string name = null, color = null, gradient = null;
        var plain = new List<string>();
        foreach (string raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            int colon = line.IndexOf(':');
            string head = colon > 0 ? line.Substring(0, colon).Trim().ToLowerInvariant() : "";
            string value = colon > 0 ? line.Substring(colon + 1).Trim() : "";
            if (head == "name") name = value;
            else if (head == "color") color = value;
            else if (head == "gradient") gradient = value;
            else plain.Add(line);
        }
        if (name == null && color == null && gradient == null) return plain.Count > 0 ? string.Join(" ", plain) : null;
        name ??= string.Join(" ", plain);
        if (name.Length == 0) return null;
        var grad = Colors(gradient);
        if (grad.Count >= 2) return Gradient(name.Replace("|", ""), grad);
        // gradient: に色が 1 つだけなら、その 1 色で塗る
        var cols = grad.Count == 1 && Colors(color).Count == 0 ? grad : Colors(color);
        if (cols.Count == 0) return name.Replace("|", "");
        // 「|」で区切った名前は区切りごと・区切りが無ければ 1 文字ずつ (色が足りなければ最後の色のまま)
        var parts = name.Contains('|') ? name.Split('|') : Letters(name);
        if (cols.Count == 1) return $"<color={cols[0]}>{name.Replace("|", "")}</color>";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            string c = cols[Math.Min(i, cols.Count - 1)];
            if (parts[i].Trim().Length == 0) sb.Append(parts[i]);
            else sb.Append("<color=").Append(c).Append('>').Append(parts[i]).Append("</color>");
        }
        return sb.ToString();
    }

    // 「#FFA500 #FF0000, 00FF00」のような並びから色を取り出す (# は省いてよい)
    private static List<string> Colors(string value)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(value)) return list;
        foreach (string raw in value.Split(new[] { ' ', ',', '\t', '　' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string c = raw.StartsWith("#", StringComparison.Ordinal) ? raw : "#" + raw;
            if (IsColor(c)) list.Add(c);
        }
        return list;
    }

    // 文字ごとに分ける (サロゲートペアの字も 1 文字として)
    private static string[] Letters(string s)
    {
        var list = new List<string>();
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add((string)e.Current);
        return list.ToArray();
    }

    // "#RRGGBB" / "#RGB" (# は無くてもよい) を色に。ColorUtility.TryParseHtmlString は Android 版のゲームに無いので自前で読む
    private static bool TryParseHex(string h, out Color c)
    {
        c = default;
        if (string.IsNullOrEmpty(h)) return false;
        h = h.Trim().TrimStart('#');
        if (h.Length == 3) h = new string(new[] { h[0], h[0], h[1], h[1], h[2], h[2] });
        if (h.Length != 6 || !uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out uint v)) return false;
        c = new Color(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, 1f);
        return true;
    }

    // 1 文字ずつ、並べた色の間を滑らかに変える
    private static string Gradient(string name, List<string> stops)
    {
        var letters = Letters(name);
        var cs = new List<Color>();
        foreach (var h in stops)
            if (TryParseHex(h, out var c)) cs.Add(c);
        if (cs.Count < 2 || letters.Length == 0) return name;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < letters.Length; i++)
        {
            float t = letters.Length == 1 ? 0f : (float)i / (letters.Length - 1) * (cs.Count - 1);
            int k = Math.Min((int)t, cs.Count - 2);
            var c = Color.Lerp(cs[k], cs[k + 1], t - k);
            if (letters[i].Trim().Length == 0) sb.Append(letters[i]);
            else sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(c)).Append('>').Append(letters[i]).Append("</color>");
        }
        return sb.ToString();
    }

    private static bool IsColor(string s) => s.Length is 7 or 9 && s[0] == '#' && IsHex(s.Substring(1));

    private static bool IsHex(string s)
    {
        foreach (char ch in s) if (!Uri.IsHexDigit(ch)) return false;
        return true;
    }
}
