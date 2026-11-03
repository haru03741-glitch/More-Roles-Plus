using UnityEngine;

namespace MoreRolesPlus.UI;

// スマホ版の移動パッド (画面の左下) と重ならないように、左下に置いた HUD の物をパッドの上端より上へずらす。
// パッドの大きさは本編の設定で変えられるので、決め打ちの高さでなく実際の絵の範囲を測る。PC では何もしない
internal sealed class StickClear
{
    private const float Gap = 0.15f; // パッドの上端との隙間 (HUD の中の長さ)
    private const int Every = 30;    // 測り直す間隔 (フレーム)

    private readonly AspectPosition _aspect;
    private readonly Vector3 _base;
    private readonly float _bottom; // 根から一番下の絵の下端までの長さ (HUD の中の長さ)
    private float _lift;
    private int _tick;
#if ANDROID
    private System.IntPtr _stick;
    private SpriteRenderer[] _parts;
#endif

    // lowest = 置いた物の中で一番下に来る絵 (作った直後・AdjustPosition の後に渡す)
    internal StickClear(AspectPosition aspect, SpriteRenderer lowest)
    {
        _aspect = aspect;
        _base = aspect.DistanceFromEdge;
        float k = Scale(aspect.transform);
        _bottom = lowest ? (aspect.transform.position.y - lowest.bounds.min.y) / k : 0f;
        _tick = Every - 1; // 最初の呼び出しで測る
    }

    internal void Update()
    {
        if (++_tick < Every) return;
        _tick = 0;
#if ANDROID
        var hud = Vanilla.Hud;
        if (!hud) return;
        float lift = Lift(hud);
        if (Mathf.Abs(lift - _lift) < 0.02f) return;
        _lift = lift;
        _aspect.DistanceFromEdge = new Vector3(_base.x, _base.y + lift, _base.z);
        _aspect.AdjustPosition();
#endif
    }

#if ANDROID
    // 置いた物をどれだけ上げればパッドに掛からないか (掛からなければ 0)。パッドが無い・隠れている時も 0
    private float Lift(HudManager hud)
    {
        var stick = hud.joystick?.TryCast<VirtualJoystick>();
        if (!stick) return 0f;
        if (_parts == null || _stick != stick.Pointer)
        {
            _stick = stick.Pointer;
            _parts = stick.GetComponentsInChildren<SpriteRenderer>(true);
        }
        float top = float.NegativeInfinity;
        foreach (var sr in _parts)
        {
            if (!sr || !sr.enabled || !sr.sprite || !sr.gameObject.activeInHierarchy) continue;
            float y = sr.bounds.max.y;
            if (y > top) top = y;
        }
        if (float.IsNegativeInfinity(top)) return 0f;
        float k = Scale(_aspect.transform);
        // 上げていない時の下端 (今の位置から今の上げ分を引く)
        float low = _aspect.transform.position.y - (_lift + _bottom) * k;
        float need = (top - low) / k + Gap;
        return need > 0f ? need : 0f;
    }
#endif

    private static float Scale(Transform t) => Mathf.Max(Mathf.Abs(t.lossyScale.y), 0.0001f);
}
