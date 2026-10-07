using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 止まった瓦礫 (割れた塊・破片・小石) を 1 枚の絵へ焼き込み、GameObject を消す。
// 残る瓦礫の数に上限が無くなり、描く物の数も焼いた回数だけになる。
// 焼き方: 止まった物を焼く用の層へ移し、その層だけを写す止めたカメラで、瓦礫の範囲ぴったりの描き先へ描いて
// (Camera.Render)、Texture2D へ読み戻す。読み戻した絵は CPU 側の写しを捨てる。
// RenderTexture のまま持たないのは、Android でアプリが裏へ回ると中身が消えるため。
// 細かさは標準の寄り具合の画面とほぼ同じ (粗いと焼いた途端にぼやけ、輪郭線が溶ける)。
// 絵は z ごとに分ける (部屋ごとに z が違う。塊は自分の部屋の z で描かれている)
internal static class RubbleBake
{
    private const int BakeLayer = 31;       // 本編が使っていない層 (影のカメラが描き直す 9〜12 には置かない)
    private const float Ppu = 176f;         // 焼いた絵の画素/単位 (標準の寄り具合で 1080 行の画面 ≈ 176)
    private const float Cell = 1.5f;        // 1 枚にまとめる範囲 (瓦礫の真ん中がこの格子の同じ升にある物)。散らばった瓦礫の隙間の透明な画素を減らすため小さめ
    private const int MaxPx = 2048;         // 1 枚の縦横の上限 (升より大きくはみ出した時の歯止め)
    private const long SettleMs = 500;      // 最後に止まってからこれだけ待ってまとめて焼く (読み戻しは GPU を待つので回数を減らす)
    private const int MaxBatch = 96;        // これだけ溜まったら待たずに焼く
    private const int SheetsPerFrame = 2;   // 1 フレームに焼く枚数 (読み戻しは GPU を待つので、残りは次のフレームへ)

    // テスト用: false で自動では焼かない (止まった物は GameObject のまま溜めておき、bake now で焼く = 焼く前後の比較)
    internal static bool Enabled = true;
    internal static int SheetCount => Sheets.Count;
    internal static int BakedCount { get; private set; }
    internal static int PendingCount => Queue.Count;
    internal static long SheetBytes { get; private set; }

    private struct Pending
    {
        public GameObject Go;
        public SpriteRenderer Sr;
        public float Z;
        public bool OwnSprite; // 割れた塊の絵は塊ごとに作ったもの (消す時に一緒に消す)
    }

    private static readonly List<(Texture2D Tex, Sprite Sp)> Sheets = new();
    private static readonly List<Pending> Queue = new();
    private static readonly List<GameObject> Plates = new(); // 焼いた床の板 (影の中の焼いた絵にも描き込むため)
    private static long _lastAddMs;
    private static Camera _cam;

    // TerrainFx から: 動きが止まった瓦礫
    public static void Add(Transform tr, SpriteRenderer sr, float z, bool ownSprite)
    {
        Queue.Add(new Pending { Go = tr.gameObject, Sr = sr, Z = z, OwnSprite = ownSprite });
        _lastAddMs = Environment.TickCount64;
    }

    public static void Tick()
    {
        if (Queue.Count == 0 || !Enabled) return;
        if (Queue.Count < MaxBatch && Environment.TickCount64 - _lastAddMs < SettleMs) return;
        Flush(SheetsPerFrame);
    }

    private sealed class Group
    {
        public float Z;
        public float X0 = float.MaxValue, Y0 = float.MaxValue, X1 = float.MinValue, Y1 = float.MinValue;
        public readonly List<Pending> Items = new();
    }

    // 溜まっている瓦礫を焼く (maxSheets 枚まで。残りは列に戻して次に回す)
    public static string Flush(int maxSheets = int.MaxValue)
    {
        if (Queue.Count == 0) return "nothing";
        if (!EnsureCamera()) { Queue.Clear(); return "no camera"; }
        var groups = new Dictionary<(int, int, int), Group>();
        foreach (var p in Queue)
        {
            if (!p.Go) continue;
            var b = p.Sr.bounds;
            Vector3 lo = b.min, hi = b.max;
            var key = (ZKey(p.Z), (int)MathF.Floor((lo.x + hi.x) * 0.5f / Cell), (int)MathF.Floor((lo.y + hi.y) * 0.5f / Cell));
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = new Group { Z = p.Z };
            g.Items.Add(p);
            g.X0 = Math.Min(g.X0, lo.x); g.Y0 = Math.Min(g.Y0, lo.y);
            g.X1 = Math.Max(g.X1, hi.x); g.Y1 = Math.Max(g.Y1, hi.y);
        }
        Queue.Clear();

        int sheets = 0, items = 0, tried = 0;
        foreach (var g in groups.Values)
        {
            if (tried++ >= maxSheets) { Queue.AddRange(g.Items); continue; }
            int layer = g.Items[0].Go.layer;
            foreach (var p in g.Items) p.Go.layer = BakeLayer;
            if (!Render(g))
            {
                // 焼けなかった瓦礫は消さずに元の層へ戻して残す
                foreach (var p in g.Items) p.Go.layer = layer;
                continue;
            }
            sheets++;
            foreach (var p in g.Items)
            {
                if (p.OwnSprite && p.Sr.sprite) UnityEngine.Object.Destroy(p.Sr.sprite);
                UnityEngine.Object.Destroy(p.Go);
                items++;
            }
        }
        BakedCount += items;
        return $"baked items={items} sheets={sheets} total={Sheets.Count} kb={SheetBytes / 1024}";
    }

    // 1 つのまとまりを、その範囲ぴったりの絵に焼く
    private static bool Render(Group g)
    {
        // 画素の格子に合わせた範囲 (焼いた絵の画素が世界の決まった位置に来るように)
        float x0 = MathF.Floor(g.X0 * Ppu) / Ppu, y0 = MathF.Floor(g.Y0 * Ppu) / Ppu;
        int w = Math.Clamp((int)MathF.Ceiling((g.X1 - x0) * Ppu) + 1, 2, MaxPx);
        int h = Math.Clamp((int)MathF.Ceiling((g.Y1 - y0) * Ppu) + 1, 2, MaxPx);
        float cx = x0 + w * 0.5f / Ppu, cy = y0 + h * 0.5f / Ppu;

        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
        var prev = RenderTexture.active;
        Texture2D tex = null;
        Sprite sp = null;
        try
        {
            RenderTexture.active = rt;
            GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));
            _cam.transform.position = new Vector3(cx, cy, g.Z - 5f);
            _cam.orthographicSize = h * 0.5f / Ppu;
            _cam.aspect = w / (float)h;
            _cam.targetTexture = rt;
            _cam.Render();

            RenderTexture.active = rt;
            tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "MrpRubble", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            tex.ReadPixels(new Rect(0f, 0f, w, h), 0, 0, false);
            tex.Apply(false, true); // CPU 側の写しは捨てる
            sp = Sprite.Create(tex, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), Ppu, 0, SpriteMeshType.FullRect);
            sp.name = "MrpRubble";

            var go = new GameObject("MrpRubble");
            DamageMap.Track(go);
            go.transform.position = new Vector3(cx, cy, g.Z);
            var sr = go.AddComponent<SpriteRenderer>();
            if (DamageMap.PlateMaterial) sr.sharedMaterial = DamageMap.PlateMaterial;
            sr.sprite = sp;
            Sheets.Add((tex, sp));
            Plates.Add(go);
            SheetBytes += (long)w * h * 4;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"rubble bake {w}x{h} failed: {e.Message}");
            if (sp) UnityEngine.Object.Destroy(sp);
            if (tex) UnityEngine.Object.Destroy(tex);
            return false;
        }
        finally
        {
            _cam.targetTexture = null;
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    internal static void Warm() => EnsureCamera();

    private static bool EnsureCamera()
    {
        if (_cam) return true;
        var go = new GameObject("MrpRubbleBakeCamera");
        UnityEngine.Object.DontDestroyOnLoad(go);
        _cam = go.AddComponent<Camera>();
        _cam.enabled = false;
        _cam.orthographic = true;
        _cam.clearFlags = CameraClearFlags.Nothing;
        _cam.cullingMask = 1 << BakeLayer;
        _cam.nearClipPlane = 0.01f;
        _cam.farClipPlane = 10f;
        _cam.allowHDR = false;
        _cam.allowMSAA = false;
        return _cam;
    }

    // 止まっている瓦礫 (焼く前の物と焼いた床の板) を into に足す
    internal static void CollectSettled(List<GameObject> into)
    {
        foreach (var p in Queue) if (p.Go) into.Add(p.Go);
        foreach (var go in Plates) if (go) into.Add(go);
    }

    private static int ZKey(float z) => (int)MathF.Round(z * 1000f);

    // マップが変わった時 (焼いた絵の GameObject は DamageMap が片付ける)
    public static void Clear()
    {
        foreach (var (tex, sp) in Sheets)
        {
            if (sp) UnityEngine.Object.Destroy(sp);
            if (tex) UnityEngine.Object.Destroy(tex);
        }
        Sheets.Clear();
        Plates.Clear();
        Queue.Clear();
        BakedCount = 0;
        SheetBytes = 0;
    }
}
