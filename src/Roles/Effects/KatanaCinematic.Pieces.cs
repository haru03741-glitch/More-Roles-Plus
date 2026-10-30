using System;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 抜刀の斬撃で被害者の服 (スキン) だけが斬り落とされる: 本物の服を消し、服の絵を斬線で 2 片に割って、
// 書き出した片の動き (場の座標の移り・回し・不透明度) で飛ばす。片の位置は生の画面の上の本物の服から始める
internal static unsafe partial class KatanaCinematic
{
    private sealed class Piece
    {
        public GameObject Go;
        public Transform Tf;
        public Mesh Mesh;
        public Material Mat;
        public int Rec;                 // 記録の番号 (82 + j × 4)
        public float Bx, Bz;            // 片の中心の場の座標 (始めのコマ)
        public float X0, Z0, R0;        // 書き出した片の始めの位置と回し
        public Color Col;
    }

    private static readonly Piece[] _pcs = new Piece[MaxPieces];
    private static int _pcCount;
    private static bool _piecesStarted;
    private static Vector3 _camCenter0;

    // 片が見え始めるコマ (書き出した不透明度が 0 より大きくなった所) で 1 回だけ作る
    private static void PiecesFrame(int f)
    {
        if (_vicCount == 0) return;
        if (!_piecesStarted)
        {
            if (U(f, 0) != 0 || F(f, 82 + 3) <= 0f) return;
            _piecesStarted = true;
            PiecesBegin(f);
        }
        float o = F(f, 24), roll = F(f, 25) * (180f / MathF.PI);
        float cx = F(f, 22), cz = F(f, 23);
        for (int i = 0; i < _pcCount; i++)
        {
            var p = _pcs[i];
            float a = F(f, p.Rec + 3);
            if (a <= 0.001f) { if (p.Go.activeSelf) p.Go.SetActive(false); continue; }
            if (!p.Go.activeSelf) p.Go.SetActive(true);
            float bx = p.Bx + F(f, p.Rec) - p.X0, bz = p.Bz + F(f, p.Rec + 1) - p.Z0;
            var (sx, sy) = Proj(cx, cz, o, F(f, 25), bx, bz);
            p.Tf.localPosition = FxMath.V3((sx - 0.5f) * _frameW, (sy - 0.5f) * _frameH, -0.08f);
            p.Tf.localRotation = FxMath.RotZ(-((F(f, p.Rec + 2) - p.R0) - roll));
            float s = _frameW / (PlateWorld * o);
            p.Tf.localScale = FxMath.V3(s, s, 1f);
            var c = p.Col;
            c.a *= a;
            p.Mat.color = c;
        }
    }

    // Blender の正射影のカメラで場の点を絵の 0〜1 へ (道具の proj と同じ)
    private static (float, float) Proj(float cx, float cz, float o, float th, float x, float z)
    {
        float dx = x - cx, dz = z - cz, c = MathF.Cos(th), s = MathF.Sin(th);
        float asp = (float)_h / _w;
        return ((dx * c - dz * s) / o + 0.5f, (dx * s + dz * c) / (o * asp) + 0.5f);
    }

    private static void PiecesBegin(int f)
    {
        _pcCount = 0;
        int layer = _root.layer;
        for (int i = 0; i < _vicCount && _pcCount + 2 <= Math.Min(MaxPieces, _nPcs); i++)
        {
            var v = _vic[i];
            var sr = v.Skin;
            if (!sr || !sr.enabled || !sr.sprite) continue;
            var sp = sr.sprite;
            var b = sr.bounds;
            var tex = sp.texture;
            var tr = sp.textureRect;
            float ang = Head(4 + Math.Min(i, 3)) * (MathF.PI / 180f);
            float tx = MathF.Cos(ang), tz = MathF.Sin(ang), nx = -tz, nz = tx;
            Vector2 ctr = FxMath.V2(b.center.x, b.center.y);
            Vector2[] rect = { FxMath.V2(b.min.x, b.min.y), FxMath.V2(b.max.x, b.min.y), FxMath.V2(b.max.x, b.max.y), FxMath.V2(b.min.x, b.max.y) };
            int jBase = 2 * Math.Min(i, 3);
            for (int side = 0; side < 2; side++)
            {
                float sg = side == 0 ? 1f : -1f;
                var poly = Clip(rect, ctr, nx * sg, nz * sg);
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
                int rec = 82 + (jBase + side) * 4;
                _pcs[_pcCount++] = new Piece
                {
                    Go = go, Tf = go.transform, Mesh = mesh, Mat = mat, Rec = rec, Col = sr.color,
                    Bx = (mx - _camCenter0.x) / PlateWorld, Bz = (my - _camCenter0.y) / PlateWorld,
                    X0 = F(f, rec), Z0 = F(f, rec + 1), R0 = F(f, rec + 2),
                };
            }
            sr.enabled = false;
        }
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
        for (int i = 0; i < _pcCount; i++)
        {
            var p = _pcs[i];
            if (p == null) continue;
            if (p.Mesh) UnityEngine.Object.Destroy(p.Mesh);
            if (p.Mat) UnityEngine.Object.Destroy(p.Mat);
            _pcs[i] = null;
        }
        _pcCount = 0;
        _piecesStarted = false;
    }
}
