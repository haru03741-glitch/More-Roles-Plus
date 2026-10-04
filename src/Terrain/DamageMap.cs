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
    // 抜くのは「円」と「切り取った壁の線の近く (帯)」の重なりだけ (部屋の床まで抜くと床に穴が空いたように見える)
    private const float BandHalf = 0.2f;      // 壁の線から横・下へ届く幅 (世界単位)
    private const float BandUpStretch = 2.2f; // 3/4 視点の壁は線より上に高さがあるので、上へはこの倍だけ届かせる

    private static ShipStatus _ship;
    private static Texture2D _tex;
    private static byte[] _pixels;
    private static int _w, _h;
    private static Vector2 _origin;
    private static readonly List<SpriteRenderer> Rooms = new();
    private static readonly List<GameObject> Underlays = new();
    private static Sprite _underlaySprite;
    private static Sprite _crackSprite;
    private static int _holeCount;
    private static Material _underlayMat;
    private static Material _roomMat;

    private static readonly int DamageTexId = Shader.PropertyToID("_MrpDamageTex");
    private static readonly int DamageRectId = Shader.PropertyToID("_MrpDamageRect");

    public static string Hole(Vector2 center, float radius, List<Vector2> removedSegments)
    {
        if (!MrpBundle.Ready) return "bundle not ready";
        if (!EnsureMap()) return "no ship";

        Stamp(center, radius, removedSegments);
        Upload();
        SpawnUnderlay(center, radius, removedSegments);
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
            Rooms.Add(sr);
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
        var mat = _roomMat = new Material(MrpBundle.TerrainMaterial) { name = "MrpTerrain" };
        mat.SetFloat("_MaskLayer", src.GetFloat("_MaskLayer"));
        mat.SetFloat("_MaskComp", src.GetFloat("_MaskComp"));
        mat.renderQueue = src.renderQueue;
        foreach (var sr in rooms) sr.sharedMaterial = mat;

        // 瓦礫の床も同じ描き方 (影が落ちるようにステンシルを書く) で、損傷マスクを逆向きに使う
        _underlayMat = new Material(mat) { name = "MrpRubble" };
        _underlayMat.SetFloat("_UseDamage", 2f); // 2 = 抜けた所にだけ描く

        Plugin.Logger.LogInfo($"damage map {_w}x{_h} ({_pixels.Length / 1024}KB) origin={_origin} rooms={rooms.Count}");
        return true;
    }

    private static void Stamp(Vector2 c, float r, List<Vector2> segs)
    {
        bool banded = segs != null && segs.Count >= 2;
        var hull = banded ? ConvexHull(segs) : null;
        float reach = r * (1f + ScorchWidth) + HoleEdge;
        int x0 = Math.Max(0, (int)((c.x - reach - _origin.x) * PixelsPerUnit));
        int x1 = Math.Min(_w - 1, (int)((c.x + reach - _origin.x) * PixelsPerUnit) + 1);
        int y0 = Math.Max(0, (int)((c.y - reach - _origin.y) * PixelsPerUnit));
        int y1 = Math.Min(_h - 1, (int)((c.y + reach - _origin.y) * PixelsPerUnit) + 1);

        for (int py = y0; py <= y1; py++)
        {
            float wy = _origin.y + (py + 0.5f) / PixelsPerUnit;
            for (int px = x0; px <= x1; px++)
            {
                float wx = _origin.x + (px + 0.5f) / PixelsPerUnit;
                float dx = wx - c.x, dy = wy - c.y;
                float d = MathF.Sqrt(dx * dx + dy * dy);

                float hole = Clamp01(0.5f + (r - d) / HoleEdge * 0.5f);
                float scorch = Clamp01(1f - (d - r) / (r * ScorchWidth));

                if (banded)
                {
                    float bd = BandDistance(hull, wx, wy);
                    hole = Math.Min(hole, Clamp01(0.5f + (BandHalf - bd) / HoleEdge * 0.5f));
                    scorch = Math.Min(scorch, Clamp01(1f - (bd - BandHalf) / (r * ScorchWidth)));
                }

                int i = (py * _w + px) * 4;
                byte hb = (byte)(hole * 255f), sb = (byte)(scorch * 255f);
                if (hb > _pixels[i]) _pixels[i] = hb;
                if (sb > _pixels[i + 1]) _pixels[i + 1] = sb;
            }
        }
    }

    // 切り取った壁の端点を囲む凸多角形までの距離 (内側は 0)。2 枚の壁の間の隙間も通路として含める。
    // 多角形より上は BandUpStretch 倍だけ近いとみなす (3/4 視点の壁の高さの分)
    private static float BandDistance(List<Vector2> hull, float x, float y)
    {
        if (hull.Count >= 3 && InsideConvex(hull, x, y)) return 0f;
        float best = float.MaxValue;
        int n = hull.Count;
        for (int k = 0; k < n; k++)
        {
            Vector2 a = hull[k], b = hull[(k + 1) % n];
            float sx = b.x - a.x, sy = b.y - a.y, l2 = sx * sx + sy * sy;
            float t = l2 > 0 ? Math.Clamp(((x - a.x) * sx + (y - a.y) * sy) / l2, 0f, 1f) : 0f;
            float ex = x - (a.x + sx * t), ey = y - (a.y + sy * t);
            if (ey > 0) ey /= BandUpStretch;
            float d = ex * ex + ey * ey;
            if (d < best) best = d;
            if (n == 2) break;
        }
        return MathF.Sqrt(best);
    }

    private static bool InsideConvex(List<Vector2> hull, float x, float y)
    {
        // 反時計回りの凸包: 全ての辺の左側なら内側
        for (int k = 0; k < hull.Count; k++)
        {
            Vector2 a = hull[k], b = hull[(k + 1) % hull.Count];
            if ((b.x - a.x) * (y - a.y) - (b.y - a.y) * (x - a.x) < 0) return false;
        }
        return true;
    }

    // 点群の凸包 (Andrew の単調連鎖・反時計回り)
    private static List<Vector2> ConvexHull(List<Vector2> pts)
    {
        var p = new List<Vector2>(pts);
        p.Sort((u, v) => u.x != v.x ? u.x.CompareTo(v.x) : u.y.CompareTo(v.y));
        if (p.Count < 3) return p;
        var h = new List<Vector2>();
        for (int pass = 0; pass < 2; pass++)
        {
            int start = h.Count;
            for (int i = 0; i < p.Count; i++)
            {
                var q = pass == 0 ? p[i] : p[p.Count - 1 - i];
                while (h.Count >= start + 2 && Cross(h[h.Count - 2], h[h.Count - 1], q) <= 0) h.RemoveAt(h.Count - 1);
                h.Add(q);
            }
            h.RemoveAt(h.Count - 1);
        }
        return h;
    }

    private static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

    // その点に重なる部屋の絵の z の範囲 (部屋ごとに z が違うので、手前・奥はその場で決める)
    private static void RoomZRange(Vector2 c, out float near, out float far)
    {
        near = float.MaxValue; far = float.MinValue;
        foreach (var sr in Rooms)
        {
            if (!sr) continue;
            var b = sr.bounds;
            if (c.x < b.min.x || c.x > b.max.x || c.y < b.min.y || c.y > b.max.y) continue;
            float z = sr.transform.position.z;
            if (z < near) near = z;
            if (z > far) far = z;
        }
        if (near > far) { near = far = Rooms.Count > 0 && Rooms[0] ? Rooms[0].transform.position.z : 8f; }
    }

    private static unsafe void Upload()
    {
        fixed (byte* p = _pixels) _tex.LoadRawTextureData((IntPtr)p, _pixels.Length);
        _tex.Apply(false, false);
    }

    // 抜いた穴の向こうには床が無い (部屋と部屋の間は船体と宇宙) ので、部屋の絵より奥に瓦礫の床を敷く
    private static void SpawnUnderlay(Vector2 c, float r, List<Vector2> segs)
    {
        RoomZRange(c, out float nearZ, out float farZ);
        if (!SpawnPassageFloor(c, r, segs, farZ + UnderlayDepth))
        {
            // 廊下の床の絵が見つからないマップでは手続きの床で代用する
            _underlaySprite ??= UnderlayArt.MakeFloor();
            var go = new GameObject("MrpRubble") { layer = 9 };
            go.transform.SetParent(_ship.transform, true);
            go.transform.position = new Vector3(c.x, c.y, farZ + UnderlayDepth);
            float size = r * 2f * 1.5f;
            go.transform.localScale = Vector3.one * (size / _underlaySprite.bounds.size.x) / _ship.transform.lossyScale.x;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _underlaySprite;
            sr.sharedMaterial = _underlayMat;
            Underlays.Add(go);
        }

        // ひびは部屋の絵のすぐ手前に、部屋と同じマテリアルで貼る (穴の中は損傷マスクで自動的に抜ける・影も効く)
        _crackSprite ??= UnderlayArt.MakeCracks();
        var crack = new GameObject("MrpCracks") { layer = 9 };
        crack.transform.SetParent(_ship.transform, true);
        crack.transform.position = new Vector3(c.x, c.y, nearZ - 0.002f);
        crack.transform.rotation = Quaternion.Euler(0f, 0f, (_holeCount++ * 137.5f) % 360f);
        float crackSize = r * 2f * UnderlayArt.CrackReach;
        crack.transform.localScale = Vector3.one * (crackSize / _crackSprite.bounds.size.x) / _ship.transform.lossyScale.x;
        var csr = crack.AddComponent<SpriteRenderer>();
        csr.sprite = _crackSprite;
        csr.sharedMaterial = _roomMat;
        Underlays.Add(crack);
    }

    // 穴の中 (部屋と部屋の隙間) には、本編が部屋どうしをつなぐのに使っている廊下の床を敷く。
    // 床の絵は実行時に本編の廊下の絵から名前で切り出す (繰り返しの 1 周期分)。
    private static readonly (string sprite, int x, int y, int w, int h)[] PassageFloorSources =
    {
        ("room_hallwaycros", 150, 168, 72, 55), // Skeld: 交差廊下の床板 (リベットの線が 55px 周期)
    };

    private static Sprite _passageTile;
    private static bool _passageSearched;

    private static bool SpawnPassageFloor(Vector2 c, float r, List<Vector2> segs, float z)
    {
        if (!_passageSearched)
        {
            _passageSearched = true;
            foreach (var sr in _ship.GetComponentsInChildren<SpriteRenderer>(true))
            {
                var sp = sr.sprite;
                if (!sp) continue;
                foreach (var src in PassageFloorSources)
                {
                    if (sp.name != src.sprite) continue;
                    var tr = sp.textureRect;
                    var rect = new Rect(tr.x + src.x, tr.y + src.y, src.w, src.h);
                    _passageTile = Sprite.Create(sp.texture, rect, new Vector2(0.5f, 0.5f), sp.pixelsPerUnit, 0, SpriteMeshType.FullRect);
                    _passageTile.name = "MrpPassageFloor";
                    _passageTile.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    _passageScale = sr.transform.lossyScale.x;
                    break;
                }
                if (_passageTile) break;
            }
        }
        if (!_passageTile) return false;

        // 通路の向き = 切り取った壁に垂直。床板の継ぎ目が歩く向きに直交するよう回す (廊下と同じ)
        Vector2 wallDir = Vector2.right;
        if (segs != null && segs.Count >= 2)
        {
            Vector2 sum = Vector2.zero;
            for (int k = 0; k + 1 < segs.Count; k += 2)
            {
                Vector2 d = segs[k + 1] - segs[k];
                if (d.y < 0 || (d.y == 0 && d.x < 0)) d = -d; // 向きを揃えて足す
                sum += d;
            }
            if (sum.sqrMagnitude > 1e-6f) wallDir = sum.normalized;
        }
        // 床板の継ぎ目 (絵の横方向) を壁と平行に
        float angle = Mathf.Atan2(wallDir.y, wallDir.x) * Mathf.Rad2Deg;
        var rot = Quaternion.Euler(0f, 0f, angle);
        Vector2 ax = wallDir, ay = new Vector2(-wallDir.y, wallDir.x);

        float tw = _passageTile.bounds.size.x * _passageScale, th = _passageTile.bounds.size.y * _passageScale;
        float reach = r * 1.4f;
        // 継ぎ目が隣の穴とも揃うよう、壁に沿った座標系の格子に置く
        float u0 = Vector2.Dot(c, ax), v0 = Vector2.Dot(c, ay);
        int iu0 = Mathf.FloorToInt((u0 - reach) / tw), iu1 = Mathf.CeilToInt((u0 + reach) / tw);
        int iv0 = Mathf.FloorToInt((v0 - reach) / th), iv1 = Mathf.CeilToInt((v0 + reach) / th);
        var parentScale = _ship.transform.lossyScale.x;
        for (int iu = iu0; iu <= iu1; iu++)
        for (int iv = iv0; iv <= iv1; iv++)
        {
            Vector2 p = ax * ((iu + 0.5f) * tw) + ay * ((iv + 0.5f) * th);
            if ((p - c).sqrMagnitude > (reach + tw) * (reach + tw)) continue;
            var go = new GameObject("MrpPassageFloor") { layer = 9 };
            go.transform.SetParent(_ship.transform, true);
            go.transform.position = new Vector3(p.x, p.y, z);
            go.transform.rotation = rot;
            go.transform.localScale = Vector3.one * (_passageScale / parentScale);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _passageTile;
            sr.sharedMaterial = _underlayMat;
            Underlays.Add(go);
        }
        return true;
    }

    private static float _passageScale = 1f;

    private static void Reset()
    {
        foreach (var go in Underlays) if (go) UnityEngine.Object.Destroy(go);
        Underlays.Clear();
        Rooms.Clear();
        _passageTile = null;
        _passageSearched = false;
        if (_tex) UnityEngine.Object.Destroy(_tex);
        _tex = null;
        _pixels = null;
        _ship = null;
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
