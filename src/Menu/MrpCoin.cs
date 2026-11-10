using System.Collections.Generic;
using MoreRolesPlus.Roles;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// MRP コインの絵 (本編の豆の絵と同じ描き方: 太い黒の縁・塗り分け)
internal static class MrpCoin
{
    private const string Name = "mrp_coin";
    private const float PixelsPerUnit = 404f; // 絵の高さ (404 px) が 1 単位

    // 数の字の色 (コインの絵の明るい所に合わせる。本編の豆の数が豆の赤なのと同じ)
    internal static readonly Color Ink = new(0.86f, 0.52f, 0.04f, 1f);

    internal static Sprite Sprite => ButtonIcons.Load(Name, PixelsPerUnit, new Vector2(0.5f, 0.5f));

    private static readonly Dictionary<int, Sprite> Sized = new();

    // 高さが h 単位の絵 (本編の豆の絵と差し替える時に、豆と同じ大きさで出るように)
    internal static Sprite SpriteSized(float h)
    {
        int key = Mathf.RoundToInt(h * 1000f);
        if (Sized.TryGetValue(key, out var sp) && sp) return sp;
        sp = ButtonIcons.Load(Name, 404f / Mathf.Max(h, 0.001f), new Vector2(0.5f, 0.5f));
        Sized[key] = sp;
        return sp;
    }

    // 絵だけを SpriteRenderer で置く。高さ h
    internal static SpriteRenderer Place(Transform parent, Vector3 pos, float h, int order, string name = "MrpCoin")
    {
        var go = new GameObject(name) { layer = parent.gameObject.layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = new Vector3(h, h, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = Sprite;
        sr.sortingOrder = order;
        return sr;
    }
}
