using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 水に浸かった家具の絵に水面を描く: 家具の絵 (部屋の絵から持ち上げた家具は切り抜きの形) を型にした板を家具の手前に置き、
// 画素ごとの高さ (絵の一番下を床とした斜め投影の高さ・天板の高さで頭打ち) と水位を比べて、水より低い所を周りと同じ水の色で覆う (形と色は水のシェーダの _Mode 3)。
// 天板の上まで水が来たら天板もまだらに覆い、家具は水越しの影として残す。
// 板は家具の子なので動きには毎フレーム触らず、水面の線と沈み具合だけを 0.1 秒ごとに見直す
internal static partial class PropSim
{
    private const long SunkScanMs = 100;
    private const float SunkShow = 0.03f;    // 周りの水がこれより浅いと出さない
    private const float SunkTopWet = 0.02f;  // 天板の上の水がこれを越えたら沈み始める
    private const float SunkFade = 0.12f;    // 沈み始めてから家具ごと覆うまでの水の厚さ
    private const float SunkProbe = 0.12f;   // 足元の箱の外側の深さを見る距離
    private const float SunkStep = 0.01f;    // 水面の線がこれだけ動いたら書き直す
    private const float SunkFrontZ = -0.00005f; // 家具の絵のすぐ手前 (TieDz より小さく: 手前の家具より前に出ない)
    private const float TieDz = 0.0001f;        // 同じ奥行きで重なった家具の、手前の物をずらす量
    private const float RoomLayerDz = 0.0006f;  // 部屋の絵と同じ奥行きの家具を、水の板 (部屋の絵の 0.0004 手前) より手前へ出す量
    private const float TieZ = 0.001f;         // これより奥行きの差が小さい家具は同じ奥行きとみなす (本編は z を 0.001 刻みで置いている)
    private const float SunkShadowDz = -0.001f; // 影の中の写しの前後: 水の写し (-0.002) より手前・火の床の写し (0) より奥

    private sealed class Sunk
    {
        public GameObject Go;
        public SpriteRenderer Sr, Shadow; // Shadow = 影の中に見せる写し (ShadowView)
        public MaterialPropertyBlock Block;
        public Sprite Own;          // 切り抜きの形から作った絵 (片付けで消す)
        public Texture2D Base;      // 列ごとの床の線 (片付けで消す)
        public Texture2D Occ;       // 影の中の写しで抜く、手前の家具の形 (片付けで消す)
        public bool OccDirty = true;
        public Vector4 Range;       // 絵の uv の範囲 (u0, 1/du, v0, dv)。床の線を作れなかった板は 0
        public float Lvl = float.NaN, Cover = -1f;
        public bool Shown, Failed;
    }

    private static readonly List<Prop> Furnished = new(); // 高さの表に載った家具 (中の詰まった物と脚のある物)
    private static readonly List<Prop> Stills = new();    // そのうち動かない家具 (Props に入れない)

    // 同じ奥行き (z) で絵が重なった家具は、本編ではどちらが手前かが決まっておらず (カメラからの距離で入れ替わる)、
    // 家具の手前に置く水面の板が、奥の家具の分まで手前の家具の上に描かれる (棚の裏の箱の水が棚の上に出る)。
    // 足元が低い方 (手前) を少しだけ手前へずらして前後を決める。奥から順に決めるので、3 つ以上重なっても順に並ぶ。
    // 部屋の絵と同じ奥行きに置かれた家具 (壁際の箱や棚) は、水の板 (部屋の絵のわずかに手前) が家具の上に描かれるので、先に水の板より手前へ出す。
    // 動かない家具の絵の前後を決めるだけで、水と物の計算には使わない
    private static void Untie()
    {
        int n = Furnished.Count;
        var srs = new SpriteRenderer[n];
        var lo = new float[n];
        var hi = new Vector4[n]; // 絵の範囲 (x0, y0, x1, y1)
        var z0 = new float[n];
        var order = new List<int>();
        for (int i = 0; i < n; i++)
        {
            var q = Furnished[i];
            if (q.Lift != null || !q.Tr) continue;
            var sr = q.Tr.GetComponent<SpriteRenderer>();
            if (!sr || !sr.sprite) continue;
            var b = sr.bounds;
            Vector3 mn = b.min, mx = b.max;
            float front = DamageMap.FrontZ(FxMath.V2((mn.x + mx.x) * 0.5f, mn.y));
            if (DamageMap.ZScale(front) >= 1f && q.Z > front - RoomLayerDz && q.Z < front + TieZ) SetZ(q, front - RoomLayerDz);
            srs[i] = sr;
            lo[i] = mn.y;
            hi[i] = V4(mn.x, mn.y, mx.x, mx.y);
            z0[i] = q.Z;
            order.Add(i);
        }
        order.Sort((a, c) => lo[c].CompareTo(lo[a]));
        int moved = 0;
        System.Text.StringBuilder log = null; // ずらした組は 1 行にまとめて書く (1 組 1 行だと書き出しだけで数 ms)
        for (int oi = 0; oi < order.Count; oi++)
        {
            int back = order[oi];
            var bq = Furnished[back];
            for (int oj = oi + 1; oj < order.Count; oj++)
            {
                int front = order[oj];
                var fq = Furnished[front];
                if (lo[back] - lo[front] < 0.02f || MathF.Abs(z0[front] - z0[back]) > TieZ) continue;
                var r = hi[back];
                var f = hi[front];
                if (f.x >= r.z || r.x >= f.z || f.y >= r.w || r.y >= f.w) continue;
                if (fq.Z <= bq.Z - TieDz) continue;
                SetZ(fq, bq.Z - TieDz);
                moved++;
                (log ??= new System.Text.StringBuilder()).Append($" | {fq.Name} z={z0[front]:F5}->{fq.Z:F5} before {bq.Name} z={bq.Z:F5}");
            }
        }
        if (moved > 0) Plugin.Logger.LogInfo($"[PropSim] untied={moved}{log}");

        static void SetZ(Prop q, float z)
        {
            q.Z = z;
            var t = q.T0;
            t.z = z;
            q.T0 = t;
            var pos = q.Tr.position;
            q.Tr.position = FxMath.V3(pos.x, pos.y, z);
        }
    }
    private static readonly int MainTexId = Shader.PropertyToID("_MainTex"), LvlId = Shader.PropertyToID("_Lvl"), CoverId = Shader.PropertyToID("_Cover"), FaceHId = Shader.PropertyToID("_FaceH"),
        RiseId = Shader.PropertyToID("_Rise"), FlatTopId = Shader.PropertyToID("_FlatTop"), VPerUnitId = Shader.PropertyToID("_VPerUnit"), VBaseId = Shader.PropertyToID("_VBase"), SilRId = Shader.PropertyToID("_SilR"),
        BaseTexId = Shader.PropertyToID("_BaseTex"), BaseU0Id = Shader.PropertyToID("_BaseU0"), BaseUsId = Shader.PropertyToID("_BaseUS"),
        BaseV0Id = Shader.PropertyToID("_BaseV0"), BaseVsId = Shader.PropertyToID("_BaseVS"), BaseOnId = Shader.PropertyToID("_BaseOn"),
        OccTexId = Shader.PropertyToID("_FrontMask"), OccOnId = Shader.PropertyToID("_FrontOn");
    private static Material _sunkMat;
    private static long _sunkNext;
    private static int _sunkGen = -1;
    private static bool _sunkAny, _sunkForce; // _sunkForce = 確認用に全部の板を沈めたまま止める (bridge `props sunk force`)
    internal static bool SunkOff; // 確認用に家具の水面の板を出さない (bridge `props sunk off`)

    internal static void SunkTick()
    {
        try { SunkCore(); }
        catch (Exception e) { Plugin.Logger.LogError($"[PropSim] sunk: {e}"); SunkClear(); }
    }

    private static void SunkCore()
    {
        if (SunkOff) { SunkHideAll(); return; }
        if (_sunkForce || !_sunkAny && !WaterSim.Running) return;
        long now = Environment.TickCount64;
        if (now < _sunkNext) return;
        _sunkNext = now + SunkScanMs;
        if (GameClock.ShipGen != _sunkGen) { SunkClear(); _sunkGen = GameClock.ShipGen; }
        if (!_ready || !WaterSim.Running || !WaterSim.Ready || !MrpBundle.WadeMaterial) { SunkHideAll(); return; }
        foreach (var p in Furnished)
        {
            var s = p.Sk;
            if (!SunkDepth(p, out float by, out float around, out float over))
            {
                if (s != null && s.Shown) { if (s.Go) s.Go.SetActive(false); s.Shown = false; }
                continue;
            }
            if (s == null || (!s.Go && !s.Failed)) s = p.Sk = SunkMake(p);
            if (s.Failed) continue;
            if (!s.Shown) { s.Go.SetActive(true); s.Shown = true; _sunkAny = true; if (!UnderSupportPlate(p)) SunkShadow(s); }
            if (s.OccDirty) SunkOcc(p, s);
            // 水位 (足元の床から): 天板に水が乗ったら天板 + 乗った厚さ。それまでは周りの深さ (中の詰まった家具は天板で止まる)
            float top = p.Top / (float)WaterSim.Full;
            float cover = over > SunkTopWet ? Math.Min(1f, (over - SunkTopWet) / SunkFade) : 0f;
            float lvl = over > SunkTopWet ? Math.Max(around, top + over) : p.Legs ? around : Math.Min(around, top);
            if (MathF.Abs(lvl - s.Lvl) < SunkStep && MathF.Abs(cover - s.Cover) < 0.05f) continue;
            s.Lvl = lvl;
            s.Cover = cover;
            s.Block.SetFloat(LvlId, lvl);
            s.Block.SetFloat(CoverId, cover);
            s.Block.SetFloat(FaceHId, top);
            s.Sr.SetPropertyBlock(s.Block);
            if (s.Shadow) s.Shadow.SetPropertyBlock(s.Block);
        }
        DoorSunk(now);
        FrontSunk(now);
    }

    // 影の中: 板は影の板の裏になるので、影の板の手前に写しを置く (水の写しは家具の形を抜いてあるので、その上に沈み具合を描く)。
    // 出すたびに呼ぶ: 影のカメラがまだ無くて作れなかった写しを作り直し、家具の前後 (z) が変わって影の板の裏へ回った写しを手前へ置き直す
    private static void SunkShadow(Sunk s)
    {
        if (!ShadowView.Ready(out float qz)) return;
        if (!s.Shadow)
        {
            var sh = ShadowView.Copy(s.Go, s.Sr.sprite, ShadowView.Sunk(_sunkMat), FxMath.Rgba(1f, 1f, 1f, 1f), SunkShadowDz);
            if (!sh) return;
            sh.flipX = s.Sr.flipX;
            sh.flipY = s.Sr.flipY;
            sh.enabled = true;
            sh.SetPropertyBlock(s.Block);
            s.Shadow = sh;
            return;
        }
        var tr = s.Shadow.transform;
        var q = tr.position;
        if (MathF.Abs(q.z - (qz - SunkShadowDz)) > 1e-4f) tr.position = FxMath.V3(q.x, q.y, qz - SunkShadowDz);
    }

    // 影の中の写しは影の板の手前 (全部の家具より手前) にあるので、そのままだと手前の家具の上にも奥の家具の水面が描かれる
    // (棚の裏に隠れた箱の水面が棚の上に出る)。この家具より手前にある家具の絵の網目を、板の絵の範囲 (床の線と同じ uv の範囲) の
    // OccPx 四方の型に塗り、写しはそこを描かない。作るのは板を出した時と、近くの家具が動いて止まった時だけ
    private const int OccPx = 64;
    private static readonly List<SpriteRenderer> OccSrs = new();

    private static void SunkOcc(Prop p, Sunk s)
    {
        var b = s.Sr.bounds;
        Vector3 bmn = b.min, bmx = b.max;
        OccSrs.Clear();
        var owner = p.Lift != null ? p.Lift.Cut.transform : p.Tr;
        FrontSprites(Rect.MinMaxRect(bmn.x, bmn.y, bmx.x, bmx.y), owner.position.z, OccSrs);
        SunkOccFill(s);
    }

    // OccSrs の絵の網目を板の型に塗る
    private static void SunkOccFill(Sunk s)
    {
        s.OccDirty = false;
        if (s.Occ) { UnityEngine.Object.Destroy(s.Occ); s.Occ = null; }
        var sr = s.Sr;
        var sp = sr.sprite;
        byte[] buf = null;
        if (OccSrs.Count > 0 && UvMap(sp, out float au, out float bu, out float av, out float bv))
        {
            // 板の絵の範囲 (u0..u1, v0..v1) を 0..OccPx へ
            float u0 = 0f, us = 1f, v0 = 0f, vs = 1f;
            if (s.Range.y > 0f) { u0 = s.Range.x; us = s.Range.y; v0 = s.Range.z; vs = s.Range.w; }
            var toLocal = sr.transform.worldToLocalMatrix;
            float fx = sr.flipX ? -1f : 1f, fy = sr.flipY ? -1f : 1f;
            buf = new byte[OccPx * OccPx];
            bool any = false;
            foreach (var f in OccSrs)
            {
                // 手前の絵には絵を外して隠す物がある (コマ送りの途中・使っていない端末の画面)
                var fsp = f ? f.sprite : null;
                if (!fsp) continue;
                var v = fsp.vertices;
                var tri = fsp.triangles;
                var m = toLocal * f.transform.localToWorldMatrix;
                float gx = f.flipX ? -1f : 1f, gy = f.flipY ? -1f : 1f;
                var px = new float[v.Length];
                var py = new float[v.Length];
                // 手前の絵の不透明な所の型があれば、網目の中でも透明な所は抜かない (網目が四角の絵)
                var mask = SpriteAlphaMask.Get(fsp);
                var mu = mask != null ? new float[v.Length] : null;
                var mv = mask != null ? new float[v.Length] : null;
                for (int i = 0; i < v.Length; i++)
                {
                    float lx = v[i].x * gx, ly = v[i].y * gy;
                    // 手前の家具の頂点 → 板の局所 → 板の絵の頂点 (反転を戻す) → uv → 型の画素
                    float ox = (m.m00 * lx + m.m01 * ly + m.m03) * fx, oy = (m.m10 * lx + m.m11 * ly + m.m13) * fy;
                    px[i] = (au * ox + bu - u0) * us * OccPx - 0.5f;
                    py[i] = (av * oy + bv - v0) / vs * OccPx - 0.5f;
                    if (mask != null) { mu[i] = (v[i].x - mask.Min.x) * mask.W / mask.Size.x; mv[i] = (v[i].y - mask.Min.y) * mask.H / mask.Size.y; }
                }
                any |= FillTris(px, py, tri, buf, mask, mu, mv);
            }
            if (!any) buf = null;
        }
        if (buf != null)
        {
            s.Occ = MaskTexture(buf);
            s.Block.SetTexture(OccTexId, s.Occ);
        }
        s.Block.SetFloat(OccOnId, buf != null ? 1f : 0f);
        s.Sr.SetPropertyBlock(s.Block);
        if (s.Shadow) s.Shadow.SetPropertyBlock(s.Block);
        OccSrs.Clear();
    }

    // 絵の網目の頂点 (局所) から uv への写し (詰め込みで回されていない絵は軸ごとの 1 次式)
    private static bool UvMap(Sprite sp, out float au, out float bu, out float av, out float bv)
    {
        au = bu = av = bv = 0f;
        var v = sp.vertices;
        var uv = sp.uv;
        if (v.Length < 2 || uv.Length < v.Length) return false;
        int ix0 = 0, ix1 = 0, iy0 = 0, iy1 = 0;
        for (int i = 1; i < v.Length; i++)
        {
            if (v[i].x < v[ix0].x) ix0 = i;
            if (v[i].x > v[ix1].x) ix1 = i;
            if (v[i].y < v[iy0].y) iy0 = i;
            if (v[i].y > v[iy1].y) iy1 = i;
        }
        float dx = v[ix1].x - v[ix0].x, dy = v[iy1].y - v[iy0].y;
        if (dx < 1e-5f || dy < 1e-5f) return false;
        au = (uv[ix1].x - uv[ix0].x) / dx; bu = uv[ix0].x - au * v[ix0].x;
        av = (uv[iy1].y - uv[iy0].y) / dy; bv = uv[iy0].y - av * v[iy0].y;
        return true;
    }

    // 三角形を、画素の真ん中が入る所だけ 255 で塗る
    private static bool FillTris(float[] px, float[] py, ushort[] tri, byte[] buf, SpriteAlphaMask.Mask mask, float[] mu, float[] mv)
    {
        const int m = OccPx;
        bool any = false;
        for (int n = 0; n + 2 < tri.Length; n += 3)
        {
            int a = tri[n], b = tri[n + 1], c = tri[n + 2];
            float ax = px[a], ay = py[a], bx = px[b], by = py[b], cx = px[c], cy = py[c];
            float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (MathF.Abs(area) < 1e-6f) continue;
            int x0 = Math.Max(0, (int)MathF.Ceiling(MathF.Min(ax, MathF.Min(bx, cx)))), x1 = Math.Min(m - 1, (int)MathF.Floor(MathF.Max(ax, MathF.Max(bx, cx))));
            int y0 = Math.Max(0, (int)MathF.Ceiling(MathF.Min(ay, MathF.Min(by, cy)))), y1 = Math.Min(m - 1, (int)MathF.Floor(MathF.Max(ay, MathF.Max(by, cy))));
            float inv = 1f / area;
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float w0 = ((bx - x) * (cy - y) - (by - y) * (cx - x)) * inv;
                float w1 = ((cx - x) * (ay - y) - (cy - y) * (ax - x)) * inv;
                if (w0 < 0f || w1 < 0f || w0 + w1 > 1f) continue;
                if (mask != null)
                {
                    float w2 = 1f - w0 - w1;
                    int iu = Math.Min(mask.W - 1, Math.Max(0, (int)MathF.Floor(w0 * mu[a] + w1 * mu[b] + w2 * mu[c])));
                    int iv = Math.Min(mask.H - 1, Math.Max(0, (int)MathF.Floor(w0 * mv[a] + w1 * mv[b] + w2 * mv[c])));
                    if (mask.Px[iv * mask.W + iu] == 0) continue;
                }
                buf[y * m + x] = 255;
                any = true;
            }
        }
        return any;
    }

    private static unsafe Texture2D MaskTexture(byte[] buf)
    {
        var tex = new Texture2D(OccPx, OccPx, TextureFormat.R8, false) { name = "MrpSunkOcc", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        fixed (byte* b = buf) tex.LoadRawTextureData((IntPtr)b, buf.Length);
        tex.Apply(false, true);
        return tex;
    }

    // 見せる姿 (D) の足元の箱の下の縁 (世界の y)・周りの水の深さ・天板より上の水の厚さ。出さない時は false
    private static bool SunkDepth(Prop p, out float by, out float around, out float over)
    {
        by = around = over = 0f;
        if (!FootBox(p, p.D, out int cx, out int cy, out int hx, out int hy, out int top)) return false;
        float x = _org.x + cx / (float)Unit, y = _org.y + cy / (float)Unit, wx = hx / (float)Unit, wy = hy / (float)Unit;
        by = y - wy;
        // 机の上に載ったままの物は床の水に浸からない (水は机の下を通る)。天板の上の水が物の高さを越えたら沈む
        if (PerchedStill(p))
        {
            var sup = SupportOf(p);
            if (sup == null) return false; // 載っている家具が分からない物は従来どおり沈めない
            around = SupportOver(sup);
            over = around - top / (float)WaterSim.Full;
            return around >= SunkShow || over > SunkTopWet;
        }
        around = Math.Max(WaterSim.CurrentAt(FxMath.V2(x, by - SunkProbe), out _, out _),
            Math.Max(WaterSim.CurrentAt(FxMath.V2(x - wx - SunkProbe, y), out _, out _), WaterSim.CurrentAt(FxMath.V2(x + wx + SunkProbe, y), out _, out _)));
        // 中の詰まった家具の上の升の水は天板の上の水。脚のある家具は下を通る水が天板を越えた分
        float mid = WaterSim.CurrentAt(FxMath.V2(x, y), out _, out _);
        over = p.Legs ? Math.Max(around, mid) - top / (float)WaterSim.Full : mid;
        return around >= SunkShow || over > SunkTopWet;
    }

    // 机の上に載ったまま動いていない物
    private static bool PerchedStill(Prop p) => p.Perch && Math.Abs(p.D.Px - p.A0x) + Math.Abs(p.D.Py - p.A0y) < PerchMove;

    // 影の中は載っている机の水面の板が物の上まで覆うので、物自身の写しは出さない (二重にかぶる)
    private static bool UnderSupportPlate(Prop p)
    {
        if (!PerchedStill(p)) return false;
        return SupportPlated(SupportOf(p));
    }

    // 載っている家具の板が出ている (または同じ刻みのこの後に出る: 載っている物の方が先に見られる)
    private static bool SupportPlated(Prop sup)
    {
        if (sup == null) return false;
        if (sup.Sk != null && sup.Sk.Failed) return false;
        return sup.Sk != null && sup.Sk.Shown || SunkDepth(sup, out _, out _, out _);
    }

    // 載っている家具 (動かない物の足元に載っている間だけ。1 回探して持つ)
    private static Prop SupportOf(Prop p)
    {
        if (!p.SupportTried)
        {
            p.SupportTried = true;
            p.Support = SupportAt(_org.x + p.D.Px / (float)Unit, _org.y + p.D.Py / (float)Unit, p);
        }
        return p.Support;
    }

    // 点 (世界・ex = 横の半分の広がり) が足元の箱にすっぽり載る家具のうち、いちばん広い物 (机の上の別の小物と自分は除く)。
    // 天板の上の物は斜めに見上げた分だけ画面の上へずれるので、箱の上の縁は天板の高さ × 斜めの倍率だけ広げる
    private static Prop SupportAt(float px, float py, Prop self, float ex = 0f)
    {
        const float Margin = 0.1f, Rise = 0.6f;
        Prop best = null;
        float bestArea = 0f;
        foreach (var q in Furnished)
        {
            if (q == self || q.Perch || q.Kind == Kind.Small) continue;
            if (!FootBox(q, q.D, out int cx, out int cy, out int hx, out int hy, out _)) continue;
            float qx = _org.x + cx / (float)Unit, qy = _org.y + cy / (float)Unit, wx = hx / (float)Unit, wy = hy / (float)Unit;
            float up = q.Top / (float)WaterSim.Full * Rise;
            if (MathF.Abs(px - qx) + ex > wx + Margin || py < qy - wy - Margin || py > qy + wy + up + Margin) continue;
            float area = wx * wy;
            if (area > bestArea) { bestArea = area; best = q; }
        }
        return best;
    }

    // 家具の天板の上の水の厚さ (単位・0 以上): 足元の箱の真ん中の升と周りから見る (SunkDepth の over と同じ)
    private static float SupportOver(Prop sup)
    {
        SunkDepth(sup, out _, out _, out float over);
        return Math.Max(0f, over);
    }

    private static Sunk SunkMake(Prop p)
    {
        var s = new Sunk();
        Transform parent;
        Sprite sp;
        Vector3 local = FxMath.V3(0f, 0f, SunkFrontZ), scale = FxMath.V3(1f, 1f, 1f);
        bool flipX = false, flipY = false;
        var mode = SpriteDrawMode.Simple;
        Vector2 size = default;
        float vBase = 0f, vPerUnit; // 床の線 (絵の一番下) の v と、世界の 1 単位が v でいくつか (画素の高さを測るのに使う)
        byte[] baseCol = null;      // 列ごとの床の線 (baseUv の範囲で 0..255)
        var baseUv = V4(0f, 1f, 0f, 1f);
        BreakPieces.Room art = null; // 持ち上げた家具の型の中で家具の画素を見分ける部屋の絵
        if (p.Lift != null)
        {
            var l = p.Lift;
            // 動く家具は最初に動いた時に切り抜くので、水に浸かった時にまだなら先に切り抜く
            if (!l.Cut && !(l.Still ? FurnitureLift.EnsureStill(l) : FurnitureLift.Ensure(l))) { s.Failed = true; return s; }
            if (!l.Cut || !l.Shape) { s.Failed = true; return s; }
            parent = l.Cut;
            // 形は四角い画素なので、幅に合わせた大きさで作って縦を伸ばす
            var w = l.ShapeWorld;
            sp = s.Own = Sprite.Create(l.Shape, new Rect(0, 0, l.Shape.width, l.Shape.height), FxMath.V2(0.5f, 0.5f), l.Shape.width / w.width, 0, SpriteMeshType.FullRect);
            sp.name = "MrpSunk";
            local = FxMath.V3(l.ShapeOff.x, l.ShapeOff.y, SunkFrontZ);
            scale = FxMath.V3(l.ShapeScale.x, l.ShapeScale.y * (w.height / w.width) * (l.Shape.width / (float)l.Shape.height), 1f);
            vPerUnit = 1f / Math.Max(w.height, 0.01f);
            // 床の線は広げる前の当たり判定の下の縁 (広げた所は床なので、そこから測ると家具の高さが余白の分だけずれる)
            var foot = l.ColIn ?? l.ShapeIn;
            if (foot != null) baseCol = ShapeBase(foot, FurnitureLift.ShapePx, out baseUv);
            art = l.Art;
        }
        else
        {
            var src = p.Tr ? p.Tr.GetComponent<SpriteRenderer>() : null;
            if (!src || !src.sprite) { s.Failed = true; return s; }
            parent = p.Tr;
            sp = src.sprite;
            flipX = src.flipX;
            flipY = src.flipY;
            // 横に並べて描く絵 (手すりなど) は同じ並べ方で重ねる
            mode = src.drawMode;
            if (mode != SpriteDrawMode.Simple) size = src.size;
            baseCol = SpriteFloorLine(sp, flipY, p.Tr.lossyScale.y, out vPerUnit, out vBase, out baseUv);
        }
        SunkPlate(s, parent, sp, local, scale, flipX, flipY, mode, size, vPerUnit, vBase, baseCol, baseUv);
        s.Block.SetFloat(SilRId, p.Lift != null ? 1f : 0f);
        if (p.Upright) s.Block.SetFloat(RiseId, 1f);
        if (p.Deep) s.Block.SetFloat(FlatTopId, 1f);
        if (art != null && art.Tex) SetRoomTest(s.Block, p.Lift, art);
        s.Go.SetActive(false);
        return s;
    }

    // 絵そのものを型にする板の床の線 = 網目の一番下の頂点 (網目は絵の不透明な所に沿う)。上下反転の絵は v の上端が下。sy = 絵の縦の拡大
    private static byte[] SpriteFloorLine(Sprite sp, bool flipY, float sy, out float vPerUnit, out float vBase, out Vector4 baseUv)
    {
        var tex = sp.texture;
        vPerUnit = sp.pixelsPerUnit / (Math.Max(MathF.Abs(sy), 1e-3f) * tex.height);
        var vs = sp.vertices;
        var uv = sp.uv;
        float lowY = float.MaxValue, highY = float.MinValue, vLow = 0f, vHigh = 0f;
        for (int i = 0; i < vs.Length && i < uv.Length; i++)
        {
            float y = vs[i].y;
            if (y < lowY) { lowY = y; vLow = uv[i].y; }
            if (y > highY) { highY = y; vHigh = uv[i].y; }
        }
        bool down = flipY != sy < 0f;
        vBase = down ? vHigh : vLow;
        if (down) vPerUnit = -vPerUnit;
        return MeshBase(uv, sp.triangles, down, out baseUv);
    }

    // 板の GameObject と、型・床の線のブロックを作る (隠したまま)
    private static void SunkPlate(Sunk s, Transform parent, Sprite sp, Vector3 local, Vector3 scale, bool flipX, bool flipY, SpriteDrawMode mode, Vector2 size,
        float vPerUnit, float vBase, byte[] baseCol, Vector4 baseUv)
    {
        if (!_sunkMat)
        {
            _sunkMat = new Material(MrpBundle.WadeMaterial) { name = "MrpSunk", hideFlags = HideFlags.DontUnloadUnusedAsset };
            _sunkMat.SetFloat("_Mode", 3f);
        }
        var go = new GameObject("MrpSunk") { layer = parent.gameObject.layer };
        s.Go = go; // 途中で失敗しても片付けで消せるよう先に持つ
        go.SetActive(false);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = local;
        go.transform.localScale = scale;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        if (mode != SpriteDrawMode.Simple) { sr.drawMode = mode; sr.size = size; }
        sr.flipX = flipX;
        sr.flipY = flipY;
        sr.sharedMaterial = _sunkMat;
        s.Sr = sr;
        s.Block = new MaterialPropertyBlock();
        // SpriteRenderer にブロックを付けるとスプライトの絵が外れる (シェーダの既定の黒になり、型が無くて何も描かれない) ので、絵もブロックで渡す
        s.Block.SetTexture(MainTexId, sp.texture);
        s.Block.SetFloat(VPerUnitId, vPerUnit);
        s.Block.SetFloat(VBaseId, vBase);
        if (baseCol != null)
        {
            s.Base = BaseTexture(baseCol);
            s.Block.SetTexture(BaseTexId, s.Base);
            // Android 版の MaterialPropertyBlock には SetVector が無いので 4 つの Float で渡す
            s.Block.SetFloat(BaseU0Id, baseUv.x);
            s.Block.SetFloat(BaseUsId, baseUv.y);
            s.Block.SetFloat(BaseV0Id, baseUv.z);
            s.Block.SetFloat(BaseVsId, baseUv.w);
            s.Range = baseUv;
        }
        s.Block.SetFloat(BaseOnId, baseCol != null ? 1f : 0f);
    }

    // 持ち上げた家具の型は当たり判定を広げてあり、広げた所の多くは床の画素 (切り抜きの絵は描かない所)。そこに板を描くと
    // 床の水の上に板の色が重なり、天板扱いの余白が天板の水と一緒に後から覆われる。切り抜きと同じ見分け方
    // (部屋の絵が、その場所にあるはずのきれいな床の色と違う画素) を板にも渡し、家具の画素だけに描かせる。
    // 型の uv は元の場所の広げた四角 (ShapeWorld) なので、家具が動いても元の場所の部屋の絵を引く
    private static readonly int RoomTexId = Shader.PropertyToID("_RoomTex"), RoomOnId = Shader.PropertyToID("_RoomOn"),
        RoomSxId = Shader.PropertyToID("_RoomSx"), RoomSyId = Shader.PropertyToID("_RoomSy"), RoomOxId = Shader.PropertyToID("_RoomOx"), RoomOyId = Shader.PropertyToID("_RoomOy"),
        RoomDxId = Shader.PropertyToID("_RoomDx"), RoomDyId = Shader.PropertyToID("_RoomDy"),
        FlXId = Shader.PropertyToID("_FlX"), FlYId = Shader.PropertyToID("_FlY"), FlTxId = Shader.PropertyToID("_FlTx"), FlTyId = Shader.PropertyToID("_FlTy"),
        ShXId = Shader.PropertyToID("_ShX"), ShYId = Shader.PropertyToID("_ShY"), ShWId = Shader.PropertyToID("_ShW"), ShHId = Shader.PropertyToID("_ShH");

    private static void SetRoomTest(MaterialPropertyBlock b, FurnitureLift.Lift l, BreakPieces.Room art)
    {
        var tex = art.Tex;
        var fp = FurnitureLift.FloorPatch(l.Floor, art);
        var w = l.ShapeWorld;
        b.SetTexture(RoomTexId, tex);
        // Android 版の MaterialPropertyBlock には SetVector が無いので Float で渡す
        b.SetFloat(RoomSxId, art.Dx * tex.width);
        b.SetFloat(RoomSyId, art.Dy * tex.height);
        b.SetFloat(RoomOxId, art.W0x);
        b.SetFloat(RoomOyId, art.W0y);
        b.SetFloat(RoomDxId, art.Dx);
        b.SetFloat(RoomDyId, art.Dy);
        b.SetFloat(FlXId, fp.x);
        b.SetFloat(FlYId, fp.y);
        b.SetFloat(FlTxId, fp.z);
        b.SetFloat(FlTyId, fp.w);
        b.SetFloat(ShXId, w.xMin);
        b.SetFloat(ShYId, w.yMin);
        b.SetFloat(ShWId, w.width);
        b.SetFloat(ShHId, w.height);
        b.SetFloat(RoomOnId, 1f);
    }

    // 列ごとの床の線: 斜めに置いた箱や脚の長さが違う家具は、列ごとに絵の下の縁の高さが違う。
    // 絵全体の一番下を床にすると、水の線が一番下の角から水平に引かれて形を無視した帯になる。
    // 型の u を BaseCols 列に割り、列ごとに絵 (網目の三角形) の一番下 (上下反転の絵は一番上) の v を 0..255 で持つ。
    // 家具が動いても絵の中の形は同じなので、作るのは板を作る時の 1 回だけ
    private const int BaseCols = 64;

    private static byte[] MeshBase(Vector2[] uv, ushort[] tri, bool down, out Vector4 range)
    {
        range = V4(0f, 1f, 0f, 1f);
        if (uv == null || tri == null || uv.Length < 3 || tri.Length < 3) return null;
        float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
        foreach (var p in uv)
        {
            u0 = Math.Min(u0, p.x); u1 = Math.Max(u1, p.x);
            v0 = Math.Min(v0, p.y); v1 = Math.Max(v1, p.y);
        }
        if (u1 - u0 < 1e-6f || v1 - v0 < 1e-6f) return null;
        var col = new float[BaseCols];
        for (int c = 0; c < BaseCols; c++) col[c] = float.NaN;
        for (int t = 0; t + 2 < tri.Length; t += 3)
        {
            var a = uv[tri[t]];
            var b = uv[tri[t + 1]];
            var d = uv[tri[t + 2]];
            float tu0 = Math.Min(a.x, Math.Min(b.x, d.x)), tu1 = Math.Max(a.x, Math.Max(b.x, d.x));
            int c0 = Math.Max(0, (int)MathF.Floor((tu0 - u0) / (u1 - u0) * BaseCols - 0.5f));
            int c1 = Math.Min(BaseCols - 1, (int)MathF.Ceiling((tu1 - u0) / (u1 - u0) * BaseCols - 0.5f));
            for (int c = c0; c <= c1; c++)
            {
                float u = u0 + (c + 0.5f) / BaseCols * (u1 - u0);
                if (u < tu0 || u > tu1) continue;
                // 三角形を縦の線で切った区間の下 (上下反転なら上) の端
                float lo = float.MaxValue, hi = float.MinValue;
                Cut(a, b, u, ref lo, ref hi);
                Cut(b, d, u, ref lo, ref hi);
                Cut(d, a, u, ref lo, ref hi);
                if (lo > hi) continue;
                float e = down ? hi : lo;
                if (float.IsNaN(col[c]) || (down ? e > col[c] : e < col[c])) col[c] = e;
            }
        }
        range = V4(u0, 1f / (u1 - u0), v0, v1 - v0);
        return Quantize(col, v0, v1 - v0);

        static void Cut(Vector2 p, Vector2 q, float u, ref float lo, ref float hi)
        {
            float pu = p.x, qu = q.x;
            if (u < Math.Min(pu, qu) || u > Math.Max(pu, qu)) return;
            // 縦の辺は両端とも区間に入る
            if (MathF.Abs(qu - pu) < 1e-7f) { lo = Math.Min(lo, Math.Min(p.y, q.y)); hi = Math.Max(hi, Math.Max(p.y, q.y)); return; }
            float v = p.y + (q.y - p.y) * (u - pu) / (qu - pu);
            lo = Math.Min(lo, v); hi = Math.Max(hi, v);
        }
    }

    // 持ち上げた家具: 形 (左下原点・n 四方) の列ごとの一番下の行。型の uv は形そのもの (0..1)
    private static byte[] ShapeBase(bool[] shape, int n, out Vector4 range)
    {
        range = V4(0f, 1f, 0f, 1f);
        var col = new float[BaseCols];
        bool any = false;
        for (int c = 0; c < BaseCols; c++)
        {
            int x = Math.Min(n - 1, c * n / BaseCols);
            col[c] = float.NaN;
            for (int y = 0; y < n; y++)
                if (shape[y * n + x]) { col[c] = y / (float)n; any = true; break; }
        }
        return any ? Quantize(col, 0f, 1f) : null;
    }

    // 列の値 (v) を v0..v0+dv の 0..255 へ。絵の無い列は隣の列の値で埋める (端の列は双線形で隣と混ざる)
    private static byte[] Quantize(float[] col, float v0, float dv)
    {
        int n = col.Length, first = -1;
        for (int c = 0; c < n; c++) if (!float.IsNaN(col[c])) { first = c; break; }
        if (first < 0) return null;
        for (int c = 0; c < first; c++) col[c] = col[first];
        for (int c = first + 1; c < n; c++) if (float.IsNaN(col[c])) col[c] = col[c - 1];
        var o = new byte[n];
        for (int c = 0; c < n; c++) o[c] = (byte)Math.Clamp((int)MathF.Round((col[c] - v0) / dv * 255f), 0, 255);
        return o;
    }

    private static Vector4 V4(float x, float y, float z, float w)
    {
        Vector4 v = default;
        v.x = x; v.y = y; v.z = z; v.w = w;
        return v;
    }

    private static unsafe Texture2D BaseTexture(byte[] col)
    {
        var tex = new Texture2D(col.Length, 1, TextureFormat.R8, false) { name = "MrpSunkBase", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        fixed (byte* b = col) tex.LoadRawTextureData((IntPtr)b, col.Length);
        tex.Apply(false, true);
        return tex;
    }

    // 水の影の写しで抜く絵 (r と重なり、水 z より手前の物)。小物は水に流されて動き続けるので入れない。
    // 部屋の絵から持ち上げた家具は床マスクが抜くので入れない
    internal static void FrontSprites(Rect r, float z, List<SpriteRenderer> into)
    {
        if (!_ready) return;
        FrontOfList(Props, r, z, into);
        FrontOfList(Stills, r, z, into);
    }

    // 抜き型に塗る家具の絵を全部 (絵の不透明な所の型を船ができた時にまとめて作る用)
    internal static void AllFrontSprites(List<SpriteRenderer> into)
    {
        if (!_ready) return;
        foreach (var list in new[] { Props, Stills })
            foreach (var p in list)
            {
                if (p.Kind == Kind.Small || p.Lift != null || !p.Tr) continue;
                if (p.Front == null) FrontOf(p);
                foreach (var sr in p.Front) if (sr && sr.sprite) into.Add(sr);
            }
    }

    private static void FrontOfList(List<Prop> list, Rect r, float z, List<SpriteRenderer> into)
    {
        foreach (var p in list)
        {
            if (p.Kind == Kind.Small || p.Lift != null || p.Hidden || !p.Tr) continue;
            if (p.Front == null) FrontOf(p);
            // 足元から絵の端まで (FrontR) で先に間引く (絵の範囲を引くのは本編の呼び出しなので、掛かりそうな物だけ)
            var c = p.FrontAt;
            if (c.x + p.FrontR <= r.xMin || c.x - p.FrontR >= r.xMax || c.y + p.FrontR <= r.yMin || c.y - p.FrontR >= r.yMax) continue;
            foreach (var sr in p.Front)
            {
                if (!sr || !sr.enabled || !sr.sprite || !sr.gameObject.activeInHierarchy) continue;
                var b = sr.bounds;
                if (b.max.x <= r.xMin || b.min.x >= r.xMax || b.max.y <= r.yMin || b.min.y >= r.yMax) continue;
                if (sr.transform.position.z >= z) continue;
                into.Add(sr);
            }
        }
    }

    private static void FrontOf(Prop p)
    {
        var list = new List<SpriteRenderer>();
        var at = p.Tr.position;
        float r = 0f;
        foreach (var sr in p.Tr.GetComponentsInChildren<SpriteRenderer>(false))
        {
            if (sr.gameObject.name.StartsWith("Mrp", StringComparison.Ordinal)) continue;
            list.Add(sr);
            var b = sr.bounds;
            r = Math.Max(r, MathF.Sqrt((b.center.x - at.x) * (b.center.x - at.x) + (b.center.y - at.y) * (b.center.y - at.y)) + b.extents.magnitude);
        }
        p.Front = list.ToArray();
        p.FrontAt = at;
        p.FrontR = r + 0.1f;
    }

    // 動いた家具が止まった: 元いた所と止まった所の抜き型を作り直す
    private static void FrontSettled(Prop p)
    {
        if (p.Kind == Kind.Small || p.Lift != null || !p.Tr || p.Front == null) return;
        var at = p.Tr.position;
        WaterArt.FrontMoved(FxMath.V2(p.FrontAt.x, p.FrontAt.y), FxMath.V2(at.x, at.y), p.FrontR);
        // 近くの家具の水面の写しの抜き型 (手前の家具の形) も作り直す
        foreach (var q in Furnished)
        {
            if (q.Sk == null || q.Front == null) continue;
            var c = q.FrontAt;
            float rr = p.FrontR + q.FrontR;
            if (Near(p.FrontAt) || Near(at)) q.Sk.OccDirty = true;

            bool Near(Vector3 o) => (o.x - c.x) * (o.x - c.x) + (o.y - c.y) * (o.y - c.y) < rr * rr;
        }
        p.FrontAt = at;
    }

    private static void SunkReport(bool force, Action<string> reply)
    {
        int n = 0;
        _sunkForce = force;
        foreach (var p in Furnished)
        {
            bool wet = SunkDepth(p, out float by, out float around, out float over);
            var s = p.Sk;
            if (!wet && s == null) continue;
            n++;
            reply($"SUNK {p.Name} sup={(p.Perch ? SupportOf(p)?.Name ?? "none" : "-")} legs={p.Legs} by={by:0.###} around={around:0.###} over={over:0.###} shown={s?.Shown} failed={s?.Failed} lvl={s?.Lvl:0.###} cover={s?.Cover:0.##}");
            if (force && s != null && s.Sr) { s.Block.SetFloat(LvlId, 100f); s.Block.SetFloat(CoverId, 1f); s.Sr.SetPropertyBlock(s.Block); if (s.Shadow) s.Shadow.SetPropertyBlock(s.Block); s.Lvl = float.NaN; }
        }
        reply($"OK props sunk n={n} of {Furnished.Count}");
    }

    private static void SunkHideAll()
    {
        foreach (var p in Furnished)
            if (p.Sk != null && p.Sk.Shown) { if (p.Sk.Go) p.Sk.Go.SetActive(false); p.Sk.Shown = false; }
        DoorSunkHideAll();
        FrontSunkHideAll();
        _sunkAny = false;
    }

    private static void SunkClear()
    {
        foreach (var p in Furnished)
        {
            var s = p.Sk;
            if (s == null) continue;
            if (s.Go) UnityEngine.Object.Destroy(s.Go);
            if (s.Own) UnityEngine.Object.Destroy(s.Own);
            if (s.Base) UnityEngine.Object.Destroy(s.Base);
            if (s.Occ) UnityEngine.Object.Destroy(s.Occ);
            p.Sk = null;
        }
        DoorSunkClear();
        FrontSunkClear();
        _sunkAny = false;
    }
}
