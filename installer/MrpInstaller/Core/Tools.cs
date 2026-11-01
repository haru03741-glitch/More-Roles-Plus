using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace MrpInstaller.Core;

// 起動・フォルダを開く・診断用の zip・デスクトップのショートカット
public static class Tools
{
    public const string SteamAppId = "945360";

    public static void Launch(GameInstall g)
    {
        if (ModInstaller.GameRunning()) throw new InstallException(Lang.T("err.running"));
        // Epic は exe を直接起動するとログインできないので、ランチャー経由で起動する
        if (!g.IsCopy && g.Platform == GamePlatform.Epic && !string.IsNullOrEmpty(g.EpicAppName))
            Open($"com.epicgames.launcher://apps/{g.EpicAppName}?action=launch&silent=true");
        else if (!g.IsCopy && g.Platform == GamePlatform.Steam && g.Path.IndexOf("steamapps", StringComparison.OrdinalIgnoreCase) >= 0)
            Open($"steam://rungameid/{SteamAppId}");
        else
            Process.Start(new ProcessStartInfo(g.ExePath) { WorkingDirectory = g.Path, UseShellExecute = true });
    }

    public static void Open(string target)
    {
        if (!target.Contains("://") && !File.Exists(target) && !Directory.Exists(target)) Directory.CreateDirectory(target);
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    public static void Select(string file)
    {
        if (File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\"");
        else Open(Path.GetDirectoryName(file));
    }

    // 不具合の報告用。ログ・設定・入っている物の一覧をデスクトップに zip で置く
    public static string Diagnostics(GameInstall g)
    {
        string zipPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"MRP-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        void Add(string file, string name)
        {
            if (!File.Exists(file)) return;
            using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = zip.CreateEntry(name).Open();
            src.CopyTo(dst);
        }
        Add(g.LogPath, "LogOutput.log");
        Add(g.P("BepInEx", "ErrorLog.log"), "ErrorLog.log");
        Add(g.BepInExCfg, "BepInEx.cfg");
        Add(g.MrpCfg, GameInstall.MrpConfig);
        Add(g.P("doorstop_config.ini"), "doorstop_config.ini");
        Add(g.P("BepInEx", "mrp-installer.txt"), "mrp-installer.txt");
        Add(Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", "AppData", "LocalLow", "Innersloth", "Among Us", "Player.log"), "Player.log");

        var sb = new StringBuilder();
        sb.AppendLine($"date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"os: {Environment.OSVersion} 64bit={Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"installer: {typeof(Tools).Assembly.GetName().Version} payload MRP={Payload.MrpVersion} BepInEx={Payload.BepInExVersion}");
        sb.AppendLine($"game: {g} state={g.State} 64bit={g.Is64Bit} updated={g.GameUpdated}");
        sb.AppendLine($"mrp: {(g.MrpBuilt.HasValue ? $"{g.MrpBuilt} {g.MrpSize} bytes current={g.MrpIsCurrent}" : "none")}");
        sb.AppendLine($"bootaccel: {g.HasPatcher}  enabled: {File.Exists(g.WinhttpPath)}");
        sb.AppendLine($"other plugins: {string.Join(", ", g.OtherPlugins)}");
        sb.AppendLine($"backups: {string.Join(", ", g.Backups.Select(Path.GetFileName))}");
        sb.AppendLine("files:");
        foreach (string d in new[] { g.Path, g.BepInExDir, g.PluginsDir, g.PatchersDir, g.P("BepInEx", "core") })
        {
            if (!Directory.Exists(d)) continue;
            foreach (string f in Directory.GetFiles(d))
            {
                var fi = new FileInfo(f);
                sb.AppendLine($"  {f.Substring(g.Path.Length).TrimStart('\\')}  {fi.Length}  {fi.LastWriteTime:yyyy-MM-dd HH:mm}");
            }
        }
        using (var w = new StreamWriter(zip.CreateEntry("summary.txt").Open(), new UTF8Encoding(false)))
            w.Write(sb.ToString());
        return zipPath;
    }

    // デスクトップに「Among Us (MRP)」のショートカット (.url) を置く
    public static string Shortcut(GameInstall g)
    {
        string name = g.IsCopy ? "Among Us (MRP Copy)" : $"Among Us (MRP {g.PlatformLabel})";
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name + ".url");
        string url = !g.IsCopy && g.Platform == GamePlatform.Epic && g.EpicAppName != null
            ? $"com.epicgames.launcher://apps/{g.EpicAppName}?action=launch&silent=true"
            : !g.IsCopy && g.Platform == GamePlatform.Steam ? $"steam://rungameid/{SteamAppId}"
            : new Uri(g.ExePath).AbsoluteUri;
        File.WriteAllText(path, $"[InternetShortcut]\r\nURL={url}\r\nIconFile={g.ExePath}\r\nIconIndex=0\r\n", Encoding.Default);
        return path;
    }

    public static string ReadLogTail(GameInstall g, int maxLines = 400)
    {
        if (!File.Exists(g.LogPath)) return null;
        using var fs = new FileStream(g.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs, Encoding.UTF8);
        var lines = r.ReadToEnd().Split('\n');
        return string.Join("\n", lines.Skip(Math.Max(0, lines.Length - maxLines)));
    }

    public static bool IsAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    // 書き込めない場所 (Program Files の Epic など) の時は、管理者として開き直す
    public static bool RestartAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch { return false; }
    }
}
