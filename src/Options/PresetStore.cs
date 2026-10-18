using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Options;

// 設定のプリセット (ホストの MRP の設定値と本編のゲーム設定に名前を付けて保存した物)。
// 保存先 = BepInEx/config/MoreRolesPlus/presets/<名前>.txt。
// 人に渡す・人からもらう時は「受け渡しのフォルダ」(PC はデスクトップの MRP_Presets) を通す:
// 書き出し = そのフォルダへ写す・取り込み = そのフォルダにある物を全部写してくる (同じ名前は上書きしない)
internal static class PresetStore
{
    public static string Dir => Path.Combine(Paths.ConfigPath, "MoreRolesPlus", "presets");

    public static string ShareDir => OperatingSystem.IsAndroid()
        ? Path.Combine(Application.persistentDataPath, "MRP_Presets")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MRP_Presets");

    private static string PathOf(string name) => Path.Combine(Dir, name + ".txt");

    // 名前に使えるのは文字・数字・_ と - だけ (置き場の外を指せないように)。Windows が予約している名前 (CON・NUL など) は使わない
    public static bool ValidName(string s) => s.Length is > 0 and <= 30 && Regex.IsMatch(s, @"^[\w\-]+\z") && !Reserved.IsMatch(s);

    private static readonly Regex Reserved = new(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z", RegexOptions.IgnoreCase);

    public static bool Exists(string name) => ValidName(name) && File.Exists(PathOf(name));

    public static List<string> Names()
    {
        var list = new List<string>();
        try
        {
            if (Directory.Exists(Dir))
                foreach (var f in Directory.GetFiles(Dir, "*.txt")) list.Add(Path.GetFileNameWithoutExtension(f));
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"presets list: {e.Message}"); }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    public static string Save(string name)
    {
        if (!ValidName(name)) return new Text("名前は文字・数字・_ と - で 30 文字まで", "Names: letters, digits, _ and -, up to 30");
        OptionSync.RestoreOwn();
        Registry.SaveTo(PathOf(name));
        SaveVanilla(PathOf(name));
        return new Text($"今の設定を「{name}」に保存しました", $"Saved the current settings as \"{name}\"");
    }

    // 読み込んで全員へ送る。読めなかったら元の設定に戻す
    public static string Load(string name)
    {
        if (!Exists(name)) return new Text($"「{name}」はありません", $"No preset \"{name}\"");
        OptionSync.RestoreOwn();
        if (!Registry.LoadFrom(PathOf(name)))
        {
            Registry.Load();
            return new Text($"「{name}」を読めませんでした", $"Couldn't read \"{name}\"");
        }
        Registry.Save();
        LoadVanilla(PathOf(name));
        if (AmongUsClient.Instance && AmongUsClient.Instance.AmHost) OptionSync.SendAll();
        LobbyView.OnOptionsReceived();
        SettingsMenu.RefreshAll();
        return new Text($"設定を「{name}」にしました", $"Loaded \"{name}\"");
    }

    // 本編のゲーム設定 (インポスターの数・速さ・本編の役職ごとの設定など) は本編の形式のまま 1 行に入れる
    private const string VanillaKey = "vanilla.options";

    private static void SaveVanilla(string path)
    {
        try
        {
            var gm = GameOptionsManager.Instance;
            var opts = gm?.CurrentGameOptions;
            if (opts == null) return;
            byte[] bytes = gm.gameOptionsFactory.ToBytes(opts, false);
            File.AppendAllText(path, VanillaKey + "=" + Convert.ToBase64String(bytes) + "\n");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"preset vanilla save: {e.Message}"); }
    }

    // 部屋の外か自分がホストの時だけ本編の設定を置き換えて、本編の保存と同期を回す
    private static void LoadVanilla(string path)
    {
        if (AmongUsClient.Instance && AmongUsClient.Instance.AmConnected && !AmongUsClient.Instance.AmHost) return;
        try
        {
            string b64 = null;
            foreach (string line in File.ReadAllLines(path))
                if (line.StartsWith(VanillaKey + "=")) b64 = line[(VanillaKey.Length + 1)..].Trim();
            if (string.IsNullOrEmpty(b64)) return;
            var gm = GameOptionsManager.Instance;
            if (gm == null) return;
            var opts = gm.gameOptionsFactory.FromBytes(Convert.FromBase64String(b64));
            if (opts == null || opts.GameMode != gm.CurrentGameOptions.GameMode) return;
            // 保存されるのはホストの設定の方 (今の設定と別に持っている) なので両方を置き換える
            gm.GameHostOptions = opts;
            gm.CurrentGameOptions = opts;
            Roles.Builtin.VanillaRoles.Dirty = true;
            Roles.Builtin.VanillaRoles.OnMenuClosed();
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"preset vanilla load: {e.Message}"); }
    }

    public static string Delete(string name)
    {
        if (!Exists(name)) return new Text($"「{name}」はありません", $"No preset \"{name}\"");
        File.Delete(PathOf(name));
        return new Text($"「{name}」を消しました", $"Deleted \"{name}\"");
    }

    public static string Export(string name)
    {
        if (!Exists(name)) return new Text($"「{name}」はありません", $"No preset \"{name}\"");
        try
        {
            Directory.CreateDirectory(ShareDir);
            File.Copy(PathOf(name), Path.Combine(ShareDir, name + ".txt"), true);
            return new Text($"「{name}」を書き出しました: {ShareDir}", $"Exported \"{name}\" to {ShareDir}");
        }
        catch (Exception e) { return new Text($"書き出せませんでした: {e.Message}", $"Couldn't export: {e.Message}"); }
    }

    // 受け渡しのフォルダにある物を全部取り込む (同じ名前が既にある物は飛ばす)
    public static string Import()
    {
        try
        {
            Directory.CreateDirectory(ShareDir);
            Directory.CreateDirectory(Dir);
            int added = 0, skipped = 0;
            foreach (var f in Directory.GetFiles(ShareDir, "*.txt"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (!ValidName(name) || File.Exists(PathOf(name))) { skipped++; continue; }
                File.Copy(f, PathOf(name), false);
                added++;
            }
            return new Text($"{added} 個取り込みました (飛ばした物 {skipped} 個)。置き場: {ShareDir}", $"Imported {added} (skipped {skipped}) from {ShareDir}");
        }
        catch (Exception e) { return new Text($"取り込めませんでした: {e.Message}", $"Couldn't import: {e.Message}"); }
    }

    // 受け渡しのフォルダをエクスプローラーで開く (PC)
    public static string OpenShare()
    {
        try
        {
            Directory.CreateDirectory(ShareDir);
            if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start("explorer.exe", "\"" + ShareDir + "\"");
            return ShareDir;
        }
        catch (Exception e) { return e.Message; }
    }
}
