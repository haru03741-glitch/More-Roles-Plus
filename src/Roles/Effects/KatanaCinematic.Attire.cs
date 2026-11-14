using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 必殺技の演出の攻撃者の着せ替え。攻撃者の体はコマに描いてあるので、本人の帽子・バイザー・スキンを試合中に重ねる。
// 素材の記録 = コマごとの網 (立ち絵の画素の格子が画面のどこへ来たか・頭の置き場の 3 点) と、見える割合 (刀・拳・鞘・試合の画面の板など手前の物の所は 0)。
// 着せ替えは網に沿って別のカメラで描き (後ろの帽子と、スキン < バイザー < 前の帽子 の 2 枚)、見える割合を掛けてコマの上に重ねる
internal static unsafe partial class KatanaCinematic
{
    private const int AttireLayer = 31;      // 本編が使っていない層 (描く間だけ描画物を点ける)
    private const int MaxAtkInst = 2;        // 一つのコマに出る攻撃者の人形 (試合の画面の上と専用の空間)
    private const int SkinCells = 12;        // スキンを網へ沿わせる升の数 (一辺)
    private const int PlaneAttire = 5, PlaneAttireBack = 6;

    private sealed class AttirePart
    {
        public bool Rigid, Back;
        public float[] Px, Py;               // 頂点の立ち絵 (体の絵・左上原点) の画素
        public Material Mat;
        public readonly Mesh[] Mesh = new Mesh[MaxAtkInst];
        public readonly MeshRenderer[] Mr = new MeshRenderer[MaxAtkInst];
        public readonly Il2CppStructArray<Vector3>[] V = new Il2CppStructArray<Vector3>[MaxAtkInst];
    }

    private static readonly System.Collections.Generic.List<AttirePart> _attire = new();
    private static GameObject _attCamGo;
    private static Camera _attCam;
    private static RenderTexture _attRt, _attBackRt;
    private static Texture2D _attMaskTex, _attBackTex;
    private static Material _attMat, _attBackMat;
    private static MeshRenderer _attMr, _attBackMr;
    private static bool _attBack;
    private static float _gx0, _gy0, _gs, _ax, _ay, _ae, _bx0, _by0;
    private static int _gnx, _gny;
    private static int _attFrames; // 確かめ用: 着せ替えを重ねたコマの数

    // 図集の絵 (透明な余白を詰めてある) の、詰めた絵 (textureRect) が実際に占める世界の範囲。
    // sr.bounds は余白込みの枠なので、textureRect の UV をそこへ貼ると引き伸ばされる
    private static void PackedWorldRect(SpriteRenderer sr, out float x0, out float y0, out float x1, out float y1)
    {
        var sp = sr.sprite;
        var tr = sp.textureRect;
        var off = sp.textureRectOffset;
        var piv = sp.pivot;
        float ppu = sp.pixelsPerUnit;
        float lx0 = (off.x - piv.x) / ppu, ly0 = (off.y - piv.y) / ppu;
        float lx1 = lx0 + tr.width / ppu, ly1 = ly0 + tr.height / ppu;
        if (sr.flipX) { lx0 = -lx0; lx1 = -lx1; }
        if (sr.flipY) { ly0 = -ly0; ly1 = -ly1; }
        var t = sr.transform;
        var a = t.TransformPoint(FxMath.V3(lx0, ly0, 0f));
        var c = t.TransformPoint(FxMath.V3(lx1, ly1, 0f));
        x0 = MathF.Min(a.x, c.x); x1 = MathF.Max(a.x, c.x);
        y0 = MathF.Min(a.y, c.y); y1 = MathF.Max(a.y, c.y);
    }

    private static void BuildAttire(PlayerControl atk, int layer)
    {
        _attFrames = 0;
        var shader = MrpBundle.KatanaMaterials[3];
        var cos = atk.cosmetics;
        if (!shader || !cos || cos.currentBodySprite == null || !cos.currentBodySprite.BodySprite) return;
        _gx0 = Head(14); _gy0 = Head(15); _gs = Head(16); _gnx = (int)Head(17); _gny = (int)Head(18);
        _ax = Head(19); _ay = Head(20); _ae = Head(21); _bx0 = Head(22); _by0 = Head(23);
        if (_gnx < 2 || _gny < 2 || _gs <= 0f || _ae <= 0f) return;
        var body = cos.currentBodySprite.BodySprite;
        bool flip = cos.FlipX;
        int color = atk.Data.DefaultOutfit.ColorId;
        float ppu = body.sprite ? body.sprite.pixelsPerUnit : 100f;
        (SpriteRenderer sr, bool rigid, bool back, int order)[] srcs =
        {
            (cos.hat ? cos.hat.BackLayer : null, true, true, 1),
            (cos.skin ? cos.skin.layer : null, false, false, 1),
            (cos.visor ? cos.visor.Image : null, true, false, 2),
            (cos.hat ? cos.hat.FrontLayer : null, true, false, 3),
        };
        float aspect = _w / (float)_h;
        foreach (var (sr, rigid, back, order) in srcs)
        {
            if (!sr || !sr.enabled || !sr.sprite || !sr.gameObject.activeInHierarchy) continue;
            if (_attire.Count == 0 && !AttireSetup(layer, aspect)) return;
            var sp = sr.sprite;
            var tex = sp.texture;
            // 図集の絵は透明な余白を詰めてある: 詰めた絵 (textureRect) が絵の枠のどこにあるかから、その局所の範囲を出す (枠いっぱいに引き伸ばさない)
            var tr = sp.textureRect;
            var off = sp.textureRectOffset;
            var piv = sp.pivot;
            float sppu = sp.pixelsPerUnit;
            float lx0 = (off.x - piv.x) / sppu, ly0 = (off.y - piv.y) / sppu;
            float lx1 = lx0 + tr.width / sppu, ly1 = ly0 + tr.height / sppu;
            if (sr.flipX) { lx0 = -lx0; lx1 = -lx1; }
            var stf = sr.transform;
            float u0 = tr.x / tex.width, u1 = tr.xMax / tex.width, v0 = tr.y / tex.height, v1 = tr.yMax / tex.height;
            int k = rigid ? 1 : SkinCells, n = (k + 1) * (k + 1);
            var part = new AttirePart { Rigid = rigid, Back = back, Px = new float[n], Py = new float[n] };
            var uv = new Vector2[n];
            for (int j = 0; j <= k; j++)
                for (int i = 0; i <= k; i++)
                {
                    float tx = i / (float)k, ty = j / (float)k;
                    var w = stf.TransformPoint(FxMath.V3(lx0 + (lx1 - lx0) * tx, ly0 + (ly1 - ly0) * ty, 0f));
                    var lp = body.transform.InverseTransformPoint(w);
                    float lx = flip ? -lp.x : lp.x;
                    int vi = j * (k + 1) + i;
                    part.Px[vi] = _bx0 + lx * ppu;
                    part.Py[vi] = _by0 - lp.y * ppu;
                    uv[vi] = FxMath.V2(u0 + (u1 - u0) * tx, v0 + (v1 - v0) * ty);
                }
            var tris = new int[k * k * 6];
            for (int j = 0, t = 0; j < k; j++)
                for (int i = 0; i < k; i++, t += 6)
                {
                    int a = j * (k + 1) + i;
                    tris[t] = a; tris[t + 1] = a + k + 1; tris[t + 2] = a + 1;
                    tris[t + 3] = a + 1; tris[t + 4] = a + k + 1; tris[t + 5] = a + k + 2;
                }
            part.Mat = new Material(sr.sharedMaterial) { hideFlags = HideFlags.HideAndDontSave, mainTexture = tex };
            for (int c = 0; c < MaxAtkInst; c++)
            {
                var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                mesh.MarkDynamic();
                part.V[c] = new Il2CppStructArray<Vector3>((long)n);
                mesh.vertices = part.V[c];
                mesh.uv = uv;
                mesh.triangles = tris;
                var go = new GameObject("Attire") { layer = AttireLayer };
                go.transform.SetParent(_attCamGo.transform, false);
                go.transform.localPosition = FxMath.V3(0f, 0f, 5f - 0.01f * order);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = part.Mat;
                mr.sortingOrder = order;
                PlayerMaterial.SetColors(color, mr);
                mr.enabled = false;
                part.Mesh[c] = mesh;
                part.Mr[c] = mr;
            }
            if (back) _attBack = true;
            _attire.Add(part);
            Plugin.Logger.LogInfo($"[KatanaCinematic] attire {sp.name} rigid={rigid} back={back} px=({part.Px[0]:0},{part.Py[0]:0})-({part.Px[n - 1]:0},{part.Py[n - 1]:0}) flip={flip}/{sr.flipX}");
        }
        if (_attire.Count == 0) return;
        _attMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        _attMat.SetTexture("_MainTex", _attRt);
        _attMat.SetTexture("_MaskTex", _attMaskTex);
        _attMr = MeshPart("Attire", _quad, _attMat, layer, -0.08f, Order + 10);
        _attMr.enabled = false;
        if (_attBack)
        {
            _attBackRt = new RenderTexture(_w, _h, 0, RenderTextureFormat.ARGB32) { name = "MrpKatanaAttireBack" };
            _attBackMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _attBackMat.SetTexture("_MainTex", _attBackRt);
            _attBackMat.SetTexture("_MaskTex", _attBackTex);
            _attBackMr = MeshPart("AttireBack", _quad, _attBackMat, layer, -0.07f, Order + 9);
            _attBackMr.enabled = false;
        }
    }

    // 着せ替えだけを描くカメラ (試合の外の遠い所・描く時だけ呼ぶ) と描き先
    private static bool AttireSetup(int layer, float aspect)
    {
        _attCamGo = new GameObject("MrpKatanaAttireCam") { layer = AttireLayer };
        _attCamGo.transform.position = FxMath.V3(-5000f, -5000f, -10f);
        _attCam = _attCamGo.AddComponent<Camera>();
        _attCam.enabled = false;
        _attCam.orthographic = true;
        _attCam.orthographicSize = 0.5f;
        _attCam.aspect = aspect;
        _attCam.clearFlags = CameraClearFlags.SolidColor;
        _attCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        _attCam.cullingMask = 1 << AttireLayer;
        _attCam.nearClipPlane = 0.01f;
        _attCam.farClipPlane = 20f;
        _attCam.allowHDR = false;
        _attCam.allowMSAA = false;
        _attRt = new RenderTexture(_w, _h, 0, RenderTextureFormat.ARGB32) { name = "MrpKatanaAttire" };
        _attMaskTex = new Texture2D(_w, _h, TextureFormat.R8, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        _attBackTex = new Texture2D(_w, _h, TextureFormat.R8, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        return true;
    }

    // 見える割合をテクスチャへ (コマを出す時に・置き場の 5 枚目と 6 枚目)
    private static void AttireMasks(int f, byte* buf, int px)
    {
        if (_attire.Count == 0 || (U(f, 1) & 32) == 0) return;
        _attMaskTex.LoadRawTextureData((IntPtr)(buf + px * PlaneAttire), px);
        _attMaskTex.Apply(false);
        if (_attBack)
        {
            _attBackTex.LoadRawTextureData((IntPtr)(buf + px * PlaneAttireBack), px);
            _attBackTex.Apply(false);
        }
    }

    // 網に沿って着せ替えを置き、2 枚に描く。不透明のコマ (インパクト)・網の無いコマでは出さない
    private static void AttireFrame(int f)
    {
        if (_attire.Count == 0) return;
        bool on = U(f, 0) == 0 && (U(f, 1) & 32) != 0;
        _attMr.enabled = on;
        if (_attBackMr) _attBackMr.enabled = on;
        if (!on) return;
        byte* p = _data + _blob + U(f, 128);
        int n = Math.Min((int)*(uint*)p, MaxAtkInst);
        p += 4;
        int gridFloats = _gnx * _gny * 2;
        float aspect = _w / (float)_h;
        for (int c = 0; c < MaxAtkInst; c++)
        {
            float* g = null, hd = null;
            if (c < n)
            {
                g = (float*)(p + 4);
                hd = g + gridFloats;
                p += 4 + (gridFloats + 6) * 4;
            }
            foreach (var part in _attire)
            {
                if (g == null) { part.Mr[c].enabled = false; continue; }
                var v = (Vector3*)((byte*)part.V[c].Pointer + 4 * IntPtr.Size);
                for (int i = 0; i < part.Px.Length; i++)
                {
                    AttirePoint(part.Rigid, part.Px[i], part.Py[i], g, hd, out float sx, out float sy);
                    v[i] = FxMath.V3((sx - 0.5f) * aspect, sy - 0.5f, 0f);
                }
                part.Mesh[c].vertices = part.V[c];
                part.Mesh[c].RecalculateBounds();
            }
        }
        // 前: スキン・バイザー・前の帽子 / 後ろ: 後ろの帽子 (体の所は見える割合で抜いてある)
        AttireRender(_attRt, false, n);
        if (_attBack) AttireRender(_attBackRt, true, n);
        _attFrames++;
    }

    // 立ち絵の画素 (px, py) → 画面の 0〜1 (左下原点)。固い物は頭の 3 点の 1 次近似・スキンは格子の双一次
    private static void AttirePoint(bool rigid, float px, float py, float* g, float* hd, out float sx, out float sy)
    {
        if (rigid)
        {
            float u = (px - _ax) / _ae, w = (py - _ay) / _ae;
            sx = hd[0] + u * (hd[2] - hd[0]) + w * (hd[4] - hd[0]);
            sy = hd[1] + u * (hd[3] - hd[1]) + w * (hd[5] - hd[1]);
            return;
        }
        float fx = FxMath.Clamp((px - _gx0) / _gs, 0f, _gnx - 1.001f);
        float fy = FxMath.Clamp((py - _gy0) / _gs, 0f, _gny - 1.001f);
        int i0 = (int)fx, j0 = (int)fy;
        float tx = fx - i0, ty = fy - j0;
        float* a = g + (j0 * _gnx + i0) * 2;
        float* b = a + 2;
        float* c = a + _gnx * 2;
        float* d = c + 2;
        float k00 = (1f - tx) * (1f - ty), k10 = tx * (1f - ty), k01 = (1f - tx) * ty, k11 = tx * ty;
        sx = a[0] * k00 + b[0] * k10 + c[0] * k01 + d[0] * k11;
        sy = a[1] * k00 + b[1] * k10 + c[1] * k01 + d[1] * k11;
    }

    private static void AttireRender(RenderTexture rt, bool back, int n)
    {
        foreach (var part in _attire)
            for (int c = 0; c < MaxAtkInst; c++) part.Mr[c].enabled = c < n && part.Back == back;
        try
        {
            _attCam.targetTexture = rt;
            _attCam.Render();
        }
        finally
        {
            _attCam.targetTexture = null;
            foreach (var part in _attire)
                for (int c = 0; c < MaxAtkInst; c++) part.Mr[c].enabled = false;
        }
    }

    private static void AttireDestroy()
    {
        foreach (var part in _attire)
        {
            foreach (var m in part.Mesh) if (m) UnityEngine.Object.Destroy(m);
            if (part.Mat) UnityEngine.Object.Destroy(part.Mat);
        }
        _attire.Clear();
        _attBack = false;
        foreach (var o in new UnityEngine.Object[] { _attMat, _attBackMat, _attMaskTex, _attBackTex }) if (o) UnityEngine.Object.Destroy(o);
        foreach (var rt in new[] { _attRt, _attBackRt }) if (rt) { rt.Release(); UnityEngine.Object.Destroy(rt); }
        if (_attCamGo) UnityEngine.Object.Destroy(_attCamGo);
        _attMat = _attBackMat = null;
        _attMaskTex = _attBackTex = null;
        _attRt = _attBackRt = null;
        _attCamGo = null;
        _attCam = null;
        _attMr = _attBackMr = null;
    }
}
