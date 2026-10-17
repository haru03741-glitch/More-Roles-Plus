using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using MoreRolesPlus.Dev;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Commands;

// /say (ホストから全員へのお知らせ)・/rename /color (自分の名前と色をロビーで変える)・/preset (設定の保存と読み込み)
internal static class LobbyTools
{
    private const int MaxSay = 300;
    private const int MaxName = 15;

    private static bool AmHost => AmongUsClient.Instance && AmongUsClient.Instance.AmHost;
    private static bool InLobby => Vanilla.StartManager && !ShipStatus.Instance && PlayerControl.LocalPlayer;

    private static readonly Text OnlyLobby = new("ロビーでだけ使えます", "Only in the lobby");

    // ---- /say ----

    private static readonly RemoteCall<string> SayCall = new("Chat.Say", Route.HostToAll,
        (w, text) => w.Write(text), r => r.ReadString(), (_, text) => ShowSay(text));

    private static void ShowSay(string text)
    {
        DevChat.Notice(Vanilla.Chat, new Text($"<color=#FF5050>ホストから</color>: {text}", $"<color=#FF5050>From the host</color>: {text}"));
    }

    public static string Say(string args)
    {
        if (args.Length == 0) return new Text("言うことを付けてください", "Add a message");
        if (!AmongUsClient.Instance || AmongUsClient.Instance.GameState is not (InnerNet.InnerNetClient.GameStates.Joined or InnerNet.InnerNetClient.GameStates.Started))
            return new Text("部屋に入っていません", "Not in a room");
        string text = args.Length > MaxSay ? args[..MaxSay] : args;
        SayCall.Send(text);
        ShowSay(text);
        return null;
    }

    // ---- /rename /color (客はホストに頼み、ホストが本編の手順で全員へ配る) ----

    private static readonly RemoteCall<string> RenameCall = new("Lobby.Rename", Route.ToHost,
        (w, name) => w.Write(name), r => r.ReadString(), (sender, name) => ApplyName(sender, name));

    private static readonly RemoteCall<byte> ColorCall = new("Lobby.Color", Route.ToHost,
        (w, c) => w.Write(c), r => r.ReadByte(), (sender, c) => ApplyColor(sender, c));

    // タグと、制御文字・書式文字 (改行・ゼロ幅・向きの切り替えなど) を除く
    private static string CleanName(string s)
    {
        s = Regex.Replace(s ?? "", "<[^>]*>", "");
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (!char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format) sb.Append(c);
        return sb.ToString().Trim();
    }

    private static bool HostAllows(PlayerControl sender)
        => sender && (sender.OwnerId == AmongUsClient.Instance.HostId || HostSettings.PlayersCanRename);

    // 頼みを連打されても、ホストが全員へ配る電文が膨らまないように 1 人 1 秒に 1 回まで
    private const long MinGapMs = 1000;
    private static readonly long[] LastAskMs = new long[256];

    private static bool TooSoon(PlayerControl sender)
    {
        long now = Environment.TickCount64;
        if (now - LastAskMs[sender.PlayerId] < MinGapMs) return true;
        LastAskMs[sender.PlayerId] = now;
        return false;
    }

    private static void ApplyName(PlayerControl sender, string name)
    {
        name = CleanName(name);
        if (!sender || !InLobby || name.Length is < 1 or > MaxName || !HostAllows(sender)) return;
        if (sender.Data == null || sender.Data.PlayerName == name || TooSoon(sender)) return;
        // 他の人と同じ名前にはしない
        foreach (var p in PlayerControl.AllPlayerControls)
            if (p && p != sender && p.Data != null && string.Equals(p.Data.PlayerName, name, StringComparison.OrdinalIgnoreCase)) return;
        Plugin.Logger.LogInfo($"rename {sender.PlayerId} -> {name}");
        sender.RpcSetName(name);
    }

    private static void ApplyColor(PlayerControl sender, byte color)
    {
        if (!sender || !InLobby || color >= Palette.PlayerColors.Length || !HostAllows(sender)) return;
        if (sender.Data == null || sender.Data.DefaultOutfit.ColorId == color || TooSoon(sender)) return;
        // 本編のホストの手順 (使われている色なら空いている色に替える)
        sender.CheckColor(color);
    }

    public static string Rename(string args)
    {
        if (!InLobby) return OnlyLobby;
        string name = CleanName(args);
        if (name.Length is < 1 or > MaxName) return new Text($"名前は 1〜{MaxName} 文字にしてください", $"Names must be 1-{MaxName} characters");
        if (!AmHost && !HostSettings.PlayersCanRename) return new Text("この部屋ではホストが名前と色の変更を止めています", "The host has turned off name and color changes");
        RenameCall.Send(name);
        return AmHost ? new Text($"名前を {name} にしました", $"Renamed to {name}") : new Text($"名前を {name} にするようホストに頼みました", $"Asked the host to rename you to {name}");
    }

    public static string Color(string args)
    {
        if (!InLobby) return OnlyLobby;
        // #RRGGBB = 自分で作った色 (見た目だけ。本編の色番号はそのまま)・reset = 本編の色へ戻す
        string a = args.Trim();
        if (a.StartsWith('#') || a.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            if (!AmHost && !HostSettings.PlayersCanRename) return new Text("この部屋ではホストが名前と色の変更を止めています", "The host has turned off name and color changes");
            if (a[0] != '#') { Menu.CustomColors.SetMine(null); return new Text("本編の色に戻しました", "Back to the game's color"); }
            if (!Menu.CustomColors.TryParse(a, out var custom)) return new Text("#RRGGBB の形で (例 #00B7FF)", "Use #RRGGBB (e.g. #00B7FF)");
            Menu.CustomColors.SetMine(custom);
            return new Text($"色を #{Menu.CustomColors.Hex(custom)} にしました", $"Your color is now #{Menu.CustomColors.Hex(custom)}");
        }
        int color = FindColor(args);
        if (color < 0) return new Text($"色は番号 (0〜{Palette.PlayerColors.Length - 1}) か色の名前で: {ColorNames()}", $"Use a number (0-{Palette.PlayerColors.Length - 1}) or a color name: {ColorNames()}");
        if (!AmHost && !HostSettings.PlayersCanRename) return new Text("この部屋ではホストが名前と色の変更を止めています", "The host has turned off name and color changes");
        ColorCall.Send((byte)color);
        return new Text($"色を {ColorName(color)} に変えます (使われていたら空いている色)", $"Changing your color to {ColorName(color)} (or a free one if taken)");
    }

    private static string ColorName(int i) => TranslationController.Instance.GetString(Palette.ColorNames[i]);

    private static int FindColor(string s)
    {
        if (int.TryParse(s, out int n)) return n >= 0 && n < Palette.PlayerColors.Length ? n : -1;
        for (int i = 0; i < Palette.ColorNames.Length && i < Palette.PlayerColors.Length; i++)
            if (string.Equals(ColorName(i), s, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string ColorNames()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Palette.ColorNames.Length && i < Palette.PlayerColors.Length; i++)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(i).Append('=').Append(ColorName(i));
        }
        return sb.ToString();
    }

    // ---- /preset (ホストの設定を名前を付けて保存・読み込み。中身は PresetStore) ----

    public static string Preset(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        string name = parts.Length > 1 ? parts[1].Trim() : "";
        Text usage = new("/preset (一覧)・/preset save|load|del|export <名前>・/preset import (設定画面の「プリセット」のタブでも同じことができる)",
            "/preset (list), /preset save|load|del|export <name>, /preset import (also in the Presets tab of the settings menu)");
        if (sub is "" or "list") return List();
        if (sub == "import") return PresetStore.Import();
        if (!PresetStore.ValidName(name)) return usage;
        switch (sub)
        {
            case "save": return PresetStore.Save(name);
            case "load":
                if (!InLobby) return OnlyLobby;
                return PresetStore.Load(name);
            case "del":
            case "delete": return PresetStore.Delete(name);
            case "export": return PresetStore.Export(name);
            default: return usage;
        }
    }

    private static string List()
    {
        var names = PresetStore.Names();
        return names.Count > 0 ? string.Join(", ", names) : new Text("保存した設定はありません", "No saved presets");
    }
}
