using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 弾が当たっている本人の画面 (倒れる前): 撃たれている向きの画面の縁が当たるたびに赤黒く染まって引き、目線が小さく揺れる。
// 当たり続けるほど縁は濃く、止むとすぐ引く。倒れた後は VictimView が代わる
internal static class HitFeel
{
    private const float PerHit = 0.32f, Base = 0.05f, Cap = 0.7f; // 1 発で縁に足す濃さ (撃たれた向きの側)・どの縁にも足す濃さ・上限
    private const float Fade = 1.8f;                               // 1 秒に引く濃さ
    private const float ShakeGap = 0.12f, ShakeTime = 0.08f, ShakeAmp = 0.1f;
    private const float EdgeW = 1.3f;                              // 縁の帯の幅 (HUD のカメラの単位・縦の半分が 3)

    private static GameObject _root;
    private static readonly SpriteRenderer[] Sides = new SpriteRenderer[4]; // 右・上・左・下
    private static readonly float[] Amt = new float[4];
    private static Sprite _band;
    private static float _nextShake;
    private static int _gen = -1;
    private static bool _on;

    // 自分に弾が当たった。sx, sy = 撃った人のいる向き
    internal static void Take(float sx, float sy)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.Data == null || lp.Data.IsDead || MeetingHud.Instance || VictimView.Active) return;
        if (!Ensure()) return;
        Amt[0] = FxMath.Min(Cap, Amt[0] + Base + PerHit * FxMath.Max(0f, sx));
        Amt[1] = FxMath.Min(Cap, Amt[1] + Base + PerHit * FxMath.Max(0f, sy));
        Amt[2] = FxMath.Min(Cap, Amt[2] + Base + PerHit * FxMath.Max(0f, -sx));
        Amt[3] = FxMath.Min(Cap, Amt[3] + Base + PerHit * FxMath.Max(0f, -sy));
        _on = true;
        float now = Time.time;
        var hud = Vanilla.Hud;
        if (now >= _nextShake && hud && hud.PlayerCam)
        {
            _nextShake = now + ShakeGap;
            hud.PlayerCam.ShakeScreen(ShakeTime, ShakeAmp);
        }
    }

    internal static void Tick()
    {
        if (!_on) return;
        if (_gen != GameClock.ShipGen || !_root || MeetingHud.Instance || VictimView.Active) { Clear(); return; }
        float d = Fade * Time.deltaTime;
        bool any = false;
        for (int i = 0; i < 4; i++)
        {
            if (Amt[i] <= 0f) continue;
            Amt[i] = FxMath.Max(0f, Amt[i] - d);
            Sides[i].color = FxMath.Rgba(0.4f, 0.02f, 0.02f, Amt[i]);
            any = true;
        }
        if (!any) Clear();
    }

    private static bool Ensure()
    {
        if (_root && _gen == GameClock.ShipGen) return true;
        var hud = Vanilla.Hud;
        if (!hud) return false;
        _gen = GameClock.ShipGen;
        // 縁から内へ薄れる帯 (横 32 × 縦 4・右の端が濃い)
        if (!_band) _band = FxSprites.Rect(32, 4, (x, _) => { float u = x + 0.5f; return u * u * u; }, "MrpHitFeelBand");
        _root = new GameObject("MrpHitFeel") { layer = 5 };
        _root.transform.SetParent(hud.transform, false);
        FxRoot.TrackHud(_root.transform); // 逆再生の間は見えていたとおりに逆へ流す
        _root.transform.localPosition = FxMath.V3(0f, 0f, -59f);
        float aspect = Screen.height > 0 ? Screen.width / (float)Screen.height : 16f / 9f;
        float hw = 3f * aspect, hh = 3f;
        var b = _band.bounds.size;
        for (int i = 0; i < 4; i++)
        {
            var go = new GameObject("Side" + i) { layer = 5 };
            go.transform.SetParent(_root.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _band;
            sr.sortingOrder = 505;
            sr.color = FxMath.Rgba(0.4f, 0.02f, 0.02f, 0f);
            bool vertical = (i & 1) == 0; // 右と左の帯は縦に長い
            float len = vertical ? hh * 2f + 0.2f : hw * 2f + 0.2f;
            float off = (vertical ? hw : hh) - EdgeW * 0.5f;
            go.transform.localPosition = i switch
            {
                0 => FxMath.V3(off, 0f, 0f),
                1 => FxMath.V3(0f, off, 0f),
                2 => FxMath.V3(-off, 0f, 0f),
                _ => FxMath.V3(0f, -off, 0f),
            };
            go.transform.localRotation = FxMath.RotZ(90f * i);
            go.transform.localScale = FxMath.V3(EdgeW / b.x, len / b.y, 1f);
            Sides[i] = sr;
            Amt[i] = 0f;
        }
        return true;
    }

    private static void Clear()
    {
        _on = false;
        for (int i = 0; i < 4; i++) Amt[i] = 0f;
        if (_root) Object.Destroy(_root);
        _root = null;
    }
}
