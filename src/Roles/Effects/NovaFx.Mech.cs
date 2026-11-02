using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// キャノンの溜めと発射に合わせて動く部品:
//  筒の芯 = フィンの間は絵が抜けていて、後ろに置いた光の棒が見える。溜まった分だけ後ろから前へ灯り、撃った後は冷めて暗くなる
//  輪     = 筒の 3 つの輪のボルトが溜めで回り始め (隣どうしは逆向き)、撃った後は惰性で止まる
//  装甲板 = 砲口の 4 枚が筒に沿って畳まれた所から、溜めに合わせて X 字に前へ開いて漏斗になり、光線が消えると畳む
//  棒     = 筒の左右の鞘から 2 本が光線の頂点の脇まで伸び、先が光る
//  油圧   = 撃った瞬間に筒が押し戻されて左右の棒が筒へ沈み、戻る。照射中は細かく震える
//  後ろの蓋 = スピーカーの中心の帽子が撃った瞬間に後ろへ押され、照射中は唸りで震える
internal static partial class NovaFx
{
    private const float BarX0 = 0.0f, BarX1 = 0.58f, BarThick = 0.18f;
    private static readonly float[] RingX = { 0.196f, 0.352f, 0.508f };
    // 梁だけの絵 (nova_cannon_girder) は梁のまわりだけ切ってあるので、コマごとに大砲の絵の中心からのずれ (画素・右と上が +) を持つ。
    // 絵を描き直したら、描いた時に出るずれの表に貼り替える
    private static readonly short[] GirderOff = { 22, 10, 24, 13, 25, 19, 25, 21, 23, 25, 19, 30, 15, 34, 8, 38, 0, 41, -8, 38, -15, 34, -20, 30, -24, 25, -26, 21, -26, 19, -24, 13, -23, 10, -19, 9, -15, 8, -9, 9, -6, 10, -3, 11, -2, 13, -1, 14, 0, 14, 0, 14, 1, 13, 3, 11, 6, 10, 9, 9, 14, 8, 19, 9 };

    // 梁の絵はボルト (-0.00003) より手前・大砲の光の絵 (-0.00007) より奥
    private static Vector3 GirderAt(int f) => FxMath.V3(GirderOff[f * 2] / CannonPpu, GirderOff[f * 2 + 1] / CannonPpu, -0.00005f);

    private const float RingR = 0.121f, RingIdle = 0.25f, RingMax = 2.6f, RingSpool = 1.2f, RingCoast = 0.9f;
    private const float RailR = 0.175f, RailX = 0.6f, RailMin = 0.03f, RailReach = 0.27f;
    private const float PlateX = 0.62f, PlateR = 0.122f, PlateLen = 0.25f, PlateW = 0.13f, PlateShut = 172f, PlateOpen = 50f;
    private const float PetalPpu = 160f, PetalH = 48f / 160f;
    private const float PistonY0 = 0.16f, PistonY1 = 0.125f, PistonX0 = -0.15f, PistonX1 = 0.07f;
    private const float RecoilPress = 0.045f, CapX = -0.335f;

    private static Sprite _bar, _petal, _rodc;

    private static bool MechArt()
    {
        _bar = ButtonIcons.Load("nova_bar", RodPpu, FxMath.V2(0.5f, 0.5f));
        _petal = ButtonIcons.Load("nova_petal", PetalPpu, FxMath.V2(0.5f, 0.5f));
        _rodc = ButtonIcons.Load("nova_rodc", RodPpu, FxMath.V2(0.5f, 0.5f));
        return _bar && _petal && _rodc;
    }

    private sealed partial class Rig
    {
        public SpriteRenderer BarDark, BarLit, BarHead, Bloom, Cap;
        public Transform BarDarkTf, BarLitTf, BarHeadTf, BloomTf, CapTf;
        public readonly SpriteRenderer[] Bolts = new SpriteRenderer[9], Plates = new SpriteRenderer[4], Rails = new SpriteRenderer[2], RailTips = new SpriteRenderer[2];
        public readonly Transform[] BoltTf = new Transform[9], PlateRoot = new Transform[4], PlateTf = new Transform[4], RailTf = new Transform[2], RailTipTf = new Transform[2];
        public readonly SpriteRenderer[] Pistons = new SpriteRenderer[2], Collars = new SpriteRenderer[2];
        public readonly Transform[] PistonTf = new Transform[2], CollarTf = new Transform[2];
        public readonly bool[] BoltOn = new bool[9];
        public readonly float[] PlateTone = { -1f, -1f, -1f, -1f };
        public float Spin, SpinAng;
        public int KeyFill = -1, KeyBright = -1, KeySpin = -1, KeyPlate = -1, KeyRail = -1, KeyPress = -1, KeyCap = -1, KeyMechA = -1, KeyTip = -1, MechFrame = -1;
        public bool CapOn = true, BloomOn = true, LitOn, TipOn, RailOn = true;
    }

    private static void MakeMech(Rig r)
    {
        // 芯の棒は絵の後ろ (抜いた所からだけ見える)・にじみは前
        r.BarDark = Make("CoreDark", _bar, r.Tf); r.BarDarkTf = r.BarDark.transform;
        r.BarLit = Make("CoreLit", _bar, r.Tf); r.BarLitTf = r.BarLit.transform;
        r.BarHead = Make("CoreHead", Terrain.DebrisArt.SoftGlow, r.Tf); r.BarHeadTf = r.BarHead.transform;
        r.Bloom = Make("CoreBloom", Terrain.DebrisArt.SoftGlow, r.Tf); r.BloomTf = r.Bloom.transform;
        r.BarLit.enabled = r.BarHead.enabled = r.Bloom.enabled = r.BloomOn = false;
        for (int i = 0; i < 9; i++)
        {
            r.Bolts[i] = Make("RingBolt", _ball, r.Tf); r.BoltTf[i] = r.Bolts[i].transform;
            r.BoltTf[i].localScale = FxMath.V3(0.038f, 0.038f, 1f);
            r.BoltOn[i] = true;
        }
        for (int i = 0; i < 4; i++)
        {
            var root = new GameObject("Plate").transform;
            root.SetParent(r.Tf, false);
            r.PlateRoot[i] = root;
            r.Plates[i] = Make("Petal", _petal, root); r.PlateTf[i] = r.Plates[i].transform;
        }
        for (int i = 0; i < 2; i++)
        {
            r.Rails[i] = Make("Rail", _rodc, r.Tf); r.RailTf[i] = r.Rails[i].transform;
            r.RailTips[i] = Make("RailTip", Terrain.DebrisArt.SoftGlow, r.Tf); r.RailTipTf[i] = r.RailTips[i].transform;
            r.RailTips[i].enabled = false;
            r.Pistons[i] = Make("PistonRod", _rodc, r.Tf); r.PistonTf[i] = r.Pistons[i].transform;
            r.Collars[i] = Make("PistonEnd", _ball, r.Tf); r.CollarTf[i] = r.Collars[i].transform;
            r.CollarTf[i].localScale = FxMath.V3(0.05f, 0.05f, 1f);
        }
        r.Cap = Make("SpeakerCap", _ball, r.Tf); r.CapTf = r.Cap.transform;
        r.CapTf.localScale = FxMath.V3(0.11f, 0.11f, 1f);
    }

    // fill = 芯の灯った所 (0 = 後ろの端・1 = 砲口)・bright = 芯の明るさ・spinTarget = 輪の回る速さの目標 (回/秒・負 = 惰性で止まる)
    // plate = 装甲板の開き・rail = 棒の伸び・press = 油圧の沈み (形の単位)・cap = 後ろの蓋の押され
    private static void TickMech(Rig r, float dt, float alpha, float fill, float bright, float spinTarget, float plate, float rail, float press, float cap)
    {
        bool frame = r.MechFrame != r.KeyFrame;
        r.MechFrame = r.KeyFrame;
        int ka = (int)(alpha * 64f);
        bool fade = ka != r.KeyMechA;
        r.KeyMechA = ka;
        float a = ka / 64f;

        // ---- 芯 ----
        if (frame)
        {
            Proj(r, BarX0, 0f, 0f, out float bx0, out float by0, out _);
            Proj(r, BarX1, 0f, 0f, out float bx1, out float by1, out _);
            PlaceBar(r.BarDarkTf, bx0, by0, bx1, by1, BarThick, 0.00009f);
        }
        if (fade) r.BarDark.color = FxMath.Rgba(0.045f, 0.045f, 0.05f, a);
        int kf = (int)(fill * 96f), kb = (int)(bright * 48f);
        bool lit = kf > 0 && kb > 0 && a > 0.01f;
        if (r.LitOn != lit) { r.LitOn = lit; r.BarLit.enabled = r.BarHead.enabled = lit; r.KeyFill = r.KeyBright = -1; }
        bool bloom = lit && kb > 6;
        if (r.BloomOn != bloom) { r.BloomOn = bloom; r.Bloom.enabled = bloom; r.KeyFill = -1; }
        if (lit)
        {
            if (frame || kf != r.KeyFill)
            {
                r.KeyFill = kf;
                float xf = BarX0 + (BarX1 - BarX0) * (kf / 96f);
                Proj(r, BarX0, 0f, 0f, out float bx0, out float by0, out _);
                Proj(r, xf, 0f, 0f, out float bx1, out float by1, out _);
                PlaceBar(r.BarLitTf, bx0, by0, bx1, by1, BarThick, 0.00007f);
                r.BarHeadTf.localPosition = FxMath.V3(bx1, by1, 0.00005f);
                r.BarHeadTf.localScale = FxMath.V3(0.36f, 0.36f, 1f);
                if (bloom) PlaceBar(r.BloomTf, bx0, by0, bx1, by1, 0.42f, -0.00008f);
            }
            if (fade || kb != r.KeyBright)
            {
                r.KeyBright = kb;
                float b = kb / 48f;
                // 灯り始めは本体の色・溜まるほど筋から芯の色へ (金属の灰と見分けが付くよう白く飛ばす)
                r.BarLit.color = b < 0.5f ? Pm(2, b * 2f, a) : Pm(3, b * 2f - 1f, a);
                r.BarHead.color = Pc(4, a * (0.6f + 0.4f * b));
                if (bloom) r.Bloom.color = Pc(3, 0.38f * b * a);
            }
        }

        // ---- 輪のボルト (隣どうし逆向き・3 本ずつ) ----
        if (spinTarget >= 0f) r.Spin += (spinTarget - r.Spin) * FxMath.Clamp01(dt * RingSpool);
        else r.Spin -= r.Spin * FxMath.Clamp01(dt / RingCoast);
        r.SpinAng += r.Spin * dt;
        if (r.SpinAng > 1f) r.SpinAng -= 1f;
        // ボルトは小さいので 1/120 回転の刻みで足りる (待機中は毎秒 30 回だけ書く)
        int ks = (int)(r.SpinAng * 120f);
        if (frame || ks != r.KeySpin || fade)
        {
            r.KeySpin = ks;
            float turn = ks / 120f * 2f * System.MathF.PI;
            for (int k = 0; k < 3; k++)
            {
                float x = RingX[k], dir = k == 1 ? -1f : 1f;
                Proj(r, x, 0f, 0f, out _, out _, out float axisD);
                for (int j = 0; j < 3; j++)
                {
                    int i = k * 3 + j;
                    float th = dir * turn + j * 2.0944f + k * 0.6f;
                    Proj(r, x, RingR * FxMath.Cos(th), RingR * FxMath.Sin(th), out float px, out float py, out float pd);
                    // 筒の上の梁の下を通る所は、上に重ねた梁だけの絵 (Girder) が重なった分だけ隠す
                    bool on = pd < axisD && a > 0.01f;
                    bool show = on != r.BoltOn[i];
                    if (show) { r.BoltOn[i] = on; r.Bolts[i].enabled = on; }
                    if (!on) continue;
                    r.BoltTf[i].localPosition = FxMath.V3(px, py, -0.00003f);
                    if (fade || show) r.Bolts[i].color = FxMath.Rgba(1f, 1f, 1f, a);
                }
            }
        }

        // ---- 装甲板 4 枚 ----
        int kp = (int)(plate * 128f);
        if (frame || kp != r.KeyPlate) { r.KeyPlate = kp; PlacePlates(r, kp / 128f); }
        if (fade) for (int i = 0; i < 4; i++) r.Plates[i].color = FxMath.Rgba(r.PlateTone[i], r.PlateTone[i], r.PlateTone[i], a);

        // ---- 前へ伸びる棒 2 本 ----
        int kr = (int)(rail * 128f);
        if (frame || kr != r.KeyRail)
        {
            r.KeyRail = kr;
            // しまっている間は鞘の中 (左右とも見えない)。伸びる時は鞘の口の輪の前から出る
            bool railOn = kr > 0;
            if (railOn != r.RailOn) { r.RailOn = railOn; r.Rails[0].enabled = r.Rails[1].enabled = railOn; }
            float x1 = RailX + RailMin + RailReach * (kr / 128f);
            for (int i = 0; i < 2; i++)
            {
                float y = i == 0 ? RailR : -RailR;
                Proj(r, RailX + RailMin, y, 0f, out float x0s, out float y0s, out float d0);
                Proj(r, x1, y, 0f, out float x1s, out float y1s, out _);
                Proj(r, RailX, 0f, 0f, out _, out _, out float axisD);
                float z = d0 > axisD ? 0.00004f : -0.0001f;
                PlaceRod(r.RailTf[i], x0s, y0s, x1s, y1s, 0.042f, z);
                r.RailTipTf[i].localPosition = FxMath.V3(x1s, y1s, z - 0.00002f);
            }
        }
        // 棒の先の光: 伸びて溜まった分だけ
        float tipA = bright * (kr / 128f) * a;
        int kt = (int)(tipA * 32f);
        bool tipOn = kt > 0;
        if (r.TipOn != tipOn) { r.TipOn = tipOn; r.RailTips[0].enabled = r.RailTips[1].enabled = tipOn; r.KeyTip = -1; }
        if (tipOn && kt != r.KeyTip)
        {
            r.KeyTip = kt;
            float t = kt / 32f;
            for (int i = 0; i < 2; i++)
            {
                r.RailTipTf[i].localScale = FxMath.V3(0.12f + 0.1f * t, 0.12f + 0.1f * t, 1f);
                r.RailTips[i].color = Pm(3, t, 0.75f * t);
            }
        }
        if (fade) for (int i = 0; i < 2; i++) r.Rails[i].color = FxMath.Rgba(1f, 1f, 1f, a);

        // ---- 油圧の棒 2 本 ----
        int kq = (int)(press * 2000f);
        if (frame || kq != r.KeyPress)
        {
            r.KeyPress = kq;
            float x1 = PistonX1 - kq / 2000f;
            for (int i = 0; i < 2; i++)
            {
                float s = i == 0 ? 1f : -1f;
                Proj(r, PistonX0, s * PistonY0, -0.02f, out float x0s, out float y0s, out float d0);
                Proj(r, x1, s * PistonY1, -0.015f, out float x1s, out float y1s, out _);
                Proj(r, PistonX0, 0f, 0f, out _, out _, out float axisD);
                float z = d0 > axisD ? 0.00004f : -0.00009f;
                PlaceRod(r.PistonTf[i], x0s, y0s, x1s, y1s, 0.03f, z);
                r.CollarTf[i].localPosition = FxMath.V3(x1s, y1s, z - 0.00002f);
            }
        }
        if (fade) for (int i = 0; i < 2; i++) { r.Pistons[i].color = FxMath.Rgba(1f, 1f, 1f, a); r.Collars[i].color = FxMath.Rgba(0.6f, 0.6f, 0.65f, a); }

        // ---- 後ろの蓋 (後ろの面が見える向きの時だけ) ----
        if (frame)
        {
            RotV(r, -1f, 0f, 0f, out _, out float ny, out float nz);
            bool on = ny * ModelHeight - nz * Squash < -0.05f;
            if (on != r.CapOn) { r.CapOn = on; r.Cap.enabled = on; }
            r.KeyCap = -1;
        }
        if (r.CapOn)
        {
            int kc = (int)(cap * 2000f);
            if (kc != r.KeyCap)
            {
                r.KeyCap = kc;
                Proj(r, CapX - kc / 2000f, 0f, 0f, out float cx, out float cy, out _);
                r.CapTf.localPosition = FxMath.V3(cx, cy, -0.00002f);
            }
            if (fade) r.Cap.color = FxMath.Rgba(0.82f, 0.82f, 0.86f, a);
        }
    }

    // 横に伸ばす帯 (芯の棒・にじみ): 絵の幅 8 画素 = 0.25 単位
    private static void PlaceBar(Transform tf, float x0, float y0, float x1, float y1, float thick, float z)
    {
        float dx = x1 - x0, dy = y1 - y0, len = FxMath.Sqrt(dx * dx + dy * dy);
        tf.localPosition = FxMath.V3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, z);
        tf.localRotation = FxMath.RotZ(FxMath.Atan2(dy, dx) / Deg);
        tf.localScale = FxMath.V3(len / (8f / RodPpu) + 0.001f, thick, 1f);
    }

    // 装甲板: 砲口の斜め 4 方向の蝶番から、筒の向きと外向きの間で回る (畳む = 後ろへ寝る・開く = 前の外へ)
    private static void PlacePlates(Rig r, float open)
    {
        float phi = (PlateShut + (PlateOpen - PlateShut) * open) * Deg;
        float cp = FxMath.Cos(phi), sp = FxMath.Sin(phi);
        Proj(r, PlateX, 0f, 0f, out _, out _, out float axisD);
        for (int i = 0; i < 4; i++)
        {
            float al = (45f + 90f * i) * Deg, ca = FxMath.Cos(al), sa = FxMath.Sin(al);
            // 長さの向き = 筒の向き cosφ + 外向き sinφ・幅の向き = 輪の接線
            float lx = PlateLen * cp, ly = PlateLen * sp * ca, lz = PlateLen * sp * sa;
            float wy = -PlateW * sa, wz = PlateW * ca;
            Proj(r, PlateX + lx * 0.5f, PlateR * ca + ly * 0.5f, PlateR * sa + lz * 0.5f, out float px, out float py, out float pd);
            ProjVec(r, lx, ly, lz, out float ux, out float uy);
            ProjVec(r, 0f, wy, wz, out float vx, out float vy);
            // 絵 (幅 1・高さ PetalH) を u と v の平行四辺形へ: M = 回転 θ1 · 伸ばし (s1, s2) · 回転 θ2
            float aa = ux, b = vx / PetalH, c = uy, dd = vy / PetalH;
            float e = (aa + dd) * 0.5f, fq = (aa - dd) * 0.5f, g = (c + b) * 0.5f, h = (c - b) * 0.5f;
            float q = FxMath.Sqrt(e * e + h * h), rr = FxMath.Sqrt(fq * fq + g * g);
            float a1 = FxMath.Atan2(g, fq), a2 = FxMath.Atan2(h, e);
            var root = r.PlateRoot[i];
            root.localPosition = FxMath.V3(px, py, pd > axisD ? 0.000045f : -0.00011f);
            root.localRotation = FxMath.RotZ((a2 + a1) * 0.5f / Deg);
            root.localScale = FxMath.V3(q + rr, q - rr, 1f);
            r.PlateTf[i].localRotation = FxMath.RotZ((a2 - a1) * 0.5f / Deg);
            // 陰影: 板の外向きの面 (長さ × 幅) と光
            float nx0 = ly * wz - lz * wy, ny0 = lz * 0f - lx * wz, nz0 = lx * wy - ly * 0f;
            float nl = FxMath.Sqrt(nx0 * nx0 + ny0 * ny0 + nz0 * nz0) + 1e-6f;
            // 外へ向く側を表にする (筒の中心から外へ)
            float outd = (ca * ny0 + sa * nz0) < 0f ? -1f : 1f;
            RotV(r, outd * nx0 / nl, outd * ny0 / nl, outd * nz0 / nl, out float nx, out float ny, out float nz);
            float lit = nx * LightX + ny * LightY + nz * LightZ;
            float tone = lit > 0.55f ? 1f : lit > 0.08f ? 0.85f : 0.66f;
            if (tone != r.PlateTone[i]) { r.PlateTone[i] = tone; r.Plates[i].color = FxMath.Rgba(tone, tone, tone, r.Alpha); }
        }
    }
}
