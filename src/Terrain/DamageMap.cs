using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// マップ全体を覆う損傷マスク (1 単位 = PixelsPerUnit 画素の RGBA32)。
//   R = 穴 (0..1・0.5 が縁) / G = 焦げ / B = 熾火 (割れ口が赤く照る強さ。爆発だけが書く) / A = 家具 (ひびを載せない)
// シェーダ MRP/TerrainSprite がこれを世界座標で引いて、部屋の絵を抜き・焦がす。
// 何も壊れていない間は作らない (部屋の絵もバニラのマテリアルのまま)。最初の損傷で作り、
// その時に部屋の絵のマテリアルを差し替える。
internal static class DamageMap
{
    private const int PixelsPerUnit = 16;
    private const float HoleEdge = 0.25f;   // 穴の縁のなだらかさ (世界単位)。ノイズで崩す幅の土台
    private const float ScorchWidth = 0.45f;
    private const float EmberCold = 0.45f;  // 打撃の切り口の B の上限 (シェーダは 0.55 以上を熱い切り口として橙を乗せる) // 穴の外側へ焦げが伸びる幅 (半径に対する比)
    private const float UnderlayDepth = 0.3f; // 部屋の絵より奥に置く瓦礫の床の z のずらし
    // 抜くのは「円」と「切り取った壁の線の近く (帯)」の重なりだけ (部屋の床まで抜くと床に穴が空いたように見える)
    private const float BandHalf = 0.2f;      // 壁の線から横・下へ届く幅 (世界単位)
    private const float LineBandEdge = 0.08f; // その帯の縁のなだらかさ
    private const float LineBandHalf = 0.05f; // 壁の中の判定がある時の帯: 歩ける側へはほぼ出さない (壁の中身は Exposed で抜く。割れ口のギザギザで少しは食い込む)
    private const float BandUpStretch = 2.2f; // 3/4 視点の壁は線より上に高さがあるので、上へはこの倍だけ届かせる
    private const float BandDownSquash = 2f;  // 壁の線より下 (手前の床) へはこの分の 1 しか届かせない

    private static ShipStatus _ship;
    private static Texture2D _tex;
    private static byte[] _pixels;
    // 画素ごとに「最後にそこを抜いた破壊の番号」(1..255・0 = 抜けていない)。割れた塊が自分の分だけ描くのに使う
    private static Texture2D _genTex;
    private static byte[] _gens;
    private static byte _gen;
    private static int _w, _h;
    private static Vector2 _origin;
    private static readonly List<SpriteRenderer> Rooms = new();
    private static readonly List<GameObject> Underlays = new();
    private static Sprite _crackSprite;
    private static Sprite _impactSprite;
    private static int _holeCount;
    private static Material _underlayMat;
    private static Material _roomMat;
    private static Material _roomMatMask, _roomMatDefault;
    private static readonly HashSet<int> Swapped = new();

    private static readonly int DamageTexId = Shader.PropertyToID("_MrpDamageTex");
    private static readonly int DamageRectId = Shader.PropertyToID("_MrpDamageRect");
    private static readonly int GenTexId = Shader.PropertyToID("_MrpGenTex");

    public static string Hole(Vector2 center, float radius, List<Vector2> removedSegments)
        => Breach(new CircleShape(center, radius), removedSegments, true);

    // 形の範囲で壁を抜いた見た目を付ける (抜く・焦がす・向こうの床・ひび)
    // floorY: この高さより下は見た目では抜かない (壁の当たり判定が見た目の根元より下 = 床まで伸びているマップがあるため)
    // keep: 家具の保護範囲 (FurnitureFor で求め、壁の当たり判定の切り取りにも同じものを渡す)
    // body: 穴から露出した壁の中 (蓋と同じ判定)。帯 (切った面の近く) の外でも、ここは抜く
    // pieces: 渡すと、抜いた所の壁の絵を割った塊を作って入れる (動かすのは TerrainFx)。割れ目は cracks (古い順・最後がこの破壊)
    public static string Breach(CutShape shape, List<Vector2> removedSegments, bool scorch, float floorY = float.NegativeInfinity,
        List<Rect> keep = null, WallBody body = null, List<BreakPiece> pieces = null, List<CrackPattern> cracks = null)
    {
        if (!MrpBundle.Ready) return "bundle not ready";
        if (!EnsureMap()) return "no ship";

        SwapNear(shape.Center, shape.BoundRadius * UnderlayArt.CrackReach + 1f);
        if (_gen == 255) GenWrapped = true;
        _gen = (byte)(_gen == 255 ? 1 : _gen + 1);
        // 番号が一周した後は、前の周で同じ番号を書いた画素を空ける (古い穴が新しい塊に入らないように)
        if (GenWrapped)
            for (int k = 0; k < _gens.Length; k++)
                if (_gens[k] == _gen) _gens[k] = 0;
        Stamp(shape, removedSegments, scorch, floorY, keep ?? FurnitureFor(shape), body, out RectInt touched);
        SpawnUnderlay(shape.Center, shape.BoundRadius, removedSegments); // ひびの家具よけを A に書くので Upload より前
        Upload();
        if (pieces != null && cracks is { Count: > 0 }) BreakPieces.Spawn(touched, pieces, cracks);
        return null;
    }

    // 抜けない損傷 (耐久が残った打撃・爆発の外側の輪): ひびだけを貼る
    public static string Cracks(Vector2 at, float reach, float angleDeg)
    {
        if (!MrpBundle.Ready) return "bundle not ready";
        if (!EnsureMap()) return "no ship";
        SwapNear(at, reach + 0.5f);
        SpawnCracks(at, reach, angleDeg, true);
        Upload();
        return null;
    }

    // 部屋の絵と損傷マスクの準備ができているか (無ければ作る)
    internal static bool Ready() => MrpBundle.Ready && EnsureMap();

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
            if (!IsRoomArt(sr)) continue;
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
        _gens = new byte[_w * _h];
        _genTex = new Texture2D(_w, _h, TextureFormat.R8, false, true)
        {
            name = "MrpDamageGen",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point,
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        Upload();

        Shader.SetGlobalTexture(DamageTexId, _tex);
        Shader.SetGlobalTexture(GenTexId, _genTex);
        Shader.SetGlobalVector(DamageRectId, new Vector4(_origin.x, _origin.y, 1f / (_w / (float)PixelsPerUnit), 1f / (_h / (float)PixelsPerUnit)));

        // 部屋の絵用のマテリアル (差し替えは壊れた場所の近くだけ、その時に行う)。
        // MaskShader の部屋は元のステンシル設定を引き継ぐ。Sprites/Default の部屋はステンシルを書かない
        // (影の板は Skeld 型 = ステンシル 1 の所に影 / Polus・Fungle 型 = 1 以外の所に影。どちらでも元の見え方を変えない)
        SpriteRenderer maskSrc = null;
        foreach (var sr in rooms) if (sr.sharedMaterial.shader.name == "Unlit/MaskShader") { maskSrc = sr; break; }
        if (maskSrc)
        {
            var src = maskSrc.sharedMaterial;
            _roomMatMask = new Material(MrpBundle.TerrainMaterial) { name = "MrpTerrain" };
            _roomMatMask.SetFloat("_MaskLayer", src.GetFloat("_MaskLayer"));
            _roomMatMask.SetFloat("_MaskComp", src.GetFloat("_MaskComp"));
            _roomMatMask.renderQueue = src.renderQueue;
        }
        _roomMatDefault = new Material(MrpBundle.TerrainMaterial) { name = "MrpTerrainDefault" };
        _roomMatDefault.SetFloat("_MaskComp", 8f);   // Always
        _roomMatDefault.SetFloat("_StencilPass", 0f); // Keep
        _roomMatDefault.renderQueue = 3000;

        // 瓦礫・ひび・穴の向こうの床: 影の板が「ステンシル 1 の所に影」(Skeld 型) なら部屋と同じく 1 を書く、そうでなければ書かない
        var mat = _roomMat = ShadowNeedsStencil() && _roomMatMask ? _roomMatMask : _roomMatDefault;

        // 瓦礫の床も同じ描き方 (影が落ちるようにステンシルを書く) で、損傷マスクを逆向きに使う
        _underlayMat = new Material(mat) { name = "MrpRubble" };
        _underlayMat.SetFloat("_UseDamage", 2f); // 2 = 抜けた所にだけ描く
        _propMat = new Material(mat) { name = "MrpDebris" };
        _propMat.SetFloat("_UseDamage", 0f);
        _decalMat = new Material(mat) { name = "MrpCrackDecal" };
        _decalMat.SetFloat("_UseDamage", 3f); // 3 = ひび: 穴の中と家具の上には描かない

        Plugin.Logger.LogInfo($"damage map {_w}x{_h} ({_pixels.Length / 1024}KB) origin={_origin} rooms={rooms.Count}");
        return true;
    }

    // touched = この破壊が抜いた画素 (番号を書いた所) の範囲
    private static void Stamp(CutShape shape, List<Vector2> segs, bool withScorch, float floorY, List<Rect> keep, WallBody body, out RectInt touched)
    {
        int tx0 = int.MaxValue, ty0 = int.MaxValue, tx1 = -1, ty1 = -1;
        Vector2 c = shape.Center;
        float r = shape.BoundRadius;
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
                float sd = shape.SignedDistance(wx, wy);

                float hole = Clamp01(0.5f - sd / HoleEdge * 0.5f);
                float scorch = withScorch ? Clamp01(1f - sd / (r * ScorchWidth)) : 0f;
                // 切り口 (残った壁の端) の印 B: 形の外側の縁だけに書く。形の内側で帯に止められた縁 (歩ける床との境) には付けない
                // (付けると床の穴の形を一周する線になり、貼ったように見える)。爆発 = 熱い切り口 (シェーダが橙を少し乗せる)・打撃 = 断面だけ
                float ember = sd > -0.03f ? Clamp01(1f - sd / (HoleEdge * 2f)) * (withScorch ? 1f : EmberCold) : 0f;

                if (banded)
                {
                    // 壁の中の判定がある時は、帯を切った区間それぞれの近くに限る (凸包だと、離れた 2 枚の壁を切った時に
                    // 間の歩ける床まで抜けてしまう)。壁の中身はその下の Exposed で抜く
                    float bd = body != null ? SegmentBandDistance(segs, wx, wy) : BandDistance(hull, wx, wy);
                    // 壁の中の判定がある時は、歩ける側へ値がなだらかに残らないよう縁を急にする (割れ口のギザギザ ±0.175 で床へ食い込むため)
                    float band = body != null
                        ? Clamp01(0.5f + (LineBandHalf - bd) / LineBandEdge * 0.5f)
                        : Clamp01(0.5f + (BandHalf - bd) / HoleEdge * 0.5f);
                    // 帯の外でも、穴から露出した壁の中 (蓋の内側) は抜く。抜かないと絵が残るのに歩ける袋になる
                    if (band < 1f && hole > 0f && body != null && body.Exposed(wx, wy)) band = 1f;
                    hole = Math.Min(hole, band);
                    scorch = Math.Min(scorch, Clamp01(1f - (bd - BandHalf) / (r * ScorchWidth)));
                }

                // 家具 (ベッド・机など) は抜かない
                // 家具の上には焦げも熾火も描かない (穴の内側の値のままだと家具の上だけ真っ黒な影のように見える)
                if ((hole > 0f || ember > 0f || scorch > FurnitureScorch) && InsideAny(keep, wx, wy))
                {
                    hole = 0f;
                    ember = 0f;
                    scorch = Math.Min(scorch, FurnitureScorch);
                }
                if (wy < floorY) hole = Math.Min(hole, Clamp01(0.5f - (floorY - wy) / HoleEdge * 0.5f));

                int i = (py * _w + px) * 4;
                byte hb = (byte)(hole * 255f), sb = (byte)(scorch * 255f);
                if (hb > _pixels[i])
                {
                    _pixels[i] = hb;
                    // 抜け方が深くなった画素はこの破壊のもの (前の破壊の塊からは消え、今回の塊に移る)
                    _gens[py * _w + px] = _gen;
                    if (px < tx0) tx0 = px;
                    if (px > tx1) tx1 = px;
                    if (py < ty0) ty0 = py;
                    if (py > ty1) ty1 = py;
                }
                if (sb > _pixels[i + 1]) _pixels[i + 1] = sb;
                byte eb = (byte)(ember * 255f);
                if (eb > _pixels[i + 2]) _pixels[i + 2] = eb;
            }
        }
        touched = tx1 < 0 ? default : new RectInt(tx0, ty0, tx1 - tx0 + 1, ty1 - ty0 + 1);
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
            else ey *= BandDownSquash;
            float d = ex * ex + ey * ey;
            if (d < best) best = d;
            if (n == 2) break;
        }
        return MathF.Sqrt(best);
    }

    // 切った区間 (線分の両端を 2 つずつ) のどれかまでの距離。上下は同じ扱い: 壁の絵がどちら側にあるかは面ごとに違う
    // (部屋の南の壁は部屋側の面が絵の上端) ので、線の上の輪郭線だけを消し、壁の中身は Exposed (壁の中の判定) で抜く
    private static float SegmentBandDistance(List<Vector2> segs, float x, float y)
    {
        float best = float.MaxValue;
        for (int k = 0; k + 1 < segs.Count; k += 2)
        {
            Vector2 a = segs[k], b = segs[k + 1];
            float sx = b.x - a.x, sy = b.y - a.y, l2 = sx * sx + sy * sy;
            float t = l2 > 0 ? Math.Clamp(((x - a.x) * sx + (y - a.y) * sy) / l2, 0f, 1f) : 0f;
            float ex = x - (a.x + sx * t), ey = y - (a.y + sy * t);
            float d = ex * ex + ey * ey;
            if (d < best) best = d;
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

    // 部屋の絵か: Ship 層の SpriteRenderer で、MaskShader (Skeld 型) か、大きな Sprites/Default (Polus・Fungle 型)。
    // 背景・海・波・色かぶせ・影などは除く
    private static readonly System.Text.RegularExpressions.Regex NotRoomArt = new(
        "background|overlay|water|wave|tint|shadow|light|square|starfield|hull",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsRoomArt(SpriteRenderer sr)
    {
        if (!sr || sr.gameObject.layer != 9 || !sr.sprite) return false;
        var m = sr.sharedMaterial;
        if (!m || !m.shader) return false;
        string sh = m.shader.name;
        if (sh == "Unlit/MaskShader") return true;
        if (sh != "Sprites/Default") return false;
        var size = sr.bounds.size;
        if (size.x * size.y < 4f || size.x > 40f || size.y > 40f) return false;
        for (var t = sr.transform; t && !t.GetComponent<ShipStatus>(); t = t.parent)
            if (NotRoomArt.IsMatch(t.name)) return false;
        return true;
    }

    private static bool HasGroundBehind(Vector2 c)
    {
        foreach (var sr in Rooms)
            if (sr && sr.sharedMaterial && sr.sharedMaterial.shader && sr.sharedMaterial.shader.name == "MRP/TerrainSprite" && sr.sharedMaterial == _roomMatDefault)
            {
                var b = sr.bounds;
                if (c.x >= b.min.x && c.x <= b.max.x && c.y >= b.min.y && c.y <= b.max.y) return true;
            }
        return false;
    }

    // 範囲に掛かる部屋の絵を、損傷マスクを見るマテリアルへ差し替える (元のシェーダに合わせた方へ)
    private static void SwapNear(Vector2 c, float reach)
    {
        foreach (var sr in Rooms)
        {
            if (!sr) continue;
            var b = sr.bounds;
            if (c.x + reach < b.min.x || c.x - reach > b.max.x || c.y + reach < b.min.y || c.y - reach > b.max.y) continue;
            if (!Swapped.Add(sr.GetInstanceID())) continue;
            bool mask = sr.sharedMaterial.shader.name == "Unlit/MaskShader";
            var target = mask && _roomMatMask ? _roomMatMask : _roomMatDefault;
            if (!mask) target.renderQueue = sr.sharedMaterial.renderQueue;
            sr.sharedMaterial = target;
        }
    }

    // 影の板が「ステンシル 1 の所にだけ影」(比較 = Equal) か
    private static bool ShadowNeedsStencil()
    {
        var go = GameObject.Find("Main Camera/ShadowQuad");
        var r = go ? go.GetComponent<Renderer>() : null;
        var m = r ? r.sharedMaterial : null;
        return m && m.HasProperty("_Mask") && Mathf.Approximately(m.GetFloat("_Mask"), 3f);
    }

    // 家具の当たり判定 (ShortObjects 層) の範囲。絵は 3/4 視点で当たり判定より上に伸びるので上へ広げる
    private const int FurnitureLayer = 12;
    private const float FurnitureMargin = 0.08f;
    private const float FurnitureUp = 0.55f;
    // 家具の上の焦げの上限。シェーダは焦げ × セルのばらつき (最大 1.25 倍) が 0.4 を超えた所を段で塗るので、
    // 0.32 未満なら家具には焦げが出ない (出すと家具の上だけ角張った暗い面になり影に見える)
    private const float FurnitureScorch = 0.3f;

    // テスト用: その点の壁の絵がどれだけ抜けているか (0..1)。地図が無ければ -1
    internal static float HoleAt(Vector2 p)
    {
        if (_pixels == null) return -1f;
        int px = (int)((p.x - _origin.x) * PixelsPerUnit), py = (int)((p.y - _origin.y) * PixelsPerUnit);
        if (px < 0 || py < 0 || px >= _w || py >= _h) return -1f;
        return _pixels[(py * _w + px) * 4] / 255f;
    }

    // テスト用: その点の損傷マスクの RGBA (0..255)
    internal static string MaskAt(Vector2 p)
    {
        if (_pixels == null) return "no map";
        int px = (int)((p.x - _origin.x) * PixelsPerUnit), py = (int)((p.y - _origin.y) * PixelsPerUnit);
        if (px < 0 || py < 0 || px >= _w || py >= _h) return "out";
        int i = (py * _w + px) * 4;
        return $"R={_pixels[i]} G={_pixels[i + 1]} B={_pixels[i + 2]} A={_pixels[i + 3]}";
    }

    // テスト用: 中心 c・半辺 r の範囲の損傷マスクを PPM (R=穴 G=焦げ B=熾火・上が北) に書く
    internal static string DumpMask(Vector2 c, float r, string path)
    {
        if (_pixels == null) return "no map";
        int x0 = Math.Max(0, (int)((c.x - r - _origin.x) * PixelsPerUnit)), x1 = Math.Min(_w - 1, (int)((c.x + r - _origin.x) * PixelsPerUnit));
        int y0 = Math.Max(0, (int)((c.y - r - _origin.y) * PixelsPerUnit)), y1 = Math.Min(_h - 1, (int)((c.y + r - _origin.y) * PixelsPerUnit));
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        using var f = System.IO.File.Create(path);
        var head = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        f.Write(head, 0, head.Length);
        var row = new byte[w * 3];
        for (int y = y1; y >= y0; y--)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * _w + x0 + x) * 4;
                row[x * 3] = _pixels[i]; row[x * 3 + 1] = _pixels[i + 1]; row[x * 3 + 2] = _pixels[i + 2];
            }
            f.Write(row, 0, row.Length);
        }
        return $"{w}x{h}";
    }

    // 形で壊す時に守る家具の範囲 (壁の絵を抜かない矩形)。壁の当たり判定もここは切らない
    internal static List<Rect> FurnitureFor(CutShape shape)
        => FurnitureNear(shape.Center, shape.BoundRadius * (1f + ScorchWidth) + HoleEdge);

    private static List<Rect> FurnitureNear(Vector2 c, float r)
    {
        var list = new List<Rect>();
        foreach (var col in Physics2D.OverlapCircleAll(c, r + FurnitureUp, 1 << FurnitureLayer))
        {
            if (!col || col.isTrigger) continue;
            // 自分の絵を持つ家具 (Polus の机など) は部屋の絵とは別に手前に描かれるので、部屋の絵を抜いても残る → 守らない。
            // 守るのは部屋の絵に描き込まれた家具 (Skeld のベッドなど) だけ。絵の高さが分からないので当たり判定から上へ広げる
            var own = col.GetComponent<SpriteRenderer>();
            if (own && own.enabled && own.sprite) continue;
            var b = col.bounds;
            list.Add(Rect.MinMaxRect(b.min.x - FurnitureMargin, b.min.y - FurnitureMargin,
                                     b.max.x + FurnitureMargin, b.max.y + FurnitureUp));
        }
        return list;
    }

    // 家具の範囲を損傷マスクの A に書く (ひびの板がそこを描かない)。呼んだ側で Upload する
    private static void MarkFurniture(Vector2 c, float reach)
    {
        foreach (var rc in FurnitureNear(c, reach))
        {
            int x0 = Math.Max(0, (int)((rc.xMin - _origin.x) * PixelsPerUnit));
            int x1 = Math.Min(_w - 1, (int)((rc.xMax - _origin.x) * PixelsPerUnit) + 1);
            int y0 = Math.Max(0, (int)((rc.yMin - _origin.y) * PixelsPerUnit));
            int y1 = Math.Min(_h - 1, (int)((rc.yMax - _origin.y) * PixelsPerUnit) + 1);
            for (int py = y0; py <= y1; py++)
            for (int px = x0; px <= x1; px++)
                _pixels[(py * _w + px) * 4 + 3] = 255;
        }
    }

    private static bool InsideAny(List<Rect> rects, float x, float y)
    {
        foreach (var rc in rects)
            if (x >= rc.xMin && x <= rc.xMax && y >= rc.yMin && y <= rc.yMax) return true;
        return false;
    }

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

    // テスト用の見た目の切り分け: マスクの 1 チャンネル (1 = 焦げ G / 2 = 熾火 B) を退避して 0 にする / 戻す。
    // 同じ場面のまま入れ替えて撮り比べるため。戻す時は退避した値と今の値の大きい方 (外している間の破壊も残す)
    private static readonly byte[][] HiddenChannel = new byte[4][];

    internal static string DebugChannel(int ch, bool on)
    {
        if (_pixels == null) return "no map";
        if (!on)
        {
            if (HiddenChannel[ch] != null) return "already off";
            var keep = new byte[_w * _h];
            for (int k = 0; k < keep.Length; k++) { keep[k] = _pixels[k * 4 + ch]; _pixels[k * 4 + ch] = 0; }
            HiddenChannel[ch] = keep;
        }
        else
        {
            var keep = HiddenChannel[ch];
            if (keep == null) return "already on";
            for (int k = 0; k < keep.Length; k++) _pixels[k * 4 + ch] = Math.Max(_pixels[k * 4 + ch], keep[k]);
            HiddenChannel[ch] = null;
        }
        Upload();
        return null;
    }

    // テスト用: 穴の向こうに敷いた床 (借りた廊下の床・手続きの床) を隠す / 戻す。ひびの板はそのまま
    internal static int DebugUnderlay(bool on)
    {
        int n = 0;
        foreach (var go in Underlays)
            if (go && go.name == "MrpHullInterior") { go.SetActive(on); n++; }
        return n;
    }

    private static unsafe void Upload()
    {
        fixed (byte* p = _pixels) _tex.LoadRawTextureData((IntPtr)p, _pixels.Length);
        _tex.Apply(false, false);
        fixed (byte* p = _gens) _genTex.LoadRawTextureData((IntPtr)p, _gens.Length);
        _genTex.Apply(false, false);
        Bridge.Perf.Upload(_pixels.Length + _gens.Length);
    }

    // 抜いた穴の向こうには床が無い (部屋と部屋の間は船体と宇宙) ので、部屋の絵より奥に瓦礫の床を敷く
    private static void SpawnUnderlay(Vector2 c, float r, List<Vector2> segs)
    {
        RoomZRange(c, out float nearZ, out float farZ);
        // 部屋の後ろに地面が描かれているマップ (Polus・Fungle = 部屋の絵が Sprites/Default) は、抜けた所から地面がそのまま見える
        if (_roomMatMask && !HasGroundBehind(c)) SpawnHullInterior(c, r, segs, farZ + UnderlayDepth);

        SpawnCracks(c, r * UnderlayArt.CrackReach, (_holeCount++ * 137.5f) % 360f, false);
    }

    // ひびは部屋の絵のすぐ手前に、部屋と同じマテリアルで貼る (穴の中は損傷マスクで自動的に抜ける・影も効く)。
    // reach = ひびの絵の半径 (世界単位)
    private static void SpawnCracks(Vector2 c, float reach, float angleDeg, bool impact)
    {
        RoomZRange(c, out float nearZ, out _);
        MarkFurniture(c, reach);
        var sprite = impact ? (_impactSprite ??= UnderlayArt.MakeCracks(true)) : (_crackSprite ??= UnderlayArt.MakeCracks(false));
        var crack = new GameObject("MrpCracks") { layer = 9 };
        crack.transform.SetParent(_ship.transform, true);
        crack.transform.position = new Vector3(c.x, c.y, nearZ - 0.002f);
        crack.transform.rotation = Quaternion.Euler(0f, 0f, angleDeg);
        crack.transform.localScale = Vector3.one * (reach * 2f / sprite.bounds.size.x) / _ship.transform.lossyScale.x;
        var csr = crack.AddComponent<SpriteRenderer>();
        csr.sprite = sprite;
        csr.sharedMaterial = _decalMat;
        Underlays.Add(crack);
    }

    // 穴の中 (部屋と部屋の隙間) は床でなく船体の中: 暗い奥に梁と配管が見える (手続きの絵を世界に固定した格子で敷き詰める)。
    // 梁は切った壁に沿わせる (壁の骨組みが見えている形)
    private static Sprite _hullTile;

    private static void SpawnHullInterior(Vector2 c, float r, List<Vector2> segs, float z)
    {
        _hullTile ??= UnderlayArt.MakeHullTile();
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
        // 壁の向きは 45° 刻みに丸める (隣の穴と格子を揃える・斜めの壁の梁も斜めに)
        float angle = MathF.Round(MathF.Atan2(wallDir.y, wallDir.x) * Mathf.Rad2Deg / 45f) * 45f;
        float rad = angle * Mathf.Deg2Rad;
        Vector2 ax = new(MathF.Cos(rad), MathF.Sin(rad)), ay = new(-ax.y, ax.x);
        var rot = Quaternion.Euler(0f, 0f, angle);

        float tw = _hullTile.bounds.size.x, th = _hullTile.bounds.size.y;
        float reach = r * 1.4f;
        float u0 = Vector2.Dot(c, ax), v0 = Vector2.Dot(c, ay);
        int iu0 = Mathf.FloorToInt((u0 - reach) / tw), iu1 = Mathf.CeilToInt((u0 + reach) / tw);
        int iv0 = Mathf.FloorToInt((v0 - reach) / th), iv1 = Mathf.CeilToInt((v0 + reach) / th);
        float parentScale = _ship.transform.lossyScale.x;
        for (int iu = iu0; iu <= iu1; iu++)
        for (int iv = iv0; iv <= iv1; iv++)
        {
            Vector2 p = ax * ((iu + 0.5f) * tw) + ay * ((iv + 0.5f) * th);
            if ((p - c).sqrMagnitude > (reach + tw) * (reach + tw)) continue;
            // 同じ格子の升は 1 枚だけ (重ねて敷くと板の数だけ描く)
            if (!HullCells.Add((iu, iv, (int)angle))) continue;
            var go = new GameObject("MrpHullInterior") { layer = 9 };
            go.transform.SetParent(_ship.transform, true);
            go.transform.position = new Vector3(p.x, p.y, z);
            go.transform.rotation = rot;
            go.transform.localScale = Vector3.one / parentScale;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _hullTile;
            sr.sharedMaterial = _underlayMat;
            Underlays.Add(go);
        }
    }

    private static readonly HashSet<(int, int, int)> HullCells = new();

    // 試合の始めに絵を先に作っておく (TerrainWarm)
    internal static void WarmArt()
    {
        _hullTile ??= UnderlayArt.MakeHullTile();
        _crackSprite ??= UnderlayArt.MakeCracks(false);
        _impactSprite ??= UnderlayArt.MakeCracks(true);
    }

    // 割れた塊の生成用: 損傷マスクの画素 (px, py) の値と、抜いた破壊の番号
    internal static int MapW => _w;
    internal static Vector2 Origin => _origin;
    internal static int MapH => _h;
    internal static byte HoleByte(int px, int py) => _pixels[(py * _w + px) * 4];
    internal static byte GenAt(int px, int py) => _gens[py * _w + px];
    internal static byte CurrentGen => _gen;
    internal static bool GenWrapped { get; private set; }
    internal static Vector2 TexelCenter(int px, int py) => new(_origin.x + (px + 0.5f) / PixelsPerUnit, _origin.y + (py + 0.5f) / PixelsPerUnit);
    internal const float TexelSize = 1f / PixelsPerUnit;
    internal static IReadOnlyList<SpriteRenderer> RoomArts => Rooms;
    internal static bool IsSwapped(SpriteRenderer sr) => Swapped.Contains(sr.GetInstanceID());
    internal static Transform ShipTransform => _ship ? _ship.transform : null;

    // 瓦礫・土煙などの部品用: 部屋と同じ描き方 (影が落ちる) で、損傷マスクは見ない
    public static Material PropMaterial => _propMat;

    // その場所の部屋の絵より手前の z
    public static float FrontZ(Vector2 c)
    {
        RoomZRange(c, out float near, out _);
        return near;
    }

    // マップが変わった時に一緒に片付ける
    public static void Track(GameObject go)
    {
        if (_ship) go.transform.SetParent(_ship.transform, true);
        Underlays.Add(go);
    }

    private static Material _propMat;
    private static Material _decalMat;

    private static void Reset()
    {
        foreach (var go in Underlays) if (go) UnityEngine.Object.Destroy(go);
        Underlays.Clear();
        TerrainFx.Clear();
        BreakPieces.Clear();
        WallPeel.Clear();
        RubbleBake.Clear();
        RubbleBlocks.Clear();
        Rooms.Clear();
        Swapped.Clear();
        Array.Clear(HiddenChannel, 0, HiddenChannel.Length);
        HullCells.Clear();
        if (_tex) UnityEngine.Object.Destroy(_tex);
        _tex = null;
        _pixels = null;
        if (_genTex) UnityEngine.Object.Destroy(_genTex);
        _genTex = null;
        _gens = null;
        _gen = 0;
        GenWrapped = false;
        _ship = null;
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
