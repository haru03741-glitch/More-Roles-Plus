using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 能力ボタンの絵 (Resources/Icons/*.png を DLL に埋め込んだ物・tools/make-button-icons.py で描く)。
// 1 回読んだら起動中は持ち続ける (ボタンを作り直すたびに読まない)
internal static class ButtonIcons
{
    private const float PixelsPerUnit = 172f; // 220 px の絵が 1.28 単位 (本編の能力ボタンの絵の枠と同じ大きさ)
    private static readonly Dictionary<string, Sprite> Cache = new();

    public static Sprite Get(string name) => Load(name, PixelsPerUnit, new Vector2(0.5f, 0.5f));

    // 同じ置き場の絵を、大きさ (1 単位あたりの px) と原点を指定して読む (演出の絵用。同じ絵を別の大きさ・原点でも持てる)
    public static Sprite Load(string name, float pixelsPerUnit, Vector2 pivot)
    {
        string key = pixelsPerUnit == PixelsPerUnit && pivot.x == 0.5f && pivot.y == 0.5f ? name : $"{name}|{pixelsPerUnit}|{pivot.x}|{pivot.y}";
        if (Cache.TryGetValue(key, out var cached) && cached) return cached;
        string res = $"MoreRolesPlus.Resources.Icons.{name}.png";
        using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(res);
        if (s == null) { Plugin.Logger.LogError($"icon missing: {res}"); return null; }
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.ToArray();
        // (long) 必須: net10 (Android) では int が nint のポインタ受けコンストラクタに解決され、壊れた配列になる
        var data = new Il2CppStructArray<byte>((long)bytes.Length);
        for (int i = 0; i < bytes.Length; i++) data[i] = bytes[i];
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "MrpIcon_" + name, wrapMode = TextureWrapMode.Clamp };
        // 読み込んだら CPU 側のピクセルは捨てる (描くだけなので要らない)
        if (!tex.LoadImage(data, true)) { Plugin.Logger.LogError($"icon decode failed: {name}"); return null; }
        tex.hideFlags = HideFlags.HideAndDontSave;
        // 読み出し不可のテクスチャは Tight の形を作れないので FullRect
        var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), pivot, pixelsPerUnit, 0, SpriteMeshType.FullRect);
        sp.name = name;
        sp.hideFlags = HideFlags.HideAndDontSave;
        Cache[key] = sp;
        return sp;
    }
}
