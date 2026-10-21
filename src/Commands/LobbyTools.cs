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

    private static readonly Text OnlyLobby = new("LobbyTools.OnlyLobby");

    // ---- /say ----

    private static readonly RemoteCall<string> SayCall = new("Chat.Say", Route.HostToAll,
        (w, text) => w.Write(text), r => r.ReadString(), (_, text) => ShowSay(text));

    private static void ShowSay(string text)
    {
        DevChat.Notice(Vanilla.Chat, new Text("LobbyTools.HostMessage").With("text", text));
    }

    public static string Say(string args)
    {
        if (args.Length == 0) return new Text("LobbyTools.AddAMessage");
        if (!AmongUsClient.Instance || AmongUsClient.Instance.GameState is not (InnerNet.InnerNetClient.GameStates.Joined or InnerNet.InnerNetClient.GameStates.Started))
            return new Text("LobbyTools.NotInARoom");
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
        if (!sender || !InLobby || color >= Menu.CustomColors.Pickable || !HostAllows(sender)) return;
        if (sender.Data == null || sender.Data.DefaultOutfit.ColorId == color || TooSoon(sender)) return;
        // 本編のホストの手順 (使われている色なら空いている色に替える)
        sender.CheckColor(color);
    }

    public static string Rename(string args)
    {
        if (!InLobby) return OnlyLobby;
        string name = CleanName(args);
        if (name.Length is < 1 or > MaxName) return new Text("LobbyTools.BadNameLength").With("maxName", MaxName);
        if (!AmHost && !HostSettings.PlayersCanRename) return new Text("LobbyTools.RenameOff");
        RenameCall.Send(name);
        return AmHost ? new Text("LobbyTools.RenamedTo").With("name", name) : new Text("LobbyTools.RenameRequested").With("name", name);
    }

    public static string Color(string args)
    {
        if (!InLobby) return OnlyLobby;
        // #RRGGBB = 自分で作った色 (見た目だけ。本編の色番号はそのまま)・#RRGGBB #RRGGBB [向き 0〜3 [ぼかし 0〜100 [位置 0〜100]]] = 2 色のグラデーション・reset = 本編の色へ戻す
        string a = args.Trim();
        if (a.StartsWith('#') || a.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            if (!AmHost && !HostSettings.PlayersCanRename) return new Text("LobbyTools.RenameOff");
            if (a[0] != '#') { Menu.CustomColors.SetMine((UnityEngine.Color32?)null); return new Text("LobbyTools.ColorReset"); }
            var parts = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!Menu.CustomColors.TryParse(parts[0], out var custom)) return new Text("LobbyTools.ColorHexHowTo");
            if (parts.Length >= 2 && Menu.CustomColors.TryParse(parts[1], out var second))
            {
                byte dir = parts.Length >= 3 && byte.TryParse(parts[2], out byte d) && d < Menu.CustomColors.DirCount ? d : (byte)0;
                // 4 つ目 = 境目のぼかし (0 = くっきり〜100)・5 つ目 = 境目の位置 (0〜100)
                byte soft = parts.Length >= 4 && byte.TryParse(parts[3], out byte sf) ? Math.Min(sf, (byte)100) : Menu.CustomColors.DefaultSoft;
                byte pos = parts.Length >= 5 && byte.TryParse(parts[4], out byte ps) ? Math.Min(ps, (byte)100) : Menu.CustomColors.DefaultPos;
                Menu.CustomColors.SetLook(new Menu.CustomColors.Look(custom, second, true, dir, soft, pos));
                return new Text("LobbyTools.ColorGradient").With("color", Menu.CustomColors.Hex(custom)).With("color2", Menu.CustomColors.Hex(second));
            }
            Menu.CustomColors.SetMine(custom);
            return new Text("LobbyTools.ColorSet").With("color", Menu.CustomColors.Hex(custom));
        }
        int color = FindColor(args);
        if (color < 0) return new Text("LobbyTools.ColorHowTo").With("max", Menu.CustomColors.Pickable - 1).With("names", ColorNames());
        if (!AmHost && !HostSettings.PlayersCanRename) return new Text("LobbyTools.RenameOff");
        ColorCall.Send((byte)color);
        return new Text("LobbyTools.ColorChanging").With("color", ColorName(color));
    }

    private static string ColorName(int i) => TranslationController.Instance.GetString(Palette.ColorNames[i]);

    private static int FindColor(string s)
    {
        if (int.TryParse(s, out int n)) return n >= 0 && n < Menu.CustomColors.Pickable ? n : -1;
        for (int i = 0; i < Palette.ColorNames.Length && i < Menu.CustomColors.Pickable; i++)
            if (string.Equals(ColorName(i), s, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string ColorNames()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Palette.ColorNames.Length && i < Menu.CustomColors.Pickable; i++)
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
        Text usage = new("LobbyTools.PresetUsage");
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
        return names.Count > 0 ? string.Join(", ", names) : new Text("LobbyTools.NoSavedPresets");
    }
}
