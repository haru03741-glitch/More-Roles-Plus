using System.Collections.Generic;
using HarmonyLib;
using Hazel;
using InnerNet;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Net;

// 全員が同じ版の MRP を入れているかの確認。
// 各自が自分のキャラが出た時に版と指紋 (と一緒に入れている BepInEx の mod の名前) を送り、ホストが覚えておく。
// 同じ版の人には設定値を送り、入っていない人・版が違う人がいる間はゲームを始めさせない。
// ホストは同じ形でその人へ自分の版を返し、版が違う人の画面にはその理由を出す (ホストの画面に出るだけでは本人が気づけない)。
// 危ない mod (他人を動かす・部屋を覗くなど) を入れた人が来たら、ホストの画面に知らせる
internal static class VersionCheck
{
    // clientId → 同じ版か
    private static readonly Dictionary<int, bool> Seen = new();
    private static readonly Dictionary<int, string> SeenVersion = new();
    private static readonly Dictionary<int, int> SeenAbi = new();
    private static readonly Dictionary<int, List<string>> SeenMods = new();
    private static int _notedHost = -1; // 版の違いを知らせたホスト (同じホストには 1 回だけ)
    private const int MaxMods = 24, MaxModName = 48;

    // Based on the client mod blacklist in https://github.com/AU-Avengers/TOU-Mira TownOfUs/Networking/SendModInfoRpc.cs (GPL-3.0)
    private static readonly string[] BadMods = { "MalumMenu", "SickoMenu", "SigmaMenu", "MoveMod", "Move Mod", "Get All Lobbies", "Mod Menu" };
    // clientId → 入ってきた時刻。版の知らせは入ってから 1 秒あまり後に届くので、その前に始めようとした時は待つ
    private static readonly Dictionary<int, long> JoinedAt = new();
    private const long CheckingMs = 10000;
    private static long _startPendingUntil; // 確認が済み次第始める (0 = 待っていない)

    // 自分のキャラが出た合図から少し待って送る (Start はコルーチンで、合図の時点ではまだ送れないことがある)
    private static bool _helloPending;
    private static long _helloAtMs;
    private static System.IntPtr _helloSentFor; // 同じ自分のキャラには 1 回だけ送る

    public static void ScheduleHello()
    {
        _helloPending = true;
        _helloAtMs = System.Environment.TickCount64 + 1000;
    }

    public static void Tick()
    {
        if (!_helloPending || System.Environment.TickCount64 < _helloAtMs) return;
        _helloPending = false;
        var client = AmongUsClient.Instance;
        var me = PlayerControl.LocalPlayer;
        if (!client || client.AmHost || !me || me.Pointer == _helloSentFor) return;
        _helloSentFor = me.Pointer;
        SendHello();
    }

    public static void SendHello() => WriteHello(-1, true);

    // target = -1 で全員 (読むのはホストだけ)・番号でその人だけ (ホストの返事)
    private static void WriteHello(int target, bool withMods)
    {
        int abi = Native.MrpNative.Abi;
        var mods = withMods ? OtherMods() : null;
        Plugin.Logger.LogInfo($"hello -> {(target < 0 ? "host" : $"client {target}")}: {Plugin.Version} fp={Registry.Fingerprint:X8} abi={abi} mods={mods?.Count ?? 0}");
        var w = Rpc.Start(Rpc.Hello, target);
        w.Write(Plugin.Version);
        w.Write(Registry.Fingerprint);
        w.Write(abi);
        if (mods != null)
        {
            w.Write((byte)mods.Count);
            foreach (var m in mods) w.Write(m);
        }
        Rpc.Finish(w);
    }

    // この mod 以外に読み込まれている BepInEx の mod (名前と版)
    private static List<string> OtherMods()
    {
        var list = new List<string>();
        try
        {
            foreach (var kv in BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance.Plugins)
            {
                var meta = kv.Value?.Metadata;
                if (meta == null || meta.GUID == Plugin.Guid) continue;
                string name = $"{meta.Name} {meta.Version}";
                list.Add(name.Length > MaxModName ? name[..MaxModName] : name);
                if (list.Count >= MaxMods) break;
            }
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"mod list: {e.Message}"); }
        return list;
    }

    private static string BadModIn(List<string> mods)
    {
        foreach (var m in mods)
            foreach (var bad in BadMods)
                if (m.IndexOf(bad, System.StringComparison.OrdinalIgnoreCase) >= 0) return m;
        return null;
    }

    public static void Receive(PlayerControl sender, MessageReader r)
    {
        string version = r.ReadString();
        uint fp = r.ReadUInt32();
        int abi = r.BytesRemaining >= 4 ? r.ReadInt32() : -1; // 古い版は送ってこない
        if (!AmongUsClient.Instance.AmHost)
        {
            if (Rpc.FromHost(sender)) OnHostHello(sender, version, fp, abi);
            return;
        }
        var mods = new List<string>();
        if (r.BytesRemaining >= 1)
        {
            int n = System.Math.Min((int)r.ReadByte(), MaxMods);
            for (int i = 0; i < n && r.BytesRemaining > 0; i++) mods.Add(r.ReadString());
        }
        bool same = version == Plugin.Version && fp == Registry.Fingerprint && abi == Native.MrpNative.Abi;
        Seen[sender.OwnerId] = same;
        SeenVersion[sender.OwnerId] = version;
        SeenAbi[sender.OwnerId] = abi;
        SeenMods[sender.OwnerId] = mods;
        Plugin.Logger.LogInfo($"hello from client {sender.OwnerId}: {version} fp={fp:X8} abi={abi} same={same} mods=[{string.Join(", ", mods)}]");
        string bad = BadModIn(mods);
        if (bad != null)
            Menu.ChatNotes.Post(new Text("ModCheck.Bad").With("name", sender.Data != null ? sender.Data.PlayerName : $"#{sender.PlayerId}").With("mod", bad));
        WriteHello(sender.OwnerId, false);
        if (same) OptionSync.SendAll(sender.OwnerId);
        if (_startPendingUntil != 0 && Blockers(out _).Count == 0 && _startPendingUntil != 0) // Blockers が期限切れを消す
        {
            _startPendingUntil = 0;
            // 数えが終わって読み込み中にもう一度始めると、ロビーの部品が壊れて毎フレーム例外が出る
            if (Vanilla.StartManager is { } gsm && !ShipStatus.Instance
                && gsm.startState == GameStartManager.StartingStates.NotStarting
                && AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Joined)
                gsm.BeginGame();
        }
    }

    // 客の側: ホストの版と比べて、違えば理由を自分の画面に出す
    internal static void OnHostHello(PlayerControl host, string version, uint fp, int abi)
    {
        Plugin.Logger.LogInfo($"hello from host: {version} fp={fp:X8} abi={abi}");
        if (_notedHost == host.OwnerId) return;
        string reason = null;
        if (version != Plugin.Version) reason = new Text("ModCheck.YouDifferVersion").With("host", version).With("mine", Plugin.Version);
        else if (abi != Native.MrpNative.Abi) reason = new Text("ModCheck.YouDifferNative");
        else if (fp != Registry.Fingerprint) reason = new Text("ModCheck.YouDifferBuild");
        if (reason == null) return;
        _notedHost = host.OwnerId;
        Menu.ChatNotes.Post(reason);
    }

    // /mods: 部屋の人が入れている mod (ホストだけが知っている)
    public static string ModsText(string _)
    {
        var sb = new System.Text.StringBuilder();
        int host = AmongUsClient.Instance.HostId;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.notRealPlayer || p.isDummy) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(p.Data != null ? p.Data.PlayerName : $"#{p.PlayerId}").Append(": ");
            List<string> mods = p.OwnerId == host ? OtherMods() : SeenMods.TryGetValue(p.OwnerId, out var m) ? m : null;
            if (mods == null) sb.Append(new Text("ModCheck.Unknown").ToString());
            else if (mods.Count == 0) sb.Append(new Text("ModCheck.None").ToString());
            else
            {
                string bad = BadModIn(mods);
                sb.Append(string.Join(", ", mods));
                if (bad != null) sb.Append(" <color=#FF5050>").Append(new Text("ModCheck.BadMark").ToString()).Append("</color>");
            }
        }
        return sb.ToString();
    }

    public static void OnClientJoined(int clientId) => JoinedAt[clientId] = System.Environment.TickCount64;

    // 客の側: 版の知らせはホストだけが覚えているので、ホストが抜けて替わったら新しいホストへ送り直す
    private static int _host = -1;

    public static void RememberHost() => _host = AmongUsClient.Instance.HostId;

    public static void CheckHost(int leftId)
    {
        var client = AmongUsClient.Instance;
        bool changed = _host != -1 && (leftId == _host || client.HostId != _host);
        _host = client.HostId;
        if (!changed) return;
        if (client.HostId != leftId) HostChange.OnChanged(); // 番号がまだ抜けた人のままなら、替わった知らせは後で来る方に任せる
        // 本編が自分をホストにするのは抜けた人の知らせの前のことも後のこともあるので、先に来た方で引き継ぐ
        if (client.AmHost) { HostChange.OnBecomeHost(); return; }
        _helloSentFor = System.IntPtr.Zero;
        ScheduleHello();
    }

    // 新しいホストの側: 前のホストが受けた知らせは引き継げないので、今いる人は入ってきたばかりと同じに待つ
    public static void OnBecomeHost()
    {
        var client = AmongUsClient.Instance;
        bool changed = _host != -1 && _host != client.ClientId;
        _host = client.ClientId;
        if (changed)
        {
            HostChange.OnChanged();
            HostChange.OnBecomeHost();
        }
        Seen.Clear();
        SeenVersion.Clear();
        SeenAbi.Clear();
        SeenMods.Clear();
        _startPendingUntil = 0;
        long now = System.Environment.TickCount64;
        foreach (var p in PlayerControl.AllPlayerControls)
            if (p) JoinedAt[p.OwnerId] = now;
    }

    // 始めようとした時に確認中の人しか残っていなければ、確認が済み次第始める
    public static void HoldStart() => _startPendingUntil = System.Environment.TickCount64 + CheckingMs;
    public static void CancelHeldStart() => _startPendingUntil = 0;

    public static bool IsSame(int clientId) => Seen.TryGetValue(clientId, out bool s) && s;

    // 部屋に入る (自分が立てた時も) たびに、前の部屋の照合結果と送った記録を捨てる
    public static void OnJoined()
    {
        Seen.Clear();
        SeenVersion.Clear();
        SeenAbi.Clear();
        SeenMods.Clear();
        JoinedAt.Clear();
        _startPendingUntil = 0;
        _host = -1;
        _notedHost = -1;
        Menu.ChatNotes.Clear();
        _helloSentFor = System.IntPtr.Zero;
        HostChange.Clear();
    }

    public static void ForgetClient(int clientId) { Seen.Remove(clientId); SeenVersion.Remove(clientId); SeenAbi.Remove(clientId); SeenMods.Remove(clientId); JoinedAt.Remove(clientId); }

    // 始められない人の一覧 (空なら始めてよい)。checking = そのうち入ってきたばかりで版の知らせを待っている人の数
    public static List<string> Blockers(out int checking)
    {
        var list = new List<string>();
        checking = 0;
        long now = System.Environment.TickCount64;
        if (_startPendingUntil != 0 && now > _startPendingUntil) _startPendingUntil = 0;
        int host = AmongUsClient.Instance.HostId;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.OwnerId == host || p.notRealPlayer || p.isDummy) continue; // ダミーはホストの端末で動く
            if (IsSame(p.OwnerId)) continue;
            string name = p.Data != null ? p.Data.PlayerName : $"#{p.PlayerId}";
            if (!SeenVersion.TryGetValue(p.OwnerId, out string v))
            {
                if (JoinedAt.TryGetValue(p.OwnerId, out long at) && now - at < CheckingMs)
                {
                    checking++;
                    list.Add(new Text("VersionCheck.Checking").With("name", name));
                }
                else list.Add(new Text("VersionCheck.NoMRP").With("name", name));
            }
            else if (v == Plugin.Version && SeenAbi.TryGetValue(p.OwnerId, out int abi) && abi != Native.MrpNative.Abi)
                list.Add(new Text("VersionCheck.DifferentNativeModule").With("name", name));
            else
                list.Add(new Text("VersionCheck.DifferentVersion").With("name", name).With("version", v));
        }
        return list;
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Start))]
internal static class HelloOnSpawnPatch
{
    // Start はコルーチンなので、この時点では自分のキャラかどうかもまだ決まっていないことがある。
    // 誰かが出たら合図だけ出し、送るかどうかは少し後で決める
    public static void Postfix()
    {
        if (AmongUsClient.Instance && !AmongUsClient.Instance.AmHost) VersionCheck.ScheduleHello();
    }
}

[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.BeginGame))]
internal static class BlockStartPatch
{
    public static bool Prefix()
    {
        if (!AmongUsClient.Instance.AmHost) return true;
        var blockers = VersionCheck.Blockers(out int checking);
        if (blockers.Count == 0) { VersionCheck.CancelHeldStart(); return true; }
        if (checking == blockers.Count)
        {
            VersionCheck.HoldStart();
            if (Vanilla.Chat is { } c)
                c.AddChat(PlayerControl.LocalPlayer, new Text("VersionCheck.WaitingForNewPlayers"), false);
            return false;
        }
        string msg = new Text("VersionCheck.Mismatch")
                     + string.Join("\n", blockers);
        if (Vanilla.Chat is { } chat) chat.AddChat(PlayerControl.LocalPlayer, msg, false);
        return false;
    }
}

[HarmonyPatch(typeof(AmongUsClient), "OnGameJoined")]
internal static class ResetOnJoinPatch
{
    public static void Prefix()
    {
        VersionCheck.OnJoined();
        Roles.PracticeMatch.OnJoined();
        Commands.ChatGuard.Reset();
    }

    public static void Postfix() => VersionCheck.RememberHost();
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerJoined))]
internal static class JoinedAtPatch
{
    public static void Postfix([HarmonyArgument(0)] ClientData data)
    {
        if (data != null) VersionCheck.OnClientJoined(data.Id);
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerLeft))]
internal static class ForgetLeftPatch
{
    public static void Postfix([HarmonyArgument(0)] ClientData data)
    {
        if (data != null) VersionCheck.ForgetClient(data.Id);
        VersionCheck.CheckHost(data != null ? data.Id : -1);
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnBecomeHost))]
internal static class BecomeHostPatch
{
    public static void Postfix() => VersionCheck.OnBecomeHost();
}
