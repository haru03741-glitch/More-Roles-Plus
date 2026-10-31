using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.UI;

// 体力のバーの端と VS の名札に出す、その人の着せ替えを着たクルーの姿。
// 本編のイントロの立ち絵 (PoolablePlayer) を複製して使う (帽子・バイザー・スキンを本編と同じに着せられる)。
// 作るのは HUD を組み立てる時の 1 回で、立ち合いが始まるたびに着せ直すだけ。立ち絵が無い時は色の丸のまま
internal static partial class DuelHud
{
    private const float CrewBar = 0.42f, CrewPlate = 0.62f;   // 立ち絵の倍率 (バーの端・名札)

    // 0 = 左のバー・1 = 右のバー・2 = 左の名札・3 = 右の名札
    private static readonly PoolablePlayer[] _crew = new PoolablePlayer[4];
    private static readonly SpriteRenderer[] _crewFallback = new SpriteRenderer[4];

    private static void BuildCrew(int layer)
    {
        Crew(0, _leftFace, CrewBar, false, layer);
        Crew(1, _rightFace, CrewBar, true, layer);
        Crew(2, _plateFaceL, CrewPlate, false, layer);
        Crew(3, _plateFaceR, CrewPlate, true, layer);
    }

    // 色の丸と同じ親・同じ位置に立ち絵を置く。右側は中央を向くよう左右を返す
    private static void Crew(int i, SpriteRenderer face, float scale, bool flip, int layer)
    {
        _crewFallback[i] = face;
        var hud = Vanilla.Hud;
        var proto = hud && hud.IntroPrefab ? hud.IntroPrefab.PlayerPrefab : null;
        if (!proto || !face) return;
        var tf = face.transform;
        var pp = Object.Instantiate(proto, tf.parent);
        pp.name = "Crew";
        foreach (var t in pp.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        var at = tf.localPosition;
        // 立ち絵の原点は足元の少し上なので、丸の中心に体の中心が来るよう下げる
        pp.transform.localPosition = FxMath.V3(at.x, at.y - scale * 0.32f, at.z - 0.05f);
        pp.transform.localScale = FxMath.V3(scale, scale, 1f);
        // 向きと名札は着せた後に決める (着せる前の立ち絵は着せ替えの層が空で、向きを変えると本編の中で落ちる)
        pp.gameObject.SetActive(false);
        _crew[i] = pp;
    }

    // 立ち合いの二人を着せる (左 = me・右 = opp)
    private static void DressCrew(int left, int right, byte me, byte opp)
    {
        Dress(left, me, false);
        Dress(right, opp, true);
    }

    private static void Dress(int i, byte id, bool flip)
    {
        var pp = _crew[i];
        var p = GameData.Instance ? GameData.Instance.GetPlayerById(id) : null;
        if (!pp || p == null)
        {
            if (pp) pp.gameObject.SetActive(false);
            if (_crewFallback[i]) _crewFallback[i].gameObject.SetActive(true);
            return;
        }
        pp.gameObject.SetActive(true);
        try
        {
            pp.UpdateFromPlayerOutfit(p.DefaultOutfit, PlayerMaterial.MaskType.None, false, false);
            pp.ToggleName(false);
            pp.SetFlipX(flip);
        }
        catch (System.Exception e)
        {
            // 着せられない着せ替えの時は色の丸に戻す (立ち合いの HUD は止めない)
            Plugin.Logger.LogWarning($"duel hud crew {id}: {e.Message}");
            pp.gameObject.SetActive(false);
            if (_crewFallback[i]) _crewFallback[i].gameObject.SetActive(true);
            return;
        }
        if (_crewFallback[i]) _crewFallback[i].gameObject.SetActive(false);
    }
}
