using System;
using System.Threading;
using System.Threading.Tasks;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// ユビキタスのドローンの絵: つやのある黒い丸い胴 (正面にクルーと同じ水色のバイザー)・4 本の腕・筒に入ったプロペラ 4 つを
// 立体の形 (距離の式) で持ち、斜め上から見た光線を当てて陰影・照り・縁の光・奥まった所の暗さを付けて RGBA に焼く (光は左上から)。
// 部品の境と輪郭には太い黒の縁取り。プロペラは筒の中の平面に重ねる: 止まっている時は羽 2 枚 (十字)、飛んでいる時は回った残像の円盤 (2 コマ)。
// 床の上の向き 16 方向 × 前後の傾き 4 段を焼く。左右対称の形なので左向きの半分は右向きの絵を左右反転して使う (焼くのは 9 方向)。
// 計算は C# の数値だけなので作業スレッドで作る: 傾き無しの 9 方向を先に焼き、傾いた絵はその後で焼く (焼けるまでは傾き無しを出す)
internal static class DroneArt
{
    private const float Sin = 0.58f;                  // 床の上の奥行きは画面でこの倍に縮む (ポータルの装置と同じ見下ろし)
    private static readonly float Cos = MathF.Sqrt(1f - Sin * Sin);
    private const float Left = -0.72f, Right = 0.72f, Bottom = -0.36f, Top = 0.86f;
    private const int Ppu = 160, SS = 2;
    private const float Shown = 0.85f;                // 試合での大きさ (形の単位 1 = 世界の 0.85)
    private static readonly int W = (int)((Right - Left) * Ppu), H = (int)((Top - Bottom) * Ppu);

    internal const int Yaws = 16;                     // 床の上の向きの数 (22.5° ごと)
    private const int Half = Yaws / 2 + 1;            // 焼く向き (真下から右回りに真上まで)
    internal const int Level = 1;                     // 傾き無しの段
    internal static readonly float[] PitchDeg = { -10f, 0f, 10f, 20f }; // 前へ倒れる角 (負 = 機首上げ)
    private const int Pitches = 4, Kinds = 3;          // 絵の種類: 0, 1 = 回る羽の残像 / 2 = 止まった羽 (傾き無しだけ)
    internal const int Still = 2;

    // 胴の真ん中 (傾ける軸・画面の原点)
    private const float PivotY = 0.30f;

    // 形 (x = 右・y = 上・z = 奥)
    private const float BodyX = 0f, BodyY = 0.30f, BodyZ = 0f, BodyRx = 0.25f, BodyRy = 0.20f, BodyRz = 0.25f;
    private const float VisY = 0.335f, VisZ = -0.165f, VisRx = 0.185f, VisRy = 0.095f, VisRz = 0.11f;
    private static readonly float[] DuctX = { -0.38f, 0.38f, -0.38f, 0.38f }, DuctZ = { 0.30f, 0.30f, -0.26f, -0.26f };
    private const float DR = 0.18f, Dr = 0.15f, DY0 = 0.235f, DY1 = 0.315f, PY = 0.275f;

    private static Task _level, _tilt;
    private static bool _failed;
    // 焼く間も主スレッドと本編の作業スレッドの分のコアを空けておく (スマホでかくつかないように)
    private static readonly ParallelOptions Cores = new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) };
    private static readonly byte[][] Px = new byte[Half * Pitches * Kinds][];
    private static readonly int[] Crop = new int[Half * Pitches * Kinds * 4];
    private static readonly int[] Ready = new int[Half * Pitches];   // 1 = その向きと傾きの絵が焼けた
    private static readonly Sprite[] Sprites = new Sprite[Half * Pitches * Kinds];
    private static Sprite _shadow;

    internal static void Prewarm()
    {
        if (_level != null || _failed) return;
        _level = Task.Run(() => { for (int u = 0; u < Half; u++) BakeView(u, Level); });
        _tilt = _level.ContinueWith(_ => Parallel.For(0, Half * (Pitches - 1), Cores, i =>
        {
            int u = i % Half, p = i / Half;
            BakeView(u, p >= Level ? p + 1 : p);
        }));
    }

    // 床の上の向き (ラジアン・右 = 0・上 = π/2) → 0〜15 の向きの番号
    internal static int YawIndex(float rad)
    {
        int k = (int)MathF.Round(rad * (Yaws / (2f * MathF.PI)));
        k %= Yaws;
        return k < 0 ? k + Yaws : k;
    }

    // 向きの番号・傾きの段・種類 (0, 1 = 回る羽 / Still = 止まった羽) の絵。flip = 左右反転して出す。焼くのに失敗していたら null (何も出さない)
    internal static Sprite Frame(int yaw, int pitch, int kind, out bool flip)
    {
        Prewarm();
        yaw &= Yaws - 1;
        flip = false;
        if (_failed) return null;
        // 焼いた向き u は真下 (12) から右回りに真上 (4) まで。左向き (5〜11) は鏡に映した右向き
        flip = yaw >= 5 && yaw <= 11;
        int u = flip ? (12 - yaw) & (Yaws - 1) : (yaw + 4) & (Yaws - 1);
        if (kind == Still) pitch = Level;
        if (pitch != Level && Volatile.Read(ref Ready[u * Pitches + pitch]) == 0) pitch = Level;
        if (pitch == Level && Volatile.Read(ref Ready[u * Pitches + Level]) == 0)
        {
            // 傾き無しの絵は出す前に必ず焼き終える (役職が付いた時に作り始めているので普段は待たない)
            try { _level.Wait(); }
            catch (Exception e)
            {
                // 焼き直しても同じ所で落ちるので、この起動の間はドローンの絵を出さない (毎 tick 例外を出さない)
                _failed = true;
                Plugin.Logger.LogError($"DroneArt: {e}");
                return null;
            }
        }
        int i = (u * Pitches + pitch) * Kinds + kind;
        var sp = Sprites[i];
        if (sp is not null) return sp; // 消さない絵なので Unity の生存確認は要らない
        return Sprites[i] = Make(i, "MrpDrone" + i);
    }

    // 黒で塗って使う柔らかい楕円
    internal static Sprite Shadow => _shadow ? _shadow : _shadow = FxSprites.Rect(64, 24, (x, y) => FxSprites.Soft(x, y) * 0.9f, "MrpDroneShadow");

    private static unsafe Sprite Make(int i, string name)
    {
        var px = Px[i];
        int x0 = Crop[i * 4], y0 = Crop[i * 4 + 1], w = Crop[i * 4 + 2], h = Crop[i * 4 + 3];
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name };
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        fixed (byte* q = px) tex.LoadRawTextureData((IntPtr)q, px.Length);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Px[i] = null; // 絵はもう GPU にあるので手元の画素は捨てる
        // 原点 = 胴の真ん中 (切り抜く前の絵の中の位置から)
        float ox = (0f - Left) * Ppu - x0, oy = (PivotY * Cos - Bottom) * Ppu - y0;
        var sp = Sprite.Create(tex, new UnityEngine.Rect(0, 0, w, h), new Vector2(ox / w, oy / h), Ppu / Shown);
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    // ---- 距離の式 ----

    private static float Len(float x, float y, float z) => MathF.Sqrt(x * x + y * y + z * z);

    private static float Ell(float px, float py, float pz, float cx, float cy, float cz, float rx, float ry, float rz)
    {
        float qx = (px - cx) / rx, qy = (py - cy) / ry, qz = (pz - cz) / rz;
        float k0 = Len(qx, qy, qz), k1 = Len(qx / rx, qy / ry, qz / rz);
        return k0 * (k0 - 1f) / MathF.Max(k1, 1e-6f);
    }

    private static float Cap(float px, float py, float pz, float ax, float ay, float az, float bx, float by, float bz, float r)
    {
        float pax = px - ax, pay = py - ay, paz = pz - az, bax = bx - ax, bay = by - ay, baz = bz - az;
        float h = Math.Clamp((pax * bax + pay * bay + paz * baz) / (bax * bax + bay * bay + baz * baz), 0f, 1f);
        return Len(pax - bax * h, pay - bay * h, paz - baz * h) - r;
    }

    private static float Box(float qx, float qy, float qz, float hx, float hy, float hz)
    {
        float dx = MathF.Abs(qx) - hx, dy = MathF.Abs(qy) - hy, dz = MathF.Abs(qz) - hz;
        return Len(MathF.Max(dx, 0f), MathF.Max(dy, 0f), MathF.Max(dz, 0f)) + MathF.Min(MathF.Max(dx, MathF.Max(dy, dz)), 0f);
    }

    // 1 胴・2 バイザー・3 腕・4 筒・5 羽の軸と下の支え
    private static float Scene(float x, float y, float z, out int id)
    {
        float d = Ell(x, y, z, BodyX, BodyY, BodyZ, BodyRx, BodyRy, BodyRz);
        id = 1;
        float v = Ell(x, y, z, 0f, VisY, VisZ, VisRx, VisRy, VisRz);
        if (v < d) { d = v; id = 2; }
        for (int k = 0; k < 4; k++)
        {
            float cx = DuctX[k], cz = DuctZ[k];
            float a = Cap(x, y, z, cx * 0.35f, 0.27f, cz * 0.35f, cx * 0.62f, 0.272f, cz * 0.62f, 0.026f);
            if (a < d) { d = a; id = 3; }
            float rho = MathF.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
            if (rho - DR - 0.02f > d) continue; // この筒は今の距離より遠い
            float qx = MathF.Abs(rho - (DR + Dr) * 0.5f) - (DR - Dr) * 0.5f, qy = MathF.Abs(y - (DY0 + DY1) * 0.5f) - (DY1 - DY0) * 0.5f;
            float ring = MathF.Sqrt(MathF.Max(qx, 0f) * MathF.Max(qx, 0f) + MathF.Max(qy, 0f) * MathF.Max(qy, 0f)) + MathF.Min(MathF.Max(qx, qy), 0f) - 0.006f;
            if (ring < d) { d = ring; id = 4; }
            float hub = Cap(x, y, z, cx, PY - 0.03f, cz, cx, PY + 0.012f, cz, 0.026f);
            float sx = x - cx, sy = y - (PY - 0.022f), sz = z - cz;
            hub = MathF.Min(hub, MathF.Min(Box(sx, sy, sz, Dr, 0.006f, 0.009f), Box(sx, sy, sz, 0.009f, 0.006f, Dr)));
            if (hub < d) { d = hub; id = 5; }
        }
        return d;
    }

    private static float Dist(float x, float y, float z) => Scene(x, y, z, out _);

    // 部品の色・照りの鋭さ・照りの強さ
    private static readonly float[] BaseR = { 0f, 0.12f, 0.25f, 0.14f, 0.13f, 0.30f };
    private static readonly float[] BaseG = { 0f, 0.125f, 0.72f, 0.145f, 0.135f, 0.31f };
    private static readonly float[] BaseB = { 0f, 0.14f, 0.98f, 0.16f, 0.15f, 0.34f };
    private static readonly float[] Shine = { 1f, 60f, 90f, 30f, 35f, 20f };
    private static readonly float[] Ks = { 0f, 0.9f, 1f, 0.35f, 0.45f, 0.2f };

    // 世界の向きを形の向きへ (向き φ と傾き α を戻す)
    private readonly struct View
    {
        private readonly float _cy, _sy, _cp, _sp;
        public View(int u, int p)
        {
            float yaw = u * (MathF.PI * 2f / Yaws), pitch = PitchDeg[p] * (MathF.PI / 180f);
            _cy = MathF.Cos(yaw); _sy = MathF.Sin(yaw); _cp = MathF.Cos(pitch); _sp = MathF.Sin(pitch);
        }
        public void Inv(float x, float y, float z, out float ox, out float oy, out float oz)
        {
            float x1 = x * _cy + z * _sy, z1 = -x * _sy + z * _cy;
            ox = x1;
            oy = y * _cp - z1 * _sp;
            oz = y * _sp + z1 * _cp;
        }
        // 画面の点から出る光線を形の向きで (始まりと向き)。球 (胴の真ん中から 0.7) に入る区間 t0〜t1。外れたら false
        public bool Ray(float sx, float sy, out float ox, out float oy, out float oz, out float dx, out float dy, out float dz, out float t0, out float t1)
        {
            float wx = sx, wy = sy * Cos + 2f * Sin - PivotY, wz = sy * Sin - 2f * Cos;
            float b = -wy * Sin + wz * Cos, c = wx * wx + wy * wy + wz * wz - 0.49f, disc = b * b - c;
            Inv(wx, wy, wz, out ox, out oy, out oz);
            oy += PivotY;
            Inv(0f, -Sin, Cos, out dx, out dy, out dz);
            if (disc <= 0f) { t0 = t1 = 0f; return false; }
            float sq = MathF.Sqrt(disc);
            t0 = MathF.Max(-b - sq, 0f); t1 = -b + sq;
            return true;
        }
    }

    private static void BakeView(int u, int p)
    {
        var view = new View(u, p);
        int w = W * SS, h = H * SS, n = w * h;
        var hitT = new float[n];
        var ids = new byte[n];
        var cr = new float[n]; var cg = new float[n]; var cb = new float[n];
        // 光と見る向きは世界に固定 (機体が回っても光は左上から)
        float lx = -0.55f, ly = 0.75f, lz = -0.38f;
        float ll = Len(lx, ly, lz); lx /= ll; ly /= ll; lz /= ll;
        float hx = lx, hy = ly + Sin, hz = lz - Cos;
        float hl = Len(hx, hy, hz); hx /= hl; hy /= hl; hz /= hl;
        view.Inv(lx, ly, lz, out lx, out ly, out lz);
        view.Inv(hx, hy, hz, out hx, out hy, out hz);
        view.Inv(0f, Sin, -Cos, out float ex, out float ey, out float ez); // 見る人の方
        const float Eps = 0.002f;
        Parallel.For(0, h, Cores, j =>
        {
            float sy = Bottom + (j + 0.5f) / (Ppu * SS);
            for (int i = 0; i < w; i++)
            {
                float sx = Left + (i + 0.5f) / (Ppu * SS);
                int o = j * w + i, id = 0;
                hitT[o] = 99f;
                if (!view.Ray(sx, sy, out float ox, out float oy, out float oz, out float vx, out float vy, out float vz, out float t, out float tEnd)) continue;
                for (int it = 0; it < 140 && t < tEnd; it++)
                {
                    float d = Scene(ox + vx * t, oy + vy * t, oz + vz * t, out int sid);
                    if (d < 0.0008f) { id = sid; break; }
                    t += MathF.Max(d, 0.0015f) * 0.9f;
                }
                ids[o] = (byte)id;
                if (id == 0) continue;
                hitT[o] = t;
                float px = ox + vx * t, py = oy + vy * t, pz = oz + vz * t;
                float nx = Dist(px + Eps, py, pz) - Dist(px - Eps, py, pz);
                float ny = Dist(px, py + Eps, pz) - Dist(px, py - Eps, pz);
                float nz = Dist(px, py, pz + Eps) - Dist(px, py, pz - Eps);
                float nl = Len(nx, ny, nz) + 1e-9f; nx /= nl; ny /= nl; nz /= nl;
                float dif = Math.Clamp(nx * lx + ny * ly + nz * lz, 0f, 1f);
                float spec = Math.Clamp(nx * hx + ny * hy + nz * hz, 0f, 1f);
                float facing = Math.Clamp(nx * ex + ny * ey + nz * ez, 0f, 1f);
                float fres = (1f - facing) * (1f - facing) * (1f - facing);
                float ao = MathF.Pow(Math.Clamp(Dist(px + nx * 0.035f, py + ny * 0.035f, pz + nz * 0.035f) / 0.035f, 0f, 1f), 0.7f);
                float lit = (0.35f + 0.85f * dif) * (0.45f + 0.55f * ao);
                float sp = Ks[id] * MathF.Pow(spec, Shine[id]);
                float r = BaseR[id] * lit + sp + 0.22f * fres * 0.55f * ao;
                float g = BaseG[id] * lit + sp + 0.22f * fres * 0.70f * ao;
                float b = BaseB[id] * lit + sp + 0.22f * fres * 0.85f * ao;
                if (id == 2) { g += 0.06f * (1f - dif); b += 0.10f * (1f - dif); }
                cr[o] = r; cg[o] = g; cb[o] = b;
            }
        });
        // 縁取り: 近くに別の部品か何も無い所、奥行きの大きな段がある所
        var edge = new bool[n];
        Parallel.For(0, h, Cores, j =>
        {
            for (int i = 0; i < w; i++)
            {
                int o = j * w + i;
                byte me = ids[o];
                float mt = hitT[o];
                bool e = false;
                for (int dj = -SS; dj <= SS && !e; dj++)
                for (int di = -SS; di <= SS; di++)
                {
                    int jj = j + dj, ii = i + di;
                    byte other = jj < 0 || jj >= h || ii < 0 || ii >= w ? (byte)0 : ids[jj * w + ii];
                    if (other != me) { e = true; break; }
                    if (me != 0 && MathF.Abs(hitT[jj * w + ii] - mt) > 0.06f) { e = true; break; }
                }
                edge[o] = e;
            }
        });
        int kinds = p == Level ? Kinds : Kinds - 1;
        for (int k = 0; k < kinds; k++)
        {
            int slot = (u * Pitches + p) * Kinds + k;
            Px[slot] = Cut(Compose(view, k == Still ? -1 : k, w, h, hitT, ids, cr, cg, cb, edge), slot);
        }
        Volatile.Write(ref Ready[u * Pitches + p], 1);
    }

    // 何も描いていない周りを切り落とす (傾いた絵が入るよう広く取った分)
    private static byte[] Cut(byte[] px, int slot)
    {
        int x0 = W, y0 = H, x1 = -1, y1 = -1;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            if (px[(y * W + x) * 4 + 3] == 0) continue;
            if (x < x0) x0 = x; if (x > x1) x1 = x;
            if (y < y0) y0 = y; if (y > y1) y1 = y;
        }
        if (x1 < 0) { x0 = y0 = 0; x1 = y1 = 0; }
        // 縁を 1 画素残す (線形の補間で縁が切れないように)
        x0 = Math.Max(x0 - 1, 0); y0 = Math.Max(y0 - 1, 0); x1 = Math.Min(x1 + 1, W - 1); y1 = Math.Min(y1 + 1, H - 1);
        int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
        var outp = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++) Buffer.BlockCopy(px, ((y0 + y) * W + x0) * 4, outp, y * cw * 4, cw * 4);
        Crop[slot * 4] = x0; Crop[slot * 4 + 1] = y0; Crop[slot * 4 + 2] = cw; Crop[slot * 4 + 3] = ch;
        return outp;
    }

    // 羽の平面を重ねて縁取りし、SS×SS を平均して 1 枚に。frame = -1 は止まった羽
    private static byte[] Compose(View view, int frame, int w, int h, float[] hitT, byte[] ids, float[] cr, float[] cg, float[] cb, bool[] edge)
    {
        var acc = new float[W * H * 4];
        const float OutR = 0.035f, OutG = 0.035f, OutB = 0.05f;
        float pyl = frame < 0 ? PY + 0.008f : PY;
        for (int j = 0; j < h; j++)
        {
            float sy = Bottom + (j + 0.5f) / (Ppu * SS);
            for (int i = 0; i < w; i++)
            {
                int o = j * w + i;
                float sx = Left + (i + 0.5f) / (Ppu * SS);
                float a = ids[o] != 0 ? 1f : 0f, r = cr[o], g = cg[o], b = cb[o];
                if (edge[o]) { r = OutR; g = OutG; b = OutB; a = 1f; }
                // 羽の平面 (筒の中・形より手前の時だけ)
                if (view.Ray(sx, sy, out float ox, out float oy, out float oz, out float vx, out float vy, out float vz, out _, out _))
                {
                    float tp = (pyl - oy) / vy;
                    if (tp > 0f && tp < hitT[o])
                    {
                        float bx = ox + vx * tp, bz = oz + vz * tp;
                        for (int k = 0; k < 4; k++)
                        {
                            float dx = bx - DuctX[k], dz = bz - DuctZ[k];
                            float rho = MathF.Sqrt(dx * dx + dz * dz);
                            if (rho >= Dr - 0.004f) continue;
                            float q = rho / Dr;
                            if (frame >= 0)
                            {
                                // 回った羽の残像: 薄い円盤に渦の筋 (2 コマで筋の位置を半分ずらす)
                                float an = MathF.Atan2(dz, dx);
                                float sw = an + q * 2.4f + frame * (MathF.PI * 0.5f) + k * 0.9f;
                                float c = MathF.Max(0f, MathF.Cos(sw * 2f));
                                float c2 = c * c, c4 = c2 * c2;
                                float arc = c4 * c4 * Math.Clamp((q - 0.22f) / 0.2f, 0f, 1f);
                                float da = (0.30f + 0.45f * arc) * Math.Clamp((0.97f - q) / 0.05f, 0f, 1f);
                                r = r * (1f - da) + 0.70f * da; g = g * (1f - da) + 0.72f * da; b = b * (1f - da) + 0.78f * da;
                                a += da * (1f - a);
                            }
                            else
                            {
                                // 止まった羽 2 枚 (十字)。縁は暗く
                                float th = 0.3f + (DuctX[k] > 0f ? 0.4f : 0f);
                                float cs = MathF.Cos(th), sn = MathF.Sin(th);
                                float qx = dx * cs + dz * sn, qz = -dx * sn + dz * cs;
                                float len = Dr - 0.02f, half = 0.022f;
                                float e1 = MathF.Max(MathF.Abs(qx) - len, MathF.Abs(qz) - half);
                                float e2 = MathF.Max(MathF.Abs(qz) - len, MathF.Abs(qx) - half);
                                float eb = MathF.Min(e1, e2);
                                if (eb < 0f)
                                {
                                    bool rim = eb > -0.007f;
                                    float v = rim ? 0.04f : 0.30f + 0.08f * (qx > 0f ? 1f : 0f);
                                    r = v; g = v; b = v * 1.08f; a = 1f;
                                }
                            }
                            break;
                        }
                    }
                }
                int dst = ((j / SS) * W + i / SS) * 4;
                acc[dst] += r * a; acc[dst + 1] += g * a; acc[dst + 2] += b * a; acc[dst + 3] += a;
            }
        }
        var px = new byte[W * H * 4];
        const float Inv = 1f / (SS * SS);
        for (int k = 0; k < W * H; k++)
        {
            float a = acc[k * 4 + 3] * Inv;
            float inv = a > 0.001f ? 1f / (acc[k * 4 + 3]) : 0f;
            px[k * 4] = (byte)(Math.Clamp(acc[k * 4] * inv, 0f, 1f) * 255f + 0.5f);
            px[k * 4 + 1] = (byte)(Math.Clamp(acc[k * 4 + 1] * inv, 0f, 1f) * 255f + 0.5f);
            px[k * 4 + 2] = (byte)(Math.Clamp(acc[k * 4 + 2] * inv, 0f, 1f) * 255f + 0.5f);
            px[k * 4 + 3] = (byte)(Math.Clamp(a, 0f, 1f) * 255f + 0.5f);
        }
        return px;
    }
}
