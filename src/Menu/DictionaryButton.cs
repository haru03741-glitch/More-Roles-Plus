using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.UI;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// 右上の「?」(試合の情報) の左に、役職辞典を開くボタンを置く。
// 本編の設定のボタンを複製して「?」の子にする (「?」は毎フレーム自分の位置を決め直すので、子にすれば一緒に動き、
// 出る/隠れるも本編に従う)。絵は 3 状態とも「?」のボタンの台に替え、中の手帳を台の地の色で覆って本編の役職の説明の絵を重ねる。
internal static class DictionaryButton
{
    private const string Name = "MrpDictionaryButton";
    private const string IconSpriteName = "tooltip_crewmate";
    private const float Gap = 0.93f;       // 「?」との中心の間 (画面の単位・チャットと「?」の間と同じ)
    private static float _iconHeight = 0.76f; // 絵の高さ (台の中に収まる大きさ。台の絵は縦に約 1.39 倍へ伸ばして描かれる)
    private static float _iconY = 0.09f;

    // 台の地の色 (本編の絵を測った値)。手帳の範囲 (台の絵の画素 17〜88 × 11〜74) を 12〜90 × 8〜76 で覆う
    private static readonly Color InactiveBg = new(176 / 255f, 177 / 255f, 181 / 255f);
    private static readonly Color ActiveBg = new(195 / 255f, 255 / 255f, 244 / 255f);
    private static readonly Color SelectedBg = Color.white;
    private static readonly Vector2 CoverSize = new(0.78f, 0.68f);
    private const float CoverY = 0.04f;

    private static Sprite _icon;
    private static readonly List<int> Slots = new();

    internal static void Ensure()
    {
        var hud = Vanilla.Hud;
        if (!hud || !hud.MatchInfoButton) return;
        var parent = hud.MatchInfoButton.transform;
        if (parent.Find(Name)) return;
        var menu = parent.parent ? parent.parent.Find("MenuButton") : null;
        var icon = Icon();
        if (!menu || !icon) return;

        var go = UnityEngine.Object.Instantiate(menu.gameObject, parent);
        go.name = Name;
        var aspect = go.GetComponent<AspectPosition>();
        if (aspect) UnityEngine.Object.DestroyImmediate(aspect);
        // 「?」と同じ倍率の子になるので、自分の倍率は 1 に
        var ps = parent.localScale;
        go.transform.localScale = Vector3.one;
        go.transform.localPosition = new Vector3(-Gap / (ps.x == 0f ? 1f : ps.x), 0f, 0f);

        var src = hud.MatchInfoButton.transform;
        Restyle(go.transform, src, "Inactive", InactiveBg, icon);
        Restyle(go.transform, src, "Active", ActiveBg, icon);
        Restyle(go.transform, src, "Selected", SelectedBg, icon);

        var btn = go.GetComponent<PassiveButton>();
        if (btn)
        {
            UiActions.Release(Slots);
            btn.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            btn.OnClick.AddListener(UiActions.Take(Click, Slots));
        }
        go.SetActive(true);
    }

    private static void Click()
    {
        if (!RoleDictionary.IsOpen) RoleDictionary.Open();
    }

    // 状態の絵を「?」の同じ状態の台に替え、手帳を覆って絵を重ねる
    private static void Restyle(Transform root, Transform src, string state, Color bg, Sprite icon)
    {
        var t = root.Find(state);
        var from = src.Find(state);
        var sr = t ? t.GetComponent<SpriteRenderer>() : null;
        var fromSr = from ? from.GetComponent<SpriteRenderer>() : null;
        if (!sr || !fromSr) return;
        sr.sprite = fromSr.sprite;

        var blank = UiAssets.Blank;
        if (blank)
        {
            var cover = new GameObject("Cover").AddComponent<SpriteRenderer>();
            cover.gameObject.layer = t.gameObject.layer;
            cover.transform.SetParent(t, false);
            cover.sprite = blank;
            cover.color = bg;
            var b = blank.bounds.size;
            cover.transform.localPosition = new Vector3(0f, CoverY, -0.01f);
            cover.transform.localScale = new Vector3(CoverSize.x / b.x, CoverSize.y / b.y, 1f);
        }

        var pic = new GameObject("Icon").AddComponent<SpriteRenderer>();
        pic.gameObject.layer = t.gameObject.layer;
        pic.transform.SetParent(t, false);
        pic.sprite = icon;
        Place(pic);
    }

    private static void Place(SpriteRenderer pic)
    {
        var ib = pic.sprite.bounds;
        float k = _iconHeight / ib.size.y;
        // 絵の中心を台の中の中心へ (絵の支点は中心から外れている)
        pic.transform.localPosition = new Vector3(-ib.center.x * k, _iconY - ib.center.y * k, -0.02f);
        pic.transform.localScale = new Vector3(k, k, 1f);
    }

    private static Sprite Icon()
    {
        if (_icon) return _icon;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
        {
            var s = o.TryCast<Sprite>();
            if (!s || s.name != IconSpriteName) continue;
            s.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            _icon = s;
            break;
        }
        return _icon;
    }

    // 確かめ用: info / click (押したのと同じ) / ensure / look <高さ> <y> (絵を置き直す) / state <Inactive|Active|Selected> (その状態の絵だけを出す)
    internal static string Command(string[] a)
    {
        var hud = Vanilla.Hud;
        var t = hud && hud.MatchInfoButton ? hud.MatchInfoButton.transform.Find(Name) : null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (t && a.Length > 3 && a[1] == "look" && float.TryParse(a[2], System.Globalization.NumberStyles.Float, inv, out float h)
            && float.TryParse(a[3], System.Globalization.NumberStyles.Float, inv, out float y))
        {
            _iconHeight = h;
            _iconY = y;
            foreach (var sr in t.GetComponentsInChildren<SpriteRenderer>(true))
                if (sr.name == "Icon") Place(sr);
        }
        if (t && a.Length > 2 && a[1] == "state")
            foreach (var n in new[] { "Inactive", "Active", "Selected" })
                t.Find(n)?.gameObject.SetActive(n == a[2]);
        if (a.Length > 1 && a[1] == "click")
        {
            if (!t) return "no button";
            t.GetComponent<PassiveButton>().OnClick.Invoke();
            return $"clicked open={RoleDictionary.IsOpen}";
        }
        if (a.Length > 1 && a[1] == "ensure") Ensure();
        t = hud && hud.MatchInfoButton ? hud.MatchInfoButton.transform.Find(Name) : null;
        return t ? $"button active={t.gameObject.activeInHierarchy} pos={t.position} icon={(bool)_icon}" : $"no button icon={(bool)_icon}";
    }
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
internal static class DictionaryButtonPatch
{
    public static void Postfix()
    {
        try { DictionaryButton.Ensure(); }
        catch (Exception e) { Plugin.Logger.LogError($"dictionary button: {e}"); }
    }
}
