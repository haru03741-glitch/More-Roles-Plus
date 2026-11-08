using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace MrpInstaller.Core;

// インストール式: 同じ exe を MoreRolesPlusSetup.exe の名前で配る (または --setup)。
// 動かすと自分を %LOCALAPPDATA%\Programs\MoreRolesPlus へ写し、スタートメニューとデスクトップにショートカットを作り、
// Windows の「アプリ」(プログラムの追加と削除) に載せてから、写した方を開く。管理者は要らない (利用者ごと)。
// 消す時は「アプリ」からか --self-uninstall。ゲームに入れた MRP はそのまま残す (外すのはアプリの「アンインストール」)
public static class SelfSetup
{
    private const string AppName = "More Roles Plus";
    private const string ExeName = "MoreRolesPlusInstaller.exe";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MoreRolesPlus";

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MoreRolesPlus");
    public static string InstalledExe => Path.Combine(Dir, ExeName);
    private static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    private static string SelfPath => Process.GetCurrentProcess().MainModule.FileName;

    // この起動はインストール式として動くか (名前に Setup が入っている・--setup)
    public static bool Wants(string[] args) =>
        Array.IndexOf(args, "--setup") >= 0
        || Path.GetFileNameWithoutExtension(SelfPath).IndexOf("Setup", StringComparison.OrdinalIgnoreCase) >= 0
           && !string.Equals(Path.GetFullPath(SelfPath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static bool WantsUninstall(string[] args) => Array.IndexOf(args, "--self-uninstall") >= 0;

    // 入れて、入れた方を開く。戻り値 = 終わりの番号
    public static int Install(bool quiet)
    {
        try
        {
            if (!quiet && MessageBox.Show(Lang.T("setup.ask", Dir), AppName, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return 1;
            // 開いている前の版は閉じてもらう (exe を置き換えられない)
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
            {
                try
                {
                    if (string.Equals(p.MainModule.FileName, InstalledExe, StringComparison.OrdinalIgnoreCase)) { p.CloseMainWindow(); p.WaitForExit(5000); }
                }
                catch { }
            }
            Directory.CreateDirectory(Dir);
            File.Copy(SelfPath, InstalledExe, true);
            Link(StartMenuLink, InstalledExe);
            Link(DesktopLink, InstalledExe);
            WriteUninstallEntry();
            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = Dir });
            return 0;
        }
        catch (Exception e)
        {
            if (!quiet) MessageBox.Show(Lang.T("setup.failed", e.Message), AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    // 入れてあるなら「アプリ」の版を今の版に合わせる (自分を更新した後など)
    public static void RefreshEntry()
    {
        try
        {
            if (!string.Equals(Path.GetFullPath(SelfPath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase)) return;
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (key == null) return;
            if (key.GetValue("DisplayVersion") as string == Payload.MrpVersion) return;
            WriteUninstallEntry();
        }
        catch { }
    }

    private static void WriteUninstallEntry()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        if (key == null) return;
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Payload.MrpVersion ?? "");
        key.SetValue("Publisher", AppName);
        key.SetValue("DisplayIcon", $"\"{InstalledExe}\",0");
        key.SetValue("InstallLocation", Dir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --self-uninstall");
        key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --self-uninstall --quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try { key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord); }
        catch { }
    }

    // 消す: ショートカット・「アプリ」の項目・mrp:// の結び付き (この exe を指している時だけ)・入れたフォルダ
    public static int Uninstall(bool quiet)
    {
        if (!quiet && MessageBox.Show(Lang.T("setup.askRemove"), AppName, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return 1;
        TryDelete(StartMenuLink);
        TryDelete(DesktopLink);
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); }
        catch { }
        try
        {
            bool ours;
            using (var cmd = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{JoinLink.Scheme}\shell\open\command"))
                ours = cmd?.GetValue("") is string s && s.IndexOf(InstalledExe, StringComparison.OrdinalIgnoreCase) >= 0;
            if (ours) Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{JoinLink.Scheme}", false);
        }
        catch { }
        // 動いている自分は消せないので、閉じた後に cmd で消す
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{Dir}\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch { }
        if (!quiet) MessageBox.Show(Lang.T("setup.removed"), AppName, MessageBoxButton.OK, MessageBoxImage.Information);
        return 0;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    // .lnk を作る (Windows の WScript.Shell を借りる)
    private static void Link(string path, string target)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type == null) return;
        dynamic shell = Activator.CreateInstance(type);
        try
        {
            dynamic lnk = shell.CreateShortcut(path);
            lnk.TargetPath = target;
            lnk.WorkingDirectory = Path.GetDirectoryName(target);
            lnk.IconLocation = target + ",0";
            lnk.Description = AppName;
            lnk.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
}
