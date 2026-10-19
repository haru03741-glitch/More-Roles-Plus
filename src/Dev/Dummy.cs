// Ported from https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Utilities/AmongUsUtil.cs (GPL-3.0)
// and https://github.com/yukieiji/ExtremeRoles ExtremeRoles/Helper/GameSystem.cs SpawnDummyPlayer (GPL-3.0)
using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Dev;

// 本編のフリープレイと同じ作りのダミー (DummyBehaviour で立っているだけの人)。
// ホストが本編の Spawn で出すので、ローカルの部屋とカスタムサーバーの部屋では他の人にも見える。
// 公式サーバーでは出さない。
internal static class Dummy
{
    public static PlayerControl Spawn(int index)
    {
        var lp = PlayerControl.LocalPlayer;
        var pc = Object.Instantiate(AmongUsClient.Instance.PlayerPrefab);
        byte id = pc.PlayerId = (byte)GameData.Instance.GetAvailableId();
        pc.isDummy = true;
        var data = GameData.Instance.AddDummy(pc);

        pc.GetComponent<DummyBehaviour>().enabled = true;
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
        // 位置の同期を止めないと、届かない位置の更新を待って原点へ戻される。重ならないように自分の右へ並べる
        pc.NetTransform.enabled = false;
        pc.transform.position = lp.transform.position + new Vector3(0.9f * (index + 1), 0f, 0f);
        return pc;
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
