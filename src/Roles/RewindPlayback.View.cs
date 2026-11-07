using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Fx;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 試合の世界に出す mod の絵 (演出・弾・破片・印) の置き場。ここの下に作った SpriteRenderer は、時戻しの逆再生で
// 見えていたとおりに逆へ流れる (役職ごとに逆再生を書かなくてよい)。船の子で、船の位置・倍率 (反転も) は今までの SetParent(ship, true) と同じ。
// 逆再生の間は根ごと SetActive(false) になるので、ここに置く物は自前の Tick で動かす (コルーチン・Animator・OnEnable に頼らない)
internal static class FxRoot
{
    private static Transform _root;
    private static int _gen = -1;

    internal static Transform Get()
    {
        if (_gen == Terrain.GameClock.ShipGen && _root) return _root;
        var ship = ShipStatus.Instance;
        if (!ship) return null;
        var go = new GameObject("MrpFxRoot");
        _root = go.transform;
        _root.SetParent(ship.transform, false);
        _gen = Terrain.GameClock.ShipGen;
        return _root;
    }

    // 演出の絵の入れ物を作る (世界の絵は new GameObject でなくこれで作る)。parent を渡さなければ根の下に作るので、それだけで逆再生に入る。
    // 世界の位置と大きさは Attach と同じく世界の値のまま (船の倍率は打ち消される)。船が無い時は場面の根に作る
    internal static GameObject New(string name, int layer = 0, Transform parent = null)
    {
        var go = new GameObject(name) { layer = layer };
        var p = parent ? parent : Get();
        if (p) go.transform.SetParent(p, true);
        return go;
    }

    // 作った絵をここへ移す (世界の位置はそのまま)。船が無い時は何もしない
    internal static void Attach(GameObject go)
    {
        var r = Get();
        if (r) go.transform.SetParent(r, true);
    }

    internal static bool Exists => _gen == Terrain.GameClock.ShipGen && _root;
    internal static Transform Current => Exists ? _root : null;

    // 船の外 (世界の座標そのまま) に置く物の置き場。頂点を世界の座標で書くメッシュ・使い回す粒子など。
    // 場面が替わると中身ごと消える (持ち主は Unity の null で作り直す)
    private static Transform _world;

    internal static void AttachWorld(GameObject go)
    {
        if (!_world)
        {
            _world = new GameObject("MrpFxWorld").transform;
        }
        go.transform.SetParent(_world, true);
    }

    internal static Transform CurrentWorld => _world ? _world : null;

    // 根の下に置けない場に残る物 (物理の体を持つ物・本編の部品を複製した物など) を、置いたまま逆再生に入れる。
    // 根と同じく控え、逆再生の間はその物を隠して控えを置く。消えた物は控えの時に外れる。
    // 戻し先より後に出した物は隠さないので、持ち主が Rewind.Undo で消す
    internal static void TrackWorld(GameObject go)
    {
        if (!go) return;
        var t = go.transform;
        for (int i = WorldObjs.Count - 1; i >= 0; i--)
        {
            if (!WorldObjs[i]) WorldObjs.RemoveAt(i);
            else if (WorldObjs[i].Pointer == t.Pointer) return;
        }
        WorldObjs.Add(t);
        RewindPlayback.HideWhilePlaying(t); // 逆再生の途中で出し直した物 (取り消しで戻った物) は、控えの絵と二重にならないよう隠しておく
    }

    internal static readonly List<Transform> WorldObjs = new();

    // TrackWorld から外す (使い回す物を池へ返す時に。逆再生の間に隠していた物は戻し終わりに出さない)
    internal static void Untrack(GameObject go)
    {
        if (!go) return;
        var p = go.transform.Pointer;
        for (int i = WorldObjs.Count - 1; i >= 0; i--)
            if (!WorldObjs[i] || WorldObjs[i].Pointer == p) WorldObjs.RemoveAt(i);
        RewindPlayback.ForgetHidden(p);
    }

    // 毎フレーム頂点を書き直すメッシュを登録する (v と c は持ち主がメッシュへ書き込む元の配列)。
    // 登録したメッシュは控えの時に頂点と色を写すので、逆再生でその時の形で出る
    internal static void Track(Mesh mesh, Il2CppStructArray<Vector3> v, Il2CppStructArray<Color32> c)
    {
        if (!mesh || v == null || c == null) return;
        RewindPlayback.TrackMesh(mesh, v, c);
    }

    // 画面 (HUD) に出す mod の板の根を登録する (本編の HudManager の子)。下の絵と字は HUD の中の位置で控え、
    // 逆再生の間は根を隠して控えのとおりに出す。戻し終わりに根は戻すので、持ち主は戻った値から出し直す
    internal static void TrackHud(Transform root)
    {
        if (!root) return;
        for (int i = HudRoots.Count - 1; i >= 0; i--)
        {
            if (!HudRoots[i]) HudRoots.RemoveAt(i);
            else if (HudRoots[i].Pointer == root.Pointer) return;
        }
        HudRoots.Add(root);
        RewindPlayback.HideHudWhilePlaying(root); // 逆再生の途中で出した板は、控えの絵と二重にならないよう戻し終わりまで隠す
    }

    internal static readonly List<Transform> HudRoots = new();

    // 画面 (HUD) の板の入れ物を作る。画面の板は new GameObject + SetParent(hud.transform) でなくこれで作る。
    // HudManager の子に作って TrackHud に入れるので、それだけで逆再生の間は見えていたとおりに逆へ流れる (役職ごとに逆再生を書かなくてよい)。
    // 逆再生の間は根ごと SetActive(false) になり、戻し終わりに戻る (持ち主は戻った値から出し直す)。HUD が無い時は null
    internal static GameObject NewHud(string name, int layer = -1)
    {
        var hud = Vanilla.Hud;
        if (!hud) return null;
        var go = new GameObject(name) { layer = layer >= 0 ? layer : hud.gameObject.layer };
        go.transform.SetParent(hud.transform, false);
        TrackHud(go.transform);
        return go;
    }

    // 本編の部品を写して画面に置く (Instantiate(src, hud.transform) の代わり)。写した物は TrackHud に入る
    internal static GameObject CloneHud(GameObject src)
    {
        var hud = Vanilla.Hud;
        if (!hud || !src) return null;
        var go = Object.Instantiate(src, hud.transform);
        TrackHud(go.transform);
        return go;
    }

    // 時刻で描く画面の板の入れ物を作る (事前に描いたコマを流す全画面など、板の中身のテクスチャが毎コマ変わるので控えの絵では逆に流せない物)。
    // 控えには入れず隠しもしない。逆再生の間は記録の時刻 at (Environment.TickCount64 の値) ごとに seek(at) を呼ぶので、持ち主がその時刻の姿を描く
    // (その時刻にまだ無かったなら自分で隠す)。逆再生が終わると end() を呼ぶ。HUD が無い時は null
    internal static GameObject NewHudClock(string name, Action<long> seek, Action end, int layer = -1)
    {
        var hud = Vanilla.Hud;
        if (!hud || seek == null || end == null) return null;
        var go = new GameObject(name) { layer = layer >= 0 ? layer : hud.gameObject.layer };
        go.transform.SetParent(hud.transform, false);
        RewindPlayback.AddClock(go, seek, end);
        return go;
    }

    // 本物のカメラの子に置く画面いっぱいの世界の層の板 (暗がり・閃光)。HUD はカメラの子なので、HUD からの相対で控えて同じ所へ出せる。
    // HUD がカメラの子でない時は控えに入れない (置き場所が合わない)
    internal static GameObject NewOnCamera(string name, Camera cam, int layer = -1)
    {
        if (!cam) return null;
        var go = new GameObject(name) { layer = layer >= 0 ? layer : cam.gameObject.layer };
        go.transform.SetParent(cam.transform, false);
        var hud = Vanilla.Hud;
        if (hud && hud.transform.IsChildOf(cam.transform)) TrackHud(go.transform);
        return go;
    }
}

// 録画: FxRoot の下の SpriteRenderer と MeshRenderer を姿と同じ 0.1 秒ごとに控え、逆再生の間は根を隠して代わりの板を控えのとおりに置く。
// メッシュは同じ Mesh を指すだけで頂点は写さない (一度作って動かさない片・跡の型)。毎フレーム頂点を書き直すメッシュは、控えた時の形でなく今の形で出る
internal static partial class RewindPlayback
{
    private const int MaxPerSlot = 224;          // 1 回の控えで 224 枚まで (超えた分は控えない)
    private const int WorldMax = 160;            // そのうち世界の絵は 160 枚まで (残りは HUD の分)
    private const int ViewCap = Slots * MaxPerSlot; // 控えの輪と同じ数だけ持つので、生きている控えが上書きされない

    private struct Shot
    {
        public IntPtr Id;            // 絵の持ち主 (同じ絵を前後の控えで照らし合わせる)
        public float X, Y, Z, Rot, Sx, Sy;
        public Color Col;
        public int Sprite, Mat;      // SpriteCache/MatCache の番号
        public int Order, Layer;
        public byte Flags;           // 1 = flipX・2 = flipY
        public bool Mesh;            // true = MeshRenderer (Sprite は MeshCache の番号・色は材質のまま)
        public bool Hud;             // true = HUD の中の位置 (HudManager からの相対)
        public int Text;             // 字 (TextMeshPro) なら TextCache の番号・それ以外は -1
        public int DynN;             // 写した頂点の数 (0 = 写していない・Mesh をそのまま出す)
        public long DynAt;           // 写した頂点の DynBuf の通し位置
    }

    // 毎フレーム頂点を書き直すメッシュ (FxRoot.Track で登録) の頂点と色の写し。輪で使い、上書きされた控えは写し無しとして扱う
    private sealed class Dyn
    {
        public Mesh Mesh;
        public Il2CppStructArray<Vector3> V;
        public Il2CppStructArray<Color32> C;
        // 逆再生で出す自前のメッシュ (三角形は元から 1 回だけ写す)
        public Mesh Own;
        public Il2CppStructArray<Vector3> OwnV;
        public Il2CppStructArray<Color32> OwnC;
    }

    private const int DynBytes = 4 << 20;   // 写しの輪の大きさ (居合の線 1 回 = 約 14KB)
    private const int VertBytes = 16;        // 位置 12 + 色 4
    private static byte[] _dynBuf;
    private static long _dynAt;              // 次に書く通し位置
    private static readonly Dictionary<IntPtr, Dyn> Dyns = new();

    internal static void TrackMesh(Mesh mesh, Il2CppStructArray<Vector3> v, Il2CppStructArray<Color32> c)
    {
        var p = mesh.Pointer;
        if (Dyns.TryGetValue(p, out var d) && d.Mesh && d.V == v) return;
        if (d != null && d.Own) Object.Destroy(d.Own);
        Dyns[p] = new Dyn { Mesh = mesh, V = v, C = c };
    }

    private static unsafe void CopyDyn(ref Shot e, Dyn d)
    {
        if (!d.Mesh) return;
        int n = (int)Math.Min(d.V.Length, d.C.Length);
        int bytes = n * VertBytes;
        if (n <= 0 || bytes > DynBytes / 4) return;
        _dynBuf ??= new byte[DynBytes];
        int off = (int)(_dynAt % DynBytes);
        if (off + bytes > DynBytes) { _dynAt += DynBytes - off; off = 0; } // 端で割れないよう先頭へ
        fixed (byte* dst = &_dynBuf[off])
        {
            byte* vs = (byte*)d.V.Pointer + 4 * IntPtr.Size;
            byte* cs = (byte*)d.C.Pointer + 4 * IntPtr.Size;
            Buffer.MemoryCopy(vs, dst, bytes, n * 12);
            Buffer.MemoryCopy(cs, dst + n * 12, bytes - n * 12, n * 4);
        }
        e.DynAt = _dynAt;
        e.DynN = n;
        DynShots++;
        _dynAt += bytes;
    }

    // 写しがまだ輪に残っているか
    private static bool DynAlive(in Shot e) => e.DynN > 0 && _dynBuf != null && e.DynAt >= _dynAt - DynBytes + (long)e.DynN * VertBytes;

    // 写しを自前のメッシュへ書く。b があれば (同じ頂点数なら) u で補間する
    private static unsafe Mesh DynMesh(Dyn d, in Shot a, in Shot b, bool lerp, float u)
    {
        int n = a.DynN;
        if (!d.Own || d.OwnV == null || d.OwnV.Length != n)
        {
            if (!d.Mesh) return null;
            if (d.Own) Object.Destroy(d.Own);
            d.OwnV = new Il2CppStructArray<Vector3>((long)n);
            d.OwnC = new Il2CppStructArray<Color32>((long)n);
            d.Own = new Mesh { name = "MrpRewindDyn" };
            d.Own.vertices = d.OwnV;
            d.Own.colors32 = d.OwnC;
            d.Own.triangles = d.Mesh.triangles;
            d.Own.bounds = new Bounds(Vector3.zero, FxMath.V3(1000f, 1000f, 10f));
        }
        float* vp = (float*)((byte*)d.OwnV.Pointer + 4 * IntPtr.Size);
        byte* cp = (byte*)d.OwnC.Pointer + 4 * IntPtr.Size;
        fixed (byte* buf = _dynBuf)
        {
            float* av = (float*)(buf + (int)(a.DynAt % DynBytes));
            byte* ac = (byte*)av + n * 12;
            if (!lerp)
            {
                Buffer.MemoryCopy(av, vp, n * 12, n * 12);
                Buffer.MemoryCopy(ac, cp, n * 4, n * 4);
            }
            else
            {
                float* bv = (float*)(buf + (int)(b.DynAt % DynBytes));
                byte* bc = (byte*)bv + n * 12;
                for (int k = 0; k < n * 3; k++) vp[k] = av[k] + (bv[k] - av[k]) * u;
                for (int k = 0; k < n * 4; k++) cp[k] = (byte)(ac[k] + (bc[k] - ac[k]) * u + 0.5f);
            }
        }
        d.Own.vertices = d.OwnV;
        d.Own.colors32 = d.OwnC;
        DynDrawn++;
        return d.Own;
    }

    private static Shot[] Shots; // 時戻しのいる試合の最初の控えで作る (約 3MB)
    private static readonly int[] ViewStart = new int[Slots], ViewCount = new int[Slots];
    private static int _viewHead; // 次に書く位置 (輪)

    private static readonly List<Sprite> SpriteCache = new();
    private static readonly Dictionary<IntPtr, int> SpriteIndex = new();
    private static readonly List<Material> MatCache = new();
    private static readonly Dictionary<IntPtr, int> MatIndex = new();
    private static readonly List<Mesh> MeshCache = new();
    private static readonly Dictionary<IntPtr, int> MeshIndex = new();
    private static readonly Dictionary<IntPtr, MeshFilter> FilterOf = new(); // MeshRenderer → 同じ物の MeshFilter (GetComponent を毎回呼ばない)

    // 生まれた時刻 (前の控えに無く今の控えにある = 出てきた)。戻し終わりに、戻し先より後に生まれた絵を隠すのに使う
    private static readonly Dictionary<IntPtr, long> Born = new();
    private static readonly HashSet<IntPtr> SeenNow = new(), SeenPrev = new();

    // 逆再生の間の代わりの板
    private sealed class Proxy
    {
        public GameObject Go;
        public Transform Tf;
        public SpriteRenderer Sr;
        public Material Default;
        public int Sprite = -1, Mat = -1, Order = int.MinValue, Layer = -1;
        public byte Flags = 255;
    }

    private sealed class MeshProxy
    {
        public GameObject Go;
        public Transform Tf;
        public MeshFilter Mf;
        public MeshRenderer Mr;
        public int Mesh = -1, Mat = -1, Order = int.MinValue, Layer = -1;
    }

    private static readonly List<Proxy> Proxies = new();
    private static readonly List<MeshProxy> MeshProxies = new();
    private static Transform _proxyRoot;
    private static int _proxyGen = -1, _proxyUsed, _meshUsed;
    private static bool _rootHidden;
    private static readonly Dictionary<IntPtr, int> NewerIndex = new();

    internal static int ViewShots, ViewDropped, HudDropped, ViewHidden, DynShots, DynDrawn; // 確かめ用

    private static void ClearView()
    {
        StopView();
        _viewHead = 0;
        Array.Clear(ViewCount, 0, Slots);
        SpriteCache.Clear(); SpriteIndex.Clear();
        MatCache.Clear(); MatIndex.Clear();
        MeshCache.Clear(); MeshIndex.Clear(); FilterOf.Clear();
        TextCache.Clear(); TextIndex.Clear(); TextSrc.Clear(); IsTextMesh.Clear();
        Born.Clear(); SeenNow.Clear(); SeenPrev.Clear();
    }

    private static void CaptureView(int s, long now)
    {
        ViewCount[s] = 0;
        ViewStart[s] = _viewHead;
        Shots ??= new Shot[ViewCap];
        DynShots = 0;
        int n = 0;
        SeenNow.Clear();
        var root = FxRoot.Current;
        if (root) CaptureUnder(root, ref n, now);
        var world = FxRoot.CurrentWorld;
        if (world) CaptureUnder(world, ref n, now);
        for (int i = FxRoot.WorldObjs.Count - 1; i >= 0; i--)
        {
            var t = FxRoot.WorldObjs[i];
            if (!t) { FxRoot.WorldObjs.RemoveAt(i); continue; }
            if (t.gameObject.activeInHierarchy) CaptureUnder(t, ref n, now);
        }
        if (n >= WorldMax) ViewDropped++; // 上限で切った回数
        CaptureHud(ref n);
        if (n >= MaxPerSlot) HudDropped++; // 画面の板が上限で切れた回数 (世界の絵が多いと画面の分が減る)
        ViewCount[s] = n;
        _viewHead = (_viewHead + n) % ViewCap;
        ViewShots = n;
        SeenPrev.Clear();
        foreach (var id in SeenNow) SeenPrev.Add(id);
        // 今回見なかった物の生まれた時刻は捨てる (番地が別の物に使い回された時に古い時刻を引き継がない)
        if (Born.Count > SeenNow.Count)
        {
            Gone.Clear();
            foreach (var kv in Born) if (!SeenNow.Contains(kv.Key)) Gone.Add(kv.Key);
            foreach (var id in Gone) Born.Remove(id);
        }
        // 消えた MeshRenderer の控えも捨てる (破片を出すたびに増えないように)
        if (FilterOf.Count > SeenNow.Count + 32)
        {
            Gone.Clear();
            foreach (var kv in FilterOf) if (!SeenNow.Contains(kv.Key)) Gone.Add(kv.Key);
            foreach (var id in Gone) FilterOf.Remove(id);
        }
        if (IsTextMesh.Count > 512) IsTextMesh.Clear();
        if (TextSrc.Count > 256)
        {
            Gone.Clear();
            foreach (var kv in TextSrc) if (!kv.Value) Gone.Add(kv.Key);
            foreach (var id in Gone) TextSrc.Remove(id);
        }
        // 持ち主が捨てたメッシュの登録を外す (自前のメッシュも捨てる)
        if (Dyns.Count > 0 && ++_dynSweep >= 50)
        {
            _dynSweep = 0;
            Gone.Clear();
            foreach (var kv in Dyns)
                if (!kv.Value.Mesh) { if (kv.Value.Own) Object.Destroy(kv.Value.Own); Gone.Add(kv.Key); }
            foreach (var id in Gone) Dyns.Remove(id);
        }
    }

    private static int _dynSweep;

    private static void CaptureUnder(Transform root, ref int n, long now)
    {
        var all = root.GetComponentsInChildren<SpriteRenderer>(false);
        for (int i = 0; i < all.Count && n < WorldMax; i++)
        {
            var sr = all[i];
            if (!sr || !sr.enabled) continue;
            var sp = sr.sprite;
            if (!sp) continue;
            var c = sr.color;
            if (c.a <= 0.003f) continue;
            ref var e = ref Shots[(_viewHead + n) % ViewCap];
            Pose(ref e, sr.transform.localToWorldMatrix);
            e.Id = sr.Pointer;
            e.Col = c;
            e.Sprite = Intern(sp, SpriteCache, SpriteIndex);
            var mat = sr.sharedMaterial;
            e.Mat = mat ? Intern(mat, MatCache, MatIndex) : -1;
            e.Order = sr.sortingOrder;
            e.Layer = sr.gameObject.layer;
            e.Flags = (byte)((sr.flipX ? 1 : 0) | (sr.flipY ? 2 : 0));
            e.Mesh = false;
            e.Hud = false;
            e.Text = -1;
            SeenNow.Add(e.Id);
            if (!SeenPrev.Contains(e.Id)) Born[e.Id] = now;
            n++;
        }
        var meshes = root.GetComponentsInChildren<MeshRenderer>(false);
        for (int i = 0; i < meshes.Count && n < WorldMax; i++)
        {
            var mr = meshes[i];
            if (!mr || !mr.enabled) continue;
            var id = mr.Pointer;
            if (!FilterOf.TryGetValue(id, out var mf) || !mf)
            {
                mf = mr.GetComponent<MeshFilter>();
                FilterOf[id] = mf;
            }
            if (!mf) continue;
            var mesh = mf.sharedMesh;
            if (!mesh) continue;
            var mat = mr.sharedMaterial;
            if (!mat) continue;
            ref var e = ref Shots[(_viewHead + n) % ViewCap];
            Pose(ref e, mr.transform.localToWorldMatrix);
            e.Id = id;
            e.Col = Color.white;
            e.Sprite = Intern(mesh, MeshCache, MeshIndex);
            e.Mat = Intern(mat, MatCache, MatIndex);
            e.Order = mr.sortingOrder;
            e.Layer = mr.gameObject.layer;
            e.Flags = 0;
            e.Mesh = true;
            e.Hud = false;
            e.Text = -1;
            e.DynN = 0;
            if (Dyns.Count > 0 && Dyns.TryGetValue(mesh.Pointer, out var d)) CopyDyn(ref e, d);
            SeenNow.Add(id);
            if (!SeenPrev.Contains(id)) Born[id] = now;
            n++;
        }
    }

    // ---- HUD ----

    private static readonly List<string> TextCache = new();
    private static readonly Dictionary<string, int> TextIndex = new();
    private static readonly Dictionary<IntPtr, TextMeshPro> TextSrc = new();   // 字の持ち主 (代わりの字を複製する元)
    private static readonly Dictionary<IntPtr, bool> IsTextMesh = new();       // MeshRenderer が字の物か (字は文字列で控える)
    private static readonly List<Parked> HudHidden = new(); // 逆再生の間に隠した HUD の根と元の親

    // HUD の根の下の絵と字を HudManager からの相対の位置で控える。戻し終わりに隠すこと (HideBornAfter) はしない
    // (HUD は持ち主が戻った値から出し直す・切り替えで出し入れする題字を隠すと戻らなくなる)
    private static void CaptureHud(ref int n)
    {
        var roots = FxRoot.HudRoots;
        if (roots.Count == 0) return;
        var hud = Vanilla.Hud;
        if (!hud) return;
        Matrix4x4 inv = default;
        bool got = false;
        for (int r = 0; r < roots.Count; r++)
        {
            var root = roots[r];
            if (!root || !root.gameObject.activeInHierarchy) continue;
            if (!got) { inv = hud.transform.worldToLocalMatrix; got = true; }
            var srs = root.GetComponentsInChildren<SpriteRenderer>(false);
            for (int i = 0; i < srs.Count && n < MaxPerSlot; i++)
            {
                var sr = srs[i];
                if (!sr || !sr.enabled) continue;
                var sp = sr.sprite;
                if (!sp) continue;
                var c = sr.color;
                if (c.a <= 0.003f) continue;
                ref var e = ref Shots[(_viewHead + n) % ViewCap];
                PoseRel(ref e, inv, sr.transform.localToWorldMatrix);
                e.Id = sr.Pointer;
                e.Col = c;
                e.Sprite = Intern(sp, SpriteCache, SpriteIndex);
                var mat = sr.sharedMaterial;
                e.Mat = mat ? Intern(mat, MatCache, MatIndex) : -1;
                e.Order = sr.sortingOrder;
                e.Layer = sr.gameObject.layer;
                e.Flags = (byte)((sr.flipX ? 1 : 0) | (sr.flipY ? 2 : 0));
                e.Mesh = false;
                e.Hud = true;
                e.Text = -1;
                n++;
            }
            var mrs = root.GetComponentsInChildren<MeshRenderer>(false);
            for (int i = 0; i < mrs.Count && n < MaxPerSlot; i++)
            {
                var mr = mrs[i];
                if (!mr || !mr.enabled) continue;
                var id = mr.Pointer;
                if (!IsTextMesh.TryGetValue(id, out bool text))
                {
                    text = mr.GetComponent<TMP_Text>() || mr.GetComponent<TMP_SubMesh>();
                    IsTextMesh[id] = text;
                }
                if (text) continue;
                if (!FilterOf.TryGetValue(id, out var mf) || !mf)
                {
                    mf = mr.GetComponent<MeshFilter>();
                    FilterOf[id] = mf;
                }
                if (!mf) continue;
                var mesh = mf.sharedMesh;
                var mat = mr.sharedMaterial;
                if (!mesh || !mat) continue;
                ref var e = ref Shots[(_viewHead + n) % ViewCap];
                PoseRel(ref e, inv, mr.transform.localToWorldMatrix);
                e.Id = id;
                e.Col = Color.white;
                e.Sprite = Intern(mesh, MeshCache, MeshIndex);
                e.Mat = Intern(mat, MatCache, MatIndex);
                e.Order = mr.sortingOrder;
                e.Layer = mr.gameObject.layer;
                e.Flags = 0;
                e.Mesh = true;
                e.Hud = true;
                e.Text = -1;
                e.DynN = 0;
                n++;
            }
            var tmps = root.GetComponentsInChildren<TextMeshPro>(false);
            for (int i = 0; i < tmps.Count && n < MaxPerSlot; i++)
            {
                var tmp = tmps[i];
                if (!tmp || !tmp.enabled) continue;
                var c = tmp.color;
                if (c.a <= 0.003f) continue;
                var str = tmp.text;
                if (string.IsNullOrEmpty(str)) continue;
                if (!TextIndex.TryGetValue(str, out int k))
                {
                    if (TextCache.Count >= 1024) continue; // 毎回違う字を出す物で膨らませない
                    k = TextCache.Count;
                    TextCache.Add(str);
                    TextIndex[str] = k;
                }
                ref var e = ref Shots[(_viewHead + n) % ViewCap];
                PoseRel(ref e, inv, tmp.transform.localToWorldMatrix);
                e.Id = tmp.Pointer;
                TextSrc[e.Id] = tmp;
                e.Col = c;
                e.Sprite = e.Mat = -1;
                e.Order = 0;
                e.Layer = tmp.gameObject.layer;
                e.Flags = 0;
                e.Mesh = false;
                e.Hud = true;
                e.Text = k;
                e.DynN = 0;
                n++;
            }
        }
    }

    // a (HUD の逆) × b (絵の行列) を、Unity の行列の掛け算 (interop) を使わずに計算して姿を取り出す
    private static void PoseRel(ref Shot e, in Matrix4x4 a, in Matrix4x4 b)
    {
        Matrix4x4 m = default;
        m.m00 = a.m00 * b.m00 + a.m01 * b.m10 + a.m02 * b.m20;
        m.m01 = a.m00 * b.m01 + a.m01 * b.m11 + a.m02 * b.m21;
        m.m10 = a.m10 * b.m00 + a.m11 * b.m10 + a.m12 * b.m20;
        m.m11 = a.m10 * b.m01 + a.m11 * b.m11 + a.m12 * b.m21;
        m.m03 = a.m00 * b.m03 + a.m01 * b.m13 + a.m02 * b.m23 + a.m03;
        m.m13 = a.m10 * b.m03 + a.m11 * b.m13 + a.m12 * b.m23 + a.m13;
        m.m23 = a.m20 * b.m03 + a.m21 * b.m13 + a.m22 * b.m23 + a.m23;
        Pose(ref e, m);
    }

    private sealed class TextProxy
    {
        public GameObject Go;
        public Transform Tf;
        public TextMeshPro Tmp;
        public int Text = -1;
        public bool Used;
    }

    private static readonly Dictionary<IntPtr, TextProxy> TextProxies = new();
    private static readonly List<Proxy> HudProxies = new();
    private static readonly List<MeshProxy> HudMeshProxies = new();
    private static Transform _hudProxyRoot;
    private static IntPtr _hudProxyOf;
    private static int _hudUsed, _hudMeshUsed;

    private static bool EnsureHudProxyRoot()
    {
        var hud = Vanilla.Hud;
        if (!hud) return false;
        if (_hudProxyRoot && _hudProxyOf == hud.Pointer) return true;
        if (_hudProxyRoot) Object.Destroy(_hudProxyRoot.gameObject);
        HudProxies.Clear();
        HudMeshProxies.Clear();
        TextProxies.Clear();
        _hudUsed = _hudMeshUsed = 0;
        var go = new GameObject("MrpRewindHud") { layer = hud.gameObject.layer };
        _hudProxyRoot = go.transform;
        _hudProxyRoot.SetParent(hud.transform, false);
        _hudProxyOf = hud.Pointer;
        return true;
    }

    // 字の代わり: 持ち主を 1 回だけ複製する (書体・材質・縁取り・色の段がそろう)。字の部品でない子は捨てる
    private static TextProxy TextProxyOf(IntPtr id)
    {
        if (TextProxies.TryGetValue(id, out var t) && t.Go) return t;
        if (!TextSrc.TryGetValue(id, out var src) || !src) return null;
        var go = Object.Instantiate(src.gameObject, _hudProxyRoot);
        go.name = "MrpRewindText";
        var tf = go.transform;
        for (int i = tf.childCount - 1; i >= 0; i--)
        {
            var ch = tf.GetChild(i);
            if (!ch.GetComponent<TMP_SubMesh>()) Object.Destroy(ch.gameObject);
        }
        var tmp = go.GetComponent<TextMeshPro>();
        if (!tmp) { Object.Destroy(go); return null; }
        t = new TextProxy { Go = go, Tf = tf, Tmp = tmp };
        TextProxies[id] = t;
        return t;
    }

    private static readonly List<IntPtr> Gone = new();

    // 行列から位置・向き (z 回り)・大きさを取り出す。裏返し (行列式が負) は縦の大きさを負にして表す
    private static void Pose(ref Shot e, Matrix4x4 m)
    {
        e.X = m.m03; e.Y = m.m13; e.Z = m.m23;
        float sx = FxMath.Sqrt(m.m00 * m.m00 + m.m10 * m.m10), sy = FxMath.Sqrt(m.m01 * m.m01 + m.m11 * m.m11);
        if (m.m00 * m.m11 - m.m01 * m.m10 < 0f) sy = -sy;
        e.Sx = sx; e.Sy = sy;
        e.Rot = FxMath.Atan2(m.m10, m.m00) * (180f / MathF.PI);
    }

    private static int Intern<T>(T o, List<T> list, Dictionary<IntPtr, int> index) where T : Object
    {
        var p = o.Pointer;
        if (index.TryGetValue(p, out int k))
        {
            if (!list[k]) list[k] = o; // 破棄された物の番地が使い回された: 今の物に差し替える
            return k;
        }
        list.Add(o);
        index[p] = list.Count - 1;
        return list.Count - 1;
    }

    private static void BeginView()
    {
        var root = FxRoot.Current;
        if (root && root.gameObject.activeSelf) { root.gameObject.SetActive(false); _rootHidden = true; }
        var world = FxRoot.CurrentWorld;
        if (world && world.gameObject.activeSelf) { world.gameObject.SetActive(false); _worldHidden = true; }
        WorldObjHidden.Clear();
        foreach (var t in FxRoot.WorldObjs)
        {
            if (!t || !t.gameObject.activeSelf) continue;
            t.gameObject.SetActive(false);
            WorldObjHidden.Add(t);
        }
        HudHidden.Clear();
        foreach (var r in FxRoot.HudRoots) Park(r);
    }

    // HUD の根は SetActive で隠さず、止めた置き場の下へ一時的に移して隠す。逆再生の間に持ち主が自分で閉じた
    // (SetActive(false) した) 板を、戻し終わりにこちらが出し直してしまわないように (持ち主の出し入れはそのまま残る)
    private static Transform _park;
    private static IntPtr _parkOf;

    private static void Park(Transform r)
    {
        if (!r) return;
        var hud = Vanilla.Hud;
        if (!hud) return;
        if (!_park || _parkOf != hud.Pointer)
        {
            if (_park) Object.Destroy(_park.gameObject);
            var go = new GameObject("MrpRewindParked") { layer = hud.gameObject.layer };
            go.SetActive(false);
            _park = go.transform;
            _park.SetParent(hud.transform, false);
            _parkOf = hud.Pointer;
        }
        var parent = r.parent;
        if (!parent || parent.Pointer == _park.Pointer) return;
        HudHidden.Add(new Parked { T = r, Parent = parent });
        r.SetParent(_park, false);
    }

    private struct Parked { public Transform T, Parent; }

    // 時刻で描く画面 (FxRoot.NewHudClock)
    private struct Clock { public GameObject Go; public Action<long> Seek; public Action End; }
    private static readonly List<Clock> Clocks = new();

    internal static void AddClock(GameObject go, Action<long> seek, Action end)
    {
        for (int i = Clocks.Count - 1; i >= 0; i--) if (!Clocks[i].Go) Clocks.RemoveAt(i);
        Clocks.Add(new Clock { Go = go, Seek = seek, End = end });
    }

    private static void SeekClocks(long at)
    {
        for (int i = 0; i < Clocks.Count; i++)
        {
            var c = Clocks[i];
            if (!c.Go) continue;
            try { c.Seek(at); }
            catch (Exception e) { Plugin.Logger.LogWarning($"RewindPlayback clock seek: {e.Message}"); }
        }
    }

    private static void EndClocks()
    {
        for (int i = Clocks.Count - 1; i >= 0; i--)
        {
            var c = Clocks[i];
            if (!c.Go) { Clocks.RemoveAt(i); continue; }
            try { c.End(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"RewindPlayback clock end: {e.Message}"); }
        }
    }

    private static bool _worldHidden;

    internal static void ForgetHidden(IntPtr p)
    {
        for (int i = WorldObjHidden.Count - 1; i >= 0; i--)
            if (!WorldObjHidden[i] || WorldObjHidden[i].Pointer == p) WorldObjHidden.RemoveAt(i);
    }

    internal static void HideWhilePlaying(Transform t)
    {
        if (!Active) return;
        if (!t || !t.gameObject.activeSelf) return;
        t.gameObject.SetActive(false);
        WorldObjHidden.Add(t);
    }
    private static readonly List<Transform> WorldObjHidden = new();

    internal static void HideHudWhilePlaying(Transform t)
    {
        if (!Active) return;
        Park(t);
    }

    // 控え a (古い) と b (新しい) の間 u で置く。同じ絵が両方にあれば位置・向き・大きさ・色を補間する
    private static void DriveView(int a, int b, float u)
    {
        if (Shots == null || !EnsureProxyRoot()) return;
        NewerIndex.Clear();
        int bs = ViewStart[b], bn = ViewCount[b];
        if (b != a)
            for (int k = 0; k < bn; k++) NewerIndex[Shots[(bs + k) % ViewCap].Id] = (bs + k) % ViewCap;
        int s0 = ViewStart[a], n = ViewCount[a];
        int used = 0, usedMesh = 0, hudUsed = 0, hudMesh = 0;
        bool hudOk = FxRoot.HudRoots.Count > 0 && EnsureHudProxyRoot();
        foreach (var t in TextProxies.Values) t.Used = false;
        for (int k = 0; k < n; k++)
        {
            ref var e = ref Shots[(s0 + k) % ViewCap];
            if (e.Hud && !hudOk) continue;
            Sprite sp = null;
            Mesh mesh = null;
            if (e.Text >= 0)
            {
                // 字は下で置く (位置と色は絵と同じに補間する)
            }
            else if (e.Mesh)
            {
                if (e.Sprite < 0 || e.Sprite >= MeshCache.Count || e.Mat < 0) continue;
                mesh = MeshCache[e.Sprite];
                if (!mesh || !MatCache[e.Mat]) continue;
            }
            else
            {
                if (e.Sprite < 0 || e.Sprite >= SpriteCache.Count) continue;
                sp = SpriteCache[e.Sprite];
                if (!sp) continue;
            }
            float x = e.X, y = e.Y, rot = e.Rot, sx = e.Sx, sy = e.Sy;
            var col = e.Col;
            if (b != a && NewerIndex.TryGetValue(e.Id, out int j))
            {
                ref var f = ref Shots[j];
                x += (f.X - x) * u; y += (f.Y - y) * u;
                sx += (f.Sx - sx) * u; sy += (f.Sy - sy) * u;
                float dr = f.Rot - rot;
                if (dr > 180f) dr -= 360f; else if (dr < -180f) dr += 360f;
                rot += dr * u;
                col = FxMath.Rgba(col.r + (f.Col.r - col.r) * u, col.g + (f.Col.g - col.g) * u, col.b + (f.Col.b - col.b) * u, col.a + (f.Col.a - col.a) * u);
            }
            if (e.Text >= 0)
            {
                if (e.Text >= TextCache.Count) continue;
                var tp = TextProxyOf(e.Id);
                if (tp == null) continue;
                if (tp.Text != e.Text) { tp.Tmp.text = TextCache[e.Text]; tp.Text = e.Text; }
                tp.Tmp.color = col;
                tp.Tf.localPosition = FxMath.V3(x, y, e.Z);
                tp.Tf.localRotation = FxMath.RotZ(rot);
                tp.Tf.localScale = FxMath.V3(sx, sy, 1f);
                if (!tp.Go.activeSelf) tp.Go.SetActive(true);
                tp.Used = true;
                continue;
            }
            if (e.Mesh)
            {
                Mesh dyn = null;
                if (e.DynN > 0)
                {
                    // 頂点を写したメッシュ: 写しが輪から消えていれば出さない (今の形は戻し先の形ではない)
                    if (!DynAlive(e) || !Dyns.TryGetValue(mesh.Pointer, out var d)) continue;
                    var fb = e;
                    bool lerp = false;
                    if (b != a && NewerIndex.TryGetValue(e.Id, out int j2))
                    {
                        fb = Shots[j2];
                        lerp = fb.DynN == e.DynN && DynAlive(fb);
                    }
                    dyn = DynMesh(d, e, fb, lerp, u);
                    if (!dyn) continue;
                }
                var q = e.Hud ? MeshProxyAt(HudMeshProxies, _hudProxyRoot, hudMesh++) : MeshProxyAt(MeshProxies, _proxyRoot, usedMesh++);
                if (dyn) { q.Mf.sharedMesh = dyn; q.Mesh = int.MinValue; }
                else if (q.Mesh != e.Sprite) { q.Mf.sharedMesh = mesh; q.Mesh = e.Sprite; }
                if (q.Mat != e.Mat) { q.Mr.sharedMaterial = MatCache[e.Mat]; q.Mat = e.Mat; }
                if (q.Order != e.Order) { q.Mr.sortingOrder = e.Order; q.Order = e.Order; }
                if (q.Layer != e.Layer) { q.Go.layer = e.Layer; q.Layer = e.Layer; }
                Place(q.Tf, e.Hud, x, y, e.Z, rot, sx, sy);
                if (!q.Go.activeSelf) q.Go.SetActive(true);
                continue;
            }
            var p = e.Hud ? ProxyAt(HudProxies, _hudProxyRoot, hudUsed++) : ProxyAt(Proxies, _proxyRoot, used++);
            if (p.Sprite != e.Sprite) { p.Sr.sprite = sp; p.Sprite = e.Sprite; }
            if (p.Mat != e.Mat)
            {
                var m = e.Mat >= 0 ? MatCache[e.Mat] : null;
                p.Sr.sharedMaterial = m ? m : p.Default;
                p.Mat = e.Mat;
            }
            if (p.Order != e.Order) { p.Sr.sortingOrder = e.Order; p.Order = e.Order; }
            if (p.Layer != e.Layer) { p.Go.layer = e.Layer; p.Layer = e.Layer; }
            if (p.Flags != e.Flags) { p.Sr.flipX = (e.Flags & 1) != 0; p.Sr.flipY = (e.Flags & 2) != 0; p.Flags = e.Flags; }
            p.Sr.color = col;
            Place(p.Tf, e.Hud, x, y, e.Z, rot, sx, sy);
            if (!p.Go.activeSelf) p.Go.SetActive(true);
        }
        for (int k = hudUsed; k < _hudUsed; k++)
            if (HudProxies[k].Go) HudProxies[k].Go.SetActive(false);
        _hudUsed = hudUsed;
        for (int k = hudMesh; k < _hudMeshUsed; k++)
            if (HudMeshProxies[k].Go) HudMeshProxies[k].Go.SetActive(false);
        _hudMeshUsed = hudMesh;
        foreach (var t in TextProxies.Values)
            if (!t.Used && t.Go && t.Go.activeSelf) t.Go.SetActive(false);
        for (int k = used; k < _proxyUsed; k++)
            if (Proxies[k].Go) Proxies[k].Go.SetActive(false);
        _proxyUsed = used;
        for (int k = usedMesh; k < _meshUsed; k++)
            if (MeshProxies[k].Go) MeshProxies[k].Go.SetActive(false);
        _meshUsed = usedMesh;
    }

    private static bool EnsureProxyRoot()
    {
        if (_proxyGen == Terrain.GameClock.ShipGen && _proxyRoot) return true;
        if (_proxyRoot) Object.Destroy(_proxyRoot.gameObject); // 前の船の板 (船の子ではないので一緒に消えない)
        Proxies.Clear();
        MeshProxies.Clear();
        _proxyUsed = _meshUsed = 0;
        if (!ShipStatus.Instance) return false;
        // 世界の座標で直に置く (船の子にすると船の倍率が二重に掛かる)
        _proxyRoot = new GameObject("MrpRewindView").transform;
        _proxyGen = Terrain.GameClock.ShipGen;
        return true;
    }

    // 世界の板は世界の座標・HUD の板は HUD の中の位置
    private static void Place(Transform tf, bool hud, float x, float y, float z, float rot, float sx, float sy)
    {
        if (hud)
        {
            tf.localPosition = FxMath.V3(x, y, z);
            tf.localRotation = FxMath.RotZ(rot);
        }
        else
        {
            tf.position = FxMath.V3(x, y, z);
            tf.rotation = FxMath.RotZ(rot);
        }
        tf.localScale = FxMath.V3(sx, sy, 1f);
    }

    private static Proxy ProxyAt(List<Proxy> list, Transform parent, int i)
    {
        while (list.Count <= i) list.Add(null);
        var p = list[i];
        if (p == null || !p.Go)
        {
            var go = new GameObject("MrpRewindShot") { layer = parent.gameObject.layer };
            go.transform.SetParent(parent, false);
            var sr = go.AddComponent<SpriteRenderer>();
            p = list[i] = new Proxy { Go = go, Tf = go.transform, Sr = sr, Default = sr.sharedMaterial };
        }
        return p;
    }

    private static MeshProxy MeshProxyAt(List<MeshProxy> list, Transform parent, int i)
    {
        while (list.Count <= i) list.Add(null);
        var q = list[i];
        if (q == null || !q.Go)
        {
            var go = new GameObject("MrpRewindMesh") { layer = parent.gameObject.layer };
            go.transform.SetParent(parent, false);
            q = list[i] = new MeshProxy { Go = go, Tf = go.transform, Mf = go.AddComponent<MeshFilter>(), Mr = go.AddComponent<MeshRenderer>() };
        }
        return q;
    }

    private static void StopView()
    {
        for (int k = 0; k < _proxyUsed && k < Proxies.Count; k++)
            if (Proxies[k].Go) Proxies[k].Go.SetActive(false);
        _proxyUsed = 0;
        for (int k = 0; k < _meshUsed && k < MeshProxies.Count; k++)
            if (MeshProxies[k].Go) MeshProxies[k].Go.SetActive(false);
        _meshUsed = 0;
        if (_rootHidden)
        {
            _rootHidden = false;
            var root = FxRoot.Current;
            if (root) root.gameObject.SetActive(true);
        }
        if (_worldHidden)
        {
            _worldHidden = false;
            var world = FxRoot.CurrentWorld;
            if (world) world.gameObject.SetActive(true);
        }
        foreach (var t in WorldObjHidden)
            if (t) t.gameObject.SetActive(true);
        WorldObjHidden.Clear();
        for (int k = 0; k < _hudUsed && k < HudProxies.Count; k++)
            if (HudProxies[k].Go) HudProxies[k].Go.SetActive(false);
        _hudUsed = 0;
        for (int k = 0; k < _hudMeshUsed && k < HudMeshProxies.Count; k++)
            if (HudMeshProxies[k].Go) HudMeshProxies[k].Go.SetActive(false);
        _hudMeshUsed = 0;
        foreach (var t in TextProxies.Values)
            if (t.Go && t.Go.activeSelf) t.Go.SetActive(false);
        // 元の親へ戻す (出すか隠すかは持ち主が決めたまま)。親が先に消えていたら根ごと捨てる (置き場に残すと次の逆再生で出ない)
        foreach (var h in HudHidden)
        {
            if (!h.T) continue;
            if (h.Parent) h.T.SetParent(h.Parent, false);
            else Object.Destroy(h.T.gameObject);
        }
        HudHidden.Clear();
    }

    // 戻し終わり: 戻し先の時刻より後に出てきた絵を隠す (戻し先の世界には無かった物)。
    // 遊びに関わる物 (弾など) や毎フレーム出し直す物は、持ち主が OnRewound / Rewind.Undo で自分で消す
    internal static void HideBornAfter(long destMs)
    {
        ViewHidden = 0;
        var root = FxRoot.Current;
        if (root) HideUnder(root, destMs);
        var world = FxRoot.CurrentWorld;
        if (world) HideUnder(world, destMs);
        Born.Clear();
        SeenPrev.Clear();
    }

    private static void HideUnder(Transform root, long destMs)
    {
        var srs = root.GetComponentsInChildren<SpriteRenderer>(false);
        var mrs = root.GetComponentsInChildren<MeshRenderer>(false);
        // 戻し先にもあった絵とその先祖 (根の直下まで) は隠さない。試合中ずっと生きる入れ物 (大砲の根など) の下に後から出た子だけを隠す
        Keep.Clear();
        Late.Clear();
        var rootPtr = root.Pointer;
        int ns = srs.Count, nm = mrs.Count;
        for (int i = 0; i < ns + nm; i++)
        {
            Renderer r = i < ns ? srs[i] : mrs[i - ns];
            if (!r) continue;
            if (Born.TryGetValue(r.Pointer, out long born) && born > destMs) { Late.Add(r); continue; }
            for (var t = r.transform; t && t.Pointer != rootPtr; t = t.parent)
                if (!Keep.Add(t.Pointer)) break;
        }
        foreach (var r in Late)
        {
            if (!r) continue;
            var t = r.transform;
            // 自分の下に残す絵がある: 自分の絵だけ消す
            if (Keep.Contains(t.Pointer)) { r.enabled = false; ViewHidden++; continue; }
            // 子の絵だけ隠すと親の絵が残るので、残す物を含まない一番上の先祖ごと隠す
            for (var up = t.parent; up && up.Pointer != rootPtr && !Keep.Contains(up.Pointer); up = up.parent) t = up;
            if (t.gameObject.activeSelf) { t.gameObject.SetActive(false); ViewHidden++; }
        }
        Late.Clear();
    }

    private static readonly HashSet<IntPtr> Keep = new();
    private static readonly List<Renderer> Late = new();

    // 戻した時刻より後の控えを捨てた後: 次に書く位置を残った一番新しい控えの続きへ戻す
    private static void ForgetView()
    {
        if (_count == 0) { _viewHead = 0; return; }
        int s = (_head - 1 + Slots) % Slots;
        _viewHead = (ViewStart[s] + ViewCount[s]) % ViewCap;
    }

    internal static string DescribeView() =>
        $"view[shots={ViewShots} dropped={ViewDropped} hudDropped={HudDropped} worldObjs={FxRoot.WorldObjs.Count} hidden={ViewHidden} sprites={SpriteCache.Count} proxies={_proxyUsed}/{Proxies.Count} meshes={_meshUsed}/{MeshProxies.Count} hud={_hudUsed}/{_hudMeshUsed}/{TextProxies.Count} texts={TextCache.Count} dyn={DynShots}/{DynDrawn}/{Dyns.Count} rootHidden={_rootHidden}]";
}
