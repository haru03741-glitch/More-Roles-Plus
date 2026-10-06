using System;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace MoreRolesPlus.Options;

// Based on https://github.com/waffle-ful/Aeterna-End-K-not Patches/CalamityMenu/CalamityFonts.cs (GPL-3.0)
// 設定画面の見出し・役職名に使うフォント (Mochiy Pop One・SIL OFL 1.1)。
// - 初めて使う時に 1 回だけ作り、以後は使い回す (画面を閉じても捨てない)。
// - 字は使う時に足していく。このフォントに無い字は本編のフォントで出る。
// - 作れなかった時は本編のフォントのまま (Apply が何もしない)。
// - TMP の outlineWidth 等を tmp ごとに書くとマテリアルが複製されて残るので、太さは共有のマテリアル側に持つ。
internal static class MenuFont
{
    private const string Resource = "MoreRolesPlus.Resources.Fonts.MochiyPopOne-Regular.ttf";
    private static TMP_FontAsset _font;
    private static bool _failed;

    // vanilla = 本編のフォント (シェーダを借りる元と、無い字の代わり)
    public static TMP_FontAsset Get(TMP_FontAsset vanilla)
    {
        if (_font || _failed) return _font;
        try
        {
            _font = Create(vanilla);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"menu font: {e}");
            _font = null;
        }
        if (!_font) _failed = true;
        return _font;
    }

    // tmp の字をこのフォントにする。mask = スクロールする欄の層 (0 = 切り抜かない)・outline = 縁取りの太さ (0..1)。
    // マテリアルは組み合わせごとに 1 つを全員で使う (tmp ごとに複製しない)
    public static void Apply(TMP_Text tmp, int mask = 0, float outline = 0f)
    {
        if (!tmp) return;
        var font = Get(tmp.font);
        if (!font) return;
        if (tmp.font != font) tmp.font = font;
        var mat = Material(mask, outline);
        if (mat && tmp.fontSharedMaterial != mat) tmp.fontSharedMaterial = mat;
    }

    private static readonly System.Collections.Generic.Dictionary<int, Material> Materials = new();

    private static Material Material(int mask, float outline)
    {
        if (mask == 0 && outline <= 0f) return _font.material;
        int key = mask * 1000 + (int)(outline * 100f);
        if (Materials.TryGetValue(key, out var m) && m) return m;
        m = new Material(_font.material) { name = $"MrpMenuFont_{mask}_{outline:0.00}" };
        if (mask != 0)
        {
            // 本編の欄の中の字と同じ切り抜き (層の番号と一致した所だけ描く)
            m.SetFloat(ShaderUtilities.ID_StencilID, mask);
            m.SetFloat(ShaderUtilities.ID_StencilComp, 3f);
        }
        if (outline > 0f)
        {
            m.SetFloat(ShaderUtilities.ID_OutlineWidth, outline);
            m.SetColor(ShaderUtilities.ID_OutlineColor, new Color(0.04f, 0.04f, 0.06f, 1f));
        }
        m.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Materials[key] = m;
        return m;
    }

    // 先に字を入れておく (初めて描く時に字を作る分の引っかかりを避ける)
    public static void Prepare(string chars)
    {
        if (_font && !string.IsNullOrEmpty(chars)) _font.TryAddCharacters(chars);
    }

    // root/MoreRolesPlus へ書き出してパスを返す。書けなければ null
    private static string Export(Stream stream, string root)
    {
        try
        {
            string dir = Path.Combine(root, "MoreRolesPlus");
            string path = Path.Combine(dir, "MochiyPopOne-Regular.ttf");
            if (!File.Exists(path) || new FileInfo(path).Length != stream.Length)
            {
                Directory.CreateDirectory(dir);
                stream.Position = 0;
                using var fs = File.Create(path);
                stream.CopyTo(fs);
            }
            return path;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"menu font: cannot write under {root}: {e.Message}");
            return null;
        }
    }

    private static TMP_FontAsset Create(TMP_FontAsset vanilla)
    {
        // Font はファイルからしか作れないので、埋め込みのフォントを一度だけキャッシュへ書き出す
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource);
        if (stream == null) return null;
        // 置き場は BepInEx の cache。書けない環境 (端末によっては) なら config の隣へ
        string path = Export(stream, BepInEx.Paths.CachePath) ?? Export(stream, BepInEx.Paths.ConfigPath);
        if (path == null) return null;

        var font = new Font(path);
        var fa = TMP_FontAsset.CreateFontAsset(font, 64, 6, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
        if (!fa) return null;
        fa.name = "MrpMenuFont";

        // TMP のシェーダがビルドに無いとマテリアルが空になるので、本編のフォントのシェーダを借りる
        if (vanilla && vanilla.material && (!fa.material || !fa.material.shader || !fa.material.shader.isSupported))
        {
            var mat = new Material(vanilla.material);
            mat.SetTexture(ShaderUtilities.ID_MainTex, fa.atlasTexture);
            mat.SetFloat(ShaderUtilities.ID_TextureWidth, fa.atlasWidth);
            mat.SetFloat(ShaderUtilities.ID_TextureHeight, fa.atlasHeight);
            mat.SetFloat(ShaderUtilities.ID_GradientScale, fa.atlasPadding + 1);
            fa.material = mat;
        }
        // Mobile 系の SDF シェーダは OUTLINE_ON が無いと縁取りを描かない
        if (fa.material) fa.material.EnableKeyword(ShaderUtilities.Keyword_Outline);

        if (vanilla)
        {
            fa.fallbackFontAssetTable ??= new Il2CppSystem.Collections.Generic.List<TMP_FontAsset>();
            fa.fallbackFontAssetTable.Add(vanilla);
        }

        // シーンの切り替えで消されないように
        font.hideFlags = HideFlags.DontUnloadUnusedAsset;
        fa.hideFlags = HideFlags.DontUnloadUnusedAsset;
        if (fa.material) fa.material.hideFlags = HideFlags.DontUnloadUnusedAsset;
        if (fa.atlasTexture) fa.atlasTexture.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Plugin.Logger.LogInfo($"menu font: shader={(fa.material ? fa.material.shader.name : "-")} atlas={fa.atlasWidth}x{fa.atlasHeight}");
        return fa;
    }
}
