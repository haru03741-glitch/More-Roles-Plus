// Based on the /dump command in https://github.com/Gurge44/EndlessHostRoles (GPL-3.0)
using System;
using System.IO;
using System.IO.Compression;
using BepInEx;
using UnityEngine;

namespace MoreRolesPlus.Commands;

// /dump: 不具合の報告用に、ログと設定を 1 つの zip にしてデスクトップの MRP_Logs へ置く (PC)。
// BAN 一覧などの他の人の身元が入ったファイルは入れない。書き込み中のログも読めるように共有で開く
internal static class LogDump
{
    public static string Run(string _)
    {
        if (OperatingSystem.IsAndroid())
            return new Text("LogDump.OnAndroidUseShareLogs");
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MRP_Logs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"MRP-log-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            int n = 0;
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                n += Add(zip, Path.Combine(Paths.BepInExRootPath, "LogOutput.log"), "LogOutput.log");
                n += Add(zip, Path.Combine(Paths.BepInExRootPath, "ErrorLog.log"), "ErrorLog.log");
                n += Add(zip, Path.Combine(Application.persistentDataPath, "Player.log"), "Player.log");
                n += Add(zip, Path.Combine(Application.persistentDataPath, "Player-prev.log"), "Player-prev.log");
                n += Add(zip, Path.Combine(Paths.ConfigPath, Plugin.Guid + ".cfg"), "MoreRolesPlus.cfg");
                n += Add(zip, Path.Combine(Paths.ConfigPath, "MoreRolesPlus.options.txt"), "MoreRolesPlus.options.txt");
            }
            Plugin.Logger.LogInfo($"log dump: {path} ({n} files)");
            return new Text("LogDump.SavedTheLogsFiles").With("n", n).With("path", path);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"log dump: {e}");
            return new Text("LogDump.CouldntSaveTheLogs").With("message", e.Message);
        }
    }

    private static int Add(ZipArchive zip, string src, string name)
    {
        if (!File.Exists(src)) return 0;
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Fastest).Open();
        input.CopyTo(output);
        return 1;
    }
}
