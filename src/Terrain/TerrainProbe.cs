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
                  .Append(" layer=").Append(c.gameObject.layer)
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
        RegisterSprites();
        RegisterShaderInfo();
        RegisterDamage();
        RegisterFindSprite();
        RegisterMapSurvey();
        RegisterNearWall();
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
            var removed = new System.Collections.Generic.List<Vector2>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var c in Physics2D.OverlapCircleAll(center, r, WallMask))
            {
                var e = c ? c.TryCast<EdgeCollider2D>() : null;
                if (!e || e.isTrigger) continue;
                if (!EdgeCutter.Cut(e, center, r, removed)) continue;
                if (e.gameObject.layer == ShadowLayer) shadow++; else ship++;
            }
            double colMs = sw.Elapsed.TotalMilliseconds;
            string visual = DamageMap.Hole(center, r, removed);
            sw.Stop();
            reply($"OK hole ship={ship} shadow={shadow} colliderMs={colMs:0.00} totalMs={sw.Elapsed.TotalMilliseconds:0.00} visual={visual ?? "ok"}");
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

    // 見た目の下調べ: 指定点に重なる描画物 (Renderer の bounds が点を含むもの) を列挙
    internal static void RegisterSprites()
    {
        TestBridge.Register("sprites", "<x> <y> 点に重なる描画物を列挙", (args, reply) =>
        {
            string[] p = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2 || !float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                              || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
            { reply("ERR sprites needs <x> <y>"); return; }

            int n = 0;
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (!r || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                var b = r.bounds;
                if (x < b.min.x || x > b.max.x || y < b.min.y || y > b.max.y) continue;
                n++;
                var sb = new StringBuilder("SPR ").Append(Path(r.transform))
                    .Append(" type=").Append(r.GetIl2CppType().Name)
                    .Append(" z=").Append(TestBridge.F(r.transform.position.z))
                    .Append(" layer=").Append(r.gameObject.layer)
                    .Append(" sort=").Append(r.sortingLayerID).Append('/').Append(r.sortingOrder)
                    .Append(" bounds=").Append(V((Vector2)b.min)).Append('-').Append(V((Vector2)b.max));
                var mat = r.sharedMaterial;
                if (mat) sb.Append(" mat=").Append(mat.name).Append(" shader=").Append(mat.shader ? mat.shader.name : "?");
                var sr = r.TryCast<SpriteRenderer>();
                if (sr && sr.sprite)
                {
                    var s = sr.sprite;
                    var tex = s.texture;
                    sb.Append(" sprite=").Append(s.name).Append(" rect=").Append(s.textureRect.width).Append('x').Append(s.textureRect.height)
                      .Append(" ppu=").Append(s.pixelsPerUnit)
                      .Append(" color=").Append(sr.color.ToString());
                    if (tex) sb.Append(" tex=").Append(tex.name).Append(' ').Append(tex.width).Append('x').Append(tex.height)
                                .Append(' ').Append(tex.format).Append(" readable=").Append(tex.isReadable);
                }
                reply(sb.ToString());
            }
            reply($"OK sprites n={n}");
        });
    }

    internal static void RegisterShaderInfo()
    {
        TestBridge.Register("matinfo", "<GameObject のパス> 描画物のマテリアルとシェーダの中身", (args, reply) =>
        {
            var go = GameObject.Find(args.Trim());
            if (!go) { reply($"ERR matinfo not found: {args}"); return; }
            var r = go.GetComponent<Renderer>();
            if (!r || !r.sharedMaterial) { reply("ERR matinfo no renderer/material"); return; }
            var m = r.sharedMaterial;
            var sh = m.shader;
            reply($"MAT {m.name} queue={m.renderQueue} passes={m.passCount} keywords=[{string.Join(",", m.shaderKeywords)}] shader={sh.name} shaderQueue={sh.renderQueue}");
            int pc = sh.GetPropertyCount();
            for (int i = 0; i < pc; i++)
            {
                string name = sh.GetPropertyName(i);
                var type = sh.GetPropertyType(i);
                string val = type switch
                {
                    UnityEngine.Rendering.ShaderPropertyType.Color => m.GetColor(name).ToString(),
                    UnityEngine.Rendering.ShaderPropertyType.Vector => m.GetVector(name).ToString(),
                    UnityEngine.Rendering.ShaderPropertyType.Float or UnityEngine.Rendering.ShaderPropertyType.Range => TestBridge.F(m.GetFloat(name)),
                    UnityEngine.Rendering.ShaderPropertyType.Texture => m.GetTexture(name) ? m.GetTexture(name).name : "null",
                    _ => "?",
                };
                reply($"PROP {name} {type} = {val}");
            }
            int users = 0;
            foreach (var other in Object.FindObjectsOfType<Renderer>()) if (other && other.sharedMaterial == m) users++;
            reply($"OK matinfo users={users}");
        });
    }

    internal static void RegisterDamage()
    {
        TestBridge.Register("blast", "<x> <y> <r> [dx dy force] 爆発 (ロケットランチャー相当)。向きと力 (0..1) を付けると向きの先へ伸びた涙形に抜ける", (args, reply) =>
        {
            if (!TryParseFloats(args, out var v) || (v.Length != 3 && v.Length != 6)) { reply("ERR blast needs <x> <y> <r> [dx dy force]"); return; }
            var dir = v.Length == 6 ? new Vector2(v[3], v[4]) : Vector2.zero;
            float force = v.Length == 6 ? v[5] : 0f;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string res = TerrainSync.Request(new DamageEvent(DamageKind.Explosion, new Vector2(v[0], v[1]), dir.normalized, v[2], force, (ushort)System.Environment.TickCount));
            reply($"OK blast {res} ms={sw.Elapsed.TotalMilliseconds:0.00}");
        });

        TestBridge.Register("netloop", "[rev] <x y r | h x y dx dy force>... 爆発 / 打撃 (h) を電文に書いて読み直し、受け手の順番待ちを通して適用 (rev = 後ろの連番から届ける)", (args, reply) =>
        {
            var p = new System.Collections.Generic.List<string>(args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries));
            bool rev = p.Count > 0 && p[0] == "rev";
            if (rev) p.RemoveAt(0);
            var events = new System.Collections.Generic.List<DamageEvent>();
            for (int i = 0; i < p.Count;)
            {
                bool blunt = p[i] == "h";
                if (blunt) i++;
                int n = blunt ? 5 : 3;
                if (i + n > p.Count || !TryParseFloats(string.Join(' ', p.GetRange(i, n)), out var v)) { reply("ERR netloop needs [rev] <x y r | h x y dx dy force>..."); return; }
                i += n;
                ushort seed = (ushort)(System.Environment.TickCount + events.Count);
                events.Add(blunt
                    ? new DamageEvent(DamageKind.Blunt, new Vector2(v[0], v[1]), new Vector2(v[2], v[3]).normalized, 0f, v[4], seed)
                    : new DamageEvent(DamageKind.Explosion, new Vector2(v[0], v[1]), Vector2.zero, v[2], 0f, seed));
            }
            if (events.Count == 0) { reply("ERR netloop needs [rev] <x y r | h x y dx dy force>..."); return; }
            reply($"OK netloop {TerrainSync.Loopback(events.ToArray(), rev)}");
        });

        TestBridge.Register("hammer", "<x> <y> <dx> <dy> [force] 打撃 (位置から向きの先の壁を叩く。斜めに振ると斜めに抜ける。力 0..1・既定 0.5)", (args, reply) =>
        {
            if (!TryParseFloats(args, out var v) || (v.Length != 4 && v.Length != 5)) { reply("ERR hammer needs <x> <y> <dx> <dy> [force]"); return; }
            float force = v.Length == 5 ? v[4] : 0.5f;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string res = TerrainSync.Request(new DamageEvent(DamageKind.Blunt, new Vector2(v[0], v[1]), new Vector2(v[2], v[3]), 0f, force, (ushort)System.Environment.TickCount));
            reply($"OK hammer {res} ms={sw.Elapsed.TotalMilliseconds:0.00}");
        });
    }

    internal static void RegisterFindSprite()
    {
        TestBridge.Register("findsprite", "<正規表現> 読み込み済みの Sprite を名前で探す", (args, reply) =>
        {
            var re = new System.Text.RegularExpressions.Regex(args.Trim(), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            int n = 0;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
            {
                var sp = o.TryCast<Sprite>();
                if (!sp || !re.IsMatch(sp.name)) continue;
                if (++n > 40) break;
                reply($"SPRITE {sp.name} rect={sp.textureRect.width}x{sp.textureRect.height} tex={(sp.texture ? sp.texture.name : "?")} ppu={sp.pixelsPerUnit}");
            }
            reply($"OK findsprite n={n}");
        });
    }

    internal static void RegisterMapSurvey()
    {
        TestBridge.Register("mapsurvey", "今のマップの壁・家具・部屋の絵の作りを数える", (_, reply) =>
        {
            var ship = ShipStatus.Instance;
            if (!ship) { reply("ERR mapsurvey no ship"); return; }
            reply($"MAP {ship.name} scale={TestBridge.F(ship.transform.lossyScale.x)}");

            var colCount = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var c in ship.GetComponentsInChildren<Collider2D>(true))
            {
                int layer = c.gameObject.layer;
                if (layer != 9 && layer != 10 && layer != 12) continue;
                string k = $"L{layer} {c.GetIl2CppType().Name}{(c.isTrigger ? " trigger" : "")}";
                colCount[k] = colCount.TryGetValue(k, out int v) ? v + 1 : 1;
            }
            foreach (var kv in colCount) reply($"COLS {kv.Key} = {kv.Value}");

            // Ship 層の (トリガーでない) 当たり判定の持ち主の名前: 親/自分
            var names = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var c in ship.GetComponentsInChildren<Collider2D>(true))
            {
                if (c.gameObject.layer != 9 || c.isTrigger) continue;
                var t = c.transform;
                string k = (t.parent ? t.parent.name + "/" : "") + t.name + ":" + c.GetIl2CppType().Name;
                names[k] = names.TryGetValue(k, out int v) ? v + 1 : 1;
            }
            reply("SHIPNAMES " + string.Join(" | ", names.Keys));

            var shaders = new System.Collections.Generic.Dictionary<string, int>();
            var floors = new System.Collections.Generic.List<string>();
            foreach (var sr in ship.GetComponentsInChildren<SpriteRenderer>(true))
            {
                var m = sr.sharedMaterial;
                string k = m && m.shader ? m.shader.name : "?";
                shaders[k] = shaders.TryGetValue(k, out int v) ? v + 1 : 1;
                var sp = sr.sprite;
                if (sp && k == "Unlit/MaskShader" && floors.Count < 60) floors.Add($"{sp.name}({(int)sp.textureRect.width}x{(int)sp.textureRect.height})");
            }
            foreach (var kv in shaders) reply($"SHADER {kv.Key} = {kv.Value}");
            reply("ROOMSPRITES " + string.Join(" ", floors));
            reply("OK mapsurvey");
        });
    }

    internal static void RegisterNearWall()
    {
        TestBridge.Register("nearwall", "自分から 8 方向に壁を探し、いちばん近い壁の方向と距離を出す", (_, reply) =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp) { reply("ERR nearwall no local player"); return; }
            Vector2 from = lp.GetTruePosition();
            float best = float.MaxValue; Vector2 bestDir = default, bestPt = default; string bestName = "";
            for (int k = 0; k < 8; k++)
            {
                float a = k * Mathf.PI / 4f;
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                foreach (var h in Physics2D.CircleCastAll(from, 0.1f, dir, 4f, 1 << ShipLayer))
                {
                    if (!h.collider || h.collider.isTrigger || h.distance >= best) continue;
                    if (TerrainDamage.IsProtected(h.collider)) continue;
                    best = h.distance; bestDir = dir; bestPt = h.point;
                    bestName = $"{Path(h.collider.transform)}({h.collider.GetIl2CppType().Name})";
                }
            }
            if (best == float.MaxValue) { reply("ERR nearwall none within 4"); return; }
            reply($"OK nearwall from={TestBridge.F(from.x)},{TestBridge.F(from.y)} dir={TestBridge.F(bestDir.x)},{TestBridge.F(bestDir.y)} d={TestBridge.F(best)} at={TestBridge.F(bestPt.x)},{TestBridge.F(bestPt.y)} col={bestName}");
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

    internal static bool TryParseFloats(string args, out float[] v)
    {
        string[] p = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        v = new float[p.Length];
        for (int i = 0; i < p.Length; i++)
            if (!float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return false;
        return p.Length > 0;
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
