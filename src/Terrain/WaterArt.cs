using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 水の計算 (WaterSim) の高さを絵にする。WaterSim.TileCells 升四方のタイルごとに 1 枚の絵を持ち、
// 水が変わったタイルだけを Redraw 秒ごと・1 フレーム TilesPerFrame 枚まで描き直す。
// 絵に書くのは水の量だけ (R = 深さ / DeepScale・G = 見せてよいか・B = 濡れているか)。色・縁・水面の揺れときらめきはシェーダ MRP/Water が決める。
// 高さは 3×3 (1-2-1) でならしてから升の真ん中の値として 3 次の B スプラインで引く。
// 縁は深さの低い線でなく「濡れた升か」(0/1) をならした値の中ほどの線で引く: 深さの低い線はならしの裾野を通るので
// 升ごとのこぶが並んだ階段になる。中ほどの線は濡れた升と乾いた升の間を通るので、斜めの前線もまっすぐな線になる。
// 床マスク (FloorMask・部屋の絵の画素ごとの床) があるマップでは、水の縁はシェーダがそのマスクで画素ごとに切る
// (壁の面・家具の天板の上は見せない。水は家具の下を通っている)。
// マスクが無い時は、壁の中 (SolidMap で歩けない所) と家具の当たり判定の中を見せない (1 画素 = SolidMap の 1 升 = 1/16 単位)。
// 影の中の焼いた絵 (ShadowPatch) には描き込まない: 水は部屋いっぱいに広がり、焼く升が数十になって読み戻しで止まる (実測 127 回・1 回 30ms 前後)。
// 水は視界の中だけに見える
internal static class WaterArt
{
    private const int Px = 4;                 // 升 1 つの画素
    private const float DeepScale = 1600f;    // R = 1 の深さ (シェーダの _RUnits と同じ)
    // 濡れ具合 = 深さ WetMin で 0・WetMin + WetRamp で 1 (B = それを B スプラインでならした値。シェーダはその 0.35 の線 ≒ 深さ 30 を水の縁にする)。
    // 0/1 で切ると、薄く広がった水 (深さ 30 前後) で升ごとに濡れた・乾いたが入れ替わり、縁がでこぼこになる
    private const int WetMin = 15, WetRamp = 45;
    private const float Redraw = 0.1f;
    private const int TilesPerFrame = 3;
    private const int FurnSub = Px;           // 家具の型抜きの細かさ (= 画素)
    // A = 壁の面の上の水の厚さ (単位) × FaceScale + 128。128 の線が壁に這い上がった水面の線。壁の面でない画素は 0
    private const float FaceScale = 96f;
    private const int FaceBaseScan = 3;       // 壁の根元の升が閉じている (壁の線を太らせた分) 時に、下へ探す升の数

    private static void Enable(Tile t, bool on)
    {
        t.Sr.enabled = on;
        if (t.Shadow) t.Shadow.enabled = on;
    }

    private sealed class Tile
    {
        public int Tx, Ty;
        public GameObject Go;
        public SpriteRenderer Sr, Shadow; // Shadow = 影の中に見せる写し (ShadowView)
        public Texture2D Tex;
        public Sprite Sp;
        public byte[] Px;
        public bool[] Furn;
        public byte[] Face;       // 壁の面の画素の床からの高さ (FloorMask.FaceHeights)。壁の面が無いタイルは null
        public bool FaceDone;
        public int FurnVersion;
        public MaterialPropertyBlock Block;
        public Texture2D Floor; // 床マスク (部屋の絵の画素ごとの床) をこのタイルの範囲で切り出した物
        public bool FloorOn;
        public float LastDraw = -10f;
        public bool Waiting;
        public bool Empty = true;
    }

    // 爆発のくぼみ (絵だけ)。水の計算は全員の結果を揃えるため Delay 刻み遅れて爆心をえぐるので、
    // それまでの間は描く高さから差し引いて同じ形を先に見せ、計算の水が追いついたら消す
    private const float CraterIn = 0.08f, CraterOut = 0.25f;
    private const float CraterDepth = 1.15f, RimBulge = 0.5f;
    private struct Crater { public float X, Y, R, T0; }
    private static readonly List<Crater> Craters = new();

    private static readonly Dictionary<int, Tile> Tiles = new();
    private static readonly List<int> Waiting = new();
    private static int[] _raw;     // 描く間の高さ (タイル + 縁 3 升)
    private static int[] _depth;   // それを 3×3 でならした物 (タイル + 縁 2 升)
    private static bool[] _closed; // 描く間の升が閉じているか (タイル + 縁 3 升)
    private static int[] _wet;     // 濡れ具合 (0..WetRamp)。縁を引くための物 (タイル + 縁 2 升)
    private static float _clock;
    private static int _furnVersion;
    private static long _lastMs;
    internal static double LastDrawMs { get; private set; }
    internal static int Drawn { get; private set; }
    // 切り分け用: 水たまりの絵を全部隠す (計算と描き直しは続ける)。bridge `layer water 0`
    internal static bool Hidden;
    internal static int DebugHide(bool hide)
    {
        Hidden = hide;
        int n = 0;
        foreach (var t in Tiles.Values) if (t.Sr) { Enable(t, !hide && !t.Empty); n++; }
        return n;
    }

    public static void Tick()
    {
        try { TickCore(); }
        catch (Exception e) { Plugin.Logger.LogError($"[WaterArt] {e}"); Clear(); WaterSim.Reset(); }
    }

    private static void TickCore()
    {
        if (!WaterSim.Ready) { if (Tiles.Count > 0) Clear(); _lastMs = 0; return; }
        if (!MrpBundle.WaterMaterial) return;
        long now = Environment.TickCount64;
        float dt = _lastMs == 0 ? 0.016f : Math.Min(0.1f, (now - _lastMs) / 1000f);
        _lastMs = now;
        _clock += dt;

        if (Craters.Count > 0) TickCraters();
        if (Tiles.Count > 0) ShadowView.Check(now);
        var dirty = WaterSim.DirtyTiles;
        if (dirty.Count > 0)
        {
            foreach (int t in dirty)
            {
                WaterSim.Clean(t);
                Queue(t);
                // 水が上の壁を這い上がるので、上のタイルも描き直す
                if (t + WaterSim.TilesW < WaterSim.TilesW * WaterSim.TilesH) Queue(t + WaterSim.TilesW);
            }
            dirty.Clear();
        }
        int drawn = 0;
        bool created = false;
        for (int i = 0; i < Waiting.Count && drawn < TilesPerFrame; i++)
        {
            var tile = Tiles[Waiting[i]];
            if (_clock - tile.LastDraw < Redraw) continue;
            // 新しいタイルは家具の型抜き (当たり判定を画素ごとに引く) が重いので 1 フレーム 1 枚まで
            if (tile.Px == null) { if (created) continue; created = true; }
            tile.Waiting = false;
            Waiting.RemoveAt(i--);
            Draw(tile);
            drawn++;
        }
    }

    private static void Queue(int t)
    {
        if (!Tiles.TryGetValue(t, out var tile)) Tiles[t] = tile = new Tile { Tx = t % WaterSim.TilesW, Ty = t / WaterSim.TilesW };
        if (!tile.Waiting) { tile.Waiting = true; Waiting.Add(t); }
    }

    // 爆発の瞬間に呼ぶ (WaterLeak から・自分の手元だけ)。r = 水の計算の衝撃と同じ半径
    internal static void AddCrater(Vector2 c, float r)
    {
        if (!WaterSim.Ready || r <= 0f) return;
        Craters.Add(new Crater { X = c.x, Y = c.y, R = r, T0 = _clock });
    }

    private static float CraterHold => (float)WaterSim.Delay / GameClock.Hz;

    // くぼみの掛かるタイルを毎フレーム描き直しに回す (描き直しの間隔は Redraw のまま)。消えた後にもう 1 回
    private static void TickCraters()
    {
        float end = CraterHold + CraterOut;
        float cell = WaterSim.Cell, ts = cell * WaterSim.TileCells;
        Vector2 org = WaterSim.Origin;
        for (int i = Craters.Count - 1; i >= 0; i--)
        {
            var c = Craters[i];
            bool done = _clock - c.T0 > end, fresh = _clock - c.T0 < CraterIn * 2f;
            float rr = c.R * (1f + RimBulge);
            int tx0 = (int)FxMath.Floor((c.X - rr - org.x) / ts), tx1 = (int)FxMath.Floor((c.X + rr - org.x) / ts);
            int ty0 = (int)FxMath.Floor((c.Y - rr - org.y) / ts), ty1 = (int)FxMath.Floor((c.Y + rr - org.y) / ts);
            for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                if (tx < 0 || ty < 0 || tx >= WaterSim.TilesW) continue;
                int t = ty * WaterSim.TilesW + tx;
                if (!Tiles.TryGetValue(t, out var tile)) continue;
                if (fresh) tile.LastDraw = -10f;   // 出始めは間隔を待たずに描く (爆発と同じ頃に見せる)
                if (!tile.Waiting) { tile.Waiting = true; Waiting.Add(t); }
            }
            if (done) Craters.RemoveAt(i);
        }
    }

    // 升の真ん中 (wx, wy) の描く高さに掛ける倍率 (くぼみ = 0 に近い・縁 = 1 を超える)
    private static float CraterScale(float wx, float wy)
    {
        float k = 1f, hold = CraterHold;
        foreach (var c in Craters)
        {
            float age = _clock - c.T0;
            float amt = age < CraterIn ? age / CraterIn : age < hold ? 1f : 1f - (age - hold) / CraterOut;
            if (amt <= 0f) continue;
            float dx = wx - c.X, dy = wy - c.Y, d2 = (dx * dx + dy * dy) / (c.R * c.R);
            if (d2 < 1f) k *= 1f - amt * FxMath.Min(1f, CraterDepth * (1f - d2));   // 真ん中 (半径の半分弱) は乾いた床まで (計算の水のくぼみと同じ広さ)
            else
            {
                float d = FxMath.Sqrt(d2), rim = 1f - FxMath.Abs(d - 1.15f) / 0.3f;
                if (rim > 0f) k *= 1f + RimBulge * amt * rim;
            }
        }
        return k;
    }

    private static unsafe void Draw(Tile t)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int tc = WaterSim.TileCells, w = WaterSim.W, h = WaterSim.H;
        int x0 = t.Tx * tc, y0 = t.Ty * tc;
        // 高さを縁 3 升ぶん広げて写し (光の筋はずらした所を見るので余分に)、1-2-1 でならす
        int dw = tc + 4, rw = tc + 6;
        _raw ??= new int[rw * rw];
        _depth ??= new int[dw * dw];
        _wet ??= new int[dw * dw];
        _closed ??= new bool[rw * rw];
        bool any = false;
        bool crater = Craters.Count > 0;
        float cs = WaterSim.Cell, ox = WaterSim.Origin.x, oy = WaterSim.Origin.y;
        float org0x = ox, org0y = oy;
        for (int y = 0; y < rw; y++)
        for (int x = 0; x < rw; x++)
        {
            int cx = x0 + x - 3, cy = y0 + y - 3;
            bool inGrid = cx >= 1 && cy >= 1 && cx < w - 1 && cy < h - 1;
            int v = inGrid ? WaterSim.Height(cy * w + cx) : 0;
            _closed[y * rw + x] = inGrid && !WaterSim.Open(cy * w + cx);
            // 閉じた升 (壁の際) には、開いた隣が 1 つだけの時にその水を描き込む。壁の中は画素ごとの SolidMap で切るので、
            // 水が壁の際まで届いて見える。開いた隣が 2 つ以上 (升の中を細い壁が通っている) だと、乾いた側まで水を描いてしまうので描かない
            if (v == 0 && cx >= 1 && cy >= 1 && cx < w - 1 && cy < h - 1 && !WaterSim.Open(cy * w + cx))
            {
                int k = cy * w + cx, open = 0, from = 0;
                if (WaterSim.Open(k + 1)) { open++; from = k + 1; }
                if (WaterSim.Open(k - 1)) { open++; from = k - 1; }
                if (WaterSim.Open(k + w)) { open++; from = k + w; }
                if (WaterSim.Open(k - w)) { open++; from = k - w; }
                if (open == 1) v = WaterSim.Height(from);
            }
            if (v > 0 && crater) v = (int)(v * CraterScale(ox + (cx + 0.5f) * cs, oy + (cy + 0.5f) * cs));
            _raw[y * rw + x] = v;
            if (v > 0 && x >= 2 && y >= 2 && x < rw - 2 && y < rw - 2) any = true;
        }
        for (int y = 0; y < dw; y++)
        for (int x = 0; x < dw; x++)
        {
            int c = (y + 1) * rw + x + 1;
            int sum = _raw[c] * 4 + (_raw[c - 1] + _raw[c + 1] + _raw[c - rw] + _raw[c + rw]) * 2
                + _raw[c - rw - 1] + _raw[c - rw + 1] + _raw[c + rw - 1] + _raw[c + rw + 1];
            _depth[y * dw + x] = sum / 16;
            int wet = Wet(_raw[c]);
            if (_closed[c]) wet = Math.Max(wet, WallWet(c, rw));
            _wet[y * dw + x] = wet;
        }
        t.LastDraw = _clock;
        int n0 = tc * Px;
        if (!t.FaceDone)
        {
            t.FaceDone = true;
            var face = new byte[n0 * n0];
            if (FloorMask.Active && FloorMask.FaceHeights(org0x + x0 * cs, org0y + y0 * cs, tc * cs, n0, face)) t.Face = face;
        }
        bool faceWet = t.Face != null && FaceWet(t, x0, y0, n0);
        if (!any && !faceWet)
        {
            if (!t.Empty && t.Sr) Enable(t, false);
            t.Empty = true;
            return;
        }
        Ensure(t);

        int n = tc * Px;
        float cell = WaterSim.Cell;
        if (t.Floor != null && t.FloorOn == FloorMask.Off)
        {
            SetFloor(t, !FloorMask.Off);
            t.FurnVersion = -1;
        }
        if (!t.FloorOn && t.FurnVersion != _furnVersion)
        {
            t.FurnVersion = _furnVersion;
            t.Furn = FurnitureMask(WaterSim.Origin.x + t.Tx * tc * cell, WaterSim.Origin.y + t.Ty * tc * cell, tc * cell);
        }
        int sw = SolidMap.W;
        float sppu = SolidMap.Ppu;
        var sorg = SolidMap.Origin;
        var org = WaterSim.Origin;
        float edge = (float)tc / (n - 1);
        for (int py = 0; py < n; py++)
        for (int px = 0; px < n; px++)
        {
            int i = (py * n + px) * 4;
            // 画素 0 と n - 1 はタイルの境目ちょうど (隣のタイルの端の画素と同じ所)。シェーダは端の画素の真ん中から引く
            float u = px * edge, v = py * edge;
            float d = Sample(_depth, u, v);
            float wet = Sample(_wet, u, v) * (1f / WetRamp);
            float wx = org.x + (x0 + u) * cell, wy = org.y + (y0 + v) * cell;
            int sx = (int)((wx - sorg.x) * sppu), sy = (int)((wy - sorg.y) * sppu);
            // 壁の際の溝を埋めるために広げた分も、絵の無い空の上には描かない。
            // 床マスクで切る時は壁と家具をマスクが絵の形で切るので、歩ける所の升 (1/16) では切らない (縁に升の段が出る)
            bool vis = (t.FloorOn || NearOpen(sx, sy, sw) && (t.Furn == null || !t.Furn[py * n + px])) && !SolidMap.BareSky(wx, wy);
            t.Px[i] = (byte)Math.Min(255f, d * (255f / DeepScale));
            t.Px[i + 1] = vis ? (byte)255 : (byte)0;
            t.Px[i + 2] = (byte)Math.Min(255f, wet * 255f + 0.5f);
            t.Px[i + 3] = 0;
        }
        if (t.Face != null)
        {
            WriteFace(t, x0, y0, n);
            SpreadFace(t, n);
        }
        fixed (byte* b = t.Px) t.Tex.LoadRawTextureData((IntPtr)b, t.Px.Length);
        t.Tex.Apply(false, false);
        if (t.Empty) { Enable(t, !Hidden); t.Empty = false; }
        Drawn++;
        LastDrawMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    // 壁の面の画素 (wx, 床からの高さ z の所) の真下の床の水の深さ (単位)。横は升の真ん中の間を線形に引く (升ごとの段を水面の線に出さない)
    private static float BaseDepth(float wx, float wyBase)
    {
        float cell = WaterSim.Cell;
        var org = WaterSim.Origin;
        float fx = (wx - org.x) / cell - 0.5f;
        int cx = (int)MathF.Floor(fx), cy = (int)MathF.Floor((wyBase - org.y) / cell);
        float f = fx - cx;
        return (CellBase(cx, cy) * (1f - f) + CellBase(cx + 1, cy) * f) * (1f / WaterSim.Full);
    }

    private static int CellBase(int cx, int cy)
    {
        int w = WaterSim.W;
        if (cx < 1 || cx >= w - 1) return 0;
        for (int d = 0; d <= FaceBaseScan; d++)
        {
            int y = cy - d;
            if (y < 1 || y >= WaterSim.H - 1) return 0;
            int k = y * w + cx;
            if (WaterSim.Open(k)) return WaterSim.Height(k);
        }
        return 0;
    }

    // 壁の面に水が掛かっているか (タイルを出すか)
    private static bool FaceWet(Tile t, int x0, int y0, int n)
    {
        float cell = WaterSim.Cell, edge = (float)WaterSim.TileCells / (n - 1);
        var org = WaterSim.Origin;
        for (int py = 0; py < n; py += 2)
        for (int px = 0; px < n; px += 2)
        {
            byte z = t.Face[py * n + px];
            if (z > FloorMask.FaceTop) continue;
            float wx = org.x + (x0 + px * edge) * cell, wy = org.y + (y0 + py * edge) * cell, h = z * (1f / FloorMask.FaceUnit);
            if (BaseDepth(wx, wy - h) > h) return true;
        }
        return false;
    }

    // A に壁の面の上の水の厚さを書く。
    // 水が面の上端より高い列 (低い家具・低い壁を越えた) では、面の上の画素にも同じ A を続け、床でなければ G (見せてよいか) を 0 にする。
    // A を 0 にすると面の終わりで A が 128 を横切り、シェーダが乾いた縁を水面の線 (明るい縁と泡) と取り違える
    private const int OvertopPx = 3;

    private static void WriteFace(Tile t, int x0, int y0, int n)
    {
        float cell = WaterSim.Cell, edge = (float)WaterSim.TileCells / (n - 1);
        var org = WaterSim.Origin;
        for (int px = 0; px < n; px++)
        {
            byte topA = 0;
            int carry = 0;
            for (int py = 0; py < n; py++)
            {
                int j = py * n + px, i = j * 4;
                byte z = t.Face[j];
                if (z <= FloorMask.FaceTop)
                {
                    float wx = org.x + (x0 + px * edge) * cell, wy = org.y + (y0 + py * edge) * cell, h = z * (1f / FloorMask.FaceUnit);
                    byte a = (byte)FxMath.Clamp(128f + (BaseDepth(wx, wy - h) - h) * FaceScale, 0f, 255f);
                    t.Px[i + 3] = a;
                    topA = a;
                    carry = a > 128 ? OvertopPx : 0;
                }
                else if (carry > 0)
                {
                    // 面のすぐ上が床 (ベッドの奥の床) なら床の水はそのまま見せる (A は床では使わない)
                    if (z == 255) t.Px[i + 1] = 0;
                    t.Px[i + 3] = topA;
                    carry--;
                }
                else carry = 0;
            }
        }
    }

    // A を面の外へ SpreadPx 画素にじませる (横 → 縦の 2 回で四角く広げる・値は近くの面の大きい方)。
    // 面のすぐ外の画素の A が 0 だと、双線形で引いた A が面の縁で必ず 128 を横切り、沈んだベッドや壁の輪郭が
    // 水面の線 (明るい縁と泡) としてなぞられる。床の画素は A を持っても床として描くので見た目は変わらない。
    // 床でない画素は G を 0 にして、面の縁はこれまでどおり G で切る
    private const int SpreadPx = 3;
    private static byte[] _spread;

    private static void SpreadFace(Tile t, int n)
    {
        var px = t.Px;
        var face = t.Face;
        if (_spread == null || _spread.Length < n * n) _spread = new byte[n * n];
        var tmp = _spread;
        for (int y = 0; y < n; y++)
        {
            int row = y * n;
            for (int x = 0; x < n; x++)
            {
                byte a = px[(row + x) * 4 + 3];
                if (a == 0)
                {
                    int lo = Math.Max(0, x - SpreadPx), hi = Math.Min(n - 1, x + SpreadPx);
                    for (int k = lo; k <= hi; k++) { byte b = px[(row + k) * 4 + 3]; if (b > a) a = b; }
                }
                tmp[row + x] = a;
            }
        }
        for (int y = 0; y < n; y++)
        {
            int row = y * n;
            for (int x = 0; x < n; x++)
            {
                int j = row + x, i = j * 4;
                if (px[i + 3] != 0) continue;
                byte a = tmp[j];
                int lo = Math.Max(0, y - SpreadPx), hi = Math.Min(n - 1, y + SpreadPx);
                for (int k = lo; k <= hi; k++) { byte b = tmp[k * n + x]; if (b > a) a = b; }
                if (a == 0) continue;
                px[i + 3] = a;
                if (face[j] == 255) px[i + 1] = 0;
            }
        }
    }

    // 水を見せてよい画素か: 歩ける所か、左右 Undilate 升・下 UndilateDown 升・上 UndilateUp 升以内に歩ける所がある所。
    // SolidMap は継ぎ目を塞ぐために壁の線を両側へ太らせてあり、そのままだと壁の手前に乾いた帯 (溝) が残る。
    // 太らせた幅までしか戻さないので、元の壁の線は越えない (向こう側の部屋へは描かない)。
    // 上の壁は当たり判定の線が床の絵の際より手前の所 (ミラの廊下) と、ロッカーのように絵が線より手前へ出ている所があり、
    // 上へ 2 升戻すとロッカーの下に水が掛かるので 1 升だけ戻す
    private const int Undilate = 2;
    private const int UndilateDown = 2;
    private const int UndilateUp = 1;

    private static bool NearOpen(int sx, int sy, int sw)
    {
        int k = sy * sw + sx;
        if (SolidMap.OpenCell(k)) return true;
        if (sx >= Undilate && sx < sw - Undilate)
            for (int d = 1; d <= Undilate; d++)
                if (SolidMap.OpenCell(k + d) || SolidMap.OpenCell(k - d)) return true;
        // 下に歩ける所がある = 上の壁の手前の帯 / 上に歩ける所がある = 下の壁の手前の帯
        if (sy >= UndilateUp)
            for (int d = 1; d <= UndilateUp; d++)
                if (SolidMap.OpenCell(k - d * sw)) return true;
        if (sy < SolidMap.H - UndilateDown)
            for (int d = 1; d <= UndilateDown; d++)
                if (SolidMap.OpenCell(k + d * sw)) return true;
        return false;
    }

    // 閉じた升 (壁の中) の濡れ具合を隣の開いた升から借りる (いちばん濡れた隣)。壁の中は見せない画素なので、
    // 縁の線を壁の奥へ追いやって、見える床の上に壁に沿った縁 (濃い輪郭) を出さない。
    // 細い壁で向こうが乾いた部屋でも、ならした縁が届くのは壁の升の真ん中から 0.6 升までで、絵の壁の黒い帯 (床マスクの外) に収まる
    private static int WallWet(int c, int rw)
    {
        int a = Math.Max(Math.Max(WetOpen(c - 1), WetOpen(c + 1)), Math.Max(WetOpen(c - rw), WetOpen(c + rw)));
        int b = Math.Max(Math.Max(WetOpen(c - rw - 1), WetOpen(c - rw + 1)), Math.Max(WetOpen(c + rw - 1), WetOpen(c + rw + 1)));
        return Math.Max(a, b);
    }

    private static int Wet(int h) => h <= WetMin ? 0 : Math.Min(h - WetMin, WetRamp);
    private static int WetOpen(int i) => _closed[i] ? 0 : Wet(_raw[i]);

    // タイルの中の位置 (cu, cv: 升の単位・タイルの左下 = 0) での高さ (升の真ん中の値を 3 次の B スプラインで)。
    // 双線形だと升の境目で傾きが折れ、水の縁の線が升の形の階段になる。B スプラインは境目でも曲がり方まで続くので縁が丸い線になる
    private static float Sample(int[] src, float cu, float cv)
    {
        int dw = WaterSim.TileCells + 4;
        float u = cu - 0.5f + 2f, v = cv - 0.5f + 2f;
        int u0 = (int)MathF.Floor(u), v0 = (int)MathF.Floor(v);
        if (u0 < 1 || v0 < 1 || u0 + 2 >= dw || v0 + 2 >= dw) return 0f;
        float fu = u - u0, fv = v - v0;
        Weights(fu, out float a0, out float a1, out float a2, out float a3);
        Weights(fv, out float b0, out float b1, out float b2, out float b3);
        int r = (v0 - 1) * dw + u0 - 1;
        return b0 * Row(src, r, a0, a1, a2, a3) + b1 * Row(src, r + dw, a0, a1, a2, a3)
             + b2 * Row(src, r + dw * 2, a0, a1, a2, a3) + b3 * Row(src, r + dw * 3, a0, a1, a2, a3);
    }

    private static float Row(int[] src, int i, float a0, float a1, float a2, float a3)
        => a0 * src[i] + a1 * src[i + 1] + a2 * src[i + 2] + a3 * src[i + 3];

    private static void Weights(float t, out float w0, out float w1, out float w2, out float w3)
    {
        float t2 = t * t, t3 = t2 * t, s = 1f - t;
        w0 = s * s * s * (1f / 6f);
        w1 = (3f * t3 - 6f * t2 + 4f) * (1f / 6f);
        w2 = (-3f * t3 + 3f * t2 + 3f * t + 1f) * (1f / 6f);
        w3 = t3 * (1f / 6f);
    }

    private static void SetFloor(Tile t, bool on)
    {
        t.FloorOn = on;
        var mpb = t.Block ??= new MaterialPropertyBlock();
        // SpriteRenderer にブロックを付けるとスプライトの絵が外れるので、絵もブロックで渡す
        mpb.SetTexture(MainTexId, t.Tex);
        mpb.SetTexture(FloorTexId, t.Floor);
        mpb.SetFloat(FloorOnId, on ? 1f : 0f);
        t.Sr.SetPropertyBlock(mpb);
        if (t.Shadow) t.Shadow.SetPropertyBlock(mpb);
    }

    private static readonly int MainTexId = Shader.PropertyToID("_MainTex"), FloorTexId = Shader.PropertyToID("_FloorMask"), FloorOnId = Shader.PropertyToID("_FloorOn");

    private const float DeckZ = -0.001f; // エアシップの警備室の下のデッキの床の絵の z (手すりは -0.1 で水より手前)

    private static unsafe void Ensure(Tile t)
    {
        if (t.Go) return;
        int tc = WaterSim.TileCells, n = tc * Px;
        float cell = WaterSim.Cell;
        var org = WaterSim.Origin;
        t.Px = new byte[n * n * 4];
        t.Tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "MrpWater", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        t.Sp = Sprite.Create(t.Tex, new Rect(0, 0, n, n), Vector2.zero, Px / cell, 0, SpriteMeshType.FullRect);
        t.Sp.name = "MrpWater";
        t.Go = new GameObject("MrpWater") { layer = 0 };
        t.Sr = t.Go.AddComponent<SpriteRenderer>();
        t.Sr.sprite = t.Sp;
        t.Sr.sharedMaterial = MrpBundle.WaterMaterial;
        float wx = org.x + t.Tx * tc * cell, wy = org.y + t.Ty * tc * cell;
        // タイルは部屋の境をまたぐので、4 隅と真ん中で部屋の絵を引いて一番手前より前に置く
        // (真ん中だけだと、隣の部屋の絵のほうが手前の所で水がその下に隠れて四角く欠けた)
        float size = tc * cell, front = float.MaxValue;
        for (int k = 0; k < 5; k++)
        {
            float fx = k == 4 ? 0.5f : (k & 1), fy = k == 4 ? 0.5f : (k >> 1);
            float px = wx + size * (0.05f + 0.9f * fx), py = wy + size * (0.05f + 0.9f * fy);
            front = Math.Min(front, DamageMap.FrontZ(new Vector2(px, py)));
            // 屋外のデッキの床の絵はクルーと同じ奥行きにあり、部屋の絵の z では床の下に隠れる
            if (WaterSim.OnDeck(px, py)) front = Math.Min(front, DeckZ);
        }
        float zs = DamageMap.ZScale(front);
        // 床の上・足跡 (−0.001) と波紋より奥
        t.Go.transform.position = FxMath.V3(wx, wy, front - 0.0004f * zs);
        t.Shadow = ShadowView.Copy(t.Go, t.Sp, ShadowView.Water, FxMath.Rgba(1f, 1f, 1f, 1f), -0.002f); // 火の写しより奥
        if (FloorMask.Active)
        {
            // 床マスクがあれば水の縁は部屋の絵の床の画素で切る (家具の型抜きも要らない)
            MakeFloor(t, wx, wy, size);
            return;
        }
        t.Furn = FurnitureMask(wx, wy, tc * cell);
        t.FurnVersion = _furnVersion;
    }

    private static unsafe void MakeFloor(Tile t, float wx, float wy, float size)
    {
        int m = (int)MathF.Round(size * FloorMask.Ppu);
        var buf = new byte[m * m];
        FloorMask.Fill(wx, wy, size, m, buf);
        t.Floor = new Texture2D(m, m, TextureFormat.R8, false) { name = "MrpWaterFloor", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        fixed (byte* b = buf) t.Floor.LoadRawTextureData((IntPtr)b, buf.Length);
        t.Floor.Apply(false, true);
        SetFloor(t, !FloorMask.Off);
    }

    // 床マスクの r の範囲が変わった (持ち上げた家具の跡が床になった): 掛かるタイルのマスクを作り直す。
    // マスクは CPU 側の写しを捨てて作ってあるので、書き換えずに作り直す
    internal static void FloorChanged(Rect r)
    {
        if (!FloorMask.Active) return;
        float size = WaterSim.TileCells * WaterSim.Cell;
        var org = WaterSim.Origin;
        foreach (var t in Tiles.Values)
        {
            if (!t.Floor || !t.Sr) continue;
            float x0 = org.x + t.Tx * size, y0 = org.y + t.Ty * size;
            if (r.xMax <= x0 || r.xMin >= x0 + size || r.yMax <= y0 || r.yMin >= y0 + size) continue;
            UnityEngine.Object.Destroy(t.Floor);
            MakeFloor(t, x0, y0, size);
        }
    }

    // テスト用: 床マスクの使う / 使わないを切り替えた。今あるタイルを全部その方式で描き直す
    internal static void FloorMaskToggled()
    {
        foreach (var kv in Tiles)
        {
            var t = kv.Value;
            if (t.Floor && t.Sr) SetFloor(t, !FloorMask.Off);
            t.FurnVersion = -1;
            if (t.Empty || t.Waiting) continue;
            t.Waiting = true;
            Waiting.Add(kv.Key);
        }
    }

    // 家具 (部屋の絵から持ち上げた物) が a から b へ動いて止まった: 型抜きを作り直す (どのタイルも次に描く時に)。
    // すぐ描き直すのは、元の場所か止まった所から r 以内に掛かるタイルだけ
    internal static void FurnitureMoved(Vector2 a, Vector2 b, float r)
    {
        _furnVersion++;
        // 床マスクで切っているタイルは、マスクに家具の今いる所を抜き直す
        FloorChanged(Rect.MinMaxRect(a.x - r, a.y - r, a.x + r, a.y + r));
        FloorChanged(Rect.MinMaxRect(b.x - r, b.y - r, b.x + r, b.y + r));
        float size = WaterSim.TileCells * WaterSim.Cell;
        var org = WaterSim.Origin;
        foreach (var kv in Tiles)
        {
            var t = kv.Value;
            if (t.Empty || t.Waiting) continue;
            float x0 = org.x + t.Tx * size, y0 = org.y + t.Ty * size;
            if (!Near(a) && !Near(b)) continue;
            t.Waiting = true;
            Waiting.Add(kv.Key);

            bool Near(Vector2 c) => c.x + r > x0 && c.x - r < x0 + size && c.y + r > y0 && c.y - r < y0 + size;
        }
    }

    // 部屋の絵に描き込まれた家具 (机・ベッド) の範囲。どちらも絵は当たり判定の中に収まっているので、当たり判定の内側だけ隠す
    private static bool[] FurnitureMask(float wx, float wy, float size)
    {
        var cols = new List<Collider2D>();
        DamageMap.FurnitureColliders(new Vector2(wx + size * 0.5f, wy + size * 0.5f), size * 0.75f, cols);
        if (cols.Count == 0) return null;
        int m = WaterSim.TileCells * FurnSub;
        var mask = new bool[m * m];
        float step = size / (m - 1); // 端の画素はタイルの境目ちょうど (水の絵と同じ並び)
        foreach (var col in cols)
        {
            var b = col.bounds;
            float bx0 = b.min.x, bx1 = b.max.x, by0 = b.min.y, by1 = b.max.y;
            for (int y = 0; y < m; y++)
            {
                float qy = wy + y * step;
                if (qy < by0 || qy > by1) continue;
                for (int x = 0; x < m; x++)
                {
                    float qx = wx + x * step;
                    if (qx < bx0 || qx > bx1 || mask[y * m + x]) continue;
                    if (Inside(col, qx, qy))
                        mask[y * m + x] = true;
                }
            }
        }
        return mask;
    }

    // 当たり判定の内側か (縁を FurnitureInset だけ削る: 当たり判定は絵より少し大きく、そのまま隠すと家具の際に乾いた線が残った)
    private const float FurnitureInset = 0.025f;
    internal static bool Inside(Collider2D col, float x, float y) =>
        col.OverlapPoint(new Vector2(x, y))
        && col.OverlapPoint(new Vector2(x + FurnitureInset, y)) && col.OverlapPoint(new Vector2(x - FurnitureInset, y))
        && col.OverlapPoint(new Vector2(x, y + FurnitureInset)) && col.OverlapPoint(new Vector2(x, y - FurnitureInset));

    internal static void Clear()
    {
        foreach (var t in Tiles.Values)
        {
            if (t.Go) UnityEngine.Object.Destroy(t.Go);
            if (t.Sp) UnityEngine.Object.Destroy(t.Sp);
            if (t.Tex) UnityEngine.Object.Destroy(t.Tex);
            if (t.Floor) UnityEngine.Object.Destroy(t.Floor);
        }
        Tiles.Clear();
        Waiting.Clear();
        Craters.Clear();
        _clock = 0f;
    }

    internal static string Describe() => $"tiles={Tiles.Count} waiting={Waiting.Count} drawn={Drawn} drawMs={LastDrawMs:0.00}";
}
