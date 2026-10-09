using System;
using System.Collections.Generic;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 水漏れ: 水回りの部屋 (WaterRooms) に面した壁に穴が開くと、壁の中の配管が裂けて水が噴き出す。
// 破壊が適用されるたびに全員の手元で決める (電文は増やさない)。叩いてひびが入っただけ (穴が開かない) では漏れない。
// 床の水は WaterSim が計算し WaterArt が描く。ここは配管・噴き出しの絵・音・人の濡れ方 (端末ごとでよい物)。
// 噴き出しは WaterSim が水を入れ始めた刻みから見せる (壊してから WaterSim.Delay 刻み後)。会議で止まる。
// 水の中を歩くと一歩ごとに水音 (周りにも聞こえる)。出てから WetFor 秒は濡れた足跡 (粉塵と同じ足跡の列)
internal static class WaterLeak
{
    // 水回りの部屋 (本編の部屋の内部名)。どのマップでも同じ名前は同じ扱い
    private static readonly HashSet<string> WaterRooms = new()
    {
        "LifeSupp", "MedBay", "Medical", "Reactor", "Cafeteria", "Kitchen", "Greenhouse", "Laboratory",
        "Decontamination", "Decontamination2", "Decontamination3", "LockerRoom", "BoilerRoom", "Specimens",
        "Showers", "Lounge", "FishingDock",
    };

    private const float RoomProbe = 0.6f;     // 壊れた区間から両側へこれだけ離れた点の部屋を見る
    private const int SegmentsChecked = 6;    // 壊した点に近い区間からこれだけ調べる

    // 噴き出し: 壁の中を通る配管の縦の裂け目から、平たい扇形の水の膜が出る。膜は先へ行くほど薄く広がり、
    // 先でちぎれて粒になって落ちる。落ちた線に沿って泡と波紋。裂け目に霧。
    // 破裂の直後 (BurstTime 秒) は広く遠く、普段は FanSteady、弱まると狭く手前に落ちる
    private const float JetFull = 10f, JetFade = 5f;
    private const float PipeHeightMin = 0.14f, PipeHeightMax = 0.42f; // 配管の高さ (壁の中・床から)。漏れごとに種から
    private const float PipeThickMin = 0.11f, PipeThickMax = 0.19f;
    private const float MouthOut = 0.04f;     // 裂け目を壁の線から部屋側へ出す距離
    private const float SlitWidth = 0.12f;    // 裂け目の幅
    private const float BurstTime = 0.5f;
    private const float MistRate = 5f, MistLife = 0.4f, MistFrom = 0.1f, MistTo = 0.28f;
    private const float FoamFrom = 0.18f, FoamTo = 0.5f; // 落ちた所の泡の大きさ
    private const int LandEvery = 3;          // 床に落ちた粒の何個に 1 つ、波紋と跳ねる粒を出すか
    private const int MaxMist = 64;
    private const float Gravity = 7f;         // 跳ねる粒 (絵だけ) の重力
    private const float DropSize = 0.08f;
    private const int MaxDrops = 128;
    private const int MaxJets = 3;
    private const int MaxPipes = 8;
    private const float LeakSoundEvery = 1.0f, LeakSoundRange = 14f, LeakSoundMuffle = 6f, LeakSoundVolume = 0.55f;

    // 波紋
    private const float RippleLife = 0.5f, RippleFrom = 0.18f, RippleTo = 0.6f;
    private const int MaxRipples = 48;

    // 爆発の水しぶき (絵だけ・爆発と同じフレーム)。水の計算は 0.6 秒遅れて爆心をえぐるので、
    // かたまりの飛ぶ時間をそれに合わせ、落ちる頃に計算の水が縁に積もる
    private const float BlastReach = 1.6f, BlastExtra = 0.5f;   // 半径 = 爆発の半径 × これ + これ (WaterSim の衝撃と同じ)
    private const float BlastProbe = 0.2f;                       // 水のある所を探す間隔
    private const int BlobMin = 8, BlobMax = 40, SprayDrops = 20, CrownFoam = 8;
    private const float BlobSizeMin = 0.12f, BlobSizeMax = 0.24f;
    private const float AirZ = -1.02f;                           // 飛んでいる間は土煙 (-1) より手前

    // 人
    private const float ScanInterval = 0.1f;
    private const float WetFor = 6f;
    private const float StepDist = 0.55f;
    private const float SplashRange = 10f, SplashMuffle = 5f, SplashVolume = 0.6f;
    private const float PrintAlphaFresh = 0.5f, PrintAlphaOld = 0.18f;
    private const float WetR = 0.15f, WetG = 0.29f, WetB = 0.5f;
    private const int WetDepth = 40;          // これより深い水の中を「水の中」とする (WaterSim.Full = 1 単位)

    // 噴いている 1 か所 (音・口の霧・落ちた所の跳ね。水の形は WaterSim・膜の絵は WaterSpray)
    private sealed class Jet
    {
        public float X, Y, T, SoundAcc, MistAcc;
        public float H;                // 裂けた配管の高さ (霧の出る所)
        public float DropZ, RingZ;     // その場所の床の z から決めた粒と波紋の z (粒ごとに部屋を引かない)
        public Vector2 Dir;
        public bool Both;
        public System.Random Rnd;
    }

    private sealed class Mist
    {
        public Transform Tr;
        public SpriteRenderer Sr;
        public float X, Y, Z, Vx, Vy, S0, S1, Age = MistLife, Life = MistLife, A = 0.35f;
    }

    private sealed class Drop
    {
        public Transform Tr;
        public SpriteRenderer Sr;
        public Transform ShTr;           // 床の影 (かたまりだけ)
        public SpriteRenderer ShSr;
        public float X, Y, H, Vx, Vy, Vh, Z, RingZ, Size, TrailAcc, Age, Phase, Bw = 1f; // Bw = 絵の幅 (毎フレーム聞かない)
        public bool Live, Ripple, Split;
        public int Kind = -1;            // 今の絵 (DropKind)
        public int Look;                 // 絵を作った時の Kind と種類 (変わった時だけ差し替える)
    }

    // しずくの種類: 普通の粒 (漏れ)・飛ぶ水のかたまり・細かいしぶきの筋
    private static float _shadowW;
    private const int DropPlain = 0, DropBlob = 1, DropStreak = 2;
    private const float BlobSplitSize = 0.16f;     // これより大きいかたまりは弧の頂点で 2 つに分かれる
    private const float BlobStretch = 0.07f, BlobStretchMax = 1.7f;     // 速さ 1 単位/秒あたりの伸び
    private const float StreakStretch = 0.25f, StreakStretchMax = 3.2f;
    private const float WobbleAmp = 0.1f, WobbleRate = 22f;

    private sealed class Ring
    {
        public Transform Tr;
        public SpriteRenderer Sr;
        public float Age = RippleLife, A0, Life = RippleLife, From = RippleFrom, To = RippleTo, Sw = 1f;
        public bool Wave;
    }

    private static readonly List<Jet> Jets = new();
    private static readonly List<GameObject> Pipes = new();
    private static readonly Mist[] Mists = new Mist[MaxMist];
    private static int _nextMist, _liveMist;
    private static readonly Drop[] Drops = new Drop[MaxDrops];
    private static readonly Ring[] Rings = new Ring[MaxRipples];
    private static int _nextDrop, _nextRing, _liveDrops, _liveRings;
    private static readonly float[] WetUntil = new float[256];
    private static readonly float[] LastX = new float[256], LastY = new float[256], Walked = new float[256];
    private static readonly bool[] Tracked = new bool[256], LeftFoot = new bool[256];
    private static bool _anyWet;
    private static int _splashN;
    private static int _shipGen;
    private static long _lastMs;
    private static float _clock, _scanAcc;

    // テスト用
    internal static int Made;
    internal static string Last = "-";

    // 破壊を適用した直後に呼ぶ (ホスト・客・一人の全員)
    public static void OnApplied(in ResolvedDamage r)
    {
        try { Add(r); }
        catch (Exception e) { Plugin.Logger.LogError($"[WaterLeak] {e}"); }
    }

    private static void Add(in ResolvedDamage r)
    {
        if (r.Kind == DamageKind.Explosion) Splash(r);
        if (!ShipStatus.Instance || TerrainDamage.LastCut <= 0) return;
        if (r.Kind != DamageKind.Explosion && r.Hp > 0) return;
        if (!FindLeak(r.Position, out Vector2 at, out Vector2 n, out float segLen, out string room)) return;
        Start(at, n, r.Seed, room, segLen, r.Tick);
    }

    // 爆発の水しぶき: 爆心の周りの水のある所から、水のかたまりを外へ弧を描いて飛ばし、細かいしぶき・広がる波の輪・泡を出す
    private static readonly List<Vector2> WetPts = new();
    private static void Splash(in ResolvedDamage r)
    {
        if (!WaterSim.Ready || !GameClock.ShipAlive || MeetingHud.Instance) return;
        Vector2 c = r.Position;
        float rad = r.Size * BlastReach + BlastExtra;
        WetPts.Clear();
        long deep = 0;
        for (float y = -rad; y <= rad; y += BlastProbe)
        for (float x = -rad; x <= rad; x += BlastProbe)
        {
            if (x * x + y * y > rad * rad) continue;
            var p = new Vector2(c.x + x, c.y + y);
            int d = WaterSim.DepthAt(p);
            if (d < WetDepth) continue;
            WetPts.Add(p);
            deep += d;
        }
        if (WetPts.Count == 0) return;
        WaterArt.AddCrater(c, rad);
        float depth = Math.Min(1f, deep / (float)WetPts.Count / WaterSim.Full * 2f);   // 0.5 単位で最大
        float floor = DamageMap.FrontZ(c), zs = DamageMap.ZScale(floor);
        float ringZ = floor - 0.0008f * zs;
        var rnd = new System.Random(r.Seed * 7919 + 17);
        float bx = r.Direction.x * r.Force, by = r.Direction.y * r.Force;

        int blobs = Math.Clamp(BlobMin + WetPts.Count / 3, BlobMin, BlobMax);
        for (int i = 0; i < blobs; i++)
        {
            var p = WetPts[rnd.Next(WetPts.Count)];
            float dx = p.x - c.x, dy = p.y - c.y, dl = MathF.Sqrt(dx * dx + dy * dy);
            float ang = dl < 0.05f ? (float)rnd.NextDouble() * MathF.PI * 2f : MathF.Atan2(dy, dx) + ((float)rnd.NextDouble() - 0.5f) * 0.7f;
            float ux = MathF.Cos(ang) + bx, uy = MathF.Sin(ang) + by, ul = MathF.Max(0.001f, MathF.Sqrt(ux * ux + uy * uy));
            float vh = 2.2f + 1.2f * (float)rnd.NextDouble();
            float t = 2f * vh / Gravity;
            float land = Reach(p, ux / ul, uy / ul, rad * (1f + 0.7f * (float)rnd.NextDouble()) - dl);
            float hs = land / t;
            float size = BlobSizeMin + (BlobSizeMax - BlobSizeMin) * (0.4f * depth + 0.6f * (float)rnd.NextDouble());
            SpawnDrop(p.x, p.y, 0.02f, ux / ul * hs, uy / ul * hs, vh, false, AirZ - 0.0001f * i, ringZ, size, DropBlob, rnd.Next(3));
        }
        for (int i = 0; i < SprayDrops; i++)
        {
            var p = WetPts[rnd.Next(WetPts.Count)];
            float ang = (float)rnd.NextDouble() * MathF.PI * 2f;
            float vx = MathF.Cos(ang) + bx * 0.5f, vy = MathF.Sin(ang) * 0.7f + by * 0.5f, vl = MathF.Max(0.001f, MathF.Sqrt(vx * vx + vy * vy));
            float vh = 3f + 1.5f * (float)rnd.NextDouble(), t = 2f * vh / Gravity;
            float hs = Reach(p, vx / vl, vy / vl, (3f + 3f * (float)rnd.NextDouble()) * t) / t;
            SpawnDrop(p.x, p.y, 0.02f, vx / vl * hs, vy / vl * hs, vh, true, AirZ, ringZ, DropSize, DropStreak, 0);
        }
        AddRipple(c.x, c.y, 0.9f, ringZ, 0.7f, rad * 0.4f, rad * 2.2f, true);
        AddRipple(c.x, c.y, 0.6f, ringZ, 0.9f, rad * 0.2f, rad * 1.4f, true);
        for (int i = 0; i < CrownFoam; i++)
        {
            float ang = (i + (float)rnd.NextDouble() * 0.6f) * MathF.PI * 2f / CrownFoam;
            float ca = MathF.Cos(ang), sa = MathF.Sin(ang);
            AddMist(c.x + ca * rad * 0.3f, c.y + sa * rad * 0.3f, ca * 1.4f, sa * 1.0f, AirZ + 0.001f, 0.3f, 0.8f, 0.55f, 0.65f);
        }
    }

    // p から向き (ux, uy) へ want 進む間に歩けない所 (壁・船の外) があれば、その手前までの距離
    private static float Reach(Vector2 p, float ux, float uy, float want)
    {
        const float step = 0.1f;
        float d = 0f;
        while (d + step <= want)
        {
            if (SolidMap.Solid(new Vector2(p.x + ux * (d + step), p.y + uy * (d + step)))) return MathF.Max(0.05f, d - step);
            d += step;
        }
        return MathF.Max(0.05f, want);
    }

    // 壊した点に近い壊れた区間から、両側のどちらかが水回りの部屋のものを探す
    private static bool FindLeak(Vector2 from, out Vector2 at, out Vector2 normal, out float segLen, out string room)
    {
        at = normal = Vector2.zero;
        segLen = 0f;
        room = null;
        var segs = TerrainDamage.LastRemoved;
        int pairs = segs.Count / 2;
        if (pairs == 0) return false;
        var order = new List<(float D, int I)>(pairs);
        for (int i = 0; i < pairs; i++)
        {
            Vector2 a = segs[2 * i], b = segs[2 * i + 1];
            float mx = (a.x + b.x) * 0.5f - from.x, my = (a.y + b.y) * 0.5f - from.y;
            order.Add((mx * mx + my * my, i));
        }
        order.Sort((p, q) => p.D.CompareTo(q.D));
        for (int k = 0; k < Math.Min(SegmentsChecked, order.Count); k++)
        {
            int i = order[k].I;
            Vector2 a = segs[2 * i], b = segs[2 * i + 1];
            float dx = b.x - a.x, dy = b.y - a.y, len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1e-4f) continue;
            var mid = new Vector2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f);
            var n = new Vector2(-dy / len, dx / len);
            room = WaterRoom(mid + n * RoomProbe) ?? WaterRoom(mid - n * RoomProbe) ?? WaterRoom(mid);
            if (room == null) continue;
            at = mid;
            normal = n;
            segLen = len;
            return true;
        }
        room = null;
        return false;
    }

    private static string WaterRoom(Vector2 p)
    {
        var r = TerrainDamage.RoomAt(p);
        if (!r) return null;
        string name = r.RoomId.ToString();
        return WaterRooms.Contains(name) ? name : null;
    }

    private static void Start(Vector2 at, Vector2 n, ushort seed, string room, float segLen, ushort tick)
    {
        // 噴く向き = 壊れた区間の両側のうち歩ける側 (壁を突き抜けた時は両側)
        bool pos = !SolidMap.Valid || !SolidMap.Solid(at + n * 0.4f);
        bool neg = !SolidMap.Valid || !SolidMap.Solid(at - n * 0.4f);
        if (!pos && !neg) pos = true;
        float floor = DamageMap.FrontZ(at), zs = DamageMap.ZScale(floor);
        AddPipes(at.x, at.y, floor - 0.003f * zs + 0.0005f, n, segLen, seed);
        var dir = pos ? n : -n;
        WaterSim.AddLeak(tick, at, dir, pos && neg, seed);
        Made++;
        Last = $"{room} @{at.x:0.0},{at.y:0.0} sides={(pos && neg ? 2 : 1)} tick={tick}";
    }

    // WaterSim が水を入れ始めた刻み: 噴き出しを見せる (dir の側・both なら反対側も)
    private static void StartJet(Vector2 at, Vector2 dir, ushort seed, bool both)
    {
        try
        {
            if (MeetingHud.Instance || !ShipStatus.Instance) return;
            var jet = new Jet { X = at.x, Y = at.y, Rnd = new System.Random(seed), SoundAcc = LeakSoundEvery, H = PipeLook(seed).H };
            float floor = DamageMap.FrontZ(at), zs = DamageMap.ZScale(floor);
            jet.DropZ = floor - 0.003f * zs;
            jet.RingZ = floor - 0.0008f * zs;
            jet.Dir = dir;
            jet.Both = both;
            WaterSpray.Start(at, dir, jet.DropZ);
            while (Jets.Count >= MaxJets) Jets.RemoveAt(0);
            Jets.Add(jet);
        }
        catch (Exception e) { Plugin.Logger.LogError($"[WaterLeak] jet: {e}"); }
    }

    // ShadowPatch から: 影の中の焼いた絵に描き込む物
    internal static void CollectPuddles(List<GameObject> into)
    {
        foreach (var go in Pipes) if (go) into.Add(go);
    }

    // 外壁が宇宙まで抜けた喉の中に描いていた配管を消す (HullThroat が穴を開けた時)
    internal static int HidePipesInThroat()
    {
        int n = 0;
        for (int i = Pipes.Count - 1; i >= 0; i--)
        {
            var go = Pipes[i];
            if (!go) { Pipes.RemoveAt(i); continue; }
            var p = go.transform.position;
            if (!HullThroat.Contains(FxMath.V2(p.x, p.y))) continue;
            UnityEngine.Object.Destroy(go);
            Pipes.RemoveAt(i);
            n++;
        }
        return n;
    }

    // ── 噴き出しと波紋 ─────────────────────────────────────────────────

    // 漏れごとの配管の見た目 (種から決めるので全員同じ): 裂け方・色・高さ・傾き・太さ・長さ・左右の向き・横を通る 2 本目
    private struct Look
    {
        public int Kind, Paint;
        public float H, Tilt, Thick, Len, Flip, H2, Len2, Thick2;
        public int Paint2;
        public bool Second;
    }

    private static Look PipeLook(ushort seed)
    {
        var r = new System.Random(seed ^ 0x5a17);
        float F() => (float)r.NextDouble();
        var l = new Look
        {
            Kind = r.Next(DebrisArt.IntactPipe),
            Paint = r.Next(DebrisArt.PipePaints),
            H = PipeHeightMin + (PipeHeightMax - PipeHeightMin) * F(),
            Tilt = (F() * 2f - 1f) * 8f,
            Thick = PipeThickMin + (PipeThickMax - PipeThickMin) * F(),
            Len = 0.7f + 0.45f * F(),
            Flip = r.Next(2) == 0 ? 1f : -1f,
            Second = F() < 0.4f,
        };
        // 2 本目は裂けていない細い管。上か下へずらす (床に埋まらない高さ)
        float off = (0.12f + 0.08f * F()) * (l.H > 0.26f && r.Next(3) > 0 ? -1f : 1f);
        l.H2 = Math.Clamp(l.H + off, 0.08f, 0.6f);
        l.Len2 = 0.8f + 0.5f * F();
        l.Thick2 = 0.07f + 0.04f * F();
        l.Paint2 = r.Next(DebrisArt.PipePaints);
        return l;
    }

    // 壁の中を壁に沿って通る配管 (裂け目が漏れの所)。水たまりと同じく試合の終わりまで残る
    private static void AddPipes(float x, float y, float z, Vector2 n, float segLen, ushort seed)
    {
        var l = PipeLook(seed);
        float wall = MathF.Atan2(-n.x, n.y) * (180f / MathF.PI);
        AddPipe(x, y + l.H, z, wall + l.Tilt, Math.Clamp(segLen * l.Len, 0.4f, 1.4f), l.Thick, l.Flip, DebrisArt.Pipe(l.Kind, l.Paint));
        if (l.Second) // 少し奥 (壁の中) を通る、裂けていない管
            AddPipe(x, y + l.H2, z + 0.0002f, wall + l.Tilt * 0.3f, Math.Clamp(segLen * l.Len2, 0.4f, 1.6f), l.Thick2, l.Flip, DebrisArt.Pipe(DebrisArt.IntactPipe, l.Paint2));
    }

    private static void AddPipe(float x, float y, float z, float deg, float len, float thick, float flip, Sprite sprite)
    {
        // 外壁が宇宙まで抜けた所には壁の中の配管も残っていない
        if (HullThroat.Contains(FxMath.V2(x, y))) return;
        var pipe = new GameObject("MrpSplitPipe") { layer = 0 };
        var sr = pipe.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        var b = sprite.bounds.size;
        pipe.transform.position = FxMath.V3(x, y, z);
        pipe.transform.localRotation = FxMath.RotZ(deg);
        pipe.transform.localScale = FxMath.V3(flip * len / Math.Max(0.0001f, b.x), thick / Math.Max(0.0001f, b.y), 1f);
        while (Pipes.Count >= MaxPipes) { if (Pipes[0]) UnityEngine.Object.Destroy(Pipes[0]); Pipes.RemoveAt(0); }
        Pipes.Add(pipe);
        ShadowPatch.MarkDirtyLater(FxMath.V2(x, y), len * 0.5f + 0.2f); // 影の中の焼いた絵にも描き込む
    }

    private static void TickJets(float dt)
    {
        for (int j = Jets.Count - 1; j >= 0; j--)
        {
            var jet = Jets[j];
            jet.T += dt;
            if (jet.T >= JetFull + JetFade) { Jets.RemoveAt(j); continue; }
            float k = jet.T <= JetFull ? 1f : 1f - (jet.T - JetFull) / JetFade;
            float burst = Math.Max(0f, 1f - jet.T / BurstTime);
            jet.SoundAcc += dt;
            if (jet.SoundAcc >= LeakSoundEvery)
            {
                jet.SoundAcc = 0f;
                BreakNoise.PlayAt("noise_leak", new Vector2(jet.X, jet.Y), LeakSoundRange, LeakSoundMuffle, LeakSoundVolume * (0.3f + 0.7f * k));
            }
            // 裂け目の霧 (破裂の直後は多い)
            jet.MistAcc += dt * MistRate * (0.3f + 0.7f * k) * (1f + 3f * burst);
            var rnd = jet.Rnd;
            while (jet.MistAcc >= 1f)
            {
                jet.MistAcc -= 1f;
                var d = jet.Both && rnd.Next(2) == 0 ? -jet.Dir : jet.Dir;
                float u = (float)rnd.NextDouble() * 2f - 1f;
                float vs = 0.35f + 0.6f * burst;
                float mx = jet.X + d.x * MouthOut - d.y * u * SlitWidth * 0.5f, my = jet.Y + d.y * MouthOut + d.x * u * SlitWidth * 0.5f;
                AddMist(mx, my + jet.H, d.x * vs - d.y * u * vs * 0.4f, d.y * vs + d.x * u * vs * 0.4f + 0.12f, jet.DropZ - 0.002f, MistFrom, MistTo);
            }
        }
    }

    // 刻みが進んだ時: 床に落ちた粒の何個かに波紋・跳ねる粒・泡 (絵だけ)
    private static int _landStep = -1, _landN;
    private static void TickLanding()
    {
        if (Jets.Count == 0 || WaterSim.Step == _landStep) return;
        _landStep = WaterSim.Step;
        var jet = Jets[Jets.Count - 1];
        var rnd = jet.Rnd;
        var org = WaterSim.Origin;
        const float inv = 1f / WaterSim.Unit;
        foreach (var (lx, ly) in WaterSim.Landed)
        {
            if (++_landN % LandEvery != 0) continue;
            float x = org.x + lx * inv, y = org.y + ly * inv;
            AddRipple(x, y, 0.5f, jet.RingZ);
            if (_landN % (LandEvery * 2) == 0) AddMist(x, y + 0.02f, 0f, 0.05f, jet.RingZ - 0.0002f, FoamFrom * 0.6f, FoamTo * 0.6f);
            float ang = (float)rnd.NextDouble() * MathF.PI * 2f, sp = 0.3f + 0.5f * (float)rnd.NextDouble();
            SpawnDrop(jet, x, y, 0.02f, MathF.Cos(ang) * sp, MathF.Sin(ang) * sp * 0.6f, 0.6f + 0.6f * (float)rnd.NextDouble(), false);
        }
    }

    private static void SpawnDrop(Jet jet, float x, float y, float h, float vx, float vy, float vh, bool ripple) =>
        SpawnDrop(x, y, h, vx, vy, vh, ripple, jet.DropZ, jet.RingZ, DropSize, DropPlain, 0);

    // kind = DropBlob: 爆発で飛ぶ水のかたまり (しぶきの尾・床の影・頂点でちぎれる・落ちた所に波紋と泡と跳ね返り)。
    // DropStreak: 細かいしぶき (向きへ伸びる光の筋)。look = かたまりの絵の種類
    private static Drop SpawnDrop(float x, float y, float h, float vx, float vy, float vh, bool ripple, float z, float ringZ, float size, int kind, int look)
    {
        var d = Drops[_nextDrop];
        if (d == null || !d.Tr)
        {
            var go = new GameObject("MrpWaterDrop") { layer = 0 };
            d = Drops[_nextDrop] = new Drop { Tr = go.transform, Sr = go.AddComponent<SpriteRenderer>() };
        }
        int want = kind * 16 + look;
        if (d.Look != want || d.Kind < 0)
        {
            d.Look = want;
            d.Sr.sprite = kind == DropBlob ? DebrisArt.WaterBlob(look) : kind == DropStreak ? DebrisArt.WaterStreak : DebrisArt.WaterDrop;
            d.Bw = Math.Max(0.0001f, d.Sr.sprite.bounds.size.x);
            if (kind == DropPlain) d.Tr.rotation = FxMath.RotZ(0f);
            d.Size = -1f;
        }
        d.Kind = kind;
        if (kind == DropPlain && d.Size != size)
        {
            float s = size / d.Bw;
            d.Tr.localScale = FxMath.V3(s, s, 1f);
        }
        d.Size = size;
        if (kind == DropBlob)
        {
            if (!d.ShTr)
            {
                var sg = new GameObject("MrpWaterDropShadow") { layer = 0 };
                d.ShTr = sg.transform;
                d.ShSr = sg.AddComponent<SpriteRenderer>();
                d.ShSr.sprite = DebrisArt.SoftShadow;
                if (_shadowW <= 0f) _shadowW = Math.Max(0.0001f, DebrisArt.SoftShadow.bounds.size.x);
            }
            d.ShTr.gameObject.SetActive(true);
        }
        else if (d.ShTr) d.ShTr.gameObject.SetActive(false);
        _nextDrop = (_nextDrop + 1) % MaxDrops;
        if (!d.Live) _liveDrops++;
        d.X = x; d.Y = y; d.H = h;
        d.Vx = vx; d.Vy = vy; d.Vh = vh;
        d.Z = z;
        d.RingZ = ringZ;
        d.Ripple = ripple;
        d.TrailAcc = 0f;
        d.Age = 0f;
        d.Phase = (x * 13.7f + y * 7.3f) % 6.28f;
        d.Split = false;
        d.Live = true;
        d.Tr.gameObject.SetActive(true);
        d.Tr.position = FxMath.V3(d.X, d.Y + d.H, d.Z);
        return d;
    }

    // 霧と泡 (同じ使い回しの列)。s0 → s1 = 出てから消えるまでの大きさ
    private static void AddMist(float x, float y, float vx, float vy, float z, float s0, float s1) =>
        AddMist(x, y, vx, vy, z, s0, s1, MistLife, 0.35f);

    private static void AddMist(float x, float y, float vx, float vy, float z, float s0, float s1, float life, float alpha)
    {
        var m = Mists[_nextMist];
        if (m == null || !m.Tr)
        {
            var go = new GameObject("MrpWaterMist") { layer = 0 };
            m = Mists[_nextMist] = new Mist { Tr = go.transform, Sr = go.AddComponent<SpriteRenderer>() };
            m.Sr.sprite = DebrisArt.Foam;
        }
        _nextMist = (_nextMist + 1) % MaxMist;
        if (m.Age >= m.Life) _liveMist++;
        m.Age = 0f;
        m.Life = life;
        m.A = alpha;
        m.X = x; m.Y = y; m.Z = z; m.Vx = vx; m.Vy = vy; m.S0 = s0; m.S1 = s1;
        m.Sr.enabled = true;
        m.Tr.position = FxMath.V3(x, y, z);
        m.Tr.localScale = FxMath.V3(0f, 0f, 1f);
    }

    private static void TickMist(float dt)
    {
        if (_liveMist == 0) return;
        int live = 0;
        float sw = Math.Max(0.0001f, DebrisArt.Foam.bounds.size.x);
        foreach (var m in Mists)
        {
            if (m == null || m.Age >= m.Life) continue;
            m.Age += dt;
            if (m.Age >= m.Life) { m.Sr.enabled = false; continue; }
            live++;
            float u = m.Age / m.Life;
            m.X += m.Vx * dt;
            m.Y += m.Vy * dt;
            m.Tr.position = FxMath.V3(m.X, m.Y, m.Z);
            float size = (m.S0 + (m.S1 - m.S0) * u) / sw;
            m.Tr.localScale = FxMath.V3(size, size, 1f);
            m.Sr.color = FxMath.Rgba(1f, 1f, 1f, m.A * (1f - u) * (1f - u));
        }
        _liveMist = live;
    }

    private static void TickDrops(float dt)
    {
        if (_liveDrops == 0) return;
        int live = 0;
        foreach (var d in Drops)
        {
            if (d == null || !d.Live) continue;
            d.X += d.Vx * dt;
            d.Y += d.Vy * dt;
            float vh0 = d.Vh;
            d.Vh -= Gravity * dt;
            d.H += d.Vh * dt;
            d.Age += dt;
            bool blob = d.Kind == DropBlob;
            if (d.H <= 0f)
            {
                d.Live = false;
                d.Tr.gameObject.SetActive(false);
                if (blob)
                {
                    d.ShTr.gameObject.SetActive(false);
                    Land(d);
                }
                else if (d.Ripple) AddRipple(d.X, d.Y, 0.6f, d.RingZ);
                continue;
            }
            live++;
            if (blob)
            {
                if (vh0 > 0f && d.Vh <= 0f && !d.Split && d.Size > BlobSplitSize) Split(d);
                if ((d.TrailAcc += dt) >= 0.05f)
                {
                    d.TrailAcc = 0f;
                    AddMist(d.X, d.Y + d.H, d.Vx * 0.1f, d.Vy * 0.1f, d.Z + 0.0005f, d.Size * 0.4f, d.Size * 0.8f, 0.25f, 0.3f);
                }
                // 床の影: 高いほど小さく薄い
                float hk = FxMath.Clamp01(d.H / 1.2f);
                float ss = d.Size * (1.1f - 0.4f * hk) / _shadowW;
                d.ShTr.position = FxMath.V3(d.X, d.Y - d.Size * 0.15f, d.RingZ - 0.0001f);
                d.ShTr.localScale = FxMath.V3(ss, ss * 0.5f, 1f);
                d.ShSr.color = FxMath.Rgba(1f, 1f, 1f, 0.35f * (1f - 0.7f * hk));
            }
            if (d.Kind != DropPlain) Orient(d);
            d.Tr.position = FxMath.V3(d.X, d.Y + d.H, d.Z);
        }
        _liveDrops = live;
    }

    // 画面の上の動き (横 + 高さ) の向きへ回し、速いほど進む向きへ伸ばす。かたまりは揺れながら飛ぶ
    private static void Orient(Drop d)
    {
        float vx = d.Vx, vy = d.Vy + d.Vh;
        float sp = FxMath.Sqrt(vx * vx + vy * vy);
        bool blob = d.Kind == DropBlob;
        float st = blob ? FxMath.Min(BlobStretchMax, 1f + sp * BlobStretch) : FxMath.Min(StreakStretchMax, 1f + sp * StreakStretch);
        float w = blob ? 1f + WobbleAmp * FxMath.Sin(d.Age * WobbleRate + d.Phase) : 1f;
        float s = d.Size / d.Bw;
        d.Tr.rotation = FxMath.RotZ(FxMath.Atan2(vy, vx) * (180f / MathF.PI));
        d.Tr.localScale = FxMath.V3(s * st * w, s / FxMath.Sqrt(st) / w, 1f);
    }

    // かたまりが弧の頂点でちぎれる: 自分は少し小さくなり、横へ分かれる小さなかたまりを 2 つ出す
    private static void Split(Drop d)
    {
        d.Split = true;
        float sz = d.Size;
        d.Size = sz * 0.78f;
        float sp = FxMath.Sqrt(d.Vx * d.Vx + d.Vy * d.Vy), nx = sp > 0.01f ? -d.Vy / sp : 1f, ny = sp > 0.01f ? d.Vx / sp : 0f;
        for (int k = 0; k < 2; k++)
        {
            float side = k == 0 ? 1f : -1f, kick = 0.4f + 0.5f * (((int)(d.Phase * 100f) >> k & 7) / 7f);
            var c = SpawnDrop(d.X, d.Y, d.H, d.Vx * 0.9f + nx * side * kick, d.Vy * 0.9f + ny * side * kick, 0.3f + 0.2f * k,
                false, d.Z - 0.00005f, d.RingZ, sz * (0.38f + 0.12f * k), DropBlob, k + 1);
            c.Split = true;
            c.Phase = d.Phase + 1.7f * (k + 1);
        }
    }

    // かたまりが床に落ちた: 波紋・泡と、王冠のように跳ね上がる小さなしぶき
    private static void Land(Drop d)
    {
        AddRipple(d.X, d.Y, 0.8f, d.RingZ, 0.6f, d.Size * 1.5f, d.Size * 5f);
        AddMist(d.X, d.Y + 0.02f, 0f, 0.08f, d.RingZ - 0.0002f, d.Size * 1.2f, d.Size * 2.6f, 0.45f, 0.6f);
        float a0 = d.Phase;
        for (int k = 0; k < 3; k++)
        {
            float ang = a0 + k * 2.1f, hs = 0.5f + 0.25f * k;
            SpawnDrop(d.X, d.Y, 0.02f, FxMath.Cos(ang) * hs + d.Vx * 0.15f, FxMath.Sin(ang) * hs * 0.6f + d.Vy * 0.15f, 1.3f + 0.3f * k,
                false, d.Z, d.RingZ, DropSize * 0.8f, DropStreak, 0);
        }
    }

    // strength = 最初の濃さ。z = 床の z から決めた波紋の z
    private static void AddRipple(float x, float y, float strength, float z) =>
        AddRipple(x, y, strength, z, RippleLife, RippleFrom, RippleTo);

    private static void AddRipple(float x, float y, float strength, float z, float life, float from, float to) =>
        AddRipple(x, y, strength, z, life, from, to, false);

    // wave = 大きく広がる波 (細かい絵を使う)
    private static void AddRipple(float x, float y, float strength, float z, float life, float from, float to, bool wave)
    {
        var r = Rings[_nextRing];
        if (r == null || !r.Tr)
        {
            var go = new GameObject("MrpRipple") { layer = 0 };
            r = Rings[_nextRing] = new Ring { Tr = go.transform, Sr = go.AddComponent<SpriteRenderer>() };
            r.Sr.sprite = DebrisArt.Ripple;
        }
        _nextRing = (_nextRing + 1) % MaxRipples;
        if (r.Age >= r.Life) _liveRings++;
        r.Age = 0f;
        r.Life = life; r.From = from; r.To = to;
        if (r.Wave != wave || r.Sw == 1f)
        {
            r.Wave = wave;
            r.Sr.sprite = wave ? DebrisArt.WaveRing : DebrisArt.Ripple;
            r.Sw = Math.Max(0.0001f, r.Sr.sprite.bounds.size.x);
        }
        r.A0 = strength;
        r.Tr.position = FxMath.V3(x, y, z);
        r.Tr.localScale = FxMath.V3(0f, 0f, 1f);
        r.Sr.enabled = true;
        r.Sr.color = FxMath.Rgba(1f, 1f, 1f, strength);
    }

    private static void TickRipples(float dt)
    {
        if (_liveRings == 0) return;
        int live = 0;
        foreach (var r in Rings)
        {
            if (r == null || r.Age >= r.Life) continue;
            r.Age += dt;
            if (r.Age >= r.Life) { r.Sr.enabled = false; continue; }
            live++;
            float u = r.Age / r.Life;
            float e = 1f - (1f - u) * (1f - u);
            float size = (r.From + (r.To - r.From) * e) / r.Sw;
            // 横長の楕円 (床を斜めに見ている)
            r.Tr.localScale = FxMath.V3(size, size * 0.6f, 1f);
            r.Sr.color = FxMath.Rgba(1f, 1f, 1f, r.A0 * (1f - u));
        }
        _liveRings = live;
    }

    // ── 人 ─────────────────────────────────────────────────────────────

    // 0.1 秒ごと: 全員の位置で水たまりの中か。中なら一歩ごとに水音と波紋・出た後しばらく濡れた足跡
    private static void Scan()
    {
        _anyWet = false;
        var all = PlayerControl.AllPlayerControls;
        for (int i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (!pc) continue;
            var data = pc.Data;
            byte id = pc.PlayerId;
            if (data == null || data.IsDead || data.Disconnected) { Tracked[id] = false; WetUntil[id] = 0f; continue; }
            Vector2 pos = pc.GetTruePosition();
            bool inWater = WaterSim.DepthAt(pos) >= WetDepth;
            if (inWater) WetUntil[id] = _clock + WetFor;
            if (WetUntil[id] > _clock) _anyWet = true;

            if (!Tracked[id]) { Tracked[id] = true; LastX[id] = pos.x; LastY[id] = pos.y; Walked[id] = 0f; continue; }
            float mx = pos.x - LastX[id], my = pos.y - LastY[id];
            float step = MathF.Sqrt(mx * mx + my * my);
            LastX[id] = pos.x; LastY[id] = pos.y;
            if (step > 2f) { Walked[id] = 0f; continue; } // 飛んだ (通気口・転送)
            if (WetUntil[id] <= _clock) { Walked[id] = 0f; continue; }
            if (pc.inVent || pc.shouldAppearInvisible || !pc.Visible) continue;
            Walked[id] += step;
            if (Walked[id] < StepDist || step < 0.0001f) continue;
            Walked[id] = 0f;
            LeftFoot[id] = !LeftFoot[id];
            if (inWater)
            {
                _splashN = _splashN % 3 + 1;
                if (!MeetingHud.Instance) BreakNoise.PlayAt("noise_splash_" + _splashN, pos, SplashRange, SplashMuffle, SplashVolume);
                float floor = DamageMap.FrontZ(pos);
                AddRipple(pos.x, pos.y, 0.8f, floor - 0.0008f * DamageMap.ZScale(floor));
            }
            else
            {
                float fresh = (WetUntil[id] - _clock) / WetFor;
                DustCloud.PlacePrint(pos.x, pos.y, mx / step, my / step, LeftFoot[id],
                    PrintAlphaOld + (PrintAlphaFresh - PrintAlphaOld) * fresh, WetR, WetG, WetB);
            }
        }
    }

    // ── 毎フレーム ─────────────────────────────────────────────────────

    public static void Tick()
    {
        try { TickCore(); }
        catch (Exception e) { Plugin.Logger.LogError($"[WaterLeak] {e}"); Clear(); }
    }

    private static void TickCore()
    {
        if (GameClock.ShipGen != _shipGen) { _shipGen = GameClock.ShipGen; Clear(); }
        if (!WaterSim.Ready && Jets.Count == 0 && _liveDrops == 0 && _liveRings == 0 && _liveMist == 0 && !_anyWet) { _lastMs = 0; return; }

        long now = Environment.TickCount64;
        float dt = _lastMs == 0 ? 0.016f : Math.Min(0.05f, (now - _lastMs) / 1000f);
        _lastMs = now;
        if (!GameClock.ShipAlive) { Clear(); return; }
        _clock += dt;

        if (MeetingHud.Instance)
        {
            Jets.Clear();
            HideDrops();
        }
        else
        {
            TickJets(dt);
            TickLanding();
            TickDrops(dt);
            TickMist(dt);
            TickRipples(dt);
        }

        _scanAcc += dt;
        if (_scanAcc >= ScanInterval)
        {
            _scanAcc = 0f;
            Scan();
        }
    }

    private static void HideDrops()
    {
        foreach (var d in Drops)
            if (d != null && d.Live)
            {
                d.Live = false;
                if (d.Tr) d.Tr.gameObject.SetActive(false);
                if (d.ShTr) d.ShTr.gameObject.SetActive(false);
            }
        _liveDrops = 0;
        foreach (var r in Rings)
            if (r != null && r.Age < r.Life) { r.Age = r.Life; if (r.Sr) r.Sr.enabled = false; }
        _liveRings = 0;
        foreach (var m in Mists)
            if (m != null && m.Age < m.Life) { m.Age = m.Life; if (m.Sr) m.Sr.enabled = false; }
        _liveMist = 0;
    }

    private static void Clear()
    {
        Jets.Clear();
        WaterSpray.Clear();
        foreach (var go in Pipes) if (go) UnityEngine.Object.Destroy(go);
        Pipes.Clear();
        for (int i = 0; i < MaxMist; i++)
        {
            if (Mists[i]?.Tr) UnityEngine.Object.Destroy(Mists[i].Tr.gameObject);
            Mists[i] = null;
        }
        _nextMist = _liveMist = 0;
        for (int i = 0; i < MaxDrops; i++)
        {
            if (Drops[i]?.Tr) UnityEngine.Object.Destroy(Drops[i].Tr.gameObject);
            if (Drops[i]?.ShTr) UnityEngine.Object.Destroy(Drops[i].ShTr.gameObject);
            Drops[i] = null;
        }
        for (int i = 0; i < MaxRipples; i++)
        {
            if (Rings[i]?.Tr) UnityEngine.Object.Destroy(Rings[i].Tr.gameObject);
            Rings[i] = null;
        }
        _nextDrop = _nextRing = _liveDrops = _liveRings = 0;
        Array.Clear(WetUntil);
        Array.Clear(Tracked);
        _anyWet = false;
        _clock = _scanAcc = 0f;
        _lastMs = 0;
    }

    internal static void Register()
    {
        WaterSim.LeakStarted += StartJet;
        TestBridge.Register("leak", "[clear | here [dx dy] | rooms] 水漏れ: 噴き出し・水たまり・濡れた人 (here = 自分の足元で漏らす・壁は壊さない。rooms = このマップの水回りと自分のいる部屋)", (args, reply) =>
        {
            string a = args.Trim();
            var lp = PlayerControl.LocalPlayer;
            if (a == "clear") { Clear(); WaterSim.Reset(); Made = 0; Last = "-"; }
            else if (a.StartsWith("here") && lp)
            {
                var dir = new Vector2(lp.cosmetics && lp.cosmetics.FlipX ? -1f : 1f, 0f);
                var parts = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && float.TryParse(parts[1], out float dx) && float.TryParse(parts[2], out float dy) && dx * dx + dy * dy > 1e-6f)
                    dir = new Vector2(dx, dy).normalized;
                Start(lp.GetTruePosition(), dir, (ushort)Environment.TickCount, "here", 0.8f, GameClock.Stamp);
            }
            else if (a == "rooms")
            {
                var sb = new System.Text.StringBuilder();
                var ship = ShipStatus.Instance;
                if (ship)
                    foreach (var r in ship.AllRooms)
                        if (r && WaterRooms.Contains(r.RoomId.ToString())) sb.Append(' ').Append(r.RoomId);
                var me = lp ? TerrainDamage.RoomAt(lp.GetTruePosition()) : null;
                reply($"OK leak rooms=[{sb} ] here={(me ? me.RoomId.ToString() : "-")} water={(me && WaterRooms.Contains(me.RoomId.ToString()) ? "yes" : "no")}");
                return;
            }
            var wet = new System.Text.StringBuilder();
            for (int i = 0; i < 256; i++)
                if (WetUntil[i] > _clock) wet.Append($" {i}({WetUntil[i] - _clock:0.0}s)");
            reply($"OK leak made={Made} last={Last} jets={Jets.Count} pipes={Pipes.Count} drops={_liveDrops} mist={_liveMist} ripples={_liveRings} depthHere={(lp ? WaterSim.DepthAt(lp.GetTruePosition()) : 0)} {WaterArt.Describe()} {WaterSpray.Describe()} wet=[{wet}]");
        });
    }
}
