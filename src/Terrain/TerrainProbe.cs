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
        BreakNoise.Register();
        DustCloud.Register();
        WaterLeak.Register();
        WaterSim.Register();
        PropSim.Register();
        BreakableProps.Register();
        FurnitureLift.Register();
        RegisterMapSurvey();
        RegisterNearWall();
    }

    private static void RegisterCutting()
    {
        TestBridge.Register("edges", "<x> <y> <r> 円に掛かる壁 (Ship/Shadow 層の EdgeCollider2D) の頂点をワールド座標で出す。同じ層と家具の層 (12) の PolygonCollider2D は POLY で", (args, reply) =>
        {
            if (!TryParse3(args, out float x, out float y, out float r)) { reply("ERR edges needs <x> <y> <r>"); return; }
            int n = 0;
            foreach (var c in Physics2D.OverlapCircleAll(new Vector2(x, y), r, WallMask | (1 << 12)))
            {
                var pc = c ? c.TryCast<PolygonCollider2D>() : null;
                if (pc)
                {
                    n++;
                    for (int k = 0; k < pc.pathCount; k++)
                    {
                        var pb = new StringBuilder("POLY ").Append(Path(pc.transform)).Append(" layer=").Append(pc.gameObject.layer).Append(" path=").Append(k).Append(' ');
                        foreach (var p in pc.GetPath(k)) pb.Append(V((Vector2)pc.transform.TransformPoint(p + pc.offset)));
                        reply(pb.ToString());
                    }
                    continue;
                }
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

    // テスト用の種 (null = 時刻から)。固定すると端末をまたいで同じ割れ方になる (PC と Android の数値の突き合わせ)。
    // 固定した時はコマンド 1 回ごとに 1 進む (同じ順に送れば同じ列)
    private static int? _fixedSeed;
    private static ushort NextSeed(int k = 0) => (ushort)((_fixedSeed.HasValue ? _fixedSeed++ : System.Environment.TickCount) + k);

    internal static void RegisterDamage()
    {
        TestBridge.Register("stray", "[0|1] 止まった瓦礫のうち、部屋の範囲の外で穴の外 (船体の隙間) に止まった数を数える (Polus/Fungle は屋外も外に数える)", (args, reply) =>
        {
            string a = args.Trim();
            if (a == "1") TerrainFx.StrayProbe = (x, y) =>
            {
                var p = new Vector2(x, y);
                var ship = ShipStatus.Instance;
                if (!ship) return false;
                foreach (var r in ship.AllRooms)
                    if (r && r.roomArea && r.roomArea.OverlapPoint(p)) return false;
                return DamageMap.HoleAt(p) < 0.3f;
            };
            else if (a == "0") TerrainFx.StrayProbe = null;
            if (a.Length > 0) TerrainFx.Settled = TerrainFx.Stray = 0;
            reply($"OK stray {(TerrainFx.StrayProbe != null ? 1 : 0)} settled={TerrainFx.Settled} stray={TerrainFx.Stray}");
        });

        TestBridge.Register("seed", "<n|off> blast / hammer / netloop の種を固定する (off = 時刻から)", (args, reply) =>
        {
            string a = args.Trim();
            _fixedSeed = int.TryParse(a, out int n) ? n : null;
            reply($"OK seed {(_fixedSeed?.ToString() ?? "off")}");
        });

        TestBridge.Register("blast", "<x> <y> <r> [dx dy force] 爆発 (ロケットランチャー相当)。向きと力 (0..1) を付けると向きの先へ伸びた涙形に抜ける", (args, reply) =>
        {
            if (!TryParseFloats(args, out var v) || (v.Length != 3 && v.Length != 6)) { reply("ERR blast needs <x> <y> <r> [dx dy force]"); return; }
            var dir = v.Length == 6 ? new Vector2(v[3], v[4]) : Vector2.zero;
            float force = v.Length == 6 ? v[5] : 0f;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string res = TerrainSync.Request(new DamageEvent(DamageKind.Explosion, new Vector2(v[0], v[1]), dir.normalized, v[2], force, NextSeed()));
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
                ushort seed = NextSeed(events.Count);
                events.Add(blunt
                    ? new DamageEvent(DamageKind.Blunt, new Vector2(v[0], v[1]), new Vector2(v[2], v[3]).normalized, 0f, v[4], seed)
                    : new DamageEvent(DamageKind.Explosion, new Vector2(v[0], v[1]), Vector2.zero, v[2], 0f, seed));
            }
            if (events.Count == 0) { reply("ERR netloop needs [rev] <x y r | h x y dx dy force>..."); return; }
            reply($"OK netloop {TerrainSync.Loopback(events.ToArray(), rev)}");
        });

        TestBridge.Register("modbutton", "[番号] MRP の能力ボタンの一覧 (名前・待ち時間・表示) / 番号のボタンを押す (人が押したのと同じ道)", (args, reply) =>
            reply("OK modbutton " + Roles.ModButton.Probe(int.TryParse(args.Trim(), out int k) ? k : -1)));

        TestBridge.Register("terrainsync", "地形の同期の状態 (適用数・受けなかった依頼・指紋・客ごとの照合)", (args, reply) =>
        {
            var sb = new System.Text.StringBuilder("OK terrainsync");
            sb.Append($" applied={TerrainSync.Applied} refused={TerrainSync.Refused} mismatches={TerrainSync.Mismatches}");
            sb.Append($" seq={TerrainDigest.LastSeq} digest={TerrainDigest.Running:x8}");
            foreach (var kv in TerrainSync.Checks) sb.Append($" p{kv.Key}=#{kv.Value.seq}:{(kv.Value.same ? "same" : "DIFF")}");
            reply(sb.ToString());
        });

        TestBridge.Register("swing", "[force] [dx dy] ハンマーを振る (ボタンと同じ。向き省略 = 歩いている向き・向いている左右)", (args, reply) =>
        {
            float[] v = System.Array.Empty<float>();
            if (args.Trim().Length > 0 && (!TryParseFloats(args, out v) || (v.Length != 1 && v.Length != 3))) { reply("ERR swing [force] [dx dy]"); return; }
            float force = v.Length >= 1 ? v[0] : 0.5f;
            Vector2? aim = v.Length == 3 ? new Vector2(v[1], v[2]) : null;
            string res = HammerSwing.Swing(force, out bool ok, aim);
            reply($"OK swing ok={ok} {res} {HammerSwing.Describe()}");
        });

        TestBridge.Register("bomb", "[半径] 足元に爆弾を置く (ボタンと同じ。導火線の後に爆発)", (args, reply) =>
        {
            float[] v = System.Array.Empty<float>();
            if (args.Trim().Length > 0 && (!TryParseFloats(args, out v) || v.Length != 1)) { reply("ERR bomb [radius]"); return; }
            string res = BombFuse.Place(v.Length == 1 ? v[0] : 1.2f, out bool ok);
            reply($"OK bomb ok={ok} {res} {BombFuse.Describe()}");
        });

        TestBridge.Register("hammer","<x> <y> <dx> <dy> [force] 打撃 (位置から向きの先の壁を叩く。斜めに振ると斜めに抜ける。力 0..1・既定 0.5)", (args, reply) =>
        {
            if (!TryParseFloats(args, out var v) || (v.Length != 4 && v.Length != 5)) { reply("ERR hammer needs <x> <y> <dx> <dy> [force]"); return; }
            float force = v.Length == 5 ? v[4] : 0.5f;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string res = TerrainSync.Request(new DamageEvent(DamageKind.Blunt, new Vector2(v[0], v[1]), new Vector2(v[2], v[3]), 0f, force, NextSeed()));
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
        TestBridge.Register("findclip", "<正規表現> 読み込み済みの AudioClip を名前で探す", (args, reply) =>
        {
            var re = new System.Text.RegularExpressions.Regex(args.Trim(), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            int n = 0;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<AudioClip>()))
            {
                var c = o.TryCast<AudioClip>();
                if (!c || !re.IsMatch(c.name)) continue;
                if (++n > 80) break;
                reply($"CLIP {c.name} len={c.length:0.00}");
            }
            reply($"OK findclip n={n}");
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

        TestBridge.Register("shipcols", "船の層 (9) と家具の層 (12) の当たり判定 (トリガーでない物) を Screens/shipcols_<船>.txt へ: 持ち主・種類・範囲・頂点数・自分の絵の有無", (_, reply) =>
        {
            var ship = ShipStatus.Instance;
            if (!ship) { reply("ERR shipcols no ship"); return; }
            var sb = new StringBuilder();
            sb.Append("# ").Append(ship.name).Append('\n');
            int n = 0;
            foreach (var c in ship.GetComponentsInChildren<Collider2D>(true))
            {
                int layer = c.gameObject.layer;
                if ((layer != 9 && layer != 12) || c.isTrigger) continue;
                var b = c.bounds;
                var segs = new System.Collections.Generic.List<Vector2>();
                SolidMap.Segments(c, segs);
                n++;
                sb.Append(Path(c.transform)).Append(" | L").Append(layer).Append(' ').Append(c.GetIl2CppType().Name)
                  .Append(" | min=").Append(V((Vector2)b.min)).Append(" size=").Append(V((Vector2)b.size))
                  .Append(" | segs=").Append(segs.Count / 2)
                  .Append(" | sprite=").Append(c.GetComponent<SpriteRenderer>() ? "own" : "-").Append('\n');
            }
            string path = System.IO.Path.Combine(TestBridge.ScreensDir, $"shipcols_{ship.name}.txt");
            System.IO.File.WriteAllText(path, sb.ToString());
            reply($"OK shipcols n={n} -> {path}");
        });

        TestBridge.Register("propsurvey", "[最大の大きさ=3] 部屋の絵でない小さな絵 (家具・小物) を Screens/props.txt へ: 位置・大きさ・自分の当たり判定・付いている部品", (args, reply) =>
        {
            var ship = ShipStatus.Instance;
            if (!ship) { reply("ERR propsurvey no ship"); return; }
            float maxSize = float.TryParse(args.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float ms) ? ms : 3f;
            var sb = new StringBuilder();
            sb.Append("# ").Append(ship.name).Append('\n');
            int n = 0, withCol = 0;
            foreach (var sr in ship.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (!sr.gameObject.activeInHierarchy || !sr.sprite) continue;
                var m = sr.sharedMaterial;
                if (m && m.shader && m.shader.name == "Unlit/MaskShader") continue;
                var b = sr.bounds;
                if (b.size.x > maxSize || b.size.y > maxSize) continue;
                var go = sr.gameObject;
                var cols = new StringBuilder();
                foreach (var c in go.GetComponents<Collider2D>())
                    cols.Append(c.GetIl2CppType().Name).Append(c.isTrigger ? "(T)" : "").Append("@L").Append(go.layer).Append(' ');
                var comps = new StringBuilder();
                foreach (var c in go.GetComponents<Component>())
                {
                    string tn = c.GetIl2CppType().Name;
                    if (tn == "Transform" || tn == "SpriteRenderer" || tn.EndsWith("Collider2D")) continue;
                    comps.Append(tn).Append(' ');
                }
                if (cols.Length > 0) withCol++;
                n++;
                sb.Append(Path(sr.transform)).Append(" | ").Append(sr.sprite.name)
                  .Append(" | c=").Append(V((Vector2)b.center)).Append(" s=").Append(V((Vector2)b.size))
                  .Append(" z=").Append(TestBridge.F(sr.transform.position.z))
                  .Append(" | col=").Append(cols).Append("| ").Append(comps).Append('\n');
            }
            string path = System.IO.Path.Combine(TestBridge.ScreensDir, $"props_{ship.name}.txt");
            System.IO.File.WriteAllText(path, sb.ToString());
            reply($"OK propsurvey n={n} withCollider={withCol} -> {path}");
        });

        TestBridge.Register("roomz", "損傷マスクを見る部屋の絵の一覧と z (|z| < 0.1 = クルーと同じ奥行き・z < -0.1 = クルーより手前。床側の絵が掛かる所では瓦礫・ひびの z に使わない)", (args, reply) =>
        {
            var rooms = DamageMap.RoomArts;
            int band = 0, fg = 0;
            foreach (var sr in rooms)
            {
                if (!sr) continue;
                float z = sr.transform.position.z;
                if (z < -0.1f) fg++; else if (z < 0.1f) band++;
                var b = sr.bounds;
                reply($"ROOMZ {sr.transform.parent?.name}/{sr.name} sprite={(sr.sprite ? sr.sprite.name : "-")} z={TestBridge.F(z)} bounds=({TestBridge.F(b.min.x)},{TestBridge.F(b.min.y)})-({TestBridge.F(b.max.x)},{TestBridge.F(b.max.y)})");
            }
            reply($"OK roomz n={rooms.Count} band={band} foreground={fg}");
        });
    }

    internal static void RegisterNearWall()
    {
        TestBridge.Register("holecheck", "直前の破壊で切り取った壁の線の上で、壁の絵が抜けていない所 (当たり判定だけ抜けた所) を出す", (args, reply) =>
        {
            var segs = TerrainDamage.LastRemoved;
            int total = 0, bad = 0;
            for (int i = 0; i + 1 < segs.Count; i += 2)
            {
                Vector2 a = segs[i], b = segs[i + 1];
                int n = Mathf.Max(1, Mathf.CeilToInt((b - a).magnitude / 0.05f));
                for (int k = 0; k <= n; k++)
                {
                    Vector2 q = Vector2.Lerp(a, b, k / (float)n);
                    float h = DamageMap.HoleAt(q);
                    total++;
                    if (h >= 0.5f) continue;
                    if (bad++ < 12) reply($"KEPT {V(q)} hole={TestBridge.F(h)} seg={V(a)}-{V(b)}");
                }
            }
            reply($"OK holecheck segs={segs.Count / 2} samples={total} kept={bad}");
        });

        TestBridge.Register("maskdump", "<x> <y> <r> 損傷マスクの範囲を Screens/mask.ppm に書く (R=穴 G=焦げ B=熾火)", (args, reply) =>
        {
            if (!TryParse3(args, out float x, out float y, out float r)) { reply("ERR maskdump needs <x> <y> <r>"); return; }
            string path = System.IO.Path.Combine(TestBridge.ScreensDir, "mask.ppm");
            reply($"OK maskdump {DamageMap.DumpMask(new Vector2(x, y), r, path)} {path}");
        });

        TestBridge.Register("fxpause", "<0|1> 壊れる演出 (塊・破片・土煙) の動きを止める / 再開する (見た目の確認用)", (args, reply) =>
        {
            TerrainFx.Paused = args.Trim() == "1";
            reply($"OK fxpause {(TerrainFx.Paused ? 1 : 0)} pieces={BreakPieces.Count} pending={RubbleBake.PendingCount} baked={RubbleBake.BakedCount} sheets={RubbleBake.SheetCount} kb={RubbleBake.SheetBytes / 1024}");
        });

        TestBridge.Register("layer", "<char|rim|underlay|junk> <0|1> 見た目の層を外す / 戻す (切り分け用)。char/rim/underlay は今ある損傷にすぐ効く・junk は次の破壊から", (args, reply) =>
        {
            string[] p = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2) { reply("ERR layer <char|rim|underlay|junk> <0|1>"); return; }
            bool on = p[1] != "0";
            switch (p[0])
            {
                case "char": reply($"OK layer char {(on ? 1 : 0)} {DamageMap.DebugChannel(1, on)}"); break;
                case "rim": reply($"OK layer rim {(on ? 1 : 0)} {DamageMap.DebugChannel(2, on)}"); break;
                case "underlay": reply($"OK layer underlay {(on ? 1 : 0)} n={DamageMap.DebugUnderlay(on)}"); break;
                case "junk": TerrainFx.HideJunk = !on; reply($"OK layer junk {(on ? 1 : 0)}"); break;
                default: reply("ERR layer <char|rim|underlay|junk> <0|1>"); break;
            }
        });

        TestBridge.Register("furniture", "<x> <y> <r> 守る家具の範囲 (家具の当たり判定・壁の線の出っ張り)", (args, reply) =>
        {
            var a = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (a.Length < 3) { reply("ERR furniture <x> <y> <r>"); return; }
            var c = new Vector2(float.Parse(a[0], CultureInfo.InvariantCulture), float.Parse(a[1], CultureInfo.InvariantCulture));
            var list = DamageMap.FurnitureAt(c, float.Parse(a[2], CultureInfo.InvariantCulture));
            foreach (var rc in list) TestBridge.Out($"FURNITURE x={rc.xMin:F2}..{rc.xMax:F2} y={rc.yMin:F2}..{rc.yMax:F2}");
            reply($"OK furniture n={list.Count}");
        });

        TestBridge.Register("bumps", "[x y r] 壁の線の出っ張り (家具として守る所) の一覧。引数なしでマップ全体", (args, reply) =>
        {
            var a = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            var c = a.Length >= 2 ? new Vector2(float.Parse(a[0], CultureInfo.InvariantCulture), float.Parse(a[1], CultureInfo.InvariantCulture)) : Vector2.zero;
            float r = a.Length >= 3 ? float.Parse(a[2], CultureInfo.InvariantCulture) : 200f;
            var list = DamageMap.WallBumpsAt(c, r);
            foreach (var (rc, name) in list)
                TestBridge.Out($"BUMP {name} center=({rc.center.x:F2},{rc.center.y:F2}) x={rc.xMin:F2}..{rc.xMax:F2} y={rc.yMin:F2}..{rc.yMax:F2}");
            reply($"OK bumps n={list.Count}");
        });

        TestBridge.Register("warm", "試合の始めの先回りの準備 (損傷マスク・絵・種点・焼くカメラ・コンパイル) にかかった時間", (_, reply) => reply($"OK warm {TerrainWarm.Report}"));

        TestBridge.Register("shadowmask", "視界の影を作る層 (Constants.ShadowMask) と船の層 (ShipOnlyMask・ShipAndObjectsMask) のビット", (_, reply) =>
            reply($"OK shadowmask shadow={(int)Constants.ShadowMask:X} shipOnly={(int)Constants.ShipOnlyMask:X} shipAndObjects={(int)Constants.ShipAndObjectsMask:X}"));

        TestBridge.Register("bake", "[0|1|now] 止まった瓦礫を床の板へ焼くか (0 = GameObject のまま残す・now = 溜まっている分を今焼く)。引数なしで状態", (args, reply) =>
        {
            string a = args.Trim();
            if (a == "now") { reply($"OK bake {RubbleBake.Flush()}"); return; }
            if (a.Length > 0) RubbleBake.Enabled = a != "0";
            reply($"OK bake {(RubbleBake.Enabled ? 1 : 0)} pieces={BreakPieces.Count} pending={RubbleBake.PendingCount} baked={RubbleBake.BakedCount} sheets={RubbleBake.SheetCount} kb={RubbleBake.SheetBytes / 1024}");
        });

        TestBridge.Register("rubble", "大きな瓦礫の当たり判定の一覧 (位置・半径)", (_, reply) =>
        {
            foreach (var go in RubbleBlocks.All)
            {
                if (!go) continue;
                var col = go.GetComponent<CircleCollider2D>();
                Vector3 p = go.transform.position;
                reply($"RUBBLE at=({p.x:0.000},{p.y:0.000}) r={(col ? col.radius * go.transform.lossyScale.x : 0f):0.000} layer={go.layer}");
            }
            reply($"OK rubble n={RubbleBlocks.Count}");
        });

        TestBridge.Register("pieces", "[0|1] 壊れた壁の絵を割った塊を作るか (見た目の比較用)。引数なしで直前の破壊の割れ方 (種点・細胞・塊)", (args, reply) =>
        {
            if (args.Trim().Length > 0) BreakPieces.Enabled = args.Trim() != "0";
            reply($"OK pieces {(BreakPieces.Enabled ? 1 : 0)} {BreakPieces.LastStats}");
        });

        TestBridge.Register("cameras", "全カメラ (写す層・描き先・順番・置き換えシェーダの有無)", (_, reply) =>
        {
            foreach (var cam in Camera.allCameras)
                reply($"CAM {cam.name} enabled={cam.enabled} depth={cam.depth} mask=0x{cam.cullingMask:x} target={(cam.targetTexture ? cam.targetTexture.name : "-")} ortho={cam.orthographicSize} path={cam.transform.parent?.name}/{cam.name}");
            reply($"OK cameras n={Camera.allCamerasCount}");
        });

        TestBridge.Register("shadowcam", "[repl 0|1] [dump] 影のカメラ (置き換えシェーダ・描き先)。repl 0 = 置き換えを外す / 1 = 本編の設定に戻す。dump = 描き先を Screens/shadow.ppm へ", (args, reply) =>
        {
            var collab = Object.FindObjectOfType<ShadowCollab>();
            var cam = collab ? collab.ShadowCamera : null;
            if (!cam) { reply("ERR no shadow camera"); return; }
            var sc = cam.GetComponent<ShadowCamera>();
            var parts = args.Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "repl" && i + 1 < parts.Length)
                {
                    if (parts[++i] == "0") cam.ResetReplacementShader();
                    else if (sc) { sc.enabled = false; sc.enabled = true; } // OnEnable が本編の置き換えを掛け直す
                }
                else if (parts[i] == "dump")
                {
                    var rt = cam.targetTexture;
                    if (!rt) { reply("ERR no target"); return; }
                    var prev = RenderTexture.active;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
                    RenderTexture.active = prev;
                    var px = tex.GetPixels32();
                    Object.Destroy(tex);
                    // RGB と A を横に並べて書く (左 = 色・右 = アルファ)
                    int w = rt.width, h = rt.height;
                    var body = new byte[w * 2 * h * 3];
                    for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        var c = px[(h - 1 - y) * w + x];
                        int o = (y * w * 2 + x) * 3, oa = (y * w * 2 + w + x) * 3;
                        body[o] = c.r; body[o + 1] = c.g; body[o + 2] = c.b;
                        body[oa] = body[oa + 1] = body[oa + 2] = c.a;
                    }
                    string path = System.IO.Path.Combine(TestBridge.ScreensDir, "shadow.ppm");
                    using (var fs = System.IO.File.Create(path))
                    {
                        var head = Encoding.ASCII.GetBytes($"P6\n{w * 2} {h}\n255\n");
                        fs.Write(head, 0, head.Length);
                        fs.Write(body, 0, body.Length);
                    }
                    reply($"DUMP {path} {w}x{h}");
                }
            }
            reply($"OK shadowcam shadozer={(sc && sc.Shadozer ? sc.Shadozer.name : "-")} clear={cam.clearFlags} bg={cam.backgroundColor} target={(cam.targetTexture ? cam.targetTexture.name : "-")}");
        });

        TestBridge.Register("shadowpatch", "[0|1|now] 影の中の壊れた所の焼いた絵を作るか (0 = 作らない・直す前の見え方)・now = 溜まった分を今焼く。引数なしで状態", (args, reply) =>
        {
            string a = args.Trim();
            if (a == "now") { reply($"OK shadowpatch flushed={ShadowPatch.FlushAll()} tiles={ShadowPatch.TileCount}"); return; }
            if (a.Length > 0) ShadowPatch.Enabled = a != "0";
            reply($"OK shadowpatch {(ShadowPatch.Enabled ? 1 : 0)} tiles={ShadowPatch.TileCount} baked={ShadowPatch.BakedTotal}");
        });

        TestBridge.Register("solidmap", "[x y r] 歩ける所の地図の状態。範囲を渡すと Screens/solid.ppm に書く (白 = 歩ける・灰 = 船体の塊・紺 = 船の外・黒 = それ以外)", (args, reply) =>
        {
            SolidMap.Ensure();
            foreach (var sp in SolidMap.ExtraSeeds) reply($"EXTRA seed {V(sp)}");
            foreach (var sp in SolidMap.LeakedSeeds) reply($"LEAKED seed {V(sp)}");
            if (TryParse3(args, out float x, out float y, out float r))
            {
                string path = System.IO.Path.Combine(TestBridge.ScreensDir, "solid.ppm");
                reply($"DUMP {path} {SolidMap.Dump(new Vector2(x, y), r, path)}");
            }
            reply($"OK solidmap {SolidMap.Stats}");
        });

        TestBridge.Register("terrainmap", "マップ全体の絵に壊れ方の判定を重ねて Screens/terrainmap.ppm に書く (はしご・部屋・部屋の組は terrainmap.txt へ。床 = 島ごとの色・壁 緑 = 壊せる / 橙 = 段差 / 青 = 外壁 / 水色 = 厚い壁 / 紫 = 守る物 / 黄 = 家具 / 灰 = 両側に床なし・桃 = はしご)", (_, reply) =>
        {
            string path = System.IO.Path.Combine(TestBridge.ScreensDir, "terrainmap.ppm");
            string size = TerrainReview.Dump(path, out string legend);
            reply($"DUMP {path} {size}");
            System.IO.File.WriteAllLines(System.IO.Path.ChangeExtension(path, ".txt"), TerrainReview.Notes);
            foreach (var note in TerrainReview.Notes) if (!note.StartsWith("PIECE")) reply(note);
            reply($"OK terrainmap {legend} | {SolidMap.Stats}");
        });

        TestBridge.Register("paintbase", "[倍率=2] 注釈を塗ってもらう用のマップ全体の絵 (明るいまま・判定の色なし) を Screens/paintbase.ppm に書く", (args, reply) =>
        {
            int scale = int.TryParse(args.Trim(), out int sc) && sc >= 1 && sc <= 4 ? sc : 2;
            string path = System.IO.Path.Combine(TestBridge.ScreensDir, "paintbase.ppm");
            string size = TerrainReview.PaintBase(path, scale, out string info);
            reply($"DUMP {path} {size}");
            foreach (var r in ShipStatus.Instance.AllRooms)
            {
                if (!r || !r.roomArea) continue;
                var b = r.roomArea.bounds;
                reply($"ROOM {r.RoomId} {b.min.x:0.00} {b.min.y:0.00} {b.max.x:0.00} {b.max.y:0.00}");
            }
            reply($"OK paintbase {info}");
        });

        TestBridge.Register("solidat", "<x> <y> その点が歩けない所か (地図)・船体の塊の中か", (args, reply) =>
        {
            if (!TryParseFloats(args, out var v) || v.Length != 2) { reply("ERR solidat needs <x> <y>"); return; }
            SolidMap.Ensure();
            var pt = new Vector2(v[0], v[1]);
            reply($"OK solidat solid={SolidMap.Solid(pt)} hull={SolidMap.InHull(pt)} outside={SolidMap.NearOutside(pt.x, pt.y)}");
        });

        TestBridge.Register("zoom", "[大きさ=3] カメラの写す範囲 (縦の半分・世界単位)。小さいほど寄る", (args, reply) =>
        {
            var cam = Camera.main;
            if (!cam) { reply("ERR no camera"); return; }
            cam.orthographicSize = float.TryParse(args.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z) && z > 0.3f ? z : 3f;
            reply($"OK zoom {cam.orthographicSize}");
        });

        TestBridge.Register("look", "<x> <y> | off カメラをその点に止める (自分を追わない・見た目の確認用)", (args, reply) =>
        {
            var cam = Camera.main;
            var fc = cam ? cam.GetComponent<FollowerCamera>() : null;
            if (!fc) { reply("ERR no follower camera"); return; }
            if (args.Trim() == "off") { fc.Locked = false; reply("OK look off"); return; }
            if (!TryParseFloats(args, out var v) || v.Length != 2) { reply("ERR look needs <x> <y> | off"); return; }
            fc.Locked = true;
            var t = cam.transform;
            t.position = new Vector3(v[0], v[1], t.position.z);
            reply($"OK look {v[0]} {v[1]}");
        });

        TestBridge.Register("hud", "<0|1> HUD (タスク一覧・ボタン) を隠す / 戻す (撮影用)", (args, reply) =>
        {
            var hud = HudManager.Instance;
            if (!hud) { reply("ERR no hud"); return; }
            hud.gameObject.SetActive(args.Trim() != "0");
            reply($"OK hud {(hud.gameObject.activeSelf ? 1 : 0)}");
        });

        TestBridge.Register("maskat", "<x> <y> その点の損傷マスク (R=穴 G=焦げ B=熾火 A=家具)", (args, reply) =>
        {
            if (!TryParseFloats(args, out var v) || v.Length != 2) { reply("ERR maskat needs <x> <y>"); return; }
            reply($"OK maskat {DamageMap.MaskAt(new Vector2(v[0], v[1]))}");
        });

        TestBridge.Register("roomline", "<x1> <y1> <x2> <y2> 線に沿って 0.1 刻みで、その点を含む部屋の範囲 (roomArea) を出す", (args, reply) =>
        {
            if (!TryParseFloats(args, out var v) || v.Length != 4) { reply("ERR roomline needs <x1> <y1> <x2> <y2>"); return; }
            var ship = ShipStatus.Instance;
            if (!ship) { reply("ERR no ship"); return; }
            Vector2 a = new(v[0], v[1]), b = new(v[2], v[3]);
            int n = Mathf.CeilToInt((b - a).magnitude / 0.1f);
            string prev = null;
            for (int i = 0; i <= n; i++)
            {
                Vector2 q = Vector2.Lerp(a, b, i / (float)n);
                var sb = new StringBuilder();
                foreach (var r in ship.AllRooms)
                    if (r && r.roomArea && r.roomArea.OverlapPoint(q)) sb.Append(r.RoomId).Append(' ');
                string cur = sb.Length == 0 ? "-" : sb.ToString().Trim();
                if (cur != prev) reply($"ROOM {V(q)} {cur}");
                prev = cur;
            }
            reply("OK roomline");
        });

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
                    if (TerrainDamage.IsProtected(h.collider, h.point)) continue;
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
