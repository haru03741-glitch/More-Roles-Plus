using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 立ち合いでサムライと戦うインポスターの化け物の姿。本編の舌のキルの絵 (killalien_imposter) を本人の色で塗り、
// 本物の体と着せ替えを隠した上に重ねる。絵は右向き (口が右) で、基準点を揃えて置けば頭の割れ方が絵のまま出る。
// 舌は相手の向きへ伸ばす自前の網 (縁の暗い帯 + 中の帯) で、戻る時はくねらせる。斬られた舌の先は飛んで消える
internal static unsafe class ImpostorMaw
{
    // 本編の絵の寸法 (ppu 100・立ち姿 01 の基準点から)。体の中心・体の高さ・口 (体の中心から)
    private const float ArtCx = -1.20f, ArtCy = -0.035f, ArtH = 1.91f;
    private const float MouthX = 0.45f, MouthY = 0.015f;

    // 舌の一撃の段取り (秒)。ホストの裁き (DuelTongue) と同じ数
    internal const float Open = 0.40f, Extend = 0.06f, Hold = 0.10f, Retract = 0.22f, Close = 0.18f;
    private const float Squash = 0.09f;
    private const float GuardOpen = 0.06f, GuardHold = 0.14f, GuardClose = 0.15f;
    private const float CutRetract = 0.12f, TipLife = 0.45f;

    private const int Segs = 16, Rows = Segs + 1;
    private const int TipSegs = 8, TipRows = TipSegs + 1;
    private const int TongueVerts = Rows * 4, TipVerts = TipRows * 4, Verts = TongueVerts + TipVerts;
    private const float TongueW = 0.045f, Rim = 0.02f;

    private static readonly int[] OpenFrames = { 5, 6, 7, 8, 9 };
    private static readonly int[] CloseFrames = { 25, 26, 27, 28 };
    private static readonly int[] SquashFrames = { 29, 30, 31 };

    internal enum Mode : byte { Strike, Guard }

    private sealed class Maw
    {
        public byte Id;
        public PlayerControl Pc;
        public GameObject Go, TGo;
        public Transform Tf;
        public SpriteRenderer Sr, Body;
        public Mesh Mesh;
        public Il2CppStructArray<Vector3> V;
        public Il2CppStructArray<Color32> C;
        public bool Active, BodyHidden, CosHidden, Flip;
        public Mode Mode;
        public float T0, Ang, Scale, Len, Pred, CutAt, CutDist, EndAt;
        public float Ox, Oy, Oz; // 人の根から本物の体の真ん中まで (隠した体の bounds は動かないので始めに測る)
        public int Shown;
        public bool Resolved, Hit, TipOn;
        public float TipT0, TipX, TipY, TipVx, TipVy, TipAng, TipSpin, TipLen;
    }

    private static readonly Dictionary<byte, Maw> Maws = new();
    private static readonly List<Maw> Live = new();
    private static Sprite[] _frames;
    private static bool _searched;
    private static Material _tongueMat;
    internal static int Plays, Guards, Cuts, NoArt;
    internal static float Slow = 1f; // 確かめ用: 見た目だけをこの倍の時間に伸ばす

    // ---- 絵を探す (試合の始めに 1 回。キルの演出の絵は試合中は常に読まれている) ----

    internal static bool Preload()
    {
        if (_frames != null && _frames[9]) return true;
        if (_searched) return false;
        _searched = true;
        var arr = new Sprite[49];
        int n = 0;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
        {
            var s = o.TryCast<Sprite>();
            if (!s) continue;
            string nm = s.name;
            if (nm == null || nm.Length != 22 || !nm.StartsWith("killalien_imposter", StringComparison.Ordinal)) continue;
            if (int.TryParse(nm.AsSpan(18), out int k) && k >= 1 && k <= 48 && !arr[k])
            {
                arr[k] = s;
                n++;
            }
        }
        if (n < 31 || !arr[9] || !arr[28])
        {
            Plugin.Logger.LogWarning($"ImpostorMaw: kill art not found ({n}/48)");
            return false;
        }
        _frames = arr;
        return true;
    }

    // 試合ごと。見つからなかった時も次の試合で探し直す
    internal static void Clear()
    {
        foreach (var m in Live) Stop(m);
        Live.Clear();
        foreach (var m in Maws.Values)
        {
            if (m.Go) Object.Destroy(m.Go);
            if (m.TGo) Object.Destroy(m.TGo);
            if (m.Mesh) Object.Destroy(m.Mesh);
        }
        Maws.Clear();
        _searched = false;
    }

    internal static bool Playing(byte id) => Maws.TryGetValue(id, out var m) && m.Active;

    // ---- 始める (全員の端末) ----

    // 舌の一撃: ang = 舌を伸ばす向き (度)
    internal static void Strike(PlayerControl pc, float ang, PlayerControl target)
    {
        var m = Begin(pc, ang, Mode.Strike);
        if (m == null) return;
        // 届く前の長さの見込み: 相手が線の近くにいればそこまで、いなければ届きいっぱい
        m.Pred = DuelTongue.Reach;
        if (target)
        {
            Vector2 a = pc.GetTruePosition(), b = target.GetTruePosition();
            float ar = ang * FxMath.Deg2Rad, dx = b.x - a.x, dy = b.y - a.y;
            float along = dx * FxMath.Cos(ar) + dy * FxMath.Sin(ar), side = FxMath.Abs(dx * FxMath.Sin(ar) - dy * FxMath.Cos(ar));
            if (along > 0f && side < 0.45f) m.Pred = FxMath.Min(along, m.Pred);
        }
        m.Len = m.Pred;
        Plays++;
        SamuraiSound.At("noise_samurai_dash", pc, 0.5f);
    }

    // 口で刀を受けて弾く: ang = 相手の向き
    internal static void Guard(PlayerControl pc, float ang)
    {
        var m = Begin(pc, ang, Mode.Guard);
        if (m == null) return;
        Guards++;
        Vector2 mo = MouthOf(m);
        DuelHitFx.Clash(FxMath.V3(mo.x + FxMath.Cos(ang * FxMath.Deg2Rad) * 0.15f, mo.y + FxMath.Sin(ang * FxMath.Deg2Rad) * 0.15f, m.Tf.position.z - 0.01f), pc.gameObject.layer, 0.8f);
    }

    // 伸びきった時の結果 (ホストから)。len = 舌の長さ
    internal static void Resolve(byte id, float len, bool hit)
    {
        if (!Maws.TryGetValue(id, out var m) || !m.Active || m.Mode != Mode.Strike || m.CutAt >= 0f) return;
        m.Resolved = true;
        m.Hit = hit;
        m.Len = FxMath.Max(0.3f, len);
    }

    // サムライに舌を斬られた: dist = 口から斬られた所まで
    internal static void Cut(byte id, float dist)
    {
        if (!Maws.TryGetValue(id, out var m) || !m.Active || m.Mode != Mode.Strike || m.CutAt >= 0f) return;
        float now = Time.time;
        // 溜めの途中 (舌がまだ出ていない) に斬られたら、舌は斬られた所の少し先まで飛び出した所で斬れる
        float len = FxMath.Max(CurLen(m, now), FxMath.Min(dist + 0.5f, DuelTongue.Reach));
        dist = FxMath.Clamp(dist, 0.15f, FxMath.Max(0.2f, len - 0.1f));
        m.CutAt = now;
        m.CutDist = dist;
        Cuts++;
        Vector2 mo = MouthOf(m);
        float ar = m.Ang * FxMath.Deg2Rad, co = FxMath.Cos(ar), si = FxMath.Sin(ar);
        float cx = mo.x + co * dist, cy = mo.y + si * dist;
        if (len - dist > 0.05f)
        {
            // 斬れた先は斬った刀に弾かれて横へ飛び、回りながら落ちる
            float side = FxMath.Range(0f, 1f) < 0.5f ? -1f : 1f;
            m.TipOn = true;
            m.TipT0 = now;
            m.TipLen = len - dist;
            m.TipX = cx + co * m.TipLen * 0.5f;
            m.TipY = cy + si * m.TipLen * 0.5f;
            m.TipVx = -si * side * 2.4f + co * 0.6f;
            m.TipVy = co * side * 2.4f + si * 0.6f + 1.2f;
            m.TipAng = m.Ang;
            m.TipSpin = side * FxMath.Range(500f, 800f);
        }
        DuelHitFx.Clash(FxMath.V3(cx, cy, m.Tf.position.z - 0.01f), m.Pc.gameObject.layer, 0.7f);
    }

    // ---- 毎フレーム。出ていなければ件数の比較だけ ----

    internal static void Tick()
    {
        if (Live.Count == 0) return;
        float now = Time.time;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var m = Live[i];
            if (!m.Pc || !m.Go || m.Pc.Data == null || m.Pc.Data.IsDead || now >= m.EndAt || MeetingHud.Instance || m.Pc.inVent || !m.Pc.Visible)
            {
                Stop(m);
                Live.RemoveAt(i);
                continue;
            }
            Draw(m, now);
        }
    }

    // ---- 中身 ----

    private static Maw Begin(PlayerControl pc, float ang, Mode mode)
    {
        if (!pc || !pc.Visible || pc.cosmetics == null || !Preload()) { NoArt++; return null; }
        // 前の姿が残っていれば先に本物の体を戻す (隠したままだと見えていない体と取り違える)
        if (Maws.TryGetValue(pc.PlayerId, out var prev) && prev.Active)
        {
            Stop(prev);
            Live.Remove(prev);
        }
        var bodyRef = pc.cosmetics.currentBodySprite;
        var body = bodyRef != null ? bodyRef.BodySprite : null;
        var mat = FxHands.PlayerMat();
        if (!body || !body.enabled || !mat) { NoArt++; return null; }
        if (!Maws.TryGetValue(pc.PlayerId, out var m) || !m.Go)
        {
            m = Make(pc, mat);
            Maws[pc.PlayerId] = m;
        }
        var bb = body.bounds;
        float bh = bb.size.y;
        Vector3 root = pc.transform.position, bc = bb.center;
        m.Ox = bc.x - root.x;
        m.Oy = bc.y - root.y;
        m.Oz = bc.z - root.z;
        m.Shown = 0;
        m.Pc = pc;
        m.Body = body;
        m.Scale = bh > 0.1f ? bh / ArtH : 0.5f;
        m.Ang = ang;
        m.Flip = FxMath.Cos(ang * FxMath.Deg2Rad) < 0f;
        m.Mode = mode;
        m.T0 = Time.time;
        m.Resolved = m.Hit = m.TipOn = false;
        m.CutAt = -1f;
        m.EndAt = m.T0 + Slow * (mode == Mode.Guard ? GuardOpen + GuardHold + GuardClose : Open + Extend + Hold + Retract + Close + Squash);
        int color = pc.cosmetics.ColorId;
        if (color >= 0 && color < Palette.PlayerColors.Length) PlayerMaterial.SetColors(color, m.Sr);
        m.Sr.flipX = m.Flip;
        m.Tf.localScale = FxMath.V3(m.Scale, m.Scale, 1f);
        m.Go.layer = m.TGo.layer = pc.gameObject.layer;
        body.enabled = false;
        m.BodyHidden = true;
        pc.cosmetics.SetBodyCosmeticsVisible(false);
        m.CosHidden = true;
        m.Active = true;
        m.Go.SetActive(true);
        m.TGo.SetActive(mode == Mode.Strike);
        Live.Remove(m);
        Live.Add(m);
        Draw(m, m.T0);
        return m;
    }

    private static Maw Make(PlayerControl pc, Material mat)
    {
        var go = new GameObject("MrpImpostorMaw") { layer = pc.gameObject.layer };
        Object.DontDestroyOnLoad(go);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sharedMaterial = mat;
        var tgo = new GameObject("MrpImpostorTongue") { layer = pc.gameObject.layer };
        Object.DontDestroyOnLoad(tgo);
        var v = new Il2CppStructArray<Vector3>((long)Verts);
        var c = new Il2CppStructArray<Color32>((long)Verts);
        int tris = (Segs * 2 + TipSegs * 2) * 6;
        var idx = new Il2CppStructArray<int>((long)tris);
        int o = 0;
        // 縁 (0,1) を先に、中 (2,3) を後に描く。舌と斬れた先で同じ並び
        for (int part = 0; part < 2; part++)
        {
            int baseV = part == 0 ? 0 : TongueVerts, segs = part == 0 ? Segs : TipSegs;
            for (int layer = 0; layer < 2; layer++)
            for (int s = 0; s < segs; s++)
            {
                int a = baseV + s * 4 + layer * 2, b = a + 1, d = a + 4, e = d + 1;
                idx[o++] = a; idx[o++] = d; idx[o++] = b;
                idx[o++] = b; idx[o++] = d; idx[o++] = e;
            }
        }
        var mesh = new Mesh { name = "MrpImpostorTongue" };
        mesh.vertices = v;
        mesh.colors32 = c;
        mesh.triangles = idx;
        mesh.bounds = new Bounds(Vector3.zero, FxMath.V3(1000f, 1000f, 10f));
        tgo.AddComponent<MeshFilter>().mesh = mesh;
        var mr = tgo.AddComponent<MeshRenderer>();
        if (!_tongueMat)
        {
            var shader = Shader.Find("Sprites/Default");
            _tongueMat = shader ? new Material(shader) : new Material(mat);
            _tongueMat.hideFlags = HideFlags.HideAndDontSave;
        }
        mr.sharedMaterial = _tongueMat;
        go.SetActive(false);
        tgo.SetActive(false);
        return new Maw { Id = pc.PlayerId, Go = go, Tf = go.transform, Sr = sr, TGo = tgo, Mesh = mesh, V = v, C = c };
    }

    private static void Stop(Maw m)
    {
        if (!m.Active) return;
        m.Active = false;
        if (m.Go) m.Go.SetActive(false);
        if (m.TGo) m.TGo.SetActive(false);
        // 透明になった人 (ファントムなど) は本編が見せる時に戻すので、こちらからは出さない
        bool show = m.Pc && m.Pc.Visible;
        if (m.BodyHidden && m.Body && show) m.Body.enabled = true;
        m.BodyHidden = false;
        if (m.CosHidden && show && m.Pc.cosmetics != null) m.Pc.cosmetics.SetBodyCosmeticsVisible(true);
        m.CosHidden = false;
    }

    // 体の中心 (本物の体の絵の真ん中) から見た口の位置
    private static Vector2 MouthOf(Maw m)
    {
        Vector3 c = m.Pc.transform.position;
        float fx = m.Flip ? -1f : 1f;
        return FxMath.V2(c.x + m.Ox + MouthX * fx * m.Scale, c.y + m.Oy + MouthY * m.Scale);
    }

    // 今の舌の長さ (口から先まで)
    private static float CurLen(Maw m, float now)
    {
        float t = (now - m.T0) / Slow;
        if (m.CutAt >= 0f)
        {
            float k = FxMath.Clamp01((now - m.CutAt) / Slow / CutRetract);
            return m.CutDist * (1f - k * k);
        }
        if (t < Open) return 0f;
        t -= Open;
        if (t < Extend)
        {
            float k = t / Extend;
            return m.Len * (1f - (1f - k) * (1f - k));
        }
        t -= Extend;
        if (t < Hold) return m.Len;
        t -= Hold;
        if (t < Retract)
        {
            float k = t / Retract;
            return m.Len * (1f - k * k);
        }
        return 0f;
    }

    private static int FrameAt(Maw m, float now)
    {
        float t = (now - m.T0) / Slow;
        if (m.Mode == Mode.Guard)
        {
            if (t < GuardOpen) return OpenFrames[Math.Min((int)(t / GuardOpen * 3f) + 2, 4)];
            t -= GuardOpen;
            if (t < GuardHold) return 9;
            t -= GuardHold;
            return CloseFrames[Math.Min((int)(t / GuardClose * CloseFrames.Length), CloseFrames.Length - 1)];
        }
        // 斬られたら舌を引き戻しきった所で閉じ始める
        float closeAt = m.CutAt >= 0f ? (m.CutAt - m.T0) / Slow + CutRetract : Open + Extend + Hold + Retract;
        if (t < Open) return OpenFrames[Math.Min((int)(t / Open * OpenFrames.Length), OpenFrames.Length - 1)];
        if (t < closeAt) return 9;
        t -= closeAt;
        if (t < Close) return CloseFrames[Math.Min((int)(t / Close * CloseFrames.Length), CloseFrames.Length - 1)];
        t -= Close;
        return SquashFrames[Math.Min((int)(t / Squash * SquashFrames.Length), SquashFrames.Length - 1)];
    }

    private static void Draw(Maw m, float now)
    {
        if (m.Mode == Mode.Strike && m.CutAt >= 0f && (now - m.CutAt) / Slow > CutRetract + Close + Squash && m.EndAt > now + 0.01f && !m.TipOn)
            m.EndAt = now; // 斬られた後は閉じきったら終わり
        int f = FrameAt(m, now);
        if (f != m.Shown && _frames[f])
        {
            m.Shown = f;
            m.Sr.sprite = _frames[f];
        }
        // 体の中心に絵の体の中心を合わせる (左向きは基準点を軸に鏡になる)
        Vector3 root = m.Pc.transform.position;
        Vector3 c = FxMath.V3(root.x + m.Ox, root.y + m.Oy, root.z + m.Oz);
        float fx = m.Flip ? -1f : 1f;
        float z = c.z - 0.001f;
        m.Tf.position = FxMath.V3(c.x - ArtCx * fx * m.Scale, c.y - ArtCy * m.Scale, z);
        if (m.Mode == Mode.Strike) DrawTongue(m, now, c, fx, z - 0.0005f);
    }

    private static void DrawTongue(Maw m, float now, Vector3 c, float fx, float z)
    {
        float* vp = (float*)((byte*)m.V.Pointer + 4 * IntPtr.Size);
        byte* cp = (byte*)m.C.Pointer + 4 * IntPtr.Size;
        float mx = c.x + MouthX * fx * m.Scale, my = c.y + MouthY * m.Scale;
        float len = CurLen(m, now);
        float ar = m.Ang * FxMath.Deg2Rad, co = FxMath.Cos(ar), si = FxMath.Sin(ar);
        float t = (now - m.T0) / Slow;
        // くねり: 伸びきって止めている間は先が小さく震え、戻る間 (斬られて引く間も) は S 字に大きくうねる
        float wob;
        float retractT = m.CutAt >= 0f ? (now - m.CutAt) / Slow / CutRetract : (t - Open - Extend - Hold) / Retract;
        if (retractT > 0f) wob = 0.16f * FxMath.Sin(FxMath.Clamp01(retractT) * FxMath.PI);
        else wob = t > Open + Extend ? 0.015f : 0f;
        float phase = t * 38f;
        WriteStrip(vp, cp, 0, Segs, mx, my, co, si, len, wob, phase, z, true);
        // 斬れた先
        if (m.TipOn)
        {
            float tt = (now - m.TipT0) / Slow;
            if (tt > TipLife) m.TipOn = false;
            else
            {
                float x = m.TipX + m.TipVx * tt, y = m.TipY + m.TipVy * tt - 4.5f * tt * tt;
                float a = (m.TipAng + m.TipSpin * tt) * FxMath.Deg2Rad, tc = FxMath.Cos(a), ts = FxMath.Sin(a);
                float l = m.TipLen;
                WriteStrip(vp, cp, TongueVerts, TipSegs, x - tc * l * 0.5f, y - ts * l * 0.5f, tc, ts, l, 0.1f * FxMath.Clamp01(tt * 6f), tt * 30f, z,
                    false, 1f - FxMath.Clamp01((tt - TipLife * 0.5f) / (TipLife * 0.5f)));
            }
        }
        if (!m.TipOn) Collapse(vp, TongueVerts, TipVerts, z);
        m.Mesh.vertices = m.V;
        m.Mesh.colors32 = m.C;
    }

    // 帯 1 本 (縁と中の 2 層)。root = 口から生えている (根元は細め・先は丸く太る)
    private static void WriteStrip(float* vp, byte* cp, int vb, int segs, float x0, float y0, float co, float si, float len, float wob, float phase, float z,
        bool root, float alpha = 1f)
    {
        if (len < 0.01f)
        {
            Collapse(vp, vb, (segs + 1) * 4, z);
            return;
        }
        byte al = (byte)(255f * FxMath.Clamp01(alpha));
        for (int r = 0; r <= segs; r++)
        {
            float u = (float)r / segs;
            // うねりは根元で 0・先へ向かって大きく
            float w = wob * FxMath.Sin(u * FxMath.PI * 1.6f - phase) * u;
            float px = x0 + co * len * u - si * w, py = y0 + si * len * u + co * w;
            float half = TongueW * 0.5f * (root ? 0.75f + 0.25f * u : 1f);
            if (u > 0.88f) half *= 1f + 0.5f * FxMath.Sin((u - 0.88f) / 0.12f * FxMath.PI); // 先の膨らみ
            if (r == segs) half *= 0.45f;
            float nx = -si, ny = co;
            int v = vb + r * 4;
            // 縁 (暗い)
            Put(vp, cp, v, px + nx * (half + Rim), py + ny * (half + Rim), z, 46, 6, 18, al);
            Put(vp, cp, v + 1, px - nx * (half + Rim), py - ny * (half + Rim), z, 46, 6, 18, al);
            // 中 (桃色・片側に光)
            Put(vp, cp, v + 2, px + nx * half, py + ny * half, z - 0.0001f, 236, 120, 150, al);
            Put(vp, cp, v + 3, px - nx * half, py - ny * half, z - 0.0001f, 176, 48, 86, al);
        }
    }

    private static void Put(float* vp, byte* cp, int v, float x, float y, float z, byte r, byte g, byte b, byte a)
    {
        vp[v * 3] = x;
        vp[v * 3 + 1] = y;
        vp[v * 3 + 2] = z;
        byte* c = cp + v * 4;
        c[0] = r;
        c[1] = g;
        c[2] = b;
        c[3] = a;
    }

    private static void Collapse(float* vp, int from, int count, float z)
    {
        for (int i = from; i < from + count; i++)
        {
            vp[i * 3] = 0f;
            vp[i * 3 + 1] = 0f;
            vp[i * 3 + 2] = z;
        }
    }

    internal static string Describe() =>
        $"art={(_frames != null ? "ok" : _searched ? "missing" : "-")} live={Live.Count} plays={Plays} guards={Guards} cuts={Cuts} noArt={NoArt}";
}
