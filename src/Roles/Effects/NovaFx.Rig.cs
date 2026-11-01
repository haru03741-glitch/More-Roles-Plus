using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// キャノンの動く部品: 砲口の爪 3 本 (構えている間はわずかに開閉し、撃つと開く)・上面の排熱口の蓋 2 枚 (撃った後に開いて熱を吐く)・照準器のレンズの照り。
// 絵の 3D の形と同じ座標で部品の位置を計算し、今のコマと同じ見え方で画面へ写す (真上から斜めに見た平行投影なので、
// 棒は帯・玉は円・平らな板は平行四辺形になり、回して伸ばすだけで正しく重なる)。部品は形の真ん中に置いた台 (Rig) の子で、
// 台の位置は毎フレーム・部品の置き直しはコマか開き具合が変わった時だけ
internal static partial class NovaFx
{
    // 3D の形 (キャノンの絵を描いた時と同じ数値)
    private const float ModelCenterX = 0.22f, ModelScale = 1.35f, ModelRoll = 35f, ModelHeight = 0.82f;
    private const float VentX0 = -0.29f, VentX1 = -0.13f, VentY0 = 0.016f, VentY1 = 0.092f, VentZ = 0.142f;
    private const float RodPpu = 32f, BallPpu = 48f, FlapPpu = 96f;
    private const float LightX = -0.45f, LightY = -0.35f, LightZ = 0.82f;   // 形を描いた時と同じ光
    private const float LightSx = -0.45f, LightSy = 0.47f;                  // 同じ光の画面の上の向き

    private static Sprite _rod, _ball, _tip, _flap;

    private static bool PartsArt()
    {
        _rod = ButtonIcons.Load("nova_rod", RodPpu, FxMath.V2(0.5f, 0.5f));
        _ball = ButtonIcons.Load("nova_ball", BallPpu, FxMath.V2(0.5f, 0.5f));
        _tip = ButtonIcons.Load("nova_tip", BallPpu, FxMath.V2(0.5f, 0.5f));
        _flap = ButtonIcons.Load("nova_flap", FlapPpu, FxMath.V2(0.5f, 0.5f));
        return _rod && _ball && _tip && _flap;
    }

    private sealed class Rig
    {
        public GameObject Go;
        public Transform Tf;
        public readonly SpriteRenderer[] Rods = new SpriteRenderer[6], Balls = new SpriteRenderer[3], Tips = new SpriteRenderer[3], TipGlow = new SpriteRenderer[3];
        public readonly Transform[] RodTf = new Transform[6], BallTf = new Transform[3], TipTf = new Transform[3], TipGlowTf = new Transform[3];
        public readonly SpriteRenderer[] Flaps = new SpriteRenderer[2], Heat = new SpriteRenderer[2];
        public readonly Transform[] FlapRoot = new Transform[2], FlapTf = new Transform[2], HeatTf = new Transform[2];
        public SpriteRenderer Glint;
        public Transform GlintTf;
        public readonly float[] VentX = new float[2], VentY = new float[2], VentNx = new float[2], VentNy = new float[2];
        public readonly bool[] HeatOn = new bool[2];
        public readonly float[] FlapTone = { 1f, 1f };
        public readonly int[] HeatKey = { -1, -1 };
        public bool GlintOn = true;
        public float Alpha = 1f;
        public int KeyFrame = -1, KeyOpen = -1, KeyVent = -1, KeyAlpha = -1, KeyGlow = -1;
        public float Cr, Sr, Cy, Sy; // 今のコマのひねりと向き
    }

    private static Rig MakeRig(Transform parent)
    {
        var r = new Rig { Go = new GameObject("Rig") };
        r.Tf = r.Go.transform;
        r.Tf.SetParent(parent, false);
        for (int i = 0; i < 6; i++) { r.Rods[i] = Make("Rod", _rod, r.Tf); r.RodTf[i] = r.Rods[i].transform; }
        for (int i = 0; i < 3; i++)
        {
            r.Balls[i] = Make("Knee", _ball, r.Tf); r.BallTf[i] = r.Balls[i].transform;
            r.Tips[i] = Make("Tip", _tip, r.Tf); r.TipTf[i] = r.Tips[i].transform;
            r.TipGlow[i] = Make("TipGlow", Terrain.DebrisArt.SoftGlow, r.Tf); r.TipGlowTf[i] = r.TipGlow[i].transform;
            r.BallTf[i].localScale = FxMath.V3(0.075f, 0.075f, 1f);
            r.TipTf[i].localScale = FxMath.V3(0.068f, 0.068f, 1f);
            r.TipGlowTf[i].localScale = FxMath.V3(0.24f, 0.24f, 1f);
        }
        for (int i = 0; i < 2; i++)
        {
            var root = new GameObject("Flap").transform;
            root.SetParent(r.Tf, false);
            r.FlapRoot[i] = root;
            r.Flaps[i] = Make("Lid", _flap, root); r.FlapTf[i] = r.Flaps[i].transform;
            r.Heat[i] = Make("VentHeat", Terrain.DebrisArt.SoftGlow, r.Tf); r.HeatTf[i] = r.Heat[i].transform;
            r.Heat[i].enabled = false;
        }
        r.Glint = Make("Glint", Terrain.DebrisArt.SoftGlow, r.Tf); r.GlintTf = r.Glint.transform;
        r.GlintTf.localScale = FxMath.V3(0.06f, 0.06f, 1f);
        return r;
    }

    // 形の点 (大きさを掛ける前) → 台の上の画面の位置と奥行き (小さいほど手前)
    private static void Proj(Rig r, float mx, float my, float mz, out float x, out float y, out float d)
    {
        RotV(r, (mx - ModelCenterX) * ModelScale, my * ModelScale, mz * ModelScale, out float x2, out float y2, out float z1);
        x = x2; y = y2 * Squash + z1 * ModelHeight; d = y2 * ModelHeight - z1 * Squash;
    }

    // 形の向き (ひねり → 向き) を掛けた向き
    private static void RotV(Rig r, float x0, float y0, float z0, out float x2, out float y2, out float z1)
    {
        float y1 = r.Cr * y0 - r.Sr * z0;
        z1 = r.Sr * y0 + r.Cr * z0;
        x2 = r.Cy * x0 - r.Sy * y1;
        y2 = r.Sy * x0 + r.Cy * y1;
    }

    // 形の向きの棒 (大きさを掛ける前) → 画面の上の長さ
    private static void ProjVec(Rig r, float vx, float vy, float vz, out float x, out float y)
    {
        RotV(r, vx * ModelScale, vy * ModelScale, vz * ModelScale, out float x2, out float y2, out float z1);
        x = x2; y = y2 * Squash + z1 * ModelHeight;
    }

    // 台を置いて部品を合わせる。open = 爪の開き (0..1)・vent = 排熱口の蓋の開き (0..1)・heat = 口の中の熱 (0..1)・glow = 爪の先の光
    private static void TickRig(Shot s, float cx, float cy, float now, float open, float vent, float heat, float glow, float alpha, bool glint)
    {
        var r = s.Rig;
        r.Tf.position = FxMath.V3(cx, cy, s.Z);
        int f = s.Frame;
        if (f != r.KeyFrame)
        {
            float fy = f * 11.25f;
            float roll = ModelRoll * FxMath.Cos(fy * Deg) * Deg;
            r.Cr = FxMath.Cos(roll); r.Sr = FxMath.Sin(roll);
            r.Cy = FxMath.Cos(fy * Deg); r.Sy = FxMath.Sin(fy * Deg);
        }
        int ko = (int)(open * 128f), kv = (int)(vent * 128f);
        if (f != r.KeyFrame || ko != r.KeyOpen) PlaceClaws(r, ko / 128f);
        if (f != r.KeyFrame || kv != r.KeyVent) PlaceFlaps(r, kv / 128f);
        r.KeyFrame = f; r.KeyOpen = ko; r.KeyVent = kv;
        // 色: 消え方 (構えとしまう時) と爪の先の光が変わった時だけ
        int ka = (int)(alpha * 64f), kg = (int)(glow * 32f);
        if (ka != r.KeyAlpha || kg != r.KeyGlow)
        {
            r.KeyAlpha = ka; r.KeyGlow = kg;
            float a = ka / 64f, g = kg / 32f;
            var white = FxMath.Rgba(1f, 1f, 1f, a);
            for (int i = 0; i < 6; i++) r.Rods[i].color = white;
            r.Alpha = a;
            for (int i = 0; i < 2; i++) r.Flaps[i].color = FxMath.Rgba(r.FlapTone[i], r.FlapTone[i], r.FlapTone[i], a);
            var tip = FxMath.Rgba(0.42f + 0.58f * g, 0.6f + 0.4f * g, 0.7f + 0.3f * g, a);
            for (int i = 0; i < 3; i++)
            {
                r.Balls[i].color = white;
                r.Tips[i].color = tip;
                r.TipGlow[i].enabled = g > 0.02f;
                r.TipGlow[i].color = FxMath.Rgba(1f, 0.16f, 0.12f, 0.55f * g * a);
            }
        }
        // 排熱口の中の熱 (蓋が開いた分だけ見える)
        for (int i = 0; i < 2; i++)
        {
            bool on = heat > 0.01f && vent > 0.05f;
            if (r.HeatOn[i] != on) { r.HeatOn[i] = on; r.Heat[i].enabled = on; r.HeatKey[i] = -1; }
            if (!on) continue;
            // 揺らぎ込みで 1/48 刻み: 変わった時だけ書く
            int hk = (int)(heat * FxMath.Clamp01(vent * 2f) * FxMath.Range(0.85f, 1f) * 48f);
            if (hk == r.HeatKey[i]) continue;
            r.HeatKey[i] = hk;
            float hv = hk / 48f;
            r.Heat[i].color = FxMath.Rgba(1f, 0.32f + 0.25f * heat, 0.1f, 0.75f * hv);
        }
        // 照準器のレンズの照り: レンズの縁をゆっくり回る
        if (r.GlintOn != glint) { r.GlintOn = glint; r.Glint.enabled = glint; }
        if (glint)
        {
            float ph = now * 2.4f;
            Proj(r, -0.043f, 0.02f * FxMath.Cos(ph), 0.2f + 0.02f * FxMath.Sin(ph), out float gx, out float gy, out _);
            r.GlintTf.localPosition = FxMath.V3(gx, gy, -0.00012f);
            r.Glint.color = FxMath.Rgba(1f, 0.9f, 0.88f, (0.45f + 0.35f * FxMath.Sin(ph * 0.5f)) * alpha);
        }
    }

    // 爪 3 本: 砲口の輪の内側の付け根から膝まで外へ開き、膝から先は内へ曲がる。開くほど膝が外へ・先が前へ向いて漏斗になる
    private static void PlaceClaws(Rig r, float open)
    {
        float t1 = (37f + 40f * open) * Deg, t2 = (-26.6f + 50f * open) * Deg;
        float kx = 0.6f + 0.15f * FxMath.Cos(t1), kr = 0.1f + 0.15f * FxMath.Sin(t1);
        float tx = kx + 0.089f * FxMath.Cos(t2), tr = kr + 0.089f * FxMath.Sin(t2);
        for (int k = 0; k < 3; k++)
        {
            float a = (90f + 120f * k) * Deg, ca = FxMath.Cos(a), sa = FxMath.Sin(a);
            Proj(r, 0.6f, 0.1f * ca, 0.1f * sa, out float bx, out float by, out _);
            Proj(r, kx, kr * ca, kr * sa, out float nx, out float ny, out float nd);
            Proj(r, tx, tr * ca, tr * sa, out float px, out float py, out float pd);
            // 筒の向こう側の爪は砲口の絵の後ろ、手前側は前
            Proj(r, kx, 0f, 0f, out _, out _, out float axisD);
            float z = nd > axisD ? 0.00004f : -0.00008f;
            PlaceRod(r.RodTf[k * 2], bx, by, nx, ny, 0.07f, z);
            PlaceRod(r.RodTf[k * 2 + 1], nx, ny, px, py, 0.058f, z);
            r.BallTf[k].localPosition = FxMath.V3(nx, ny, z - 0.00001f);
            r.TipTf[k].localPosition = FxMath.V3(px, py, (pd > axisD ? 0.00004f : -0.00008f) - 0.00001f);
            r.TipGlowTf[k].localPosition = FxMath.V3(px, py, -0.0001f);
        }
    }

    private static void PlaceRod(Transform tf, float x0, float y0, float x1, float y1, float thick, float z)
    {
        float dx = x1 - x0, dy = y1 - y0, len = FxMath.Sqrt(dx * dx + dy * dy);
        tf.localPosition = FxMath.V3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, z);
        tf.localRotation = FxMath.RotZ(FxMath.Atan2(dy, dx) / Deg);
        // 絵の上の段が光の側。棒の上の向きが光と逆なら返す
        float up = -dy * LightSx + dx * LightSy;
        tf.localScale = FxMath.V3(len / (8f / RodPpu), up >= 0f ? thick : -thick, 1f);
    }

    // 排熱口の蓋 2 枚: 赤い線の側を蝶番にして、外の辺が持ち上がる (左右に開くエラのように)
    private static void PlaceFlaps(Rig r, float vent)
    {
        float phi = vent * 65f * Deg, cp = FxMath.Cos(phi), sp = FxMath.Sin(phi);
        float len = VentX1 - VentX0, w = VentY1 - VentY0;
        for (int i = 0; i < 2; i++)
        {
            float sd = i == 0 ? 1f : -1f;
            float vy = sd * w * cp, vz = w * sp;
            Proj(r, (VentX0 + VentX1) * 0.5f, sd * VentY0 + vy * 0.5f, VentZ + vz * 0.5f, out float px, out float py, out _);
            ProjVec(r, len, 0f, 0f, out float ux, out float uy);
            ProjVec(r, 0f, vy, vz, out float wx, out float wy);
            // 絵 (幅 1・高さ 0.5) を u と v の平行四辺形へ: M = 回転 θ1 · 伸ばし (s1, s2) · 回転 θ2
            float a = ux, b = wx * 2f, c = uy, dd = wy * 2f;
            float e = (a + dd) * 0.5f, fq = (a - dd) * 0.5f, g = (c + b) * 0.5f, h = (c - b) * 0.5f;
            float q = FxMath.Sqrt(e * e + h * h), rr = FxMath.Sqrt(fq * fq + g * g);
            float a1 = FxMath.Atan2(g, fq), a2 = FxMath.Atan2(h, e);
            var root = r.FlapRoot[i];
            root.localPosition = FxMath.V3(px, py, -0.00002f);
            root.localRotation = FxMath.RotZ((a2 + a1) * 0.5f / Deg);
            root.localScale = FxMath.V3(q + rr, q - rr, 1f);
            r.FlapTf[i].localRotation = FxMath.RotZ((a2 - a1) * 0.5f / Deg);
            // 陰影 (形を描いた時と同じ 3 段)
            RotV(r, 0f, -sd * sp, cp, out float nx, out float ny, out float nz);
            float lit = nx * LightX + ny * LightY + nz * LightZ;
            float tone = lit > 0.55f ? 1f : lit > 0.08f ? 0.85f : 0.63f;
            if (tone != r.FlapTone[i]) { r.FlapTone[i] = tone; r.Flaps[i].color = FxMath.Rgba(tone, tone, tone, r.Alpha); }
            // 口の中の熱と湯気の出どころ・吹き出す向き (外の上)
            Proj(r, (VentX0 + VentX1) * 0.5f, sd * (VentY0 + VentY1) * 0.5f, 0.14f, out float hx, out float hy, out _);
            r.HeatTf[i].localPosition = FxMath.V3(hx, hy, -0.00001f);
            r.HeatTf[i].localScale = FxMath.V3(0.22f, 0.22f * Squash + 0.04f, 1f);
            r.VentX[i] = hx; r.VentY[i] = hy;
            ProjVec(r, 0f, sd * 0.6f, 0.8f, out r.VentNx[i], out r.VentNy[i]);
        }
    }
}
