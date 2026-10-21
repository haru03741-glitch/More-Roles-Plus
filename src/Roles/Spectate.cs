// Based on the spectator / GM of https://github.com/Gurge44/EndlessHostRoles (GPL-3.0)
using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using HarmonyLib;
using InnerNet;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles;

[Settings(Tab.General, "SpectateSettings.Title", order: -65)]
public static class SpectateSettings
{
    public static readonly BoolOpt Allow = new("SpectateSettings.Allow", true);
}

// 観戦: ロビーで「次の試合を観戦する」を選んだ人は、試合の始めから幽霊として見るだけになる。
// - 本人の端末がホストへ「観戦したい / しない」を送る (Esc の「MRP の設定」・/spectate)。
// - ホストは本編が陣営を決めた直後に、観戦の人がインポスターに選ばれていたら他のクルーと入れ替え、MRP の役職も配らない。
// - 始まりの場面が終わったら、ホストが観戦の人の一覧を全員へ送り、各端末がその人を死体なしで死なせてタスクを外す
//   (本編の追放の処理を借りる。追放の出来事は起こさない)。勝ち負けの人数には生きている人しか数えないので影響しない。
// - 観戦の人は会議で全員の役職が見える
internal static class Spectate
{
    private static readonly HashSet<byte> Wants = new();     // ホスト: 観戦したい人
    private static readonly HashSet<byte> Watching = new();  // 全員: この試合の観戦の人
    private static bool _applied;
    internal static bool IntroStarted;
    private static long _shipSince;
    internal static byte Applying = 255;                      // 今幽霊にしている人 (追放の出来事を起こさないための印)

    private static readonly RemoteCall<bool> Want = new("Spectate.Want", Route.ToHost,
        (w, v) => w.Write(v), r => r.ReadBoolean(), (sender, v) => OnWant(sender, v));

    private static readonly RemoteCall<List<byte>> Apply = new("Spectate.Apply", Route.HostToAll,
        (w, ids) => { w.Write((byte)ids.Count); foreach (var id in ids) w.Write(id); },
        r => { int n = r.ReadByte(); var l = new List<byte>(n); for (int i = 0; i < n; i++) l.Add(r.ReadByte()); return l; },
        (_, ids) => ApplyLocal(ids));

    public static bool IsWatching(byte pid) => Watching.Contains(pid);

    // ---- 本人 ----

    public static void OnOptionChanged() => SendMine();

    // ロビーに戻った時、観戦を選んでいれば送り直す (試合中に選んだ分を届ける)
    private static void ResendInLobby()
    {
        if (!Menu.ClientOptions.Spectate.Value) return;
        _sendPending = true;
        _sendAt = Environment.TickCount64 + 1000;
    }

    // ホストが替わった: 観戦の希望は前のホストのメモリにしか無いので、ロビーなら送り直す (試合中ならロビーに戻った時に送る)
    public static void OnHostChanged()
    {
        if (!ShipStatus.Instance) ResendInLobby();
    }

    private static void SendMine()
    {
        if (!PlayerControl.LocalPlayer || !AmongUsClient.Instance) return;
        Want.Send(Menu.ClientOptions.Spectate.Value);
    }

    private static bool _sendPending;
    private static long _sendAt;

    // 自分のキャラが出てきた (部屋に入った): 観戦を選んでいれば少し待ってからホストへ伝える
    public static void OnSpawned(PlayerControl p)
    {
        if (!p || !p.AmOwner || !Menu.ClientOptions.Spectate.Value) return;
        _sendPending = true;
        _sendAt = Environment.TickCount64 + 1500;
    }

    public static string Command(string _)
    {
        if (ShipStatus.Instance) return new Text("Spectate.OnlyInLobby");
        var e = Menu.ClientOptions.Spectate;
        e.Value = !e.Value;
        SendMine();
        return e.Value ? new Text("Spectate.WillSpectate")
                       : new Text("Spectate.WillPlay");
    }

    // ---- ホスト ----

    private static void OnWant(PlayerControl sender, bool v)
    {
        // やめるのはいつでも受ける。観戦したいは試合中には受けない (次のロビーで送り直す)
        if (!sender || (v && ShipStatus.Instance)) return;
        if (v) Wants.Add(sender.PlayerId);
        else Wants.Remove(sender.PlayerId);
        Plugin.Logger.LogInfo($"spectate: {sender.Data?.PlayerName} {(v ? "on" : "off")}");
    }

    public static void OnLeft(PlayerControl p)
    {
        if (p) Wants.Remove(p.PlayerId);
    }

    // この試合の観戦の人 (今いる人だけ)。ホストが本編の陣営を決めた直後に呼ぶ
    private static readonly List<PlayerControl> Chosen = new();

    public static bool IsChosen(byte pid)
    {
        foreach (var p in Chosen)
            if (p && p.PlayerId == pid) return true;
        return false;
    }

    // 観戦の人がインポスターに選ばれていたら、観戦でないクルーと入れ替える。入れ替える相手がいなければその人は観戦をやめる
    public static void AfterVanillaSelection()
    {
        Chosen.Clear();
        _applied = IntroStarted = false;
        _shipSince = 0;
        if (!SpectateSettings.Allow || Wants.Count == 0) return;
        var all = PlayerControl.AllPlayerControls;
        var crew = new List<PlayerControl>();
        foreach (var p in all)
        {
            if (!p || p.notRealPlayer || p.Data == null || p.Data.Disconnected || !p.Data.Role) continue;
            if (Wants.Contains(p.PlayerId)) Chosen.Add(p);
            else if (!RoleManager.IsImpostorRole(p.Data.Role.Role)) crew.Add(p);
        }
        // 観戦の人を除いても「クルーがインポスターより多い」ように残す (足りない分は観戦をやめてもらう)。
        // 観戦の人がインポスターなら入れ替えでクルーが 1 人減る
        int impostors = 0, players = 0;
        foreach (var p in all)
        {
            if (!p || p.notRealPlayer || p.Data == null || p.Data.Disconnected || !p.Data.Role) continue;
            players++;
            if (RoleManager.IsImpostorRole(p.Data.Role.Role)) impostors++;
        }
        while (Chosen.Count > 0 && players - Chosen.Count - impostors < impostors + 1) Chosen.RemoveAt(Chosen.Count - 1);
        var rng = new System.Random();
        for (int i = Chosen.Count - 1; i >= 0; i--)
        {
            var s = Chosen[i];
            if (!RoleManager.IsImpostorRole(s.Data.Role.Role))
            {
                if (s.Data.Role.Role != RoleTypes.Crewmate) s.RpcSetRole(RoleTypes.Crewmate, true);
                continue;
            }
            if (crew.Count == 0) { Chosen.RemoveAt(i); continue; }
            int k = rng.Next(crew.Count);
            var swap = crew[k];
            crew.RemoveAt(k);
            swap.RpcSetRole(RoleTypes.Impostor, true);
            s.RpcSetRole(RoleTypes.Crewmate, true);
        }
        if (Chosen.Count > 0) Plugin.Logger.LogInfo($"spectate: {Chosen.Count} spectator(s)");
    }

    // 始まりの場面が終わったら観戦の人を幽霊にする (ホストが全員へ送る)。見るのは FixedUpdate で、試合中に 1 回だけ
    public static void Tick()
    {
        if (_sendPending && Environment.TickCount64 >= _sendAt)
        {
            _sendPending = false;
            SendMine();
        }
        if (_deferred != null)
        {
            var h = Vanilla.Hud;
            if ((h && h.UseButton && h.UseButton.isActiveAndEnabled) || Environment.TickCount64 - _deferredAt > 20000)
            {
                var later = _deferred;
                _deferred = null;
                _forceLocal = true;
                try { ApplyLocal(later); }
                finally { _forceLocal = false; }
            }
        }
        if (_applied || Chosen.Count == 0) return;
        var client = AmongUsClient.Instance;
        if (!client || !client.AmHost || !ShipStatus.Instance || !PlayerControl.LocalPlayer) return;
        // 船は始まりの場面より先に出る。始まりの場面が始まり、その後に操作のボタンが戻ってから
        // (どちらも来なければ 30 秒後に) 幽霊にする。始まりの場面の頭で死因の記録が消されるため
        long now = Environment.TickCount64;
        if (_shipSince == 0) _shipSince = now;
        var hud = Vanilla.Hud;
        bool introDone = IntroStarted && hud && hud.UseButton && hud.UseButton.isActiveAndEnabled;
        if (!introDone && now - _shipSince < 30000) return;
        _applied = true;
        var ids = new List<byte>();
        foreach (var p in Chosen)
            if (p && p.Data != null && !p.Data.Disconnected) ids.Add(p.PlayerId);
        Chosen.Clear();
        if (ids.Count == 0) return;
        Apply.Send(ids);
        ApplyLocal(ids);
    }

    private static List<byte> _deferred;
    private static bool _forceLocal;
    private static ImportantTextTask _task; // 自分のタスク欄の「観戦中」
    private static long _deferredAt;

    // テスト用: 一人の試合でも自分を観戦の人にする
    internal static void WatchSelfForTest()
    {
        var me = PlayerControl.LocalPlayer;
        if (!me) return;
        _forceLocal = true;
        try { ApplyLocal(new List<byte> { me.PlayerId }); }
        finally { _forceLocal = false; }
    }

    private static void ApplyLocal(List<byte> ids)
    {
        // 自分の端末の始まりの場面がまだなら終わるまで待つ (始まりの場面の頭で死因の記録が消え、途中の死は見た目が崩れる)
        var hud = Vanilla.Hud;
        if (!_forceLocal && !(AmongUsClient.Instance && AmongUsClient.Instance.AmHost) && !(hud && hud.UseButton && hud.UseButton.isActiveAndEnabled))
        {
            _deferred = ids;
            _deferredAt = Environment.TickCount64;
            return;
        }
        _deferred = null;
        Watching.Clear();
        foreach (byte id in ids)
        {
            var info = GameData.Instance ? GameData.Instance.GetPlayerById(id) : null;
            var p = info != null ? info.Object : null;
            // 客では自分の始まりの場面を待つ間に、ホストで死んだこと (IsDead) が先に届いていることがある。
            // 死んでいても観戦の印・タスク欄は付ける (飛ばすと終わりの画面と本人のタスク欄が普通の幽霊のままになる)
            if (!p || info.Disconnected) continue;
            Watching.Add(id);
            Applying = id;
            try { p.Exiled(); }
            finally { Applying = 255; }
            MatchLog.OnSpectate(p); // 死因は「観戦」 (追放の後置が呼ばれない道があっても残す)
            info.Tasks?.Clear();
            if (p.AmOwner) ShowTask(p);
        }
        if (GameData.Instance) GameData.Instance.RecomputeTaskCounts();
        var me = PlayerControl.LocalPlayer;
        if (me && Watching.Contains(me.PlayerId) && Vanilla.Hud && Vanilla.Hud.Notifier)
            Vanilla.Hud.Notifier.AddDisconnectMessage(new Text("Spectate.Spectating"));
    }

    // タスク欄には「観戦中」だけを出す (空のままだと本編が None と出す)
    private static void ShowTask(PlayerControl p)
    {
        p.ClearTasks();
        if (_task) UnityEngine.Object.Destroy(_task.gameObject);
        var go = new UnityEngine.GameObject("MrpSpectateTask");
        go.transform.SetParent(p.transform, false);
        var t = _task = go.AddComponent<ImportantTextTask>();
        t.Text = new Text("Spectate.Hud");
        p.myTasks.Insert(0, t.Cast<PlayerTask>());
    }

    // ロビーに戻った: この試合の観戦の印を消す (観戦したいかどうかは残す)
    public static void OnLobby()
    {
        // 「観戦中」の行を外す (キャラは試合をまたいで残るので、子に作った物も残る)
        if (_task)
        {
            var me = PlayerControl.LocalPlayer;
            if (me && me.myTasks != null) me.myTasks.Remove(_task.Cast<PlayerTask>());
            UnityEngine.Object.Destroy(_task.gameObject);
        }
        _task = null;
        _deferred = null;
        Watching.Clear();
        Chosen.Clear();
        _applied = IntroStarted = false;
        _shipSince = 0;
        ResendInLobby();
    }

    internal static string Describe() =>
        $"OK spectate mine={Menu.ClientOptions.Spectate.Value} wants=[{string.Join(",", Wants)}] watching=[{string.Join(",", Watching)}] chosen={Chosen.Count} applied={_applied}";
}

[HarmonyPatch]
internal static class SpectatePatches
{
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Start)), HarmonyPostfix]
    public static void Spawned(PlayerControl __instance) => Spectate.OnSpawned(__instance);

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerLeft)), HarmonyPrefix]
    public static void Left([HarmonyArgument(0)] ClientData data)
    {
        try { Spectate.OnLeft(data?.Character); }
        catch (Exception e) { Plugin.Logger.LogError($"spectate: {e.Message}"); }
    }

    [HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start)), HarmonyPostfix]
    public static void Lobby() => Spectate.OnLobby();

    [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.CoBegin)), HarmonyPrefix]
    public static void IntroBegin() => Spectate.IntroStarted = true;

    // 幽霊の役職が付くと、本編はタスク欄の先頭の文字の行を「死亡した。タスクを完了して勝利しよう」に書き換える。
    // 観戦の人の幽霊の役職は観戦を当てた後に届くことがあり、「観戦中」の行が書き換えられるので、観戦中の人には書かせない
    [HarmonyPatch(typeof(CrewmateGhostRole), nameof(CrewmateGhostRole.SpawnTaskHeader)), HarmonyPrefix]
    public static bool GhostTaskHeader([HarmonyArgument(0)] PlayerControl playerControl) =>
        !(playerControl && Spectate.IsWatching(playerControl.PlayerId));

    // 観戦の人は本編の追放の処理で幽霊にするので、本編が死んだ時の幽霊役職 (守護天使など) を抽選してしまう。
    // 観戦の人には特別な幽霊役職を付けず、ただの幽霊にする
    [HarmonyPatch(typeof(RoleManager), nameof(RoleManager.AssignRoleOnDeath)), HarmonyPrefix]
    public static void AssignRoleOnDeath([HarmonyArgument(0)] PlayerControl player, [HarmonyArgument(1)] ref bool specialRolesAllowed)
    {
        if (player && Spectate.IsWatching(player.PlayerId)) specialRolesAllowed = false;
    }
}
