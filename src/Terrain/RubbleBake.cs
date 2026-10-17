using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 止まった瓦礫 (割れた塊・破片・小石) を 1 枚の絵へ焼き込み、GameObject を消す。
// 残る瓦礫の数に上限が無くなり、描く物の数も焼いた回数だけになる。
// 焼き方: 止まった物を焼く用の層へ移し、その層だけを写す止めたカメラで、瓦礫の範囲ぴったりの描き先へ描いて
// (Camera.Render)、Texture2D へ読み戻す。読み戻した絵は CPU 側の写しを捨てる。
// RenderTexture のまま持たないのは、Android でアプリが裏へ回ると中身が消えるため。
// 端末は描き先からの写し (CopyTexture) が無く読み戻すしかない。描いてすぐ読むと GPU が描き終えるまで待つので (1 枚 4〜5ms の大半)、
// 描くのと読むのを分け、ReadDelay フレーム後に読む (その間は瓦礫を元の層へ戻して見せておく・焼きへ渡した瓦礫はもう動かない)。
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
    // 1 フレームに焼く量: 1 枚焼いた後、さらにこの時間まで続けて焼く (残りは次のフレームへ)。端末の読み戻しは GPU がそれまでの描画を
    // 終えるまで待つので 1 枚目が重く (重い場面で 5〜35ms)、同じフレームの 2 枚目からは 1ms 前後 (実測)。枚数で区切ると重いフレームが増える。
    // PC は GPU の中で写すので待ちが無く、1 枚ずつの重さだけ
#if ANDROID
    private const double ExtraBudgetMs = 6.0;
    private const int ReadDelay = 3;
    // 読み戻したフレームの次からは、そのフレームの送り込み (Apply) と描画を待つので 1 枚目が 15〜25ms になる (実機)。間を空けると 1 枚目は 1〜5ms
    private const int ReadGap = 4;
    private const long LostGapMs = 1000; // Tick の間がこれより空いた (裏へ回っていた) ら、描き先の中身が消えたとみて描き直す
#else
    private const double ExtraBudgetMs = 1.0;
#endif

    // テスト用: false で自動では焼かない (止まった物は GameObject のまま溜めておき、bake now で焼く = 焼く前後の比較)
    internal static bool Enabled = true;
    internal static int SheetCount => Sheets.Count;
    internal static int BakedCount { get; private set; }
#if ANDROID
    internal static int PendingCount => Queue.Count + Reading.Count;
#else
    internal static int PendingCount => Queue.Count;
#endif
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
#if ANDROID
    private sealed class InFlight
    {
        public Group G;
        public RenderTexture Rt;
        public int W, H, Frame;
        public float Cx, Cy;
        public bool Lost;
    }
    private static readonly List<InFlight> Reading = new(); // 描いて読み戻し待ちのまとまり (描いた順)
    private static readonly List<(InFlight F, Texture2D Tex)> Read = new(); // このフレームに読んで送り込み待ちの物
    private static int _frame, _lastReadFrame = -100;
    private static long _lastTickMs;
#endif

    // TerrainFx から: 動きが止まった瓦礫
    public static void Add(Transform tr, SpriteRenderer sr, float z, bool ownSprite)
    {
        Queue.Add(new Pending { Go = tr.gameObject, Sr = sr, Z = z, OwnSprite = ownSprite });
        _lastAddMs = Environment.TickCount64;
    }

    public static void Tick()
    {
#if ANDROID
        _frame++;
        long now = Environment.TickCount64;
        if (now - _lastTickMs > LostGapMs) foreach (var f in Reading) f.Lost = true;
        _lastTickMs = now;
        if (Reading.Count > 0 && _frame - _lastReadFrame >= ReadGap) ReadBack(budget: true);
#endif
        if (Queue.Count == 0 || !Enabled) return;
        if (Queue.Count < MaxBatch && Environment.TickCount64 - _lastAddMs < SettleMs) return;
        Flush(budget: true);
    }

    private sealed class Group
    {
        public float Z;
        public int[] Layers; // 瓦礫ごとの焼く前の層 (描く間だけ焼く層へ移す)
        public float X0 = float.MaxValue, Y0 = float.MaxValue, X1 = float.MinValue, Y1 = float.MinValue;
        public readonly List<Pending> Items = new();
    }

    // 溜まっている瓦礫を焼く (budget = 1 フレームの予算まで。残りは列に戻して次に回す)
    public static string Flush(bool budget = false)
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

        int sheets = 0, items = 0;
        long firstDone = 0;
        foreach (var g in groups.Values)
        {
            if (budget && firstDone != 0 && (System.Diagnostics.Stopwatch.GetTimestamp() - firstDone) * 1000.0 / System.Diagnostics.Stopwatch.Frequency > ExtraBudgetMs)
            {
                Queue.AddRange(g.Items);
                continue;
            }
            g.Layers = new int[g.Items.Count];
            for (int i = 0; i < g.Items.Count; i++) g.Layers[i] = g.Items[i].Go.layer;
#if ANDROID
            var f = Begin(g);
            if (f == null) continue;
            if (!budget) f.Frame = int.MinValue / 2; // その場で全部焼く (bake now)
            Reading.Add(f);
            sheets++;
            items += g.Items.Count;
            if (firstDone == 0) firstDone = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        if (!budget) ReadBack(budget: false);
        return $"rendered items={items} sheets={sheets} reading={Reading.Count} total={Sheets.Count} kb={SheetBytes / 1024}";
#else
            if (!Render(g))
            {
                // 焼けなかった瓦礫は消さずに元の層へ戻して残す
                RestoreLayers(g);
                continue;
            }
            sheets++;
            if (firstDone == 0) firstDone = System.Diagnostics.Stopwatch.GetTimestamp();
            items += DestroyItems(g);
        }
        BakedCount += items;
        return $"baked items={items} sheets={sheets} total={Sheets.Count} kb={SheetBytes / 1024}";
#endif
    }

    private static int DestroyItems(Group g)
    {
        int n = 0;
        foreach (var p in g.Items)
        {
            if (!p.Go) continue;
            if (p.OwnSprite && p.Sr.sprite) UnityEngine.Object.Destroy(p.Sr.sprite);
            UnityEngine.Object.Destroy(p.Go);
            n++;
        }
        return n;
    }

    // 範囲を画素の格子に合わせる (焼いた絵の画素が世界の決まった位置に来るように)
    private static void Frame(Group g, out int w, out int h, out float cx, out float cy)
    {
        float x0 = MathF.Floor(g.X0 * Ppu) / Ppu, y0 = MathF.Floor(g.Y0 * Ppu) / Ppu;
        w = Math.Clamp((int)MathF.Ceiling((g.X1 - x0) * Ppu) + 1, 2, MaxPx);
        h = Math.Clamp((int)MathF.Ceiling((g.Y1 - y0) * Ppu) + 1, 2, MaxPx);
        cx = x0 + w * 0.5f / Ppu; cy = y0 + h * 0.5f / Ppu;
    }

    // まとまりを描き先へ描く (瓦礫は描く間だけ焼く層へ移す)
    private static void Draw(Group g, RenderTexture rt, int w, int h, float cx, float cy)
    {
        foreach (var p in g.Items) if (p.Go) p.Go.layer = BakeLayer;
        var prev = RenderTexture.active;
        try
        {
            RenderTexture.active = rt;
            GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));
            _cam.transform.position = new Vector3(cx, cy, g.Z - 5f);
            _cam.orthographicSize = h * 0.5f / Ppu;
            _cam.aspect = w / (float)h;
            _cam.targetTexture = rt;
            _cam.Render();
        }
        finally
        {
            _cam.targetTexture = null;
            RenderTexture.active = prev;
#if ANDROID
            RestoreLayers(g); // 読み戻すまで見せておく
#endif
        }
    }

    private static void RestoreLayers(Group g)
    {
        for (int i = 0; i < g.Items.Count; i++) if (g.Items[i].Go) g.Items[i].Go.layer = g.Layers[i];
    }

    // 焼いた絵を床の板として置く
    private static void Place(Texture2D tex, int w, int h, float cx, float cy, float z)
    {
        var sp = Sprite.Create(tex, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), Ppu, 0, SpriteMeshType.FullRect);
        sp.name = "MrpRubble";
        var go = new GameObject("MrpRubble");
        DamageMap.Track(go);
        go.transform.position = new Vector3(cx, cy, z);
        // 焼いた絵は世界の向きで撮ったので、左右反転の船の子でも裏返さない
        if (DamageMap.ShipMirrored) go.transform.localScale = new Vector3(-1f, 1f, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        if (DamageMap.PlateMaterial) sr.sharedMaterial = DamageMap.PlateMaterial;
        sr.sprite = sp;
        Sheets.Add((tex, sp));
        Plates.Add(go);
        SheetBytes += (long)w * h * 4;
    }

#if ANDROID
    private static InFlight Begin(Group g)
    {
        Frame(g, out int w, out int h, out float cx, out float cy);
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
        try { Draw(g, rt, w, h, cx, cy); }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"rubble bake {w}x{h} failed: {e.Message}");
            RenderTexture.ReleaseTemporary(rt);
            return null; // 焼けなかった瓦礫は消さずに残す (元の層へは Draw が戻している)
        }
        return new InFlight { G = g, Rt = rt, W = w, H = h, Cx = cx, Cy = cy, Frame = _frame };
    }

    // ReadDelay フレーム経ったまとまりを読み戻して床の板にし、瓦礫を消す (budget = 1 枚目の後は ExtraBudgetMs まで)
    private static void ReadBack(bool budget)
    {
        // 読むのを先に全部済ませてから送り込む (Apply)。送り込みを挟むと次の ReadPixels がその完了まで待たされ、2 枚目以降も重くなる
        long first = 0;
        int done = 0;
        Read.Clear();
        for (; done < Reading.Count; done++)
        {
            var f = Reading[done];
            if (budget && _frame - f.Frame < ReadDelay) break; // 描いた順なので、ここから後もまだ
            if (budget && first != 0 && (System.Diagnostics.Stopwatch.GetTimestamp() - first) * 1000.0 / System.Diagnostics.Stopwatch.Frequency > ExtraBudgetMs) break;
            bool any = false;
            foreach (var p in f.G.Items) if (p.Go) { any = true; break; }
            if (!any) { RenderTexture.ReleaseTemporary(f.Rt); continue; } // 待つ間に瓦礫が全部片付けられた (板を置くと消えた瓦礫が残る)
            Texture2D tex = null;
            var prev = RenderTexture.active;
            try
            {
                if (f.Lost) Draw(f.G, f.Rt, f.W, f.H, f.Cx, f.Cy);
                tex = new Texture2D(f.W, f.H, TextureFormat.RGBA32, false) { name = "MrpRubble", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                RenderTexture.active = f.Rt;
                tex.ReadPixels(new Rect(0f, 0f, f.W, f.H), 0, 0, false);
                RenderTexture.active = prev;
                Read.Add((f, tex));
            }
            catch (Exception e)
            {
                RenderTexture.active = prev;
                Plugin.Logger.LogWarning($"rubble bake {f.W}x{f.H} failed: {e.Message}");
                if (tex) UnityEngine.Object.Destroy(tex);
            }
            finally { RenderTexture.ReleaseTemporary(f.Rt); }
            if (first == 0) first = System.Diagnostics.Stopwatch.GetTimestamp(); // 予算は 1 枚目の後から (1 枚目は描画の同期待ちで重く、2 枚目からは軽い)
        }
        Reading.RemoveRange(0, done);
        if (Read.Count > 0) _lastReadFrame = _frame;
        foreach (var (f, tex) in Read)
        {
            try
            {
                tex.Apply(false, true); // CPU 側の写しは捨てる
                Place(tex, f.W, f.H, f.Cx, f.Cy, f.G.Z);
                BakedCount += DestroyItems(f.G);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"rubble bake {f.W}x{f.H} failed: {e.Message}");
                UnityEngine.Object.Destroy(tex);
            }
        }
        Read.Clear();
    }
#endif

#if !ANDROID
    // 1 つのまとまりを、その範囲ぴったりの絵に焼く (瓦礫は焼く層に移したまま。消すのは呼んだ側)
    private static bool Render(Group g)
    {
        Frame(g, out int w, out int h, out float cx, out float cy);
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
        Texture2D tex = null;
        try
        {
            Draw(g, rt, w, h, cx, cy);
            tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "MrpRubble", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            // 読み戻し (GPU を待って 1 枚 3〜4ms) をせず、GPU の中で描き先から写す。先に空の絵を送って CPU 側の写しを捨てておく (後で Apply すると写した絵が空で上書きされる)
            tex.Apply(false, true);
            Graphics.CopyTexture(rt, 0, 0, tex, 0, 0);
            Place(tex, w, h, cx, cy, g.Z);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"rubble bake {w}x{h} failed: {e.Message}");
            if (tex) UnityEngine.Object.Destroy(tex);
            return false;
        }
        finally { RenderTexture.ReleaseTemporary(rt); }
    }
#endif

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
#if ANDROID
        foreach (var f in Reading) foreach (var p in f.G.Items) if (p.Go) into.Add(p.Go);
#endif
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
#if ANDROID
        foreach (var f in Reading) RenderTexture.ReleaseTemporary(f.Rt);
        Reading.Clear();
#endif
        BakedCount = 0;
        SheetBytes = 0;
    }
}
