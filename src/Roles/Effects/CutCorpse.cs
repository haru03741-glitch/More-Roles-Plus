using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 必殺技で斬られた人の死体 (全員の画面): 袈裟と腰の横一文字の 2 本で斬れた形にする。
// 脚は本編の死体のまま (骨が立つ = 死体の記号・通報もそのまま)。体の上の 2 片 (頭と背中 / バイザーの下の欠片) を、
// 必殺技の始まりに写した生きている姿から切り出し、本編の死体が出た所で左右の床へ落とす。
// 片は死体と同じだけ残る (会議・生き返り・死体が消えたら一緒に消す)。動くのは落ちる間だけ
internal static class CutCorpse
{
    private const float KesaDeg = 35f;        // 袈裟の傾き (バイザーの側が高い)
    private const float KesaLift = 0.05f;     // 袈裟が通る高さ = 体の中心 + 体の高さ × これ
    private const float Waist = 0.38f;        // 腰の横一文字 = 足元 + 体の高さ × これ
    private const float Rim = 0.06f;          // 切り口の縁の太さ (体の高さに対して)
    private const float Expire = 9f;          // 死体が出ないまま待つ上限 (必殺技の始まりから・倒れるのは約 3.8 秒後)
    private const float BodySearchEvery = 0.1f;
    private const int AliveCheckEvery = 6;    // 落ちきった後、死体が残っているかを見る間隔 (フレーム)
    private const int FurnitureLayer = 12;
    private static readonly int StopMask = Constants.ShipAndObjectsMask | 1 << FurnitureLayer;
    private static readonly Color32 RimColor = new(22, 18, 44, 255);

    // 片の動き: 切り口に沿って滑る → 床へ落ちる → 少し揺れて止まる
    private sealed class Piece
    {
        public GameObject Go;
        public Transform Tf;
        public readonly List<Mesh> Meshes = new(6);
        public readonly List<Material> Mats = new(6);
        public float Cx, Cy;           // 片の中心 (写した時のワールド)
        public float Delay, SlideT, FallT;
        public float Sx, Sy;           // 滑る向きと長さ
        public float Lx, Ly, Rot;      // 落ちた所 (中心からの差) と傾き (度)
        public float Arc;              // 落ちる間に浮く高さ (飛ばされた時)
        public float Z;
        public bool Landed;
    }

    private sealed class Corpse
    {
        public byte Id;
        public float Made, Act = -1f, NextSearch;
        public float Px, Py;           // 写した時の人の位置 (死体の出た所との差で片を寄せる)
        public float FootX, FootY, H;
        public int Layer;
        public DeadBody Body;
        public readonly Piece[] Pcs = new Piece[2];
        public bool Settled;
    }

    private static readonly List<Corpse> _live = new();
    private static readonly Dictionary<IntPtr, Material> RimMats = new();
    private static int _shipGen = -1, _frame;
    internal static int Made, Shown, Expired;

    // 全員の端末: 必殺技が始まった時に斬られる人の今の姿を写しておく (倒れるまで演出の間は止まっている)
    internal static void Expect(byte[] victims)
    {
        if (victims == null) return;
        CheckShip();
        foreach (byte id in victims) Capture(id);
    }

    // 立ち合いでサムライに斬られて倒れる人 (全員の端末・倒れる知らせは本編のキルより先に届く)。
    // 片は斬った人と反対へ飛ぶ (強い一撃ほど遠くへ)。2 本の線は一振りなので、欠片も間を置かずに離れる
    internal static bool CaptureHit(byte id, byte from, bool strong)
    {
        var a = GameData.Instance ? GameData.Instance.GetPlayerById(from)?.Object : null;
        var t = GameData.Instance ? GameData.Instance.GetPlayerById(id)?.Object : null;
        float dx = 0f, dy = 0f;
        if (a && t)
        {
            Vector2 at = a.GetTruePosition(), me = t.GetTruePosition();
            dx = me.x - at.x;
            dy = me.y - at.y;
            float d = FxMath.Sqrt(dx * dx + dy * dy);
            if (d > 0.01f) { dx /= d; dy /= d; }
            else dx = dy = 0f;
        }
        return Capture(id, new Fling { Dx = dx, Dy = dy, Far = strong ? 1.8f : 0.35f, Arc = strong ? 0.6f : 0f, Quick = true });
    }

    private struct Fling
    {
        public float Dx, Dy, Far, Arc;
        public bool Quick;
    }

    internal static bool Capture(byte id) => Capture(id, default);

    private static bool Capture(byte id, Fling fl)
    {
        CheckShip();
        var pc = GameData.Instance ? GameData.Instance.GetPlayerById(id)?.Object : null;
        if (!pc || pc.Data == null || pc.Data.IsDead || !pc.cosmetics) return false;
        foreach (var c in _live)
            if (c.Id == id) return false;
        var cos = pc.cosmetics;
        var body = cos.currentBodySprite != null ? cos.currentBodySprite.BodySprite : null;
        if (!body || !body.enabled || !body.sprite) return false;
        var b = body.bounds;
        float h = b.size.y;
        if (h < 0.05f) return false;
        Vector3 pp = pc.transform.position;
        var c0 = new Corpse { Id = id, Made = Time.time, Px = pp.x, Py = pp.y, FootX = b.center.x, FootY = b.min.y, H = h, Layer = pc.gameObject.layer };
        // バイザーの側 (+1 = 右向き)。袈裟はバイザーの側が高く背中の側が低い
        float face = cos.FlipX ? -1f : 1f;
        // 斬った人がいれば、その人の側を前として斬る (頭と背中の片が斬った人と反対へ飛ぶ)
        if (fl.Dx > 0.15f) face = -1f;
        else if (fl.Dx < -0.15f) face = 1f;
        float ar = KesaDeg * FxMath.Deg2Rad;
        // 袈裟の線の上側の法線 (線はバイザーの側へ上る: 傾き = tan·face)
        float knx = -FxMath.Sin(ar) * face, kny = FxMath.Cos(ar);
        float kx = b.center.x, ky = b.center.y + h * KesaLift;
        float kd = knx * kx + kny * ky;
        float wy = b.min.y + h * Waist;
        var kesaUp = new FxMeshCut.Plane(knx, kny, kd);
        var kesaDown = new FxMeshCut.Plane(-knx, -kny, -kd);
        var waistUp = new FxMeshCut.Plane(0f, 1f, wy);

        var srcs = new List<(SpriteRenderer sr, bool cut, bool player)>(6) { (body, true, true) };
        if (cos.skin && cos.skin.layer) srcs.Add((cos.skin.layer, true, false));
        if (cos.visor && cos.visor.Image) srcs.Add((cos.visor.Image, true, false));
        if (cos.hat)
        {
            if (cos.hat.BackLayer) srcs.Add((cos.hat.BackLayer, false, false));
            if (cos.hat.FrontLayer) srcs.Add((cos.hat.FrontLayer, false, false));
        }
        int color = cos.ColorId;
        var ship = ShipStatus.Instance;
        for (int k = 0; k < 2; k++)
        {
            // 0 = 頭と背中 (袈裟の上・帽子も載る)・1 = バイザーの下の欠片 (袈裟の下・腰の上)
            var planes = k == 0 ? new[] { kesaUp } : new[] { kesaDown, waistUp };
            var go = new GameObject(k == 0 ? "MrpCutCorpseTop" : "MrpCutCorpseChip") { layer = c0.Layer };
            if (ship) go.transform.SetParent(ship.transform, true);
            go.SetActive(false);
            var p = new Piece { Go = go, Tf = go.transform };
            int made = 0;
            // 片の中心: 切り口の上の点を軸にすると回した時に床から浮くので、片の網目の真ん中で回す
            float sx = 0f, sy = 0f;
            int sn = 0;
            var tmp = FxMeshCut.Cut(body, 0f, 0f, planes, planes.Length);
            if (tmp)
            {
                foreach (var v in tmp.vertices) { sx += v.x; sy += v.y; sn++; }
                Object.Destroy(tmp);
            }
            if (sn == 0) { Object.Destroy(go); continue; }
            p.Cx = sx / sn;
            p.Cy = sy / sn;
            go.transform.position = FxMath.V3(p.Cx, p.Cy, pp.z);
            foreach (var (sr, cut, player) in srcs)
            {
                if (!sr || !sr.enabled || !sr.sprite || !sr.gameObject.activeInHierarchy) continue;
                if (!cut && k != 0) continue;
                var pl = cut ? planes : Array.Empty<FxMeshCut.Plane>();
                if (AddMesh(p, sr, pl, player, color, c0.Layer, sr.transform.position.z - pp.z) && cut)
                {
                    // 切り口の縁 (本編の絵の縁と同じ暗い色): 片の中で切った線から Rim の幅だけ
                    foreach (var cutPl in planes) AddRim(p, sr, pl, cutPl, h * Rim, c0.Layer, sr.transform.position.z - pp.z - 0.0002f);
                    made++;
                }
            }
            if (made == 0) { Kill(p); continue; }
            Plan(c0, p, k, face, fl);
            c0.Pcs[k] = p;
        }
        if (c0.Pcs[0] == null && c0.Pcs[1] == null) return false;
        _live.Add(c0);
        Made++;
        return true;
    }

    private static bool AddMesh(Piece p, SpriteRenderer sr, FxMeshCut.Plane[] planes, bool player, int color, int layer, float dz)
    {
        var mesh = FxMeshCut.Cut(sr, p.Cx, p.Cy, Shift(planes, p.Cx, p.Cy), planes.Length);
        if (!mesh) return false;
        var go = new GameObject("Part") { layer = layer };
        go.transform.SetParent(p.Tf, false);
        go.transform.localPosition = FxMath.V3(0f, 0f, dz);
        go.AddComponent<MeshFilter>().mesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        // 体は本編の死体と同じ材質 (生きている体の材質はこの後に幽霊の物へ替わる)
        var baseMat = player ? FxHands.PlayerMat() : null;
        var mat = new Material(baseMat ? baseMat : sr.sharedMaterial) { mainTexture = sr.sprite.texture };
        mr.sharedMaterial = mat;
        mr.sortingOrder = sr.sortingOrder;
        if (color >= 0 && color < Palette.PlayerColors.Length) PlayerMaterial.SetColors(color, mr);
        var used = mr.sharedMaterial;
        if (used != mat) p.Mats.Add(mat);
        p.Mats.Add(used);
        p.Meshes.Add(mesh);
        return true;
    }

    // 片の中で、切った線 cut から幅 w の帯 (絵の透けている所は透けたまま)
    private static void AddRim(Piece p, SpriteRenderer sr, FxMeshCut.Plane[] planes, FxMeshCut.Plane cut, float w, int layer, float dz)
    {
        var band = new FxMeshCut.Plane[planes.Length + 1];
        Array.Copy(planes, band, planes.Length);
        band[planes.Length] = new FxMeshCut.Plane(-cut.Nx, -cut.Ny, -(cut.D + w));
        var mesh = FxMeshCut.Cut(sr, p.Cx, p.Cy, Shift(band, p.Cx, p.Cy), band.Length, RimColor, "MrpCutCorpseRim");
        if (!mesh) return;
        var go = new GameObject("Rim") { layer = layer };
        go.transform.SetParent(p.Tf, false);
        go.transform.localPosition = FxMath.V3(0f, 0f, dz);
        go.AddComponent<MeshFilter>().mesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = RimMat(sr.sprite.texture);
        mr.sortingOrder = sr.sortingOrder;
        p.Meshes.Add(mesh);
    }

    // 面はワールドの原点で作ってあるので、片の中心 (ox, oy) からの位置へ直す
    private static FxMeshCut.Plane[] Shift(FxMeshCut.Plane[] planes, float ox, float oy)
    {
        var r = new FxMeshCut.Plane[planes.Length];
        for (int i = 0; i < planes.Length; i++)
        {
            var q = planes[i];
            r[i] = new FxMeshCut.Plane(q.Nx, q.Ny, q.D - q.Nx * ox - q.Ny * oy);
        }
        return r;
    }

    private static Material RimMat(Texture tex)
    {
        if (RimMats.TryGetValue(tex.Pointer, out var m) && m) return m;
        var shader = Shader.Find("Sprites/Default");
        m = shader ? new Material(shader) : new Material(FxHands.PlayerMat());
        m.mainTexture = tex;
        RimMats[tex.Pointer] = m;
        return m;
    }

    // 片ごとの落ち方 (体の高さに合わせる)。頭と背中は袈裟に沿って背中の側へ滑り、切り口を下にして床へ。
    // 欠片は腰の一文字の後でバイザーの側へ滑って前へ落ちる。どちらも脚から離して置く (重なると脚と骨が読めない)
    private static void Plan(Corpse c, Piece p, int k, float face, Fling fl)
    {
        float h = c.H, back = -face;
        float ar = KesaDeg * FxMath.Deg2Rad;
        if (k == 0)
        {
            p.Delay = 0f;
            p.SlideT = 0.27f;
            p.FallT = 0.11f;
            p.Sx = back * FxMath.Cos(ar) * 0.22f * h;
            p.Sy = -FxMath.Sin(ar) * 0.22f * h;
            p.Lx = back * 0.8f * h;
            p.Ly = c.FootY + 0.2f * h - p.Cy;
            p.Rot = 35f * back;
        }
        else
        {
            p.Delay = fl.Quick ? 0.06f : 0.23f;     // 必殺技は腰の横一文字の音 (袈裟の 7 コマ後) に合わせる
            p.SlideT = 0.06f;
            p.FallT = 0.15f;
            p.Sx = face * 0.08f * h;
            p.Sy = 0f;
            p.Lx = face * 0.85f * h;
            p.Ly = c.FootY + 0.1f * h - p.Cy;
            p.Rot = -10f * face;
        }
        // 一振りで斬られた時は切り口で滑る間を短く (すぐ飛ぶ)
        if (fl.Quick) p.SlideT *= 0.35f;
        // 飛ばされた分 (床の上を動く)。欠片は少し近くへ
        float far = fl.Far * h * (k == 0 ? 1f : 0.8f);
        float fx = p.Lx + fl.Dx * far, fy = fl.Dy * far;
        if (fl.Arc > 0f)
        {
            p.Arc = fl.Arc * h;
            p.FallT += 0.3f;
        }
        // 壁と家具の手前で止める (足元の高さで、床の上の行き先まで見る)
        float y0 = c.FootY + 0.05f * h;
        for (int i = 0; i < 3; i++)
        {
            float ex = p.Cx + fx - c.FootX, ey = fy, el = FxMath.Sqrt(ex * ex + ey * ey);
            float pad = el > 0.01f ? 0.15f / el : 0f;
            if (!PhysicsHelpers.AnythingBetween(FxMath.V2(c.FootX, y0), FxMath.V2(c.FootX + ex * (1f + pad), y0 + ey * (1f + pad)), StopMask, false)) break;
            if (i == 2) { fx = p.Sx; fy = 0f; }
            else { fx *= 0.5f; fy *= 0.5f; }
        }
        p.Lx = fx;
        p.Ly += fy;
    }

    private static void CheckShip()
    {
        if (_shipGen == GameClock.ShipGen) return;
        _shipGen = GameClock.ShipGen;
        Clear();
    }

    internal static void Tick()
    {
        if (_live.Count == 0) return;
        if (_shipGen != GameClock.ShipGen) { CheckShip(); return; }
        float now = Time.time;
        bool check = ++_frame >= AliveCheckEvery;
        if (check) _frame = 0;
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var c = _live[i];
            if (c.Act < 0f)
            {
                if (now - c.Made > Expire) { Drop(c); _live.RemoveAt(i); Expired++; continue; }
                if (now < c.NextSearch) continue;
                c.NextSearch = now + BodySearchEvery;
                c.Body = FindBody(c.Id);
                if (!c.Body) continue;
                Activate(c, now);
                continue;
            }
            // 死体が消えた (会議・生き返り・食べられた) ら片も消す
            if (check && !c.Body) { Drop(c); _live.RemoveAt(i); continue; }
            if (c.Settled) continue;
            bool all = true;
            for (int k = 0; k < 2; k++)
            {
                var p = c.Pcs[k];
                if (p == null || p.Landed && p.Delay < 0f) continue;
                all &= Pose(c, p, now - c.Act);
            }
            if (all) c.Settled = true;
        }
    }

    // 本編の死体が出た: 片を出す (死体の出た所が写した時の位置とずれていたら同じだけ寄せる)
    private static void Activate(Corpse c, float now)
    {
        c.Act = now;
        Vector3 bp = c.Body.transform.position;
        float mx = bp.x - c.Px, my = bp.y - c.Py;
        if (mx * mx + my * my > 4f) mx = my = 0f;
        c.FootX += mx;
        c.FootY += my;
        for (int k = 0; k < 2; k++)
        {
            var p = c.Pcs[k];
            if (p == null) continue;
            p.Cx += mx;
            p.Cy += my;
            // 奥行き: 本編の死体のすぐ手前 (欠片は頭の片より手前)・落ちた先の高さで奥行きを合わせる
            p.Z = bp.z + (p.Cy + p.Ly - c.FootY) / 1000f - 0.0006f - 0.0003f * k;
            p.Tf.position = FxMath.V3(p.Cx, p.Cy, bp.z - 0.0006f - 0.0003f * k);
            p.Go.SetActive(true);
        }
        Shown++;
    }

    // 片を動かす。止まったら true
    private static bool Pose(Corpse c, Piece p, float t)
    {
        t -= p.Delay;
        if (t <= 0f) return false;
        float x, y, a;
        float t1 = p.SlideT, t2 = t1 + p.FallT, t3 = t2 + 0.09f;
        if (t < t1)
        {
            float s = t / t1;
            s *= s;
            x = p.Sx * s;
            y = p.Sy * s;
            a = p.Rot * 0.06f * s;
        }
        else if (t < t2)
        {
            float s = (t - t1) / p.FallT;
            float e = s * s;
            x = p.Sx + (p.Lx - p.Sx) * s;
            y = p.Sy + (p.Ly - p.Sy) * (p.Arc > 0f ? s : e) + p.Arc * FxMath.Sin(FxMath.PI * s);
            a = p.Rot * (0.06f + 1.04f * s);
        }
        else if (t < t3)
        {
            if (!p.Landed)
            {
                p.Landed = true;
                DuelHitFx.Dust(p.Cx + p.Lx, c.FootY + 0.02f * c.H + (p.Ly + p.Cy - c.FootY) * 0.3f, p.Z, c.Layer, p.Lx >= 0f ? 1f : -1f, 0f, 3, 0.55f);
            }
            float s = (t - t2) / 0.09f;
            x = p.Lx;
            y = p.Ly + 0.03f * c.H * FxMath.Sin(FxMath.PI * s);
            a = p.Rot * (1.1f - 0.1f * s);
        }
        else
        {
            p.Tf.position = FxMath.V3(p.Cx + p.Lx, p.Cy + p.Ly, p.Z);
            p.Tf.rotation = FxMath.RotZ(p.Rot);
            p.Delay = -1f;
            return true;
        }
        p.Tf.position = FxMath.V3(p.Cx + x, p.Cy + y, p.Z);
        p.Tf.rotation = FxMath.RotZ(a);
        return false;
    }

    private static DeadBody FindBody(byte id)
    {
        foreach (var b in Object.FindObjectsOfType<DeadBody>())
            if (b && b.ParentId == id) return b;
        return null;
    }

    private static void Kill(Piece p)
    {
        foreach (var m in p.Meshes)
            if (m) Object.Destroy(m);
        foreach (var m in p.Mats)
            if (m) Object.Destroy(m);
        p.Meshes.Clear();
        p.Mats.Clear();
        if (p.Go) Object.Destroy(p.Go);
    }

    private static void Drop(Corpse c)
    {
        for (int k = 0; k < 2; k++)
            if (c.Pcs[k] != null) Kill(c.Pcs[k]);
    }

    internal static void Clear()
    {
        foreach (var c in _live) Drop(c);
        _live.Clear();
        foreach (var m in RimMats.Values)
            if (m) Object.Destroy(m);
        RimMats.Clear();
    }

    internal static string Describe() => $"live={_live.Count} made={Made} shown={Shown} expired={Expired}";
}
