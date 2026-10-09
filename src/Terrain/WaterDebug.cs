using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 確認用: 水の判定を自分の周りに色で重ねる (bridge `water show [r]`)。
// 赤 = 水の升が閉じている (水が流れない)・橙 = SolidMap で歩けない (水を見せない)・黄 = 家具で水を見せない
internal static class WaterDebug
{
    private const float Ppu = 16f;
    private static GameObject _go;
    private static Texture2D _tex;
    private static Sprite _sp;

    internal static unsafe string Show(string arg)
    {
        Hide();
        var lp = PlayerControl.LocalPlayer;
        if (!lp || !WaterSim.Ready) return "ERR water show: no player or water grid (break a water wall first)";
        float r = float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) && v > 0.5f ? v : 6f;
        Vector2 c = lp.GetTruePosition();
        int n = (int)(r * 2f * Ppu);
        var px = new byte[n * n * 4];
        var cols = new List<Collider2D>();
        DamageMap.FurnitureColliders(c, r, cols);
        var worg = WaterSim.Origin;
        float cell = WaterSim.Cell;
        int closed = 0, solid = 0, furn = 0;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float wx = c.x - r + (x + 0.5f) / Ppu, wy = c.y - r + (y + 0.5f) / Ppu;
            int cx = (int)MathF.Floor((wx - worg.x) / cell), cy = (int)MathF.Floor((wy - worg.y) / cell);
            bool shut = cx < 0 || cy < 0 || cx >= WaterSim.W || cy >= WaterSim.H || !WaterSim.Open(cy * WaterSim.W + cx);
            bool so = SolidMap.Solid(new Vector2(wx, wy));
            bool fu = false;
            foreach (var col in cols)
            {
                var b = col.bounds;
                if (wx < b.min.x || wx > b.max.x || wy < b.min.y || wy > b.max.y) continue;
                if (WaterArt.Inside(col, wx, wy)) { fu = true; break; }
            }
            int i = (y * n + x) * 4;
            if (fu) { px[i] = 255; px[i + 1] = 230; px[i + 2] = 0; px[i + 3] = 110; furn++; }
            else if (so) { px[i] = 255; px[i + 1] = 120; px[i + 2] = 0; px[i + 3] = 100; solid++; }
            if (shut) { px[i] = 255; px[i + 1] = 0; px[i + 2] = 0; px[i + 3] = (byte)Math.Max((int)px[i + 3], 80); closed++; }
        }
        _tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "MrpWaterDebug", filterMode = FilterMode.Point };
        fixed (byte* b = px) _tex.LoadRawTextureData((IntPtr)b, px.Length);
        _tex.Apply(false, false);
        _sp = Sprite.Create(_tex, new Rect(0, 0, n, n), Vector2.zero, Ppu, 0, SpriteMeshType.FullRect);
        _go = new GameObject("MrpWaterDebug") { layer = 0 };
        _go.AddComponent<SpriteRenderer>().sprite = _sp;
        _go.transform.position = FxMath.V3(c.x - r, c.y - r, -20f);
        return $"OK water show r={r} closed={closed} solid={solid} furniture={furn} colliders={cols.Count}";
    }

    // 家具の当たり判定 (水を見せない所の元) を名前・形・大きさで並べる
    internal static void ListFurniture(string arg, Action<string> reply)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp) { reply("ERR no player"); return; }
        float r = float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) && v > 0.5f ? v : 8f;
        var cols = new List<Collider2D>();
        DamageMap.FurnitureColliders(lp.GetTruePosition(), r, cols);
        foreach (var col in cols)
        {
            var b = col.bounds;
            var t = col.transform;
            string path = t.parent ? t.parent.name + "/" + t.name : t.name;
            string kind = col.TryCast<PolygonCollider2D>() != null ? "poly" : col.TryCast<BoxCollider2D>() != null ? "box" : col.TryCast<CircleCollider2D>() != null ? "circle" : col.TryCast<EdgeCollider2D>() != null ? "edge" : "other";
            reply($"FURN {path} {kind} min=({b.min.x:0.00},{b.min.y:0.00}) size=({b.size.x:0.00},{b.size.y:0.00})");
        }
        reply($"OK water furn n={cols.Count} r={r}");
    }

    internal static void Hide()
    {
        if (_go) UnityEngine.Object.Destroy(_go);
        if (_sp) UnityEngine.Object.Destroy(_sp);
        if (_tex) UnityEngine.Object.Destroy(_tex);
        _go = null; _sp = null; _tex = null;
    }
}
