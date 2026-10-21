using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 家具の絵の不透明な所の型 (絵 1 枚ごとに 1 回だけ作って共有)。水の影の写しの抜き型は絵の網目で塗るが、
// 網目が四角の絵 (ハロウィンの燭台など) は透明な所まで網目に入り、影の中だけ絵の周りの四角に水が無くなる。
// 本編の絵のテクスチャは読めないので、GPU で並べて 1 回描いて読み戻す (FurnitureLift の画素の見分けと同じ使い捨てのカメラ)。
// 船ができた時に家具の絵をまとめて作る (読み戻しの止まりは 1 回だけ)。型の無い絵は網目のまま塗る
internal static class SpriteAlphaMask
{
    internal sealed class Mask
    {
        public byte[] Px;  // W × H・下の行から・1 = 不透明
        public int W, H;
        public Vector2 Min, Size; // 絵の網目の範囲 (絵の中の座標)
    }

    private const int MaxSide = 128;  // 型の一辺の上限 (抜き型は 1 単位 32 画素なので、3 単位の絵でも足りる)
    private const int AtlasPx = 1024; // 1 回に描く板の一辺
    private const int Gap = 2;        // 隣の絵がにじまない隙間
    private const int Layer = 31;     // 本編が使っていない層 (描く間だけ)
    private const byte AlphaMin = 64; // これより濃い画素を絵とみなす (縁の半透明も水を隠す)
    private static readonly Dictionary<IntPtr, Mask> Masks = new();
    private static int _gen = -1;

    internal static bool Off; // 確かめ用 (props alpha off): 型を使わず網目のまま

    internal static Mask Get(Sprite sp)
    {
        if (Off || _gen != GameClock.ShipGen || !sp) return null;
        return Masks.TryGetValue(sp.Pointer, out var m) ? m : null;
    }

    // 渡された描画物の絵のうち、まだ型の無い物を作る
    internal static void Prepare(List<SpriteRenderer> srs)
    {
        if (_gen != GameClock.ShipGen) { Masks.Clear(); _gen = GameClock.ShipGen; }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var todo = new List<(Sprite sp, int w, int h)>();
        var seen = new HashSet<IntPtr>();
        foreach (var sr in srs)
        {
            if (!sr) continue;
            var sp = sr.sprite;
            if (!sp || Masks.ContainsKey(sp.Pointer) || !seen.Add(sp.Pointer)) continue;
            var b = sp.bounds;
            float ppu = sp.pixelsPerUnit;
            float pw = b.size.x * ppu, ph = b.size.y * ppu;
            if (pw < 1f || ph < 1f) continue;
            float k = Math.Min(1f, MaxSide / Math.Max(pw, ph));
            todo.Add((sp, Math.Max(1, (int)MathF.Ceiling(pw * k)), Math.Max(1, (int)MathF.Ceiling(ph * k))));
        }
        // 棚に詰める (左から右へ・溢れたら上の段・板が埋まったら描いて次の板)
        int i = 0, boards = 0;
        while (i < todo.Count)
        {
            var batch = new List<(int idx, int x, int y)>();
            int x = 0, y = 0, rowH = 0;
            for (; i < todo.Count; i++)
            {
                var t = todo[i];
                if (x + t.w > AtlasPx) { x = 0; y += rowH + Gap; rowH = 0; }
                if (y + t.h > AtlasPx) break;
                batch.Add((i, x, y));
                x += t.w + Gap;
                rowH = Math.Max(rowH, t.h);
            }
            if (batch.Count == 0) break;
            Render(todo, batch, y + rowH);
            boards++;
        }
        if (todo.Count > 0)
            Plugin.Logger.LogInfo($"[SpriteAlphaMask] sprites={todo.Count} boards={boards} ms={(System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1}");
    }

    private static void Render(List<(Sprite sp, int w, int h)> todo, List<(int idx, int x, int y)> batch, int usedH)
    {
        int rtH = Math.Min(AtlasPx, Math.Max(16, (usedH + 15) & ~15));
        // 描く場所は船から遠い所 (層 31 の他の物が写らないように)
        var at = new Vector2(-5000f, -5000f);
        var gos = new List<GameObject>();
        var rt = RenderTexture.GetTemporary(AtlasPx, rtH, 0, RenderTextureFormat.ARGB32);
        var prev = RenderTexture.active;
        GameObject camGo = null;
        Texture2D t = null;
        try
        {
            foreach (var (idx, x, y) in batch)
            {
                var e = todo[idx];
                var b = e.sp.bounds;
                // 1 世界単位 = 板の 1 画素。絵の網目の範囲を棚の升 (x, y)〜(x + w, y + h) へ合わせる
                float sx = e.w / b.size.x, sy = e.h / b.size.y;
                var go = new GameObject("MrpAlphaMask") { layer = Layer };
                go.transform.position = FxMath.V3(at.x + x - b.min.x * sx, at.y + y - b.min.y * sy, 0f);
                go.transform.localScale = FxMath.V3(sx, sy, 1f);
                var r = go.AddComponent<SpriteRenderer>();
                // 材質は既定 (Sprites-Default) のまま。本編の MaskShader 等は型抜きの条件で絵の一部しか描かない
                r.sprite = e.sp;
                r.color = Color.white;
                gos.Add(go);
            }
            camGo = new GameObject("MrpAlphaMaskCam");
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.cullingMask = 1 << Layer;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 20f;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.orthographicSize = rtH * 0.5f;
            cam.aspect = AtlasPx / (float)rtH;
            camGo.transform.position = FxMath.V3(at.x + AtlasPx * 0.5f, at.y + rtH * 0.5f, -10f);
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;
            RenderTexture.active = rt;
            t = new Texture2D(AtlasPx, rtH, TextureFormat.RGBA32, false, true);
            t.ReadPixels(new Rect(0, 0, AtlasPx, rtH), 0, 0, false);
            var raw = t.GetRawTextureData();
            foreach (var (idx, x, y) in batch)
            {
                var e = todo[idx];
                var m = new Mask { W = e.w, H = e.h, Px = new byte[e.w * e.h], Min = e.sp.bounds.min, Size = e.sp.bounds.size };
                // 抜き型は画素の真ん中で引くので、絵の縁は抜き型の半画素ぶん太らせる (細い脚や縁の半透明の所に水が乗らない)
                int r = Math.Max(1, (int)MathF.Ceiling(0.5f / WaterArt.FrontPpu * e.w / m.Size.x));
                for (int yy = 0; yy < e.h; yy++)
                    for (int xx = 0; xx < e.w; xx++)
                    {
                        if (raw[((y + yy) * AtlasPx + x + xx) * 4 + 3] < AlphaMin) continue;
                        for (int dy = Math.Max(0, yy - r); dy <= Math.Min(e.h - 1, yy + r); dy++)
                            for (int dx = Math.Max(0, xx - r); dx <= Math.Min(e.w - 1, xx + r); dx++)
                                m.Px[dy * e.w + dx] = 1;
                    }
                Masks[e.sp.Pointer] = m;
            }
        }
        catch (Exception ex)
        {
            // 作れなかった絵は網目のまま (同じ船の間は作り直さない)
            foreach (var (idx, _, _) in batch) Masks[todo[idx].sp.Pointer] = null;
            Plugin.Logger.LogWarning($"[SpriteAlphaMask] {ex.Message}");
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            if (t) UnityEngine.Object.Destroy(t);
            if (camGo) UnityEngine.Object.Destroy(camGo);
            // Destroy はフレームの終わりまで残るので、次の板を描く前に隠す (同じ所に並べるので前の板の絵が重なる)
            foreach (var go in gos) if (go) { go.SetActive(false); UnityEngine.Object.Destroy(go); }
        }
    }
}
