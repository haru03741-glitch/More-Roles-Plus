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
    private static bool _loaded;
    private static readonly Dictionary<System.IntPtr, Material> Masked = new();

    public static Sprite Box => Load() ? _box : null;
    public static Sprite Blank => Load() ? _blank : null;
    public static TextMeshPro TextProto => Load() ? _textProto : null;

    // 縁の滑らかな白い丸 (直径 1 単位)。変えた所の点とスライダーのつまみ。本編に合う丸の絵が無いので式から焼く
    private static Sprite _circle;
    public static Sprite Circle => _circle ? _circle : _circle = Fx.FxSprites.Rect(64, 64, (x, y) => (0.5f - Fx.FxMath.Sqrt(x * x + y * y)) * 64f, "MrpUiCircle");

    // 中が透明な箱 (Box と同じ二重の縁・中だけ抜く)。後ろに敷いた絵を見せたい欄で使う。
    // 本編の箱の絵 (100px 四方・9 分割の縁 25px・ppu 100) を測った寸法: 外の白 3.5px → 黒 3px → 内の白 2px・角の半径 12px
    private static Sprite _hollow;
    public static Sprite Hollow => _hollow ? _hollow : _hollow = BakeHollow();

    private static unsafe Sprite BakeHollow()
    {
        const int n = 100;
        const float r = 12f, half = n / 2f;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            // 角の丸い四角の縁からの内向きの距離 (px)
            float qx = Fx.FxMath.Abs(x + 0.5f - half) - (half - r), qy = Fx.FxMath.Abs(y + 0.5f - half) - (half - r);
            float ox = Fx.FxMath.Max(qx, 0f), oy = Fx.FxMath.Max(qy, 0f);
            float d = r - Fx.FxMath.Sqrt(ox * ox + oy * oy) - Fx.FxMath.Min(Fx.FxMath.Max(qx, qy), 0f);
            float white = Band(d, 0f, 3.5f) + Band(d, 6.5f, 8.5f);
            float alpha = Fx.FxMath.Clamp01(d + 0.5f) * (1f - Fx.FxMath.Clamp01(d - 8.5f + 0.5f));
            int i = (y * n + x) * 4;
            byte c = (byte)(Fx.FxMath.Clamp01(white) * 255f + 0.5f);
            px[i] = px[i + 1] = px[i + 2] = c;
            px[i + 3] = (byte)(alpha * 255f + 0.5f);
        }
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "MrpUiHollow", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        fixed (byte* p = px) tex.LoadRawTextureData((System.IntPtr)p, px.Length);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(25f, 25f, 25f, 25f));
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    // d が [a, b) の内なら 1 (縁は半画素ぼかす)
    private static float Band(float d, float a, float b) => Fx.FxMath.Clamp01(d - a + 0.5f) * Fx.FxMath.Clamp01(b - d + 0.5f);

    // 本編のボタンの押した音と当てた音 (Twitch の窓の ExitGame のボタンの物)。無ければ鳴らさない
    public static AudioClip ClickSound { get; private set; }
    public static AudioClip HoverSound { get; private set; }

    public static bool Load()
    {
        // 抜き終えた後は Unity の生存確認をしない (部品を作るたびに何度も呼ばれる)
        if (_loaded) return true;
        // Instance は無い時に読むと空の物を作るので、置かれている物を名前で探す
        var tm = GameObject.Find("TwitchManager");
        var popup = tm && tm.transform.childCount > 0 ? tm.transform.GetChild(0) : null;
        if (!popup) return false;
        // 子の並びは版で変わりうるので名前で探す
        var bg = popup.Find("Background");
        var blank = popup.Find("BackroundIgnore");
        var text = popup.Find("Text_TMP");
        var exit = popup.Find("ExitGame");
        if (exit)
        {
            var pb = exit.GetComponent<PassiveButton>();
            if (pb && pb.ClickSound) ClickSound = pb.ClickSound;
            var rh = exit.GetComponent<ButtonRolloverHandler>();
            if (rh && rh.HoverSound) HoverSound = rh.HoverSound;
            if (ClickSound) ClickSound.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            if (HoverSound) HoverSound.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        }
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
        _loaded = _box && _blank && _textProto;
        return _loaded;
    }

    // 確かめ用: 抜けた物
    public static string Diag()
    {
        var tm = GameObject.Find("TwitchManager");
        Load();
        return $"tm={(bool)tm} children={(tm ? tm.transform.childCount : -1)} box={(bool)_box} blank={(bool)_blank} text={(bool)_textProto} click={(bool)ClickSound} hover={(bool)HoverSound} hud={(bool)Vanilla.Hud}" +
            (_box ? $" boxRect={_box.rect} boxBorder={_box.border} boxPpu={_box.pixelsPerUnit} tex={_box.texture.width}x{_box.texture.height}" : "");
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
