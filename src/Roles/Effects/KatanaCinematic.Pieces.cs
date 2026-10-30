using System;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 円の一閃: 抜刀で攻撃者を中心に斬撃の輪が広がり (半径は記録の 119 番・場の長さ)、輪が届いた人から順に
// 体に輪に沿った斬線が走り、服 (スキン) だけが 2 片に斬れて外の片は外へ・内の片は下へ落ちて消える。帽子とバイザーは残る。
// 服を着ていない人にも斬線は出る。並び方や人数に依らないよう、片の動きは数式で作る
internal static unsafe partial class KatanaCinematic
{
    private const int MaxHits = 16;
    private const float StreakLen = 2.6f;          // 斬線の長さ (場の長さ)
    private const int RingCx = 4, RingCy = 5;
    private const float StreakAspect = 8f;         // 帯の絵は 128×16 (1 単位 = 16 画素) = 横 8 単位      // 頭の数値: 輪の中心 (攻撃者の体の中ほど・場の座標)

    private sealed class Hit
    {
        public PlayerControl Pc;
        public SpriteRenderer Skin;
        public bool SkinWas;
        public float Bx, By;            // 体の中ほどの場の座標
        public float D, Ang;            // 攻撃者からの距離 (場) と向き (rad)
        public int At = -1;             // 輪が届いたコマ
        public Transform Streak;
        public SpriteRenderer Glow, Core;
        public readonly Piece[] Pcs = new Piece[2];
    }

    private sealed class Piece
    {
        public GameObject Go;
        public Transform Tf;
        public Mesh Mesh;
        public Material Mat;
        public float Bx, By;            // 片の中心の場の座標 (斬れた時)
        public float Sg, Far, Spin;     // 外 (+1) / 内 (−1)・飛ぶ遠さ・回し (度)
        public Color Col;
    }

    private static readonly Hit[] _hits = new Hit[MaxHits];
    private static int _hitCount, _pcCount;
    private static Vector3 _camCenter0;
    private static Sprite _streakSprite;

    // 斬られる人を近い順に覚える (位置は演出の間は止まっているので始めに 1 回だけ読む)
    private static void BuildHits(PlayerControl[] victims, int layer)
    {
        _hitCount = _pcCount = 0;
        if (victims == null || victims.Length == 0) return;
        _streakSprite ??= FxSprites.Rect(128, 16, (x, y) =>
        {
            float ax = FxMath.Abs(x) * 2f, ay = FxMath.Abs(y) * 2f;
            float end = FxMath.Clamp01((1f - ax) / 0.35f);
            float across = 1f - ay;
            return across <= 0f ? 0f : across * across * end;
        }, "MrpKatanaStreak");
        float rcx = Head(RingCx), rcy = Head(RingCy);
        var rnd = new System.Random(_shipGen * 31 + victims.Length);
        for (int i = 0; i < victims.Length && _hitCount < MaxHits; i++)
        {
            var pc = victims[i];
            if (!pc) continue;
            // 体の中ほど (体の絵の中心)。絵が無ければ立ち位置
            var cos = pc.cosmetics;
            var body = cos && cos.currentBodySprite != null ? cos.currentBodySprite.BodySprite : null;
            var p = body ? body.bounds.center : pc.transform.position;
            var h = new Hit { Pc = pc };
            h.Bx = (p.x - _camCenter0.x) / PlateWorld;
            h.By = (p.y - _camCenter0.y) / PlateWorld;
            float dx = h.Bx - rcx, dy = h.By - rcy;
            h.D = FxMath.Sqrt(dx * dx + dy * dy);
            h.Ang = MathF.Atan2(dy, dx);
            h.Skin = cos && cos.skin ? cos.skin.layer : null;
            h.SkinWas = h.Skin && h.Skin.enabled;
            if (_streakSprite)
            {
                var go = new GameObject("RingStreak") { layer = layer };
                h.Streak = go.transform;
                h.Streak.SetParent(_root.transform, false);
                h.Glow = StreakPart(h.Streak, -0.001f, Order + 10, layer);
                h.Core = StreakPart(h.Streak, -0.002f, Order + 11, layer);
                go.SetActive(false);
            }
            for (int s = 0; s < 2; s++)
                h.Pcs[s] = new Piece { Sg = s == 0 ? 1f : -1f, Far = 0.6f + 0.6f * (float)rnd.NextDouble(), Spin = (25f + 35f * (float)rnd.NextDouble()) * (rnd.Next(2) == 0 ? 1f : -1f) };
            _hits[_hitCount++] = h;
        }
    }

    // 確かめ用: 人ごとの距離 (場)・輪が届いたコマ・斬線の位置
    private static string HitsProbe()
    {
        var sb = new System.Text.StringBuilder($"hits={_hitCount}");
        for (int i = 0; i < _hitCount; i++)
        {
            var h = _hits[i];
            sb.Append($" [{h.D:0.0} at={h.At} on={(h.Streak ? h.Streak.gameObject.activeSelf : false)} pos={(h.Streak ? h.Streak.localPosition : default)} sc={(h.Core ? h.Core.transform.localScale : default)}]");
        }
        return sb.ToString();
    }

    private static SpriteRenderer StreakPart(Transform parent, float z, int order, int layer)
    {
        var go = new GameObject("Part") { layer = layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = FxMath.V3(0f, 0f, z);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _streakSprite;
        sr.sortingOrder = order;
        return sr;
    }

    private static void PiecesFrame(int f)
    {
        if (_hitCount == 0) return;
        bool opaque = U(f, 0) != 0;
        float ring = opaque ? 0f : F(f, 119);
        float cx = F(f, 22), cz = F(f, 23), o = F(f, 24), th = F(f, 25);
        float roll = th * (180f / MathF.PI);
        float unit = _frameW / o;                          // 場の 1 → HUD の長さ
        for (int i = 0; i < _hitCount; i++)
        {
            var h = _hits[i];
            if (h.At < 0)
            {
                if (ring <= 0f || ring < h.D) continue;
                h.At = f;
                CutSkin(h);
            }
            int t = f - h.At;
            StreakFrame(h, t, opaque, cx, cz, o, th, roll, unit);
            for (int s = 0; s < 2; s++)
            {
                var p = h.Pcs[s];
                if (p.Go == null) continue;
                float a = t <= 7 ? 1f : FxMath.Clamp01((14 - t) / 7f);
                if (opaque || a <= 0.001f) { if (p.Go.activeSelf) p.Go.SetActive(false); continue; }
                if (!p.Go.activeSelf) p.Go.SetActive(true);
                // 斬線に沿う動き・外へ (内の片は内へ) の動き・回し・落ち (道具の skin_pieces のキーと同じ数)
                Key(t, out float along, out float outw, out float spin, out float fall);
                float tang = h.Ang + MathF.PI / 2f;
                float ux = MathF.Cos(tang), uy = MathF.Sin(tang), nx = MathF.Cos(h.Ang), ny = MathF.Sin(h.Ang);
                float bx = p.Bx + ux * along * p.Sg * p.Far + nx * outw * p.Far * p.Sg, by = p.By + uy * along * p.Sg * p.Far + ny * outw * p.Far * p.Sg - fall * p.Far;
                var (sx, sy) = Proj(cx, cz, o, th, bx, by);
                p.Tf.localPosition = FxMath.V3((sx - 0.5f) * _frameW, (sy - 0.5f) * _frameH, -0.08f);
                p.Tf.localRotation = FxMath.RotZ(spin * p.Spin + roll);
                float sc = _frameW / (PlateWorld * o);
                p.Tf.localScale = FxMath.V3(sc, sc, 1f);
                var c = p.Col;
                c.a *= a;
                p.Mat.color = c;
            }
        }
    }

    // t = 斬れてからのコマ。0→2→6→14 の折れ線 (飛ぶ遠さを掛ける前の値)
    private static void Key(int t, out float along, out float outw, out float spin, out float fall)
    {
        float u;
        if (t <= 0) { along = 0.06f; outw = 0.05f; spin = 0f; fall = 0f; return; }
        if (t <= 2) { u = t / 2f; along = 0.06f + 0.19f * u; outw = 0.05f + 0.25f * u; spin = 0.3f * u; fall = 0f; return; }
        if (t <= 6) { u = (t - 2) / 4f; along = 0.25f + 0.35f * u; outw = 0.3f + 0.6f * u; spin = 0.3f + 0.7f * u; fall = 0.5f * u * u; return; }
        u = FxMath.Clamp01((t - 6) / 8f);
        along = 0.6f + 0.4f * u; outw = 0.9f + 0.7f * u; spin = 1f + 1.2f * u; fall = 0.5f + 0.4f * u;
    }

    // 体を通る輪に沿った斬線: 1 コマで走り、2 コマ残り、細りながら 3 コマで消える
    private static void StreakFrame(Hit h, int t, bool opaque, float cx, float cz, float o, float th, float roll, float unit)
    {
        if (!h.Streak) return;
        bool on = !opaque && t <= 5;
        if (h.Streak.gameObject.activeSelf != on) h.Streak.gameObject.SetActive(on);
        if (!on) return;
        var (sx, sy) = Proj(cx, cz, o, th, h.Bx, h.By);
        h.Streak.localPosition = FxMath.V3((sx - 0.5f) * _frameW, (sy - 0.5f) * _frameH, -0.09f);
        h.Streak.localRotation = FxMath.RotZ(h.Ang * (180f / MathF.PI) + 90f + roll);
        float run = t == 0 ? 0.55f : 1f;
        float fade = t <= 2 ? 1f : 1f - (t - 2) / 3.5f;
        float len = StreakLen * run * unit / StreakAspect, w = (t <= 2 ? 0.12f : 0.12f * fade) * unit;
        h.Core.transform.localScale = FxMath.V3(len, w, 1f);
        h.Glow.transform.localScale = FxMath.V3(len * 1.15f, w * 5f, 1f);
        h.Core.color = FxMath.Rgba(1f, 1f, 1f, fade);
        bool pink = (h.Pc.PlayerId & 1) != 0;
        h.Glow.color = pink ? FxMath.Rgba(1f, 0.37f, 0.66f, 0.95f * fade) : FxMath.Rgba(0.29f, 0.55f, 1f, 0.95f * fade);
    }

    // 本物の服を消し、服の絵を輪に沿う線で 2 片に割る (服を着ていなければ何もしない)
    private static void CutSkin(Hit h)
    {
        var sr = h.Skin;
        if (!sr || !sr.enabled || !sr.sprite) return;
        var sp = sr.sprite;
        var b = sr.bounds;
        var tex = sp.texture;
        var tr = sp.textureRect;
        float nx = MathF.Cos(h.Ang), ny = MathF.Sin(h.Ang);
        Vector2 ctr = FxMath.V2(b.center.x, b.center.y);
        Vector2[] rect = { FxMath.V2(b.min.x, b.min.y), FxMath.V2(b.max.x, b.min.y), FxMath.V2(b.max.x, b.max.y), FxMath.V2(b.min.x, b.max.y) };
        int layer = _root.layer;
        for (int side = 0; side < 2; side++)
        {
            var p = h.Pcs[side];
            var poly = Clip(rect, ctr, nx * p.Sg, ny * p.Sg);
            if (poly.Count < 3) continue;
            float mx = 0f, my = 0f;
            foreach (var q in poly) { mx += q.x; my += q.y; }
            mx /= poly.Count;
            my /= poly.Count;
            var verts = new Vector3[poly.Count];
            var uvs = new Vector2[poly.Count];
            for (int k = 0; k < poly.Count; k++)
            {
                verts[k] = FxMath.V3(poly[k].x - mx, poly[k].y - my, 0f);
                float u = (poly[k].x - b.min.x) / b.size.x, w = (poly[k].y - b.min.y) / b.size.y;
                if (sr.flipX) u = 1f - u;
                uvs[k] = FxMath.V2((tr.x + u * tr.width) / tex.width, (tr.y + w * tr.height) / tex.height);
            }
            var tris = new int[(poly.Count - 2) * 3];
            for (int k = 0; k + 2 < poly.Count; k++) { tris[k * 3] = 0; tris[k * 3 + 1] = k + 2; tris[k * 3 + 2] = k + 1; }
            var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave, vertices = verts, uv = uvs, triangles = tris };
            var mat = new Material(sr.sharedMaterial) { hideFlags = HideFlags.HideAndDontSave, mainTexture = tex };
            var go = new GameObject("SkinPiece") { layer = layer };
            go.transform.SetParent(_root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.sortingOrder = Order + 9;
            p.Go = go;
            p.Tf = go.transform;
            p.Mesh = mesh;
            p.Mat = mat;
            p.Col = sr.color;
            p.Bx = (mx - _camCenter0.x) / PlateWorld;
            p.By = (my - _camCenter0.y) / PlateWorld;
            _pcCount++;
        }
        sr.enabled = false;
    }

    // Blender の正射影のカメラで場の点を絵の 0〜1 へ (道具の proj と同じ)
    private static (float, float) Proj(float cx, float cz, float o, float th, float x, float z)
    {
        float dx = x - cx, dz = z - cz, c = MathF.Cos(th), s = MathF.Sin(th);
        float asp = (float)_h / _w;
        return ((dx * c - dz * s) / o + 0.5f, (dx * s + dz * c) / (o * asp) + 0.5f);
    }

    // 凸の多角形を「中心 c から向き n の側」だけ残して切る
    private static System.Collections.Generic.List<Vector2> Clip(Vector2[] poly, Vector2 c, float nx, float ny)
    {
        var res = new System.Collections.Generic.List<Vector2>(6);
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            float da = (a.x - c.x) * nx + (a.y - c.y) * ny, db = (b.x - c.x) * nx + (b.y - c.y) * ny;
            if (da >= 0f) res.Add(a);
            if ((da >= 0f) != (db >= 0f))
            {
                float t = da / (da - db);
                res.Add(FxMath.V2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t));
            }
        }
        return res;
    }

    private static void PiecesDestroy()
    {
        for (int i = 0; i < _hitCount; i++)
        {
            var h = _hits[i];
            if (h == null) continue;
            if (h.Skin && h.SkinWas) h.Skin.enabled = true;
            foreach (var p in h.Pcs)
            {
                if (p == null) continue;
                if (p.Mesh) UnityEngine.Object.Destroy(p.Mesh);
                if (p.Mat) UnityEngine.Object.Destroy(p.Mat);
            }
            _hits[i] = null;
        }
        _hitCount = _pcCount = 0;
    }
}
