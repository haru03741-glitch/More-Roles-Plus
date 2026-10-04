using System.Globalization;
using System.Text;
using MoreRolesPlus.Bridge;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 地形破壊の下調べ用ブリッジコマンド
internal static class TerrainProbe
{
    public static void Register()
    {
        TestBridge.Register("walls", "<x> <y> <r> 円に掛かるコライダーを列挙", (args, reply) =>
        {
            if (!TryParse3(args, out float x, out float y, out float r)) { reply("ERR walls needs <x> <y> <r>"); return; }

            var hits = Physics2D.OverlapCircleAll(new Vector2(x, y), r, ~0);
            foreach (var c in hits)
            {
                if (!c) continue;
                var sb = new StringBuilder();
                sb.Append("COL ").Append(Path(c.transform))
                  .Append(" type=").Append(c.GetIl2CppType().Name)
                  .Append(" layer=").Append(c.gameObject.layer).Append('(').Append(LayerMask.LayerToName(c.gameObject.layer)).Append(')')
                  .Append(" trigger=").Append(c.isTrigger)
                  .Append(" offset=").Append(V(c.offset));

                var edge = c.TryCast<EdgeCollider2D>();
                if (edge) sb.Append(" points=").Append(edge.pointCount);
                var poly = c.TryCast<PolygonCollider2D>();
                if (poly) sb.Append(" paths=").Append(poly.pathCount).Append(" total=").Append(poly.GetTotalPointCount());
                var box = c.TryCast<BoxCollider2D>();
                if (box) sb.Append(" size=").Append(V(box.size));

                var t = c.transform;
                sb.Append(" pos=").Append(V(t.position)).Append(" scale=").Append(V(t.lossyScale)).Append(" rot=").Append(TestBridge.F(t.eulerAngles.z));
                reply(sb.ToString());
            }
            reply($"OK walls n={hits.Length}");
        });

        RegisterCutting();
        RegisterSweep();
    }

    private static void RegisterCutting()
    {
        TestBridge.Register("edges", "<x> <y> <r> 円に掛かる壁 (Ship/Shadow 層の EdgeCollider2D) の頂点をワールド座標で出す", (args, reply) =>
        {
            if (!TryParse3(args, out float x, out float y, out float r)) { reply("ERR edges needs <x> <y> <r>"); return; }
            int n = 0;
            foreach (var c in Physics2D.OverlapCircleAll(new Vector2(x, y), r, WallMask))
            {
                var e = c ? c.TryCast<EdgeCollider2D>() : null;
                if (!e) continue;
                n++;
                var sb = new StringBuilder("EDGE ").Append(Path(e.transform)).Append(" layer=").Append(e.gameObject.layer).Append(' ');
                foreach (var p in e.points) sb.Append(V((Vector2)e.transform.TransformPoint(p + e.offset)));
                reply(sb.ToString());
            }
            reply($"OK edges n={n}");
        });

        TestBridge.Register("hole", "<x> <y> <r> 壁 (Ship/Shadow 層) を円で切り取る", (args, reply) =>
        {
            if (!TryParse3(args, out float x, out float y, out float r)) { reply("ERR hole needs <x> <y> <r>"); return; }
            var center = new Vector2(x, y);
            int ship = 0, shadow = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var c in Physics2D.OverlapCircleAll(center, r, WallMask))
            {
                var e = c ? c.TryCast<EdgeCollider2D>() : null;
                if (!e || e.isTrigger) continue;
                if (!EdgeCutter.Cut(e, center, r)) continue;
                if (e.gameObject.layer == ShadowLayer) shadow++; else ship++;
            }
            sw.Stop();
            reply($"OK hole ship={ship} shadow={shadow} ms={sw.Elapsed.TotalMilliseconds:0.00}");
        });
    }

    // 当たり判定の確認用: 2 点間に半径 0.2 の円を滑らせ、最初に当たる壁を出す (プレイヤーの体格相当)
    internal static void RegisterSweep()
    {
        TestBridge.Register("sweep", "<x1> <y1> <x2> <y2> 2 点間を円で掃いて最初に当たる壁を出す", (args, reply) =>
        {
            string[] p = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            var v = new float[4];
            if (p.Length < 4) { reply("ERR sweep needs <x1> <y1> <x2> <y2>"); return; }
            for (int i = 0; i < 4; i++)
                if (!float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) { reply("ERR sweep bad number"); return; }

            var from = new Vector2(v[0], v[1]);
            var dir = new Vector2(v[2], v[3]) - from;
            foreach (int layer in new[] { ShipLayer, ShadowLayer })
            {
                var hits = Physics2D.CircleCastAll(from, 0.2f, dir.normalized, dir.magnitude, 1 << layer);
                string what = "clear";
                float best = float.MaxValue;
                foreach (var h in hits)
                {
                    if (!h.collider || h.collider.isTrigger || h.distance >= best) continue;
                    best = h.distance;
                    what = $"hit {Path(h.collider.transform)} at {V(h.point)} d={TestBridge.F(h.distance)}";
                }
                reply($"SWEEP layer={layer} {what}");
            }
            reply("OK sweep");
        });
    }

    private const int ShipLayer = 9;
    private const int ShadowLayer = 10;
    private const int WallMask = (1 << ShipLayer) | (1 << ShadowLayer);

    internal static bool TryParse3(string args, out float a, out float b, out float c)
    {
        a = b = c = 0;
        string[] p = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        return p.Length >= 3
            && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a)
            && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b)
            && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out c);
    }

    internal static string V(Vector2 v) => $"({TestBridge.F(v.x)},{TestBridge.F(v.y)})";
    internal static string V(Vector3 v) => $"({TestBridge.F(v.x)},{TestBridge.F(v.y)},{TestBridge.F(v.z)})";

    internal static string Path(Transform t)
    {
        var sb = new StringBuilder(t.name);
        for (var p = t.parent; p; p = p.parent) sb.Insert(0, p.name + "/");
        return sb.ToString();
    }
}
