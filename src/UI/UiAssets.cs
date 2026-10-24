// Based on https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/VanillaAsset.cs (GPL-3.0)
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.UI;

// 自前の画面の部品に使う本編の絵と字の型。本編の Twitch の窓 (どの場面にも常に居る・非表示) の子から抜く。
// - Box = 白い縁の黒い箱 (本編の値の欄・窓の背景と同じ絵)。9 分割で伸ばし、色は SpriteRenderer.color で縁に付く。
// - Blank = 1x1 の白 (幕・当たり判定の下地・スクロールの棒)。
// - 字の型 = 窓の本文の TextMeshPro (本編の字の材質と、無い字の代わりのフォントの並びが付いている)。
// 一度抜いたら捨てない (場面を跨いで使う)。Twitch の窓はメインメニューでしか見つからないので、メインメニューで抜いておく。
[HarmonyPatch]
internal static class UiAssets
{
    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start)), HarmonyPostfix]
    public static void OnMainMenu() => Load();

    private static Sprite _box, _blank;
    private static TextMeshPro _textProto;
    private static readonly Dictionary<System.IntPtr, Material> Masked = new();

    public static Sprite Box => Load() ? _box : null;
    public static Sprite Blank => Load() ? _blank : null;
    public static TextMeshPro TextProto => Load() ? _textProto : null;

    public static bool Load()
    {
        if (_box && _blank && _textProto) return true;
        // Instance は無い時に読むと空の物を作るので、置かれている物を名前で探す
        var tm = GameObject.Find("TwitchManager");
        var popup = tm && tm.transform.childCount > 0 ? tm.transform.GetChild(0) : null;
        if (!popup) return false;
        // 子の並びは版で変わりうるので名前で探す
        var bg = popup.Find("Background");
        var blank = popup.Find("BackroundIgnore");
        var text = popup.Find("Text_TMP");
        if (!_box && bg) _box = bg.GetComponent<SpriteRenderer>()?.sprite;
        if (!_blank && blank) _blank = blank.GetComponent<SpriteRenderer>()?.sprite;
        if (!_textProto && text)
        {
            var src = text.GetComponent<TextMeshPro>();
            if (src)
            {
                _textProto = Object.Instantiate(src);
                _textProto.name = "MrpUiTextProto";
                _textProto.text = "";
                _textProto.gameObject.SetActive(false);
                _textProto.gameObject.hideFlags = HideFlags.HideAndDontSave;
                Object.DontDestroyOnLoad(_textProto.gameObject);
            }
        }
        if (_box) _box.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        if (_blank) _blank.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        return _box && _blank && _textProto;
    }

    // 確かめ用: 抜けた物
    public static string Diag()
    {
        var tm = GameObject.Find("TwitchManager");
        Load();
        return $"tm={(bool)tm} children={(tm ? tm.transform.childCount : -1)} box={(bool)_box} blank={(bool)_blank} text={(bool)_textProto} hud={(bool)Vanilla.Hud}";
    }

    // 確かめ用: 切り抜きの字の材質の型板の値を変える
    public static string DebugStencil(float id, float comp)
    {
        int n = 0;
        foreach (var m in Masked.Values)
        {
            if (!m) continue;
            m.SetFloat(ShaderUtilities.ID_StencilID, id);
            m.SetFloat(ShaderUtilities.ID_StencilComp, comp);
            n++;
        }
        var sb = new System.Text.StringBuilder($"n={n}");
        foreach (var m in Masked.Values)
            if (m) sb.Append($" [{m.name} shader={m.shader.name} id={m.GetFloat(ShaderUtilities.ID_StencilID)} comp={m.GetFloat(ShaderUtilities.ID_StencilComp)}]");
        return sb.ToString();
    }

    // スクロール欄 (SpriteMask) の内側だけに字を描く材質。元の材質ごとに 1 つを全員で使う
    public static Material MaskedFont(Material src)
    {
        if (!src) return src;
        if (Masked.TryGetValue(src.Pointer, out var m) && m) return m;
        m = new Material(src) { name = src.name + " (MrpMasked)" };
        // SpriteMask は型板 (stencil) に 1 を書く。その内側 = 1 以下と比べて通る所
        m.SetFloat(ShaderUtilities.ID_StencilID, 1f);
        m.SetFloat(ShaderUtilities.ID_StencilComp, 4f);
        m.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Masked[src.Pointer] = m;
        return m;
    }
}
