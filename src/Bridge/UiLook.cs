using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Bridge;

// 確かめ用: 本編の部品と、それを写して作った部品の見た目の違いを数える (bridge uilook)。
// 位置と文字の中身は比べない (並べる場所と字は替えるのが前提)。色・絵・字の大きさ/色/書体・大きさ・有効かを子まで比べる
internal static class UiLook
{
    public static Transform FindDeep(Transform root, string name)
    {
        for (int i = 0; i < root.childCount; i++)
        {
            var c = root.GetChild(i);
            if (c.name == name) return c;
            var hit = FindDeep(c, name);
            if (hit) return hit;
        }
        return null;
    }

    public static void Compare(Transform a, Transform b, string path, int depth, List<string> diffs)
    {
        string here = path.Length == 0 ? a.name : path + "/" + a.name;
        void Diff(string field, object va, object vb)
        {
            string sa = va?.ToString() ?? "-", sb = vb?.ToString() ?? "-";
            if (sa != sb) diffs.Add($"{here} {field}: {sa} | {sb}");
        }

        Diff("active", a.gameObject.activeSelf, b.gameObject.activeSelf);
        Diff("scale", Vec(a.localScale), Vec(b.localScale));
        var ra = a.GetComponent<SpriteRenderer>();
        var rb = b.GetComponent<SpriteRenderer>();
        Diff("sprite renderer", ra != null, rb != null);
        if (ra != null && rb != null)
        {
            Diff("sr.enabled", ra.enabled, rb.enabled);
            Diff("sr.color", Col(ra.color), Col(rb.color));
            Diff("sr.sprite", ra.sprite ? ra.sprite.name : "-", rb.sprite ? rb.sprite.name : "-");
            Diff("sr.size", Vec(ra.size), Vec(rb.size));
            Diff("sr.order", ra.sortingOrder, rb.sortingOrder);
            Diff("sr.mask", ra.maskInteraction, rb.maskInteraction);
        }
        var ta = a.GetComponent<TextMeshPro>();
        var tb = b.GetComponent<TextMeshPro>();
        Diff("tmp", ta != null, tb != null);
        if (ta != null && tb != null)
        {
            Diff("tmp.enabled", ta.enabled, tb.enabled);
            Diff("tmp.autoSize", ta.enableAutoSizing, tb.enableAutoSizing);
            // 自動の大きさは字の長さで決まるので、上限と下限を比べる
            if (ta.enableAutoSizing && tb.enableAutoSizing)
            {
                Diff("tmp.fontSizeMax", ta.fontSizeMax.ToString("0.##"), tb.fontSizeMax.ToString("0.##"));
                Diff("tmp.fontSizeMin", ta.fontSizeMin.ToString("0.##"), tb.fontSizeMin.ToString("0.##"));
            }
            else Diff("tmp.fontSize", ta.fontSize.ToString("0.##"), tb.fontSize.ToString("0.##"));
            Diff("tmp.color", Col(ta.color), Col(tb.color));
            Diff("tmp.font", ta.font ? ta.font.name : "-", tb.font ? tb.font.name : "-");
            Diff("tmp.style", ta.fontStyle, tb.fontStyle);
            Diff("tmp.align", ta.alignment, tb.alignment);
            // 色の札 (<color=...>) を字に埋めると color の値が同じでも違って見える
            Diff("tmp.richColor", ta.text != null && ta.text.Contains("<color"), tb.text != null && tb.text.Contains("<color"));
        }
        if (depth <= 0) return;
        Diff("children", a.childCount, b.childCount);
        int n = Mathf.Min(a.childCount, b.childCount);
        for (int i = 0; i < n; i++)
        {
            var ca = a.GetChild(i);
            var cb = b.GetChild(i);
            if (ca.name != cb.name)
            {
                diffs.Add($"{here} child{i}: {ca.name} | {cb.name}");
                continue;
            }
            Compare(ca, cb, here, depth - 1, diffs);
        }
    }

    private static string Vec(Vector3 v) => $"({v.x:0.###},{v.y:0.###},{v.z:0.###})";
    private static string Vec(Vector2 v) => $"({v.x:0.###},{v.y:0.###})";
    private static string Col(Color c) => $"({c.r:0.###},{c.g:0.###},{c.b:0.###},{c.a:0.###})";
}
