using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 斬られた扉の見た目 (全員の画面): 扉の絵を斬った線で 2 片に割り、止め (一瞬そのまま) → 線に沿ってずれながら離れ、
// 傾いて落ちて消える。割れる瞬間に線の光と刀同士の火花。壊れている間は本物の扉の絵を消し、戻る時に戻す。
// 片は扉の絵の網目を半平面で切った Mesh (詰めて置かれたテクスチャでも絵の網目どおりに切れる)。動かすのは片の transform だけ
internal static class DoorCutFx
{
    private const float HoldT = 0.07f;    // 割れた形のまま止める秒
    private const float SlideT = 0.30f;   // 線に沿ってずれて離れる秒
    private const float Life = 1.10f;     // 片が消えきるまで
    private const float Slide = 0.20f;    // 線に沿ってずれる長さ (片ごとに逆向き)
    private const float Part = 0.12f;     // 線から離れる長さ
    private const float Tilt = 9f;        // 離れる間の傾き (度)
    private const float Fall = 0.45f;     // 落ちる重さ (単位/秒²)
    private const float LineT = 0.16f;    // 線の光の秒

    private sealed class Piece
    {
        public GameObject Go;
        public Transform Tf;
        public Mesh Mesh;
        public MeshRenderer Mr;
        public Material Mat;
        public SpriteRenderer Edge; // 切り口の光 (片の根 = 切った線の上にあるので、根から線の向きに置く)
        public float T, Sx, Sy, Nx, Ny, Spin, Px, Py, Pz, R, G, B, A;
    }

    private static readonly List<Piece> Pieces = new();
    private static readonly List<(GameObject Go, SpriteRenderer Sr, float T, float Len)> Lines = new();
    // 壊れている扉ごとに消した絵 (戻す時に戻す)
    private static readonly Dictionary<System.IntPtr, List<SpriteRenderer>> Hidden = new();
    private static readonly Dictionary<System.IntPtr, Material> Mats = new();
    private static Sprite _line;
    private static int _made;

    internal static string Describe() => $"pieces={Pieces.Count} hidden={Hidden.Count} made={_made}";

    // 扉を割る。cutDeg = 割る線の向き (度)・by = 斬った人
    internal static void Break(OpenableDoor door, int cutDeg, byte by)
    {
        var col = CutDoors.ColliderOf(door);
        Vector3 c = door.transform.position;
        if (col)
        {
            var cc = CutDoors.CenterOf(col);
            c = FxMath.V3(cc.x, cc.y, c.z);
        }
        float ar = cutDeg * FxMath.Deg2Rad;
        float lx = FxMath.Cos(ar), ly = FxMath.Sin(ar);
        var list = new List<SpriteRenderer>();
        CollectRenderers(door, list);
        float now = Time.time;
        float diag = 0.6f;
        foreach (var sr in list)
        {
            if (!sr.enabled || !sr.sprite) continue;
            var b = sr.bounds;
            diag = FxMath.Max(diag, FxMath.Sqrt(b.size.x * b.size.x + b.size.y * b.size.y));
            // 線の両側を 1 片ずつ。法線 (ly, -lx) の正の側が side = 1
            for (int side = -1; side <= 1; side += 2) MakePiece(sr, c.x, c.y, lx, ly, side, now);
            sr.enabled = false;
        }
        if (list.Count > 0) Hidden[door.Pointer] = list;
        float z = list.Count > 0 ? list[0].transform.position.z - 0.01f : c.z;
        int layer = list.Count > 0 ? list[0].gameObject.layer : door.gameObject.layer;
        MakeLine(c.x, c.y, z - 0.002f, cutDeg, diag * 1.5f, layer, now);
        DuelHitFx.Clash(FxMath.V3(c.x, c.y, z - 0.004f), layer, 0.9f);
        SamuraiSound.At("noise_samurai_hit_ko", c.x, c.y, 0.75f);
        var me = PlayerControl.LocalPlayer;
        if (me && me.PlayerId == by)
        {
            var hud = Vanilla.Hud;
            if (hud && hud.PlayerCam) hud.PlayerCam.ShakeScreen(0.12f, 1.2f);
        }
    }

    // 扉の絵を戻す (扉は開いたまま)
    internal static void Mend(OpenableDoor door)
    {
        if (!Hidden.Remove(door.Pointer, out var list)) return;
        foreach (var sr in list)
            if (sr) sr.enabled = true;
    }

    // 本編の扉の絵: 板の扉は本体の絵、茸の扉は茸ごとの絵
    private static void CollectRenderers(OpenableDoor door, List<SpriteRenderer> list)
    {
        var mush = door.TryCast<MushroomWallDoor>();
        if (mush != null)
        {
            var ms = mush.mushrooms;
            if (ms != null)
                foreach (var m in ms)
                    if (m && m.sprite) list.Add(m.sprite);
            return;
        }
        var sr = door.GetComponent<SpriteRenderer>();
        if (sr) list.Add(sr);
    }

    private static void MakePiece(SpriteRenderer sr, float cx, float cy, float lx, float ly, int side, float now)
    {
        var sp = sr.sprite;
        var tf = sr.transform;
        float nx = ly, ny = -lx;
        // 線の両側を半平面 (法線との内積 * side >= 0) で切る
        var mesh = FxMeshCut.Cut(sr, cx, cy, new[] { new FxMeshCut.Plane(nx * side, ny * side, 0f) }, 1, null, "MrpDoorPiece");
        if (!mesh) return;
        var go = new GameObject("MrpDoorPiece") { layer = sr.gameObject.layer };
        var ship = ShipStatus.Instance;
        if (ship) go.transform.SetParent(ship.transform, true);
        go.AddComponent<MeshFilter>().mesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = MatFor(sr);
        var col = sr.color;
        float z = tf.position.z;
        var p = new Piece
        {
            Go = go, Tf = go.transform, Mesh = mesh, Mr = mr, T = now,
            Sx = lx * side, Sy = ly * side, Nx = nx * side, Ny = ny * side, Spin = Tilt * side * FxMath.Range(0.7f, 1.3f),
            Px = cx, Py = cy, Pz = z - 0.001f, R = col.r, G = col.g, B = col.b, A = col.a,
        };
        p.Mat = mr.material;
        _line ??= FxSprites.Rect(64, 16, Glow, "MrpDoorCutLine");
        if (_line)
        {
            var eg = new GameObject("Edge") { layer = go.layer };
            eg.transform.SetParent(go.transform, false);
            eg.transform.localPosition = FxMath.V3(0f, 0f, -0.0005f);
            eg.transform.localRotation = FxMath.RotZ(FxMath.Atan2(ly, lx) * FxMath.Rad2Deg);
            float len = FxMath.Sqrt(sp.bounds.size.x * sp.bounds.size.x + sp.bounds.size.y * sp.bounds.size.y) * 1.1f;
            eg.transform.localScale = FxMath.V3(len, 0.05f, 1f);
            p.Edge = eg.AddComponent<SpriteRenderer>();
            p.Edge.sprite = _line;
        }
        p.Tf.position = FxMath.V3(cx, cy, p.Pz);
        Pieces.Add(p);
        _made++;
    }

    // 扉の絵のテクスチャごとに 1 つ (試合の終わりに消す)
    private static Material MatFor(SpriteRenderer sr)
    {
        var tex = sr.sprite.texture;
        if (Mats.TryGetValue(tex.Pointer, out var m) && m) return m;
        var shader = Shader.Find("Sprites/Default");
        m = shader ? new Material(shader) : new Material(sr.sharedMaterial);
        m.mainTexture = tex;
        Mats[tex.Pointer] = m;
        return m;
    }

    private static void MakeLine(float x, float y, float z, float deg, float len, int layer, float now)
    {
        _line ??= FxSprites.Rect(64, 16, Glow, "MrpDoorCutLine");
        if (!_line) return;
        var go = new GameObject("MrpDoorCutLine") { layer = layer };
        var ship = ShipStatus.Instance;
        if (ship) go.transform.SetParent(ship.transform, true);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _line;
        go.transform.position = FxMath.V3(x, y, z);
        go.transform.rotation = FxMath.RotZ(deg);
        go.transform.localScale = FxMath.V3(0f, 0f, 1f);
        Lines.Add((go, sr, now, len));
    }

    // 横に長い光の筋 (真ん中が白く、縁は青い)
    private static float Glow(float x, float y)
    {
        float ax = FxMath.Abs(x) * 2f, ay = FxMath.Abs(y) * 2f;
        float w = 1f - ax * ax;
        if (w <= 0f) return 0f;
        return FxMath.Clamp01((w - ay) / 0.6f);
    }

    // 毎フレーム (片か線がある時だけ動く)
    internal static void Tick()
    {
        if (Pieces.Count == 0 && Lines.Count == 0) return;
        float now = Time.time;
        for (int i = Pieces.Count - 1; i >= 0; i--)
        {
            var p = Pieces[i];
            float t = now - p.T;
            if (!p.Go || t >= Life)
            {
                Kill(p);
                Pieces.RemoveAt(i);
                continue;
            }
            if (t < HoldT) continue;
            float s = FxMath.Clamp01((t - HoldT) / SlideT);
            float e = 1f - (1f - s) * (1f - s) * (1f - s);
            float ft = FxMath.Max(0f, t - HoldT - SlideT * 0.5f);
            float x = p.Px + p.Sx * Slide * e + p.Nx * Part * e;
            float y = p.Py + p.Sy * Slide * e + p.Ny * Part * e - Fall * ft * ft;
            p.Tf.position = FxMath.V3(x, y, p.Pz);
            p.Tf.rotation = FxMath.RotZ(p.Spin * e + p.Spin * 1.5f * ft);
            float a = t < 0.45f ? 1f : FxMath.Clamp01(1f - (t - 0.45f) / (Life - 0.45f));
            p.Mat.color = FxMath.Rgba(p.R, p.G, p.B, p.A * a);
            // 切り口は白く光ってから橙に冷めて消える
            if (p.Edge)
            {
                float heat = 1f - FxMath.Clamp01(t / 0.5f);
                p.Edge.color = FxMath.Rgba(1f, 0.55f + 0.45f * heat, 0.25f + 0.75f * heat * heat, heat * a);
            }
        }
        for (int i = Lines.Count - 1; i >= 0; i--)
        {
            var l = Lines[i];
            float t = now - l.T;
            if (!l.Go || t >= LineT)
            {
                if (l.Go) Object.Destroy(l.Go);
                Lines.RemoveAt(i);
                continue;
            }
            // 一瞬で伸びきり、細くなって消える
            float grow = FxMath.Clamp01(t / 0.03f);
            float thin = 1f - FxMath.Clamp01((t - 0.03f) / (LineT - 0.03f));
            l.Go.transform.localScale = FxMath.V3(l.Len * grow, 0.09f * thin + 0.01f, 1f);
            l.Sr.color = FxMath.Rgba(0.85f, 0.95f, 1f, thin);
        }
    }

    private static void Kill(Piece p)
    {
        if (p.Mat) Object.Destroy(p.Mat);
        if (p.Mesh) Object.Destroy(p.Mesh);
        if (p.Go) Object.Destroy(p.Go);
    }

    // 試合の替わり目 (絵を戻し、作った物を消す)
    internal static void Clear()
    {
        foreach (var p in Pieces) Kill(p);
        Pieces.Clear();
        foreach (var l in Lines)
            if (l.Go) Object.Destroy(l.Go);
        Lines.Clear();
        foreach (var list in Hidden.Values)
            foreach (var sr in list)
                if (sr) sr.enabled = true;
        Hidden.Clear();
        foreach (var m in Mats.Values)
            if (m) Object.Destroy(m);
        Mats.Clear();
    }
}
