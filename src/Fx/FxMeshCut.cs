using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Fx;

// 絵 (Sprite) の網目を半平面で切った Mesh を作る。詰めて置かれたテクスチャでも絵の網目どおりに切れる。
// 面は nx * x + ny * y >= d (x, y は原点 (ox, oy) からのワールドの位置)。頂点は原点からの位置で入る
internal static class FxMeshCut
{
    internal struct Plane
    {
        public float Nx, Ny, D;

        public Plane(float nx, float ny, float d)
        {
            Nx = nx;
            Ny = ny;
            D = d;
        }
    }

    private static readonly List<(float x, float y, float u, float v)> PolyA = new(8), PolyB = new(8);

    // sr の今の絵を切る (flipX/flipY も写す)。何も残らなければ null。color を渡すと頂点の色に入れる
    internal static Mesh Cut(SpriteRenderer sr, float ox, float oy, Plane[] planes, int count, Color32? color = null, string name = "MrpCutPiece")
    {
        var sp = sr.sprite;
        if (!sp) return null;
        var tf = sr.transform;
        var vs = sp.vertices;
        var uv = sp.uv;
        var ts = sp.triangles;
        int n = vs.Length;
        float fx = sr.flipX ? -1f : 1f, fy = sr.flipY ? -1f : 1f;
        var wx = new float[n];
        var wy = new float[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 w = tf.TransformPoint(FxMath.V3(vs[i].x * fx, vs[i].y * fy, 0f));
            wx[i] = w.x - ox;
            wy[i] = w.y - oy;
        }
        var pos = new List<Vector3>();
        var tex = new List<Vector2>();
        var idx = new List<int>();
        for (int t = 0; t + 2 < ts.Length; t += 3)
        {
            var poly = PolyA;
            poly.Clear();
            for (int k = 0; k < 3; k++)
            {
                int i = ts[t + k];
                poly.Add((wx[i], wy[i], uv[i].x, uv[i].y));
            }
            for (int p = 0; p < count && poly.Count >= 3; p++)
            {
                var pl = planes[p];
                var next = poly == PolyA ? PolyB : PolyA;
                next.Clear();
                for (int k = 0; k < poly.Count; k++)
                {
                    var a = poly[k];
                    var b = poly[(k + 1) % poly.Count];
                    float da = a.x * pl.Nx + a.y * pl.Ny - pl.D, db = b.x * pl.Nx + b.y * pl.Ny - pl.D;
                    if (da >= 0f) next.Add(a);
                    if ((da >= 0f) != (db >= 0f))
                    {
                        float f = da / (da - db);
                        next.Add((a.x + (b.x - a.x) * f, a.y + (b.y - a.y) * f, a.u + (b.u - a.u) * f, a.v + (b.v - a.v) * f));
                    }
                }
                poly = next;
            }
            if (poly.Count < 3) continue;
            int b0 = pos.Count;
            foreach (var q in poly)
            {
                pos.Add(FxMath.V3(q.x, q.y, 0f));
                tex.Add(FxMath.V2(q.u, q.v));
            }
            for (int k = 1; k + 1 < poly.Count; k++) { idx.Add(b0); idx.Add(b0 + k); idx.Add(b0 + k + 1); }
        }
        if (idx.Count < 3) return null;
        var mesh = new Mesh { name = name };
        mesh.SetVertices(pos.ToArray());
        mesh.SetUVs(0, tex.ToArray());
        if (color is Color32 c)
        {
            var cs = new Color32[pos.Count];
            for (int i = 0; i < cs.Length; i++) cs[i] = c;
            mesh.SetColors(cs);
        }
        mesh.SetTriangles(idx.ToArray(), 0);
        return mesh;
    }
}
