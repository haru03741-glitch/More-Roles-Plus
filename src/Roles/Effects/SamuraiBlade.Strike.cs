using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 居合の段ごとの見た目 (全員の画面)。段は抜いた時に残っていた気で決まる (1〜4)。
// 斬線は振った刀の真ん中の向き (= 斬った相手の方) に沿って体を通り、壁や物に当たった所で止まって火花が散る。
// 段 2 から: 振り抜くとその向きへ駆け抜け (本人の端末が体を動かす)、背を向けたまま止まってからゆっくり刀を納める。
// 鍔が鳴った瞬間、駆け抜けた道筋の上に斬線が遅れて何本も走り、霜のもやと破片が散る。
// 段 3 から長い斬線と床に残る斬り跡、段 4 (溜め切り) は壁まで届く斬線が上下に裂け、斬った本人と近くにいた人の画面に
// 白飛び → 暗転に白い線の一コマが入る (IaiScreen)。線・破片・もやは刀ごとの 1 枚の網にまとめ、出ている間だけ書き直す
internal sealed unsafe partial class SamuraiBlade
{
    private const int StLines = 24, StShards = 64, StPuffs = 20;
    private const int LnCols = 4, LnRows = 5, LnVerts = LnCols * LnRows;
    private const int PfRim = 10, PfVerts = PfRim + 1;
    private const int ShardBase = StLines * LnVerts, PuffBase = ShardBase + StShards * 3;
    private const int StVerts = PuffBase + StPuffs * PfVerts;
    private const float DelayedWindow = 5f;    // 振り抜いてからこの秒のうちに納めれば遅れ斬撃が走る
    private const float ImpactReach = 2.6f;    // 段 4 の一コマが入る近さ (斬った本人の周り)
    private const float SheatheSlow = 2.2f;    // 居合の後の納刀を伸ばす倍率 (鍔が鳴る所で遅れ斬撃)

    // 線の長さ方向の位置の割合と太さの割合 (両端は尖る)・横の位置・濃さ・白の混ぜ
    private static readonly float[] LnAlong = { 0f, 0.08f, 0.92f, 1f };
    private static readonly float[] LnAlongW = { 0f, 1f, 1f, 0f };
    private static readonly float[] LnAcross = { -1f, -0.14f, 0f, 0.14f, 1f };
    private static readonly float[] LnRowA = { 0f, 0.9f, 1f, 0.9f, 0f };
    private static readonly float[] LnRowW = { 0f, 0.45f, 1f, 0.45f, 0f };
    // 床の斬り跡: 芯ほど暗く、縁は消える
    private static readonly float[] ScarRowA = { 0f, 0.8f, 1f, 0.8f, 0f };

    private struct StLine
    {
        public bool On, Scar;
        public float T0, X, Y, Z, Ang, Len, W, Grow, Hold, Fade, Drift, A;
        public float R0, G0, B0, R1, G1, B1;   // 始めの端 → 先の端の色
    }

    private struct StShard
    {
        public bool On;
        public float T0, Life, X, Y, Vx, Vy, Rot, Spin, Size, R, G, B;
    }

    // 霜のもや: 線に沿って伸びた柔らかい楕円が広がりながら流れて消える
    private struct StPuff
    {
        public bool On;
        public float T0, Life, X, Y, Vx, Vy, Ang, Len, W, A, R, G, B;
    }

    private static readonly List<SamuraiBlade> Striking = new();
    private GameObject _stGo;
    private Mesh _stMesh;
    private Il2CppStructArray<Vector3> _stV;
    private Il2CppStructArray<Color32> _stC;
    private StLine[] _lines;
    private StShard[] _shards;
    private StPuff[] _puffs;
    private int _shardNext, _puffNext;
    private float _stZ;

    // 振り抜いた後の遅れ斬撃の控え (納めた時に駆け抜けた道筋の上へ走る)
    private bool _dlOn;
    private float _dlT, _dlX0, _dlY0, _dlX1, _dlY1, _dlAng;
    private int _dlTier;
    private bool _dlRed;
    private float _sheatheSlow = 1f, _holdExtra;
    private int _lastArmedTier, _strikes, _delayedRuns;

    // 段ごとの駆け抜ける長さ・振り抜いた形で止める秒の足し
    private static float BlinkLen(int tier) => tier >= 4 ? 2.2f : tier >= 3 ? 1.4f : 0.7f;
    private static float HoldExtra(int tier) => tier >= 4 ? 0.40f : tier >= 3 ? 0.25f : 0.10f;

    // 振り抜いた: 段に応じて斬線と破片を出し、段 2 からは駆け抜けと遅れ斬撃を控える
    private void StrikeRelease(int tier)
    {
        if (tier < 2 || _hidden || !_smOn) return;
        var pc = _role.Player;
        if (!pc || !EnsureStrike(pc)) return;
        _strikes++;
        // 振った刀の真ん中の向き = 斬った向き
        float mid = (_smA0 + _smA1) * 0.5f;
        float mr = mid * FxMath.Deg2Rad, dx = FxMath.Cos(mr), dy = FxMath.Sin(mr);
        bool red = _smRed;
        var at = pc.transform.position;
        float ox = at.x, oy = at.y;
        _stZ = at.y / 1000f - 0.03f;
        float now = Time.time;
        Pal(red, out float pr, out float pg, out float pb, out float br, out float bg, out float bb);

        float wantF, wantB;
        if (tier >= 4)
        {
            var cam = Camera.main;
            float hh = cam ? cam.orthographicSize : 3f;
            float ww = hh * (cam ? cam.aspect : 1.78f);
            wantF = wantB = FxMath.Sqrt(ww * ww + hh * hh) * 1.2f;
        }
        else if (tier >= 3) { wantF = 4.5f; wantB = 1.6f; }
        else { wantF = 2.4f; wantB = 0.5f; }
        float fwd = WallDist(ox, oy, dx, dy, wantF, out bool hitF);
        float back = WallDist(ox, oy, -dx, -dy, wantB, out bool hitB);
        float len = fwd + back;
        float cx = ox + dx * (fwd - back) * 0.5f, cy = oy + dy * (fwd - back) * 0.5f;
        float blink = FxMath.Max(0f, FxMath.Min(BlinkLen(tier), fwd - 0.35f));
        float w = tier >= 4 ? 0.46f : tier >= 3 ? 0.30f : 0.18f;
        float alpha = tier >= 3 ? 1f : 0.7f;

        // 本体 (伸びる・止める・細る)
        AddLine(now, 0f, cx, cy, _stZ, mid, len, w, tier >= 4 ? 0.06f : 0.05f, tier >= 4 ? 0.20f : 0.10f, tier >= 4 ? 0.45f : 0.30f, 0f, alpha, pr, pg, pb, br, bg, bb, false);
        // 髪の毛ほどの細い線が少しずれて追いかける
        int hairs = tier >= 4 ? 4 : tier >= 3 ? 3 : 1;
        for (int i = 0; i < hairs; i++)
        {
            float side = (i % 2 == 0 ? 1f : -1f) * (0.07f + 0.06f * i);
            float px = -dy * side, py = dx * side;
            AddLine(now, 0.02f + 0.025f * i, cx + px, cy + py, _stZ, mid + FxMath.Range(-1.5f, 1.5f), len * FxMath.Range(0.55f, 0.9f), 0.035f, 0.05f, 0.08f, 0.35f, side * 0.4f, 0.9f,
                1f, 1f, 1f, br, bg, bb, false);
        }
        if (tier >= 4)
        {
            // 少し遅れて斬線の上下に裂け目が開く (空間が裂けたように離れながら薄れる)
            AddLine(now, 0.14f, cx, cy, _stZ, mid, len * 0.96f, 0.16f, 0.03f, 0.10f, 0.55f, 0.26f, 0.8f, br, bg, bb, br, bg, bb, false);
            AddLine(now, 0.14f, cx, cy, _stZ, mid, len * 0.96f, 0.16f, 0.03f, 0.10f, 0.55f, -0.26f, 0.8f, pr, pg, pb, pr, pg, pb, false);
        }
        if (tier >= 3)
        {
            // 床に残る斬り跡 (人より奥・ゆっくり消える)
            float sl = FxMath.Min(len, blink + 2.4f);
            float sx = ox + dx * (sl * 0.5f - 0.6f), sy = oy + dy * (sl * 0.5f - 0.6f) - 0.30f;
            AddLine(now, 0.06f, sx, sy, at.y / 1000f + 0.004f, mid, sl, tier >= 4 ? 0.13f : 0.09f, 0.08f, 0.6f, 1.8f, 0f, 0.55f, 0.10f, 0.16f, 0.38f, 0.16f, 0.10f, 0.30f, true);
        }
        // 霜のもやと破片が道筋に沿って立つ
        int puffs = tier >= 4 ? 8 : tier >= 3 ? 5 : 2;
        for (int i = 0; i < puffs; i++)
        {
            float t = FxMath.Range(-0.2f, 1f) * (blink + 1f);
            PuffAt(now, 0.03f + 0.02f * i, ox + dx * t, oy + dy * t, mid, FxMath.Range(0.7f, 1.4f) * (tier >= 4 ? 1.3f : 1f), red);
        }
        ShardBurst(now, 0.02f, ox + dx * blink * 0.5f, oy + dy * blink * 0.5f, mid, blink + 1.6f, tier >= 4 ? 26 : tier >= 3 ? 14 : 6, red, tier >= 3 ? 1.1f : 0.8f);
        // 壁に当たった所の火花
        if (hitF) DuelHitFx.Clash(FxMath.V3(ox + dx * fwd, oy + dy * fwd, _stZ), pc.gameObject.layer, tier >= 4 ? 0.9f : 0.6f);
        if (hitB && tier >= 4) DuelHitFx.Clash(FxMath.V3(ox - dx * back, oy - dy * back, _stZ), pc.gameObject.layer, 0.6f);
        if (tier >= 4) Impact(pc, cx, cy, mid);
        else if (tier >= 3) SamuraiImpactShake(pc, 0f, 0.10f);

        _holdExtra = HoldExtra(tier);
        _dlOn = true;
        _dlT = now;
        _dlX0 = ox - dx * 0.3f;
        _dlY0 = oy - dy * 0.3f;
        _dlX1 = ox + dx * (blink + 0.5f);
        _dlY1 = oy + dy * (blink + 0.5f);
        _dlAng = mid;
        _dlTier = tier;
        _dlRed = red;
        // 自分の刀なら、その向きへ駆け抜けて止め、ゆっくり納める (体は本人の端末が動かし、他人には位置の同期で届く)
        if (this == LocalFollow) _role.IaiFollow(dx, dy, blink, Hold + IaiHold * _strokePow + _holdExtra);
    }

    // 壁と物の当たり判定まで (want で打ち切り)。始めから中にいる当たりは数えない
    private static float WallDist(float x, float y, float dx, float dy, float want, out bool hit)
    {
        hit = false;
        float best = want;
        foreach (var h in Physics2D.CircleCastAll(FxMath.V2(x, y), 0.06f, FxMath.V2(dx, dy), want, Constants.ShipAndObjectsMask))
        {
            if (!h.collider || h.collider.isTrigger || h.distance < 0.03f || h.distance >= best) continue;
            best = h.distance;
            hit = true;
        }
        return best;
    }

    // 刀を納め始めた: 控えた遅れ斬撃を、鍔が鳴る頃に駆け抜けた道筋の上へ走らせる
    private void StrikeSheathed()
    {
        _sheatheSlow = 1f;
        if (!_dlOn) return;
        _dlOn = false;
        float now = Time.time;
        if ((now - _dlT) / SlowMo > DelayedWindow || _hidden) return;
        var pc = _role.Player;
        if (!pc || !EnsureStrike(pc)) return;
        _delayedRuns++;
        _sheatheSlow = SheatheSlow;
        _stZ = pc.transform.position.y / 1000f - 0.03f;
        float at = 0.40f * SheatheSlow;   // 納める振り付けの鍔の光と揃える
        int tier = _dlTier;
        int n = tier >= 4 ? 9 : tier >= 3 ? 6 : 3;
        Pal(_dlRed, out float pr, out float pg, out float pb, out float br, out float bg, out float bb);
        float px = _dlX1 - _dlX0, py = _dlY1 - _dlY0;
        for (int i = 0; i < n; i++)
        {
            // 道筋の上に並べ、少し横へ散らす
            float u = n == 1 ? 0.5f : (i + FxMath.Range(-0.3f, 0.3f)) / (n - 1);
            float sideJ = FxMath.Range(-0.35f, 0.35f);
            float ar0 = _dlAng * FxMath.Deg2Rad;
            float x = _dlX0 + px * u - FxMath.Sin(ar0) * sideJ, y = _dlY0 + py * u + FxMath.Cos(ar0) * sideJ;
            // 交差する斬線: 道筋から斜めに 25〜70 度
            float tilt = FxMath.Range(25f, 70f) * (i % 2 == 0 ? 1f : -1f);
            float ang = _dlAng + tilt;
            float half = (0.55f + 0.18f * tier) * FxMath.Range(0.8f, 1.15f);
            float ar = ang * FxMath.Deg2Rad, cdx = FxMath.Cos(ar), cdy = FxMath.Sin(ar);
            float f = WallDist(x, y, cdx, cdy, half, out _), b = WallDist(x, y, -cdx, -cdy, half, out _);
            float lx = x + cdx * (f - b) * 0.5f, ly = y + cdy * (f - b) * 0.5f;
            float d = at + i * 0.05f + FxMath.Range(-0.012f, 0.012f);
            bool flipC = (i & 1) == 1;
            AddLine(now, d, lx, ly, _stZ, ang, f + b, 0.14f + 0.035f * tier, 0.03f, 0.05f, 0.26f, 0f, 1f,
                flipC ? br : pr, flipC ? bg : pg, flipC ? bb : pb, flipC ? pr : br, flipC ? pg : bg, flipC ? pb : bb, false);
            // 細い線が一拍遅れて同じ所を走る
            if (tier >= 3) AddLine(now, d + 0.03f, lx, ly, _stZ, ang + FxMath.Range(-4f, 4f), (f + b) * 0.8f, 0.03f, 0.03f, 0.04f, 0.2f, 0f, 0.9f, 1f, 1f, 1f, br, bg, bb, false);
            ShardBurst(now, d + 0.02f, lx, ly, ang, (f + b) * 0.5f, tier >= 3 ? 4 : 2, _dlRed, 0.9f);
            if (i % 2 == 0) PuffAt(now, d + 0.02f, lx, ly, ang, 0.7f, _dlRed);
        }
        if (tier >= 3) SamuraiImpactShake(pc, at, 0.10f + 0.05f * (tier - 3));
    }

    // 陣営の色 (桃・青)。修羅は暗い赤・明るい赤
    private static void Pal(bool red, out float pr, out float pg, out float pb, out float br, out float bg, out float bb)
    {
        if (red) { pr = 0.80f; pg = 0.08f; pb = 0.16f; br = 1f; bg = 0.40f; bb = 0.32f; }
        else { pr = 1f; pg = 0.37f; pb = 0.66f; br = 0.29f; bg = 0.55f; bb = 1f; }
    }

    private void AddLine(float now, float delay, float cx, float cy, float z, float ang, float len, float w, float grow, float hold, float fade, float drift, float a,
        float r0, float g0, float b0, float r1, float g1, float b1, bool scar)
    {
        if (len < 0.05f) return;
        int slot = -1;
        float oldest = float.MaxValue;
        for (int i = 0; i < StLines; i++)
        {
            if (!_lines[i].On) { slot = i; break; }
            if (_lines[i].T0 < oldest) { oldest = _lines[i].T0; slot = i; }
        }
        _lines[slot] = new StLine
        {
            On = true, Scar = scar, T0 = now + delay * SlowMo, X = cx, Y = cy, Z = z, Ang = ang, Len = len, W = w, Grow = grow, Hold = hold, Fade = fade, Drift = drift, A = a,
            R0 = r0, G0 = g0, B0 = b0, R1 = r1, G1 = g1, B1 = b1,
        };
        StartStrike();
    }

    private void PuffAt(float now, float delay, float x, float y, float ang, float size, bool red)
    {
        float ar = (ang + 90f) * FxMath.Deg2Rad;
        float side = FxMath.Range(-1f, 1f);
        _puffs[_puffNext] = new StPuff
        {
            On = true, T0 = now + delay * SlowMo, Life = FxMath.Range(0.7f, 1.2f), X = x, Y = y,
            // 線から横へ少し流れる
            Vx = FxMath.Cos(ar) * side * 0.35f, Vy = FxMath.Sin(ar) * side * 0.35f + 0.05f,
            Ang = ang, Len = 0.55f * size, W = 0.22f * size, A = 0.42f,
            R = red ? 1f : 0.78f, G = red ? 0.55f : 0.90f, B = red ? 0.55f : 1f,
        };
        _puffNext = (_puffNext + 1) % StPuffs;
        StartStrike();
    }

    // 線に沿って (along 単位の幅で) 破片を散らす。白・氷の青・桃が混ざる
    private void ShardBurst(float now, float delay, float cx, float cy, float ang, float along, int n, bool red, float power)
    {
        float ar = ang * FxMath.Deg2Rad, co = FxMath.Cos(ar), si = FxMath.Sin(ar);
        for (int i = 0; i < n; i++)
        {
            float t = FxMath.Range(-0.5f, 0.5f) * along;
            float side = i % 2 == 0 ? 1f : -1f;
            float sp = FxMath.Range(0.4f, 1.3f) * power;
            float r, g, b;
            float pick = FxMath.Range(0f, 1f);
            if (red) { r = 1f; g = pick < 0.5f ? 0.85f : 0.35f; b = pick < 0.5f ? 0.8f : 0.3f; }
            else if (pick < 0.5f) { r = g = b = 1f; }
            else if (pick < 0.85f) { r = 0.70f; g = 0.88f; b = 1f; }
            else { r = 1f; g = 0.62f; b = 0.86f; }
            _shards[_shardNext] = new StShard
            {
                On = true, T0 = now + (delay + FxMath.Range(0f, 0.04f)) * SlowMo, Life = FxMath.Range(0.8f, 1.5f),
                X = cx + co * t, Y = cy + si * t,
                // 線から離れる向きへ (少し線に沿って流れる)
                Vx = -si * side * sp + co * FxMath.Range(-0.3f, 0.3f), Vy = co * side * sp + si * FxMath.Range(-0.3f, 0.3f),
                Rot = FxMath.Range(0f, 360f), Spin = FxMath.Range(-540f, 540f), Size = FxMath.Range(0.08f, 0.21f), R = r, G = g, B = b,
            };
            _shardNext = (_shardNext + 1) % StShards;
        }
        StartStrike();
    }

    private void StartStrike()
    {
        if (!_stGo.activeSelf) _stGo.SetActive(true);
        if (!Striking.Contains(this)) Striking.Add(this);
    }

    // 段 4 の一コマと揺れ: 斬った本人と、近くにいた人の画面だけ
    private void Impact(PlayerControl pc, float x, float y, float ang)
    {
        var me = PlayerControl.LocalPlayer;
        if (!me || MeetingHud.Instance) return;
        if (this != LocalFollow)
        {
            if (me.Data == null || me.Data.IsDead) return;
            Vector3 a = pc.transform.position, b = me.transform.position;
            float dx = b.x - a.x, dy = b.y - a.y;
            if (dx * dx + dy * dy > ImpactReach * ImpactReach) return;
        }
        IaiScreen.Play(x, y, ang);
        SamuraiImpactShake(pc, 0f, 0.22f);
    }

    private void SamuraiImpactShake(PlayerControl pc, float delay, float amount)
    {
        var me = PlayerControl.LocalPlayer;
        if (!me) return;
        if (this != LocalFollow)
        {
            Vector3 a = pc.transform.position, b = me.transform.position;
            float dx = b.x - a.x, dy = b.y - a.y;
            if (dx * dx + dy * dy > ImpactReach * ImpactReach) return;
        }
        if (delay > 0f) { _shakeAt = Time.time + delay * SlowMo; _shakeAmt = amount; return; }
        var hud = Vanilla.Hud;
        if (hud && hud.PlayerCam) hud.PlayerCam.ShakeScreen(amount, 1.6f);
    }

    private float _shakeAt = -10f, _shakeAmt;

    private bool EnsureStrike(PlayerControl pc)
    {
        if (_stGo) return true;
        _lines = new StLine[StLines];
        _shards = new StShard[StShards];
        _puffs = new StPuff[StPuffs];
        _stGo = new GameObject("MrpIaiStrike") { layer = pc.gameObject.layer };
        _stV = new Il2CppStructArray<Vector3>((long)StVerts);
        _stC = new Il2CppStructArray<Color32>((long)StVerts);
        int tris = StLines * (LnCols - 1) * (LnRows - 1) * 6 + StShards * 3 + StPuffs * PfRim * 3;
        var idx = new Il2CppStructArray<int>((long)tris);
        int o = 0;
        for (int l = 0; l < StLines; l++)
        for (int c = 0; c < LnCols - 1; c++)
        for (int r = 0; r < LnRows - 1; r++)
        {
            int a = l * LnVerts + c * LnRows + r, b = a + 1, d = a + LnRows, e = d + 1;
            idx[o++] = a; idx[o++] = d; idx[o++] = b;
            idx[o++] = b; idx[o++] = d; idx[o++] = e;
        }
        for (int s = 0; s < StShards; s++)
        {
            idx[o++] = ShardBase + s * 3;
            idx[o++] = ShardBase + s * 3 + 1;
            idx[o++] = ShardBase + s * 3 + 2;
        }
        for (int p = 0; p < StPuffs; p++)
        {
            int c0 = PuffBase + p * PfVerts;
            for (int k = 0; k < PfRim; k++)
            {
                idx[o++] = c0;
                idx[o++] = c0 + 1 + k;
                idx[o++] = c0 + 1 + (k + 1) % PfRim;
            }
        }
        _stMesh = new Mesh { name = "MrpIaiStrike" };
        _stMesh.vertices = _stV;
        _stMesh.colors32 = _stC;
        _stMesh.triangles = idx;
        _stMesh.bounds = new Bounds(Vector3.zero, FxMath.V3(1000f, 1000f, 10f));
        _stGo.AddComponent<MeshFilter>().mesh = _stMesh;
        var mr = _stGo.AddComponent<MeshRenderer>();
        if (!_smearMat)
        {
            var shader = Shader.Find("Sprites/Default");
            _smearMat = shader ? new Material(shader) : new Material(_sr.sharedMaterial);
            _smearMat.hideFlags = HideFlags.HideAndDontSave;
        }
        mr.sharedMaterial = _smearMat;
        return true;
    }

    // 毎フレーム (TickAll から)。出ていなければ件数の比較だけで帰る
    private static void TickStrikes()
    {
        if (Striking.Count == 0) return;
        float now = Time.time;
        for (int i = Striking.Count - 1; i >= 0; i--)
        {
            var b = Striking[i];
            if (!b._stGo) { Striking.RemoveAt(i); continue; }
            if (!b.BuildStrike(now))
            {
                b._stGo.SetActive(false);
                Striking.RemoveAt(i);
            }
        }
    }

    private static void Collapse(float* vp, byte* cp, int from, int count, float z)
    {
        for (int v = from; v < from + count; v++)
        {
            vp[v * 3] = vp[v * 3 + 1] = 0f;
            vp[v * 3 + 2] = z;
            cp[v * 4 + 3] = 0;
        }
    }

    // 出ている物が残っていれば true
    private bool BuildStrike(float now)
    {
        if (_shakeAt > 0f && now >= _shakeAt)
        {
            _shakeAt = -10f;
            var hud = Vanilla.Hud;
            if (hud && hud.PlayerCam) hud.PlayerCam.ShakeScreen(_shakeAmt, 1.6f);
        }
        bool any = _shakeAt > 0f;
        float* vp = (float*)((byte*)_stV.Pointer + 4 * IntPtr.Size);
        byte* cp = (byte*)_stC.Pointer + 4 * IntPtr.Size;
        float z = _stZ;
        for (int l = 0; l < StLines; l++)
        {
            ref var ln = ref _lines[l];
            int vb = l * LnVerts;
            float u = ln.On ? (now - ln.T0) / SlowMo : -1f;
            float total = ln.Grow + ln.Hold + ln.Fade;
            if (ln.On && u >= total) ln.On = false;
            if (!ln.On || u < 0f)
            {
                if (ln.On) any = true;
                Collapse(vp, cp, vb, LnVerts, z);
                continue;
            }
            any = true;
            // 伸びる (始めの端から先へ一気に)・止める・細りながら薄れる
            float g = FxMath.Clamp01(u / ln.Grow);
            g = 1f - (1f - g) * (1f - g) * (1f - g);
            float f = u > ln.Grow + ln.Hold ? (u - ln.Grow - ln.Hold) / ln.Fade : 0f;
            float width = ln.W * (ln.Scar ? 1f : 1f - 0.85f * f);
            float alpha = ln.A * (1f - f) * (ln.Scar ? 1f : 1f - f);
            // 止めの間は白く焼ける (床の斬り跡は焼けない)
            float hot = ln.Scar ? 0f : f <= 0f ? 0.35f : 0.35f * (1f - f);
            float ar = ln.Ang * FxMath.Deg2Rad, co = FxMath.Cos(ar), si = FxMath.Sin(ar);
            float px = -si, py = co;
            float off = ln.Drift * (1f - (1f - f) * (1f - f));
            float L = ln.Len * g * (1f + (ln.Scar ? 0f : 0.05f * f));
            float sx = ln.X - co * ln.Len * 0.5f + px * off, sy = ln.Y - si * ln.Len * 0.5f + py * off;
            for (int c = 0; c < LnCols; c++)
            {
                float along = LnAlong[c];
                float ax = sx + co * L * along, ay = sy + si * L * along;
                float hw = width * 0.5f * LnAlongW[c];
                float cr = ln.R0 + (ln.R1 - ln.R0) * along, cg = ln.G0 + (ln.G1 - ln.G0) * along, cb = ln.B0 + (ln.B1 - ln.B0) * along;
                for (int r = 0; r < LnRows; r++)
                {
                    int vi = vb + c * LnRows + r;
                    float k = LnAcross[r] * hw;
                    vp[vi * 3] = ax + px * k;
                    vp[vi * 3 + 1] = ay + py * k;
                    vp[vi * 3 + 2] = ln.Z;
                    byte* cc = cp + vi * 4;
                    if (ln.Scar)
                    {
                        cc[0] = (byte)(255f * cr);
                        cc[1] = (byte)(255f * cg);
                        cc[2] = (byte)(255f * cb);
                        cc[3] = (byte)(255f * FxMath.Clamp01(ScarRowA[r] * alpha));
                        continue;
                    }
                    float w = FxMath.Min(1f, LnRowW[r] + hot * LnRowA[r]);
                    cc[0] = (byte)(255f * (cr + (1f - cr) * w));
                    cc[1] = (byte)(255f * (cg + (1f - cg) * w));
                    cc[2] = (byte)(255f * (cb + (1f - cb) * w));
                    cc[3] = (byte)(255f * FxMath.Clamp01(LnRowA[r] * alpha));
                }
            }
        }
        int flick = (int)(now * 20f);
        for (int s = 0; s < StShards; s++)
        {
            ref var sh = ref _shards[s];
            int vb = ShardBase + s * 3;
            float u = sh.On ? (now - sh.T0) / (sh.Life * SlowMo) : -1f;
            if (sh.On && u >= 1f) sh.On = false;
            if (!sh.On || u < 0f)
            {
                if (sh.On) any = true;
                Collapse(vp, cp, vb, 3, z);
                continue;
            }
            any = true;
            // 一気に飛んで止まり、ゆっくり沈みながら回ってきらめく
            float e = 1f - (1f - u) * (1f - u) * (1f - u);
            float x = sh.X + sh.Vx * e * 0.7f, y = sh.Y + sh.Vy * e * 0.7f - 0.10f * u * u;
            float rot = (sh.Rot + sh.Spin * u) * FxMath.Deg2Rad;
            float size = sh.Size * (1f - 0.4f * u);
            float a = (1f - u) * ((flick + s) % 4 == 0 ? 0.35f : 1f);
            for (int v = 0; v < 3; v++)
            {
                // 細長い欠片 (一つの角だけ遠い三角)
                float va = rot + (v == 0 ? 0f : v == 1 ? 2.6f : 3.6f);
                float vr = size * (v == 0 ? 1f : v == 1 ? 0.35f : 0.45f);
                int vi = vb + v;
                vp[vi * 3] = x + FxMath.Cos(va) * vr;
                vp[vi * 3 + 1] = y + FxMath.Sin(va) * vr;
                vp[vi * 3 + 2] = z;
                byte* cc = cp + vi * 4;
                cc[0] = (byte)(255f * sh.R);
                cc[1] = (byte)(255f * sh.G);
                cc[2] = (byte)(255f * sh.B);
                cc[3] = (byte)(255f * FxMath.Clamp01(a));
            }
        }
        for (int p = 0; p < StPuffs; p++)
        {
            ref var pf = ref _puffs[p];
            int vb = PuffBase + p * PfVerts;
            float u = pf.On ? (now - pf.T0) / (pf.Life * SlowMo) : -1f;
            if (pf.On && u >= 1f) pf.On = false;
            if (!pf.On || u < 0f)
            {
                if (pf.On) any = true;
                Collapse(vp, cp, vb, PfVerts, z);
                continue;
            }
            any = true;
            // 広がりながら流れ、ふくらんだ所で一番濃く、ゆっくり消える
            float e = 1f - (1f - u) * (1f - u);
            float x = pf.X + pf.Vx * e, y = pf.Y + pf.Vy * e;
            float grow = 0.5f + 0.9f * e;
            float a = pf.A * (u < 0.15f ? u / 0.15f : (1f - u) / 0.85f);
            float ar = pf.Ang * FxMath.Deg2Rad, co = FxMath.Cos(ar), si = FxMath.Sin(ar);
            vp[vb * 3] = x;
            vp[vb * 3 + 1] = y;
            vp[vb * 3 + 2] = z + 0.001f;
            byte* c0 = cp + vb * 4;
            c0[0] = (byte)(255f * pf.R);
            c0[1] = (byte)(255f * pf.G);
            c0[2] = (byte)(255f * pf.B);
            c0[3] = (byte)(255f * FxMath.Clamp01(a));
            for (int k = 0; k < PfRim; k++)
            {
                float t = k * (2f * FxMath.PI / PfRim);
                float lx = FxMath.Cos(t) * pf.Len * grow, ly = FxMath.Sin(t) * pf.W * grow;
                int vi = vb + 1 + k;
                vp[vi * 3] = x + co * lx - si * ly;
                vp[vi * 3 + 1] = y + si * lx + co * ly;
                vp[vi * 3 + 2] = z + 0.001f;
                byte* cc = cp + vi * 4;
                cc[0] = (byte)(255f * pf.R);
                cc[1] = (byte)(255f * pf.G);
                cc[2] = (byte)(255f * pf.B);
                cc[3] = 0;
            }
        }
        _stMesh.vertices = _stV;
        _stMesh.colors32 = _stC;
        return any;
    }

    private void HideStrike()
    {
        _dlOn = false;
        _shakeAt = -10f;
        if (_lines != null)
            for (int i = 0; i < StLines; i++) _lines[i].On = false;
        if (_shards != null)
            for (int i = 0; i < StShards; i++) _shards[i].On = false;
        if (_puffs != null)
            for (int i = 0; i < StPuffs; i++) _puffs[i].On = false;
        if (_stGo) _stGo.SetActive(false);
        Striking.Remove(this);
    }

    private void DisposeStrike()
    {
        HideStrike();
        if (_stGo) Object.Destroy(_stGo);
        if (_stMesh) Object.Destroy(_stMesh);
        _stGo = null;
        _stMesh = null;
    }
}

// 段 4 の居合の一コマ (自分の画面だけ): 白く飛び、暗転した画面に斬線だけが白く残り、すぐに戻る。
// 本編の FullScreen は SamuraiScreen が借りているので、HUD に自前の板を置く
internal static class IaiScreen
{
    private const float White = 0.04f, Dark = 0.16f, Out = 0.34f;

    private static GameObject _root;
    private static SpriteRenderer _dark, _glow, _core;
    private static Transform _lineTf;
    private static Sprite _solid, _soft;
    private static int _shipGen = -1;
    private static float _t0 = -10f, _lineLen;
    private static bool _on;

    internal static string Describe() => $"on={_on}";

    // 世界の点 (x, y) を通る ang 度の線
    internal static void Play(float x, float y, float ang)
    {
        var hud = Vanilla.Hud;
        var main = Camera.main;
        if (!hud || !hud.UICamera || !main) return;
        if (!Ensure(hud)) return;
        var ui = hud.UICamera;
        float k = ui.orthographicSize / FxMath.Max(main.orthographicSize, 0.01f);
        var cp = main.transform.position;
        float h = ui.orthographicSize * 2f, w = h * ui.aspect;
        float fsZ = hud.FullScreen ? hud.FullScreen.transform.localPosition.z : -500f;
        _root.transform.localPosition = FxMath.V3(0f, 0f, fsZ + 1f);
        _dark.transform.localScale = FxMath.V3(w + 1f, h + 1f, 1f);
        _lineLen = FxMath.Sqrt(w * w + h * h) * 1.2f;
        _lineTf.localPosition = FxMath.V3((x - cp.x) * k, (y - cp.y) * k, -0.1f);
        _lineTf.localRotation = FxMath.RotZ(ang);
        _t0 = Time.time;
        _on = true;
        _root.SetActive(true);
    }

    // 毎フレーム。出ていなければ比較だけで帰る
    internal static void Tick()
    {
        if (!_on) return;
        if (!_root || MeetingHud.Instance) { Stop(); return; }
        float t = (Time.time - _t0) / SamuraiBlade.SlowMo;
        if (t >= Out) { Stop(); return; }
        float darkA, lineA, r, g, b, lw;
        if (t < White)
        {
            // 白く飛ぶ (線はまだ見えない)
            r = g = b = 1f;
            darkA = 0.85f;
            lineA = 0f;
            lw = 0.02f;
        }
        else if (t < Dark)
        {
            // 暗転に白い線だけが焼き付く
            r = 0.03f; g = 0.02f; b = 0.07f;
            darkA = 0.88f;
            lineA = 1f;
            lw = 0.07f;
        }
        else
        {
            float u = (t - Dark) / (Out - Dark);
            r = 0.03f; g = 0.02f; b = 0.07f;
            darkA = 0.88f * (1f - u) * (1f - u);
            lineA = 1f - u;
            lw = 0.07f * (1f - 0.6f * u);
        }
        _dark.color = FxMath.Rgba(r, g, b, darkA);
        _core.color = FxMath.Rgba(1f, 1f, 1f, lineA);
        _glow.color = FxMath.Rgba(0.55f, 0.72f, 1f, 0.75f * lineA);
        _core.transform.localScale = FxMath.V3(_lineLen, lw, 1f);
        _glow.transform.localScale = FxMath.V3(_lineLen, lw * 7f, 1f);
    }

    private static void Stop()
    {
        _on = false;
        if (_root) _root.SetActive(false);
    }

    private static bool Ensure(HudManager hud)
    {
        if (_root && _shipGen == GameClock.ShipGen) return true;
        _shipGen = GameClock.ShipGen;
        _solid ??= FxSprites.Rect(8, 8, (_, _) => 1f, "MrpIaiSolid");
        // 縦に柔らかく消え、両端が尖る帯
        _soft ??= FxSprites.Rect(256, 32, (x, y) =>
        {
            float ax = FxMath.Abs(x) * 2f, ay = FxMath.Abs(y) * 2f;
            float end = FxMath.Clamp01((1f - ax) / 0.15f);
            float across = 1f - ay;
            return across <= 0f ? 0f : across * across * end;
        }, "MrpIaiLine");
        if (!_solid || !_soft) return false;
        int layer = hud.gameObject.layer;
        _root = new GameObject("MrpIaiScreen") { layer = layer };
        _root.transform.SetParent(hud.transform, false);
        GameClock.Ship.Bind(_root);
        _dark = Part(_root.transform, _solid, 0f, layer);
        var line = new GameObject("Line") { layer = layer };
        _lineTf = line.transform;
        _lineTf.SetParent(_root.transform, false);
        _glow = Part(_lineTf, _soft, -0.01f, layer);
        _core = Part(_lineTf, _soft, -0.02f, layer);
        _root.SetActive(false);
        return true;
    }

    private static SpriteRenderer Part(Transform parent, Sprite sp, float z, int layer)
    {
        var go = new GameObject("Part") { layer = layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = FxMath.V3(0f, 0f, z);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        return sr;
    }
}
