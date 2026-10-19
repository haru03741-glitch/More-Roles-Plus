using System;
using System.Collections.Generic;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 仕掛け壁: 後から床に立てる壁。歩き (層 9)・視界 (層 10)・水 (歩ける所の地図) を止め、ハンマーと爆発で物ごと壊れる
// (壊れ方は BreakableProps)。立てるのも地形の出来事 (DamageKind.Wall) として連番で配るので、水と破壊の順番が全員で揃う。
// ホストが置ける形に決め (床の上・端は壁まで・人や扉や端末に掛からない)、全員はその丸めた値だけで立てる
internal static class TrickWall
{
    public const float DefaultLength = 2.4f; // クルー 3 人分ほど
    public const float Thickness = 0.3f;
    private const float Height = 0.85f;      // 見た目の面の高さ (マップの壁と同じ)
    private const float MinLength = 0.6f;
    private const float EndOverlap = 0.1f;   // 端が壁に当たった時、その壁へ食い込ませる長さ (隙間を残さない)
    private const float PlayerClear = 0.3f;  // 人の足元からこれより近くには立てない
    private const float CarveMargin = 0.15f; // 壊れた時に歩ける所へ戻す範囲の余白 (地図は壁を太らせて作るため)
    private const float Ppu = 64f;
    private const float ProbeStep = 0.15f;   // 立てる所を調べる円の間隔 (円の半径 = 厚みの半分より細かく)
    private const float FaceSlope = 0.5f;    // 向きの x がこれ以上 (横から 60° 以内) の壁は手前を向いた面を持つ
    private const float FrontReach = 30f;    // 手前の側の帯の広さ (視界の届く距離より十分広く)
    private const int ShipLayer = 9;
    private const int ShadowLayer = 10;
    private const int FurnitureLayer = 12;
    internal const string Name = "MrpTrickWall";

    private static int _count; // 立てた数 (名前の番号・試合をまたいで増えるだけ)

    // ホスト: 置く依頼を置ける形に決める
    public static bool TryResolve(in DamageEvent e, out ResolvedDamage r, out string why)
    {
        r = default;
        why = null;
        if (!SolidMap.Ensure() || !SolidMap.Valid) { why = "wall no map"; return false; }
        Vector2 t = e.Direction.sqrMagnitude > 1e-6f ? e.Direction.normalized : Vector2.right;
        Vector2 c = e.Position;
        if (SolidMap.Solid(c)) { why = "wall not on floor"; return false; }
        float half = Math.Clamp(e.Size, MinLength, TerrainPermit.MaxWallLength) * 0.5f;
        float plus = Reach(c, t, half), minus = Reach(c, -t, half);
        if (plus + minus < MinLength) { why = "wall too short here"; return false; }
        c += t * ((plus - minus) * 0.5f);
        float len = plus + minus;
        c = TerrainWire.Q(c);
        t = TerrainWire.QNormal(t);
        len = TerrainWire.QSize(len);
        string bad = Blocked(c, t, len);
        if (bad != null) { why = "wall " + bad; return false; }
        r = new ResolvedDamage(DamageKind.Wall, c, Vector2.zero, t, 0f, len, 0, e.Seed);
        return true;
    }

    // 真ん中から dir へ、壁 (当たり判定) か歩けない所に当たるまでの長さ (当たれば少し食い込ませる)
    private static float Reach(Vector2 c, Vector2 dir, float half)
    {
        float end = half;
        foreach (var h in Physics2D.CircleCastAll(c, 0.01f, dir, half, 1 << ShipLayer)) // RaycastAll は Android の本編に無い
        {
            if (!h.collider || h.collider.isTrigger) continue;
            end = Math.Min(end, h.distance + EndOverlap);
        }
        const float step = 0.05f;
        for (float s = step; s < end; s += step)
            if (SolidMap.Solid(c.x + dir.x * s, c.y + dir.y * s)) { end = Math.Min(end, s + EndOverlap); break; }
        return end;
    }

    // 立てる所に掛かる物 (人・扉・通気口・端末・はしご・死体)。無ければ null
    private static string Blocked(Vector2 c, Vector2 t, float len)
    {
        var shape = new RectShape(c, t, len, Thickness);
        var all = PlayerControl.AllPlayerControls;
        for (int i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (!pc) continue;
            var data = pc.Data;
            if (data == null || data.IsDead || data.Disconnected) continue;
            var p = pc.GetTruePosition();
            if (shape.SignedDistance(p.x, p.y) < PlayerClear) return "player in the way";
        }
        // 壁の芯に沿って円を並べて調べる (OverlapBoxAll は Android の本編に無い)。
        // 端は壁へ食い込ませてあるので、その分を除いた所に船の層・家具の層の物があれば立てない
        float inner = Math.Max(0f, len * 0.5f - EndOverlap - 0.05f);
        int steps = (int)MathF.Ceiling(len * 0.5f / ProbeStep);
        for (int i = -steps; i <= steps; i++)
        {
            float s = Math.Clamp(i * ProbeStep, -len * 0.5f, len * 0.5f);
            Vector2 at = c + t * s;
            if (MathF.Abs(s) <= inner)
                foreach (var col in Physics2D.OverlapCircleAll(at, Thickness * 0.5f, (1 << ShipLayer) | (1 << FurnitureLayer)))
                    if (col && !col.isTrigger) return "something in the way";
            foreach (var col in Physics2D.OverlapCircleAll(at, Thickness * 0.5f + 0.05f, ~0))
            {
                if (!col) continue;
                if (col.GetComponentInParent<OpenableDoor>()) return "door in the way";
                if (col.GetComponentInParent<Vent>()) return "vent in the way";
                if (col.GetComponentInParent<global::Console>()) return "console in the way";
                if (col.GetComponentInParent<Ladder>()) return "ladder in the way";
                if (col.GetComponentInParent<DeadBody>()) return "body in the way";
            }
        }
        return null;
    }

    // 全員: 決まった形で立てる
    public static string Place(in ResolvedDamage r)
    {
        var ship = ShipStatus.Instance;
        if (!ship || !GameClock.ShipAlive) return "wall no ship";
        Vector2 c = r.Position, t = r.Direction, n = new(-t.y, t.x);
        float len = r.Size;
        Vector2 ht = t * (len * 0.5f), hn = n * (Thickness * 0.5f);
        // 足元の四角 (左回り)
        var foot = new[] { c - ht - hn, c + ht - hn, c + ht + hn, c - ht + hn };

        string name = $"{Name}{++_count}";
        var root = new GameObject(name);
        root.transform.SetParent(ship.transform, false);
        root.transform.position = FxMath.V3(c.x, c.y, 0f);

        var cols = new List<Collider2D>();
        var body = new GameObject(name + "-body") { layer = ShipLayer };
        body.transform.SetParent(root.transform, false);
        var poly = body.AddComponent<PolygonCollider2D>();
        poly.points = Local(body.transform, foot);
        cols.Add(poly);

        // 視界の線。足元の四角は向こう側からの視界を切る。横に近い壁は手前 (画面の下) を向いた面が見えるので、
        // 手前から見る時は足元の線を無視し (OneWayShadows)、面の上の縁 (足元を面の高さだけ上げた四角) で切る。
        // マップの壁も、手前の面の上の縁と奥の側の足元に視界の線がある
        var loop = new Vector2[5];
        for (int i = 0; i < 4; i++) loop[i] = foot[i];
        loop[4] = loop[0];
        var footLine = Shadow(root.transform, name + "-shadow", loop, cols);
        if (MathF.Abs(t.x) >= FaceSlope)
        {
            for (int i = 0; i < 5; i++) loop[i] += new Vector2(0f, Height);
            var topLine = Shadow(root.transform, name + "-shadowtop", loop, cols);
            // 手前の側 (法線のうち下を向いた側) から見る時は足元の線を、奥の側から見る時は面の上の縁の線を無視する
            // (上の縁の線は奥の床へ張り出すので、奥に立つ人がその中に入ると周りが全部影になる)
            Vector2 d = n.y < 0f ? n : -n;
            OneWay(footLine, Side(root.transform, name + "-front", c, t, d, len));
            OneWay(topLine, Side(root.transform, name + "-back", c, t, -d, len));
        }

        var sr = Art(root.transform, name, foot, c, out var tex, out var sprite);
        float bx0 = Math.Min(Math.Min(foot[0].x, foot[1].x), Math.Min(foot[2].x, foot[3].x));
        float by0 = Math.Min(Math.Min(foot[0].y, foot[1].y), Math.Min(foot[2].y, foot[3].y));
        float bx1 = Math.Max(Math.Max(foot[0].x, foot[1].x), Math.Max(foot[2].x, foot[3].x));
        float by1 = Math.Max(Math.Max(foot[0].y, foot[1].y), Math.Max(foot[2].y, foot[3].y));

        int filled = SolidMap.Fill(new RectShape(c, t, len, Thickness));
        BreakableProps.AddWall(name, sr, new List<SpriteRenderer> { sr }, cols, bx0, by0, bx1, by1,
            new RectShape(c, t, len + CarveMargin * 2f, Thickness + CarveMargin * 2f), tex, sprite);
        return $"wall placed {name} at=({c.x:0.00},{c.y:0.00}) dir=({t.x:0.00},{t.y:0.00}) len={len:0.00} cells={filled}";
    }

    // 壁の片側の広い帯 (視点がこの中かを OneWayShadows が見る)
    private static PolygonCollider2D Side(Transform root, string name, Vector2 c, Vector2 t, Vector2 d, float len)
    {
        Vector2 far = t * (len * 0.5f + FrontReach), near = c + d * (Thickness * 0.5f);
        var go = new GameObject(name) { layer = 2 };
        go.transform.SetParent(root, false);
        var area = go.AddComponent<PolygonCollider2D>();
        area.isTrigger = true;
        area.points = Local(go.transform, new[] { near - far, near + far, near + far + d * FrontReach, near - far + d * FrontReach });
        return area;
    }

    private static void OneWay(EdgeCollider2D line, Collider2D viewerSide) =>
        line.gameObject.AddComponent<OneWayShadows>().RoomCollider = viewerSide;

    private static EdgeCollider2D Shadow(Transform root, string name, Vector2[] world, List<Collider2D> cols)
    {
        var go = new GameObject(name) { layer = ShadowLayer };
        go.transform.SetParent(root, false);
        var edge = go.AddComponent<EdgeCollider2D>();
        edge.points = Local(go.transform, world);
        cols.Add(edge);
        return edge;
    }

    // ワールドの点の並び → その物の中の点 (船が左右反転していても形はワールドのまま)
    private static Vector2[] Local(Transform tr, Vector2[] world)
    {
        var o = new Vector2[world.Length];
        for (int i = 0; i < world.Length; i++) o[i] = tr.InverseTransformPoint(FxMath.V3(world[i].x, world[i].y, 0f));
        return o;
    }

    // 見た目: 足元の四角を面の高さだけ立ち上げた箱。上面は明るく、手前を向いた面は暗く、輪郭は太く濃く (本編の壁の描き方)
    private static readonly byte[] TopColor = { 150, 160, 176 };
    private static readonly byte[] SideColor = { 98, 106, 124 };
    private static readonly byte[] SideDark = { 74, 80, 96 };
    private static readonly byte[] LineColor = { 30, 32, 42 };

    private static unsafe SpriteRenderer Art(Transform root, string name, Vector2[] foot, Vector2 c, out Texture2D tex, out Sprite sprite)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var p in foot) { x0 = Math.Min(x0, p.x); y0 = Math.Min(y0, p.y); x1 = Math.Max(x1, p.x); y1 = Math.Max(y1, p.y); }
        y1 += Height;
        const float pad = 2f / Ppu;
        x0 -= pad; y0 -= pad; x1 += pad; y1 += pad;
        int w = (int)MathF.Ceiling((x1 - x0) * Ppu), h = (int)MathF.Ceiling((y1 - y0) * Ppu);

        // 画素ごとの面: 0 = 無し / 1 = 上面 / 2.. = 手前を向いた側面 (足元の辺の番号 + 2)
        // 画素の回りは Unity の算術を通さず float だけで回す (Vector2 の生成・演算子は 1 回ごとに interop)
        var face = new byte[w * h];
        var fx = new float[4];
        var fy = new float[4];
        for (int i = 0; i < 4; i++) { fx[i] = foot[i].x; fy[i] = foot[i].y; }
        // 側面: 外向きの法線が下 (手前) を向いた辺 (x が増える向きの辺) だけが見える。辺 → 立ち上げた四角
        var sx = new float[16];
        var sy = new float[16];
        var sideOn = new bool[4];
        for (int e = 0; e < 4; e++)
        {
            int j = (e + 1) & 3;
            sideOn[e] = fx[j] - fx[e] > 0f;
            sx[e * 4] = fx[e]; sy[e * 4] = fy[e];
            sx[e * 4 + 1] = fx[j]; sy[e * 4 + 1] = fy[j];
            sx[e * 4 + 2] = fx[j]; sy[e * 4 + 2] = fy[j] + Height;
            sx[e * 4 + 3] = fx[e]; sy[e * 4 + 3] = fy[e] + Height;
        }
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float qx = x0 + (x + 0.5f) / Ppu, qy = y0 + (y + 0.5f) / Ppu;
            byte f = 0;
            if (InQuad(qx, qy - Height, fx, fy, 0)) f = 1;
            else
                for (int e = 0; e < 4 && f == 0; e++)
                    if (sideOn[e] && InQuad(qx, qy, sx, sy, e * 4)) f = (byte)(e + 2);
            face[y * w + x] = f;
        }

        var px = new byte[w * h * 4];
        const int line = 2; // 輪郭の太さ (画素)
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int k = y * w + x;
            byte f = face[k];
            if (f == 0) continue;
            bool outline = false, seam = false;
            for (int dy = -line; dy <= line && !outline; dy++)
            for (int dx = -line; dx <= line; dx++)
            {
                int xx = x + dx, yy = y + dy;
                byte g = xx < 0 || yy < 0 || xx >= w || yy >= h ? (byte)0 : face[yy * w + xx];
                if (g == 0) { outline = true; break; }
                if (g != f && Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1) seam = true;
            }
            byte[] col = outline || seam ? LineColor : f == 1 ? TopColor : (f & 1) == 0 ? SideColor : SideDark;
            int i = k * 4;
            px[i] = col[0]; px[i + 1] = col[1]; px[i + 2] = col[2]; px[i + 3] = 255;
        }

        tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name };
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, false); // 壊れた時に欠片を切り出すので読めるまま
        sprite = Sprite.Create(tex, new Rect(0f, 0f, w, h), Vector2.zero, Ppu, 0, SpriteMeshType.FullRect);
        sprite.name = name;

        var go = new GameObject(name + "-art"); // 絵は層 0 (影のカメラは層 9 の物を自前の描き方で描き直す)
        go.transform.SetParent(root, false);
        // 人と同じ奥行きの決め方 (足元の y)。船が反転していても絵は反転させない
        go.transform.position = FxMath.V3(x0, y0, c.y / 1000f);
        Vector3 ls = root.lossyScale;
        go.transform.localScale = FxMath.V3(1f / ls.x, 1f / ls.y, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        return sr;
    }

    // 点が四角 (xs/ys の o から 4 点) の中か
    private static bool InQuad(float px, float py, float[] xs, float[] ys, int o)
    {
        bool inside = false;
        for (int i = 0, j = 3; i < 4; j = i++)
        {
            float ax = xs[o + i], ay = ys[o + i], bx = xs[o + j], by = ys[o + j];
            if ((ay > py) != (by > py) && px < (bx - ax) * (py - ay) / (by - ay) + ax) inside = !inside;
        }
        return inside;
    }

    internal static void Register()
    {
        TestBridge.Register("wall", "<x> <y> <角度°> [長さ] 仕掛け壁を立てる (ホストか一人の時・0° = 横・90° = 縦)", (args, reply) =>
        {
            var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (a.Length < 3 || !float.TryParse(a[0], System.Globalization.NumberStyles.Float, inv, out float x)
                || !float.TryParse(a[1], System.Globalization.NumberStyles.Float, inv, out float y)
                || !float.TryParse(a[2], System.Globalization.NumberStyles.Float, inv, out float deg))
            { reply("ERR wall <x> <y> <deg> [len]"); return; }
            float len = a.Length >= 4 && float.TryParse(a[3], System.Globalization.NumberStyles.Float, inv, out float l) ? l : DefaultLength;
            float rad = deg * (MathF.PI / 180f);
            var res = TerrainApi.WorldWall(new Vector2(x, y), new Vector2(MathF.Cos(rad), MathF.Sin(rad)), len);
            reply($"{(res.Ok ? "OK" : "ERR")} wall {res.Why}");
        });
    }
}
