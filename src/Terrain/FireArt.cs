using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 火の計算 (FireSim) を絵にする。FireSim.TileCells 升四方のタイルごとに 1 枚の小さな絵 (1 画素 = 1 升) を持ち、
// 火が変わったタイルだけを Redraw 秒ごとに描き直す。絵は自分の升の周りに縁 (横 Side 升・上下 1 升) と、上に炎が立ち上がる Up 升を持つ。
//   R = 炎の大きさ・G = 熱 (床の照り)・B = 油の量・A = 歩ける升 (0 = 閉じた升・OpenA..255 = 開いた升で、OpenA から上は燃やした量 = 床の焦げ)
// 同じ絵を床の板 (照りと油・水の少し手前) と炎の板 (行の帯ごと・クルーと同じ奥行きの決まりで前後する) で使う。
// 炎の形・色・ゆらぎはシェーダ MRP/Fire が決める
internal static class FireArt
{
    public const int Up = 4;                  // 炎が立ち上がる高さ (升)
    private const int StripRows = 1;          // 炎の板 1 枚が受け持つ行 (この単位で奥行きを変える。2 行以上だとすぐ手前に立つ人に炎が被った)
    private static int Strips => FireSim.TileCells / StripRows;
    private const float Redraw = 0.1f;
    private const int TilesPerFrame = 6;
    private const float SweepEvery = 2f, KeepEmpty = 5f;
    private const float GlowFrom = 150f, GlowRange = 1400f;
    private const float OilFull = 1200f;
    private const int OpenA = 96;
    private const int CharHalf = 120;         // 燃やした量がこれで焦げの濃さ 255×1.25/2 (金属の床はうっすら・草は中ほど・木と油は真っ黒)

    private sealed class Tile
    {
        public int Tx, Ty;
        public GameObject Floor;
        public SpriteRenderer FloorSr, FloorShadow; // Shadow = 影の中に見せる写し (ShadowView)
        public GameObject[] Flame;
        public SpriteRenderer[] FlameSr, FlameShadow;
        public Sprite[] FlameSp;
        public Texture2D Tex;
        public Sprite Sp;
        public byte[] Px;
        public float LastDraw = -10f;
        public bool Waiting;
        public bool Empty = true;
        public bool[] RowOn, RowShown;
        public bool FloorShown;
        public bool Flaming;               // 燃えている行がある (炎の板が要る)
        public float FlameAt;              // 最後に燃えている行があった時刻
    }

    private static readonly Dictionary<int, Tile> Tiles = new();
    private static readonly List<int> Waiting = new();
    private static float _clock;
    private static long _lastMs;
    private static bool _matSet;
    private static float _sweepAt;
    internal static double LastDrawMs { get; private set; }
    internal static int Drawn { get; private set; }
    internal static bool Hidden;

    private const int Side = 3;               // 横の縁 (升)。舌は 2 升幅でゆれるので、隣のタイルへはみ出す分を描ける幅
    private static int TexW => FireSim.TileCells + 2 * Side;
    private static int TexH => FireSim.TileCells + 2 + Up;

    public static void Tick()
    {
        try { TickCore(); }
        catch (Exception e) { Plugin.Logger.LogError($"[FireArt] {e}"); Clear(); FireSim.Reset(); }
    }

    private static void TickCore()
    {
        if (!FireSim.Ready) { if (Tiles.Count > 0) Clear(); _lastMs = 0; return; }
        if (!_matSet)
        {
            if (!MrpBundle.FlameMaterial || !MrpBundle.FireFloorMaterial) return;
            SetLayout();
        }
        long now = Environment.TickCount64;
        float dt = _lastMs == 0 ? 0.016f : Math.Min(0.1f, (now - _lastMs) / 1000f);
        _lastMs = now;
        _clock += dt;

        var dirty = FireSim.DirtyTiles;
        if (dirty.Count > 0)
        {
            foreach (int t in dirty)
            {
                FireSim.Clean(t);
                if (!Tiles.TryGetValue(t, out var tile)) Tiles[t] = tile = new Tile { Tx = t % FireSim.TilesW, Ty = t / FireSim.TilesW };
                if (!tile.Waiting) { tile.Waiting = true; Waiting.Add(t); }
            }
            dirty.Clear();
        }
        if (_clock - _sweepAt >= SweepEvery) Sweep();
        if (Tiles.Count > 0) ShadowView.Check(now);
        int drawn = 0;
        for (int i = 0; i < Waiting.Count && drawn < TilesPerFrame; i++)
        {
            var tile = Tiles[Waiting[i]];
            if (_clock - tile.LastDraw < Redraw) continue;
            tile.Waiting = false;
            Waiting.RemoveAt(i--);
            Draw(tile);
            drawn++;
        }
    }

    // タイルの絵の並び (どのタイルも同じ) をシェーダへ 1 回だけ渡す
    private static void SetLayout()
    {
        _matSet = true;
        float w = TexW, h = TexH, tc = FireSim.TileCells;
        var own = new Vector4(Side / w, 1f / h, (tc + Side) / w, (tc + 1f) / h);
        foreach (var m in new[] { MrpBundle.FlameMaterial, MrpBundle.FireFloorMaterial })
        {
            m.SetVector("_Own", own);
            m.SetVector("_CellUV", new Vector4(1f / w, 1f / h, 0f, 0f));
            m.SetFloat("_Rise", Up / h);
            m.SetFloat("_Strip", StripRows / h);
        }
    }

    private static unsafe void Draw(Tile t)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int tc = FireSim.TileCells, w = FireSim.W, h = FireSim.H;
        int x0 = t.Tx * tc - Side, y0 = t.Ty * tc - 1;
        int tw = TexW, th = TexH;
        t.Px ??= new byte[tw * th * 4];
        bool any = false, flaming = false;
        t.RowOn ??= new bool[Strips];
        Array.Clear(t.RowOn, 0, t.RowOn.Length);
        for (int py = 0; py < th; py++)
        for (int px = 0; px < tw; px++)
        {
            int cx = x0 + px, cy = y0 + py;
            int i = (py * tw + px) * 4;
            if (cx < 1 || cy < 1 || cx >= w - 1 || cy >= h - 1) { t.Px[i] = t.Px[i + 1] = t.Px[i + 2] = t.Px[i + 3] = 0; continue; }
            int k = cy * w + cx;
            int burn = FireSim.Burn(k);
            float heat = FireSim.Heat(k);
            int glow = heat <= GlowFrom ? 0 : (int)Math.Min(255f, (heat - GlowFrom) * (255f / GlowRange));
            int oil = FireSim.Oil(k);
            int ob = oil <= 0 ? 0 : (int)Math.Min(255f, 40f + oil * (215f / OilFull));
            t.Px[i] = (byte)burn;
            t.Px[i + 1] = (byte)glow;
            t.Px[i + 2] = (byte)ob;
            int ch = FireSim.Charred(k);
            int cb = ch <= 0 ? 0 : Math.Min(255, ch * 319 / (ch + CharHalf));
            t.Px[i + 3] = WaterSim.Open(k) ? (byte)(OpenA + cb * (255 - OpenA) / 255) : (byte)0;
            bool own = px >= Side && py >= 1 && px < tc + Side && py <= tc;
            if (own && (burn | glow | ob | cb) != 0) any = true;
            if (own && burn != 0) { t.RowOn[(py - 1) / StripRows] = true; flaming = true; }
        }
        t.LastDraw = _clock;
        t.Flaming = flaming;
        if (flaming) t.FlameAt = _clock;
        if (!any)
        {
            if (!t.Empty && t.FloorSr) Show(t, false);
            t.Empty = true;
            return;
        }
        Ensure(t);
        fixed (byte* b = t.Px) t.Tex.LoadRawTextureData((IntPtr)b, t.Px.Length);
        t.Tex.Apply(false, false);
        t.Empty = false;
        Show(t, !Hidden);
        Drawn++;
        LastDrawMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static void Ensure(Tile t)
    {
        int tc = FireSim.TileCells, tw = TexW, th = TexH;
        float cell = FireSim.Cell;
        var org = FireSim.Origin;
        float wx = org.x + (t.Tx * tc - Side) * cell, wy = org.y + (t.Ty * tc - 1) * cell;
        if (t.Flaming && t.Flame == null && t.Floor) MakeFlames(t, wx, wy);
        if (t.Floor) return;
        t.Tex = new Texture2D(tw, th, TextureFormat.RGBA32, false) { name = "MrpFire", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        t.Sp = Sprite.Create(t.Tex, new Rect(0, 0, tw, th), Vector2.zero, 1f / cell, 0, SpriteMeshType.FullRect);
        t.Sp.name = "MrpFire";
        // 床の板: 水と同じく部屋の絵の一番手前より前 (油は水に浮くので水より少し手前)
        float size = tc * cell, front = float.MaxValue;
        for (int k = 0; k < 5; k++)
        {
            float fx = k == 4 ? 0.5f : (k & 1), fy = k == 4 ? 0.5f : (k >> 1);
            front = Math.Min(front, DamageMap.FrontZ(new Vector2(wx + Side * cell + size * (0.05f + 0.9f * fx), wy + cell + size * (0.05f + 0.9f * fy))));
        }
        float zs = DamageMap.ZScale(front);
        t.Floor = new GameObject("MrpFireFloor") { layer = 0 };
        t.FloorSr = t.Floor.AddComponent<SpriteRenderer>();
        t.FloorSr.enabled = false;
        t.FloorSr.sprite = t.Sp;
        t.FloorSr.sharedMaterial = MrpBundle.FireFloorMaterial;
        t.Floor.transform.position = FxMath.V3(wx, wy, front - 0.0005f * zs);
        t.FloorShadow = ShadowView.Copy(t.Floor, t.Sp, ShadowView.FireFloor, FxMath.Rgba(1f, 1f, 1f, 1f), 0f);
        if (t.Flaming) MakeFlames(t, wx, wy);
    }

    private static void MakeFlames(Tile t, float wx, float wy)
    {
        int tw = TexW;
        float cell = FireSim.Cell;
        // 炎の板: 行の帯ごとに 1 枚。クルーと同じ奥行きの決まり (y / 1000) で帯の下の縁に立つので、
        // 帯より奥 (上) にいる人の手前・手前 (下) にいる人の奥に描かれる。帯の番号は頂点色の緑で渡す
        int n = Strips;
        t.Flame = new GameObject[n];
        t.FlameSr = new SpriteRenderer[n];
        t.FlameSp = new Sprite[n];
        t.FlameShadow = new SpriteRenderer[n];
        for (int s = 0; s < n; s++)
        {
            var sp = Sprite.Create(t.Tex, new Rect(0, s * StripRows, tw, StripRows + 2 + Up), Vector2.zero, 1f / cell, 0, SpriteMeshType.FullRect);
            sp.name = "MrpFlame";
            var go = new GameObject("MrpFlame") { layer = 0 };
            var sr = go.AddComponent<SpriteRenderer>();
            sr.enabled = false;
            sr.sprite = sp;
            sr.sharedMaterial = MrpBundle.FlameMaterial;
            sr.color = FxMath.Rgba(1f, s / 16f, 1f, 1f);
            float by = wy + (s * StripRows + 1) * cell;
            go.transform.position = FxMath.V3(wx, wy + s * StripRows * cell, by / 1000f - 0.0005f);
            t.Flame[s] = go; t.FlameSr[s] = sr; t.FlameSp[s] = sp;
            // 写しは床の写しより手前。上の帯ほど奥 (視界の中の炎と同じ前後)
            t.FlameShadow[s] = ShadowView.Copy(go, sp, ShadowView.Flame, sr.color, 0.002f + (1000f - by) * 1e-4f); // 影の板は z=-5 付近なので 1e-4 刻みなら float で潰れない
        }
    }

    private static void Show(Tile t, bool on)
    {
        if (t.FloorShown != on)
        {
            t.FloorShown = on; t.FloorSr.enabled = on;
            if (t.FloorShadow) t.FloorShadow.enabled = on;
        }
        if (t.FlameSr == null) return;
        t.RowShown ??= new bool[t.FlameSr.Length];
        for (int s = 0; s < t.FlameSr.Length; s++)
        {
            bool v = on && t.RowOn[s];
            if (t.RowShown[s] != v)
            {
                t.RowShown[s] = v; t.FlameSr[s].enabled = v;
                var sh = t.FlameShadow[s];
                if (sh) sh.enabled = v;
            }
        }
    }

    internal static int DebugHide(bool hide)
    {
        Hidden = hide;
        int n = 0;
        foreach (var t in Tiles.Values)
            if (t.FloorSr) { Show(t, !hide && !t.Empty); n++; }
        return n;
    }

    // 火が消えてしばらくたったタイルの絵を捨てる (広く燃えた試合で板と絵が残り続けないように)。
    // 焦げの残るタイルは床の板 1 枚だけ残し、炎の板 (行の数だけある) を捨てる
    private static readonly List<int> Dead = new();
    private static void Sweep()
    {
        _sweepAt = _clock;
        Dead.Clear();
        foreach (var kv in Tiles)
        {
            var t = kv.Value;
            if (t.Waiting || _clock - t.LastDraw < KeepEmpty) continue;
            if (t.Empty) Dead.Add(kv.Key);
            else if (!t.Flaming && t.Flame != null && _clock - t.FlameAt >= KeepEmpty) DropFlames(t);
        }
        foreach (int k in Dead) { Destroy(Tiles[k]); Tiles.Remove(k); }
    }

    private static void DropFlames(Tile t)
    {
        if (t.Flame != null) foreach (var go in t.Flame) if (go) UnityEngine.Object.Destroy(go);
        if (t.FlameSp != null) foreach (var sp in t.FlameSp) if (sp) UnityEngine.Object.Destroy(sp);
        t.Flame = null; t.FlameSr = null; t.FlameSp = null; t.FlameShadow = null; t.RowShown = null;
    }

    private static void Destroy(Tile t)
    {
        if (t.Floor) UnityEngine.Object.Destroy(t.Floor);
        DropFlames(t);
        if (t.Sp) UnityEngine.Object.Destroy(t.Sp);
        if (t.Tex) UnityEngine.Object.Destroy(t.Tex);
    }

    private static int CountFlames()
    {
        int n = 0;
        foreach (var t in Tiles.Values) if (t.Flame != null) n++;
        return n;
    }

    internal static void Clear()
    {
        foreach (var t in Tiles.Values) Destroy(t);
        Tiles.Clear();
        Waiting.Clear();
        _clock = _sweepAt = 0f;
    }

    internal static string Describe() => $"tiles={Tiles.Count} flaming={CountFlames()} waiting={Waiting.Count} drawn={Drawn} drawMs={LastDrawMs:0.00}";
}
