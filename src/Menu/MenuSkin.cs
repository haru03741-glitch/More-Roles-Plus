using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Menu;

// メニューの画面 (メインメニュー・オンライン・ゲームを探す・持ち物・ショップ・設定の窓 など) のボタンと背景を MRP の色にする。
// ロビーと試合の中は変えない。
//   - 本編のボタンの絵を、角の丸い紺の板 (指が乗ると水色に光る) に差し替える。斜めの光の筋は消す。大きさは元の絵のまま
//   - 船の壁の背景はホーム画面と同じ夜空にし、窓の枠は夜空に合う色にする
// 本編のボタンの押し方・位置には触らない (絵を替えるだけ)
internal static class MenuSkin
{
    private const float ScanEvery = 0.4f;
    private static float _scanAt;
    private static readonly HashSet<IntPtr> Done = new();
    private static string _scene;

    // 差し替える本編の絵の名前 → 種類
    private enum Kind { CtaOff, CtaOn, StdOff, StdOn, Outline, Panel }
    private static readonly Dictionary<string, Kind> Names = new()
    {
        ["CTAInactive"] = Kind.CtaOff,
        ["CTAActive"] = Kind.CtaOn,
        ["StandardInactive"] = Kind.StdOff,
        ["StandardActive"] = Kind.StdOn,
        ["MinorInactive"] = Kind.StdOff,
        ["MinorActive"] = Kind.StdOn,
        ["buttonInactive"] = Kind.StdOff,
        ["buttonActive"] = Kind.StdOn,
        // 設定の窓などの白い枠のボタン (本編が色を掛けて 緑 = ON などを出すので、枠は白・中は紺)
        ["buttonClick"] = Kind.Outline,
        // 黒い窓 (設定・コード入力などの小窓)
        ["buttonEnterText"] = Kind.Panel,
    };
    private static readonly Dictionary<Kind, Sprite> Sprites = new();
    private static readonly Color FrameTint = new(0.80f, 0.85f, 0.95f, 1f); // 本編の枠の色をほんの少し青寄りにするだけ

    internal static bool InMenu()
    {
        var s = SceneManager.GetActiveScene().name;
        return s is "MainMenu" or "FindAGame" or "MMOnline";
    }

    [HarmonyPatch(typeof(ModManager), nameof(ModManager.LateUpdate))]
    private static class FramePatch
    {
        public static void Postfix()
        {
            try
            {
                if (!InMenu()) return;
                var scene = SceneManager.GetActiveScene().name;
                if (scene != _scene) { _scene = scene; Done.Clear(); Texts.Clear(); TextSeen.Clear(); _scanAt = 0f; }
                MenuMotion.HoverOutside();
                FixTexts();
                if (Time.unscaledTime < _scanAt) return;
                _scanAt = Time.unscaledTime + ScanEvery;
                Scan();
                StarBacks();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"menu skin: {e.Message}");
                _scanAt = Time.unscaledTime + 5f;
            }
        }
    }

    private static void Scan()
    {
        foreach (var sr in Object.FindObjectsOfType<SpriteRenderer>())
        {
            if (!sr || !sr.sprite) continue;
            var ptr = sr.Pointer;
            if (Done.Contains(ptr)) continue;
            string name = sr.sprite.name;
            if (Names.TryGetValue(name, out var kind))
            {
                Done.Add(ptr);
                Skin(sr, kind);
            }
            else if (name.StartsWith("BUTTON-CTA") && name.EndsWith("Shine"))
            {
                // 本編の水色の斜めの光は、紺の板の上ではうっすら白い艶にする
                Done.Add(ptr);
                sr.color = new Color(1f, 1f, 1f, 0f); // 斜めの光の筋は消す (平らな板に筋だけ浮いて見える)
            }
            else if (name == "IMAGE-ShipBG")
            {
                Done.Add(ptr);
                Sky(sr);
            }
            else if (name is "UI_ShopHeader")
            {
                Done.Add(ptr);
                sr.color = new Color(0.80f, 0.85f, 0.95f, 1f);
            }
            else if (name is "IMAGE-ShipWindowFrame" or "IMAGE-LeftPanel")
            {
                Done.Add(ptr);
                sr.color = FrameTint;
            }
        }
    }

    // 持ち物・ショップの黒い背景 (星の流れる幕) の後ろに夜空を敷く
    private static void StarBacks()
    {
        foreach (var star in Object.FindObjectsOfType<StarGen>())
        {
            if (!star || Done.Contains(star.Pointer)) continue;
            Done.Add(star.Pointer);
            var t = star.transform;
            if (t.parent && t.parent.Find("MrpSky")) continue;
            var mr = star.GetComponent<MeshRenderer>();
            var sky = new GameObject("MrpSky") { layer = star.gameObject.layer }.AddComponent<SpriteRenderer>();
            sky.transform.SetParent(t.parent, false);
            sky.transform.localPosition = t.localPosition + new Vector3(0f, 0f, 1f);
            sky.transform.localScale = new Vector3(10f, 10f, 1f);
            sky.sprite = HomeScreen.Sky();
            if (mr) { sky.sortingLayerID = mr.sortingLayerID; sky.sortingOrder = mr.sortingOrder - 1; }
        }
    }

    // 他の画面の MRP のボタン (ランクマッチなど) も本編のボタンと同じ板にする
    internal static Sprite Button(bool hover) => Get(hover ? Kind.CtaOn : Kind.CtaOff);
    private static Sprite Get(Kind kind) => Sprites.TryGetValue(kind, out var sp) && sp ? sp : Sprites[kind] = Make(kind);

    // 大きいボタンの字: 本編は水色の板に合わせて濃い緑にしているので、紺の板の上では明るい色にする (本編が色を戻しても毎フレーム直す)
    private static readonly List<TMPro.TextMeshPro> Texts = new();
    private static readonly HashSet<IntPtr> TextSeen = new();
    private static readonly Color TextColor = new(0.93f, 0.97f, 1f, 1f);

    private static void KeepTexts(SpriteRenderer sr)
    {
        var pb = sr.GetComponentInParent<PassiveButton>();
        if (!pb) return;
        foreach (var t in pb.GetComponentsInChildren<TMPro.TextMeshPro>(true))
            if (t && TextSeen.Add(t.Pointer)) Texts.Add(t);
    }

    private static void FixTexts()
    {
        for (int i = Texts.Count - 1; i >= 0; i--)
        {
            var t = Texts[i];
            if (!t) { Texts.RemoveAt(i); continue; }
            var c = t.color;
            if (c.r * 0.3f + c.g * 0.59f + c.b * 0.11f < 0.6f) t.color = new Color(TextColor.r, TextColor.g, TextColor.b, c.a);
        }
    }

    private static void Skin(SpriteRenderer sr, Kind kind)
    {
        if (kind is Kind.CtaOff or Kind.CtaOn) KeepTexts(sr);
        var size = sr.size; // 元の絵の見た目の大きさ
        sr.sprite = Get(kind);
        sr.drawMode = SpriteDrawMode.Sliced;
        sr.size = new Vector2(size.x, size.y);
    }

    private static void Sky(SpriteRenderer bg)
    {
        if (bg.transform.Find("MrpSky")) return;
        var sky = new GameObject("MrpSky") { layer = bg.gameObject.layer }.AddComponent<SpriteRenderer>();
        sky.transform.SetParent(bg.transform, false);
        sky.transform.localPosition = new Vector3(0f, 0f, -0.01f);
        sky.transform.localScale = new Vector3(9f, 9f, 1f);
        sky.sprite = HomeScreen.Sky();
        sky.sortingLayerID = bg.sortingLayerID;
        sky.sortingOrder = bg.sortingOrder;
        bg.enabled = false;
    }

    // ── 絵 ──────────────────────────────────
    // 角の丸い板 (9 分割で伸ばす)。上は明るく下は暗く、縁に細い光の線、上の内側にうっすら艶
    private static Sprite Make(Kind kind)
    {
        const int w = 96, h = 48, r = 14;
        float ppu = 100f, lineW = 2.6f, fillA = 0.96f;
        Color top, bottom, edge;
        switch (kind)
        {
            // 光る縁や水色の派手な色は使わず、本編の板と同じく「上が少し明るい落ち着いた板 + 少しだけ明るい縁」にする
            // 紺の板 + 水色の差し色 (右のカードや上のバーと同じ水色)。指が乗ると少し明るくなり、縁が水色に光る
            case Kind.Outline: top = new(0.16f, 0.19f, 0.31f); bottom = new(0.11f, 0.13f, 0.23f); edge = new(0.90f, 0.93f, 0.98f); ppu = 200f; lineW = 3f; break;
            case Kind.Panel: top = new(0.12f, 0.14f, 0.24f); bottom = new(0.08f, 0.09f, 0.17f); edge = new(0.24f, 0.30f, 0.44f); lineW = 2f; fillA = 0.97f; break;
            case Kind.CtaOff: top = new(0.17f, 0.20f, 0.34f); bottom = new(0.11f, 0.13f, 0.24f); edge = new(0.24f, 0.36f, 0.48f); lineW = 1.6f; break;
            case Kind.CtaOn: top = new(0.22f, 0.28f, 0.44f); bottom = new(0.14f, 0.19f, 0.32f); edge = new(0.30f, 0.92f, 0.82f); lineW = 2.4f; break;
            case Kind.StdOff: top = new(0.15f, 0.18f, 0.30f); bottom = new(0.10f, 0.12f, 0.22f); edge = new(0.22f, 0.30f, 0.43f); lineW = 1.6f; break;
            default: top = new(0.20f, 0.30f, 0.42f); bottom = new(0.14f, 0.22f, 0.33f); edge = new(0.30f, 0.80f, 0.75f); lineW = 1.6f; break;
        }
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "MrpSkin" + kind, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float dx = Mathf.Max(Mathf.Max(r - x - 0.5f, x + 0.5f - (w - r)), 0f);
            float dy = Mathf.Max(Mathf.Max(r - y - 0.5f, y + 0.5f - (h - r)), 0f);
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float a = Mathf.Clamp01(r - d);
            if (a <= 0f) { px[y * w + x] = new Color32(0, 0, 0, 0); continue; }
            // 縁からの距離 (角は丸く)
            float inner = Mathf.Min(Mathf.Min(x + 0.5f, w - x - 0.5f), Mathf.Min(y + 0.5f, h - y - 0.5f));
            if (dx > 0f || dy > 0f) inner = r - d;
            float t = (y + 0.5f) / h; // 0 下・1 上
            var c = Color.Lerp(bottom, top, t);
            if (t > 0.55f) c = Color.Lerp(c, Color.white, 0.08f * (t - 0.55f) / 0.45f);
            float line = Mathf.Clamp01(lineW - inner);
            c = Color.Lerp(c, edge, line * 0.8f);
            px[y * w + x] = new Color(c.r, c.g, c.b, a * (line > 0.5f ? 1f : fillA));
        }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var sp = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), ppu, 0, SpriteMeshType.FullRect, new Vector4(r + 2, r + 2, r + 2, r + 2));
        sp.name = "MrpSkin" + kind;
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }
}
