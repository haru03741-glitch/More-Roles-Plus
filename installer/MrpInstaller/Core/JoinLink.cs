using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MrpInstaller.Core;

// Discord のマッチメイキングの「参加する」→ https://…/mrp/join/<コード> → mrp://join/<コード> → このインストーラー (--join)。
// MRP の入ったゲームの BepInEx/config/mrp-join.txt に部屋コードを書き、ゲームが閉じていれば起動する。
// 起動中の MRP はその紙を見つけると MRP のサーバーへ切り替えて部屋に入る
public static class JoinLink
{
    public const string Scheme = "mrp";
    public const string FileName = "mrp-join.txt";

    // mrp:// をこの exe に結び付ける (利用者ごと・管理者は要らない)。exe を動かした時に付け直す
    public static void Register()
    {
        try
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string command = $"\"{exe}\" --join \"%1\"";
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
            if (key == null) return;
            using (var cmd = key.OpenSubKey(@"shell\open\command"))
                if (cmd?.GetValue("") as string == command) return;
            key.SetValue("", "URL:More Roles Plus");
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon")) icon?.SetValue("", $"\"{exe}\",0");
            using (var c = key.CreateSubKey(@"shell\open\command")) c?.SetValue("", command);
        }
        catch { }
    }

    public static bool IsRegistered()
    {
        try
        {
            using var c = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Scheme}\shell\open\command");
            return c?.GetValue("") is string s && s.Contains("--join");
        }
        catch { return false; }
    }

    // "mrp://join/ABCDEF" / "mrp://join/ABCDEF/" / "ABCDEF" → "ABCDEF" (形が違えば null)
    public static string ParseCode(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return null;
        var m = Regex.Match(arg.Trim(), @"^(?:mrp:(?://)?join/)?([A-Za-z]{4}|[A-Za-z]{6})/?$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    // 戻り値 = 知らせる言葉 (null = うまくいった)
    public static string Join(string arg)
    {
        string code = ParseCode(arg);
        if (code == null) return Lang.T("join.badCode");
        var games = GameDetector.Detect().Where(g => g.State == InstallState.Mrp).ToList();
        if (games.Count == 0) return Lang.T("join.noMrp");
        string line = code + "\n" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // どれが起動しているか分からないので、MRP の入った全部に置く (古い紙はゲームが時刻で捨てる)
        foreach (var g in games)
        {
            try
            {
                string dir = Path.Combine(g.BepInExDir, "config");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, FileName), line);
            }
            catch { }
        }
        var running = Process.GetProcessesByName("Among Us").FirstOrDefault();
        if (running != null)
        {
            try
            {
                ShowWindow(running.MainWindowHandle, 9); // 最小化を戻す
                SetForegroundWindow(running.MainWindowHandle);
            }
            catch { }
            return null;
        }
        var pick = games.FirstOrDefault(g => string.Equals(g.Path, Settings.Current.LastSelected, StringComparison.OrdinalIgnoreCase)) ?? games[0];
        try { Tools.Launch(pick); }
        catch (Exception e) { return e.Message; }
        return null;
    }
}
