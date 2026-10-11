using System;
using System.Collections.Generic;
using System.Text;
using MoreRolesPlus.Bridge;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 確認用 (bridge `props audit`): 船の家具と、動く物・高さの表・水面の板との対応を一覧にして Screens/propaudit_<船>.txt へ書く。
// NOPROP = 家具の層 (12) の当たり判定で、どの動く物にもなっていない物
// NOHEIGHT = 動く物 (小物以外) で高さの表に無い物 (水に沈んで見えない)
// STALE = 高さの表にあって船に見つからない名前
// NOFOOT = 高さの表にあるが足元の箱が無く外した物
// NOPLATE = 高さの表にあるが水面の板を作れない物 (絵が無い・部屋の絵からの切り抜きに失敗した)
// FRONT = 水の板より手前に描かれ、足元が歩ける床にある絵で、どの動く物にも属さない物 (水がその下に描かれ、沈んで見えない)
internal static partial class PropSim
{
    private static string WriteAudit()
    {
        var ship = ShipStatus.Instance;
        string shipName = FurnitureKinds.ShipName(ship);
        var sb = new StringBuilder();
        // 動く物が持っている当たり判定と絵
        var ownedCol = new HashSet<int>();
        var ownedTr = new HashSet<int>();
        var all = new List<Prop>(Props);
        all.AddRange(Stills);
        foreach (var p in all)
        {
            if (p.Col) ownedCol.Add(p.Col.GetInstanceID());
            if (p.Lift != null)
            {
                if (p.Lift.Col) ownedCol.Add(p.Lift.Col.GetInstanceID());
                if (p.Lift.Cut) ownedTr.Add(p.Lift.Cut.GetInstanceID());
            }
            if (p.Tr)
            {
                ownedTr.Add(p.Tr.GetInstanceID());
                foreach (var c in p.Tr.GetComponentsInChildren<Collider2D>(true)) if (c) ownedCol.Add(c.GetInstanceID());
                foreach (var t in p.Tr.GetComponentsInChildren<Transform>(true)) if (t) ownedTr.Add(t.GetInstanceID());
            }
            if (p.Riders != null) foreach (var r in p.Riders) if (r.Tr) ownedTr.Add(r.Tr.GetInstanceID());
        }
        int nNoProp = 0, nNoHeight = 0, nStale = 0, nNoFoot = 0, nNoPlate = 0, nFront = 0;

        foreach (var col in ship.GetComponentsInChildren<Collider2D>(true))
        {
            if (!col || !col.enabled || col.isTrigger || !col.gameObject.activeInHierarchy || col.gameObject.layer != FurnitureLayer) continue;
            if (ownedCol.Contains(col.GetInstanceID())) continue;
            var b = col.bounds;
            var sr = col.GetComponent<SpriteRenderer>();
            sb.Append($"NOPROP {PathOf(col.transform)} col={b.min.x:0.00},{b.min.y:0.00},{b.max.x:0.00},{b.max.y:0.00} sprite={(sr && sr.sprite ? sr.sprite.name : "-")}\n");
            nNoProp++;
        }

        var matched = new HashSet<string>();
        foreach (var p in all)
        {
            string key = FurnitureHeights.Strip(p.Name);
            if (FurnitureHeights.TryGet(shipName, p.Name, out _) || FurnitureHeights.TryGetStill(shipName, p.Name, out _))
            {
                matched.Add(key);
                if (p.FootHx <= 0 || p.FootHy <= 0) { sb.Append($"NOFOOT {p.Name} {p.Kind}\n"); nNoFoot++; continue; }
                // 部屋の絵の家具は水に浸かった時に切り抜く (失敗した物だけ)
                if (p.Lift != null ? p.Lift.Failed : !p.Tr || !p.Tr.GetComponent<SpriteRenderer>())
                {
                    sb.Append($"NOPLATE {p.Name} {p.Kind} lift={(p.Lift != null)} tried={p.Lift?.Tried} liftFailed={p.Lift?.Failed}\n");
                    nNoPlate++;
                }
                continue;
            }
            if (p.Kind == Kind.Small) continue;
            Transform t = p.Lift != null && p.Lift.Cut ? p.Lift.Cut : p.Tr;
            var sr = t ? t.GetComponent<SpriteRenderer>() : null;
            string at = sr ? $" sprite={sr.bounds.min.x:0.00},{sr.bounds.min.y:0.00},{sr.bounds.max.x:0.00},{sr.bounds.max.y:0.00}" : p.Lift != null ? $" col={p.Lift.Bounds.xMin:0.00},{p.Lift.Bounds.yMin:0.00},{p.Lift.Bounds.xMax:0.00},{p.Lift.Bounds.yMax:0.00}" : "";
            sb.Append($"NOHEIGHT {key} {p.Kind} lift={(p.Lift != null)}{at}\n");
            nNoHeight++;
        }
        foreach (var name in FurnitureHeights.NamesFor(shipName))
            if (!matched.Contains(name)) { sb.Append($"STALE {name}\n"); nStale++; }

        var mine = new HashSet<int>();
        var own = new List<SpriteRenderer>();
        DamageMap.CollectOwnShipRenderers(own);
        foreach (var r in own) if (r) mine.Add(r.GetInstanceID());
        var consoles = new HashSet<int>();
        foreach (var c in ship.AllConsoles) if (c) consoles.Add(c.transform.GetInstanceID());
        foreach (var v in ship.AllVents) if (v) mine.Add(v.GetComponent<SpriteRenderer>() ? v.GetComponent<SpriteRenderer>().GetInstanceID() : 0); // ベントは水の板ができる時に床と水の間へ下げる
        foreach (var sr in ship.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (!sr || !sr.enabled || !sr.sprite || !sr.gameObject.activeInHierarchy) continue;
            if (mine.Contains(sr.GetInstanceID()) || ownedTr.Contains(sr.transform.GetInstanceID())) continue;
            string sn = sr.gameObject.name;
            if (sn.StartsWith("Mrp", StringComparison.Ordinal)) continue;
            var b = sr.bounds;
            if (Math.Max(b.size.x, b.size.y) < 0.4f) continue;
            float cx = (b.min.x + b.max.x) * 0.5f, by = b.min.y + 0.05f;
            int gx = (int)MathF.Floor((cx - _org.x) * SolidMap.Ppu), gy = (int)MathF.Floor((by - _org.y) * SolidMap.Ppu);
            if (gx < 0 || gy < 0 || gx >= _w || gy >= _h || !SolidMap.OpenCell(gy * _w + gx)) continue;
            float front = DamageMap.FrontZ(new Vector2(cx, by));
            float waterZ = front - 0.0004f * DamageMap.ZScale(front);
            float z = sr.transform.position.z;
            if (z >= waterZ) continue;
            bool console = false;
            for (var t = sr.transform; t && !console; t = t.parent) console = consoles.Contains(t.GetInstanceID());
            sb.Append($"FRONT {PathOf(sr.transform)} sprite={sr.sprite.name} layer={sr.gameObject.layer} z={z:0.0000} water={waterZ:0.0000} rect={b.min.x:0.00},{b.min.y:0.00},{b.max.x:0.00},{b.max.y:0.00}{(console ? " console" : "")}\n");
            nFront++;
        }

        string head = $"# {shipName} props={Props.Count} still={Stills.Count} furnished={Furnished.Count} NOPROP={nNoProp} NOHEIGHT={nNoHeight} STALE={nStale} NOFOOT={nNoFoot} NOPLATE={nNoPlate} FRONT={nFront}\n";
        string path = System.IO.Path.Combine(TestBridge.ScreensDir, $"propaudit_{shipName}.txt");
        System.IO.File.WriteAllText(path, head + sb);
        return $"OK props audit {head.Substring(2).TrimEnd()} -> {path}";

        static string PathOf(Transform t)
        {
            string s = t.name;
            for (var q = t.parent; q && q != ShipStatus.Instance.transform; q = q.parent) s = q.name + "/" + s;
            return s.Replace(' ', '_');
        }
    }
}
