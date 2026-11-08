using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace MrpInstaller.Core;

// 画面を出さずに使う時 (テスト・一括導入用)。
//   --list                         見つかったゲームと状態
//   --install <対象>               入れる/更新する (対象: steam / epic / 番号 / フォルダ)
//   --uninstall <対象> [--delete] [--restore]
//   --toggle <対象>                有効 ⇔ 無効
//   --clear-cache <対象>
//   --diag <対象>                  診断用 zip
//   --payload                      同梱の中身
//   --check-update                 GitHub の最新リリースと、更新が要る物
//   --update [対象]                リリースの MoreRolesPlus.dll で MRP を更新 (対象なしなら古い物全部)
//   --self-update                  リリースのインストーラーで自分を入れ替える
public static class Cli
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    private static readonly string[] Commands = { "--list", "--install", "--uninstall", "--toggle", "--clear-cache", "--diag", "--payload", "--check-update", "--update", "--self-update" };

    // --updated / --tour は画面を出す時の印なので、ここでは扱わない
    public static bool Wants(string[] args) => args.Any(a => Commands.Contains(a));

    public static int Run(string[] args)
    {
        AttachConsole(-1);
        var output = Console.Out;
        try
        {
            Console.WriteLine();
            var a = args.ToList();
            if (a.Contains("--en")) Settings.Current.Language = "en";
            string Arg(string name) { int i = a.IndexOf(name); return i >= 0 && i + 1 < a.Count ? a[i + 1] : null; }
            var games = GameDetector.Detect();
            GameInstall Pick(string name)
            {
                string t = Arg(name) ?? throw new InstallException($"{name} <steam|epic|番号|フォルダ>");
                if (int.TryParse(t, out int n) && n >= 1 && n <= games.Count) return games[n - 1];
                var g = games.FirstOrDefault(x => x.Platform.ToString().Equals(t, StringComparison.OrdinalIgnoreCase))
                        ?? games.FirstOrDefault(x => x.Path.Equals(Path.GetFullPath(t).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                if (g != null) return g;
                if (File.Exists(Path.Combine(t, "Among Us.exe"))) return new GameInstall(t, GameDetector.GuessPlatform(t), GameDetector.FindEpicAppName(t));
                throw new InstallException(Lang.T("err.noexe") + " " + t);
            }
            void Rep(double f, string m) => Console.WriteLine($"[{f * 100,3:F0}%] {m}");

            if (a.Contains("--payload"))
            {
                Console.WriteLine($"MRP {Payload.MrpVersion} {Payload.MrpTime}  BepInEx {Payload.BepInExVersion}  {Payload.Index.Count} files {ModInstaller.Size(Payload.TotalSize)} (zip {ModInstaller.Size(Payload.CompressedSize)})");
                foreach (var kv in Payload.Index.OrderBy(k => k.Key).Where(k => !k.Key.StartsWith("dotnet/")))
                    Console.WriteLine($"  {kv.Key}  {kv.Value.Size}");
            }
            if (a.Contains("--list"))
            {
                for (int i = 0; i < games.Count; i++)
                {
                    var g = games[i];
                    Console.WriteLine($"{i + 1}. {g}  state={g.State} current={g.MrpIsCurrent} 64bit={g.Is64Bit} bootaccel={g.HasPatcher} epic={g.EpicAppName ?? "-"}");
                    if (g.OtherPlugins.Count > 0) Console.WriteLine($"   other: {string.Join(", ", g.OtherPlugins)}");
                    foreach (string b in g.Backups) Console.WriteLine($"   backup: {Path.GetFileName(b)}  ({ModInstaller.BackupDescription(b)})");
                }
                if (games.Count == 0) Console.WriteLine("(none)");
            }
            if (a.Contains("--check-update") || a.Contains("--update") || a.Contains("--self-update"))
            {
                var rel = ReleaseCheck.Latest().GetAwaiter().GetResult();
                Console.WriteLine(rel == null ? "release: none" : $"release: {rel.Tag} ({rel.Version}) {rel.Published:yyyy-MM-dd} dll={(rel.DllUrl != null)} exe={(rel.ExeUrl != null)}");
                Console.WriteLine($"installer: v{ReleaseCheck.InstallerVersion.ToString(3)} outdated={ReleaseCheck.InstallerOutdated(rel)}");
                foreach (var g in games.Where(x => x.State is InstallState.Mrp or InstallState.MrpDisabled))
                    Console.WriteLine($"  {g}  mrp={ReleaseCheck.FileVersionOf(g.MrpDllPath)} outdated={ReleaseCheck.MrpOutdated(rel, g)}");
                if (a.Contains("--update"))
                {
                    string t = Arg("--update");
                    var targets = t != null && !t.StartsWith("--") ? new List<GameInstall> { Pick("--update") } : games.Where(x => ReleaseCheck.MrpOutdated(rel, x)).ToList();
                    foreach (var g in targets)
                    {
                        ReleaseCheck.InstallDll(g, rel, p => { }).GetAwaiter().GetResult();
                        Console.WriteLine(Lang.T("log.dllUpdated", rel.Tag) + " → " + g);
                    }
                }
                if (a.Contains("--self-update"))
                {
                    if (!ReleaseCheck.InstallerOutdated(rel)) Console.WriteLine("installer: up to date");
                    else Console.WriteLine("installer updated: " + ReleaseCheck.UpdateSelf(rel, p => { }).GetAwaiter().GetResult());
                }
            }
            if (a.Contains("--install")) ModInstaller.Install(Pick("--install"), Rep);
            if (a.Contains("--uninstall")) ModInstaller.Uninstall(Pick("--uninstall"), !a.Contains("--delete"), a.Contains("--restore"), Rep);
            if (a.Contains("--toggle")) Console.WriteLine(ModInstaller.Toggle(Pick("--toggle")) ? Lang.T("log.toggledOn") : Lang.T("log.toggledOff"));
            if (a.Contains("--clear-cache")) Console.WriteLine(Lang.T("log.cacheCleared", ModInstaller.Size(ModInstaller.ClearCache(Pick("--clear-cache")))));
            if (a.Contains("--diag")) Console.WriteLine(Lang.T("log.diag", Tools.Diagnostics(Pick("--diag"))));
            return 0;
        }
        catch (Exception e)
        {
            output.WriteLine(Lang.T("log.error", e is InstallException ? e.Message : e.ToString()));
            return 1;
        }
    }
}
