using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 必殺技の演出の被害者。空間では本編の大きなクルーの絵 (やられの形) を本人の色で塗り、帽子とバイザーを頭に付けて、
// 書き出した位置・回し・潰れで置く。手前の物 (攻撃者・刀・斬線) はコマを手前の型の割合でもう一度描いて被害者の上に戻す。
// 入りの生の画面では、抜刀のコマで本物の服 (スキン) を消し、服の絵を斬線で 2 片にして書き出した動きで飛ばす
internal static unsafe partial class KatanaCinematic
{
    private const int MaxVic = 5, MaxPieces = 8;
    private const float KMsc = 150f / 210f, Px = 0.01f;
    private static readonly string[] PoseNames = { "stand", "shock", "curl", "recoil", "kneel", "spun" };
    // 立ち絵の画素 (y 上向き) → 各ポーズの絵の画素。帽子とバイザーは頭の置き場で固い板として写る (道具の網から求めた厳密なアフィン)
    private static readonly float[][] PoseAffine =
    {
        new[] { 1f, 0f, 0f, 1f, 0f, 0f },
        new[] { 2.1093f, 0.7487f, -0.6960f, 2.4401f, -200.24f, -74.33f },
        new[] { 1.5932f, 1.1235f, -0.1889f, 2.2115f, -205.72f, -59.83f },
        new[] { 1.1470f, 1.5166f, -0.3281f, 1.5052f, -204.21f, 83.51f },
        new[] { 1.9189f, 0.4214f, -0.1721f, 1.5598f, -83.52f, 61.27f },
        new[] { -1.1628f, -0.1522f, -0.1397f, 1.0620f, 248.34f, 150.59f },
    };
    private static readonly Texture2D[] PoseTex = new Texture2D[6];
    private static readonly Sprite[] PoseSprite = new Sprite[6];
    private const float IdleW = 150f, IdleH = 192f;

    private sealed class Victim
    {
        public PlayerControl Pc;
        public int Color;
        public GameObject Go;
        public Transform Tf;
        public SpriteRenderer Body, Flash;
        public Material BodyMat;
        public readonly System.Collections.Generic.List<(MeshRenderer mr, Mesh mesh, Material mat)> Items = new();
        public readonly float[][] ItemCorners = new float[3][];   // 帽子の後ろ・バイザー・帽子の前の四隅 (立ち絵の画素)
        public readonly Rect[] ItemUv = new Rect[3];
        public int Pose = -2;
        public SpriteRenderer Skin;
        public bool SkinWas;
    }

    private static readonly Victim[] _vic = new Victim[MaxVic];
    private static int _vicCount;
    private static MeshRenderer _maskMr;
    private static Material _maskMat, _flashMat;

    // 空間に出すのは近い順に 5 人まで (残りは出で一緒に倒れる)
    private static void BuildVictims(PlayerControl[] victims, int layer)
    {
        _vicCount = 0;
        if (victims == null || victims.Length == 0) return;
        if (!LoadPoses()) return;
        var bodyMat = FxHands.PlayerMat();
        if (!bodyMat) return;
        _flashMat = MrpBundle.KatanaMaterials[2] ? new Material(MrpBundle.KatanaMaterials[2]) { hideFlags = HideFlags.HideAndDontSave } : null;
        _maskMat = new Material(MrpBundle.KatanaMaterials[1]) { hideFlags = HideFlags.HideAndDontSave };
        _maskMat.SetTexture("_MainTex", _colTex);
        _maskMat.SetTexture("_AlphaTex", _alphaTex);
        _maskMat.SetTexture("_MaskTex", _maskTex);
        _maskMat.SetFloat("_UseMask", 1f);
        _maskMat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        _maskMat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        _maskMr = MeshPart("FrontAgain", _quad, _maskMat, layer, -0.06f, Order + 8);
        _maskMr.enabled = false;
        for (int i = 0; i < victims.Length && _vicCount < Math.Min(MaxVic, _nVic); i++)
        {
            var pc = victims[i];
            if (!pc || pc.Data == null) continue;
            var v = new Victim { Pc = pc, Color = pc.Data.DefaultOutfit.ColorId };
            v.Go = new GameObject("Victim" + _vicCount) { layer = layer };
            v.Tf = v.Go.transform;
            v.Tf.SetParent(_root.transform, false);
            v.Tf.localPosition = FxMath.V3(0f, 0f, -0.03f);
            v.Body = v.Go.AddComponent<SpriteRenderer>();
            v.BodyMat = new Material(bodyMat) { hideFlags = HideFlags.HideAndDontSave };
            v.Body.sharedMaterial = v.BodyMat;
            v.Body.sortingOrder = Order + 4;
            PlayerMaterial.SetColors(v.Color, v.Body);
            if (_flashMat)
            {
                var fgo = new GameObject("Flash") { layer = layer };
                fgo.transform.SetParent(v.Tf, false);
                fgo.transform.localPosition = FxMath.V3(0f, 0f, -0.002f);
                v.Flash = fgo.AddComponent<SpriteRenderer>();
                v.Flash.sharedMaterial = _flashMat;
                v.Flash.sortingOrder = Order + 7;
            }
            BuildItems(v, layer);
            v.Go.SetActive(false);
            var cos = pc.cosmetics;
            v.Skin = cos && cos.skin ? cos.skin.layer : null;
            v.SkinWas = v.Skin && v.Skin.enabled;
            _vic[_vicCount++] = v;
        }
    }

    private static bool LoadPoses()
    {
        for (int i = 0; i < PoseNames.Length; i++)
        {
            if (PoseSprite[i]) continue;
            using var st = typeof(KatanaCinematic).Assembly.GetManifestResourceStream($"MoreRolesPlus.Resources.Cinema.katana_pose_{PoseNames[i]}.png");
            if (st == null) return false;
            var data = new byte[st.Length];
            for (int n = 0; n < data.Length;)
            {
                int r = st.Read(data, n, data.Length - n);
                if (r <= 0) return false;
                n += r;
            }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            if (!tex.LoadImage(data, true)) return false;
            PoseTex[i] = tex;
            // 足元 = 絵の下の中央・1 画素 = 0.01 (置く時の倍率で場の大きさに合わせる)
            PoseSprite[i] = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), FxMath.V2(0.5f, 0f), 100f, 0, SpriteMeshType.FullRect);
            PoseSprite[i].hideFlags = HideFlags.HideAndDontSave;
        }
        return true;
    }

    // 帽子 (後ろ・前) とバイザーを、本物の体との位置関係から立ち絵の画素の四隅に直して持つ (ポーズごとのアフィンで写して置く)
    private static void BuildItems(Victim v, int layer)
    {
        var cos = v.Pc.cosmetics;
        if (!cos || cos.currentBodySprite == null || !cos.currentBodySprite.BodySprite) return;
        var body = cos.currentBodySprite.BodySprite;
        var bb = body.bounds;
        if (bb.size.y <= 0.01f) return;
        bool flip = cos.FlipX;
        float k = IdleH / bb.size.y, cx = bb.center.x, by = bb.min.y;
        SpriteRenderer[] srcs =
        {
            cos.hat ? cos.hat.BackLayer : null,
            cos.visor ? cos.visor.Image : null,
            cos.hat ? cos.hat.FrontLayer : null,
        };
        int[] orders = { Order + 3, Order + 5, Order + 6 };
        for (int s = 0; s < 3; s++)
        {
            var sr = srcs[s];
            if (!sr || !sr.enabled || !sr.sprite) continue;
            var sp = sr.sprite;
            var b = sr.bounds;
            // 体の左右の向きに関わらず、立ち絵 (右向き) の画素へ
            float x0 = (b.min.x - cx) * k, x1 = (b.max.x - cx) * k;
            if (flip) { float t = -x1; x1 = -x0; x0 = t; }
            float y0 = (b.min.y - by) * k, y1 = (b.max.y - by) * k;
            v.ItemCorners[s] = new[] { x0 + IdleW * 0.5f, y0, x1 + IdleW * 0.5f, y1, sr.flipX != flip ? 1f : 0f };
            var tr = sp.textureRect;
            var tex = sp.texture;
            v.ItemUv[s] = new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width, tr.height / tex.height);
            var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            mesh.MarkDynamic();
            var mat = new Material(sr.sharedMaterial) { hideFlags = HideFlags.HideAndDontSave, mainTexture = tex };
            var go = new GameObject("Item" + s) { layer = layer };
            go.transform.SetParent(v.Tf, false);
            go.transform.localPosition = FxMath.V3(0f, 0f, -0.001f * (s + 1));
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.sortingOrder = orders[s];
            PlayerMaterial.SetColors(v.Color, mr);
            v.Items.Add((mr, mesh, mat));
        }
    }

    // ポーズが変わった時だけ帽子とバイザーの四隅を写し直す (体の絵の局所 = 画素 × 0.01・足元が原点)
    private static void PlaceItems(Victim v, int pose)
    {
        var A = PoseAffine[pose];
        float w = pose == 0 ? IdleW : PoseTex[pose].width;
        int it = 0;
        for (int s = 0; s < 3; s++)
        {
            var c = v.ItemCorners[s];
            if (c == null) continue;
            var (mr, mesh, _) = v.Items[it++];
            var uvr = v.ItemUv[s];
            bool fx = c[4] > 0.5f;
            var verts = new Vector3[4];
            float[] xs = { c[0], c[2], c[2], c[0] }, ys = { c[1], c[1], c[3], c[3] };
            for (int j = 0; j < 4; j++)
            {
                float px = A[0] * xs[j] + A[1] * ys[j] + A[4], py = A[2] * xs[j] + A[3] * ys[j] + A[5];
                verts[j] = FxMath.V3((px - w * 0.5f) * Px, py * Px, 0f);
            }
            float u0 = fx ? uvr.xMax : uvr.xMin, u1 = fx ? uvr.xMin : uvr.xMax;
            mesh.vertices = verts;
            mesh.uv = new Vector2[] { FxMath.V2(u0, uvr.yMin), FxMath.V2(u1, uvr.yMin), FxMath.V2(u1, uvr.yMax), FxMath.V2(u0, uvr.yMax) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
        }
    }

    // 記録の被害者 (32 + i × 10): 足元の画面の位置・場の 1 が画面の幅のいくつか・回し (度)・左右・絵 (−1 = 出さない)・潰れ・伸び・白の閃き
    private static void VictimsFrame(int f)
    {
        bool any = false;
        bool opaque = U(f, 0) != 0;
        uint flags = U(f, 1);
        // 板が閉じきった後は空間が見えない (記録の被害者は最後の形のまま残っているので出さない)
        bool closed = !opaque && U(f, 118) != 0;
        for (int i = 0; i < _vicCount; i++)
        {
            var v = _vic[i];
            int k = 32 + i * 10;
            int pose = (int)F(f, k + 5);
            // インパクトのコマ: A (白地に墨) では墨の影絵・B では出さない
            if (pose < 0 || closed || (opaque && (flags & 8) != 0)) { if (v.Go.activeSelf) v.Go.SetActive(false); continue; }
            any = true;
            if (!v.Go.activeSelf) v.Go.SetActive(true);
            if (pose != v.Pose)
            {
                v.Pose = pose;
                v.Body.sprite = PoseSprite[pose];
                if (v.Flash) v.Flash.sprite = PoseSprite[pose];
                PlaceItems(v, pose);
            }
            float unit = F(f, k + 2) * _frameW;
            float kp = (pose == 0 ? 1f : KMsc) * Head(2) * unit;
            float sq = F(f, k + 6), st = F(f, k + 7);
            float sx = kp * (1f + 0.17f * sq) * (1f - 0.12f * st) * F(f, k + 4), sy = kp * (1f - 0.2f * sq) * (1f + 0.2f * st);
            v.Tf.localPosition = FxMath.V3((F(f, k) - 0.5f) * _frameW, (F(f, k + 1) - 0.5f) * _frameH, -0.03f);
            v.Tf.localRotation = FxMath.RotZ(F(f, k + 3));
            v.Tf.localScale = FxMath.V3(sx, sy, 1f);
            bool ink = opaque && (flags & 4) != 0;
            v.Body.color = ink ? FxMath.Rgba(0f, 0f, 0f, 1f) : FxMath.Rgba(1f, 1f, 1f, 1f);
            if (v.Flash)
            {
                float fl = FxMath.Min(1f, F(f, k + 8) * 0.25f);
                v.Flash.enabled = fl > 0.01f;
                v.Flash.color = FxMath.Rgba(1f, 1f, 1f, fl);
            }
        }
        if (_maskMr) _maskMr.enabled = any && !opaque && (flags & 16) != 0;
    }

    private static void VictimsDestroy()
    {
        for (int i = 0; i < _vicCount; i++)
        {
            var v = _vic[i];
            if (v == null) continue;
            if (v.Skin && v.SkinWas) v.Skin.enabled = true;
            foreach (var (_, mesh, mat) in v.Items)
            {
                if (mesh) UnityEngine.Object.Destroy(mesh);
                if (mat) UnityEngine.Object.Destroy(mat);
            }
            if (v.BodyMat) UnityEngine.Object.Destroy(v.BodyMat);
            _vic[i] = null;
        }
        _vicCount = 0;
        foreach (var o in new UnityEngine.Object[] { _maskMat, _flashMat }) if (o) UnityEngine.Object.Destroy(o);
        _maskMat = _flashMat = null;
        _maskMr = null;
        PiecesDestroy();
    }
}
