using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace MrpInstaller.Core;

public sealed class InstallException : Exception
{
    public InstallException(string message) : base(message) { }
}

// 入れる・消す・戻す・切り替える。どれも画面とコマンドライン (Cli) の両方から呼ぶ
public static class ModInstaller
{
    // mod の読み込み役として、ゲームのフォルダの直下に置かれる物 (他の BepInEx mod もこれを使う)
    public static readonly string[] LoaderItems =
    {
        "BepInEx", "dotnet", "winhttp.dll", "winhttp.dll.disabled", "doorstop_config.ini", ".doorstop_version", "version.dll",
    };

    public delegate void Report(double fraction, string message);

    public static bool GameRunning() => Process.GetProcessesByName("Among Us").Length > 0;

    private static void Precheck(GameInstall g)
    {
        if (GameRunning()) throw new InstallException(Lang.T("err.running"));
        if (!File.Exists(g.ExePath)) throw new InstallException(Lang.T("err.noexe"));
    }

    public static void Install(GameInstall g, Report report)
    {
        Precheck(g);
        if (!g.Is64Bit) throw new InstallException(Lang.T("err.x86"));
        g.Refresh();

        if (g.State == InstallState.OtherMod)
        {
            report(0.02, Lang.T("log.backupOther"));
            string dir = Backup(g, "other");
            report(0.05, Lang.T("log.backedUp", Path.GetFileName(dir)));
        }

        bool fresh = !Directory.Exists(g.BepInExDir);
        bool coreChanged = false;
        int written = 0, skipped = 0, kept = 0;
        using (var zip = Payload.Open())
        {
            var entries = zip.Entries.Where(e => e.Name.Length > 0).ToList();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                string rel = Payload.Norm(e.FullName);
                string dest = g.P(rel.Split('/'));
                report(0.08 + 0.85 * i / entries.Count, "  " + rel);

                // 自分で変えた設定は残す (無い時だけ置く)
                bool userOwned = rel.StartsWith("BepInEx/config/", StringComparison.OrdinalIgnoreCase) || rel.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase);
                if (userOwned && File.Exists(dest)) { kept++; continue; }
                if (File.Exists(dest) && Payload.SameAsPayload(rel, dest)) { skipped++; continue; }
                if (rel.StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase) && File.Exists(dest)) coreChanged = true;

                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                string tmp = dest + ".mrpnew";
                using (var s = e.Open())
                using (var fs = File.Create(tmp))
                    s.CopyTo(fs);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
                File.SetLastWriteTime(dest, e.LastWriteTime.LocalDateTime);
                written++;
            }
        }

        // 無効にしてあった時は、入れ直したので有効に戻す
        if (File.Exists(g.WinhttpDisabledPath)) File.Delete(g.WinhttpDisabledPath);

        // BepInEx の土台が変わったら、作り直しが要る物を消す (次の起動で作り直される)
        if (coreChanged)
        {
            report(0.95, Lang.T("log.clearInterop"));
            ClearCache(g, false);
        }

        File.WriteAllText(g.P("BepInEx", "mrp-installer.txt"),
            $"installed={DateTime.Now:yyyy-MM-dd HH:mm:ss}\ninstaller={typeof(ModInstaller).Assembly.GetName().Version}\nmrp={Payload.MrpVersion}\n", Encoding.UTF8);
        g.Refresh();
        report(1, Lang.T("log.installDone", written, skipped, kept) + (fresh ? " " + Lang.T("log.firstLaunch") : ""));
    }

    // 読み込み役一式を MRP_Backups\<日時>_<label> へ移す。戻す時は Restore
    public static string Backup(GameInstall g, string label)
    {
        string dir = Path.Combine(g.BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" + label);
        Directory.CreateDirectory(dir);
        var plugins = g.OtherPlugins.ToList();
        if (File.Exists(g.MrpDllPath)) plugins.Add(GameInstall.MrpDll);
        foreach (string item in LoaderItems)
        {
            string src = g.P(item);
            if (Directory.Exists(src)) Directory.Move(src, Path.Combine(dir, item));
            else if (File.Exists(src)) File.Move(src, Path.Combine(dir, item));
        }
        File.WriteAllText(Path.Combine(dir, "backup-info.txt"),
            $"date={DateTime.Now:yyyy-MM-dd HH:mm:ss}\nlabel={label}\nplugins={string.Join(", ", plugins)}\n", Encoding.UTF8);
        g.Refresh();
        return dir;
    }

    public static string BackupDescription(string dir)
    {
        try
        {
            string info = Path.Combine(dir, "backup-info.txt");
            string plugins = File.Exists(info) ? File.ReadAllLines(info).FirstOrDefault(l => l.StartsWith("plugins="))?.Substring(8) : null;
            return string.IsNullOrWhiteSpace(plugins) ? "-" : plugins;
        }
        catch { return "-"; }
    }

    public static void Restore(GameInstall g, string backupDir, Report report)
    {
        Precheck(g);
        g.Refresh();
        if (LoaderItems.Any(i => File.Exists(g.P(i)) || Directory.Exists(g.P(i))))
        {
            report(0.1, Lang.T("log.backupCurrent"));
            Backup(g, g.State is InstallState.Mrp or InstallState.MrpDisabled or InstallState.Broken ? "mrp" : "other");
        }
        report(0.5, Lang.T("log.restoring", Path.GetFileName(backupDir)));
        foreach (string item in LoaderItems)
        {
            string src = Path.Combine(backupDir, item);
            if (Directory.Exists(src)) Directory.Move(src, g.P(item));
            else if (File.Exists(src)) File.Move(src, g.P(item));
        }
        Directory.Delete(backupDir, true);
        g.Refresh();
        report(1, Lang.T("log.restored"));
    }

    // keepBackup: 消す代わりに MRP_Backups へ退ける (後で戻せる)
    public static void Uninstall(GameInstall g, bool keepBackup, bool restoreOther, Report report)
    {
        Precheck(g);
        g.Refresh();
        if (keepBackup)
        {
            report(0.3, Lang.T("log.backupCurrent"));
            Backup(g, "mrp");
        }
        else
        {
            foreach (string item in LoaderItems)
            {
                report(0.3, item);
                string p = g.P(item);
                if (Directory.Exists(p)) Directory.Delete(p, true);
                else if (File.Exists(p)) File.Delete(p);
            }
        }
        g.Refresh();
        if (restoreOther)
        {
            string other = g.Backups.FirstOrDefault(b => b.EndsWith("_other", StringComparison.OrdinalIgnoreCase));
            if (other != null) Restore(g, other, report);
        }
        g.Refresh();
        report(1, Lang.T("log.uninstalled"));
    }

    public static void DeleteBackup(string dir) => Directory.Delete(dir, true);

    // winhttp.dll ⇔ winhttp.dll.disabled (無効の間は素の Among Us として起動する)
    public static bool Toggle(GameInstall g)
    {
        Precheck(g);
        bool enable = !File.Exists(g.WinhttpPath);
        if (enable)
        {
            if (!File.Exists(g.WinhttpDisabledPath)) throw new InstallException(Lang.T("err.notInstalled"));
            File.Move(g.WinhttpDisabledPath, g.WinhttpPath);
        }
        else
        {
            if (File.Exists(g.WinhttpDisabledPath)) File.Delete(g.WinhttpDisabledPath);
            File.Move(g.WinhttpPath, g.WinhttpDisabledPath);
        }
        g.Refresh();
        return enable;
    }

    public static void SetPatcher(GameInstall g, bool on)
    {
        if (on && File.Exists(g.PatcherDisabledPath) && !File.Exists(g.PatcherPath)) File.Move(g.PatcherDisabledPath, g.PatcherPath);
        if (!on && File.Exists(g.PatcherPath))
        {
            if (File.Exists(g.PatcherDisabledPath)) File.Delete(g.PatcherDisabledPath);
            File.Move(g.PatcherPath, g.PatcherDisabledPath);
        }
        g.Refresh();
    }

    // interop (ゲームの型の写し) と cache を消す。ゲームが更新されて起動しない時に効く
    public static long ClearCache(GameInstall g, bool check = true)
    {
        if (check) Precheck(g);
        long freed = 0;
        foreach (string d in new[] { g.P("BepInEx", "interop"), g.P("BepInEx", "cache") })
        {
            if (!Directory.Exists(d)) continue;
            freed += DirSize(d);
            Directory.Delete(d, true);
        }
        return freed;
    }

    // MRP 専用のコピーを作る (BepInEx などの読み込み役は写さない)
    public static void CopyGame(string src, string dest, Report report)
    {
        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any()) throw new InstallException(Lang.T("err.destNotEmpty"));
        var skip = new HashSet<string>(LoaderItems.Concat(new[] { "MRP_Backups", "MRP_Language" }), StringComparer.OrdinalIgnoreCase);
        var files = Directory.GetFiles(src, "*", SearchOption.AllDirectories)
            .Where(f => !skip.Contains(f.Substring(src.Length).TrimStart('\\').Split('\\')[0]))
            .ToList();
        long total = files.Sum(f => new FileInfo(f).Length), done = 0;
        foreach (string f in files)
        {
            string rel = f.Substring(src.Length).TrimStart('\\');
            string to = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            File.Copy(f, to, true);
            done += new FileInfo(f).Length;
            report(total == 0 ? 1 : (double)done / total, "  " + rel);
        }
    }

    public static long DirSize(string dir)
    {
        try { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }

    public static string Size(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):F2} GB" : b >= 1 << 20 ? $"{b / (double)(1 << 20):F1} MB" : $"{b / 1024.0:F0} KB";
}
