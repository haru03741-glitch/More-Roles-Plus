// Ported from https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Utilities/AmongUsUtil.cs (GPL-3.0)
// and https://github.com/yukieiji/ExtremeRoles ExtremeRoles/Helper/GameSystem.cs SpawnDummyPlayer (GPL-3.0)
using UnityEngine;

namespace MoreRolesPlus.Dev;

// 本編のフリープレイと同じ作りのダミー (DummyBehaviour で立っているだけの人)。
// ホストが本編の Spawn で出すので、ローカルの部屋なら他の人にも見える。公式やカスタムのサーバーでは出さない
// (中の人がいないプレイヤーをサーバーが受け付けない)。
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
