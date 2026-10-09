using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 視界の外 (影の中) でも壊れた所を壊れた見た目にする。
// 影の中に見えている船は、本編の影のカメラ (層 9〜12 を描く) が置き換えシェーダ (Hidden/LightCutaway) で描き直した絵で、
// 部屋の絵の損傷マスクも、穴の向こうの床やひびの板の切り抜きも効かない (壊れる前の壁とひびの板全体が見える)。
// そこで壊れた所の周りを、メインカメラと同じ描き方 (置き換え無し) で升ごとに焼き、損傷のある画素だけを残した絵を
// 層 10 (影のカメラだけが描き、メインカメラは描かない層) に部屋の絵のすぐ手前に置く。影のカメラはこれを普通の絵として描くので、
// 影の中の壊れた所が視界の中と同じに見える。視界の中では影の板が透けているので、この絵は見えない。
// 焼く見た目は影のカメラにそろえる: 本編の絵は影のカメラと同じ置き換えシェーダで描き (Polus の地面は影の中では暗く描かれるので、
// 普通に描くと壊れた所だけ明るく浮く)、何も無い所は影のカメラと同じ不透明の黒にする (透明にすると壊れる前の壁が透ける)。
// 止まった瓦礫 (層 0) は影のカメラに写らないので、型抜きの後に上から描き込む (止まるたびに少し待ってその升を焼き直す)。
// 焼いた絵は Texture2D へ読み戻して持つ (RenderTexture のままだと Android でアプリが裏へ回った時に消える)。
internal static class ShadowPatch
{
    private const int PatchLayer = 10;      // 影のカメラだけが描く層
    private const int MaskLayer = 30;       // 型抜きの板 (本編もこの mod の他の所も使っていない層)
    private const int DebrisLayer = 29;     // 焼く間だけ止まった瓦礫を移す層 (同上)
    private const int OwnLayer = 28;        // 焼く間だけこの mod の船の絵を移す層 (同上)
    private const int FrontLayer = 27;      // 焼く間だけその手前の本編の絵を移す層 (同上)
    private const string ReplacementTag = "RenderType"; // 影のカメラの置き換えと同じ (RenderType ごとに置き換える)
    private const long SettleWaitMs = 400;  // 瓦礫が止まってから升を焼き直すまで (続けて止まる分をまとめる)
    private const float Tile = 2f;          // 升の大きさ (世界単位)
    private const int Px = 128;             // 升の画素数 (影のカメラの描き先は 512 画素で画面の縦 6 単位ほど = 約 85 画素/単位なので、それより細かい)
    private const int MaxTiles = 96;        // 持つ升の上限 (1 枚 64KB)。超えたら古く焼いた順に捨てる
    private const int TilesPerFrame = 3;    // 1 フレームに焼く升 (読み戻しは GPU を待つので、残りは次のフレームへ)
    private const int ShipLayers = (1 << 9) | (1 << 11) | (1 << 12); // 影のカメラが描く層のうち、層 10 (視界の形・この絵) を除いたもの

    // テスト用: false で焼かない (影の中で壊れる前の壁が見える、直す前の見え方と比べる)
    internal static bool Enabled = true;
    internal static int TileCount => Tiles.Count;
    internal static int BakedTotal { get; private set; }

    private sealed class Patch
    {
        public GameObject Go;
        public Texture2D Tex;
        public Sprite Sp;
        public long Order;
    }

    private static readonly Dictionary<(int, int), Patch> Tiles = new();
    private static readonly List<(int, int)> Dirty = new();
    private static readonly HashSet<(int, int)> DirtySet = new();
    private static readonly List<(int, int)> Late = new();
    private static readonly HashSet<(int, int)> LateSet = new();
    private static long _lastLateMs;
    private static readonly List<(Renderer R, float Z, int Id)> Candidates = new(); // 船の層の描画物 (この mod の絵より手前の本編の絵を探す)
    private static readonly List<SpriteRenderer> Own = new();
    private static readonly HashSet<int> OwnIds = new();
    private static readonly List<(GameObject Go, int Layer)> Moved = new();
    private static Shader _shadozer;
    private static Transform _candidatesShip;
    private static readonly List<GameObject> Debris = new();
    private static readonly List<int> DebrisLayers = new();
    private static Camera _cam;
    private static GameObject _mask;
    private static Material _maskMat;
    private static long _order;

    // 壊れた範囲 (中心と半径) に掛かる升を焼き直す予定に入れる
    public static void MarkDirty(Vector2 c, float r)
    {
        if (!Enabled) return;
        int x0 = (int)MathF.Floor((c.x - r) / Tile), x1 = (int)MathF.Floor((c.x + r) / Tile);
        int y0 = (int)MathF.Floor((c.y - r) / Tile), y1 = (int)MathF.Floor((c.y + r) / Tile);
        for (int ix = x0; ix <= x1; ix++)
        for (int iy = y0; iy <= y1; iy++)
            if (DirtySet.Add((ix, iy))) Dirty.Add((ix, iy));
    }

    // 瓦礫が止まった所の升を、少し待ってから焼き直す予定に入れる
    public static void MarkSettled(float x, float y)
    {
        if (!Enabled) return;
        var key = ((int)MathF.Floor(x / Tile), (int)MathF.Floor(y / Tile));
        if (LateSet.Add(key)) Late.Add(key);
        _lastLateMs = Environment.TickCount64;
    }

    public static void Tick()
    {
        if (Late.Count > 0 && Environment.TickCount64 - _lastLateMs >= SettleWaitMs)
        {
            foreach (var key in Late) if (DirtySet.Add(key)) Dirty.Add(key);
            Late.Clear();
            LateSet.Clear();
        }
        if (Dirty.Count == 0) return;
        if (!Enabled || !DamageMap.ShipTransform || !EnsureCamera()) { Dirty.Clear(); DirtySet.Clear(); return; }
        int n = Math.Min(TilesPerFrame, Dirty.Count);
        for (int k = 0; k < n; k++)
        {
            var key = Dirty[k];
            DirtySet.Remove(key);
            Bake(key);
        }
        Dirty.RemoveRange(0, n);
    }

    // 溜まっている升を今すぐ全部焼く (テスト用)
    internal static int FlushAll()
    {
        int n = 0;
        _lastLateMs = 0;
        while (Dirty.Count > 0 || Late.Count > 0) { n += Math.Min(TilesPerFrame, Dirty.Count + Late.Count); Tick(); }
        return n;
    }

    private static void Bake((int x, int y) key)
    {
        float x0 = key.x * Tile, y0 = key.y * Tile;
        var center = new Vector2(x0 + Tile * 0.5f, y0 + Tile * 0.5f);
        // 部屋の絵とひびの板 (部屋の絵の 0.002 手前) より手前、手前の家具や扉より奥。升の四隅と真ん中でいちばん手前の部屋に合わせる。
        // 壁の上面の絵 (クルーより手前) も含める: 影のカメラはそれを壊れる前の形で描くので、その手前に置かないと影の中で壁が元に戻って見える
        float near = DamageMap.FrontZ(center, true);
        near = Math.Min(near, DamageMap.FrontZ(new Vector2(x0, y0), true));
        near = Math.Min(near, DamageMap.FrontZ(new Vector2(x0 + Tile, y0), true));
        near = Math.Min(near, DamageMap.FrontZ(new Vector2(x0, y0 + Tile), true));
        near = Math.Min(near, DamageMap.FrontZ(new Vector2(x0 + Tile, y0 + Tile), true));

        var rt = RenderTexture.GetTemporary(Px, Px, 0, RenderTextureFormat.ARGB32);
        var prev = RenderTexture.active;
        Texture2D tex = null;
        Sprite sp = null;
        try
        {
            RenderTexture.active = rt;
            GL.Clear(false, true, new Color(0f, 0f, 0f, 1f)); // 影のカメラの背景と同じ不透明の黒
            _cam.targetTexture = rt;
            _cam.transform.position = new Vector3(center.x, center.y, -100f);
            _cam.orthographicSize = Tile * 0.5f;
            _cam.aspect = 1f;
            // 1 回目: 船の絵を影のカメラと同じ見た目で。この mod の絵 (部屋の絵の穴・穴の向こうの床・ひび) だけメインカメラと同じ描き方
            RenderShip(new Rect(x0, y0, Tile, Tile));
            // 2 回目: 型抜きの板で、損傷の無い画素のアルファを 0 にする (色はそのまま)
            _mask.SetActive(true);
            _mask.transform.position = new Vector3(center.x, center.y, -50f);
            _cam.cullingMask = 1 << MaskLayer;
            _cam.Render();
            _mask.SetActive(false);
            // 3 回目: 止まった瓦礫を上から (型抜きの外にも描く)
            RenderDebris(new Rect(x0, y0, Tile, Tile));

            RenderTexture.active = rt;
            tex = new Texture2D(Px, Px, TextureFormat.RGBA32, false) { name = "MrpShadowPatch", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            tex.ReadPixels(new Rect(0f, 0f, Px, Px), 0, 0, false);
            tex.Apply(false, true); // CPU 側の写しは捨てる
            sp = Sprite.Create(tex, new Rect(0f, 0f, Px, Px), new Vector2(0.5f, 0.5f), Px / Tile, 0, SpriteMeshType.FullRect);
            sp.name = "MrpShadowPatch";

            if (Tiles.TryGetValue(key, out var old)) Drop(old);
            var go = new GameObject("MrpShadowPatch") { layer = PatchLayer };
            go.transform.SetParent(DamageMap.ShipTransform, true); // 焼き直すたびに作り直すので DamageMap の片付けの一覧には入れない
            go.transform.position = new Vector3(center.x, center.y, near - 0.005f);
            go.transform.localScale = Vector3.one / DamageMap.ShipTransform.lossyScale.x;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sp;
            Tiles[key] = new Patch { Go = go, Tex = tex, Sp = sp, Order = _order++ };
            BakedTotal++;
            if (Tiles.Count > MaxTiles) EvictOldest();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"shadow patch bake failed: {e.Message}");
            if (sp) UnityEngine.Object.Destroy(sp);
            if (tex) UnityEngine.Object.Destroy(tex);
            if (_mask) _mask.SetActive(false);
        }
        finally
        {
            _cam.targetTexture = null;
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    // 本編の絵は影のカメラの置き換えシェーダで描く (普通に描くと Polus の地面などが影の中より明るく出て、壊れた所だけ浮く)。
    // この mod の絵は置き換えると損傷マスクが効かないので普通に描き、その手前にある本編の絵 (家具・扉・壁の上面) はもう一度置き換えて描く
    private static void RenderShip(Rect tile)
    {
        var ship = DamageMap.ShipTransform;
        if (_candidatesShip != ship)
        {
            // マップごとに 1 度: 船の層の描画物と z
            _candidatesShip = ship;
            Candidates.Clear();
            foreach (var r in ship.GetComponentsInChildren<Renderer>(true))
                if ((ShipLayers & (1 << r.gameObject.layer)) != 0) Candidates.Add((r, r.transform.position.z, r.GetInstanceID()));
            var collab = UnityEngine.Object.FindObjectOfType<ShadowCollab>();
            var sc = collab && collab.ShadowCamera ? collab.ShadowCamera.GetComponent<ShadowCamera>() : null;
            _shadozer = sc ? sc.Shadozer : null;
            if (!_shadozer) Plugin.Logger.LogWarning("shadow patch: shadow camera replacement shader not found");
        }

        Own.Clear();
        OwnIds.Clear();
        DamageMap.CollectOwnShipRenderers(Own);
        float ownNear = float.MaxValue;
        Moved.Clear();
        foreach (var sr in Own)
        {
            OwnIds.Add(sr.GetInstanceID());
            var b = sr.bounds;
            if (b.max.x < tile.xMin || b.min.x > tile.xMax || b.max.y < tile.yMin || b.min.y > tile.yMax) continue;
            var go = sr.gameObject;
            Moved.Add((go, go.layer));
            go.layer = OwnLayer;
            float z = sr.transform.position.z;
            if (z < ownNear) ownNear = z;
        }
        int ownCount = Moved.Count;
        try
        {
            // 本編の絵 (この mod の絵は層を移してあるので写らない)
            _cam.cullingMask = ShipLayers;
            Render(true);
            // この mod の絵
            if (ownCount > 0)
            {
                _cam.cullingMask = 1 << OwnLayer;
                Render(false);
                // その手前の本編の絵
                foreach (var (r, z, id) in Candidates)
                {
                    if (z >= ownNear || OwnIds.Contains(id) || !r) continue;
                    var b = r.bounds;
                    if (b.max.x < tile.xMin || b.min.x > tile.xMax || b.max.y < tile.yMin || b.min.y > tile.yMax) continue;
                    var go = r.gameObject;
                    Moved.Add((go, go.layer));
                    go.layer = FrontLayer;
                }
                if (Moved.Count > ownCount)
                {
                    _cam.cullingMask = 1 << FrontLayer;
                    Render(true);
                }
            }
        }
        finally
        {
            for (int i = Moved.Count - 1; i >= 0; i--)
                if (Moved[i].Go) Moved[i].Go.layer = Moved[i].Layer;
            Moved.Clear();
        }
    }

    private static void Render(bool asShadowCamera)
    {
        if (asShadowCamera && _shadozer) _cam.RenderWithShader(_shadozer, ReplacementTag);
        else _cam.Render();
    }

    private static void RenderDebris(Rect tile)
    {
        Debris.Clear();
        DamageMap.CollectCracks(Debris);
        RubbleBake.CollectSettled(Debris);
        WaterLeak.CollectPuddles(Debris);
        FurnitureLift.CollectLifted(Debris);
        FoamArt.CollectCured(Debris);
        DebrisLayers.Clear();
        int moved = 0;
        foreach (var go in Debris)
        {
            var sr = go.GetComponent<SpriteRenderer>();
            if (!sr) { DebrisLayers.Add(-1); continue; }
            var b = sr.bounds;
            if (b.max.x < tile.xMin || b.min.x > tile.xMax || b.max.y < tile.yMin || b.min.y > tile.yMax) { DebrisLayers.Add(-1); continue; }
            DebrisLayers.Add(go.layer);
            go.layer = DebrisLayer;
            moved++;
        }
        if (moved == 0) return;
        try
        {
            _cam.cullingMask = 1 << DebrisLayer;
            _cam.Render();
        }
        finally
        {
            for (int i = 0; i < Debris.Count; i++)
                if (DebrisLayers[i] >= 0 && Debris[i]) Debris[i].layer = DebrisLayers[i];
            Debris.Clear();
        }
    }

    private static void EvictOldest()
    {
        (int, int) oldest = default;
        long best = long.MaxValue;
        foreach (var kv in Tiles)
            if (kv.Value.Order < best) { best = kv.Value.Order; oldest = kv.Key; }
        if (best == long.MaxValue) return;
        Drop(Tiles[oldest]);
        Tiles.Remove(oldest);
    }

    private static void Drop(Patch p)
    {
        if (p.Go) UnityEngine.Object.Destroy(p.Go);
        if (p.Sp) UnityEngine.Object.Destroy(p.Sp);
        if (p.Tex) UnityEngine.Object.Destroy(p.Tex);
    }

    internal static void Warm() => EnsureCamera();

    private static bool EnsureCamera()
    {
        if (_cam && _mask) return true;
        if (!MrpBundle.Ready) return false;
        if (!_cam)
        {
            var go = new GameObject("MrpShadowPatchCamera");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _cam = go.AddComponent<Camera>();
            _cam.enabled = false;
            _cam.orthographic = true;
            _cam.clearFlags = CameraClearFlags.Nothing;
            _cam.nearClipPlane = 0.01f;
            _cam.farClipPlane = 200f;
            _cam.allowHDR = false;
            _cam.allowMSAA = false;
        }
        if (!_mask)
        {
            // 型抜き: 色は残し (Zero, One)、アルファは板の値を掛ける (Zero, SrcAlpha)
            _maskMat = new Material(MrpBundle.TerrainMaterial) { name = "MrpShadowMask" };
            _maskMat.SetFloat("_UseDamage", 6f);
            _maskMat.SetFloat("_SrcBlend", 0f);   // Zero
            _maskMat.SetFloat("_DstBlend", 1f);   // One
            _maskMat.SetFloat("_SrcBlendA", 0f);  // Zero
            _maskMat.SetFloat("_DstBlendA", 5f);  // SrcAlpha
            _maskMat.SetFloat("_MaskComp", 8f);   // Always
            _maskMat.SetFloat("_StencilPass", 0f); // Keep
            var white = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "MrpShadowMaskTex", hideFlags = HideFlags.DontUnloadUnusedAsset };
            var px = new Color32[16];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
            white.SetPixels32(px);
            white.Apply(false, true);
            var sprite = Sprite.Create(white, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f), 4f / Tile, 0, SpriteMeshType.FullRect);
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _mask = new GameObject("MrpShadowMask") { layer = MaskLayer };
            UnityEngine.Object.DontDestroyOnLoad(_mask);
            var sr = _mask.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sharedMaterial = _maskMat;
            _mask.SetActive(false);
        }
        return _cam && _mask;
    }

    // マップが変わった時
    public static void Clear()
    {
        foreach (var p in Tiles.Values) Drop(p);
        Tiles.Clear();
        Dirty.Clear();
        DirtySet.Clear();
        Late.Clear();
        LateSet.Clear();
        Candidates.Clear();
        _candidatesShip = null;
        _shadozer = null;
        BakedTotal = 0;
    }
}
