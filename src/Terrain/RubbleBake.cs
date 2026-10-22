using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 止まった瓦礫 (割れた塊・破片・小石) を 1 枚の絵へ焼き込み、GameObject を消す。
// 残る瓦礫の数に上限が無くなり、描く物の数も焼いた回数だけになる。
// 焼き方: 止まった物を焼く用の層へ移し、その層だけを写す止めたカメラで、瓦礫の範囲ぴったりの描き先へ描いて
// (Camera.Render)、Texture2D へ読み戻す。読み戻した絵は CPU 側の写しを捨てる。
// RenderTexture のまま持たないのは、Android でアプリが裏へ回ると中身が消えるため。
// 端末は描き先からの写し (CopyTexture) が無く読み戻すしかない。読み戻しは 1 回ごとに描画の完了待ち (重い場面で数 ms) と
// 画素数に比例する重さ (約 3ms/MB) と呼ぶ回数ごとの重さがある (実測)。そこで端末では、まとまりを 1 枚の描き先に並べて描き、
// 描いてから ReadDelay フレーム後に、1 フレームに BandPx 画素ずつ帯で読む (その間は瓦礫を元の層へ戻して見せておく・焼きへ渡した瓦礫はもう動かない)。
// 床の板はまとまりごとに、読んだ絵の中の自分の四角を指す (並べた時の隙間も絵に残るが、呼ぶ回数を減らす方を取る)。
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
    // 1 フレームに描く量: 1 枚描いた後、さらにこの時間まで続けて描く (残りは次のフレームへ)。1 枚ごとの Camera.Render が端末で約 0.3ms。
    // PC は GPU の中で写すので読み戻しの待ちが無く、1 枚ずつの重さだけ
#if ANDROID
    private const double ExtraBudgetMs = 3.0;
    private const int ReadDelay = 3;
    // 読み戻しと送り込み (Apply) は ReadGap フレームおき。送り込んだ直後に読むとその完了まで待たされる (実機で 15〜25ms)
    private const int ReadGap = 4;
    private const int BandPx = 256 * 1024; // 1 フレームに読む画素 (約 1MB・端末で約 3ms)
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
        public Action<GameObject> OnPlate; // 1 つだけで焼き、できた板を渡す (拾える瓦礫。板ごと消せるように他と混ぜない)
    }

    private static readonly List<(Texture2D Tex, Sprite Sp)> Sheets = new();
    private static readonly List<Pending> Queue = new();
    private static readonly List<GameObject> Plates = new(); // 焼いた床の板 (影の中の焼いた絵にも描き込むため)
    private static long _lastAddMs;
    private static Camera _cam;
#if ANDROID
    // 描き先 1 枚と、そこへ並べて描いたまとまり。埋まるか列が空になるまで、Flush をまたいで同じ描き先へ描き足す
    private sealed class InFlight
    {
        public RenderTexture Rt;
        public int RtW, RtH, Frame;               // Frame = 最後に描いたフレーム
        public readonly List<int[]> Free = new(); // 空いた四角 { x, y, w, h }
        public readonly List<Slot> Slots = new();
        public bool Lost;
        public Texture2D Tex;                     // 読み戻し中の絵 (帯ごとに読み足す)
        public int W, H, Row;                     // 読む範囲 (並べた範囲の右上まで) と、次に読む行
        public bool Done;                         // 全部読んだ (次の回に送り込む)
    }
    private sealed class Slot
    {
        public Group G;
        public int X, Y, W, H;
        public float Cx, Cy;
    }
    private const int AtlasPx = 1024; // 並べる描き先の縦横 (これより大きいまとまりは 1 枚で描く)
    private const int AtlasPad = 2;   // 隣のまとまりと離す画素 (絵の縁の補間で隣がにじまないように)
    private static readonly List<InFlight> Reading = new(); // 読み戻し待ちの描き先 (作った順)
    private static InFlight _open; // 描き足している途中の描き先 (まだ読まない)
    private static int _frame, _lastReadFrame = -100;
    private static long _lastTickMs;
#endif

    // TerrainFx から: 動きが止まった瓦礫
    public static void Add(Transform tr, SpriteRenderer sr, float z, bool ownSprite, Action<GameObject> onPlate = null)
    {
        Queue.Add(new Pending { Go = tr.gameObject, Sr = sr, Z = z, OwnSprite = ownSprite, OnPlate = onPlate });
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
        if (_lastReadFrame == _frame) return; // 読み戻したフレームには描かない (どちらも数 ms あり、重なると倍になる)
        if (!Enabled) _open = null; // 焼きを止めた間も、描いた分は読み終える
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
        public Action<GameObject> OnPlate;
    }

    // 溜まっている瓦礫を焼く (budget = 1 フレームの予算まで。残りは列に戻して次に回す)
    public static string Flush(bool budget = false)
    {
        if (Queue.Count == 0) return "nothing";
        if (!EnsureCamera())
        {
            Queue.Clear();
#if ANDROID
            _open = null;
#endif
            return "no camera";
        }
        var groups = new Dictionary<(int, int, int), Group>();
        int solo = 0;
        foreach (var p in Queue)
        {
            if (!p.Go) continue;
            var b = p.Sr.bounds;
            Vector3 lo = b.min, hi = b.max;
            var key = p.OnPlate != null ? (ZKey(p.Z), int.MinValue, ++solo)
                : (ZKey(p.Z), (int)MathF.Floor((lo.x + hi.x) * 0.5f / Cell), (int)MathF.Floor((lo.y + hi.y) * 0.5f / Cell));
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = new Group { Z = p.Z, OnPlate = p.OnPlate };
            g.Items.Add(p);
            g.X0 = Math.Min(g.X0, lo.x); g.Y0 = Math.Min(g.Y0, lo.y);
            g.X1 = Math.Max(g.X1, hi.x); g.Y1 = Math.Max(g.Y1, hi.y);
        }
        Queue.Clear();

        int sheets = 0, items = 0;
        long firstDone = 0;
        var order = new List<Group>(groups.Values);
        order.Sort((a, b) => (b.Y1 - b.Y0).CompareTo(a.Y1 - a.Y0)); // 高い順 (描き先に並べた時の空きを減らす)
        foreach (var g in order)
        {
            if (budget && firstDone != 0 && (System.Diagnostics.Stopwatch.GetTimestamp() - firstDone) * 1000.0 / System.Diagnostics.Stopwatch.Frequency > ExtraBudgetMs)
            {
                Queue.AddRange(g.Items);
                continue;
            }
            g.Layers = new int[g.Items.Count];
            for (int i = 0; i < g.Items.Count; i++) g.Layers[i] = g.Items[i].Go.layer;
#if ANDROID
            if (!Begin(g)) continue;
            sheets++;
            items += g.Items.Count;
            if (firstDone == 0) firstDone = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        if (Queue.Count == 0 || !budget) _open = null; // 描き終えたので読める (残りがあれば次のフレームも同じ描き先へ描く)
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

    // まとまりを描き先の (x, y, w, h) へ描く (瓦礫は描く間だけ焼く層へ移す。描き先の消去は呼んだ側)
    private static void Draw(Group g, RenderTexture rt, int x, int y, int w, int h, float cx, float cy)
    {
        foreach (var p in g.Items) if (p.Go) p.Go.layer = BakeLayer;
        try
        {
            _cam.transform.position = new Vector3(cx, cy, g.Z - 5f);
            _cam.targetTexture = rt;
            _cam.pixelRect = new Rect(x, y, w, h);
            _cam.orthographicSize = h * 0.5f / Ppu;
            _cam.aspect = w / (float)h;
            _cam.Render();
        }
        finally
        {
            _cam.targetTexture = null;
#if ANDROID
            RestoreLayers(g); // 読み戻すまで見せておく
#endif
        }
    }

    private static void ClearTarget(RenderTexture rt)
    {
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));
        RenderTexture.active = prev;
    }

    private static void RestoreLayers(Group g)
    {
        for (int i = 0; i < g.Items.Count; i++) if (g.Items[i].Go) g.Items[i].Go.layer = g.Layers[i];
    }

    // 焼いた絵 (の x, y, w, h の四角) を床の板として置く。ownTex = この板が絵を持つ (片付けで 1 回だけ消す)
    private static GameObject Place(Texture2D tex, int x, int y, int w, int h, float cx, float cy, float z, bool ownTex)
    {
        var sp = Sprite.Create(tex, new Rect(x, y, w, h), new Vector2(0.5f, 0.5f), Ppu, 0, SpriteMeshType.FullRect);
        sp.name = "MrpRubble";
        var go = new GameObject("MrpRubble");
        DamageMap.Track(go);
        go.transform.position = new Vector3(cx, cy, z);
        // 焼いた絵は世界の向きで撮ったので、左右反転の船の子でも裏返さない
        if (DamageMap.ShipMirrored) go.transform.localScale = new Vector3(-1f, 1f, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        if (DamageMap.PlateMaterial) sr.sharedMaterial = DamageMap.PlateMaterial;
        sr.sprite = sp;
        Sheets.Add((ownTex ? tex : null, sp));
        Plates.Add(go);
        return go;
    }

#if ANDROID
    private static InFlight NewTarget(int w, int h)
    {
        var f = new InFlight { Rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32), RtW = w, RtH = h, Frame = _frame };
        f.Free.Add(new[] { 0, 0, w + AtlasPad, h + AtlasPad }); // 右と上の端は隙間が要らない
        ClearTarget(f.Rt);
        Reading.Add(f);
        return f;
    }

    // 空いた四角のうち、置いた上端が一番低く (同じなら余りが一番少ない) 所の左下へ置き、残りを 2 つの四角に分ける
    private static bool TryPlace(List<int[]> free, int w, int h, out int x, out int y)
    {
        int pw = w + AtlasPad, ph = h + AtlasPad;
        int best = -1, bestTop = int.MaxValue;
        long bestSpare = long.MaxValue;
        for (int i = 0; i < free.Count; i++)
        {
            var r = free[i];
            if (r[2] < pw || r[3] < ph) continue;
            int top = r[1] + ph;
            long spare = (long)r[2] * r[3] - (long)pw * ph;
            if (top < bestTop || (top == bestTop && spare < bestSpare)) { best = i; bestTop = top; bestSpare = spare; }
        }
        x = y = 0;
        if (best < 0) return false;
        var b = free[best];
        free.RemoveAt(best);
        x = b[0]; y = b[1];
        // 余りの短い方の向きで切る (細長い切れ端を作らない)
        if (b[2] - pw < b[3] - ph)
        {
            if (b[2] > pw) free.Add(new[] { b[0] + pw, b[1], b[2] - pw, ph });
            if (b[3] > ph) free.Add(new[] { b[0], b[1] + ph, b[2], b[3] - ph });
        }
        else
        {
            if (b[2] > pw) free.Add(new[] { b[0] + pw, b[1], b[2] - pw, b[3] });
            if (b[3] > ph) free.Add(new[] { b[0], b[1] + ph, pw, b[3] - ph });
        }
        return true;
    }

    // まとまりを描く。普通の大きさは描き足している描き先に並べ、大きい物は 1 枚で描く
    private static bool Begin(Group g)
    {
        Frame(g, out int w, out int h, out float cx, out float cy);
        int x = 0, y = 0;
        try
        {
            InFlight f;
            if (w > AtlasPx || h > AtlasPx) f = NewTarget(w, h);
            else
            {
                if (_open == null || !TryPlace(_open.Free, w, h, out x, out y))
                {
                    _open = NewTarget(AtlasPx, AtlasPx);
                    TryPlace(_open.Free, w, h, out x, out y);
                }
                f = _open;
            }
            Draw(g, f.Rt, x, y, w, h, cx, cy);
            f.Frame = _frame;
            f.Slots.Add(new Slot { G = g, X = x, Y = y, W = w, H = h, Cx = cx, Cy = cy });
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"rubble bake {w}x{h} failed: {e.Message}");
            return false; // 焼けなかった瓦礫は消さずに残す (元の層へは Draw が戻している)
        }
    }

    private static bool Alive(Group g)
    {
        foreach (var p in g.Items) if (p.Go) return true;
        return false;
    }

    // ReadDelay フレーム経った描き先を帯ごとに読み戻し、全部読んだら次の回に送り込んで (Apply) 床の板にし、瓦礫を消す。
    // budget = 1 フレームに帯 1 本か送り込み 1 回。false = 全部その場で
    private static void ReadBack(bool budget)
    {
        while (Reading.Count > 0)
        {
            var f = Reading[0];
            if (f == _open) return; // まだ描き足している途中
            if (budget && _frame - f.Frame < ReadDelay) return;
            if (f.Done)
            {
                Reading.RemoveAt(0);
                Finish(f);
                _lastReadFrame = _frame;
                if (budget) return;
                continue;
            }
            if (f.Tex == null)
            {
                bool any = false;
                foreach (var s in f.Slots)
                {
                    any |= Alive(s.G);
                    f.W = Math.Max(f.W, s.X + s.W); f.H = Math.Max(f.H, s.Y + s.H);
                }
                if (!any) // 待つ間に瓦礫が全部片付けられた (板を置くと消えた瓦礫が残る)
                {
                    Reading.RemoveAt(0);
                    RenderTexture.ReleaseTemporary(f.Rt);
                    continue;
                }
                f.Tex = new Texture2D(f.W, f.H, TextureFormat.RGBA32, false) { name = "MrpRubble", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            }
            int rows = budget ? Math.Clamp(BandPx / f.W, 1, f.H - f.Row) : f.H - f.Row;
            var prev = RenderTexture.active;
            try
            {
                if (f.Lost)
                {
                    ClearTarget(f.Rt);
                    foreach (var s in f.Slots) Draw(s.G, f.Rt, s.X, s.Y, s.W, s.H, s.Cx, s.Cy);
                    f.Lost = false;
                }
                RenderTexture.active = f.Rt;
                f.Tex.ReadPixels(new Rect(0f, f.Row, f.W, rows), 0, f.Row, false);
                RenderTexture.active = prev;
                f.Row += rows;
            }
            catch (Exception e)
            {
                RenderTexture.active = prev;
                Plugin.Logger.LogWarning($"rubble bake {f.W}x{f.H} failed: {e.Message}");
                Reading.RemoveAt(0);
                Drop(f);
                continue; // 焼けなかった瓦礫は消さずに残す
            }
            _lastReadFrame = _frame;
            if (f.Row >= f.H)
            {
                RenderTexture.ReleaseTemporary(f.Rt);
                f.Rt = null;
                f.Done = true;
            }
            if (budget) return; // 送り込みは次の回
        }
    }

    // 読み終えた絵を送り込んで、まとまりごとに床の板を置く
    private static void Finish(InFlight f)
    {
        var tex = f.Tex;
        f.Tex = null;
        try { tex.Apply(false, true); } // CPU 側の写しは捨てる
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"rubble bake {f.W}x{f.H} failed: {e.Message}");
            UnityEngine.Object.Destroy(tex);
            return;
        }
        bool own = true;
        foreach (var s in f.Slots)
        {
            if (!Alive(s.G)) continue;
            try
            {
                var plate = Place(tex, s.X, s.Y, s.W, s.H, s.Cx, s.Cy, s.G.Z, own);
                own = false;
                s.G.OnPlate?.Invoke(plate);
                BakedCount += DestroyItems(s.G);
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"rubble bake {s.W}x{s.H} failed: {e.Message}"); }
        }
        if (own) UnityEngine.Object.Destroy(tex);
        else SheetBytes += (long)f.W * f.H * 4;
    }

    private static void Drop(InFlight f)
    {
        if (f.Rt) RenderTexture.ReleaseTemporary(f.Rt);
        if (f.Tex) UnityEngine.Object.Destroy(f.Tex);
        f.Rt = null;
        f.Tex = null;
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
            ClearTarget(rt);
            Draw(g, rt, 0, 0, w, h, cx, cy);
            tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "MrpRubble", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            // 読み戻し (GPU を待って 1 枚 3〜4ms) をせず、GPU の中で描き先から写す。先に空の絵を送って CPU 側の写しを捨てておく (後で Apply すると写した絵が空で上書きされる)
            tex.Apply(false, true);
            Graphics.CopyTexture(rt, 0, 0, tex, 0, 0);
            var plate = Place(tex, 0, 0, w, h, cx, cy, g.Z, ownTex: true);
            SheetBytes += (long)w * h * 4;
            g.OnPlate?.Invoke(plate);
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
        foreach (var f in Reading) foreach (var s in f.Slots) foreach (var p in s.G.Items) if (p.Go) into.Add(p.Go);
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
        foreach (var f in Reading) Drop(f);
        Reading.Clear();
        _open = null;
#endif
        BakedCount = 0;
        SheetBytes = 0;
    }
}
