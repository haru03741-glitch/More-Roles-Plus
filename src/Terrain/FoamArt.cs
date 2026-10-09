using System;
using System.Collections.Generic;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 外壁の穴の補修フォームの見た目。口の線の両端から吹き付けが中央へ進み、届いた所から泡が膨らんで 1 つの塊になる。
// 喉を切った船 (スケルド) では泡を部屋の絵と船体の絵の奥に置き、口の線から宇宙まで喉をまるごと埋める。残っている絵が床と壁を隠すので、
// 泡は切れた所 (壁の正面の切れ目と船体の上面の穴) からだけ見え、切り口の線が泡の縁にかぶさる。宇宙の側は丸く盛り上がる。
// それ以外の船では口の線より外の栓 (北向きは壁の正面 FaceHeight の上から) を一番手前に描く。
// 粒を並べるのでなく、泡の玉の場 (メタボール) から 1 枚の絵を描く: 高さから法線を出して左上から光を当て、
// 濡れている間はクリーム色に鋭い艶、固まると黄土色にくすんで艶が消え、表面の気泡の穴が見える。縁は本編の絵柄に合わせて濃い輪郭線。
// ふさがる時刻は Decompression の刻み (全員同じ) で決まり、見た目はそこから手元の時計で滑らかに進める。物がふさいだ口には出さない。
// 絵を描き直すのは膨らんで固まるまでの数秒だけ (その後は止める)
internal static class FoamArt
{
    private const int Ppu = 64;                // 絵の細かさ (1 単位あたりの画素)
    private const float CraterReachMul = 1.6f;  // 広い穴の栓は玉の影響を半径の 1.6 倍で打ち切る (2 倍との差は場の 5% 未満・画素の計算は 0.64 倍)
    private const float Pad = 0.15f;           // 玉は口の両端の外にも置く (角まで埋めてから切る)
    private const float SideBleed = 0.03f;     // 両脇は喉の切り口の輪郭線に少しかぶせる
    private const float SideBleedBehind = 0.1f; // 船体の絵の裏に置く時は切り口のギザギザに少し入る (縁は船体の絵が隠す・それより先は宇宙に出る)
    private const float BehindHull = 0.5f;     // 船体の絵より奥に置き、切り口の縁が泡にかぶさるようにする
    private const float PlugDepth = 0.55f;     // 栓の厚み (口の線から外へ)
    private const float PlugMin = 0.2f;        // 船体の上面が薄くてもこの厚みは詰める
    private const float Bulge = 0.15f;         // 宇宙の側へ盛り上がってよい分
    private const float FloorTuck = 0.1f;      // 船体の裏に置く時は口の線より床の側へこれだけ (部屋の絵の裏に隠れて隙間を作らない)
    private const float RowStep = 0.14f;       // 玉の列の間隔
    private const float FaceHeight = 0.72f;    // 北向きの壁の正面の高さ (スケルド食堂の上で実測)
    private const float Spacing = 0.13f;       // 玉の間隔
    private const float SprayShare = 0.55f;    // 端から中央に吹き付けが届くまで (FoamTime に対する割合)
    private const float GrowShare = 0.3f;      // 1 つの玉が膨らみ切るまで
    private const float CureFrom = 0.75f, CureTo = 1.6f; // 乾き始めと固まり終わり
    private const int MaxLen = 8;              // 絵の長さの上限 (単位)

    private sealed class Ball
    {
        public float U, V, R, Start;
        public bool Hidden; // 影響の届く所が全部船体の絵の裏 (描かない)
    }

    private sealed class Plug
    {
        public readonly List<Ball> Balls = new();
        public Texture2D Tex;
        public GameObject Go;
        public byte[] Px;
        public float[] F;
        public byte[] Pores;
        public int W, H;
        public float U0, V0;
        public float Len, VMin, VMax, Thick, Bleed0, Bleed1; // 切る範囲: VMin ≤ v ≤ VMax・−Bleed0 ≤ u ≤ Len + Bleed1
        public bool SoftInner; // 内側 (v = VMin) の縁に輪郭線を付けない (角で隣の栓に重なる所に線が出る)
        public float Reach = 2f; // 玉の影響が届く距離 (半径の倍数)
        public bool Final;     // 固まり終わりの描き直しを済ませた
        public byte[] Vis;     // 穴から見える画素 (null = 全部)。船体の板の裏に広く置いた栓 (エアシップ) で隠れる所を描かない
    }

    private sealed class Foam
    {
        public readonly List<Plug> Plugs = new();
        public float T0;                       // 手元の時計での吹き付け始め
        public bool Alternate;                 // 栓が重い (エアシップの広い穴) ので 1 フレームに 1 枚ずつ描き直す
        public bool Done;
    }

    private static readonly Dictionary<Decompression.Breach, Foam> Foams = new();
    private static GameObject _root;
    private static int _shipGen = -1, _failedGen = -2;

    public static void Tick()
    {
        if (GameClock.ShipGen != _shipGen)
        {
            _shipGen = GameClock.ShipGen;
            Foams.Clear();
            _root = null;
        }
        if (!Decompression.Running)
        {
            if (Foams.Count > 0 && !GameClock.ShipAlive) Foams.Clear();
            return;
        }
        if (_failedGen == _shipGen) return;
        try { TickCore(); }
        catch (Exception e)
        {
            // 同じ船では作り直しても同じ所で落ちるので、この試合の泡は止める
            Plugin.Logger.LogError($"[FoamArt] tick: {e}");
            _failedGen = _shipGen;
        }
    }

    private static void TickCore()
    {
        var list = Decompression.OpenedBreaches;
        float now = -1f;
        for (int i = 0; i < list.Count; i++)
        {
            var br = list[i];
            if (br.ByProp) continue;
            if (!Foams.TryGetValue(br, out var f))
            {
                int age = Decompression.Step - br.Start - Decompression.FoamDelay;
                if (age < 0) continue;
                if (now < 0f) now = Time.time;
                f = Build(br, now - age / (float)GameClock.Hz);
                Foams[br] = f;
            }
            if (f.Done) continue;
            if (now < 0f) now = Time.time;
            float p = (now - f.T0) / (Decompression.FoamTime / (float)GameClock.Hz);
            bool last = p >= CureTo;
            // ふさがった後の乾いていく間は変化が小さいので 4 フレームに 1 回
            int frame = Time.frameCount;
            if (p >= 1f && !last && (frame & 3) != 0) continue;
            if (f.Alternate && !last && f.Plugs.Count > 1)
                Draw(f.Plugs[(p >= 1f ? frame >> 2 : frame) % f.Plugs.Count], p, false);
            else if (f.Alternate && last)
            {
                // 固まり終わりの描き直しも 1 フレームに 1 枚 (全部まとめると 1 フレームに数枚分の重さが乗る)
                Plug next = null;
                foreach (var plug in f.Plugs) if (!plug.Final) { next = plug; break; }
                if (next != null) { Draw(next, p, true); next.Final = true; }
                foreach (var plug in f.Plugs) if (!plug.Final) { last = false; break; }
            }
            else
                foreach (var plug in f.Plugs) Draw(plug, p, last);
            if (last)
            {
                f.Done = true;
                // 固まった泡を影の中の焼いた絵にも描き込む
                foreach (var plug in f.Plugs)
                {
                    if (!plug.Go) continue;
                    var b = plug.Go.GetComponent<SpriteRenderer>().bounds;
                    var e = b.extents;
                    ShadowPatch.MarkDirtyLater(FxMath.V2(b.center.x, b.center.y), MathF.Max(e.x, e.y) + 0.2f);
                }
            }
        }
    }

    private static Foam Build(Decompression.Breach br, float t0)
    {
        var f = new Foam { T0 = t0 };
        if (!EnsureRoot()) { f.Done = true; return f; }
        var rnd = new System.Random(br.Start * 7919 + br.Lines.Count);
        foreach (var (a, b) in br.Lines)
        {
            float dx = b.x - a.x, dy = b.y - a.y, len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 0.05f || len > MaxLen) continue;
            float ux = dx / len, uy = dy / len;
            // 宇宙・空の側を向く法線 (口の中点から床を通らずに外へ出る側)
            float nx = -uy, ny = ux;
            var mid = new Vector2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f);
            bool outP = SolidMap.SkyAhead(mid, new Vector2(nx, ny), TerrainDamage.BreachReach);
            bool outQ = SolidMap.SkyAhead(mid, new Vector2(-nx, -ny), TerrainDamage.BreachReach);
            bool flip = !outP && outQ;
            // 北向きほど壁の正面が画面の上へ立つので、その上 (船体の上面) から詰める。厚みは宇宙まで (床が先なら床の手前まで)
            float ox = flip ? -nx : nx, oy = flip ? -ny : ny;
            float depth = SolidMap.SkyDistance(mid.x, mid.y, ox, oy, TerrainDamage.HullReach + 1f);
            bool behind = !float.IsNaN(HullThroat.BackZ());
            bool crater = behind && HullThroat.IsCrater; // エアシップ: 船体の板に開けた浅い穴を奥まで埋める (板の裏なので穴の外へはみ出しても見えない)
            float vmin = behind ? -FloorTuck : FaceHeight * MathF.Max(0f, oy);
            // 広い穴は口に沿っていちばん深い所に合わせて開けてあるので、中点で床に当たっても止めない (はみ出しは隣の部屋の床の絵の裏)
            float reach = crater ? HullThroat.CraterReach : depth < 0f ? -depth - 0.2f : depth > 0f ? depth : vmin + PlugDepth;
            float thick = behind ? MathF.Max(PlugMin, reach - vmin) : Math.Clamp(reach - vmin, PlugMin, PlugDepth);
            float vmax = vmin + thick + (depth < 0f ? 0f : Bulge);
            float side = crater ? HullThroat.CraterSide : behind ? SideBleedBehind : SideBleed;
            // 折れた口の角では隣の線の穴と斜めに広がった所がどちらの栓にも入らないので、角の側だけ穴の奥行きまで延ばす
            float bleed0 = crater && JoinsOther(br, a, b, a) ? HullThroat.CraterReach : side;
            float bleed1 = crater && JoinsOther(br, a, b, b) ? HullThroat.CraterReach : side;
            var plug = MakePlug(rnd, a, ux, uy, flip, len, vmin, thick, vmax, behind, bleed0, bleed1);
            plug.SoftInner = crater;
            if (crater)
            {
                plug.Reach = CraterReachMul;
                f.Alternate = true;
                Cull(plug, a, ux, uy, flip);
            }
            f.Plugs.Add(plug);
        }
        Plugin.Logger.LogInfo($"[FoamArt] foam start={br.Start} plugs={f.Plugs.Count} lines={br.Lines.Count}");
        return f;
    }

    // 損傷マスクで穴の開いた所 (と縁の少し外) だけを見える画素にし (喉の外でも爆発で船体の板が抜けた所は空が見える)、見える画素に影響しない玉を外す。作る時に 1 回だけ
    private static void Cull(Plug pl, Vector2 a, float ux, float uy, bool flip)
    {
        int w = pl.W, h = pl.H;
        float vx = flip ? uy : -uy, vy = flip ? -ux : ux; // 絵の v 軸の世界での向き (flip は縦を裏返して置く)
        const float inv = 1f / Ppu;
        float ax = a.x, ay = a.y;
        var hole = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            float v = pl.V0 + (y + 0.5f) * inv;
            for (int x = 0; x < w; x++)
            {
                float u = pl.U0 + (x + 0.5f) * inv;
                if (DamageMap.HoleByteAt(ax + ux * u + vx * v, ay + uy * u + vy * v) > 5) hole[y * w + x] = 1;
            }
        }
        // 部屋の絵の穴の縁はマスクの外までギザギザに抜ける (1 升 = 1/16 + 縁の揺らぎ) ので、その分と輪郭線・落ち影の分だけ広げる (縦横に分けた最大値)
        const int Grow = 16;
        var tmp = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (hole[y * w + x] == 0) continue;
                for (int k = Math.Max(0, x - Grow); k <= Math.Min(w - 1, x + Grow); k++) tmp[y * w + k] = 1;
            }
        var vis = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (tmp[y * w + x] == 0) continue;
                for (int k = Math.Max(0, y - Grow); k <= Math.Min(h - 1, y + Grow); k++) vis[k * w + x] = 1;
            }
        pl.Vis = vis;
        // 見える画素の累積和で、玉の届く四角に見える画素が 1 つも無ければ外す
        var sum = new int[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            int row = 0;
            for (int x = 0; x < w; x++)
            {
                row += vis[y * w + x];
                sum[(y + 1) * (w + 1) + x + 1] = sum[y * (w + 1) + x + 1] + row;
            }
        }
        int hidden = 0;
        foreach (var bl in pl.Balls)
        {
            float reach = bl.R * 1.15f * pl.Reach;
            int x0 = Math.Max(0, (int)((bl.U - reach - pl.U0) * Ppu)), x1 = Math.Min(w, (int)((bl.U + reach - pl.U0) * Ppu) + 2);
            int y0 = Math.Max(0, (int)((bl.V - reach - pl.V0) * Ppu)), y1 = Math.Min(h, (int)((bl.V + reach - pl.V0) * Ppu) + 2);
            if (x1 <= x0 || y1 <= y0
                || sum[y1 * (w + 1) + x1] - sum[y0 * (w + 1) + x1] - sum[y1 * (w + 1) + x0] + sum[y0 * (w + 1) + x0] == 0)
            { bl.Hidden = true; hidden++; }
        }
        Plugin.Logger.LogInfo($"[FoamArt] cull {w}x{h} balls={pl.Balls.Count} hidden={hidden}");
    }

    // 口の線 a-b の端 end が、同じ口の別の線の端とつながっているか
    private static bool JoinsOther(Decompression.Breach br, Vector2 a, Vector2 b, Vector2 end)
    {
        foreach (var (p, q) in br.Lines)
        {
            if (p == a && q == b) continue;
            if ((p - end).sqrMagnitude < 0.0025f || (q - end).sqrMagnitude < 0.0025f) return true;
        }
        return false;
    }

    private static Plug MakePlug(System.Random rnd, Vector2 a, float ux, float uy, bool flip, float len, float vmin, float thick, float vmax, bool behind, float bleed0, float bleed1)
    {
        var pl = new Plug { Len = len, VMin = vmin, VMax = vmax, Thick = thick, Bleed0 = bleed0, Bleed1 = bleed1 };
        // 玉: 栓の内側の面から外へ RowStep ごとの列 + 宇宙の側の面を盛り上げる小さな玉 (栓の厚みに合わせて詰める)
        float body = thick - 0.1f;
        int rows = Math.Max(3, (int)MathF.Ceiling(body / RowStep) + 1);
        for (int i = 0; i < rows; i++) AddRow(pl, rnd, len, vmin + 0.04f + body * i / (rows - 1) * 0.8f, i == 0 ? 0.17f : 0.16f, 1f);
        AddRow(pl, rnd, len, vmin + 0.04f + body * 0.82f, 0.09f, 0.6f);

        // 絵の範囲 = 玉が膨らみ切った大きさ (1.1 倍) が収まる所を切る範囲で詰めた所 + 落ち影の分
        float u0 = 0f, u1 = len, v0 = 0f, v1 = 0f;
        foreach (var bl in pl.Balls)
        {
            float r = bl.R * 1.15f;
            u0 = MathF.Min(u0, bl.U - r); u1 = MathF.Max(u1, bl.U + r);
            v0 = MathF.Min(v0, bl.V - r); v1 = MathF.Max(v1, bl.V + r);
        }
        u0 = MathF.Max(u0, -pl.Bleed0); u1 = MathF.Min(u1, len + pl.Bleed1);
        v0 = MathF.Max(v0, vmin); v1 = MathF.Min(v1, vmax);
        pl.U0 = u0 - 0.08f; pl.V0 = v0 - 0.08f;
        pl.W = (int)MathF.Ceiling((u1 - u0 + 0.16f) * Ppu);
        pl.H = (int)MathF.Ceiling((v1 - v0 + 0.16f) * Ppu);
        pl.Px = new byte[pl.W * pl.H * 4];
        pl.F = new float[pl.W * pl.H];
        // 表面の気泡の穴 (固まると見える): 半径 0.6〜1.4 画素の柔らかい丸をまばらに
        pl.Pores = new byte[pl.W * pl.H];
        int pores = pl.W * pl.H / 70;
        for (int i = 0; i < pores; i++)
        {
            float cx = (float)rnd.NextDouble() * pl.W, cy = (float)rnd.NextDouble() * pl.H;
            float r = 0.6f + 0.8f * (float)rnd.NextDouble(), dark = 0.5f + 0.5f * (float)rnd.NextDouble();
            for (int y = Math.Max(0, (int)(cy - r - 1)); y < Math.Min(pl.H, (int)(cy + r + 2)); y++)
            for (int x = Math.Max(0, (int)(cx - r - 1)); x < Math.Min(pl.W, (int)(cx + r + 2)); x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                float pa = Math.Clamp(r + 0.5f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f) * dark;
                int k = y * pl.W + x;
                pl.Pores[k] = (byte)Math.Max(pl.Pores[k], (int)(pa * 255f));
            }
        }

        pl.Tex = GameClock.Ship.Bind(new Texture2D(pl.W, pl.H, TextureFormat.RGBA32, false) { name = "MrpFoam" });
        pl.Tex.filterMode = FilterMode.Bilinear;
        pl.Tex.wrapMode = TextureWrapMode.Clamp;
        Upload(pl);
        var sp = GameClock.Ship.Bind(Sprite.Create(pl.Tex, new Rect(0, 0, pl.W, pl.H),
            new Vector2(-pl.U0 * Ppu / pl.W, -pl.V0 * Ppu / pl.H), Ppu, 0, SpriteMeshType.FullRect)); // 後から描き足すので透明な所で削った形にしない
        var go = new GameObject("MrpFoam") { layer = 0 };
        pl.Go = go;
        go.transform.SetParent(_root.transform, false);
        // 喉を切った船ではその船体の絵のすぐ奥 (星空より手前)。それ以外は口の線より少し奥 (口の手前まで吸い寄せられた人が泡の前に来る)
        float back = HullThroat.BackZ();
        float z = float.IsNaN(back) ? (a.y + uy * len * 0.5f + 0.1f) / 1000f : back + BehindHull;
        go.transform.position = new Vector3(a.x, a.y, z);
        go.transform.localRotation = Quaternion.Euler(0f, 0f, MathF.Atan2(uy, ux) * 57.29578f);
        go.transform.localScale = new Vector3(1f, flip ? -1f : 1f, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        // 壁と同じ影の掛かり方にする (Sprites/Default だと、下に部屋の絵がある所だけ影の板が掛かり、喉の上だけ明るく浮く)
        var mat = DamageMap.WallLikeMaterial;
        if (mat) sr.sharedMaterial = mat;
        return pl;
    }

    private static void AddRow(Plug pl, System.Random rnd, float len, float v, float r, float chance)
    {
        // 口の両端より外へ広がった穴 (エアシップ) はその外まで玉を置く
        float pad0 = MathF.Max(Pad, pl.Bleed0), pad1 = MathF.Max(Pad, pl.Bleed1), span = len + pad0 + pad1;
        int n = Math.Max(2, (int)MathF.Ceiling(span / Spacing) + 1);
        for (int i = 0; i < n; i++)
        {
            if (chance < 1f && rnd.NextDouble() > chance) continue;
            float u = -pad0 + span * i / (n - 1) + ((float)rnd.NextDouble() - 0.5f) * Spacing * 0.6f;
            float edge = Math.Clamp(MathF.Min(u, len - u) / (len * 0.5f), 0f, 1f); // 0 = 端・1 = 中央
            pl.Balls.Add(new Ball
            {
                U = u,
                V = v + ((float)rnd.NextDouble() - 0.5f) * 0.1f,
                R = r * (0.7f + 0.6f * (float)rnd.NextDouble()),
                Start = edge * SprayShare + (float)rnd.NextDouble() * 0.06f + (v - pl.VMin) / pl.Thick * 0.07f,
            });
        }
    }

    // p = 吹き付け始めからの経過 / FoamTime
    private static void Draw(Plug pl, float p, bool last)
    {
        int w = pl.W, h = pl.H;
        var F = pl.F;
        const float inv = 1f / Ppu;
        // 場はなめらかなので 2 画素おきの格子で足し込み、画素へは線形に補間する (足し込みの手間が 1/4)
        int gw = (w + 2) >> 1, gh = (h + 2) >> 1;
        var G = Scratch(ref _grid, gw * gh);
        Array.Clear(G, 0, gw * gh);
        const float ginv = 2f / Ppu;
        // 場 = Σ (1 - d²/(2r)²)³ (玉の影響は半径の 2 倍で 0)。Thresh 以上が泡の中 (玉 1 つなら半径 r の丸)
        var vis = pl.Vis;
        foreach (var bl in pl.Balls)
        {
            if (bl.Hidden) continue;
            float k = (p - bl.Start) / GrowShare;
            if (k <= 0f) continue;
            float s;
            if (k >= 1f) s = 1f + 0.1f * Math.Clamp((p - bl.Start - GrowShare) / 0.6f, 0f, 1f); // 届いた後もゆっくりふくらむ
            else
            {
                // 少し行き過ぎて戻る膨らみ
                float c1 = 1.2f, c3 = c1 + 1f, q = k - 1f;
                s = 1f + c3 * q * q * q + c1 * q * q;
            }
            float r = bl.R * s;
            if (r <= 0.005f) continue;
            float reach = r * pl.Reach, inv4 = 1f / (4f * r * r);
            // 格子点 (gx, gy) = 画素 (2gx, 2gy) の中心
            float cu = bl.U - pl.U0 - 0.5f * inv, cv = bl.V - pl.V0 - 0.5f * inv;
            int x0 = Math.Max(0, (int)((cu - reach) * (Ppu / 2f))), x1 = Math.Min(gw - 1, (int)((cu + reach) * (Ppu / 2f)) + 1);
            int y0 = Math.Max(0, (int)((cv - reach) * (Ppu / 2f))), y1 = Math.Min(gh - 1, (int)((cv + reach) * (Ppu / 2f)) + 1);
            for (int y = y0; y <= y1; y++)
            {
                float dv = y * ginv - cv;
                float rest = 1f - dv * dv * inv4;
                if (rest <= 0f) continue;
                int row = y * gw;
                for (int x = x0; x <= x1; x++)
                {
                    float du = x * ginv - cu;
                    float q = rest - du * du * inv4;
                    if (q > 0f) G[row + x] += q * q * q;
                }
            }
        }
        for (int y = 0; y < h; y++)
        {
            int gy = y >> 1, row = y * w;
            int g0 = gy * gw, g1 = Math.Min(gy + 1, gh - 1) * gw;
            if ((y & 1) == 0)
                for (int x = 0; x < w; x++)
                {
                    int gx = x >> 1;
                    F[row + x] = (x & 1) == 0 ? G[g0 + gx] : 0.5f * (G[g0 + gx] + G[g0 + Math.Min(gx + 1, gw - 1)]);
                }
            else
                for (int x = 0; x < w; x++)
                {
                    int gx = x >> 1, gx1 = Math.Min(gx + 1, gw - 1);
                    F[row + x] = (x & 1) == 0 ? 0.5f * (G[g0 + gx] + G[g1 + gx])
                        : 0.25f * (G[g0 + gx] + G[g0 + gx1] + G[g1 + gx] + G[g1 + gx1]);
                }
        }

        // 喉の形で切る: 切り口に近いほど場を Thresh の手前まで下げ、縁の丸みと輪郭線を切り口にも付ける
        const float Rim = 1.5f / Ppu;
        float vmin = pl.VMin, vmax = pl.VMax, bleed = pl.Bleed0, umax = pl.Len + pl.Bleed1;
        for (int y = 0; y < h; y++)
        {
            float v = pl.V0 + (y + 0.5f) * inv;
            float ev = pl.SoftInner ? vmax - v : MathF.Min(v - vmin, vmax - v);
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                if (vis != null && vis[row + x] == 0) continue;
                float u = pl.U0 + (x + 0.5f) * inv;
                float e = MathF.Min(ev, MathF.Min(u + bleed, umax - u));
                // 切り口から 1.5 画素は輪郭線 (Thresh のすぐ上)・その奥 0.1 単位で丸く盛り上がる
                float cap = e <= 0f ? 0f : e < Rim ? Thresh + 0.001f : Thresh + (e - Rim) * 6f;
                if (F[row + x] > cap) F[row + x] = cap;
            }
        }

        float cure = Math.Clamp((p - CureFrom) / (CureTo - CureFrom), 0f, 1f);
        float wet = 1f - cure;
        // 濡れたクリーム色 → 乾いた黄土色
        float br = 238f + (205f - 238f) * cure, bg = 222f + (178f - 222f) * cure, bb = 168f + (118f - 168f) * cure;
        // 光 (左上・手前) と半分ベクトル (見る向きは真上)
        const float lx = -0.45f, ly = 0.55f, lz = 0.70f;
        float hl = MathF.Sqrt(lx * lx + ly * ly + (lz + 1f) * (lz + 1f));
        float hx = lx / hl, hy = ly / hl, hz = (lz + 1f) / hl;
        var px = pl.Px;
        // 高さは隣の画素からも読むので先に 1 画素 1 回だけ計算しておく
        var Hs = Scratch(ref _height, w * h);
        for (int i = 0; i < w * h; i++) Hs[i] = Height(F[i]);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x, o = i * 4;
            float f = F[i];
            if (vis != null && vis[i] == 0) { px[o + 3] = 0; continue; }
            if (f < Thresh * 0.9f)
            {
                // 落ち影: 左上から光が来るので右下に
                int sx = x - 2, sy = y + 3;
                float fs = sx >= 0 && sy < h ? F[sy * w + sx] : 0f;
                px[o] = 20; px[o + 1] = 16; px[o + 2] = 10;
                px[o + 3] = fs >= Thresh ? (byte)90 : (byte)0;
                continue;
            }
            float alpha = Math.Clamp((f - Thresh * 0.9f) / (Thresh * 0.1f), 0f, 1f);
            // 隣の画素との高さの差から法線
            float hc = Hs[i];
            float hxp = x + 1 < w ? Hs[i + 1] : hc, hxm = x > 0 ? Hs[i - 1] : hc;
            float hyp = y + 1 < h ? Hs[i + w] : hc, hym = y > 0 ? Hs[i - w] : hc;
            float gx = (hxm - hxp) * 9f, gy = (hym - hyp) * 9f, gl = MathF.Sqrt(gx * gx + gy * gy + 1f);
            float nx = gx / gl, ny = gy / gl, nz = 1f / gl;
            float diff = Math.Clamp(nx * lx + ny * ly + nz * lz, 0f, 1f);
            // 玉と玉の谷・縁ほど暗く (盛り上がりの陰)
            float shade = (0.5f + 0.6f * diff) * (0.72f + 0.28f * hc);
            // 気泡の穴: 固まるほど暗く見える (濡れている間は艶に埋もれる)
            byte pore = pl.Pores[i];
            if (pore != 0 && hc > 0.1f) shade *= 1f - (0.1f + 0.35f * cure) * (pore / 255f);
            float r = br * shade, g = bg * shade, b = bb * shade;
            // 濡れた艶 (鋭い白い照り)
            float sp = Math.Clamp(nx * hx + ny * hy + nz * hz, 0f, 1f);
            sp *= sp; sp *= sp; sp *= sp; sp *= sp; // 16 乗
            sp *= sp;                                // 32 乗
            float sheen = sp; // 32 乗 = 鋭い照り
            float broad = Math.Clamp(nx * hx + ny * hy + nz * hz, 0f, 1f);
            broad *= broad; broad *= broad; broad *= broad; // 8 乗 = 濡れた面の広い照り
            float gloss = (0.9f * wet + 0.08f) * sheen + 0.3f * wet * broad;
            r += (255f - r) * gloss; g += (255f - g) * gloss; b += (255f - b) * gloss;
            // 縁の輪郭線 (本編の絵柄)
            if (hc < 0.07f) { r = 70f; g = 54f; b = 32f; }
            px[o] = (byte)Math.Clamp((int)r, 0, 255);
            px[o + 1] = (byte)Math.Clamp((int)g, 0, 255);
            px[o + 2] = (byte)Math.Clamp((int)b, 0, 255);
            px[o + 3] = (byte)(alpha * 255f);
        }
        Upload(pl, last);
        if (last) { pl.Px = null; pl.F = null; pl.Pores = null; } // 跡はもう描き直さない
    }

    // 描き直しの作業用 (描くのは 1 枚ずつなので使い回す)
    private static float[] _grid, _height;
    private static float[] Scratch(ref float[] buf, int n)
    {
        if (buf == null || buf.Length < n) buf = new float[n];
        return buf;
    }

    private const float Thresh = 0.42f; // (1 - 1/4)³: 玉 1 つの縁が半径 r に来る値

    // 縁 0 → 奥ほど 1 へ丸く盛り上がる高さ
    private static float Height(float f)
    {
        float t = Math.Clamp((f - Thresh) / (Thresh * 1.6f), 0f, 1f);
        return MathF.Sqrt(t * (2f - t));
    }

    private static unsafe void Upload(Plug pl, bool last = false)
    {
        fixed (byte* p = pl.Px) pl.Tex.LoadRawTextureData((IntPtr)p, pl.Px.Length);
        pl.Tex.Apply(false, last); // 最後は CPU 側の写しを手放す
    }

    private static bool EnsureRoot()
    {
        if (_root) return true;
        if (!ShipStatus.Instance) return false;
        _root = new GameObject("MrpFoamRoot") { layer = 0 };
        _root.transform.SetParent(ShipStatus.Instance.transform, false);
        // 泡の絵は世界の単位で描くので、船の縮小 (エアシップは 0.7) を打ち消す
        var ls = ShipStatus.Instance.transform.lossyScale;
        _root.transform.localScale = new Vector3(1f / ls.x, 1f / ls.y, 1f / ls.z);
        GameClock.Ship.Bind(_root);
        return true;
    }

    // ShadowPatch から: 影の中の焼いた絵に描き込む物 (固まり終わった泡)
    internal static void CollectCured(List<GameObject> into)
    {
        if (GameClock.ShipGen != _shipGen) return;
        foreach (var f in Foams.Values)
        {
            if (!f.Done) continue;
            foreach (var plug in f.Plugs) if (plug.Go) into.Add(plug.Go);
        }
    }

    internal static void Register()
    {
        TestBridge.Register("foam", "補修フォームの見た目: 口ごとの塊の数・固まり終わったか", (_, reply) =>
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in Foams) sb.Append($" [start={kv.Key.Start} plugs={kv.Value.Plugs.Count} done={kv.Value.Done} t={Time.time - kv.Value.T0:0.00}]");
            reply($"OK foam n={Foams.Count}{sb}");
        });
    }
}
