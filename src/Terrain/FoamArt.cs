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
    private const float CureTo = 1.6f;         // 固まり終わり
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
        public float[] BallArgs; // 描く玉 (u v r start を 4 個ずつ)。作った後は変わらないので 1 回だけ詰める
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
        if (_jobState != 0)
        {
            try { FinishJobs(); }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[FoamArt] job: {e}");
                _failedGen = _shipGen;
            }
        }
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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct CullArgs
    {
        public byte* Mask; public int MW, MH;
        public float OX, OY, MPpu;
        public int W, H;
        public float U0, V0;
        public float AX, AY, UX, UY, VX, VY;
        public float* Balls; public int BallN;
        public float Reach;
        public byte* Vis;
        public byte* Hidden;
        public int HiddenN;
    }

    // 損傷マスクで穴の開いた所 (と縁の少し外) だけを見える画素にし (喉の外でも爆発で船体の板が抜けた所は空が見える)、見える画素に影響しない玉を外す。作る時に 1 回だけ (C++)
    private static unsafe void Cull(Plug pl, Vector2 a, float ux, float uy, bool flip)
    {
        int w = pl.W, h = pl.H, n = pl.Balls.Count;
        float vx = flip ? uy : -uy, vy = flip ? -ux : ux; // 絵の v 軸の世界での向き (flip は縦を裏返して置く)
        var vis = new byte[w * h];
        var hide = new byte[n];
        var balls = new float[n * 3];
        for (int i = 0; i < n; i++) { var bl = pl.Balls[i]; balls[i * 3] = bl.U; balls[i * 3 + 1] = bl.V; balls[i * 3 + 2] = bl.R; }
        int hidden;
        if (!DamageMap.HoleMap(out var mask, out int mw, out int mh, out float ox, out float oy, out float mppu)) { mask = Array.Empty<byte>(); mw = mh = 0; }
        fixed (byte* m = mask) fixed (float* bp = balls) fixed (byte* vp = vis) fixed (byte* hp = hide)
        {
            var c = new CullArgs
            {
                Mask = m, MW = mw, MH = mh, OX = ox, OY = oy, MPpu = mppu, W = w, H = h, U0 = pl.U0, V0 = pl.V0,
                AX = a.x, AY = a.y, UX = ux, UY = uy, VX = vx, VY = vy, Balls = bp, BallN = n, Reach = pl.Reach, Vis = vp, Hidden = hp,
            };
            Native.MrpNative.FoamCull(&c);
            hidden = c.HiddenN;
        }
        pl.Vis = vis;
        for (int i = 0; i < n; i++) pl.Balls[i].Hidden = hide[i] != 0;
        Plugin.Logger.LogInfo($"[FoamArt] cull {w}x{h} balls={n} hidden={hidden}");
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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private unsafe struct DrawArgs
    {
        public float* F, G, Hs;
        public byte* Px;
        public byte* Vis, Pores;
        public float* Balls; public int BallN;
        public int W, H;
        public float U0, V0, Reach, VMin, VMax, Bleed, UMax;
        public int SoftInner;
        public float P;
    }

    // 描き直しは水の作業スレッドへ積み (LateUpdate で投げる)、次のフレームの Tick の頭で受け取って転送する (絵は 1 フレーム遅れ)
    private struct Job { public Plug Pl; public float P; public bool Last; }
    private static Job[] Jobs = new Job[16];
    private static int _jobN, _jobState, _jobGen; // 0 = 無し / 1 = 積んだ / 2 = 描いた
    internal static bool HasJobs => _jobState == 1;
    internal static Exception JobError;

    // p = 吹き付け始めからの経過 / FoamTime
    private static void Draw(Plug pl, float p, bool last)
    {
        if (pl.Px == null || pl.F == null || pl.Pores == null) return; // 固まり終わって手放した絵 (C++ に空の置き場を渡さない)
        if (Native.MrpNative.Async && _jobState != 2)
        {
            if (_jobN == Jobs.Length) Array.Resize(ref Jobs, _jobN * 2); // 同じ栓を積んだ描きと同期の描きに分けない (転送の順が入れ替わる)
            Jobs[_jobN++] = new Job { Pl = pl, P = p, Last = last };
            _jobState = 1;
            _jobGen = _shipGen;
            return;
        }
        WaterSim.EnsureIdle(); // 作業スレッドと同じ置き場 (_grid/_height) を使う
        DrawNative(pl, p);
        Finish(pl, last);
    }

    // 積んだ栓を描く (作業スレッド。投げられなかった時はメイン)。Unity には触らない
    internal static void RunJobs()
    {
        if (_jobState != 1) return;
        for (int i = 0; i < _jobN; i++) DrawNative(Jobs[i].Pl, Jobs[i].P);
        _jobState = 2;
    }

    // 描いた栓を受け取って転送する (メイン)。船が替わっていたら絵ごと壊れているので捨てる
    private static void FinishJobs()
    {
        try
        {
            WaterSim.EnsureIdle();
            if (JobError != null)
            {
                var e = JobError;
                JobError = null;
                throw new InvalidOperationException("foam job", e);
            }
            if (_jobGen != GameClock.ShipGen) return;
            if (_jobState == 1) RunJobs(); // 投げられなかった
            for (int i = 0; i < _jobN; i++) Finish(Jobs[i].Pl, Jobs[i].Last);
        }
        finally
        {
            for (int i = 0; i < _jobN; i++) Jobs[i].Pl = null;
            _jobN = 0;
            _jobState = 0;
        }
    }

    // 絵は C++ で描く
    private static unsafe void DrawNative(Plug pl, float p)
    {
        int w = pl.W, h = pl.H, gw = (w + 2) >> 1, gh = (h + 2) >> 1;
        var G = Scratch(ref _grid, gw * gh);
        var Hs = Scratch(ref _height, w * h);
        var balls = pl.BallArgs ??= PackBalls(pl);
        fixed (float* f = pl.F) fixed (float* g = G) fixed (float* hs = Hs) fixed (byte* px = pl.Px) fixed (byte* vis = pl.Vis) fixed (byte* pores = pl.Pores) fixed (float* bp = balls)
        {
            var a = new DrawArgs
            {
                F = f, G = g, Hs = hs, Px = px, Vis = vis, Pores = pores, Balls = bp, BallN = balls.Length / 4,
                W = w, H = h, U0 = pl.U0, V0 = pl.V0, Reach = pl.Reach, VMin = pl.VMin, VMax = pl.VMax, Bleed = pl.Bleed0, UMax = pl.Len + pl.Bleed1,
                SoftInner = pl.SoftInner ? 1 : 0, P = p,
            };
            Native.MrpNative.FoamDraw(&a);
        }
    }

    private static float[] PackBalls(Plug pl)
    {
        int n = 0;
        foreach (var bl in pl.Balls) if (!bl.Hidden) n++;
        var b = new float[n * 4];
        n = 0;
        foreach (var bl in pl.Balls)
        {
            if (bl.Hidden) continue;
            b[n * 4] = bl.U; b[n * 4 + 1] = bl.V; b[n * 4 + 2] = bl.R; b[n * 4 + 3] = bl.Start;
            n++;
        }
        return b;
    }

    private static void Finish(Plug pl, bool last)
    {
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
