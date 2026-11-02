using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 壊れたドローンの絵: 斬られた半分 (切り口を上に向けて殻を下に転がった形)・横倒しの残骸 (前右の筒が腕の途中で折れて無い)・
// 折れて外れた筒 1 つ (縁で立てかけたように傾く)。飛んでいる絵と同じ距離の式と光で焼く。
// 切り口には中身を描く: 殻の厚み・2 枚の基板 (上に銅の線と部品)・電池 (巻いた層が見える)・モーターへの線 (赤/黒/黄の被覆と銅の芯)。
// 床の上の向きは 16 方向 (焼くのは 9 方向・左向きは鏡)。飛んでいる絵を焼き終えた後に作業スレッドで焼く
internal static partial class DroneArt
{
    internal enum Shape : byte { Whole = 0, Half = 1, Wreck = 2, Duct = 3 }

    private const int CutId = 6;
    private const int WreckDuct = 3;          // 墜落で折れる筒 (前右)
    private const float ArmBreak = 0.47f;     // 折れた腕が胴から残る長さ (筒の中心までの割合)
    private const float ArmStub = 0.53f;      // 外れた筒の側に残る腕の付け根

    private const int Pieces = 3;             // Half・Wreck・Duct
    private static Task _wreck;
    private static bool _wreckFailed;
    private static readonly byte[][] WPx = new byte[Half * Pieces][];
    private static readonly int[] WCrop = new int[Half * Pieces * 4];
    private static readonly Sprite[] WSprites = new Sprite[Half * Pieces];
    private static readonly float[] Lift = new float[Pieces];

    // 床に置いた時の、絵の原点 (胴の真ん中) から床までの高さ (世界の単位)。焼くのに失敗していたら 0
    internal static float RestLift(Shape s) => WaitWreck() ? Lift[(int)s - 1] : 0f;
    private static bool _wreckDone;

    private static void PrewarmWreck()
    {
        if (_wreck != null) return;
        _wreck = (_tilt ?? _level).ContinueWith(_ =>
        {
            for (int s = 0; s < Pieces; s++) Lift[s] = MeasureLift((Shape)(s + 1));
            Parallel.For(0, Half * Pieces, Cores, i => BakePiece(i % Half, (Shape)(1 + i / Half)));
            Volatile.Write(ref _wreckDone, true);
        });
    }

    // 焼き終えるまで待つ (役職が付いた時に作り始めているので普段は待たない)。失敗していたら false
    private static bool WaitWreck()
    {
        if (Volatile.Read(ref _wreckDone)) return true;
        Prewarm();
        PrewarmWreck();
        if (_wreckFailed || _wreck == null) return false;
        try { _wreck.Wait(); }
        catch (Exception e)
        {
            // 焼き直しても同じ所で落ちるので、この起動の間は壊れた絵を出さない
            _wreckFailed = true;
            Plugin.Logger.LogError($"DroneArt wreck: {e}");
            return false;
        }
        return true;
    }

    // 壊れた絵 (向きの番号 0〜15)。焼くのに失敗していたら null
    internal static Sprite WreckFrame(Shape shape, int yaw, out bool flip)
    {
        yaw &= Yaws - 1;
        flip = yaw >= 5 && yaw <= 11;
        if (!WaitWreck()) return null;
        int u = flip ? (12 - yaw) & (Yaws - 1) : (yaw + 4) & (Yaws - 1);
        int i = u * Pieces + (int)shape - 1;
        var sp = WSprites[i];
        if (sp is not null) return sp;
        return WSprites[i] = Make(WPx, WCrop, i, "MrpDroneWreck" + i);
    }

    // 向きの番号 yaw の絵で、形の床の上の向き (lx, lz) が画面のどちらを向くか (長さ 1)
    private static void ScreenDir(int yaw, float lx, float lz, out float dx, out float dy)
    {
        yaw &= Yaws - 1;
        bool flip = yaw >= 5 && yaw <= 11;
        int u = flip ? (12 - yaw) & (Yaws - 1) : (yaw + 4) & (Yaws - 1);
        float a = u * (MathF.PI * 2f / Yaws), cy = MathF.Cos(a), sy = MathF.Sin(a);
        float wx = lx * cy - lz * sy, wz = lx * sy + lz * cy;
        if (flip) wx = -wx;
        dx = wx; dy = wz * Sin;
        float l = MathF.Sqrt(dx * dx + dy * dy);
        if (l > 1e-5f) { dx /= l; dy /= l; }
    }

    // 斬られた半分の切り口が向く方
    internal static void HalfFaceDir(int yaw, out float dx, out float dy) => ScreenDir(yaw, -1f, 0f, out dx, out dy);

    // 残骸の折れた筒があった方
    internal static void DuctDir(int yaw, out float dx, out float dy) => ScreenDir(yaw, DuctX[WreckDuct], DuctZ[WreckDuct], out dx, out dy);

    // ---- 形 ----

    private static float Scene(Shape shape, float x, float y, float z, out int id)
    {
        switch (shape)
        {
            case Shape.Half:
            {
                // x >= 0 の半分。切った面 (x = 0) は中身の色で塗る
                float d = Scene(x, y, z, out id, -1);
                if (-x > d) { d = -x; id = CutId; }
                return d;
            }
            case Shape.Wreck:
                return Scene(x, y, z, out id, WreckDuct);
            case Shape.Duct:
            {
                float d = 9f;
                id = 0;
                float cx = DuctX[WreckDuct], cz = DuctZ[WreckDuct];
                float a = Cap(x, y, z, cx * ArmStub, 0.271f, cz * ArmStub, cx * 0.62f, 0.272f, cz * 0.62f, 0.026f);
                if (a < d) { d = a; id = 3; }
                Duct(WreckDuct, x, y, z, ref d, ref id);
                return d;
            }
            default:
                return Scene(x, y, z, out id, -1);
        }
    }

    private static float Dist(Shape shape, float x, float y, float z) => Scene(shape, x, y, z, out _);

    private static bool HasDuct(Shape shape, int k) => shape switch
    {
        Shape.Half => DuctX[k] > 0f,
        Shape.Wreck => k != WreckDuct,
        Shape.Duct => k == WreckDuct,
        _ => true,
    };

    // ---- 置き方 (床の上の向きの前に、形をどう転がしたか) ----

    // 回す中心 (形の座標)。絵の原点 (胴の真ん中の高さ) へ来る
    private static void Origin(Shape s, out float ox, out float oy, out float oz)
    {
        switch (s)
        {
            case Shape.Half: ox = 0.12f; oy = BodyY; oz = 0f; break;
            case Shape.Duct: ox = DuctX[WreckDuct]; oy = PY; oz = DuctZ[WreckDuct]; break;
            default: ox = 0f; oy = BodyY; oz = 0f; break;
        }
    }

    // 形 → 置いた向き の回転 (行ごと 9 個)
    private static float[] Pose(Shape s)
    {
        switch (s)
        {
            // 半分は筒と殻の底で立ったまま、支えの無い切り口の側が少し沈む (切り口は 15° 上を向く)
            case Shape.Half: return RotZ(-15f);
            // 折れた筒の側 (+x) へ 28° 倒れ、機首が 10° 下がる
            case Shape.Wreck: return Mul(RotZ(-28f), RotX(-10f));
            // 筒は腕の付け根の側が床に付いて、少し起き上がって転がる
            case Shape.Duct: return Mul(RotZ(10f), RotX(24f));
            default: return RotZ(0f);
        }
    }

    private static float[] RotZ(float deg)
    {
        float a = deg * (MathF.PI / 180f), c = MathF.Cos(a), s = MathF.Sin(a);
        return new[] { c, -s, 0f, s, c, 0f, 0f, 0f, 1f };
    }

    private static float[] RotX(float deg)
    {
        float a = deg * (MathF.PI / 180f), c = MathF.Cos(a), s = MathF.Sin(a);
        return new[] { 1f, 0f, 0f, 0f, c, -s, 0f, s, c };
    }

    private static float[] Mul(float[] a, float[] b)
    {
        var r = new float[9];
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
            r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
        return r;
    }

    // 世界 (原点 = 回す中心) → 形 の向き: 置いた向きの回転の逆 × 床の上の向きの逆
    private readonly struct Posed : IView
    {
        private readonly float _m0, _m1, _m2, _m3, _m4, _m5, _m6, _m7, _m8;
        private readonly float _ox, _oy, _oz;

        public Posed(int u, Shape s)
        {
            float yaw = u * (MathF.PI * 2f / Yaws), cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);
            // 床の上の向きの逆 (飛んでいる絵の View と同じ回り方)
            var inv = new[] { cy, 0f, sy, 0f, 1f, 0f, -sy, 0f, cy };
            var p = Pose(s);
            var pt = new[] { p[0], p[3], p[6], p[1], p[4], p[7], p[2], p[5], p[8] };
            var m = Mul(pt, inv);
            _m0 = m[0]; _m1 = m[1]; _m2 = m[2]; _m3 = m[3]; _m4 = m[4]; _m5 = m[5]; _m6 = m[6]; _m7 = m[7]; _m8 = m[8];
            Origin(s, out _ox, out _oy, out _oz);
        }

        public void Inv(float x, float y, float z, out float ox, out float oy, out float oz)
        {
            ox = _m0 * x + _m1 * y + _m2 * z;
            oy = _m3 * x + _m4 * y + _m5 * z;
            oz = _m6 * x + _m7 * y + _m8 * z;
        }

        // 絵の原点 (画面の (0, PivotY * Cos)) に回す中心が来る。球 (中心から 0.7) に入る区間 t0〜t1
        public bool Ray(float sx, float sy, out float ox, out float oy, out float oz, out float dx, out float dy, out float dz, out float t0, out float t1)
        {
            float rel = sy - PivotY * Cos;
            float wx = sx, wy = rel * Cos + 2f * Sin, wz = rel * Sin - 2f * Cos;
            float b = -wy * Sin + wz * Cos, c = wx * wx + wy * wy + wz * wz - 0.49f, disc = b * b - c;
            Inv(wx, wy, wz, out ox, out oy, out oz);
            ox += _ox; oy += _oy; oz += _oz;
            Inv(0f, -Sin, Cos, out dx, out dy, out dz);
            if (disc <= 0f) { t0 = t1 = 0f; return false; }
            float sq = MathF.Sqrt(disc);
            t0 = MathF.Max(-b - sq, 0f); t1 = -b + sq;
            return true;
        }
    }

    private static void BakePiece(int u, Shape s)
    {
        var view = new Posed(u, s);
        var t = Trace(view, s);
        int slot = u * Pieces + (int)s - 1;
        WPx[slot] = Cut(Compose(view, s, -1, t), WCrop, slot);
    }

    // 置いた形のいちばん低い所から回す中心までの高さ (世界の単位・床に置いた時に原点を持ち上げる分)
    private static float MeasureLift(Shape s)
    {
        var p = Pose(s);
        Origin(s, out float cx, out float cy, out float cz);
        float low = 0f;
        const float Step = 0.012f;
        for (float x = -0.62f; x <= 0.62f; x += Step)
        for (float y = 0.05f; y <= 0.55f; y += Step)
        for (float z = -0.5f; z <= 0.5f; z += Step)
        {
            if (Dist(s, x, y, z) > 0f) continue;
            float wy = p[3] * (x - cx) + p[4] * (y - cy) + p[5] * (z - cz);
            if (wy < low) low = wy;
        }
        return -low * Cos * Shown;
    }

    // ---- 切り口の中身 (形の座標の y と z・切った面は x = 0) ----

    private static void CutFace(float y, float z, out float r, out float g, out float b, out float ks, out float shine)
    {
        float inside = Scene(0f, y, z, out _, -1);
        ks = 0.08f; shine = 12f;
        // バイザー = 表に張った色付きの透明な板の断面 (胴の奥までは届かない)
        if (inside > -0.026f && Ell(0f, y, z, 0f, VisY, VisZ, VisRx, VisRy, VisRz) < 0f)
        {
            r = 0.10f; g = 0.34f; b = 0.46f; ks = 0.5f; shine = 40f;
            return;
        }
        // 殻の厚み (切った樹脂は外の艶より明るく粗い)
        if (inside > -0.02f)
        {
            float grain = 0.012f * MathF.Sin(z * 410f + y * 170f);
            r = 0.19f + grain; g = 0.19f + grain; b = 0.20f + grain;
            return;
        }
        // モーターへの線 (被覆の色の輪と銅の芯)
        for (int k = 0; k < Wires.Length; k += 3)
        {
            float dy = y - Wires[k], dz = z - Wires[k + 1], rr = dy * dy + dz * dz;
            if (rr > 0.0105f * 0.0105f) continue;
            if (rr < 0.0048f * 0.0048f) { r = 0.86f; g = 0.52f; b = 0.26f; ks = 1f; shine = 50f; return; }
            int c = (int)Wires[k + 2];
            r = WireR[c]; g = WireG[c]; b = WireB[c]; ks = 0.35f; shine = 30f;
            return;
        }
        // 電池: 巻いた層が縞に見える (外は白い包み・黒い帯)
        if (y > 0.322f && y < 0.44f && z > -0.045f && z < 0.17f && inside < -0.03f)
        {
            bool wrap = y < 0.328f || y > 0.434f || z < -0.039f || z > 0.164f;
            if (wrap)
            {
                bool strap = z > 0.05f && z < 0.08f;
                r = g = b = strap ? 0.09f : 0.82f;
                ks = 0.3f; shine = 20f;
                return;
            }
            bool foil = ((int)((y - 0.328f) / 0.0065f) & 1) == 0;
            r = foil ? 0.66f : 0.40f; g = foil ? 0.66f : 0.42f; b = foil ? 0.70f : 0.46f;
            ks = foil ? 0.7f : 0.2f; shine = 35f;
            return;
        }
        // 基板 2 枚 (上の板は飛ぶための部品・下の板はモーターを回す部品)
        if (Board(y, z, 0.292f, out r, out g, out b, out ks, out shine)) return;
        if (Board(y, z, 0.205f, out r, out g, out b, out ks, out shine)) return;
        // 2 枚の板をつなぐ真鍮の柱
        if (y > 0.205f && y < 0.292f && (MathF.Abs(z - 0.125f) < 0.0055f || MathF.Abs(z + 0.125f) < 0.0055f))
        {
            r = 0.72f; g = 0.56f; b = 0.26f; ks = 0.9f; shine = 40f;
            return;
        }
        // 空洞: 奥は暗い (縁に近いほど殻の照り返しで少し明るい)
        float lit = Math.Clamp(1f + inside / 0.08f, 0f, 1f);
        r = 0.06f + 0.04f * lit; g = 0.06f + 0.04f * lit; b = 0.065f + 0.045f * lit;
    }

    // 線の (y, z, 色) ・色 0 赤 / 1 黒 / 2 黄
    private static readonly float[] Wires =
    {
        0.262f, 0.125f, 0, 0.262f, 0.149f, 1, 0.257f, 0.172f, 2,
        0.266f, -0.115f, 2, 0.268f, -0.139f, 0, 0.250f, -0.130f, 1,
        0.180f, 0.040f, 0, 0.181f, 0.064f, 1,
    };
    private static readonly float[] WireR = { 0.72f, 0.06f, 0.86f }, WireG = { 0.08f, 0.06f, 0.68f }, WireB = { 0.06f, 0.07f, 0.10f };

    // 板の真ん中の高さ cy。板は暗い緑、上の面に銅の線、上に黒い部品
    private static bool Board(float y, float z, float cy, out float r, out float g, out float b, out float ks, out float shine)
    {
        r = g = b = 0f; ks = 0.1f; shine = 15f;
        if (z < -0.17f || z > 0.17f) return false;
        float dy = y - cy;
        if (dy > -0.01f && dy < 0.008f)
        {
            r = 0.06f; g = 0.19f; b = 0.10f;
            if (dy > 0.0058f) { r = 0.80f; g = 0.52f; b = 0.24f; ks = 0.9f; shine = 45f; } // 銅の線
            else if (MathF.Abs(MathF.Sin(z * 260f)) > 0.96f) { r = 0.78f; g = 0.66f; b = 0.30f; ks = 0.8f; shine = 45f; } // 穴の金の縁
            return true;
        }
        // 部品 (足の付いた黒い角)
        if (dy >= 0.008f && dy < 0.022f)
        {
            float u = z + 0.17f;
            int cell = (int)(u / 0.045f);
            float f = u - cell * 0.045f;
            bool chip = ((cell * 7 + (int)(cy * 1000f)) % 3) != 1 && f > 0.006f && f < 0.034f;
            if (!chip) return false;
            r = 0.045f; g = 0.045f; b = 0.05f; ks = 0.4f; shine = 25f;
            if (dy < 0.0105f) { r = 0.62f; g = 0.62f; b = 0.64f; ks = 0.8f; } // 足
            return true;
        }
        return false;
    }
}
