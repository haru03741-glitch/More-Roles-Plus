// Based on https://github.com/waffle-ful/Aeterna-End-K-not (Installer/EndKnotInstaller/GameDetector.cs, GPL-3.0)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace MrpInstaller.Core;

// Steam (ライブラリの全フォルダ)・Epic (ランチャーのマニフェスト)・前に足したフォルダ・MRP 専用のコピーを探す
public static class GameDetector
{
    public static List<GameInstall> Detect()
    {
        var found = new List<GameInstall>();
        try { found.AddRange(Steam()); } catch { }
        try { found.AddRange(Epic()); } catch { }
        foreach (string dir in Settings.Current.ExtraFolders.ToList())
        {
            if (!File.Exists(Path.Combine(dir, "Among Us.exe"))) continue;
            bool copy = Settings.Current.CopyFolders.Contains(dir, StringComparer.OrdinalIgnoreCase);
            found.Add(new GameInstall(dir, copy ? GamePlatform.Steam : GuessPlatform(dir), FindEpicAppName(dir), copy));
        }
        // Epic は同じ場所を指すマニフェストを何個も残すので、場所でまとめる
        return found.GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    private static IEnumerable<GameInstall> Steam()
    {
        string root = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string
                      ?? Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam", "SteamPath", null) as string;
        if (root == null || !Directory.Exists(root)) yield break;
        var libs = new List<string> { root };
        string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        foreach (string lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string dir = Path.Combine(lib, "steamapps", "common", "Among Us");
            if (File.Exists(Path.Combine(dir, "Among Us.exe"))) yield return new GameInstall(dir, GamePlatform.Steam);
        }
    }

    private static IEnumerable<GameInstall> Epic()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        var json = new JavaScriptSerializer();
        foreach (string item in Directory.GetFiles(dir, "*.item"))
        {
            GameInstall g = null;
            try
            {
                var d = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(item));
                string exe = d.TryGetValue("LaunchExecutable", out var e) ? e as string : null;
                string name = d.TryGetValue("DisplayName", out var n) ? n as string : null;
                if (exe != "Among Us.exe" && name != "Among Us") continue;
                string loc = d.TryGetValue("InstallLocation", out var l) ? l as string : null;
                string app = d.TryGetValue("AppName", out var a) ? a as string : null;
                string ns = d.TryGetValue("CatalogNamespace", out var c) ? c as string : null;
                string cat = d.TryGetValue("CatalogItemId", out var ci) ? ci as string : null;
                // ランチャーの起動 URL は namespace:catalogItem:appName が確実
                string launch = ns != null && cat != null && app != null ? $"{ns}%3A{cat}%3A{app}" : app;
                if (loc != null && File.Exists(Path.Combine(loc, "Among Us.exe"))) g = new GameInstall(loc, GamePlatform.Epic, launch);
            }
            catch { }
            if (g != null) yield return g;
        }
    }

    public static GamePlatform GuessPlatform(string dir)
    {
        if (Directory.Exists(Path.Combine(dir, ".egstore")) || dir.IndexOf("Epic Games", StringComparison.OrdinalIgnoreCase) >= 0) return GamePlatform.Epic;
        if (File.Exists(Path.Combine(dir, "steam_appid.txt")) || dir.IndexOf("steamapps", StringComparison.OrdinalIgnoreCase) >= 0) return GamePlatform.Steam;
        return GamePlatform.Manual;
    }

    public static string FindEpicAppName(string dir)
    {
        try
        {
            string full = Path.GetFullPath(dir).TrimEnd('\\');
            return Epic().FirstOrDefault(i => string.Equals(i.Path, full, StringComparison.OrdinalIgnoreCase))?.EpicAppName;
        }
        catch { return null; }
    }
}
