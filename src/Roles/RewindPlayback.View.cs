using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
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

    // 作った絵をここへ移す (世界の位置はそのまま)。船が無い時は何もしない
    internal static void Attach(GameObject go)
    {
        var r = Get();
        if (r) go.transform.SetParent(r, true);
    }

    internal static bool Exists => _gen == Terrain.GameClock.ShipGen && _root;
    internal static Transform Current => Exists ? _root : null;
}

// 録画: FxRoot の下の SpriteRenderer と MeshRenderer を姿と同じ 0.1 秒ごとに控え、逆再生の間は根を隠して代わりの板を控えのとおりに置く。
// メッシュは同じ Mesh を指すだけで頂点は写さない (一度作って動かさない片・跡の型)。毎フレーム頂点を書き直すメッシュは、控えた時の形でなく今の形で出る
internal static partial class RewindPlayback
{
    private const int MaxPerSlot = 160;          // 1 回の控えで 160 枚まで (超えた分は控えない)
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
    }

    private static readonly Shot[] Shots = new Shot[ViewCap];
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

    internal static int ViewShots, ViewDropped, ViewHidden; // 確かめ用

    private static void ClearView()
    {
        StopView();
        _viewHead = 0;
        Array.Clear(ViewCount, 0, Slots);
        SpriteCache.Clear(); SpriteIndex.Clear();
        MatCache.Clear(); MatIndex.Clear();
        MeshCache.Clear(); MeshIndex.Clear(); FilterOf.Clear();
        Born.Clear(); SeenNow.Clear(); SeenPrev.Clear();
    }

    private static void CaptureView(int s, long now)
    {
        ViewCount[s] = 0;
        ViewStart[s] = _viewHead;
        var root = FxRoot.Current;
        if (!root) return;
        var all = root.GetComponentsInChildren<SpriteRenderer>(false);
        int n = 0;
        SeenNow.Clear();
        for (int i = 0; i < all.Count && n < MaxPerSlot; i++)
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
            SeenNow.Add(e.Id);
            if (!SeenPrev.Contains(e.Id)) Born[e.Id] = now;
            n++;
        }
        var meshes = root.GetComponentsInChildren<MeshRenderer>(false);
        for (int i = 0; i < meshes.Count && n < MaxPerSlot; i++)
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
            SeenNow.Add(id);
            if (!SeenPrev.Contains(id)) Born[id] = now;
            n++;
        }
        if (n >= MaxPerSlot) ViewDropped++; // 上限で切った回数
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
    }

    // 控え a (古い) と b (新しい) の間 u で置く。同じ絵が両方にあれば位置・向き・大きさ・色を補間する
    private static void DriveView(int a, int b, float u)
    {
        if (!EnsureProxyRoot()) return;
        NewerIndex.Clear();
        int bs = ViewStart[b], bn = ViewCount[b];
        if (b != a)
            for (int k = 0; k < bn; k++) NewerIndex[Shots[(bs + k) % ViewCap].Id] = (bs + k) % ViewCap;
        int s0 = ViewStart[a], n = ViewCount[a];
        int used = 0, usedMesh = 0;
        for (int k = 0; k < n; k++)
        {
            ref var e = ref Shots[(s0 + k) % ViewCap];
            Sprite sp = null;
            Mesh mesh = null;
            if (e.Mesh)
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
            if (e.Mesh)
            {
                var q = MeshProxyAt(usedMesh++);
                if (q.Mesh != e.Sprite) { q.Mf.sharedMesh = mesh; q.Mesh = e.Sprite; }
                if (q.Mat != e.Mat) { q.Mr.sharedMaterial = MatCache[e.Mat]; q.Mat = e.Mat; }
                if (q.Order != e.Order) { q.Mr.sortingOrder = e.Order; q.Order = e.Order; }
                if (q.Layer != e.Layer) { q.Go.layer = e.Layer; q.Layer = e.Layer; }
                q.Tf.position = FxMath.V3(x, y, e.Z);
                q.Tf.rotation = FxMath.RotZ(rot);
                q.Tf.localScale = FxMath.V3(sx, sy, 1f);
                if (!q.Go.activeSelf) q.Go.SetActive(true);
                continue;
            }
            var p = ProxyAt(used++);
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
            p.Tf.position = FxMath.V3(x, y, e.Z);
            p.Tf.rotation = FxMath.RotZ(rot);
            p.Tf.localScale = FxMath.V3(sx, sy, 1f);
            if (!p.Go.activeSelf) p.Go.SetActive(true);
        }
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

    private static Proxy ProxyAt(int i)
    {
        while (Proxies.Count <= i)
        {
            var go = new GameObject("MrpRewindShot");
            go.transform.SetParent(_proxyRoot, false);
            var sr = go.AddComponent<SpriteRenderer>();
            Proxies.Add(new Proxy { Go = go, Tf = go.transform, Sr = sr, Default = sr.sharedMaterial });
        }
        var p = Proxies[i];
        if (!p.Go)
        {
            var go = new GameObject("MrpRewindShot");
            go.transform.SetParent(_proxyRoot, false);
            var sr = go.AddComponent<SpriteRenderer>();
            p = Proxies[i] = new Proxy { Go = go, Tf = go.transform, Sr = sr, Default = sr.sharedMaterial };
        }
        return p;
    }

    private static MeshProxy MeshProxyAt(int i)
    {
        while (MeshProxies.Count <= i) MeshProxies.Add(null);
        var q = MeshProxies[i];
        if (q == null || !q.Go)
        {
            var go = new GameObject("MrpRewindMesh");
            go.transform.SetParent(_proxyRoot, false);
            q = MeshProxies[i] = new MeshProxy { Go = go, Tf = go.transform, Mf = go.AddComponent<MeshFilter>(), Mr = go.AddComponent<MeshRenderer>() };
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
    }

    // 戻し終わり: 戻し先の時刻より後に出てきた絵を隠す (戻し先の世界には無かった物)。
    // 遊びに関わる物 (弾など) や毎フレーム出し直す物は、持ち主が OnRewound / Rewind.Undo で自分で消す
    internal static void HideBornAfter(long destMs)
    {
        ViewHidden = 0;
        var root = FxRoot.Current;
        if (!root)
        {
            Born.Clear();
            SeenPrev.Clear();
            return;
        }
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
        Born.Clear();
        SeenPrev.Clear();
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
        $"view[shots={ViewShots} dropped={ViewDropped} hidden={ViewHidden} sprites={SpriteCache.Count} proxies={_proxyUsed}/{Proxies.Count} meshes={_meshUsed}/{MeshProxies.Count} rootHidden={_rootHidden}]";
}
