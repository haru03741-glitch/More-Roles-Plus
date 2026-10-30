using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 空間の裂け目の見た目: 裂け目の左側 (線の向きから反時計回りの側) を、線に沿ってずれ幅だけ動かした景色で塗る。
// 縁はくっきり切る (薄れさせると 2 枚の絵が重なって見え、ずれて見えない)。人も景色と一緒に写るので、
// 左側に入った体の部分だけがずれた所に見え、裂け目をまたいで立つ人は体が分かれて見える。位置は変わらない (見た目だけ)。
// 景色は裂け目ごとの小さなカメラ (メインカメラと同じ写し方で、ずれ幅だけ動かした位置) が半分の大きさの描き先へ写す。
// 帯のカメラは視界の影の板も写す。影の板は本物の視界を世界の座標で描くので、写した景色はその場所の本当の見え方で暗くなり、
// 壁の向こうの人が帯の明るい所に写って居場所が分かることはない。帯は影の板より奥なので、見ている所の暗さも上から掛かる。
// 画面に入っている裂け目だけ写す
internal static class RiftView
{
    private const int Layer = 1;            // 帯の層 (帯のカメラはこの層を写さない。裂け目の線もこの層)
    private const float Width = 20f;        // 帯の幅 (線から左へ・画面の外まで届く長さ)
    private static float Z = -0.9f;         // 人 (z ≈ y/1000) より手前・影の板 (-5) より奥
    private static int _queue = -1;         // 確かめ用: 帯の材質の描く順番 (-1 = シェーダのまま)
    // 帯の色に掛ける値。小さなカメラで写した景色はメインの画面より一様に約 1.6 倍明るく写る (PC 実測・床もクルーも同じ割合)
    private static float _gain = 0.62f;
    private const float FadeIn = 0.25f;
    private const int MaxViews = 3;         // 1 フレームに写す帯の数 (多ければ交代で)
    private const int Cols = 2, Rows = 2, Verts = Cols * Rows;

    private sealed class View
    {
        public byte Id;
        public float Born;
        public bool Seen;
        public float Hh, Hw; // カメラに書いた写す範囲 (変わった時だけ書く)
        public Camera Cam;
        public RenderTexture Rt;
        public Material Mat;
        public GameObject Go;
        public Mesh Mesh;
        public Il2CppStructArray<Vector3> V;
        public Il2CppStructArray<Vector2> Uv;
        public Il2CppStructArray<Color32> C;
    }

    private static readonly List<View> Views = new();
    private static readonly List<View> Due = new();
    private static int _gen = -1, _turn;
    private static GameObject _root;
    private static int _rtW, _rtH;

    internal static int Renders; // 計器: 写した回数

    // 人とカメラが動き終わった後に (Plugin の LateUpdate)
    internal static void LateTick()
    {
        var rifts = SpaceRifts.All;
        if (rifts.Count == 0 && Views.Count == 0) return;
        if (_gen != GameClock.ShipGen) DropAll();
        var main = Camera.main;
        if (!main || !ShipStatus.Instance || rifts.Count == 0 || MeetingHud.Instance || ExileController.Instance) { HideAll(); Sync(rifts); return; }
        Sync(rifts);
        Vector3 cp = main.transform.position;
        float cx = cp.x, cy = cp.y, hh = main.orthographicSize, hw = hh * main.aspect;
        float now = Time.time;
        Due.Clear();
        for (int i = 0; i < Views.Count; i++)
        {
            var v = Views[i];
            int at = IndexOf(rifts, v.Id);
            if (at < 0) continue;
            var rf = rifts[at];
            // 帯 = 線の左側。景色は線に沿って +ずれ幅の所を写す (左側の物はずれ幅だけ手前の向きへずれて見える)
            float nx = -rf.Uy, ny = rf.Ux;
            float off = rf.Shift;
            v.Seen = InView(rf, nx, ny, cx, cy, hw, hh);
            if (!v.Seen) { if (v.Go && v.Go.activeSelf) v.Go.SetActive(false); continue; }
            if (!Ensure(v, main)) continue;
            if (!v.Go.activeSelf) v.Go.SetActive(true);
            float alpha = FxMath.Clamp01((now - v.Born) / FadeIn);
            WriteBand(v, rf, nx, ny, cx, cy, hw, hh, alpha);
            v.Cam.transform.position = FxMath.V3(cx + rf.Ux * off, cy + rf.Uy * off, cp.z);
            // 写す範囲はズームの時だけ変わる
            if (v.Hh != hh || v.Hw != hw) { v.Hh = hh; v.Hw = hw; v.Cam.orthographicSize = hh; v.Cam.aspect = hw / hh; }
            Due.Add(v);
        }
        // 写すのは 1 フレームに MaxViews まで (多ければ交代で)
        int n = Due.Count;
        for (int k = 0; k < n && k < MaxViews; k++)
        {
            var v = Due[(_turn + k) % n];
            ClearRt(v.Rt);
            v.Cam.Render();
            Renders++;
        }
        if (n > MaxViews) _turn = (_turn + MaxViews) % n;
    }

    // 描き先を黒で消す (半透明の床が前のコマの上に重なって明るく写らないように)
    private static void ClearRt(RenderTexture rt)
    {
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(true, true, Color.black);
        RenderTexture.active = prev;
    }

    // 裂け目の一覧と帯をそろえる (増えた物を足し、消えた物を捨てる)
    private static void Sync(List<SpaceRifts.Rift> rifts)
    {
        for (int i = Views.Count - 1; i >= 0; i--)
            if (IndexOf(rifts, Views[i].Id) < 0) { Release(Views[i]); Views.RemoveAt(i); }
        for (int i = 0; i < rifts.Count; i++)
        {
            bool have = false;
            for (int k = 0; k < Views.Count; k++) if (Views[k].Id == rifts[i].Id) { have = true; break; }
            if (!have) Views.Add(new View { Id = rifts[i].Id, Born = Time.time });
        }
    }

    private static int IndexOf(List<SpaceRifts.Rift> rifts, byte id)
    {
        for (int i = 0; i < rifts.Count; i++) if (rifts[i].Id == id) return i;
        return -1;
    }

    // 帯 (線の両端と、向こう側へ Width 離れた 2 点の四角) が画面に掛かるか
    private static bool InView(in SpaceRifts.Rift rf, float nx, float ny, float cx, float cy, float hw, float hh)
    {
        float x0 = rf.X0, y0 = rf.Y0, x1 = rf.X0 + rf.Ux * rf.Len, y1 = rf.Y0 + rf.Uy * rf.Len;
        float minX = FxMath.Min(FxMath.Min(x0, x1), FxMath.Min(x0 + nx * Width, x1 + nx * Width));
        float maxX = FxMath.Max(FxMath.Max(x0, x1), FxMath.Max(x0 + nx * Width, x1 + nx * Width));
        float minY = FxMath.Min(FxMath.Min(y0, y1), FxMath.Min(y0 + ny * Width, y1 + ny * Width));
        float maxY = FxMath.Max(FxMath.Max(y0, y1), FxMath.Max(y0 + ny * Width, y1 + ny * Width));
        return maxX > cx - hw && minX < cx + hw && maxY > cy - hh && minY < cy + hh;
    }

    private static unsafe void WriteBand(View v, in SpaceRifts.Rift rf, float nx, float ny, float cx, float cy, float hw, float hh, float alpha)
    {
        float* vp = (float*)((byte*)v.V.Pointer + 4 * System.IntPtr.Size);
        float* up = (float*)((byte*)v.Uv.Pointer + 4 * System.IntPtr.Size);
        byte* cp = (byte*)v.C.Pointer + 4 * System.IntPtr.Size;
        float len = rf.Len;
        byte a = (byte)(255f * alpha);
        for (int c = 0; c < Cols; c++)
        {
            float along = c == 0 ? 0f : len;
            for (int r = 0; r < Rows; r++)
            {
                float across = r == 0 ? 0f : Width;
                int i = c * Rows + r;
                float x = rf.X0 + rf.Ux * along + nx * across, y = rf.Y0 + rf.Uy * along + ny * across;
                vp[i * 3] = x;
                vp[i * 3 + 1] = y;
                vp[i * 3 + 2] = Z;
                // 描き先はメインカメラと同じ範囲をずらして写しているので、メインカメラの画面の位置がそのまま描き先の位置
                up[i * 2] = (x - cx) / (2f * hw) + 0.5f;
                up[i * 2 + 1] = (y - cy) / (2f * hh) + 0.5f;
                cp[i * 4] = cp[i * 4 + 1] = cp[i * 4 + 2] = (byte)(255f * FxMath.Clamp01(_gain));
                cp[i * 4 + 3] = a;
            }
        }
        v.Mesh.vertices = v.V;
        v.Mesh.uv = v.Uv;
        v.Mesh.colors32 = v.C;
        // 透明物はカメラから網の範囲の中心までの距離で並ぶので、中心をカメラの真ん中に置いて z だけで前後が決まるようにする
        // (中心が遠いと、影の板より後に描かれて視界の暗さが帯に掛からない)
        v.Mesh.bounds = new Bounds(FxMath.V3(cx, cy, Z), FxMath.V3(1000f, 1000f, 0.1f));
    }

    private static bool Ensure(View v, Camera main)
    {
        if (v.Cam) return true;
        // カメラだけ先に消えた (船の入れ替わりの途中など): 残った描き先・網・材質を捨ててから作り直す
        if (v.Rt || v.Mesh || v.Mat || v.Go) Release(v);
        v.Hh = v.Hw = 0f;
        var ship = ShipStatus.Instance;
        var shader = Shader.Find("UI/Default");
        if (!ship || !shader) return false;
        if (!_root)
        {
            _gen = GameClock.ShipGen;
            _root = new GameObject("MrpRiftViews");
            _root.transform.SetParent(ship.transform, false);
            // 船の倍率と反転を打ち消して、世界の座標のまま置く
            _root.transform.position = Vector3.zero;
            _root.transform.rotation = Quaternion.identity;
            Vector3 ls = ship.transform.lossyScale;
            _root.transform.localScale = FxMath.V3(1f / ls.x, 1f / ls.y, 1f / ls.z);
        }
        int w = System.Math.Max(64, Screen.width / 2), h = System.Math.Max(64, Screen.height / 2);
        _rtW = w; _rtH = h;
        // 部屋の床と壁はステンシルで描くので、ステンシル付きの深度 (24) が要る
        v.Rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "MrpRiftView" };
        v.Rt.hideFlags |= HideFlags.HideAndDontSave;
        var camGo = new GameObject("MrpRiftViewCam");
        camGo.transform.SetParent(_root.transform, false);
        v.Cam = camGo.AddComponent<Camera>();
        v.Cam.CopyFrom(main);
        // 写さない層: UI・影を作る物・帯と裂け目の線。視界の影の板は写す (写した場所の本当の見え方で暗くする)
        int hidden = (1 << 5) | (1 << 10) | (1 << Layer);
        v.Cam.cullingMask = main.cullingMask & ~hidden;
        v.Cam.clearFlags = CameraClearFlags.SolidColor;
        // 床の絵は半透明で、メインの画面では奥の暗い宇宙が透ける。描き先も黒で消さないと床が白っぽく写る
        v.Cam.backgroundColor = Color.black;
        v.Cam.orthographic = true;
        v.Cam.depth = main.depth - 2f;
        v.Cam.targetTexture = v.Rt;
        v.Cam.enabled = false; // 自分の手で描く (画面に入っている時だけ)

        v.Go = new GameObject("MrpRiftBand") { layer = Layer };
        v.Go.transform.SetParent(_root.transform, false);
        v.V = new Il2CppStructArray<Vector3>((long)Verts);
        v.Uv = new Il2CppStructArray<Vector2>((long)Verts);
        v.C = new Il2CppStructArray<Color32>((long)Verts);
        var idx = new Il2CppStructArray<int>((long)((Cols - 1) * (Rows - 1) * 6));
        int o = 0;
        for (int c = 0; c < Cols - 1; c++)
        for (int r = 0; r < Rows - 1; r++)
        {
            int a = c * Rows + r, b = a + 1, d = a + Rows, e = d + 1;
            idx[o++] = a; idx[o++] = d; idx[o++] = b;
            idx[o++] = b; idx[o++] = d; idx[o++] = e;
        }
        v.Mesh = new Mesh { name = "MrpRiftBand" };
        v.Mesh.vertices = v.V;
        v.Mesh.uv = v.Uv;
        v.Mesh.colors32 = v.C;
        v.Mesh.triangles = idx;
        v.Mesh.bounds = new Bounds(FxMath.V3(0f, 0f, Z), FxMath.V3(1000f, 1000f, 0.1f));
        v.Go.AddComponent<MeshFilter>().mesh = v.Mesh;
        v.Mat = new Material(shader) { name = "MrpRiftView" };
        v.Mat.hideFlags |= HideFlags.HideAndDontSave;
        // 描き先の透明度は使わない (半透明の床の絵でも景色は不透明に見せる)。薄れは頂点の色で
        v.Mat.SetVector("_TextureSampleAdd", new Vector4(0f, 0f, 0f, 1f));
        v.Mat.mainTexture = v.Rt;
        if (_queue >= 0) v.Mat.renderQueue = _queue;
        v.Go.AddComponent<MeshRenderer>().sharedMaterial = v.Mat;
        return true;
    }

    private static void HideAll()
    {
        for (int i = 0; i < Views.Count; i++)
            if (Views[i].Go && Views[i].Go.activeSelf) Views[i].Go.SetActive(false);
    }

    private static void Release(View v)
    {
        if (v.Cam) { v.Cam.targetTexture = null; Object.Destroy(v.Cam.gameObject); }
        if (v.Go) Object.Destroy(v.Go);
        if (v.Mesh) Object.Destroy(v.Mesh);
        if (v.Mat) Object.Destroy(v.Mat);
        if (v.Rt) { v.Rt.Release(); Object.Destroy(v.Rt); }
        v.Cam = null;
        v.Go = null;
        v.Mesh = null;
        v.Mat = null;
        v.Rt = null;
    }

    // 船が替わった: 帯とカメラは船と一緒に消えるが、描き先・網・材質は自分で片付ける
    private static void DropAll()
    {
        for (int i = 0; i < Views.Count; i++) Release(Views[i]);
        Views.Clear();
        _root = null;
        _gen = GameClock.ShipGen;
    }

    // 確かめ用: 帯の z と材質の描く順番をその場で変える
    internal static void Tune(float z, int queue, float gain)
    {
        Z = z;
        _queue = queue;
        _gain = gain;
        foreach (var v in Views)
        {
            if (v.Mesh) v.Mesh.bounds = new Bounds(FxMath.V3(0f, 0f, Z), FxMath.V3(1000f, 1000f, 0.1f));
            if (v.Mat && queue >= 0) v.Mat.renderQueue = queue;
        }
    }

    internal static string Describe()
    {
        int seen = 0;
        foreach (var v in Views) if (v.Seen) seen++;
        return $"views={Views.Count} seen={seen} renders={Renders} rt={_rtW}x{_rtH}";
    }
}
