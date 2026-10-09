using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 水の計算 (WaterSim) の高さを絵にする。WaterSim.TileCells 升四方のタイルごとに 1 枚の絵を持ち、
// 水が変わったタイルだけを Redraw 秒ごと・1 フレーム TilesPerFrame 枚まで描き直す。
// 絵に書くのは水の量だけ (R = 深さ / DeepScale・G = 見せてよいか)。色・縁・水面の揺れときらめきはシェーダ MRP/Water が決める。
// 高さは 3×3 (1-2-1) でならしてから升の真ん中の値として双線形で引く (シェーダの側でも双線形で引くので縁はなめらか)。
// 壁の中 (SolidMap で歩けない所) と、部屋の絵に描き込まれた家具の上は見せない (水は家具の下を通っている)。
// 1 画素 = SolidMap の 1 升 (1/16 単位) なので、壁の際もその細かさで切れる。
// 影の中の焼いた絵 (ShadowPatch) には描き込まない: 水は部屋いっぱいに広がり、焼く升が数十になって読み戻しで止まる (実測 127 回・1 回 30ms 前後)。
// 水は視界の中だけに見える
internal static class WaterArt
{
    private const int Px = 4;                 // 升 1 つの画素
    private const float DeepScale = 600f;     // R = 1 の深さ (シェーダの _Edge = 30 / 600 が水の縁)
    private const float Redraw = 0.1f;
    private const int TilesPerFrame = 3;
    private const int FurnSub = Px;           // 家具の型抜きの細かさ (= 画素)

    private sealed class Tile
    {
        public int Tx, Ty;
        public GameObject Go;
        public SpriteRenderer Sr;
        public Texture2D Tex;
        public Sprite Sp;
        public byte[] Px;
        public bool[] Furn;
        public int FurnVersion;
        public float LastDraw = -10f;
        public bool Waiting;
        public bool Empty = true;
    }

    private static readonly Dictionary<int, Tile> Tiles = new();
    private static readonly List<int> Waiting = new();
    private static int[] _raw;     // 描く間の高さ (タイル + 縁 3 升)
    private static int[] _depth;   // それを 3×3 でならした物 (タイル + 縁 2 升)
    private static float _clock;
    private static int _furnVersion;
    private static long _lastMs;
    internal static double LastDrawMs { get; private set; }
    internal static int Drawn { get; private set; }
    // 切り分け用: 水たまりの絵を全部隠す (計算と描き直しは続ける)。bridge `layer water 0`
    internal static bool Hidden;
    internal static int DebugHide(bool hide)
    {
        Hidden = hide;
        int n = 0;
        foreach (var t in Tiles.Values) if (t.Sr) { t.Sr.enabled = !hide && !t.Empty; n++; }
        return n;
    }

    public static void Tick()
    {
        try { TickCore(); }
        catch (Exception e) { Plugin.Logger.LogError($"[WaterArt] {e}"); Clear(); WaterSim.Reset(); }
    }

    private static void TickCore()
    {
        if (!WaterSim.Ready) { if (Tiles.Count > 0) Clear(); _lastMs = 0; return; }
        if (!MrpBundle.WaterMaterial) return;
        long now = Environment.TickCount64;
        float dt = _lastMs == 0 ? 0.016f : Math.Min(0.1f, (now - _lastMs) / 1000f);
        _lastMs = now;
        _clock += dt;

        var dirty = WaterSim.DirtyTiles;
        if (dirty.Count > 0)
        {
            foreach (int t in dirty)
            {
                WaterSim.Clean(t);
                if (!Tiles.TryGetValue(t, out var tile)) Tiles[t] = tile = new Tile { Tx = t % WaterSim.TilesW, Ty = t / WaterSim.TilesW };
                if (!tile.Waiting) { tile.Waiting = true; Waiting.Add(t); }
            }
            dirty.Clear();
        }
        int drawn = 0;
        bool created = false;
        for (int i = 0; i < Waiting.Count && drawn < TilesPerFrame; i++)
        {
            var tile = Tiles[Waiting[i]];
            if (_clock - tile.LastDraw < Redraw) continue;
            // 新しいタイルは家具の型抜き (当たり判定を画素ごとに引く) が重いので 1 フレーム 1 枚まで
            if (tile.Px == null) { if (created) continue; created = true; }
            tile.Waiting = false;
            Waiting.RemoveAt(i--);
            Draw(tile);
            drawn++;
        }
    }

    private static unsafe void Draw(Tile t)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int tc = WaterSim.TileCells, w = WaterSim.W, h = WaterSim.H;
        int x0 = t.Tx * tc, y0 = t.Ty * tc;
        // 高さを縁 3 升ぶん広げて写し (光の筋はずらした所を見るので余分に)、1-2-1 でならす
        int dw = tc + 4, rw = tc + 6;
        _raw ??= new int[rw * rw];
        _depth ??= new int[dw * dw];
        bool any = false;
        for (int y = 0; y < rw; y++)
        for (int x = 0; x < rw; x++)
        {
            int cx = x0 + x - 3, cy = y0 + y - 3;
            int v = cx < 1 || cy < 1 || cx >= w - 1 || cy >= h - 1 ? 0 : WaterSim.Height(cy * w + cx);
            // 閉じた升 (壁の際) には、開いた隣が 1 つだけの時にその水を描き込む。壁の中は画素ごとの SolidMap で切るので、
            // 水が壁の際まで届いて見える。開いた隣が 2 つ以上 (升の中を細い壁が通っている) だと、乾いた側まで水を描いてしまうので描かない
            if (v == 0 && cx >= 1 && cy >= 1 && cx < w - 1 && cy < h - 1 && !WaterSim.Open(cy * w + cx))
            {
                int k = cy * w + cx, open = 0, from = 0;
                if (WaterSim.Open(k + 1)) { open++; from = k + 1; }
                if (WaterSim.Open(k - 1)) { open++; from = k - 1; }
                if (WaterSim.Open(k + w)) { open++; from = k + w; }
                if (WaterSim.Open(k - w)) { open++; from = k - w; }
                if (open == 1) v = WaterSim.Height(from);
            }
            _raw[y * rw + x] = v;
            if (v > 0 && x >= 2 && y >= 2 && x < rw - 2 && y < rw - 2) any = true;
        }
        for (int y = 0; y < dw; y++)
        for (int x = 0; x < dw; x++)
        {
            int c = (y + 1) * rw + x + 1;
            int sum = _raw[c] * 4 + (_raw[c - 1] + _raw[c + 1] + _raw[c - rw] + _raw[c + rw]) * 2
                + _raw[c - rw - 1] + _raw[c - rw + 1] + _raw[c + rw - 1] + _raw[c + rw + 1];
            _depth[y * dw + x] = sum / 16;
        }
        t.LastDraw = _clock;
        if (!any)
        {
            if (!t.Empty && t.Sr) t.Sr.enabled = false;
            t.Empty = true;
            return;
        }
        Ensure(t);

        int n = tc * Px;
        float cell = WaterSim.Cell;
        if (t.FurnVersion != _furnVersion)
        {
            t.FurnVersion = _furnVersion;
            t.Furn = FurnitureMask(WaterSim.Origin.x + t.Tx * tc * cell, WaterSim.Origin.y + t.Ty * tc * cell, tc * cell);
        }
        int sw = SolidMap.W;
        float sppu = SolidMap.Ppu;
        var sorg = SolidMap.Origin;
        var org = WaterSim.Origin;
        for (int py = 0; py < n; py++)
        for (int px = 0; px < n; px++)
        {
            int i = (py * n + px) * 4;
            float d = Sample(px, py);
            float wx = org.x + (x0 + (px + 0.5f) / Px) * cell, wy = org.y + (y0 + (py + 0.5f) / Px) * cell;
            int sx = (int)((wx - sorg.x) * sppu), sy = (int)((wy - sorg.y) * sppu);
            // 壁の際の溝を埋めるために広げた分も、絵の無い空の上には描かない
            bool vis = NearOpen(sx, sy, sw) && (t.Furn == null || !t.Furn[py * n + px]) && !SolidMap.BareSky(wx, wy);
            t.Px[i] = (byte)Math.Min(255f, d * (255f / DeepScale));
            t.Px[i + 1] = vis ? (byte)255 : (byte)0;
            t.Px[i + 2] = 0;
            t.Px[i + 3] = 255;
        }
        fixed (byte* b = t.Px) t.Tex.LoadRawTextureData((IntPtr)b, t.Px.Length);
        t.Tex.Apply(false, false);
        if (t.Empty) { t.Sr.enabled = !Hidden; t.Empty = false; }
        Drawn++;
        LastDrawMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    // 水を見せてよい画素か: 歩ける所か、左右 Undilate 升・下 UndilateDown 升・上 UndilateUp 升以内に歩ける所がある所。
    // SolidMap は継ぎ目を塞ぐために壁の線を両側へ太らせてあり、そのままだと壁の手前に乾いた帯 (溝) が残る。
    // 太らせた幅までしか戻さないので、元の壁の線は越えない (向こう側の部屋へは描かない)。
    // 上の壁は当たり判定の線が床の絵の際より手前の所 (ミラの廊下) と、ロッカーのように絵が線より手前へ出ている所があり、
    // 上へ 2 升戻すとロッカーの下に水が掛かるので 1 升だけ戻す
    private const int Undilate = 2;
    private const int UndilateDown = 2;
    private const int UndilateUp = 1;

    private static bool NearOpen(int sx, int sy, int sw)
    {
        int k = sy * sw + sx;
        if (SolidMap.OpenCell(k)) return true;
        if (sx >= Undilate && sx < sw - Undilate)
            for (int d = 1; d <= Undilate; d++)
                if (SolidMap.OpenCell(k + d) || SolidMap.OpenCell(k - d)) return true;
        // 下に歩ける所がある = 上の壁の手前の帯 / 上に歩ける所がある = 下の壁の手前の帯
        if (sy >= UndilateUp)
            for (int d = 1; d <= UndilateUp; d++)
                if (SolidMap.OpenCell(k - d * sw)) return true;
        if (sy < SolidMap.H - UndilateDown)
            for (int d = 1; d <= UndilateDown; d++)
                if (SolidMap.OpenCell(k + d * sw)) return true;
        return false;
    }

    // タイルの画素 (px, py) での高さ (升の真ん中の値を双線形で)
    private static float Sample(int px, int py)
    {
        int dw = WaterSim.TileCells + 4;
        float u = (px + 0.5f) / Px - 0.5f + 2f, v = (py + 0.5f) / Px - 0.5f + 2f;
        int u0 = (int)MathF.Floor(u), v0 = (int)MathF.Floor(v);
        if (u0 < 0 || v0 < 0 || u0 + 1 >= dw || v0 + 1 >= dw) return 0f;
        float fu = u - u0, fv = v - v0;
        float a = _depth[v0 * dw + u0], b = _depth[v0 * dw + u0 + 1], c = _depth[(v0 + 1) * dw + u0], d = _depth[(v0 + 1) * dw + u0 + 1];
        return (a + (b - a) * fu) * (1f - fv) + (c + (d - c) * fu) * fv;
    }

    private static void Ensure(Tile t)
    {
        if (t.Go) return;
        int tc = WaterSim.TileCells, n = tc * Px;
        float cell = WaterSim.Cell;
        var org = WaterSim.Origin;
        t.Px = new byte[n * n * 4];
        t.Tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "MrpWater", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        t.Sp = Sprite.Create(t.Tex, new Rect(0, 0, n, n), Vector2.zero, Px / cell, 0, SpriteMeshType.FullRect);
        t.Sp.name = "MrpWater";
        t.Go = new GameObject("MrpWater") { layer = 0 };
        t.Sr = t.Go.AddComponent<SpriteRenderer>();
        t.Sr.sprite = t.Sp;
        t.Sr.sharedMaterial = MrpBundle.WaterMaterial;
        float wx = org.x + t.Tx * tc * cell, wy = org.y + t.Ty * tc * cell;
        // タイルは部屋の境をまたぐので、4 隅と真ん中で部屋の絵を引いて一番手前より前に置く
        // (真ん中だけだと、隣の部屋の絵のほうが手前の所で水がその下に隠れて四角く欠けた)
        float size = tc * cell, front = float.MaxValue;
        for (int k = 0; k < 5; k++)
        {
            float fx = k == 4 ? 0.5f : (k & 1), fy = k == 4 ? 0.5f : (k >> 1);
            front = Math.Min(front, DamageMap.FrontZ(new Vector2(wx + size * (0.05f + 0.9f * fx), wy + size * (0.05f + 0.9f * fy))));
        }
        float zs = DamageMap.ZScale(front);
        // 床の上・足跡 (−0.001) と波紋より奥
        t.Go.transform.position = FxMath.V3(wx, wy, front - 0.0004f * zs);
        t.Furn = FurnitureMask(wx, wy, tc * cell);
        t.FurnVersion = _furnVersion;
    }

    // 家具 (部屋の絵から持ち上げた物) が a から b へ動いて止まった: 型抜きを作り直す (どのタイルも次に描く時に)。
    // すぐ描き直すのは、元の場所か止まった所から r 以内に掛かるタイルだけ
    internal static void FurnitureMoved(Vector2 a, Vector2 b, float r)
    {
        _furnVersion++;
        float size = WaterSim.TileCells * WaterSim.Cell;
        var org = WaterSim.Origin;
        foreach (var kv in Tiles)
        {
            var t = kv.Value;
            if (t.Empty || t.Waiting) continue;
            float x0 = org.x + t.Tx * size, y0 = org.y + t.Ty * size;
            if (!Near(a) && !Near(b)) continue;
            t.Waiting = true;
            Waiting.Add(kv.Key);

            bool Near(Vector2 c) => c.x + r > x0 && c.x - r < x0 + size && c.y + r > y0 && c.y - r < y0 + size;
        }
    }

    // 部屋の絵に描き込まれた家具 (机・ベッド) の範囲。どちらも絵は当たり判定の中に収まっているので、当たり判定の内側だけ隠す
    private static bool[] FurnitureMask(float wx, float wy, float size)
    {
        var cols = new List<Collider2D>();
        DamageMap.FurnitureColliders(new Vector2(wx + size * 0.5f, wy + size * 0.5f), size * 0.75f, cols);
        if (cols.Count == 0) return null;
        int m = WaterSim.TileCells * FurnSub;
        var mask = new bool[m * m];
        float step = size / m;
        foreach (var col in cols)
        {
            var b = col.bounds;
            float bx0 = b.min.x, bx1 = b.max.x, by0 = b.min.y, by1 = b.max.y;
            for (int y = 0; y < m; y++)
            {
                float qy = wy + (y + 0.5f) * step;
                if (qy < by0 || qy > by1) continue;
                for (int x = 0; x < m; x++)
                {
                    float qx = wx + (x + 0.5f) * step;
                    if (qx < bx0 || qx > bx1 || mask[y * m + x]) continue;
                    if (Inside(col, qx, qy))
                        mask[y * m + x] = true;
                }
            }
        }
        return mask;
    }

    // 当たり判定の内側か (縁を FurnitureInset だけ削る: 当たり判定は絵より少し大きく、そのまま隠すと家具の際に乾いた線が残った)
    private const float FurnitureInset = 0.025f;
    internal static bool Inside(Collider2D col, float x, float y) =>
        col.OverlapPoint(new Vector2(x, y))
        && col.OverlapPoint(new Vector2(x + FurnitureInset, y)) && col.OverlapPoint(new Vector2(x - FurnitureInset, y))
        && col.OverlapPoint(new Vector2(x, y + FurnitureInset)) && col.OverlapPoint(new Vector2(x, y - FurnitureInset));

    internal static void Clear()
    {
        foreach (var t in Tiles.Values)
        {
            if (t.Go) UnityEngine.Object.Destroy(t.Go);
            if (t.Sp) UnityEngine.Object.Destroy(t.Sp);
            if (t.Tex) UnityEngine.Object.Destroy(t.Tex);
        }
        Tiles.Clear();
        Waiting.Clear();
        _clock = 0f;
    }

    internal static string Describe() => $"tiles={Tiles.Count} waiting={Waiting.Count} drawn={Drawn} drawMs={LastDrawMs:0.00}";
}
