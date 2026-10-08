using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using MoreRolesPlus.Roles;

namespace MoreRolesPlus.Options;

// 起動時に一度だけ、DLL の中の役職クラスと [Settings] クラスを探して設定項目を集める。
// - 同期・保存の順番 = 名前 (Key) の文字順。全員が同じ版なら全員で同じ並びになる (版の確認は VersionCheck)。
// - 画面の並び = タブごとの見出し (役職 / 設定のまとまり) の順。
internal static class Registry
{
    // 設定画面の見出し 1 つ分
    internal sealed class Section
    {
        public Text Title;
        public string Color;   // 見出しの色 (null なら既定)
        public Assignable Role;  // 役職・アドオンの見出しなら、その物
        public readonly List<Opt> Opts = new();
    }

    public static readonly List<Opt> All = new();          // 同期・保存の順
    public static readonly List<RoleBase> Roles = new();   // 役職の雛形 (Id の文字順 = 同期の番号)
    public static readonly List<AddonBase> Addons = new(); // アドオンの雛形 (同上)
    private static readonly Dictionary<Tab, List<Section>> Sections = new();
    private static readonly Dictionary<string, Opt> ByKey = new(StringComparer.Ordinal);

    // 版と設定・役職・電文の並びから作る指紋。違う版どうしでは番号がずれるので、同期の前に照合する
    public static uint Fingerprint { get; private set; }

    private static string SavePath => Path.Combine(Paths.ConfigPath, "MoreRolesPlus.options.txt");

    public static IReadOnlyList<Section> SectionsOf(Tab tab) => Sections.TryGetValue(tab, out var l) ? l : Array.Empty<Section>();

    public static void Init()
    {
        var types = typeof(Registry).Assembly.GetTypes();

        foreach (var t in types.Where(t => t.IsSubclassOf(typeof(RoleBase)) && !t.IsAbstract).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            RoleBase role;
            try { role = (RoleBase)Activator.CreateInstance(t); }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"role {t.Name}: cannot create ({e.InnerException?.Message ?? e.Message}) — skipped");
                continue;
            }
            if (Roles.Any(r => r.Id == role.Id))
            {
                Plugin.Logger.LogError($"role {t.Name}: Id '{role.Id}' is already used — skipped");
                continue;
            }

            Section sec;
            try
            {
                sec = new Section { Title = role.Name, Color = role.Color, Role = role };
                var opts = CollectFields(t);
                role.Chance = new IntOpt("出現率", "Chance", 0, 0, 100, 10, "%");
                role.Count = new IntOpt("人数", "Count", 1, 1, Math.Max(role.MaxCount, 1));
                Add(sec, role.Chance, role.Id + ".Chance");
                Add(sec, role.Count, role.Id + ".Count");
                foreach (var (name, opt) in opts) Add(sec, opt, role.Id + "." + name);
            }
            catch (Exception e)
            {
                // 役職 1 つの書き間違いで mod 全体が読み込まれなくならないように、その役職だけ飛ばす
                Plugin.Logger.LogError($"role {t.Name}: {e.InnerException?.Message ?? e.Message} — skipped");
                continue;
            }
            Roles.Add(role);
            SectionList(role.Tab).Add(sec);
            MoreRolesPlus.Roles.EventBinder.Prepare(t); // 試合中の最初の割り当てで反射しないように先に
        }
        Roles.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        foreach (var t in types.Where(t => t.IsSubclassOf(typeof(AddonBase)) && !t.IsAbstract).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            AddonBase addon;
            try { addon = (AddonBase)Activator.CreateInstance(t); }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"addon {t.Name}: cannot create ({e.InnerException?.Message ?? e.Message}) — skipped");
                continue;
            }
            if (Addons.Any(a => a.Id == addon.Id) || Roles.Any(r => r.Id == addon.Id))
            {
                Plugin.Logger.LogError($"addon {t.Name}: Id '{addon.Id}' is already used — skipped");
                continue;
            }
            Section sec;
            try
            {
                sec = new Section { Title = addon.Name, Color = addon.Color, Role = addon };
                var opts = CollectFields(t);
                addon.Chance = new IntOpt("出現率", "Chance", 0, 0, 100, 10, "%");
                addon.Count = new IntOpt("人数", "Count", 1, 1, Math.Max(addon.MaxCount, 1));
                addon.OnCrew = new BoolOpt("クルーに付く", "Crewmates can have it", true);
                addon.OnImpostor = new BoolOpt("インポスターに付く", "Impostors can have it", true);
                addon.OnNeutral = new BoolOpt("第三陣営に付く", "Neutrals can have it", true);
                Add(sec, addon.Chance, addon.Id + ".Chance");
                Add(sec, addon.Count, addon.Id + ".Count");
                Add(sec, addon.OnCrew, addon.Id + ".OnCrew");
                Add(sec, addon.OnImpostor, addon.Id + ".OnImpostor");
                Add(sec, addon.OnNeutral, addon.Id + ".OnNeutral");
                foreach (var (name, opt) in opts) Add(sec, opt, addon.Id + "." + name);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"addon {t.Name}: {e.InnerException?.Message ?? e.Message} — skipped");
                continue;
            }
            Addons.Add(addon);
            SectionList(Tab.Addon).Add(sec);
            MoreRolesPlus.Roles.EventBinder.Prepare(t);
        }
        Addons.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        foreach (var (t, attr) in types
                     .Select(t => (t, attr: t.GetCustomAttribute<SettingsAttribute>()))
                     .Where(x => x.attr != null)
                     .OrderBy(x => x.attr.Order).ThenBy(x => x.t.Name, StringComparer.Ordinal))
        {
            var sec = new Section { Title = new Text(attr.Ja, attr.En) };
            try
            {
                foreach (var (name, opt) in CollectFields(t)) Add(sec, opt, t.Name + "." + name);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"settings {t.Name}: {e.InnerException?.Message ?? e.Message} — skipped");
                continue;
            }
            // 設定のまとまりは役職より上に出す
            SectionList(attr.Tab).Insert(SectionList(attr.Tab).Count(s => s.Role == null), sec);
        }

        All.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        Fingerprint = MakeFingerprint();
        Load();
        Plugin.Logger.LogInfo($"registry: {Roles.Count} roles, {Addons.Count} addons, {All.Count} options, fingerprint {Fingerprint:X8}");
    }

    public static bool TryGet(string key, out Opt opt) => ByKey.TryGetValue(key, out opt);

    private static List<Section> SectionList(Tab tab)
    {
        if (!Sections.TryGetValue(tab, out var l)) Sections[tab] = l = new List<Section>();
        return l;
    }

    // static readonly の設定項目を宣言順に集める (読む時に static の初期化が走るので、失敗はここで起きる)
    private static List<(string name, Opt opt)> CollectFields(Type t)
    {
        var list = new List<(string, Opt)>();
        var fields = t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => typeof(Opt).IsAssignableFrom(f.FieldType))
            .OrderBy(f => f.MetadataToken);
        foreach (var f in fields)
        {
            if (f.GetValue(null) is Opt opt) list.Add((f.Name, opt));
        }
        return list;
    }

    private static void Add(Section sec, Opt opt, string key)
    {
        if (ByKey.ContainsKey(key))
        {
            Plugin.Logger.LogError($"option key '{key}' is used twice — the second one is ignored");
            return;
        }
        opt.Key = key;
        ByKey[key] = opt;
        All.Add(opt);
        sec.Opts.Add(opt);
    }

    private static uint MakeFingerprint()
    {
        uint h = 2166136261;
        void Mix(string s)
        {
            foreach (char c in s) h = (h ^ c) * 16777619;
            h = (h ^ '|') * 16777619;
        }
        Mix(Plugin.Version);
        foreach (var o in All) Mix(o.Key + ":" + o.Count);
        foreach (var r in Roles) Mix(r.Id);
        foreach (var a in Addons) Mix("+" + a.Id);
        foreach (var c in Net.Remote.All) Mix(c.Name);
        return h;
    }

    // ---- 保存 (自分がホストの時の設定値。ホストから受け取った値は保存しない) ----

    public static void Load() => LoadFrom(SavePath);

    // path の値を読む (無い項目は既定値)。読めたら true
    public static bool LoadFrom(string path)
    {
        foreach (var o in All) o.Index = o.DefaultIndex;
        try
        {
            if (!File.Exists(path)) return false;
            foreach (string line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0 || line.StartsWith('#')) continue;
                if (ByKey.TryGetValue(line[..eq].Trim(), out var opt)) opt.Load(line[(eq + 1)..].Trim());
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"options load failed: {e.Message}"); return false; }
        return true;
    }

    public static void Save() => SaveTo(SavePath);

    public static void SaveTo(string path)
    {
        try
        {
            var sb = new StringBuilder("# More Roles Plus の設定値 (ゲーム内の設定画面で変えると上書きされる)\n");
            foreach (var o in All) sb.Append(o.Key).Append('=').Append(o.Save()).Append('\n');
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, sb.ToString());
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"options save failed: {e.Message}"); }
    }
}
