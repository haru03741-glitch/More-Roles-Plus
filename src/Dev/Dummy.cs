// Ported from https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Utilities/AmongUsUtil.cs (GPL-3.0)
// and https://github.com/yukieiji/ExtremeRoles ExtremeRoles/Helper/GameSystem.cs SpawnDummyPlayer (GPL-3.0)
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Dev;

// 本編のフリープレイと同じ作りのダミー (中の人のいないプレイヤー)。
// ホストが本編の Spawn で出すので、ローカルの部屋とカスタムサーバーの部屋では他の人にも見える。
// 公式サーバーでは出さない。
internal static class Dummy
{
    public static PlayerControl Spawn(int index)
    {
        var lp = PlayerControl.LocalPlayer;
        var pc = UnityEngine.Object.Instantiate(AmongUsClient.Instance.PlayerPrefab);
        byte id = pc.PlayerId = (byte)GameData.Instance.GetAvailableId();
        pc.isDummy = true;
        var data = GameData.Instance.AddDummy(pc);

        // 見た目 (色だけ・帽子などは無し)
        int color = id % Palette.PlayerColors.Length;
        pc.SetName($"Dummy {id}");
        pc.SetColor(color);
        pc.SetHat(CosmeticsLayer.EMPTY_HAT_ID, color);
        pc.SetVisor(CosmeticsLayer.EMPTY_VISOR_ID, color);
        pc.SetSkin(CosmeticsLayer.EMPTY_SKIN_ID, color);
        pc.SetPet(CosmeticsLayer.EMPTY_PET_ID, color);
        // 名札とレベルが未設定のままだと不完全なプレイヤーとして隠される (本編のダミーは空文字と 0)
        data.DefaultOutfit.NamePlateId = "";
        data.PlayerLevel = 0;

        AmongUsClient.Instance.Spawn(data, -2, InnerNet.SpawnFlags.None);
        AmongUsClient.Instance.Spawn(pc, -2, InnerNet.SpawnFlags.None);
        data.RpcSetTasks(new byte[0]);
        // 本編の DummyBehaviour (フリープレイ用) は位置の同期を毎フレーム止めるので使わない
        pc.GetComponent<DummyBehaviour>().enabled = false;
        // 位置の送り手をホストにする。サーバーと客には持ち主なし (-2) のまま見えているので、ホストの手元だけ書き換える
        // (送り手がいないと、届かない位置の更新を待って原点へ戻される)。本編が -2 へ戻すことがあるので Tick で見張る
        pc.NetTransform.OwnerId = AmongUsClient.Instance.ClientId;
        Owned.Add(pc);
        // 重ならないように自分の右へ並べる
        pc.NetTransform.SnapTo(new Vector2(lp.transform.position.x + 0.9f * (index + 1), lp.transform.position.y));
        return pc;
    }

    // ホストが出したダミー
    private static readonly List<PlayerControl> Owned = new();
    // 歩かせている途中のダミー
    private static readonly Dictionary<byte, (PlayerControl pc, float x, float y)> Walking = new();

    public static void WalkTo(PlayerControl pc, float x, float y) => Walking[pc.PlayerId] = (pc, x, y);

    // ---- 本物のプレイヤーと同じ行動 (ホストの端末で本編/MRP の同じ判定を通す) ----

    // 断った理由を返す (本編のキルは判定の結果が返らないので、送ったら null)
    public static string Kill(PlayerControl dummy, PlayerControl target)
    {
        if (!dummy || dummy.Data == null || dummy.Data.IsDead) return "dead";
        if (Roles.KillAbility.UsesFor(dummy)) return Roles.KillAbility.TryKill(dummy, target);
        if (!dummy.Data.Role || !dummy.Data.Role.IsImpostor) return "role";
        dummy.CmdCheckMurder(target);
        return null;
    }

    // 死体の通報 (body = null で緊急会議)
    public static void Report(PlayerControl dummy, NetworkedPlayerInfo body) => dummy.CmdReportDeadBody(body);

    // 投票 (253 = スキップ)
    public static void Vote(PlayerControl dummy, byte target)
    {
        var hud = MeetingHud.Instance;
        if (hud) hud.CmdCastVote(dummy.PlayerId, target);
    }

    public static void Tick()
    {
        if (Owned.Count == 0) return;
        var client = AmongUsClient.Instance;
        if (!client || !client.AmHost) { Owned.Clear(); Walking.Clear(); return; }
        int me = client.ClientId;
        for (int i = Owned.Count - 1; i >= 0; i--)
        {
            var pc = Owned[i];
            if (!pc) { Owned.RemoveAt(i); continue; }
            var nt = pc.NetTransform;
            if (nt.OwnerId != me) nt.OwnerId = me;
            if (!nt.enabled) nt.enabled = true;
        }
        if (Walking.Count == 0) return;
        List<byte> done = null;
        foreach (var (id, w) in Walking)
        {
            var pc = w.pc;
            if (!pc || pc.Data == null || pc.Data.IsDead || !pc.MyPhysics) { (done ??= new()).Add(id); continue; }
            var body = pc.MyPhysics.body;
            var pos = pc.GetTruePosition();
            float dx = w.x - pos.x, dy = w.y - pos.y, dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist < 0.1f)
            {
                body.velocity = Vector2.zero;
                (done ??= new()).Add(id);
                continue;
            }
            float speed = pc.MyPhysics.TrueSpeed / dist;
            body.velocity = new Vector2(dx * speed, dy * speed);
        }
        if (done != null) foreach (var id in done) Walking.Remove(id);
    }
}

[HarmonyPatch]
internal static class DummyPatches
{
    // 客の側: 本編は中の人のいない (ClientId が負の) プレイヤーの情報を受け取っても一覧へ入れない。
    // 体は名前や色を最初に一覧から読むので、体が動き出す前に足しておく。
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Start)), HarmonyPrefix]
    public static void RegisterRemoteDummy(PlayerControl __instance)
    {
        var client = AmongUsClient.Instance;
        var gd = GameData.Instance;
        if (!client || client.AmHost || !gd || !__instance || gd.GetPlayerById(__instance.PlayerId) != null) return;
        foreach (var o in client.allObjects.allObjects)
        {
            var info = o ? o.TryCast<NetworkedPlayerInfo>() : null;
            if (!info || info.PlayerId != __instance.PlayerId || info.ClientId >= 0) continue;
            gd.AllPlayers.Add(info);
            // 本編はダミーの印が無いと名札を「???」に伏せる (ホストの端末では Spawn の前に付けている)
            __instance.isDummy = true;
            return;
        }
    }

    // 本編は名札をこの情報から書き直さないので、ここで 1 回書く
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Start)), HarmonyPostfix]
    public static void NameRemoteDummy(PlayerControl __instance)
    {
        if (!AmongUsClient.Instance || AmongUsClient.Instance.AmHost || !__instance) return;
        var info = __instance.Data;
        if (info != null && info.ClientId < 0) __instance.SetName(info.PlayerName);
    }
}
