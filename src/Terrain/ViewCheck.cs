using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MoreRolesPlus.Bridge;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 確認用 (ブリッジ): 水の見え方の食い違いを画素で数える。
// - 影と視界: 同じカメラで、画面全体を視界の描き方にした絵 (影の板を隠す・家具の絵が水より手前) と、画面全体を影の描き方にした絵
//   (視界の形を隠す・水の写しが一番手前で、床マスクと手前の家具の型で抜く) を撮り比べる。どちらかだけに水がある画素 = 片方の抜き方の漏れ。
// - 重ね塗り: 水の層 (床の水・壁の面・家具の水面の板・影の写し・滝) を半透明の単色で描かせ、水を描かない絵との差から
//   1 画素に何枚重なったかを読む (1 枚 = 0.5・2 枚 = 0.75)。2 枚以上 = 二重にかぶっている。
// 主カメラと影のカメラをその場で描き直して撮るので、1 回のコマンドで終わる (毎フレームの道には何も足さない)。
// viewsweep = 今のマップを画面 1 枚ずつの格子で回って撮る (ブリッジの見張りの中だけで進む)
internal static class ViewCheck
{
    private static readonly int FlatId = Shader.PropertyToID("_MrpDebugFlat"), ShadowOnId = Shader.PropertyToID("_MrpShadowOn");
    private const int Width = 960;
    private const int Shown = 12; // 種類ごとに返すまとまりの数

    private enum Kind : byte { None, ShadowOnly, VisionOnly, OverlapVision, OverlapShadow }

    internal static void Register()
    {
        TestBridge.Register("viewcheck", "[最小の画素数=40] [crew] 水の見え方の食い違い: 影の中だけ水がある所 (shadowOnly)・視界の中だけ水がある所 (visionOnly)・水が 2 枚以上重なった所 (overlapV/overlapS) をまとまりごとに世界の座標で出し、Screens/viewcheck_<時刻>.ppm へ (赤 = 影だけ・青 = 視界だけ・黄 = 視界で重なり・橙 = 影で重なり)。クルーの周りは数えない (crew = 数える・クルーの水面の板は床の水に重なるので、重なりの数え方の確かめに使う)", (args, reply) =>
        {
            var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int minPx = a.Length > 0 && int.TryParse(a[0], out int m) ? Math.Max(1, m) : 40;
            bool crew = Array.IndexOf(a, "crew") >= 0;
            string path = Path.Combine(TestBridge.ScreensDir, $"viewcheck_{DateTime.Now:yyyyMMdd_HHmmss}.ppm");
            try { Run(minPx, crew, reply, path, true); }
            catch (Exception e) { Shader.SetGlobalFloat(FlatId, 0f); reply($"ERR viewcheck {e.Message}"); }
        });

        TestBridge.Register("viewsweep", "[間隔=7] [最小の画素数=60] | stop  今のマップを画面 1 枚ずつの格子で回って viewcheck を撮り、Screens/viewsweep_<時刻>/ へ (見つかった地点の絵 p<番号>.ppm と一覧 list.txt)。水は先に入れておく (water flood <深さ>)。フリープレイだけ。終わると VIEWSWEEP done をログへ", (args, reply) =>
        {
            var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a.Length > 0 && a[0] == "stop") { _sweep = null; reply("OK viewsweep stop"); return; }
            if (AmongUsClient.Instance.NetworkMode != NetworkModes.FreePlay || !PlayerControl.LocalPlayer) { reply("ERR viewsweep freeplay only"); return; }
            if (WaterSim.W == 0) { reply("ERR viewsweep no water grid (water flood first)"); return; }
            var inv = CultureInfo.InvariantCulture;
            float step = a.Length > 0 && float.TryParse(a[0], NumberStyles.Float, inv, out float st) ? Math.Max(2f, st) : 7f;
            int minPx = a.Length > 1 && int.TryParse(a[1], out int mp) ? Math.Max(1, mp) : 60;
            var pts = SweepPoints(step, step * 0.6f);
            string dir = Path.Combine(TestBridge.ScreensDir, $"viewsweep_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(dir);
            _sweep = new Sweep { Points = pts, MinPx = minPx, Dir = dir };
            reply($"OK viewsweep points={pts.Count} step={F(step)} minPx={minPx} -> {dir}");
        });
    }

    // 見つかったまとまりの数を返す。always = 見つからなくても絵を書く
    private static int Run(int minPx, bool crew, Action<string> reply, string path, bool always)
    {
        var main = Camera.main;
        var collab = UnityEngine.Object.FindObjectOfType<ShadowCollab>();
        var shadowCam = collab ? collab.ShadowCamera : null;
        var lightGo = GameObject.Find("LightChild");
        var light = lightGo ? lightGo.GetComponent<MeshRenderer>() : null;
        var quadGo = GameObject.Find("Main Camera/ShadowQuad");
        var quad = quadGo ? quadGo.GetComponent<MeshRenderer>() : null;
        if (!main || !shadowCam || !light || !quad) { reply($"ERR viewcheck main={(bool)main} shadow={(bool)shadowCam} light={(bool)light} quad={(bool)quad}"); return -1; }

        int w = Width, h = Math.Max(1, (int)MathF.Round(Width * (float)Screen.height / Screen.width));
        var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        bool lightWas = light.enabled, quadWas = quad.enabled;
        Color32[] v0, v1, v2, v3, s1, s2, s3;
        try
        {
            shadowCam.Render();
            v0 = Grab(main, rt, tex, 0f);
            // 影の板を隠して影の写しを止めると、画面全体が視界の描き方になる
            quad.enabled = false;
            Shader.SetGlobalFloat(ShadowOnId, 0f);
            v1 = Grab(main, rt, tex, 1f);
            v2 = Grab(main, rt, tex, 2f);
            v3 = Grab(main, rt, tex, 3f);
            // 視界の形 (影の描き先のアルファを抜く網目) を隠すと、画面全体が影になる
            quad.enabled = quadWas;
            Shader.SetGlobalFloat(ShadowOnId, 1f);
            light.enabled = false;
            Shader.SetGlobalFloat(FlatId, 0f);
            shadowCam.Render();
            s1 = Grab(main, rt, tex, 1f);
            s2 = Grab(main, rt, tex, 2f);
            s3 = Grab(main, rt, tex, 3f);
        }
        finally
        {
            light.enabled = lightWas;
            quad.enabled = quadWas;
            Shader.SetGlobalFloat(ShadowOnId, 1f);
            Shader.SetGlobalFloat(FlatId, 0f);
            shadowCam.Render();
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.Destroy(tex);
        }

        int n = w * h;
        var skip = crew ? new bool[w * h] : CrewMask(main, w, h);
        var kind = new Kind[n];
        int[] totals = new int[5];
        for (int i = 0; i < n; i++)
        {
            if (skip[i]) continue;
            bool vc = Flat(v1[i]), sc = Flat(s1[i]);
            Kind k = Kind.None;
            if (sc && !vc) k = Kind.ShadowOnly;
            else if (vc && !sc) k = Kind.VisionOnly;
            else if (Layers(v2[i], v3[i]) > 0.66f) k = Kind.OverlapVision;
            else if (Layers(s2[i], s3[i]) > 0.66f) k = Kind.OverlapShadow;
            kind[i] = k;
            totals[(int)k]++;
        }

        var groups = Groups(kind, w, h, minPx);
        groups.Sort((a, b) => b.Px.CompareTo(a.Px));
        var shownPer = new int[5];
        foreach (var g in groups)
        {
            if (shownPer[(int)g.Kind]++ >= Shown) continue;
            Vector3 c = main.ViewportToWorldPoint(new Vector3((g.X0 + g.X1 + 1) * 0.5f / w, (g.Y0 + g.Y1 + 1) * 0.5f / h, 0f));
            Vector3 a = main.ViewportToWorldPoint(new Vector3(g.X0 / (float)w, g.Y0 / (float)h, 0f));
            Vector3 b = main.ViewportToWorldPoint(new Vector3((g.X1 + 1) / (float)w, (g.Y1 + 1) / (float)h, 0f));
            reply($"VC {Name(g.Kind)} px={g.Px} at={F(c.x)},{F(c.y)} box={F(a.x)},{F(a.y)}..{F(b.x)},{F(b.y)}");
        }

        if (always || groups.Count > 0) WritePpm(path, v0, kind, w, h);
        reply($"OK viewcheck shadowOnly={totals[1]} visionOnly={totals[2]} overlapV={totals[3]} overlapS={totals[4]} groups={groups.Count} minPx={minPx} {w}x{h} -> {path}");
        return groups.Count;
    }

    private sealed class Sweep
    {
        public List<Vector2> Points;
        public int Index, Wait, MinPx, Found, Spots;
        public bool Moved;
        public string Dir;
        public readonly List<string> Lines = new();
    }
    private static Sweep _sweep;
    private const int SettleFrames = 45; // 移ってからカメラが追いつき、水の絵のタイルが描き直されるまで

    // 格子の点のうち、近くに水の升が開いている所 (そこへ移る)
    private static List<Vector2> SweepPoints(float sx, float sy)
    {
        var pts = new List<Vector2>();
        var org = WaterSim.Origin;
        float cell = WaterSim.Cell, wx = WaterSim.W * cell, wy = WaterSim.H * cell;
        int reach = (int)MathF.Ceiling(2f / cell);
        for (float y = org.y + sy * 0.5f; y < org.y + wy; y += sy)
            for (float x = org.x + sx * 0.5f; x < org.x + wx; x += sx)
            {
                int cx = (int)((x - org.x) / cell), cy = (int)((y - org.y) / cell);
                int best = -1, bestD = int.MaxValue;
                for (int dy = -reach; dy <= reach; dy++)
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        int px = cx + dx, py = cy + dy;
                        if (px < 1 || py < 1 || px >= WaterSim.W - 1 || py >= WaterSim.H - 1) continue;
                        int k = py * WaterSim.W + px, d = dx * dx + dy * dy;
                        if (d < bestD && WaterSim.Open(k)) { bestD = d; best = k; }
                    }
                if (best < 0) continue;
                pts.Add(new Vector2(org.x + (best % WaterSim.W + 0.5f) * cell, org.y + (best / WaterSim.W + 0.5f) * cell));
            }
        return pts;
    }

    // ブリッジの見張りから毎フレーム (viewsweep の間だけ働く)
    internal static void Tick()
    {
        var s = _sweep;
        if (s == null) return;
        if (s.Wait > 0) { s.Wait--; return; }
        var lp = PlayerControl.LocalPlayer;
        if (!lp || s.Index >= s.Points.Count)
        {
            _sweep = null;
            string list = Path.Combine(s.Dir, "list.txt");
            File.WriteAllLines(list, s.Lines);
            // wait marker は BepInEx のログを見る
            Plugin.Logger.LogInfo($"VIEWSWEEP done points={s.Points.Count} spots={s.Spots} groups={s.Found} -> {list}");
            return;
        }
        var p = s.Points[s.Index];
        if (!s.Moved)
        {
            var nt = lp.NetTransform;
            nt.SnapTo(p, (ushort)(nt.lastSequenceId + 328));
            s.Moved = true;
            s.Wait = SettleFrames;
            return;
        }
        int idx = s.Index;
        string path = Path.Combine(s.Dir, $"p{idx:000}.ppm");
        var lines = new List<string>();
        int n;
        try { n = Run(s.MinPx, false, lines.Add, path, false); }
        catch (Exception e) { Shader.SetGlobalFloat(FlatId, 0f); n = -1; lines.Add($"ERR {e.Message}"); }
        if (n != 0)
        {
            s.Spots++;
            s.Found += Math.Max(0, n);
            s.Lines.Add($"P{idx:000} at={F(p.x)},{F(p.y)} image={(n > 0 ? Path.GetFileName(path) : "-")}");
            foreach (var l in lines) s.Lines.Add("  " + l);
        }
        s.Index++;
        s.Moved = false;
    }

    private static Color32[] Grab(Camera cam, RenderTexture rt, Texture2D tex, float flat)
    {
        Shader.SetGlobalFloat(FlatId, flat);
        var prevTarget = cam.targetTexture;
        var prevActive = RenderTexture.active;
        cam.targetTexture = rt;
        cam.Render();
        cam.targetTexture = prevTarget;
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
        RenderTexture.active = prevActive;
        var src = tex.GetPixels32();
        var px = new Color32[src.Length];
        for (int i = 0; i < px.Length; i++) px[i] = src[i];
        return px;
    }

    // 確認用の単色 (マゼンタ) で塗られた画素
    private static bool Flat(Color32 c) => c.r > 200 && c.g < 70 && c.b > 200;

    // 半透明の単色 (0.5) を重ねた画素 o と、水を描かない画素 b から、単色がどれだけ混ざったか (1 枚 = 0.5・2 枚 = 0.75・3 枚 = 0.875)
    private static float Layers(Color32 o, Color32 b)
    {
        float br = b.r / 255f, bg = b.g / 255f, bb = b.b / 255f;
        float mr = 1f - br, mg = -bg, mb = 1f - bb;
        float len = mr * mr + mg * mg + mb * mb;
        if (len < 0.15f) return 0f; // 下の絵がもともと単色に近い
        float dr = o.r / 255f - br, dg = o.g / 255f - bg, db = o.b / 255f - bb;
        return (dr * mr + dg * mg + db * mb) / len;
    }

    // クルー (体・名前・水面の輪) の周りは、視界では絵が水より手前・影では見えないので数えない
    private static bool[] CrewMask(Camera cam, int w, int h)
    {
        var skip = new bool[w * h];
        var all = PlayerControl.AllPlayerControls;
        for (int i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (!pc) continue;
            Vector3 p = pc.transform.position;
            Vector3 a = cam.WorldToViewportPoint(new Vector3(p.x - 0.6f, p.y - 0.6f, 0f));
            Vector3 b = cam.WorldToViewportPoint(new Vector3(p.x + 0.6f, p.y + 1.1f, 0f));
            int x0 = Math.Max(0, (int)(a.x * w)), x1 = Math.Min(w - 1, (int)(b.x * w));
            int y0 = Math.Max(0, (int)(a.y * h)), y1 = Math.Min(h - 1, (int)(b.y * h));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++) skip[y * w + x] = true;
        }
        return skip;
    }

    private struct Group { public Kind Kind; public int Px, X0, Y0, X1, Y1; }

    // 同じ種類の画素の 4 近傍のまとまり (minPx 画素以上だけ)
    private static List<Group> Groups(Kind[] kind, int w, int h, int minPx)
    {
        var seen = new bool[kind.Length];
        var stack = new Stack<int>();
        var list = new List<Group>();
        for (int s = 0; s < kind.Length; s++)
        {
            if (seen[s] || kind[s] == Kind.None) continue;
            var k = kind[s];
            var g = new Group { Kind = k, X0 = w, Y0 = h, X1 = -1, Y1 = -1 };
            seen[s] = true;
            stack.Push(s);
            while (stack.Count > 0)
            {
                int i = stack.Pop(), x = i % w, y = i / w;
                g.Px++;
                if (x < g.X0) g.X0 = x;
                if (x > g.X1) g.X1 = x;
                if (y < g.Y0) g.Y0 = y;
                if (y > g.Y1) g.Y1 = y;
                if (x > 0) Visit(i - 1);
                if (x < w - 1) Visit(i + 1);
                if (y > 0) Visit(i - w);
                if (y < h - 1) Visit(i + w);
            }
            if (g.Px >= minPx) list.Add(g);

            void Visit(int j)
            {
                if (seen[j] || kind[j] != k) return;
                seen[j] = true;
                stack.Push(j);
            }
        }
        return list;
    }

    // 普段の絵を半分の明るさにして、食い違いの画素を色で重ねる (上から下の行の順・P6)
    private static void WritePpm(string path, Color32[] baseImg, Kind[] kind, int w, int h)
    {
        var head = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        var body = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (h - 1 - y) * w + x, o = (y * w + x) * 3;
                var c = baseImg[i];
                byte r = (byte)(c.r / 2), g = (byte)(c.g / 2), b = (byte)(c.b / 2);
                switch (kind[i])
                {
                    case Kind.ShadowOnly: r = 255; g = 40; b = 40; break;
                    case Kind.VisionOnly: r = 40; g = 120; b = 255; break;
                    case Kind.OverlapVision: r = 255; g = 230; b = 0; break;
                    case Kind.OverlapShadow: r = 255; g = 140; b = 0; break;
                }
                body[o] = r; body[o + 1] = g; body[o + 2] = b;
            }
        using var fs = File.Create(path);
        fs.Write(head, 0, head.Length);
        fs.Write(body, 0, body.Length);
    }

    private static string Name(Kind k) => k switch
    {
        Kind.ShadowOnly => "shadowOnly",
        Kind.VisionOnly => "visionOnly",
        Kind.OverlapVision => "overlapV",
        _ => "overlapS",
    };

    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
