using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.UI;

// メインメニューのクルーの大きな絵 (本編のテクスチャ MainScreenCrew の 6 ポーズ) を、ほかの画面の飾りに使う。
// 絵は体/影/バイザーを R/B/G で塗り分けた物なので、本編の人の材質で色を付ける。
// 絵が読み込まれているのはメインメニューだけなので、そこで抜いて捨てられないようにしておく。
[HarmonyPatch]
internal static class CrewArt
{
    public const int Count = 6;

    private static readonly Sprite[] Poses = new Sprite[Count]; // テクスチャの左から順
    private static bool _loaded;
    private static int _tries; // 見つからない版で、メインメニューに戻るたびに全部の絵を走査し続けないため
    private static readonly Dictionary<int, Material> Mats = new(); // 色ごとに 1 つ (作ったら捨てない)

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start)), HarmonyPostfix]
    public static void OnMainMenu()
    {
        if (_loaded || _tries >= 3) return;
        _tries++;
        var found = new List<Sprite>();
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
        {
            var sp = o.TryCast<Sprite>();
            if (!sp) continue;
            var tex = sp.texture;
            if (tex && tex.name == "MainScreenCrew") found.Add(sp);
        }
        if (found.Count < Count) return;
        found.Sort((a, b) => a.textureRect.x.CompareTo(b.textureRect.x));
        for (int i = 0; i < Count; i++)
        {
            Poses[i] = found[i];
            Poses[i].hideFlags |= HideFlags.DontUnloadUnusedAsset;
        }
        found[0].texture.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        _loaded = true;
    }

    public static Sprite Pose(int i) => _loaded && i >= 0 && i < Count && Poses[i] ? Poses[i] : null;

    // 体の色 c のクルーを描く材質 (影は体を暗くした色・バイザーは本編の色)
    public static Material Material(Color c)
    {
        int key = ((int)(c.r * 255f) << 16) | ((int)(c.g * 255f) << 8) | (int)(c.b * 255f);
        if (Mats.TryGetValue(key, out var m) && m) return m;
        var src = Fx.FxHands.PlayerMat();
        if (!src) return null;
        m = new Material(src) { name = "MrpCrewArt", hideFlags = HideFlags.DontUnloadUnusedAsset };
        m.SetColor("_BodyColor", c);
        m.SetColor("_BackColor", new Color(c.r * 0.6f, c.g * 0.6f, c.b * 0.55f + 0.03f, 1f));
        m.SetColor("_VisorColor", new Color(149f / 255f, 202f / 255f, 220f / 255f, 1f));
        Mats[key] = m;
        return m;
    }

    // parent の子に絵を 1 枚置く (足元を at に合わせ、高さを height に)。絵か材質が無ければ何もしない
    public static SpriteRenderer Place(Transform parent, int pose, Color c, Vector3 at, float height, int layer, int order)
    {
        var sp = Pose(pose);
        var mat = sp ? Material(c) : null;
        if (!mat) return null;
        var go = new GameObject("MrpCrewArt") { layer = layer };
        go.transform.SetParent(parent, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        sr.sharedMaterial = mat;
        sr.sortingOrder = order;
        var b = sp.bounds;
        float s = height / b.size.y;
        go.transform.localScale = new Vector3(s, s, 1f);
        go.transform.localPosition = new Vector3(at.x - b.center.x * s, at.y - b.min.y * s, at.z);
        return sr;
    }
}
