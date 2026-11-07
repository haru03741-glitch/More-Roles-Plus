using UnityEngine;

namespace MoreRolesPlus.UI;

// 押した所でいちばん手前にある本編のボタンを調べる。
// PC はマウスを重ねた時に本編が currentOver を立てるのでそれを見る。スマホのタッチでは本編が currentOver を立てない
// (押している間も空のまま) ので、押した点に重なるボタンを登録の一覧から探す。押した瞬間に 1 回だけ呼ぶ。
internal static class PointerHit
{
    // 押した所のいちばん手前のボタンが target か
    internal static bool IsTop(PassiveUiElement target)
    {
        if (!target || !PassiveButtonManager.InstanceExists) return false;
        var pm = PassiveButtonManager.Instance;
        var over = pm.currentOver;
        if (over) return over.Pointer == target.Pointer;
        if (Input.touchCount == 0) return false;
        var cam = Camera.main;
        if (!cam) return false;
        Vector3 w = cam.ScreenToWorldPoint(Input.mousePosition);
        var top = TopAt(pm, new Vector2(w.x, w.y));
        return top && top.Pointer == target.Pointer;
    }

    // pt に重なる登録済みのボタンのうち、いちばん手前 (z が小さい) の物
    internal static PassiveUiElement TopAt(PassiveButtonManager pm, Vector2 pt)
    {
        var list = pm.Buttons;
        if (list == null) return null;
        PassiveUiElement top = null;
        float topZ = float.MaxValue;
        for (int i = 0, n = list.Count; i < n; i++)
        {
            var b = list[i];
            if (!b || !b.isActiveAndEnabled || !Hit(b, pt)) continue;
            float z = b.transform.position.z;
            if (z < topZ) { topZ = z; top = b; }
        }
        return top;
    }

    private static bool Hit(PassiveUiElement b, Vector2 pt)
    {
        // スクロールの窓などで見えている範囲の外は押せない
        var mask = b.ClickMask;
        if (mask && !mask.OverlapPoint(pt)) return false;
        var cols = b.Colliders;
        if (cols == null) return false;
        for (int i = 0; i < cols.Length; i++)
        {
            var c = cols[i];
            if (c && c.isActiveAndEnabled && c.OverlapPoint(pt)) return true;
        }
        return false;
    }
}
