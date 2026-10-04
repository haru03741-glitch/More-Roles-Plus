using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 剥げかけ: 崩れる前の打撃 (耐久が残った時) で、叩いた所から放射状のひびが入り、表面の細胞が浮いてずれ、
// 欠けた所から壁の中 (断面) が覗く。ひびは崩れる時と同じ種点 (同じ中心・同じ種) で描くので、
// 3 回目に崩れる時の割れ目は、それまでに入ったひびをなぞる。
// 面を持つ壁 (下から叩いた・絵が当たり判定の線より上に立っている) だけ。他の壁は従来のひびの板
internal static class WallPeel
{
    private const float Lift = 0.4f;       // 中心を叩いた点 (壁の根元) からこれだけ上へ (壁の面の中ほど)
    private const float FaceHeight = 0.85f; // 壁の面の高さ (これより上は壁の上面)
    private const float Sample = 1f / 48f;  // 細胞の範囲を測る刻み

    private sealed class State
    {
        public Vector2 Center;
        public float BaseY;
        public int Seed, Slot;
        public readonly List<(GameObject Go, Sprite Sp)> Parts = new();
    }

    private static readonly Dictionary<long, State> States = new();
    private static int _nextSlot;

    // 崩れなかった打撃。hp = 残りの耐久。false = 剥げかけを出せない壁 (呼んだ側が従来のひびを貼る)
    public static bool Hit(Vector2 hit, Vector2 normal, int hp, ushort seed)
    {
        if (normal.y >= -0.5f || !DamageMap.Ready()) return false;
        long key = WallDurability.CellKey(hit);
        if (!States.TryGetValue(key, out var st))
        {
            st = new State { Center = new Vector2(hit.x, hit.y + Lift), BaseY = hit.y, Seed = seed * 31 + 7, Slot = _nextSlot };
            _nextSlot = (_nextSlot + 1) & 255;
            States[key] = st;
        }
        DestroyParts(st);

        // 耐久が減るほどひびが広がり、浮く・欠ける細胞が増える
        bool last = hp <= 1;
        float reach = last ? 0.55f : 0.32f;
        int lifted = last ? 4 : 2, missing = last ? 2 : 0;
        Vector2 c = st.Center;
        float y0 = st.BaseY + 0.03f, y1 = st.BaseY + FaceHeight;
        var area = Rect.MinMaxRect(c.x - reach, Math.Max(y0, c.y - reach), c.x + reach, Math.Min(y1, c.y + reach));
        FractureSites.Build(c, st.Seed, area, FractureSites.PeelRow0 + st.Slot);
        float slot = st.Slot / 255f;

        // ひびの線 (細胞の境)
        Keep(st, BreakPieces.MakePeel(c, area, new Color(1f, slot, reach, 1f), false, out var sp), sp);

        // 中心に近い細胞から、浮かせる / 欠けさせる物を選ぶ (中心の細胞は砕けやすいので欠ける側から)
        var near = new List<(int Index, float D)>();
        for (int i = 0; i < FractureSites.Count; i++)
        {
            float dx = FractureSites.SiteX(i) - c.x, dy = FractureSites.SiteY(i) - c.y;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d <= reach * 0.75f && FractureSites.SiteY(i) > y0 + 0.05f) near.Add((i, d));
        }
        near.Sort((a, b) => a.D != b.D ? a.D.CompareTo(b.D) : a.Index.CompareTo(b.Index));
        var bounds = CellBounds(area, near);
        var rnd = new System.Random(st.Seed ^ (hp * 977));
        int used = 0;
        foreach (var (i, d) in near)
        {
            if (used >= lifted + missing) break;
            if (!bounds.TryGetValue(i, out var b)) continue;
            // 外の細胞ほど選ばれにくい (ひびの中ほどがよく浮く)
            if (rnd.NextDouble() > 0.85 - d / reach * 0.4) continue;
            bool drop = used < missing;
            used++;
            float sx = FractureSites.SiteX(i), sy = FractureSites.SiteY(i);
            if (drop)
            {
                // 断面だけ残し、表面の絵は根元へ落とす
                Keep(st, BreakPieces.MakePeel(new Vector2(sx, sy), b, new Color(i / 255f, slot, 0f, 1f), false, out sp), sp);
                var fall = BreakPieces.MakePeel(new Vector2(sx, sy), b, new Color(i / 255f, slot, 0.5f, 0.5f), true, out _);
                if (fall != null) TerrainFx.DropPeel(fall, st.BaseY, -normal, st.Seed + i * 131 + hp);
                continue;
            }
            // 浮いた表面: 中心から外へ押し出され、少し下がる (0.2 単位の幅を 0..1 で頂点色へ)
            float ux = sx - c.x, uy = sy - c.y, ul = MathF.Max(1e-4f, MathF.Sqrt(ux * ux + uy * uy));
            float push = (last ? 0.04f : 0.024f) + (float)rnd.NextDouble() * (last ? 0.035f : 0.02f);
            float ox = ux / ul * push, oy = uy / ul * push - (last ? 0.025f : 0.015f); // 重さで少し垂れる
            var wide = Rect.MinMaxRect(b.xMin + Math.Min(0f, ox), b.yMin + Math.Min(0f, oy), b.xMax + Math.Max(0f, ox), b.yMax + Math.Max(0f, oy));
            Keep(st, BreakPieces.MakePeel(new Vector2(sx, sy), wide, new Color(i / 255f, slot, 0.5f + ox / 0.2f, 0.5f + oy / 0.2f), false, out sp), sp);
        }
        return true;
    }

    // 崩れる打撃の割れ目: 剥げかけがあればその中心と種 (ひびをなぞって崩れる)
    public static bool TryGet(Vector2 hit, out Vector2 center, out int seed)
    {
        if (States.TryGetValue(WallDurability.CellKey(hit), out var st))
        {
            center = st.Center;
            seed = st.Seed;
            return true;
        }
        center = default;
        seed = 0;
        return false;
    }

    // 崩れた後: 剥げかけの部品を片付ける (崩れた塊が代わりに描く)
    public static void Release(Vector2 hit)
    {
        long key = WallDurability.CellKey(hit);
        if (!States.TryGetValue(key, out var st)) return;
        DestroyParts(st);
        States.Remove(key);
    }

    // マップが変わった時 (GameObject は DamageMap が片付ける)
    public static void Clear()
    {
        foreach (var st in States.Values)
            foreach (var p in st.Parts)
                if (p.Sp) UnityEngine.Object.Destroy(p.Sp);
        States.Clear();
        _nextSlot = 0;
    }

    private static void Keep(State st, BreakPiece p, Sprite sp)
    {
        if (p == null) return;
        st.Parts.Add((p.Tr.gameObject, sp));
    }

    private static void DestroyParts(State st)
    {
        foreach (var p in st.Parts)
        {
            if (p.Go) UnityEngine.Object.Destroy(p.Go);
            if (p.Sp) UnityEngine.Object.Destroy(p.Sp);
        }
        st.Parts.Clear();
    }

    // 選んだ細胞の範囲 (area の中で、刻み Sample で測り、輪郭線ぶん広げる)
    private static Dictionary<int, Rect> CellBounds(Rect area, List<(int Index, float D)> cells)
    {
        var want = new HashSet<int>();
        foreach (var c in cells) want.Add(c.Index);
        var lo = new Dictionary<int, (float X0, float Y0, float X1, float Y1)>();
        for (float y = area.yMin + Sample * 0.5f; y < area.yMax; y += Sample)
        for (float x = area.xMin + Sample * 0.5f; x < area.xMax; x += Sample)
        {
            int k = FractureSites.KeyAt(x, y);
            if (!want.Contains(k)) continue;
            if (!lo.TryGetValue(k, out var b)) b = (x, y, x, y);
            lo[k] = (Math.Min(b.X0, x), Math.Min(b.Y0, y), Math.Max(b.X1, x), Math.Max(b.Y1, y));
        }
        var res = new Dictionary<int, Rect>();
        const float pad = Sample + 0.02f;
        foreach (var kv in lo)
        {
            var b = kv.Value;
            res[kv.Key] = Rect.MinMaxRect(Math.Max(area.xMin, b.X0 - pad), Math.Max(area.yMin, b.Y0 - pad),
                Math.Min(area.xMax, b.X1 + pad), Math.Min(area.yMax, b.Y1 + pad));
        }
        return res;
    }
}
