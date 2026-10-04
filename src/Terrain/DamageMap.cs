using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// マップ全体を覆う損傷マスク (1 単位 = PixelsPerUnit 画素の RGBA32)。
//   R = 穴 (0..1・0.5 が縁) / G = 焦げ
// シェーダ MRP/TerrainSprite がこれを世界座標で引いて、部屋の絵を抜き・焦がす。
// 何も壊れていない間は作らない (部屋の絵もバニラのマテリアルのまま)。最初の損傷で作り、
// その時に部屋の絵のマテリアルを差し替える。
internal static class DamageMap
{
    private const int PixelsPerUnit = 16;
    private const float HoleEdge = 0.25f;   // 穴の縁のなだらかさ (世界単位)。ノイズで崩す幅の土台
    private const float ScorchWidth = 0.7f; // 穴の外側へ焦げが伸びる幅 (半径に対する比)
    private const float UnderlayDepth = 0.3f; // 部屋の絵より奥に置く瓦礫の床の z のずらし

    private static ShipStatus _ship;
    private static Texture2D _tex;
    private static byte[] _pixels;
    private static int _w, _h;
    private static Vector2 _origin;
    private static float _roomZ;
    private static readonly List<GameObject> Underlays = new();
    private static Sprite _underlaySprite;
    private static Material _underlayMat;

    private static readonly int DamageTexId = Shader.PropertyToID("_MrpDamageTex");
    private static readonly int DamageRectId = Shader.PropertyToID("_MrpDamageRect");

    public static string Hole(Vector2 center, float radius)
    {
        if (!MrpBundle.Ready) return "bundle not ready";
        if (!EnsureMap()) return "no ship";

        Stamp(center, radius);
        Upload();
        SpawnUnderlay(center, radius);
        return null;
    }

    private static bool EnsureMap()
    {
        var ship = ShipStatus.Instance;
        if (!ship) return false;
        if (_ship == ship && _tex) return true;

        Reset();
        _ship = ship;

        // 部屋の絵 (バニラの Unlit/MaskShader) の範囲を合わせて、マスクの置き場所を決める
        var rooms = new List<SpriteRenderer>();
        Bounds all = default;
        bool any = false;
        foreach (var sr in ship.GetComponentsInChildren<SpriteRenderer>(true))
        {
            var m = sr.sharedMaterial;
            if (!m || !m.shader || m.shader.name != "Unlit/MaskShader") continue;
            rooms.Add(sr);
            if (!any) { all = sr.bounds; any = true; } else all.Encapsulate(sr.bounds);
        }
        if (!any) return false;

        all.Expand(4f);
        _origin = all.min;
        _w = Mathf.CeilToInt(all.size.x * PixelsPerUnit);
        _h = Mathf.CeilToInt(all.size.y * PixelsPerUnit);
        _pixels = new byte[_w * _h * 4];
        _tex = new Texture2D(_w, _h, TextureFormat.RGBA32, false, true)
        {
            name = "MrpDamage",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        Upload();

        Shader.SetGlobalTexture(DamageTexId, _tex);
        Shader.SetGlobalVector(DamageRectId, new Vector4(_origin.x, _origin.y, 1f / (_w / (float)PixelsPerUnit), 1f / (_h / (float)PixelsPerUnit)));

        // 部屋の絵を、同じ描き方 + 損傷マスクのマテリアルへ (元のステンシル設定を引き継ぐ)
        var src = rooms[0].sharedMaterial;
        var mat = new Material(MrpBundle.TerrainMaterial) { name = "MrpTerrain" };
        mat.SetFloat("_MaskLayer", src.GetFloat("_MaskLayer"));
        mat.SetFloat("_MaskComp", src.GetFloat("_MaskComp"));
        mat.renderQueue = src.renderQueue;
        foreach (var sr in rooms) sr.sharedMaterial = mat;

        // 瓦礫の床も同じ描き方 (影が落ちるようにステンシルを書く) で、損傷マスクだけ見ない
        _underlayMat = new Material(mat) { name = "MrpRubble" };
        _underlayMat.SetFloat("_UseDamage", 0f);
        _roomZ = rooms[0].transform.position.z;

        Plugin.Logger.LogInfo($"damage map {_w}x{_h} ({_pixels.Length / 1024}KB) origin={_origin} rooms={rooms.Count}");
        return true;
    }

    private static void Stamp(Vector2 c, float r)
    {
        float reach = r * (1f + ScorchWidth) + HoleEdge;
        int x0 = Math.Max(0, (int)((c.x - reach - _origin.x) * PixelsPerUnit));
        int x1 = Math.Min(_w - 1, (int)((c.x + reach - _origin.x) * PixelsPerUnit) + 1);
        int y0 = Math.Max(0, (int)((c.y - reach - _origin.y) * PixelsPerUnit));
        int y1 = Math.Min(_h - 1, (int)((c.y + reach - _origin.y) * PixelsPerUnit) + 1);

        for (int py = y0; py <= y1; py++)
        {
            float wy = _origin.y + (py + 0.5f) / PixelsPerUnit - c.y;
            for (int px = x0; px <= x1; px++)
            {
                float wx = _origin.x + (px + 0.5f) / PixelsPerUnit - c.x;
                float d = MathF.Sqrt(wx * wx + wy * wy);

                float hole = Clamp01(0.5f + (r - d) / HoleEdge * 0.5f);
                float scorch = Clamp01(1f - (d - r) / (r * ScorchWidth));

                int i = (py * _w + px) * 4;
                byte hb = (byte)(hole * 255f), sb = (byte)(scorch * 255f);
                if (hb > _pixels[i]) _pixels[i] = hb;
                if (sb > _pixels[i + 1]) _pixels[i + 1] = sb;
            }
        }
    }

    private static unsafe void Upload()
    {
        fixed (byte* p = _pixels) _tex.LoadRawTextureData((IntPtr)p, _pixels.Length);
        _tex.Apply(false, false);
    }

    // 抜いた穴の向こうには床が無い (部屋と部屋の間は船体と宇宙) ので、部屋の絵より奥に瓦礫の床を敷く
    private static void SpawnUnderlay(Vector2 c, float r)
    {
        _underlaySprite ??= UnderlayArt.Make();
        var go = new GameObject("MrpRubble") { layer = 9 };
        go.transform.SetParent(_ship.transform, true);
        go.transform.position = new Vector3(c.x, c.y, _roomZ + UnderlayDepth);
        float size = r * 2f * 1.5f; // 縁のぎざぎざ (ノイズで最大 ~1.35 倍に広がる) を覆う
        go.transform.localScale = Vector3.one * (size / _underlaySprite.bounds.size.x) / _ship.transform.lossyScale.x;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _underlaySprite;
        sr.sharedMaterial = _underlayMat;
        Underlays.Add(go);
    }

    private static void Reset()
    {
        foreach (var go in Underlays) if (go) UnityEngine.Object.Destroy(go);
        Underlays.Clear();
        if (_tex) UnityEngine.Object.Destroy(_tex);
        _tex = null;
        _pixels = null;
        _ship = null;
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
