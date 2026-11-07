using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace MoreRolesPlus.Options;

// どんな字も出せるようにする: 本編の字体に無い字 (絵文字・記号・珍しい漢字・ハングル・アラビア文字 など) を、
// 端末に入っている字体から借りて出す (TMP の予備の字体の表に足す)。
// - 字体は「本編の字体に無い字が実際に来た時」に、その字の種類に合う物だけを読む (起動の時には読まない)。
//   字体のファイルは 1 つ 2〜30MB あり、全部を先に読むと起動が遅く、メモリも 100MB 余り増えていた
// - 来た字を見るのは、人が打った字が入る所だけ: 入力欄・チャット・人の名前 (1 秒ごとに見る。同じ名前は 2 度見ない)
// - 字体のファイルは端末の物を読むだけ (配らない)。字は使われた時に足していく (Dynamic)
// - 絵文字は字の形だけ (単色) で出る。色付きの絵文字の絵は TMP の SDF では描けない
// - 作れない字体は飛ばす (無ければその字は今まで通り □)
internal static class FontFallback
{
    private enum Kind { Emoji, Symbol, Latin, Hangul, Cjk, CjkExt, Thai, Indic, African, Other }

#if ANDROID
    private const string Dir = "/system/fonts";
    private static readonly Dictionary<Kind, string[]> Files = new()
    {
        [Kind.Emoji] = new[] { "NotoEmoji-Regular.ttf", "NotoSansSymbols-Regular-Subsetted2.ttf" },
        [Kind.Symbol] = new[] { "NotoSansSymbols-Regular-Subsetted.ttf", "NotoSansSymbols-Regular-Subsetted2.ttf", "NotoSansMath-Regular.ttf", "Roboto-Regular.ttf" },
        [Kind.Latin] = new[] { "Roboto-Regular.ttf", "NotoNaskhArabic-Regular.ttf", "NotoSansHebrew-Regular.ttf" },
        [Kind.Hangul] = new[] { "NotoSansCJK-Regular.ttc", "DroidSansFallbackFull.ttf" },
        [Kind.Cjk] = new[] { "NotoSansCJK-Regular.ttc", "NotoSerifCJK-Regular.ttc", "DroidSansFallbackFull.ttf", "DroidSansFallback.ttf" },
        [Kind.CjkExt] = new[] { "NotoSansCJK-Regular.ttc", "DroidSansFallbackFull.ttf" },
        [Kind.Thai] = new[] { "NotoSansThai-Regular.ttf" },
        [Kind.Indic] = new[] { "NotoSansDevanagari-Regular.ttf" },
        [Kind.African] = new[] { "Roboto-Regular.ttf" },
        [Kind.Other] = new[] { "NotoSansSymbols-Regular-Subsetted.ttf", "Roboto-Regular.ttf", "NotoSansCJK-Regular.ttc" },
    };
#else
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
    private static readonly Dictionary<Kind, string[]> Files = new()
    {
        [Kind.Emoji] = new[] { "seguiemj.ttf", "seguisym.ttf" },
        [Kind.Symbol] = new[] { "seguisym.ttf", "segoeui.ttf", "seguiemj.ttf" },
        [Kind.Latin] = new[] { "segoeui.ttf", "seguisym.ttf" },
        [Kind.Hangul] = new[] { "malgun.ttf" },
        [Kind.Cjk] = new[] { "YuGothM.ttc", "msyh.ttc", "meiryo.ttc", "simsun.ttc", "msgothic.ttc" },
        [Kind.CjkExt] = new[] { "SimsunExtG.ttf", "simsun.ttc" },
        [Kind.Thai] = new[] { "LeelawUI.ttf" },
        [Kind.Indic] = new[] { "Nirmala.ttc" },
        [Kind.African] = new[] { "ebrima.ttf", "gadugi.ttf" },
        [Kind.Other] = new[] { "seguisym.ttf", "segoeui.ttf", "gadugi.ttf", "ebrima.ttf", "msyh.ttc" },
    };
#endif

    private static TMP_FontAsset _vanilla;
    private static readonly List<TMP_FontAsset> Made = new();
    private static readonly Dictionary<string, TMP_FontAsset> ByFile = new(StringComparer.OrdinalIgnoreCase); // 読めなかった字体は null
    private static readonly HashSet<int> Unfound = new();     // どの字体にも無かった字 (2 度探さない)
    private static readonly HashSet<string> SeenText = new(); // 見た名前 (2 度見ない)

    private static TMP_FontAsset Vanilla()
    {
        if (_vanilla) return _vanilla;
        foreach (var t in UnityEngine.Object.FindObjectsOfType<TextMeshPro>())
            if (t && t.font && t.font.material) return _vanilla = t.font;
        return null;
    }

    // 本編の字体で出せない字があれば、その字の種類に合う字体を読んで予備に足す。足したら true
    internal static bool EnsureFor(string text)
    {
        if (string.IsNullOrEmpty(text) || PlainOnly(text)) return false;
        bool added = false;
        try
        {
            var vanilla = Vanilla();
            if (!vanilla || !NeedsLook(text, vanilla)) return false;
            if (Seen.Count == 0) { _sceneDirty = true; AttachAll(true); } // 本編の字体の表を控える (足した字体を後で入れるため)
            for (int i = 0; i < text.Length; i++)
            {
                int cp = text[i];
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { cp = char.ConvertToUtf32(text[i], text[i + 1]); i++; }
                else if (char.IsControl(text[i]) || char.IsSurrogate(text[i])) continue;
                if (cp < 0x80 || cp == 0xFE0F || cp == 0xFE0E || cp == 0x200D || cp == 0x200B) continue;
                if (Unfound.Contains(cp) || Has(vanilla, cp)) continue;
                if (Load(cp)) added = true;
                else Unfound.Add(cp);
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"font fallback: {e.Message}"); }
        return added;
    }

    // ASCII・かな・和文の記号だけの文は字体を探す前に帰る (本編の字体を探す処理は画面の文字を全部見るので重い)
    private static bool PlainOnly(string s)
    {
        foreach (char c in s)
            if (!(c < 0x80 || c >= 0x3040 && c <= 0x30FF || c >= 0x3000 && c <= 0x303F)) return false;
        return true;
    }

    // ひらがな・カタカナ・ASCII だけの字は本編の字体で出るので見ない (たいていの名前やチャットはここで終わる)
    private static bool NeedsLook(string s, TMP_FontAsset vanilla)
    {
        foreach (char c in s)
        {
            if (c < 0x80 || c >= 0x3040 && c <= 0x30FF || c >= 0x3000 && c <= 0x303F) continue;
            // 漢字・全角の記号も、本編の字体にあれば見ない (一部の漢字は無い)
            if (c >= 0x4E00 && c <= 0x9FFF || c >= 0xFF00 && c <= 0xFFEF) { if (Has(vanilla, c)) continue; }
            return true;
        }
        return false;
    }

    private static bool Has(TMP_FontAsset fa, int cp)
    {
        if (cp <= 0xFFFF)
        {
            try { return fa.HasCharacter((char)cp, true, true); }
            catch { return false; }
        }
        // 0xFFFF より上の字 (多くの絵文字) は char で尋ねられないので、表に足してみて見る
        if (Own(fa, cp)) return true;
        foreach (var f in Made) if (Own(f, cp)) return true;
        return false;
    }

    private static bool Own(TMP_FontAsset fa, int cp)
    {
        try
        {
            if (cp <= 0xFFFF) return fa.HasCharacter((char)cp, false, true);
            var table = fa.characterLookupTable;
            if (table != null && table.ContainsKey((uint)cp)) return true;
            if (fa.atlasPopulationMode != AtlasPopulationMode.Dynamic) return false;
            fa.TryAddCharacters(char.ConvertFromUtf32(cp), out string _);
            table = fa.characterLookupTable;
            return table != null && table.ContainsKey((uint)cp);
        }
        catch { return false; }
    }

    private static Kind KindOf(int cp)
    {
        if (cp >= 0x1F000 && cp <= 0x1FAFF || cp >= 0x2600 && cp <= 0x27BF || cp >= 0x2B00 && cp <= 0x2BFF) return Kind.Emoji;
        if (cp >= 0x20000) return Kind.CjkExt;
        if (cp >= 0x1100 && cp <= 0x11FF || cp >= 0x3130 && cp <= 0x318F || cp >= 0xAC00 && cp <= 0xD7AF) return Kind.Hangul;
        if (cp >= 0x2E80 && cp <= 0x9FFF || cp >= 0xF900 && cp <= 0xFAFF || cp >= 0xFF00 && cp <= 0xFFEF) return Kind.Cjk;
        if (cp >= 0x0E00 && cp <= 0x0EFF || cp >= 0x1780 && cp <= 0x17FF) return Kind.Thai;
        if (cp >= 0x0900 && cp <= 0x0DFF) return Kind.Indic;
        if (cp >= 0x1200 && cp <= 0x167F) return Kind.African;
        if (cp >= 0x2000 && cp <= 0x2BFF || cp >= 0xE000 && cp <= 0xF8FF) return Kind.Symbol;
        if (cp < 0x2000 || cp >= 0xFB00 && cp <= 0xFDFF || cp >= 0xFE70 && cp <= 0xFEFF) return Kind.Latin;
        return Kind.Other;
    }

    // その字の種類の字体を順に読み、その字がある物が見つかるまで (見つからなければ false)
    private static bool Load(int cp)
    {
        foreach (var file in Files[KindOf(cp)])
        {
            if (!ByFile.TryGetValue(file, out var fa))
            {
                fa = null;
                string path = Path.Combine(Dir, file);
#if ANDROID
                if (!File.Exists(path)) path = Path.Combine("/product/fonts", file);
#endif
                try
                {
                    if (File.Exists(path))
                    {
                        var sw = Stopwatch.StartNew();
                        fa = Create(path, _vanilla);
                        if (fa)
                        {
                            Made.Add(fa);
                            Attach(fa);
                            Plugin.Logger.LogInfo($"font fallback: + {file} for U+{cp:X4} ({sw.ElapsedMilliseconds}ms)");
                        }
                    }
                }
                catch (Exception e) { Plugin.Logger.LogWarning($"font fallback: {file} {e.Message}"); }
                ByFile[file] = fa;
            }
            if (fa && Own(fa, cp)) return true;
        }
        return false;
    }

    private static TMP_FontAsset Create(string path, TMP_FontAsset vanilla)
    {
        var font = new Font(path);
        var fa = TMP_FontAsset.CreateFontAsset(font, 64, 6, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
        if (!fa) return null;
        fa.name = "MrpFallback_" + Path.GetFileNameWithoutExtension(path);
        // TMP のシェーダがビルドに無いとマテリアルが空になるので、本編の字体のシェーダを借りる (MenuFont と同じ)
        if (vanilla.material && (!fa.material || !fa.material.shader || !fa.material.shader.isSupported))
        {
            var mat = new Material(vanilla.material);
            mat.SetTexture(ShaderUtilities.ID_MainTex, fa.atlasTexture);
            mat.SetFloat(ShaderUtilities.ID_TextureWidth, fa.atlasWidth);
            mat.SetFloat(ShaderUtilities.ID_TextureHeight, fa.atlasHeight);
            mat.SetFloat(ShaderUtilities.ID_GradientScale, fa.atlasPadding + 1);
            fa.material = mat;
        }
        font.hideFlags = HideFlags.DontUnloadUnusedAsset;
        fa.hideFlags = HideFlags.DontUnloadUnusedAsset;
        if (fa.material) fa.material.hideFlags = HideFlags.DontUnloadUnusedAsset;
        if (fa.atlasTexture) fa.atlasTexture.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return fa;
    }

    // 全体の予備の表と、控えた字体それぞれの予備の表の後ろに足す
    private static void Attach(TMP_FontAsset f)
    {
        try
        {
            var global = TMP_Settings.fallbackFontAssets;
            if (global != null && !global.Contains(f)) global.Add(f);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"font fallback: global {e.Message}"); }
        foreach (var fa in Seen.Values)
            if (fa && fa.fallbackFontAssetTable != null && !fa.fallbackFontAssetTable.Contains(f)) fa.fallbackFontAssetTable.Add(f);
        _sceneDirty = true;
    }

    // 字体それぞれの予備の表 (全体の表を見ない字体もあるので、シーンごとに新しく出た字体にも足す)。
    // 字体をまだ 1 つも足していない間は何もしない (起動の時に字体を全部探す手間を省く)
    private static readonly Dictionary<IntPtr, TMP_FontAsset> Seen = new();
    private static bool _sceneDirty = true;
    private static void AttachAll(bool force = false)
    {
        if (!force && Made.Count == 0 || !_sceneDirty) return;
        _sceneDirty = false;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<TMP_FontAsset>()))
        {
            var fa = o ? o.TryCast<TMP_FontAsset>() : null;
            if (!fa || fa.name.StartsWith("MrpFallback_", StringComparison.Ordinal)) continue;
            Seen[fa.Pointer] = fa;
            fa.fallbackFontAssetTable ??= new Il2CppSystem.Collections.Generic.List<TMP_FontAsset>();
            foreach (var f in Made)
                if (!fa.fallbackFontAssetTable.Contains(f)) fa.fallbackFontAssetTable.Add(f);
        }
    }

    // 人の名前は 1 秒ごとに見る。新しく字体を足したら字を描き直す
    private static long _next;
    internal static void Tick()
    {
        long now = Environment.TickCount64; // 毎回呼ばれるので Unity の時刻を読まない
        if (now < _next) return;
        _next = now + 1000;
        bool added = false;
        try
        {
            foreach (var p in PlayerControl.AllPlayerControls)
            {
                if (!p || p.Data == null) continue;
                string n = p.Data.PlayerName;
                if (string.IsNullOrEmpty(n) || !SeenText.Add(n)) continue;
                if (EnsureFor(n)) added = true;
            }
        }
        catch { }
        if (SeenText.Count > 4096) SeenText.Clear();
        if (added) Redraw();
    }

    private static void Redraw()
    {
        AttachAll();
        foreach (var t in UnityEngine.Object.FindObjectsOfType<TextMeshPro>())
            if (t && t.isActiveAndEnabled) t.SetAllDirty();
        foreach (var t in UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>())
            if (t && t.isActiveAndEnabled) t.SetAllDirty();
    }

    internal static string Describe() => $"fontfallback made={Made.Count} ({string.Join(",", Made.ConvertAll(f => f.name))}) unfound={Unfound.Count}";

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
    private static class MenuPatch
    {
        public static void Postfix() { _vanilla = null; _sceneDirty = true; AttachAll(); }
    }

    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
    private static class HudPatch
    {
        public static void Postfix() { _sceneDirty = true; AttachAll(); }
    }

    [HarmonyPatch(typeof(ChatController), nameof(ChatController.AddChat))]
    private static class ChatPatch
    {
        public static void Prefix([HarmonyArgument(1)] string chatText)
        {
            try { if (EnsureFor(chatText)) AttachAll(); } catch { }
        }
    }

    [HarmonyPatch(typeof(TextBoxTMP), nameof(TextBoxTMP.SetText))]
    private static class BoxPatch
    {
        public static void Prefix([HarmonyArgument(0)] string input, [HarmonyArgument(1)] string inputCompo)
        {
            try { if (EnsureFor(string.IsNullOrEmpty(inputCompo) ? input : input + inputCompo)) Redraw(); } catch { }
        }
    }
}
