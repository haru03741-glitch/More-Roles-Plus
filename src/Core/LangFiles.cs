using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;

namespace MoreRolesPlus;

// 遊ぶ人が書き換える文のファイル (BepInEx/config/MoreRolesPlus/Language)。
//   <言語>.dat          MRP の文 (今の文が全部入っている。書き換えた行だけが効く)
//   Vanilla/<言語>.dat  本編 (Among Us そのもの) の文
//   _defaults/          書き換えたかどうかを見分けるための元の文の写し
// MOD や本編の更新で文が変わった時は、書き換えていない行だけ新しい文に直し、増えた鍵は近くの行へ足す
internal static class LangFiles
{
    public static string Dir => Path.Combine(Paths.ConfigPath, "MoreRolesPlus", "Language");
    private static string DefaultsDir => Path.Combine(Dir, "_defaults");

    // MRP の文を読む。書き換えた鍵を cur に入れ、その数を返す
    public static int ApplyMod(string name, DatFile defaults, Dictionary<string, string> cur)
    {
        var edited = Sync(Path.Combine(Dir, name + ".dat"), Path.Combine(DefaultsDir, name + ".dat"), defaults, Header(name, false));
        foreach (var kv in edited) cur[kv.Key] = kv.Value;
        return edited.Count;
    }

    // 本編の文を読む。書き換えた鍵を返す。defaults は本編の今の文 (毎回読むと重いので写しが今の版なら写しを使う)
    public static Dictionary<string, string> ApplyVanilla(string name, string version, Func<DatFile> readGame)
    {
        string snap = Path.Combine(DefaultsDir, "Vanilla", name + ".dat");
        DatFile defaults = null;
        try
        {
            if (File.Exists(snap))
            {
                var s = DatFile.Parse(File.ReadAllText(snap, Encoding.UTF8));
                if (s.Lines.Count > 0 && s.Lines[0] == version) defaults = s;
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"lang: {snap}: {e.Message}"); }
        defaults ??= readGame();
        return Sync(Path.Combine(Dir, "Vanilla", name + ".dat"), snap, defaults, Header(name, true));
    }

    // 遊ぶ人のファイルを今の文に合わせて (無ければ作り)、組み込みと違う行 = 書き換えた行を返す
    private static Dictionary<string, string> Sync(string file, string snapFile, DatFile defaults, string[] header)
    {
        var edited = new Dictionary<string, string>();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            Directory.CreateDirectory(Path.GetDirectoryName(snapFile));
            WriteIfChanged(Path.Combine(Dir, "README.txt"), Readme);

            List<string> lines;
            bool dirty = false;
            if (!File.Exists(file))
            {
                lines = new List<string>(header);
                lines.AddRange(defaults.Lines);
                dirty = true;
            }
            else
            {
                lines = DatFile.SplitLines(File.ReadAllText(file, Encoding.UTF8));
                DatFile snap = File.Exists(snapFile) ? DatFile.Parse(File.ReadAllText(snapFile, Encoding.UTF8)) : null;
                var present = new HashSet<string>();
                for (int i = 0; i < lines.Count; i++)
                {
                    if (!DatFile.TryLine(lines[i], out string key, out string raw)) continue;
                    present.Add(key);
                    // 書き換えていない行 (前の元の文のまま) は、元の文が変わっていれば新しい文へ
                    if (snap != null && defaults.Values.TryGetValue(key, out string now) && snap.Values.TryGetValue(key, out string old)
                        && DatFile.Unescape(raw) == old && now != old)
                    {
                        lines[i] = DatFile.Line(key, now);
                        dirty = true;
                    }
                }
                if (AddMissing(lines, present, defaults)) dirty = true;
            }
            if (dirty) File.WriteAllText(file, Join(lines), Utf8NoBom);
            WriteIfChanged(snapFile, Join(defaults.Lines));

            var final = DatFile.FromLines(lines);
            List<string> unknown = null;
            foreach (string key in final.Order)
            {
                string v = final.Values[key];
                if (!defaults.Values.TryGetValue(key, out string d)) (unknown ??= new List<string>()).Add(key);
                if (d == null || d != v) edited[key] = v;
            }
            // 鍵の打ち間違いや、更新で無くなった鍵 (その行は効かない)
            if (unknown != null)
                Plugin.Logger.LogWarning($"lang: {Path.GetFileName(file)}: unknown keys ({unknown.Count}) = {string.Join(", ", unknown.GetRange(0, Math.Min(10, unknown.Count)))}");
        }
        catch (Exception e) { Plugin.Logger.LogError($"lang: {file}: {e}"); }
        return edited;
    }

    // 足りない鍵 (消した行・新しく増えた文) を、組み込みの並びで隣にある同じまとまり (鍵の最初の . まで) の行のそばへ足す。
    // 同じまとまりの行が無ければ直前の鍵の行の後へ
    private static bool AddMissing(List<string> lines, HashSet<string> present, DatFile defaults)
    {
        var order = defaults.Order;
        var after = new Dictionary<string, List<string>>();
        var before = new Dictionary<string, List<string>>();
        List<string> head = null;
        bool any = false;
        for (int i = 0; i < order.Count; i++)
        {
            string key = order[i];
            if (present.Contains(key)) continue;
            any = true;
            string line = DatFile.Line(key, defaults.Values[key]);
            string group = Group(key);
            string prev = null, next = null;
            for (int j = i - 1; j >= 0 && prev == null; j--) if (present.Contains(order[j])) prev = order[j];
            for (int j = i + 1; j < order.Count && next == null; j++) if (present.Contains(order[j])) next = order[j];
            if (prev != null && (Group(prev) == group || next == null || Group(next) != group)) Add(after, prev, line);
            else if (next != null) Add(before, next, line);
            else (head ??= new List<string>()).Add(line);
        }
        if (!any) return false;

        var result = new List<string>(lines.Count + 16);
        foreach (string line in lines)
        {
            bool isKey = DatFile.TryLine(line, out string key, out _);
            if (isKey && head != null) { result.AddRange(head); head = null; }
            if (isKey && before.Remove(key, out var pre)) result.AddRange(pre);
            result.Add(line);
            if (isKey && after.Remove(key, out var add)) result.AddRange(add);
        }
        if (head != null) result.AddRange(head);
        lines.Clear();
        lines.AddRange(result);
        return true;

        static string Group(string key)
        {
            int dot = key.IndexOf('.');
            return dot < 0 ? key : key.Substring(0, dot);
        }

        static void Add(Dictionary<string, List<string>> map, string anchor, string line)
        {
            if (!map.TryGetValue(anchor, out var list)) map[anchor] = list = new List<string>();
            list.Add(line);
        }
    }

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static string Join(List<string> lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;

    private static void WriteIfChanged(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == text) return;
        File.WriteAllText(path, text, Utf8NoBom);
    }

    internal static void RegisterBridge()
    {
        Bridge.TestBridge.Register("lang", "[reload | set <English|Japanese|…> | get <鍵>] 文のファイル: 今の言語と書き換えた数 / 読み直す / ゲームの言語を切り替える / 鍵の今の文 (MRP の鍵か本編の StringNames)", (args, reply) =>
        {
            var a = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string sub = a.Length > 0 ? a[0] : "";
            if (sub == "reload") { reply("OK lang " + Command("").Replace('\n', ' ')); return; }
            if (sub == "set" && a.Length > 1)
            {
                if (!Enum.TryParse(a[1], true, out SupportedLangs lang)) { reply("ERR lang set: unknown language " + a[1]); return; }
                AmongUs.Data.DataManager.Settings.Language.CurrentLanguage = lang;
                TranslationController.Instance.SetLanguage(lang);
                reply($"OK lang set {lang} now={Lang.Current} mod={Tr.Edited}");
                return;
            }
            if (sub == "get" && a.Length > 1)
            {
                string key = a[1].Trim();
                string v = Tr.Has(key) ? Tr.Get(key)
                    : Enum.TryParse(key, out StringNames sn) && TranslationController.InstanceExists ? TranslationController.Instance.GetString(sn) : null;
                reply(v == null ? "ERR lang get: unknown key " + key : $"OK lang get {key} = {DatFile.Escape(v)}");
                return;
            }
            reply($"OK lang now={Lang.Current} mod={Tr.Edited} dir={Dir}");
        });
    }

    // /lang = 文のファイルを読み直す・/lang open = フォルダを開く (PC)
    public static string Command(string args)
    {
        if (args.Trim().Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Dir);
            if (!OperatingSystem.IsWindows()) return new Text("LangFiles.OpenOnlyPc").With("path", Dir);
            System.Diagnostics.Process.Start("explorer.exe", "\"" + Dir + "\"");
            return new Text("LangFiles.Opened").With("path", Dir);
        }
        Lang.Reload();
        int vanilla = VanillaText.Reload();
        return new Text("LangFiles.Reloaded").With("mod", Tr.Edited).With("vanilla", vanilla).With("path", Dir);
    }

    private static string[] Header(string name, bool vanilla) => new[]
    {
        "# ================================================================",
        vanilla ? $"#  Among Us (本編) の文 / Among Us texts — {name}" : $"#  More Roles Plus の文 / More Roles Plus texts — {name}",
        "# ================================================================",
        "#  「鍵: 文」の : より右を書き換えて保存すると、ゲームの文が変わります。",
        "#  反映: ゲームを起動し直す / チャットで /lang と打つ / ゲームの言語を切り替える",
        vanilla
            ? "#  {0} {1} のような所には、遊んでいる時に名前や数字が入ります。"
            : "#  {name} のような { } の所には、遊んでいる時に名前や数字が入ります。",
        "#  改行は \\n 。文字の飾りが使えます: <color=#FF4040>赤</color> <b>太字</b> <i>斜め</i> <size=80%>小さく</size>",
        "#  # で始まる行はメモです (読まれません。自由に書いてかまいません)。",
        "#  元に戻す: その行を消す (次に読み込んだ時に元の文で書き直されます)。ファイルごと消すと全部元に戻ります。",
        "#",
        "#  Change the text after \"key:\" and save.",
        "#  To apply: restart the game / type /lang in chat / switch the game language.",
        vanilla
            ? "#  {0} {1} etc. are filled in with names or numbers while playing."
            : "#  {name} etc. are filled in with names or numbers while playing.",
        "#  New line = \\n. Rich text works: <color=#FF4040>red</color> <b>bold</b> <i>italic</i> <size=80%>small</size>",
        "#  Lines starting with # are notes. Delete a line (or the whole file) to restore the original text.",
        "# ================================================================",
        "",
    };

    private static readonly string Readme = string.Join(Environment.NewLine, new[]
    {
        "More Roles Plus — 文の書き換え / Custom texts",
        "",
        "■ 日本語",
        "このフォルダのファイルを書き換えると、ゲームの中の文を好きに変えられます。",
        "",
        "  Japanese.dat など        More Roles Plus の文 (役職の名前・説明・設定・コマンドの返事 など)",
        "  Vanilla/Japanese.dat など  Among Us そのものの文 (ボタン・タスク・部屋の名前・メニュー など)",
        "  _defaults/               元の文の写し (比べるためのもの。ここを書き換えても何も変わりません)",
        "",
        "ファイルはゲームの言語ごとにあり、今の言語のファイルが読まれます。",
        "まだ無い言語のファイルは、ゲームの言語をその言語に切り替えると作られます (中身は英語なので、訳を書けばその言語で遊べます)。",
        "",
        "書き方: 「鍵: 文」の : より右を書き換えて保存するだけです。",
        "  例)  Hawkeye.Name: 鷹の目   →   Hawkeye.Name: スナイパーの目",
        "反映: ゲームを起動し直す / チャットで /lang と打つ / ゲームの言語を切り替える",
        "  {name} や {0} の所には名前や数字が入ります。消すとその値は出なくなります。",
        "  改行は \\n 、色は <color=#FF4040>赤</color> 、太字は <b>太字</b> のように書けます。",
        "  # で始まる行はメモです。",
        "  元に戻したい行は消してください。ファイルごと消すと全部元に戻ります。",
        "  更新で元の文が変わった時、書き換えていない行は自動で新しい文になります。書き換えた行はそのまま残ります。",
        "",
        "■ English",
        "Edit the files in this folder to change any text in the game.",
        "",
        "  English.dat etc.          More Roles Plus texts (role names and descriptions, settings, command replies, ...)",
        "  Vanilla/English.dat etc.  Among Us texts (buttons, tasks, room names, menus, ...)",
        "  _defaults/                Copies of the original texts, used for comparison. Editing them does nothing.",
        "",
        "There is one file per game language; the file for the current language is used.",
        "Switch the game language to create the file for another language (it starts in English, so you can translate it).",
        "",
        "Change the text after \"key:\" and save.",
        "  e.g.  Hawkeye.Name: Hawkeye   ->   Hawkeye.Name: Eagle Eye",
        "To apply: restart the game / type /lang in chat / switch the game language.",
        "  {name} and {0} are filled in with names or numbers.",
        "  New line = \\n. Rich text works: <color=#FF4040>red</color>, <b>bold</b>.",
        "  Lines starting with # are notes.",
        "  Delete a line, or the whole file, to restore the original text.",
        "  When an update changes an original text, lines you have not edited are updated automatically; your edits stay.",
        "",
    });
}

// 本編の文の書き換え。本編は言語ごとに文の表 (LanguageUnit.AllStrings) を 1 つ持ち、GetString は毎回そこを引くので、
// 言語を読み込んだ直後に表の中身を書き換える (GetString そのものには手を入れないので、呼ばれる回数の費用は増えない)
[HarmonyPatch(typeof(TranslationController))]
internal static class VanillaText
{
    // 言語の表ごとの、書き換える前の本編の文 (null = 元は無かった鍵)。本編が表を使い回しても、消した行を元に戻せるように表ごとに持つ。
    // 同じ場所に別の言語の表が作られた時は戻さない
    private static readonly Dictionary<IntPtr, (SupportedLangs Lang, Dictionary<string, string> Orig)> Units = new();

    [HarmonyPatch(nameof(TranslationController.Initialize)), HarmonyPostfix]
    private static void AfterInitialize(TranslationController __instance)
    {
        Apply(__instance, false);
        Lang.Refresh();
    }

    [HarmonyPatch(nameof(TranslationController.SetLanguage)), HarmonyPostfix]
    private static void AfterSetLanguage(TranslationController __instance, SupportedLangs language)
    {
        Apply(__instance, true);
        Lang.Use(language);
    }

    // 本編の文を読み直す。書き換えた鍵の数を返す
    public static int Reload() => TranslationController.InstanceExists ? Apply(TranslationController.Instance, true) : 0;

    private static int Apply(TranslationController tc, bool resetTexts)
    {
        try
        {
            var unit = tc ? tc.currentLanguage : null;
            if (unit == null) return 0;
            var all = unit.AllStrings;
            var lang = unit.languageID;
            bool had = false;
            if (Units.TryGetValue(unit.Pointer, out var prev) && prev.Lang == lang)
                foreach (var kv in prev.Orig)
                {
                    had = true;
                    if (kv.Value == null) all.Remove(kv.Key);
                    else all[kv.Key] = kv.Value;
                }
            var orig = new Dictionary<string, string>();
            Units[unit.Pointer] = (lang, orig);

            // 写しが今の本編の物かの印 (版の数字が更新で変わらないこともあるので文の数も並べる)
            string version = $"# Among Us {Constants.Year}.{Constants.Month}.{Constants.Day}.{Constants.Revision} ({all.Count} texts)";
            var edited = LangFiles.ApplyVanilla(lang.ToString(), version, () => ReadGame(all, version));
            foreach (var kv in edited)
            {
                orig[kv.Key] = all.TryGetValue(kv.Key, out string o) ? o : null;
                all[kv.Key] = kv.Value;
            }
            if (resetTexts && (had || edited.Count > 0))
                foreach (var t in tc.ActiveTexts)
                {
                    // 消えかけの文字が 1 つ例外を出しても残りは描き直す
                    try { t?.ResetText(); }
                    catch (Exception e) { Plugin.Logger.LogWarning($"lang: reset text: {e.Message}"); }
                }
            Plugin.Logger.LogInfo($"lang: vanilla {lang} edited={edited.Count}");
            return edited.Count;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"lang: vanilla: {e}");
            return 0;
        }
    }

    // 本編の今の文を全部読む (版が変わった時と初めての時だけ)
    private static DatFile ReadGame(Il2CppSystem.Collections.Generic.Dictionary<string, string> all, string version)
    {
        var lines = new List<string> { version };
        foreach (var kv in all) lines.Add(DatFile.Line(kv.Key, kv.Value ?? ""));
        return DatFile.FromLines(lines);
    }
}
