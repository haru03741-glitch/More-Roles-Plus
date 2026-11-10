using MoreRolesPlus.Fx;
using MoreRolesPlus.Roles;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.UI;

// PC でマウスで操作する能力 (右下にボタンを出さない物) の待ち時間と残りの回数を、自分の体の右下に小さく出す (自分の画面だけ)。
// 待ち時間が無く回数も無ければ何も出さない
internal static class HandCooldown
{
    private const float OffX = 0.42f, OffY = -0.5f; // 体の中心から字の真ん中まで
    private const float FontSize = 1.7f;

    private static TextMeshPro _text;
    private static int _key = int.MinValue; // 今出している中身 (同じなら字を書き直さない)
    private static bool _shown;

    // 毎フレーム (Plugin.Update)
    internal static void Tick()
    {
        if (System.OperatingSystem.IsAndroid()) return;
        int secs = 0, uses = -1;
        bool any = false;
        var all = ModButton.Buttons;
        for (int i = 0; i < all.Count; i++)
        {
            var b = all[i];
            if (!b.PcHidden || !b.Shown) continue;
            any = true;
            int s = (int)System.MathF.Ceiling(b.Left);
            if (s > secs) secs = s;
            if (b.UsesLeft >= 0) uses = uses < 0 ? b.UsesLeft : FxMath.Min(uses, b.UsesLeft);
        }
        var lp = PlayerControl.LocalPlayer;
        bool want = any && (secs > 0 || uses >= 0) && lp && !RewindPlayback.Active && !MeetingHud.Instance && lp.Visible;
        if (!want)
        {
            if (_shown && _text) { _text.gameObject.SetActive(false); }
            _shown = false;
            return;
        }
        if (!_text && !Build()) return;
        if (!_shown) { _text.gameObject.SetActive(true); _shown = true; }
        var c = lp.transform.position;
        _text.transform.position = FxMath.V3(c.x + OffX, c.y + OffY, c.z - 0.002f);
        int key = secs * 1000 + uses + 1;
        if (key == _key) return;
        _key = key;
        _text.text = secs > 0 && uses >= 0 ? $"{secs}  ×{uses}" : secs > 0 ? secs.ToString() : $"×{uses}";
    }

    private static bool Build()
    {
        var hud = Vanilla.Hud;
        var src = hud && hud.KillButton ? hud.KillButton.cooldownTimerText : null;
        if (!src) return false;
        var go = FxRoot.New("MrpHandCooldown"); // 演出の根の下 (船と一緒に消える)
        _text = go.AddComponent<TextMeshPro>();
        _text.font = src.font;
        _text.fontSharedMaterial = src.fontSharedMaterial;
        _text.enableAutoSizing = false;
        _text.fontSize = FontSize;
        _text.enableWordWrapping = false;
        _text.overflowMode = TextOverflowModes.Overflow;
        _text.alignment = TextAlignmentOptions.Center;
        _text.color = FxMath.Rgba(1f, 1f, 1f, 0.85f);
        _text.rectTransform.sizeDelta = FxMath.V2(2f, 0.4f);
        _key = int.MinValue;
        _shown = true;
        return true;
    }
}
