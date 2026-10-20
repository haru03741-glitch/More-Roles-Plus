using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace MoreRolesPlus;

// MRP の文の表。組み込み (Resources/Lang/<言語>.dat) に、遊ぶ人のファイル (LangFiles) の書き換えを重ねたもの。
// 引くのは管理側の Dictionary だけ (ゲーム側の呼び出しは無い)。表を作り直すのは言語が変わった時と /lang の時だけ
internal static class Tr
{
    private static readonly DatFile BuiltinJa = ReadBuiltin("Japanese") ?? DatFile.Empty;
    private static readonly DatFile BuiltinEn = ReadBuiltin("English") ?? DatFile.Empty;

    private static Dictionary<string, string> _cur = BuiltinEn.Values;

    public static bool Loaded { get; private set; }
    public static int Edited { get; private set; } // 遊ぶ人のファイルで書き換えた鍵の数

    // 鍵の文。今の言語に無ければ英語、それも無ければ鍵そのもの (書き忘れが画面で分かるように)
    public static string Get(string key)
    {
        if (key == null) return "";
        if (_cur.TryGetValue(key, out var s)) return s;
        return BuiltinEn.Values.TryGetValue(key, out s) ? s : key;
    }

    public static string Builtin(string key, bool japanese)
    {
        if (key == null) return "";
        if (japanese && BuiltinJa.Values.TryGetValue(key, out var s)) return s;
        return BuiltinEn.Values.TryGetValue(key, out s) ? s : key;
    }

    public static bool Has(string key) => key != null && BuiltinEn.Values.ContainsKey(key);

    public static void Load(SupportedLangs lang)
    {
        string name = lang.ToString();
        var defaults = Defaults(lang);
        var cur = new Dictionary<string, string>(defaults.Values);
        Edited = LangFiles.ApplyMod(name, defaults, cur);
        _cur = cur;
        Loaded = true;
        Plugin.Logger.LogInfo($"lang: {name} keys={cur.Count} edited={Edited}");
    }

    // その言語の組み込みの文。日本語と英語以外は、組み込みがあればそれに英語の足りない分を足し、無ければ英語
    private static DatFile Defaults(SupportedLangs lang)
    {
        if (lang == SupportedLangs.English) return BuiltinEn;
        var own = lang == SupportedLangs.Japanese ? BuiltinJa : ReadBuiltin(lang.ToString());
        return own == null ? BuiltinEn : own.WithMissingFrom(BuiltinEn);
    }

    private static DatFile ReadBuiltin(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MoreRolesPlus.Resources.Lang." + name + ".dat");
        if (s == null) return null;
        using var r = new StreamReader(s);
        return DatFile.Parse(r.ReadToEnd());
    }
}

// 「鍵: 文」の行を並べたファイル。# で始まる行と空の行はメモ。文の中の \n は改行。
// : は半角でも全角でもよい (最初の 1 個で分ける)
internal sealed class DatFile
{
    public static readonly DatFile Empty = new(new List<string>());

    public readonly List<string> Lines;                       // 元の行 (メモも含む)
    public readonly Dictionary<string, string> Values = new(); // 鍵 → 文 (改行に戻したもの)
    public readonly List<string> Order = new();                // 鍵の並び

    private DatFile(List<string> lines)
    {
        Lines = lines;
        foreach (string line in lines)
        {
            if (!TryLine(line, out string key, out string raw)) continue;
            if (!Values.ContainsKey(key)) Order.Add(key);
            Values[key] = Unescape(raw);
        }
    }

    public static DatFile Parse(string text) => new(SplitLines(text));
    public static DatFile FromLines(List<string> lines) => new(lines);

    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
        if (lines.Count > 0 && lines[0].Length > 0 && lines[0][0] == '﻿') lines[0] = lines[0].Substring(1);
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    // 足りない鍵を other から末尾へ足したもの (翻訳が追いついていない文は英語で出す)
    public DatFile WithMissingFrom(DatFile other)
    {
        List<string> extra = null;
        foreach (string key in other.Order)
        {
            if (Values.ContainsKey(key)) continue;
            extra ??= new List<string> { "", "# ── 未翻訳 (英語のまま) / Not translated yet ──" };
            extra.Add(Line(key, other.Values[key]));
        }
        if (extra == null) return this;
        var lines = new List<string>(Lines);
        lines.AddRange(extra);
        return new DatFile(lines);
    }

    public static bool TryLine(string line, out string key, out string raw)
    {
        key = raw = null;
        int start = 0;
        while (start < line.Length && (line[start] == ' ' || line[start] == '\t')) start++;
        if (start >= line.Length || line[start] == '#') return false;
        int colon = -1;
        for (int i = start; i < line.Length; i++)
            if (line[i] == ':' || line[i] == '：') { colon = i; break; }
        if (colon <= start) return false;
        key = line.Substring(start, colon - start).Trim();
        if (key.Length == 0) return false;
        int v = colon + 1;
        if (v < line.Length && line[v] == ' ') v++; // 「鍵: 文」の : の後の空白 1 個は区切り
        raw = line.Substring(v);
        return true;
    }

    public static string Line(string key, string value) => key + ": " + Escape(value);
    public static string Escape(string v) => v.Replace("\r", "").Replace("\n", "\\n");
    public static string Unescape(string raw) => raw.Replace("\\n", "\n");
}
