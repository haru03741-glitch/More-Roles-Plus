using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;

namespace MoreRolesPlus.Menu;

// チャットの吹き出しの字の中にボタンの絵を出す (<sprite name="...">)。
// 絵ごとに TMP の絵の素材 (元の絵の入ったテクスチャをそのまま使い、範囲だけ指す) を作り、根の素材の控え (fallback) に並べる。
// 根の素材は絵を出す吹き出しの字にだけ付ける。吹き出しの字は stencil でチャット欄の枠に切り抜かれているので、
// 絵の素材にも字と同じ stencil の値を写す (写さないと送って流れた絵が枠の外に残る)
internal static class ChatIcons
{
    private const uint FirstUnicode = 0xE000; // 私用領域 (文字としては打たれない)
    private static TMP_SpriteAsset _root;
    private static readonly Dictionary<int, (string Name, TMP_SpriteAsset Asset)> Assets = new(); // 絵の InstanceID ごと
    private static Shader _shader;
    private static uint _serial;
    // 版が空だと UpdateLookupTables が古い形式 (spriteInfoList) からの変換を走らせて落ちる
    private const string AssetVersion = "1.1.0";

    // 字の中に置く絵の書き方。絵が無い・作れない時は空
    public static string Tag(Sprite sp)
    {
        if (!sp || !sp.texture) return string.Empty;
        int id = sp.GetInstanceID();
        if (Assets.TryGetValue(id, out var e) && e.Asset && e.Asset.spriteSheet) return $"<sprite name=\"{e.Name}\">";
        if (!_shader) _shader = Shader.Find("TextMeshPro/Sprite");
        if (!_shader) return string.Empty;
        if (!_root)
        {
            _root = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
            _root.name = "MrpChatIcons";
            _root.hideFlags = HideFlags.HideAndDontSave;
            _root.m_SpriteGlyphTable = new Il2CppSystem.Collections.Generic.List<TMP_SpriteGlyph>();
            _root.m_SpriteCharacterTable = new Il2CppSystem.Collections.Generic.List<TMP_SpriteCharacter>();
            _root.fallbackSpriteAssets = new Il2CppSystem.Collections.Generic.List<TMP_SpriteAsset>();
            _root.m_Version = AssetVersion;
            _root.UpdateLookupTables();
        }

        // 番号は作るたびに進める (テクスチャが消えて作り直した時に、別の絵と名前・文字が被らないように)
        uint serial = _serial++;
        string name = "mrp" + serial;
        var r = sp.textureRect;
        var glyph = new TMP_SpriteGlyph
        {
            index = 0,
            glyphRect = new GlyphRect((int)r.x, (int)r.y, (int)r.width, (int)r.height),
            metrics = new GlyphMetrics(r.width, r.height, 0f, r.height * 0.85f, r.width),
            scale = 1f,
        };
        var ch = new TMP_SpriteCharacter(FirstUnicode + serial, glyph) { name = name, scale = 1.6f }; // 字の高さ (アセンダー) に合わせて縮むので、ボタンの絵の余白の分だけ大きく

        var mat = new Material(_shader) { name = "MrpChatIcon_" + sp.name, mainTexture = sp.texture, hideFlags = HideFlags.HideAndDontSave };
        var asset = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
        asset.name = "MrpChatIcon_" + sp.name;
        asset.hideFlags = HideFlags.HideAndDontSave;
        asset.spriteSheet = sp.texture;
        asset.material = mat;
        asset.m_SpriteGlyphTable = new Il2CppSystem.Collections.Generic.List<TMP_SpriteGlyph>();
        asset.m_SpriteGlyphTable.Add(glyph);
        asset.m_SpriteCharacterTable = new Il2CppSystem.Collections.Generic.List<TMP_SpriteCharacter>();
        asset.m_SpriteCharacterTable.Add(ch);
        asset.m_Version = AssetVersion;
        asset.UpdateLookupTables();
        // 表を作り直すと文字から絵への参照が外れることがあるので付け直す
        ch.glyphIndex = 0;
        ch.glyph = glyph;

        if (e.Asset) _root.fallbackSpriteAssets.Remove(e.Asset); // 作り直した時は古い素材を控えから外す
        _root.fallbackSpriteAssets.Add(asset);
        Assets[id] = (name, asset);
        return $"<sprite name=\"{name}\">";
    }

    // 絵を出す字に根の素材を付け、絵の素材の stencil を字の素材に合わせる。素材を付け替えた (それまで付いていなかった) 時は true
    public static bool Apply(TextMeshPro t)
    {
        if (!_root || !t) return false;
        var fm = t.fontSharedMaterial;
        if (fm)
            foreach (var e in Assets.Values)
            {
                var m = e.Asset ? e.Asset.material : null;
                if (!m) continue;
                foreach (var p in StencilProps)
                    if (fm.HasProperty(p) && m.HasProperty(p)) m.SetFloat(p, fm.GetFloat(p));
            }
        if (t.spriteAsset == _root) return false;
        t.spriteAsset = _root;
        return true;
    }

    private static readonly string[] StencilProps = { "_Stencil", "_StencilComp", "_StencilOp", "_StencilReadMask", "_StencilWriteMask" };
}
