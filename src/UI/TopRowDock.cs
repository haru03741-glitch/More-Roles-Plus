using UnityEngine;

namespace MoreRolesPlus.UI;

// 右上のボタンの並び (設定・チャット・「?」・地図・友達など) の左隣に置く。並びの左端はロビーと試合・チャットの有無で
// 変わるので、決め打ちの位置でなく出ているボタンの絵の範囲を測る。測れない間は AspectPosition の初めの位置のまま
internal sealed class TopRowDock
{
    private const float Gap = 0.2f; // 並びの左端との隙間 (HUD の中の長さ)
    private const int Every = 30;   // 測り直す間隔 (フレーム)

    private readonly AspectPosition _aspect;
    private readonly float _half; // 置いた物の右端から根までの長さ (HUD の中の長さ)
    private float _x = float.NaN, _y = float.NaN;
    private int _tick;

    internal TopRowDock(AspectPosition aspect, float halfWidth)
    {
        _aspect = aspect;
        _half = halfWidth;
        _tick = Every - 1; // 最初の呼び出しで測る
    }

    internal void Update()
    {
        if (++_tick < Every) return;
        _tick = 0;
        var hud = Vanilla.Hud;
        if (!hud) return;
        float left = float.PositiveInfinity, top = float.NegativeInfinity, bottom = float.PositiveInfinity;
        Measure(hud.SettingsButton ? hud.SettingsButton.transform.parent : null, hud, ref left, ref top, ref bottom);
        Measure(hud.Chat ? hud.Chat.chatButton ? hud.Chat.chatButton.transform : null : null, hud, ref left, ref top, ref bottom);
        Measure(hud.MatchInfoButton ? hud.MatchInfoButton.transform : null, hud, ref left, ref top, ref bottom);
        Measure(hud.transform.Find("Friends List Button"), hud, ref left, ref top, ref bottom);
        if (float.IsPositiveInfinity(left)) return;

        var t = _aspect.transform;
        float k = Mathf.Max(Mathf.Abs(t.lossyScale.y), 0.0001f);
        // 根の置き場 (世界の座標): 右端が並びの左端から Gap だけ左・高さは並びの真ん中
        float x = left - (Gap + _half) * k, y = (top + bottom) * 0.5f;
        if (Mathf.Abs(x - _x) < 0.01f && Mathf.Abs(y - _y) < 0.01f) return;
        _x = x;
        _y = y;
        // AspectPosition は画面の右上からの長さで持つので、今の位置との差を長さに直して足す (出し直しても同じ所に戻る)
        var p = t.position;
        var d = _aspect.DistanceFromEdge;
        _aspect.DistanceFromEdge = new Vector3(d.x + (p.x - x) / k, d.y + (p.y - y) / k, d.z);
        _aspect.AdjustPosition();
    }

    private static void Measure(Transform root, HudManager hud, ref float left, ref float top, ref float bottom)
    {
        if (!root || root == hud.transform || !root.gameObject.activeInHierarchy) return;
        foreach (var sr in root.GetComponentsInChildren<SpriteRenderer>())
        {
            if (!sr.enabled || !sr.sprite) continue;
            var b = sr.bounds;
            var min = b.min;
            var max = b.max;
            if (min.x < left) left = min.x;
            if (max.y > top) top = max.y;
            if (min.y < bottom) bottom = min.y;
        }
    }
}
