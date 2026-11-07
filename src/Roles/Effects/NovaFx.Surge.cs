using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 満タンのまま押し続けている間、キャノンが船の電気を吸い上げて配線が乱れる:
//  照明 = 視界の届く距離が不規則にがくっと落ちてすぐ戻る (時々 2 度続けて)。間は少し暗いまま揺らぐ。
//         視界が広くて縁が画面の外にある人にも分かるよう、同じだけ画面 (世界の側・HUD は除く) も暗くなる
//  床   = 低い振動が続き、照明が落ちる瞬間に一度強く揺れる
// 撃つ人に近いほど強いが、同じ配線の上なので船のどこにいても少しは揺らぐ。満タンから時間が経つほど荒れる。
// 溜めは全員の端末に届いているので電文は足さない。死んでいる人の視界は変えない
internal static partial class NovaFx
{
    internal static float VisionMul { get; private set; } = 1f;
    internal static byte LocalId = 255;

    private const float SurgeNear = 4f, SurgeFar = 24f;   // この距離まで最も強く・ここから先は SurgeFloor のまま
    private const float SurgeFloor = 0.3f;
    private const float SurgeBuild = 3f;                  // 満タンから荒れきるまで
    private static float _dipEnd, _dipNext, _dipDepth, _nextRumble;
    private static bool _surging;
    private static GameObject _dimGo;
    private static SpriteRenderer _dim;
    private static int _dimKey = -1;

    private static void TickSurge(float now)
    {
        float best = 0f;
        bool haveMe = false, dead = false;
        float mx = 0f, my = 0f;
        foreach (var s in Shots)
        {
            if (s.Stance || s.FiredAt >= 0f || s.EndAt >= 0f || s.Dur <= 0f) continue;
            float over = now - s.C0 - s.Dur;
            if (over < 0f) continue;
            if (!haveMe)
            {
                var lp = PlayerControl.LocalPlayer;
                if (!lp || lp.Data == null) break;
                var me = lp.transform.position;
                mx = me.x; my = me.y;
                LocalId = lp.PlayerId;
                dead = lp.Data.IsDead;
                haveMe = true;
            }
            float dx = s.Px - mx, dy = s.Py - my, d = FxMath.Sqrt(dx * dx + dy * dy);
            float near = d <= SurgeNear ? 1f : d >= SurgeFar ? 0f : 1f - (d - SurgeNear) / (SurgeFar - SurgeNear);
            float k = (SurgeFloor + (1f - SurgeFloor) * near) * (0.45f + 0.55f * FxMath.Clamp01(over / SurgeBuild));
            if (k > best) best = k;
        }
        if (best <= 0f) { EndSurge(); return; }
        if (!_surging) { _surging = true; _dipNext = now + FxMath.Range(0.05f, 0.25f); }

        bool dipStart = false;
        if (now >= _dipNext)
        {
            dipStart = true;
            if (!dead) SndBrownout(best);
            _dipDepth = FxMath.Range(0.3f, 0.62f) * best;
            _dipEnd = now + FxMath.Range(0.05f, 0.18f);
            // 3 回に 1 回ほどは落ちた直後にもう一度ちらつく
            _dipNext = FxMath.Range(0f, 1f) < 0.35f
                ? _dipEnd + FxMath.Range(0.04f, 0.1f)
                : _dipEnd + FxMath.Range(0.25f, 1.2f) * (1.35f - 0.7f * best);
        }
        float sag = best * (0.06f + 0.05f * FxMath.Sin(now * 2.7f) + 0.03f * FxMath.Sin(now * 7.3f + 1.1f));
        VisionMul = dead ? 1f : now < _dipEnd ? 1f - _dipDepth : 1f - sag;
        Dim(dead ? 0f : (1f - VisionMul) * 0.75f);

        if (dipStart || now >= _nextRumble)
        {
            _nextRumble = now + 0.1f;
            var hud = Vanilla.Hud;
            if (hud && hud.PlayerCam)
                hud.PlayerCam.ShakeScreen(dipStart ? 0.05f + 0.07f * best : 0.012f + 0.03f * best, dipStart ? 0.2f : 0.14f);
        }
    }

    private static void EndSurge()
    {
        if (!_surging) return;
        _surging = false;
        VisionMul = 1f;
        Dim(0f);
    }

    // 画面の暗さ (0〜1)。ゲームのカメラの子の黒い板 (HUD は別のカメラなので覆わない)。濃さが 1/64 変わった時だけ書く
    private static void Dim(float a)
    {
        int key = (int)(a * 64f);
        if (key == _dimKey && (key == 0 || _dimGo)) return;
        _dimKey = key;
        if (key == 0) { if (_dimGo) _dimGo.SetActive(false); return; }
        if (!_dimGo)
        {
            var cam = Camera.main;
            if (!cam) return;
            _dimGo = FxRoot.NewOnCamera("MrpNovaSurgeDim", cam);
            var tf = _dimGo.transform;
            tf.localPosition = FxMath.V3(0f, 0f, cam.nearClipPlane + 0.45f);
            float h = cam.orthographicSize * 2.1f;
            tf.localScale = FxMath.V3(h * cam.aspect * 3f, h * 3f, 1f);
            _dim = _dimGo.AddComponent<SpriteRenderer>();
            _dim.sprite = UI.DuelHud.WhiteSprite();
        }
        _dim.color = FxMath.Rgba(0.004f, 0.005f, 0.01f, key / 64f);
        if (!_dimGo.activeSelf) _dimGo.SetActive(true);
    }
}
