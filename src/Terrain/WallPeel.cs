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

    private const float PeelMargin = 0.15f; // 剥げかけの割れ目の種点は描く範囲からこれだけ先まで
    private const int PeelCap = 16;          // 重ねた剥げかけの割れ目 1 つ分の種点の上限 (崩れる時も同じ数でなぞる)
    private const int MaxHits = 2;           // 覚えておく打撃の数 (古い物から忘れる)。耐久 3 なら崩れる前は 2 回まで。16 × 2 で崩れる打撃の割れ目に 16 点以上残す

    // 叩いた 1 回分: 割れ目 (向き・掠め具合で形が変わる) と、その時に浮かせた / 欠けさせた数
    private sealed class HitMark
    {
        public CrackPattern Crack;
        public float Reach, G;
        public ushort Along;
        public int Lifted, Missing, Hp;
    }

    private sealed class State
    {
        public float BaseY;
        public int Slot;
        public readonly List<HitMark> Hits = new();
        public readonly List<(GameObject Go, Sprite Sp)> Parts = new();
    }

    private static readonly Dictionary<long, State> States = new();
    private static int _nextSlot;

    // 崩れなかった打撃。swing = 振った向き・hp = 残りの耐久。false = 剥げかけを出せない壁 (呼んだ側が従来のひびを貼る)。
    // 叩くたびに前のひびは残したまま、この打撃の割れ目を重ねる。正面からは丸く深く、掠めるほど壁に沿って
    // 振った向きへ細長く浅く割れ、浮いた表面は振った向きへめくれる
    public static bool Hit(Vector2 hit, Vector2 normal, Vector2 swing, int hp, ushort seed)
    {
        if (normal.y >= -0.5f || !DamageMap.Ready()) return false;
        long key = WallDurability.CellKey(hit);
        if (!States.TryGetValue(key, out var st))
        {
            st = new State { BaseY = hit.y, Slot = _nextSlot };
            _nextSlot = (_nextSlot + 1) & 255;
            States[key] = st;
        }
        DestroyParts(st);

        // 耐久が減るほどひびが広がり、浮く・欠ける細胞が増える
        bool last = hp <= 1;
        float reach = last ? 0.55f : 0.32f;
        var crack = TerrainDamage.StrikeCrack(new Vector2(hit.x, hit.y + Lift), normal, swing, seed * 31 + 7, default, PeelMargin, PeelCap, 0.8f);
        float g = FractureSites.Glance(normal, swing, out ushort along);
        Vector2 c = crack.Center;
        // 掠めるほど横に長く・縦に浅く
        float y0 = st.BaseY + 0.03f, y1 = st.BaseY + FaceHeight;
        float hw = reach * (1f + 0.6f * g), hh = reach * (1f - 0.3f * g);
        var area = Rect.MinMaxRect(c.x - hw, Math.Max(y0, c.y - hh), c.x + hw, Math.Min(y1, c.y + hh));
        st.Hits.Add(new HitMark
        {
            Crack = crack.WithArea(area, PeelMargin, PeelCap), Reach = reach, G = g, Along = along, Hp = hp,
            Lifted = last ? 4 : 2, Missing = last ? (g > 0.6f ? 1 : 2) : 0,
        });
        if (st.Hits.Count > MaxHits) st.Hits.RemoveAt(0);

        var cracks = new List<CrackPattern>(st.Hits.Count);
        foreach (var h in st.Hits) cracks.Add(h.Crack);
        FractureSites.Build(cracks, FractureSites.PeelRow0 + st.Slot);
        float slot = st.Slot / 255f;
        for (int j = 0; j < st.Hits.Count; j++) Draw(st, j, slot, normal, j == st.Hits.Count - 1);
        return true;
    }

    // 打撃 j の割れ目のひびと、浮いた / 欠けた表面。fresh = 今の打撃 (欠けた表面を落とす。前の打撃の分は跡だけ)
    private static void Draw(State st, int j, float slot, Vector2 normal, bool fresh)
    {
        var h = st.Hits[j];
        var area = h.Crack.Area;
        Vector2 c = h.Crack.Center;
        float y0 = st.BaseY + 0.03f;
        int from = FractureSites.Start(j), to = FractureSites.Start(j + 1);

        // ひびの線 (細胞の境)。b = 縦の半径・a = 中心の種点 + 64 × 横の伸びの段 (1 + 0.4 × 段 倍。シェーダの _UseDamage = 5)
        int ci = FractureSites.KeyAt(c.x, c.y);
        int sl = Math.Clamp((int)MathF.Round((h.Crack.Stretch - 1f) / 0.4f), 0, 3);
        Keep(st, BreakPieces.MakePeel(c, area, new Color(1f, slot, h.Reach * (1f - 0.3f * h.G), (ci + 64 * sl) / 255f), false, out var sp), sp);

        // 中心に近い細胞から、浮かせる / 欠けさせる物を選ぶ (中心の細胞は砕けやすいので欠ける側から)。
        // 掠めた打撃は振った向きの先の細胞ほど選ばれやすい
        Vector2 at = FractureSites.Dir(h.Along);
        float limit = h.Reach * (0.75f + 0.45f * h.G);
        var near = new List<(int Index, float D)>();
        for (int i = from; i < to; i++)
        {
            float dx = FractureSites.SiteX(i) - c.x, dy = FractureSites.SiteY(i) - c.y;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d <= limit && FractureSites.SiteY(i) > y0 + 0.05f) near.Add((i, d - (dx * at.x + dy * at.y) * 0.5f * h.G));
        }
        near.Sort((a, b) => a.D != b.D ? a.D.CompareTo(b.D) : a.Index.CompareTo(b.Index));
        var bounds = CellBounds(area, near);
        var rnd = new System.Random(h.Crack.Seed ^ (h.Hp * 977));
        bool last = h.Hp <= 1;
        int used = 0;
        foreach (var (i, d) in near)
        {
            if (used >= h.Lifted + h.Missing) break;
            if (!bounds.TryGetValue(i, out var b)) continue;
            // 外の細胞ほど選ばれにくい (ひびの中ほどがよく浮く)
            if (rnd.NextDouble() > 0.85 - Math.Max(0f, d) / limit * 0.4) continue;
            bool drop = used < h.Missing;
            used++;
            float sx = FractureSites.SiteX(i), sy = FractureSites.SiteY(i);
            if (drop)
            {
                // 断面だけ残し、表面の絵は根元へ落とす (前の打撃で欠けた所は跡だけ)
                Keep(st, BreakPieces.MakePeel(new Vector2(sx, sy), b, new Color(i / 255f, slot, 0f, 1f), false, out sp), sp);
                if (!fresh) continue;
                var fall = BreakPieces.MakePeel(new Vector2(sx, sy), b, new Color(i / 255f, slot, 0.5f, 0.5f), true, out _);
                if (fall != null) TerrainFx.DropPeel(fall, st.BaseY, -normal, h.Crack.Seed + i * 131 + h.Hp);
                continue;
            }
            // 浮いた表面: 中心から外へ押し出され、掠めた打撃は振った向きへめくれる (先の側ほど大きく)。少し下がる。
            // 0.2 単位の幅を 0..1 で頂点色へ
            float ux = sx - c.x, uy = sy - c.y, ul = MathF.Max(1e-4f, MathF.Sqrt(ux * ux + uy * uy));
            ux /= ul; uy /= ul;
            float vx = ux * (1f - 0.7f * h.G) + at.x * 1.3f * h.G, vy = uy * (1f - 0.7f * h.G) + at.y * 1.3f * h.G;
            float vl = MathF.Max(1e-4f, MathF.Sqrt(vx * vx + vy * vy));
            float push = (last ? 0.04f : 0.024f) + (float)rnd.NextDouble() * (last ? 0.035f : 0.02f);
            push *= 1f + 0.6f * h.G * (ux * at.x + uy * at.y);
            float ox = Math.Clamp(vx / vl * push, -0.095f, 0.095f), oy = Math.Clamp(vy / vl * push - (last ? 0.025f : 0.015f), -0.095f, 0.095f); // 重さで少し垂れる
            var wide = Rect.MinMaxRect(b.xMin + Math.Min(0f, ox), b.yMin + Math.Min(0f, oy), b.xMax + Math.Max(0f, ox), b.yMax + Math.Max(0f, oy));
            Keep(st, BreakPieces.MakePeel(new Vector2(sx, sy), wide, new Color(i / 255f, slot, 0.5f + ox / 0.2f, 0.5f + oy / 0.2f), false, out sp), sp);
        }
    }

    // 崩れる打撃の割れ目の下地: 剥げかけていればそのひび (古い順)。崩れる打撃の割れ目を呼んだ側が最後に足す
    public static List<CrackPattern> Cracks(Vector2 hit)
    {
        var list = new List<CrackPattern>();
        if (States.TryGetValue(WallDurability.CellKey(hit), out var st))
            foreach (var h in st.Hits) list.Add(h.Crack);
        return list;
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
