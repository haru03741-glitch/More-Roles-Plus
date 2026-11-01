using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Roles;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.UI;

// 特別枠 (二つ名) の人がいる試合で、役職の発表が終わった直後に全員の画面の上へ「この航路には 航路守 がいる」と出す。
// 誰かは出さない (全員の画面に同じ物が出るので、見ても誰かは分からない)。出すかはその役職の設定
internal static class EpithetAnnounce
{
    private const float In = 0.3f, Hold = 2.2f, Out = 0.5f, MarkH = 0.62f;

    private static GameObject _go;
    private static TextMeshPro _text;
    private static SpriteRenderer _mark, _glow, _line;
    private static float _t0;
    private static bool _live;

    internal static void OnGameStart(HudManager hud)
    {
        if (!hud) return;
        var role = Find();
        if (role == null || !role.AnnounceEpithet) return;
        Build(hud, role);
    }

    private static RoleBase Find()
    {
        foreach (var r in RoleState.All)
            if (r != null && r.Epithet != Epithet.None) return r;
        return null;
    }

    private static void Build(HudManager hud, RoleBase role)
    {
        var cam = Camera.main;
        if (!cam) return;
        if (_go) Object.Destroy(_go);
        int layer = hud.gameObject.layer;
        _go = new GameObject("EpithetAnnounce") { layer = layer };
        var tf = _go.transform;
        tf.SetParent(hud.transform, false);
        tf.localPosition = FxMath.V3(0f, cam.orthographicSize * 0.56f, -40f);

        // 横に長いオーロラ色の光の帯 → 印 → 字
        _glow = Quad("Glow", GlowSprite, 0f, 0f, 0.02f, 7.5f, 1.1f, layer);
        _line = Quad("Line", DuelHud.WhiteSprite(), 0f, -0.42f, 0.01f, 5.2f, 0.025f, layer);
        var sp = RoleArt.Mark(role.Epithet);
        if (sp)
        {
            float k = MarkH / sp.bounds.size.y;
            _mark = Quad("Mark", sp, 0f, 0f, 0f, 1f, 1f, layer);
            _mark.transform.localScale = FxMath.V3(k, k, 1f);
        }

        var src = hud.TaskPanel ? hud.TaskPanel.taskText : null;
        if (!src)
        {
            Object.Destroy(_go);
            _go = null;
            return;
        }
        var go = Object.Instantiate(src.gameObject, tf);
        go.name = "Text";
        go.layer = layer;
        for (int i = go.transform.childCount - 1; i >= 0; i--) Object.Destroy(go.transform.GetChild(i).gameObject);
        _text = go.GetComponent<TextMeshPro>();
        _text.enableWordWrapping = false;
        _text.enableAutoSizing = false;
        _text.alignment = TextAlignmentOptions.Center;
        _text.rectTransform.sizeDelta = FxMath.V2(8f, 1f);
        _text.fontSize = 2.6f;
        _text.fontStyle = FontStyles.Bold;
        _text.color = Color.white;
        string name = Tr.Get("Epithet." + role.Epithet);
        _text.text = new Text("Epithet.Announce").With("name", $"<color=#9fe8ff>{name}</color>");
        _text.ForceMeshUpdate();
        float w = _text.textBounds.size.x;
        // 字は印の右へ・印と字のまとまりを真ん中に
        float total = MarkH + 0.15f + w;
        if (_mark) _mark.transform.localPosition = FxMath.V3(-total * 0.5f + MarkH * 0.5f, 0f, -0.01f);
        go.transform.localPosition = FxMath.V3(-total * 0.5f + MarkH + 0.15f + w * 0.5f - _text.textBounds.center.x, -_text.textBounds.center.y, -0.02f);

        _t0 = Time.time;
        _live = true;
        Tick();
        Plugin.Logger.LogInfo($"[epithet] announce {role.Id}");
    }

    private static Sprite _glowSprite;
    private static Sprite GlowSprite => _glowSprite ? _glowSprite : _glowSprite = FxSprites.Rect(64, 32, FxSprites.Soft, "MrpAnnounceGlow");

    private static SpriteRenderer Quad(string name, Sprite sp, float x, float y, float z, float w, float h, int layer)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(_go.transform, false);
        go.transform.localPosition = FxMath.V3(x, y, z);
        float sw = sp ? sp.bounds.size.x : 1f, sh = sp ? sp.bounds.size.y : 1f;
        go.transform.localScale = FxMath.V3(w / sw, h / sh, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        sr.color = FxMath.Rgba(1f, 1f, 1f, 0f);
        return sr;
    }

    internal static void Tick()
    {
        if (!_live) return;
        if (!_go)
        {
            _live = false;
            return;
        }
        float t = Time.time - _t0;
        if (t >= In + Hold + Out)
        {
            Object.Destroy(_go);
            _go = null;
            _live = false;
            return;
        }
        float a = t < In ? t / In : t > In + Hold ? 1f - (t - In - Hold) / Out : 1f;
        a = FxMath.Clamp01(a);
        var c = Aura.Hue(t * 0.35f, 0.42f * a);
        _glow.color = FxMath.Rgba(c.r * 0.6f, c.g * 0.6f, c.b * 0.6f, c.a);
        var lc = Aura.Hue(t * 0.35f + 0.3f, a);
        _line.color = lc;
        if (t <= In * 1.5f + 0.1f)
        {
            float g = FxMath.Clamp01(t / (In * 1.5f));
            float grow = 1f - (1f - g) * (1f - g);
            _line.transform.localScale = FxMath.V3(FxMath.Max(5.2f * grow, 0.001f), 0.025f, 1f);
        }
        if (_mark) _mark.color = FxMath.Rgba(1f, 1f, 1f, a);
        if (_text) _text.alpha = a;
    }
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.OnGameStart))]
internal static class EpithetAnnouncePatch
{
    public static void Postfix(HudManager __instance)
    {
        try { EpithetAnnounce.OnGameStart(__instance); }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"epithet announce: {e.Message}"); }
    }
}
