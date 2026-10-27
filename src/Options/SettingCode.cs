using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Options;

// 設定を 1 行の文字 (設定のコード) にしてチャットや SNS で渡す。
// 中身 = 初期値から変えた MRP の項目 (「鍵=値」・プリセットのファイルと同じ書き方) + 本編のゲーム設定。
// 縮めて base64 にし、頭に版の印を付ける。読む時は初期値へ戻してから中身を当てる (プリセットを読むのと同じ)
internal static class SettingCode
{
    private const string Prefix = "MRP1.";
    private const int MaxChars = 32 * 1024;     // 受け取る文字の上限 (大きな物でメモリを食わせない)
    private const int MaxText = 256 * 1024;     // 展開した後の上限

    // 今の値をコードにする (客の時はホストから届いた値)
    public static string Make()
    {
        var sb = new StringBuilder();
        sb.Append(OptionMigrations.VersionKey).Append('=').Append(OptionMigrations.Current).Append('\n');
        foreach (var o in Registry.All)
            if (o.Changed) sb.Append(o.Key).Append('=').Append(o.Save()).Append('\n');
        string vanilla = PresetStore.VanillaLine();
        if (vanilla != null) sb.Append(vanilla).Append('\n');

        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
        {
            byte[] raw = Encoding.UTF8.GetBytes(sb.ToString());
            z.Write(raw, 0, raw.Length);
        }
        return Prefix + Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Copy()
    {
        try
        {
            string code = Make();
            GUIUtility.systemCopyBuffer = code;
            return new Text("SettingCode.Copied").With("count", code.Length);
        }
        catch (Exception e) { return new Text("SettingCode.CouldntMake").With("message", e.Message); }
    }

    // 貼り付けてある文字から読み込んで全員へ送る (ホストか部屋の外だけ)。1 回の「元に戻す」で MRP の値は戻る
    public static string Paste()
    {
        if (AmongUsClient.Instance && AmongUsClient.Instance.AmConnected && !AmongUsClient.Instance.AmHost)
            return new Text("PresetPage.OnlyTheHostCanLoad");
        string text;
        try { text = Decode(GUIUtility.systemCopyBuffer); }
        catch (Exception) { text = null; }
        if (text == null) return new Text("SettingCode.NotACode");

        string path = Path.Combine(PresetStore.Dir, ".code.tmp");
        OptionSync.RestoreOwn();
        var before = new int[Registry.All.Count];
        for (int i = 0; i < before.Length; i++) before[i] = Registry.All[i].Index;
        try
        {
            Directory.CreateDirectory(PresetStore.Dir);
            File.WriteAllText(path, text);
            if (!Registry.LoadFrom(path))
            {
                Registry.Load();
                return new Text("SettingCode.NotACode");
            }
            PresetStore.LoadVanilla(path);
        }
        finally
        {
            try { File.Delete(path); } catch (Exception) { }
        }
        Registry.Save();

        OptHistory.BeginGroup();
        int changed = 0;
        for (int i = 0; i < before.Length; i++)
        {
            var o = Registry.All[i];
            if (o.Index == before[i]) continue;
            OptHistory.Push(o, before[i]);
            OptEditor.AfterStep(o);
            changed++;
        }
        OptHistory.EndGroup();

        PresetLabel.Recheck();
        if (AmongUsClient.Instance && AmongUsClient.Instance.AmHost) OptionSync.SendAll();
        LobbyView.OnOptionsReceived();
        SettingsMenu.RefreshAll();
        RolePage.RefreshCards();
        return new Text("SettingCode.Loaded").With("count", changed);
    }

    // コードを中身の文字へ。コードでなければ null
    internal static string Decode(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        code = code.Trim();
        if (code.Length > MaxChars || !code.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        string b64 = code[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        byte[] packed = Convert.FromBase64String(b64);
        using var z = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        var buf = new byte[4096];
        int n;
        while ((n = z.Read(buf, 0, buf.Length)) > 0)
        {
            outMs.Write(buf, 0, n);
            if (outMs.Length > MaxText) return null;
        }
        string text = Encoding.UTF8.GetString(outMs.ToArray());
        return text.StartsWith(OptionMigrations.VersionKey + "=", StringComparison.Ordinal) ? text : null;
    }
}
