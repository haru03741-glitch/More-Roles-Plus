using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 振った所に残る太い三日月 (全員の画面)。始めの角度と今の角度だけから式で作るので、自分のなぞりと他人の再生で同じ形になる。
// 先頭 (今の刀) ほど太く、尾は細く薄い。外の縁は白い芯 → 色 → 柔らかく消え、内へ色が薄れる。色は尾の桃から先頭の青 (修羅は赤)。
// 振り終えると尾から食われるように縮みながら薄れる
internal sealed unsafe partial class SamuraiBlade
{
    private const int SmN = 28;            // 弧の分け数
    private const int SmR = 6;             // 内から外への輪の数
    private const float SmOut = SwingGrip + TipLen + 0.02f;
    private const float SmMaxSpan = 230f;  // これより長い振りは尾を切る
    private const float SmFade = 0.15f;

    private static Material _smearMat;
    private GameObject _smearGo;
    private Mesh _smearMesh;
    private Il2CppStructArray<Vector3> _smV;
    private Il2CppStructArray<Color32> _smC;
    private float _smA0, _smA1, _smCx, _smCy, _smZ, _smStr = 1f;
    private float _smT = -10f;   // 薄れ始めた時刻 (-10 = 描いている途中)
    private bool _smOn, _smRed;

    private bool SmearLive => _smOn;

    // a0 → a1 (度・右を向いた時の値) の三日月を出す/伸ばす。ending = 振り終えた (ここから薄れる)
    private void DrawSmear(float a0, float a1, bool ending, float strength, bool world)
    {
        if (_hidden) return;
        var pc = _role.Player;
        if (!pc) return;
        if (ending)
        {
            if (_smOn && _smT < 0f) ReleaseSmear();
            return;
        }
        if (!EnsureSmear(pc)) return;
        // 抜く/納める振り付けは右向きの値なので、左を向いていれば鏡に映す
        if (!world && _flip)
        {
            a0 = 180f - a0;
            a1 = 180f - a1;
        }
        var at = pc.transform.position;
        float drop = world ? SwingDrop + _last.Hop : 0f;
        _smCx = at.x + (world ? _last.Dx : 0f);
        _smCy = at.y + drop;
        _smZ = at.y / 1000f - 0.004f;
        _smA0 = a0;
        _smA1 = a1;
        _smStr = strength;
        _smRed = this == LocalFollow || !world ? _fallenTint : _repFallen;
        _smT = -10f;
        _smOn = true;
        if (!_smearGo.activeSelf) _smearGo.SetActive(true);
        BuildSmear(0f);
    }

    private void ReleaseSmear()
    {
        if (!_smOn || _smT >= 0f) return;
        _smT = Time.time;
    }

    // 薄れる間 (毎フレーム・TickAll から)
    internal void FadeSmear(float now)
    {
        if (!_smOn || _smT < 0f) return;
        float u = (now - _smT) / (SmFade * SlowMo);
        if (u >= 1f)
        {
            _smOn = false;
            _smT = -10f;
            if (_smearGo) _smearGo.SetActive(false);
            return;
        }
        BuildSmear(u);
    }

    private bool EnsureSmear(PlayerControl pc)
    {
        if (_smearGo) return true;
        _smearGo = new GameObject("MrpKatanaSmear") { layer = pc.gameObject.layer };
        _smV = new Il2CppStructArray<Vector3>((long)(SmN * SmR));
        _smC = new Il2CppStructArray<Color32>((long)(SmN * SmR));
        var idx = new Il2CppStructArray<int>((long)((SmN - 1) * (SmR - 1) * 6));
        int o = 0;
        for (int i = 0; i < SmN - 1; i++)
        for (int r = 0; r < SmR - 1; r++)
        {
            int a = i * SmR + r, b = a + 1, c = a + SmR, d = c + 1;
            idx[o++] = a; idx[o++] = c; idx[o++] = b;
            idx[o++] = b; idx[o++] = c; idx[o++] = d;
        }
        _smearMesh = new Mesh { name = "MrpKatanaSmear" };
        _smearMesh.vertices = _smV;
        _smearMesh.colors32 = _smC;
        _smearMesh.triangles = idx;
        _smearMesh.bounds = new Bounds(Vector3.zero, FxMath.V3(1000f, 1000f, 10f));
        _smearGo.AddComponent<MeshFilter>().mesh = _smearMesh;
        var mr = _smearGo.AddComponent<MeshRenderer>();
        if (!_smearMat)
        {
            // 見つからない端末では刀の絵の材質 (同じ Sprites/Default) を写す
            var shader = Shader.Find("Sprites/Default");
            _smearMat = shader ? new Material(shader) : new Material(_sr.sharedMaterial);
            _smearMat.hideFlags = HideFlags.HideAndDontSave;
        }
        mr.sharedMaterial = _smearMat;
        return true;
    }

    // 輪ごとの半径の割合 (0 = 内の縁・1 = 外の縁)・濃さ・白の混ぜ
    private static readonly float[] RingK = { 0f, 0.22f, 0.78f, 0.92f, 0.985f, 1.12f };
    private static readonly float[] RingA = { 0f, 1f, 1f, 1f, 1f, 0f };
    private static readonly float[] RingW = { 0f, 0f, 0.10f, 0.70f, 1f, 0.6f };

    // fade = 0 (濃い) 〜 1 (消えた)
    private void BuildSmear(float fade)
    {
        float span = _smA1 - _smA0;
        float a0 = _smA0;
        if (span > SmMaxSpan) { a0 = _smA1 - SmMaxSpan; span = SmMaxSpan; }
        else if (span < -SmMaxSpan) { a0 = _smA1 + SmMaxSpan; span = -SmMaxSpan; }
        float len = FxMath.Abs(span);
        // 短すぎる振りは細く薄く
        float grow = FxMath.Clamp01(len / 60f);
        // 振り終えた後は尾から食われ、全体が少し外へ広がる
        float eat = fade * 0.85f;
        float spread = 1f + 0.06f * fade;
        float alpha = (1f - fade) * (1f - fade) * _smStr;
        float* vp = (float*)((byte*)_smV.Pointer + 4 * IntPtr.Size);
        byte* cp = (byte*)_smC.Pointer + 4 * IntPtr.Size;
        for (int i = 0; i < SmN; i++)
        {
            float u = (float)i / (SmN - 1);               // 0 = 尾・1 = 先頭
            float ang = (a0 + span * u) * FxMath.Deg2Rad;
            float co = FxMath.Cos(ang), si = FxMath.Sin(ang);
            // 太さ: 尾は細く先頭へ太る (先頭の手前で最も太い三日月)
            float thick = (0.06f + 0.62f * FxMath.Pow(u, 1.2f) * (1f - 0.3f * u * u)) * grow * (0.6f + 0.4f * _smStr);
            float rOut = SmOut * spread;
            float rIn = rOut - thick;
            float au = FxMath.Pow(u, 0.45f) * (u < eat ? 0f : FxMath.Clamp01((u - eat) / 0.12f)) * alpha;
            // 尾の濃い桃から先頭の濃い青 (陣営の青→桃を地の床に負けない濃さで)・修羅は暗い赤から明るい赤
            float cr, cg, cb;
            if (_smRed) { cr = 0.75f + 0.25f * u; cg = 0.06f + 0.25f * u; cb = 0.15f + 0.15f * u; }
            else { cr = 0.95f - 0.77f * u; cg = 0.22f + 0.20f * u; cb = 0.62f + 0.38f * u; }
            for (int r = 0; r < SmR; r++)
            {
                int vi = i * SmR + r;
                float rad = rIn + (rOut - rIn) * RingK[r];
                vp[vi * 3] = _smCx + co * rad;
                vp[vi * 3 + 1] = _smCy + si * rad;
                vp[vi * 3 + 2] = _smZ;
                float w = RingW[r];
                byte* c = cp + vi * 4;
                c[0] = (byte)(255f * (cr + (1f - cr) * w));
                c[1] = (byte)(255f * (cg + (1f - cg) * w));
                c[2] = (byte)(255f * (cb + (1f - cb) * w));
                c[3] = (byte)(255f * FxMath.Clamp01(RingA[r] * au));
            }
        }
        _smearMesh.vertices = _smV;
        _smearMesh.colors32 = _smC;
    }
}
