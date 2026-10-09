using System;
using System.Collections.Generic;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 外壁が抜けた時の演出 (見た目だけ・同期なし・全員の手元で同じ口から作る)。
// ① 開通の瞬間: 口の閃光 (暗い下地 → にじみ → 白い核)・瓦礫が回りながら宇宙へ・カメラの揺れ。爆発と同じフレームに出すため
//    気圧の計算 (同期のため数刻み遅れる) を待たずに破壊の適用から出す。追いつきでまとめて届いた古い開通には出さない。
// ② 船の中: 白い風の筋と紙くず・火花が流れ (Decompression.PullAt) に乗って口へ吸い込まれる。視界の中だけ (影の板より奥)。
// ③ 船の外: 口から宇宙へ白い霧が噴き出す (影の板より手前 = 壁越しでも見える)。強さ = 気圧 × 口の開き (泡がふさぐと細って止む)。
// エアシップは空へ開いても気圧が減らない弱い風なので、強さ 0.4 倍の灰色の雲にする。
// 絵と置き場は試合の始めに作り (TerrainWarm)、口が開いたら位置と色を書くだけにする (喉を開ける時の引っかかりに重ねない)。
// 出ていない時は managed のフラグで抜けて interop に触らない。算術は FxMath
internal static class DecompFx
{
    // ① 開通の瞬間
    private const float FlashTime = 0.25f;
    private const float FlashCore = 1.8f, FlashGlow = 5f, FlashUnder = 7f; // 直径 (単位)
    private const int Debris = 30;
    private const float DebrisLife = 1.5f, DebrisSpeedMin = 6f, DebrisSpeedMax = 10f, DebrisSpread = 35f;
    private const float ShakeReach = 14f, ShakeTime = 1.2f, ShakeHit = 0.9f;
    // 口が開いている間ずっと続く揺れ (強さ 1): どこにいても RumbleFar・口の際へ寄るほど RumbleNear まで
    private const float RumbleFar = 0.08f, RumbleNear = 0.25f, RumbleClose = 6f, RumbleReach = 30f;
    private const int CatchUpTicks = 45;     // これより古い開通 (途中参加・追いつき) には瞬間の演出を出さない
    private const long BurstWindowMs = 300;  // 窓あたり MaxBursts 回まで
    private const int MaxBursts = 2;

    // ② 船の中
    private const int Streaks = 140, Scraps = 30, Dusts = 30;
    private const float StreakReach = 6f;     // 口からこの距離以内に筋を置く
    private const float StreakSpeed = 10f;   // 引く強さ 1 (歩く速さの倍) の所の筋の速さ (実際の流れの 3 倍)
    private const float StreakLife = 1.6f;
    private const float StreakZ = -1f;        // クルー (z ≈ y/1000) より手前・影の板 (-5) より奥
    private const float NearMouth = 0.12f;    // ここまで来た筋は口の外へ抜けて消える

    // ③ 船の外
    private const int Puffs = 150;
    private const float PuffLife = 0.8f, PuffSpeed = 16f, PuffSpread = 18f, PuffDrag = 2.2f;
    private const float PuffSize0 = 0.2f, PuffSize1 = 1.2f, PuffAlpha = 0.6f;
    private const float PreTime = 2f;         // 気圧の計算が追いつくまで、開通の瞬間の口から噴かせておく上限
    private const int Rays = 70;              // 口から一直線に飛ぶ速い光の筋 (勢いを見せる)
    private const float RayLife = 0.35f, RaySpeedMin = 18f, RaySpeedMax = 26f, RaySpread = 12f;
    private const float BoostTime = 2f, BoostMax = 2.5f; // 開通から 2 秒は最大 2.5 倍の勢い
    private const float JetZ = -6f;           // 影の板 (-5) より手前
    private const float WindVis = 0.4f;
    private const float FarAway = 20f;        // 自分が口からこれより遠ければ船の外の霧も出さない
    // 口から宇宙へ出た水 (WaterSim.Spilled): 水色の霧と飛沫。濡れ具合 = 出た水の量 (深さ 1 単位の升 = 1) が毎秒 WetFade の割合で引く
    private const float WetReach = 2.5f;      // 出た水をこの距離以内の口の噴き出しに足す
    private const float WetFade = 2.5f, WetFull = 0.5f;
    private const int WetPuffs = 90, WetRays = 60;

    private const float PollEvery = 0.25f;    // 会議・自分の位置・揺れを読む間隔

    private enum Kind : byte { Streak, Scrap, Dust, Puff, Ray, Chip, Flash }

    private sealed class Bit
    {
        public Kind Kind;
        public Transform Tr;
        public SpriteRenderer Sr;
        public float Unit = 1f;                // 絵の大きさ (単位) の逆数
        public bool Alive;
        public float X, Y, Z, Vx, Vy, Age, Life, Size, Size1, Rot, Spin, Phase, A0, Shade;
        public float R = 1f, G = 1f, B = 1f;
        public bool Spark;                     // 紙くずのうち火花の絵
        public int AlphaQ = -1;
    }

    private sealed class Jet
    {
        public int Lines = -1;
        public float Mx, My, Nx, Ny, Width; // 口の中点・外向き・幅
        public float T0;                    // 手元の時計で見つけた時
        public float Until;                 // 先行の噴流 (気圧の計算が口を持つ前) の終わり
        public float I;                     // 強さ (0..1)
        public float PuffDebt, RayDebt, StreakDebt, ScrapDebt;
        public Bit Core;
        public float Gust = 1f, DustDebt;
        public float Wet, WetPuffDebt, WetRayDebt;
        public bool SeenOpen, Whistled, Popped; // 開いているのを見た・泡で狭まる笛が鳴った・ふさがった音を鳴らした
    }

    private static readonly List<Bit> All = new();
    private static readonly List<Bit> FreeStreak = new(), FreeScrap = new(), FreePuff = new(), FreeRay = new(), FreeDust = new(), FreeChip = new(), FreeFlash = new(), FreeCore = new();
    private static readonly Dictionary<Decompression.Breach, Jet> Jets = new();
    private static readonly List<Jet> Pre = new();
    private static readonly Queue<long> Recent = new();
    private static GameObject _root;
    private static float _ox, _oy, _isx = 1f, _isy = 1f; // 船の位置と拡大の逆数 (粒の位置を船の中の座標へ)
    private static Sprite _glow, _streak, _cone;
    private static Sprite[] _fog;
    private static int _shipGen = -1, _failedGen = -2;
    private static int _live;
    private static long _lastMs;
    private static float _clock, _pollAcc;
    private static bool _meeting, _shaking;
    private static float _hitT = -1f, _hitAmp, _rumble;
    private static float _wantX, _wantY, _appliedX, _appliedY; // 揺れでカメラに足したい量 / 前のフレームで足した量
    private static float _meX, _meY;
    private static bool _haveMe;

    // 確認用
    internal static int Bursts;
    internal static string Last = "-";

    // ── 準備 (TerrainWarm から・外壁を壊せる船だけ) ───────────────────────

    internal static void Warm()
    {
        if (!SolidMap.Breachable) return;
        try { EnsurePool(); }
        catch (Exception e) { Plugin.Logger.LogError($"[DecompFx] warm: {e}"); _failedGen = GameClock.ShipGen; }
    }

    private static bool EnsurePool()
    {
        if (GameClock.ShipGen != _shipGen) Forget();
        if (_root) return true;
        if (_failedGen == GameClock.ShipGen || !ShipStatus.Instance) return false;
        _shipGen = GameClock.ShipGen;
        _glow ??= Bake(64, (x, y) => { float r2 = (x * x + y * y) * 4f; float a = MathF.Exp(-r2 * 4.5f); return (a, 1f); }, "MrpDecompGlow");
        // 風の筋: 頭 (+x) が明るく尾へ細く薄れる。芯は白・縁は暗い灰 (明るい床でも沈まない)
        _streak ??= Bake(64, (x, y) =>
        {
            float u = x + 0.5f;                                  // 0 = 尾, 1 = 頭
            float half = 0.07f + 0.13f * u;                     // 太さ (縦 -0.5〜0.5 のうち)
            float d = MathF.Abs(y) / half;
            if (d >= 1f) return (0f, 0f);
            float along = u < 0.85f ? u / 0.85f : (1f - u) / 0.15f;
            float a = (1f - d * d) * along * along;
            float lum = d < 0.45f ? 1f : 1f - (d - 0.45f) / 0.55f * 0.75f;
            return (a, lum);
        }, "MrpDecompStreak");

        _fog ??= new[] { Fog(1), Fog(2), Fog(3) };
        // 噴流の芯: 口 (-x) で細く明るく、先 (+x) へ広く薄く
        _cone ??= Bake(64, (x, y) =>
        {
            float u = x + 0.5f;
            float half = 0.08f + 0.38f * u;
            float d = y / half;
            float a = MathF.Exp(-d * d * 2.2f) * MathF.Pow(1f - u, 1.3f) * MathF.Min(1f, u / 0.06f);
            return (a, 1f);
        }, "MrpDecompCone");

        _root = new GameObject("MrpDecompFx") { layer = 0 };
        _root.transform.SetParent(ShipStatus.Instance.transform, false);
        // 船は拡大されている (スケルドは 1.2 倍) ので、粒の位置だけ船の中の座標へ割り戻す (大きさと奥行きは船に合わせたまま)
        var st = ShipStatus.Instance.transform;
        var sp = st.position;
        var ss = st.lossyScale;
        _ox = sp.x; _oy = sp.y;
        _isx = MathF.Abs(ss.x) > 1e-4f ? 1f / ss.x : 1f;
        _isy = MathF.Abs(ss.y) > 1e-4f ? 1f / ss.y : 1f;
        GameClock.Ship.Bind(_root);
        GameClock.Ship.OnRelease(Forget);

        for (int i = 0; i < Streaks; i++) FreeStreak.Add(Make(Kind.Streak, _streak, i));
        for (int i = 0; i < Scraps; i++)
        {
            bool spark = (i & 1) == 0;
            var b = Make(Kind.Scrap, spark ? DebrisArt.Spark : DebrisArt.Plate(i), i);
            b.Spark = spark;
            FreeScrap.Add(b);
        }
        for (int i = 0; i < Puffs; i++) FreePuff.Add(Make(Kind.Puff, _fog[i % _fog.Length], i));
        for (int i = 0; i < Rays; i++) FreeRay.Add(Make(Kind.Ray, _streak, i));
        for (int i = 0; i < Dusts; i++) FreeDust.Add(Make(Kind.Dust, _fog[i % _fog.Length], i));
        for (int i = 0; i < Debris; i++) FreeChip.Add(Make(Kind.Chip, (i % 3) == 0 ? DebrisArt.Plate(i) : DebrisArt.Chunk(i), i));
        for (int i = 0; i < 3 * MaxBursts; i++) FreeFlash.Add(Make(Kind.Flash, _glow, i));
        for (int i = 0; i < 4; i++) FreeCore.Add(Make(Kind.Flash, _cone, 100 + i));
        Plugin.Logger.LogInfo($"[DecompFx] pool={All.Count}");
        return true;
    }

    private static Bit Make(Kind kind, Sprite sp, int i)
    {
        var go = new GameObject("MrpDecompBit") { layer = 0 };
        go.transform.SetParent(_root.transform, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        sr.enabled = false;
        var rect = sp.rect;
        float w = MathF.Max(rect.width, rect.height) / sp.pixelsPerUnit;
        var b = new Bit { Kind = kind, Tr = go.transform, Sr = sr, Unit = w > 1e-4f ? 1f / w : 1f, Phase = i * 2.39996f };
        All.Add(b);
        return b;
    }

    // ── ① 開通の瞬間 (Decompression.OnApplied から・全員の手元) ──────────────

    internal static void OnBreach(int tick, List<(Vector2 A, Vector2 B)> lines, float width, Vector2 away)
    {
        try
        {
            if (lines == null || lines.Count == 0 || width <= 0f) return;
            if (GameClock.Now - tick > CatchUpTicks) { Last = "skip (late)"; return; }
            long now = Environment.TickCount64;
            while (Recent.Count > 0 && now - Recent.Peek() > BurstWindowMs) Recent.Dequeue();
            if (Recent.Count >= MaxBursts) { Last = "skip (burst)"; return; }
            if (!EnsurePool()) return;
            Recent.Enqueue(now);
            Frame(lines, away, out float mx, out float my, out float nx, out float ny, out float w);
            float k = Decompression.Wind ? WindVis : 1f;
            Burst(mx, my, nx, ny, w, k);
            if (FindJet(Pre, mx, my) == null && !NearOpenJet(mx, my))
                Pre.Add(new Jet { T0 = _clock, Until = _clock + PreTime, Mx = mx, My = my, Nx = nx, Ny = ny, Width = w, I = k });
            Shake(mx, my, k);
            BreakNoise.PlayAt("noise_decomp_breach", FxMath.V2(mx, my), 40f, 14f, 1f);
            Bursts++;
            Last = $"burst at {mx:0.00},{my:0.00} n={nx:0.00},{ny:0.00} w={w:0.00}";
        }
        catch (Exception e) { Plugin.Logger.LogError($"[DecompFx] breach: {e}"); }
    }

    private static void Burst(float mx, float my, float nx, float ny, float w, float k)
    {
        // 閃光: 奥から暗い下地・青白いにじみ・白い核
        Flash(mx, my, JetZ - 0.30f, FlashUnder * (0.7f + 0.3f * k), 0.02f, 0.03f, 0.06f, 0.45f * k);
        Flash(mx + nx * 0.2f, my + ny * 0.2f, JetZ - 0.31f, FlashGlow * (0.6f + 0.4f * k), 0.75f, 0.88f, 1f, 0.9f * k);
        Flash(mx + nx * 0.1f, my + ny * 0.1f, JetZ - 0.32f, FlashCore, 1f, 1f, 1f, k);
        int n = (int)(Debris * (0.4f + 0.6f * k));
        float baseAng = FxMath.Atan2(ny, nx);
        for (int i = 0; i < n && FreeChip.Count > 0; i++)
        {
            var b = Take(FreeChip);
            float t = FxMath.Value - 0.5f;
            float ang = baseAng + FxMath.Range(-DebrisSpread, DebrisSpread) * (MathF.PI / 180f);
            float sp = FxMath.Range(DebrisSpeedMin, DebrisSpeedMax) * (0.6f + 0.4f * k);
            b.X = mx - ny * t * w; b.Y = my + nx * t * w; b.Z = JetZ - 0.2f - i * 0.001f;
            b.Vx = FxMath.Cos(ang) * sp; b.Vy = FxMath.Sin(ang) * sp;
            b.Life = DebrisLife * FxMath.Range(0.7f, 1f);
            b.Size = FxMath.Range(0.18f, 0.38f);
            b.Rot = FxMath.Range(0f, 360f);
            b.Spin = FxMath.Range(360f, 720f) * (FxMath.Value < 0.5f ? -1f : 1f);
            b.R = b.G = b.B = 1f; b.A0 = 1f;
            Spawn(b);
        }
    }

    private static void Flash(float x, float y, float z, float size, float r, float g, float bl, float a)
    {
        if (FreeFlash.Count == 0) return;
        var b = Take(FreeFlash);
        b.X = x; b.Y = y; b.Z = z; b.Vx = b.Vy = 0f;
        b.Life = FlashTime; b.Size = size; b.Rot = 0f; b.Spin = 0f;
        b.R = r; b.G = g; b.B = bl; b.A0 = a;
        Spawn(b);
    }

    // 口に近い人ほど強く揺らす。吸い出しの揺れは演出の芯なので本編の「画面の揺れ」設定に関わらず出し、カメラのずらし量を自分で揺らす
    private static void Shake(float mx, float my, float k)
    {
        if (!ReadMe()) return;
        float dx = _meX - mx, dy = _meY - my, d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d >= ShakeReach) return;
        _hitT = 0f;
        _hitAmp = ShakeHit * (1f - d / ShakeReach) * k;
    }

    // 毎フレーム: 開通の揺れ (減っていく) と口のそばの細かい揺れの大きい方。揺れていない時はカメラに触らない
    private static void StepShake(float dt)
    {
        float amp = 0f;
        if (_hitT >= 0f)
        {
            _hitT += dt;
            if (_hitT >= ShakeTime) _hitT = -1f;
            else { float u = 1f - _hitT / ShakeTime; amp = _hitAmp * u * u; }
        }
        amp = FxMath.Max(amp, _rumble);
        if (amp <= 0.002f)
        {
            if (_shaking) StopShake();
            return;
        }
        _shaking = true;
        float t = _clock;
        _wantX = (FxMath.Sin(t * 71f) + 0.6f * FxMath.Sin(t * 113f + 1.7f)) * 0.62f * amp;
        _wantY = (FxMath.Sin(t * 83f + 0.4f) + 0.6f * FxMath.Sin(t * 97f + 2.9f)) * 0.62f * amp;
    }

    // カメラの追従が終わった後 (LateUpdate) に、前のフレームで足した揺れを引いて今の揺れを足す。
    // 追従の目標 (Offset) を揺らすと追従のなめらかさで揺れが鈍るので、カメラの位置そのものを動かす
    internal static void LateTick()
    {
        if (!_shaking && _appliedX == 0f && _appliedY == 0f) return;
        try
        {
            var cam = PlayerCam();
            if (!cam) { _appliedX = _appliedY = 0f; return; }
            float nx = _shaking ? _wantX : 0f, ny = _shaking ? _wantY : 0f;
            var tr = cam.transform;
            var pos = tr.position;
            tr.position = FxMath.V3(pos.x + nx - _appliedX, pos.y + ny - _appliedY, pos.z);
            _appliedX = nx; _appliedY = ny;
        }
        catch (Exception e)
        {
            _appliedX = _appliedY = 0f;
            _shaking = false;
            Plugin.Logger.LogError($"[DecompFx] shake: {e}");
        }
    }

    private static void StopShake()
    {
        _shaking = false;
        _hitT = -1f;
        _rumble = 0f;
        _wantX = _wantY = 0f; // 足していた分は次の LateTick で引く
    }

    private static FollowerCamera PlayerCam() { var hud = Vanilla.Hud; return hud ? hud.PlayerCam : null; }

    // ── 毎フレーム ─────────────────────────────────────────────────────

    public static void Tick()
    {
        if (GameClock.ShipGen != _shipGen && _root is not null) Forget();
        bool any = Pre.Count > 0 || (Decompression.Running && Decompression.OpenedBreaches.Count > 0);
        if (!any && _live == 0 && !_shaking && _hitT < 0f) { _lastMs = 0; return; }
        if (_failedGen == GameClock.ShipGen) return;
        try { TickCore(any); }
        catch (Exception e)
        {
            // 同じ船では作り直しても同じ所で落ちるので、この試合の演出は止める
            Plugin.Logger.LogError($"[DecompFx] tick: {e}");
            _failedGen = GameClock.ShipGen;
            HideAll();
        }
    }

    private static void TickCore(bool any)
    {
        if (!EnsurePool()) return;
        long now = Environment.TickCount64;
        float dt = _lastMs == 0 ? 0.016f : Math.Min(0.1f, (now - _lastMs) / 1000f);
        _lastMs = now;
        _clock += dt;
        _pollAcc += dt;
        if (_pollAcc >= PollEvery)
        {
            _pollAcc = 0f;
            bool m = MeetingHud.Instance;
            if (m && !_meeting) HideAll();
            _meeting = m;
            ReadMe();
        }
        if (_meeting) { if (_shaking) StopShake(); return; }
        _rumble = 0f;
        if (!Decompression.Running && Jets.Count > 0) DropJets(); // 気圧の計算が止まった (口が全部消えた) ら芯を返す
        if (any) Emit(dt);
        StepShake(dt);
        for (int i = 0; i < All.Count; i++)
        {
            var b = All[i];
            if (b.Alive) Step(b, dt);
        }
    }

    private static void Emit(float dt)
    {
        float k = Decompression.Wind ? WindVis : 1f;
        if (Decompression.Running)
        {
            var list = Decompression.OpenedBreaches;
            for (int i = 0; i < list.Count; i++)
            {
                var br = list[i];
                if (!Jets.TryGetValue(br, out var j))
                {
                    Frame(br.Lines, br.Away, out float mx, out float my, out _, out _, out _);
                    // 開通の瞬間から噴いていた先行の噴流を引き継ぐ (勢いと芯をそのまま)
                    j = FindJet(Pre, mx, my) ?? new Jet { T0 = _clock };
                    Pre.Remove(j);
                    j.Lines = -1;
                    Jets[br] = j;
                }
                if (j.Lines != br.Lines.Count)
                {
                    j.Lines = br.Lines.Count;
                    Frame(br.Lines, br.Away, out j.Mx, out j.My, out j.Nx, out j.Ny, out j.Width);
                }
                j.I = Decompression.MouthPull(br) * k;
                float open = Decompression.Open(br);
                if (!br.Sealed) { j.SeenOpen = true; if (open < 1f) j.Whistled = true; }
                // ふさがった: 泡が閉じ切った・物がふさいだ (物の時は笛が無くても空気が急に止まる)。途中参加で最初から閉じていた口は鳴らさない
                else if (j.SeenOpen && !j.Popped && (j.Whistled || br.ByProp))
                {
                    j.Popped = true;
                    DecompSound.Pops++;
                    BreakNoise.PlayAt("noise_decomp_seal", FxMath.V2(j.Mx, j.My), 24f, 10f, 0.9f);
                }
                EmitJet(j, dt, true, open);
            }
            TakeSpills(dt);
        }
        for (int i = Pre.Count - 1; i >= 0; i--)
        {
            var j = Pre[i];
            if (_clock >= j.Until) j.I = 0f;
            EmitJet(j, dt, false, 1f);
            if (j.I <= 0f) Pre.RemoveAt(i);
        }
    }

    private static void EmitJet(Jet j, float dt, bool inside, float open)
    {
        UpdateCore(j);
        if (j.I <= 0.01f) return;
        float dx = _meX - j.Mx, dy = _meY - j.My;
        // 強さ 0.4 (泡の前の下げ止め) でも勢いが残るように見た目の強さは平方根で
        float vis = FxMath.Sqrt(j.I);
        float d = FxMath.Sqrt(dx * dx + dy * dy);
        // 突風: 噴き出しの強さを不規則に脈打たせる
        float gust = 0.8f + 0.2f * FxMath.Sin(_clock * 5.3f + j.Mx) + 0.15f * FxMath.Sin(_clock * 13.7f + j.My);
        j.Gust = gust;
        if (_haveMe && d < RumbleReach)
            _rumble = FxMath.Max(_rumble, vis * j.Gust * (RumbleFar + (RumbleNear - RumbleFar) * FxMath.Max(0f, 1f - d / RumbleClose)));
        if (_haveMe) DecompSound.Feed(vis, gust, j.Mx - _meX, j.My - _meY, open, inside);
        if (_haveMe && d > FarAway) return;
        float boost = _clock - j.T0 < BoostTime ? BoostMax - (BoostMax - 1f) * (_clock - j.T0) / BoostTime : 1f;
        boost *= gust;
        j.PuffDebt += dt * Puffs / PuffLife * vis * boost;
        while (j.PuffDebt >= 1f && FreePuff.Count > 0) { j.PuffDebt -= 1f; SpawnPuff(j, vis, boost); }
        if (j.PuffDebt > 2f) j.PuffDebt = 2f;
        j.RayDebt += dt * Rays / RayLife * vis * boost * (Decompression.Wind ? 0.5f : 1f);
        while (j.RayDebt >= 1f && FreeRay.Count > 0) { j.RayDebt -= 1f; SpawnRay(j, vis, boost); }
        if (j.RayDebt > 2f) j.RayDebt = 2f;
        if (!inside || (_haveMe && dx * dx + dy * dy > (StreakReach + 8f) * (StreakReach + 8f))) return;
        j.StreakDebt += dt * Streaks / StreakLife * vis * 1.5f;
        while (j.StreakDebt >= 1f && FreeStreak.Count > 0) { j.StreakDebt -= 1f; SpawnInside(j, Kind.Streak, vis); }
        if (j.StreakDebt > 2f) j.StreakDebt = 2f;
        j.ScrapDebt += dt * Scraps / StreakLife * vis;
        while (j.ScrapDebt >= 1f && FreeScrap.Count > 0) { j.ScrapDebt -= 1f; SpawnInside(j, Kind.Scrap, vis); }
        if (j.ScrapDebt > 2f) j.ScrapDebt = 2f;
        j.DustDebt += dt * Dusts / StreakLife * vis;
        while (j.DustDebt >= 1f && FreeDust.Count > 0) { j.DustDebt -= 1f; SpawnInside(j, Kind.Dust, vis); }
        if (j.DustDebt > 2f) j.DustDebt = 2f;
    }

    // 宇宙へ出た水を近い口の噴き出しに足し、濡れている口から水色の霧と飛沫を噴く
    private static void TakeSpills(float dt)
    {
        var sp = WaterSim.Spilled;
        for (int i = 0; i < sp.Count; i++)
        {
            Jet best = null;
            float bd = WetReach * WetReach;
            foreach (var j in Jets.Values)
            {
                float dx = j.Mx - sp[i].X, dy = j.My - sp[i].Y, d = dx * dx + dy * dy;
                if (d < bd) { bd = d; best = j; }
            }
            if (best != null) best.Wet += sp[i].Amount / (float)WaterSim.Full;
        }
        foreach (var j in Jets.Values)
        {
            if (j.Wet <= 0.002f) { j.Wet = 0f; continue; }
            float k = FxMath.Min(1f, j.Wet / WetFull);
            j.Wet -= j.Wet * FxMath.Min(1f, dt * WetFade);
            float dx = _meX - j.Mx, dy = _meY - j.My;
            if (_haveMe && dx * dx + dy * dy > FarAway * FarAway) continue;
            float boost = FxMath.Max(1f, j.Gust);
            j.WetPuffDebt += dt * WetPuffs / PuffLife * k;
            while (j.WetPuffDebt >= 1f && FreePuff.Count > 0) { j.WetPuffDebt -= 1f; SpawnPuff(j, 1f, boost, true); }
            if (j.WetPuffDebt > 2f) j.WetPuffDebt = 2f;
            j.WetRayDebt += dt * WetRays / RayLife * k;
            while (j.WetRayDebt >= 1f && FreeRay.Count > 0) { j.WetRayDebt -= 1f; SpawnRay(j, 1f, boost, true); }
            if (j.WetRayDebt > 2f) j.WetRayDebt = 2f;
        }
    }

    private static void DropJets()
    {
        foreach (var j in Jets.Values)
        {
            j.I = 0f;
            UpdateCore(j);
        }
        Jets.Clear();
    }

    private const float SameMouth = 1.5f;

    private static Jet FindJet(List<Jet> list, float x, float y)
    {
        foreach (var j in list)
            if ((j.Mx - x) * (j.Mx - x) + (j.My - y) * (j.My - y) <= SameMouth * SameMouth) return j;
        return null;
    }

    // 同じ口がもう噴いている (同じ穴を広げた爆発) なら先行の噴流は作らない
    private static bool NearOpenJet(float x, float y)
    {
        foreach (var j in Jets.Values)
            if (j.I > 0.01f && (j.Mx - x) * (j.Mx - x) + (j.My - y) * (j.My - y) <= SameMouth * SameMouth) return true;
        return false;
    }

    // 口に沿った細長い青白い光 (噴流の芯)
    private static void UpdateCore(Jet j)
    {
        if (j.I <= 0.01f)
        {
            if (j.Core != null) { j.Core.Alive = false; j.Core.Sr.enabled = false; _live--; FreeCore.Add(j.Core); j.Core = null; }
            return;
        }
        if (j.Core == null)
        {
            if (FreeCore.Count == 0) return;
            var b = Take(FreeCore);
            b.Life = float.MaxValue; b.Spin = 0f; b.Vx = b.Vy = 0f;
            b.R = 0.8f; b.G = 0.9f; b.B = 1f;
            j.Core = b;
            Spawn(b);
        }
        var c = j.Core;
        float len = 6.5f, wid = MathF.Max(1.4f, j.Width * 1.8f);
        c.X = j.Mx + j.Nx * len * 0.5f; c.Y = j.My + j.Ny * len * 0.5f; c.Z = JetZ + 0.05f;
        c.Rot = FxMath.Atan2(j.Ny, j.Nx) * (180f / MathF.PI);
        c.Size = len; c.Size1 = wid;
        c.A0 = (0.5f + 0.4f * j.I) * j.Gust * (Decompression.Wind ? 0.5f : 1f);
    }

    private static void SpawnPuff(Jet j, float vis, float boost, bool wet = false)
    {
        var b = Take(FreePuff);
        float t = FxMath.Value - 0.5f;
        float ang = FxMath.Atan2(j.Ny, j.Nx) + FxMath.Range(-PuffSpread, PuffSpread) * (MathF.PI / 180f);
        float sp = PuffSpeed * boost * FxMath.Range(0.8f, 1.15f);
        b.X = j.Mx - j.Ny * t * j.Width * 0.9f + j.Nx * 0.05f;
        b.Y = j.My + j.Nx * t * j.Width * 0.9f + j.Ny * 0.05f;
        b.Z = JetZ - 0.1f - (b.Phase % 1f) * 0.05f;
        b.Vx = FxMath.Cos(ang) * sp; b.Vy = FxMath.Sin(ang) * sp;
        b.Life = PuffLife * FxMath.Range(0.8f, 1.2f);
        b.Size = PuffSize0 * FxMath.Range(0.8f, 1.2f);
        b.Size1 = PuffSize1 * FxMath.Range(0.8f, 1.2f) * (0.6f + 0.4f * MathF.Min(1f, j.Width));
        b.Rot = ang * (180f / MathF.PI);
        b.Spin = FxMath.Range(-40f, 40f);
        if (wet) { b.R = 0.42f; b.G = 0.66f; b.B = 0.95f; b.Size1 *= 0.7f; }
        else if (Decompression.Wind) { b.R = 0.78f; b.G = 0.8f; b.B = 0.84f; }
        else { b.R = 0.86f; b.G = 0.93f; b.B = 1f; }
        b.A0 = wet ? 0.8f : PuffAlpha * MathF.Min(1f, 0.35f + 0.65f * vis);
        Spawn(b);
    }

    private static void SpawnRay(Jet j, float vis, float boost, bool wet = false)
    {
        var b = Take(FreeRay);
        float t = FxMath.Value - 0.5f;
        float ang = FxMath.Atan2(j.Ny, j.Nx) + FxMath.Range(-RaySpread, RaySpread) * (MathF.PI / 180f);
        float sp = FxMath.Range(RaySpeedMin, RaySpeedMax) * (0.75f + 0.25f * boost);
        b.X = j.Mx - j.Ny * t * j.Width * 0.8f;
        b.Y = j.My + j.Nx * t * j.Width * 0.8f;
        b.Z = JetZ - 0.16f - (b.Phase % 1f) * 0.01f;
        b.Vx = FxMath.Cos(ang) * sp; b.Vy = FxMath.Sin(ang) * sp;
        b.Rot = ang * (180f / MathF.PI);
        b.Life = RayLife * FxMath.Range(0.7f, 1.2f);
        b.Size = FxMath.Range(1.8f, 3.4f);
        if (wet) { b.Size *= 0.5f; b.R = 0.55f; b.G = 0.78f; b.B = 1f; }
        else { b.R = 0.9f; b.G = 0.96f; b.B = 1f; }
        b.A0 = 0.85f * MathF.Min(1f, 0.4f + 0.6f * vis);
        Spawn(b);
    }

    // 口から StreakReach 以内の流れのある所に置く。外れたら今回は置かない
    private static void SpawnInside(Jet j, Kind kind, float vis)
    {
        for (int tries = 0; tries < 4; tries++)
        {
            var off = FxMath.InsideUnitCircle();
            float px = j.Mx + off.x * StreakReach, py = j.My + off.y * StreakReach;
            float side = (px - j.Mx) * j.Nx + (py - j.My) * j.Ny;
            if (side > 0f) { px -= 2f * side * j.Nx; py -= 2f * side * j.Ny; } // 口の外側に引いたら内側へ折り返す
            var at = FxMath.V2(px, py);
            if (!Decompression.PullAt(at, out _, out float mul, out _) || mul < 0.08f || SolidMap.Solid(at)) continue;
            var b = Take(kind == Kind.Streak ? FreeStreak : kind == Kind.Dust ? FreeDust : FreeScrap);
            b.X = px; b.Y = py;
            b.Vx = b.Vy = 0f;
            b.Age = 0f;
            b.Life = StreakLife * FxMath.Range(0.7f, 1.1f);
            if (kind == Kind.Streak)
            {
                b.Z = StreakZ - (b.Phase % 1f) * 0.01f;
                b.R = b.G = b.B = 1f;
                b.A0 = 0.75f * MathF.Min(1f, 0.4f + 0.6f * vis);
            }
            else if (kind == Kind.Dust)
            {
                // 床の砂ぼこり: 筋より奥 (床に近い) に薄く大きく
                b.Z = StreakZ + 0.02f;
                b.Size = FxMath.Range(0.5f, 0.9f);
                b.R = 0.78f; b.G = 0.74f; b.B = 0.68f;
                b.A0 = 0.32f * MathF.Min(1f, 0.4f + 0.6f * vis);
            }
            else
            {
                b.Z = StreakZ - 0.02f;
                b.Size = FxMath.Range(0.06f, 0.12f);
                b.Rot = FxMath.Range(0f, 360f);
                b.Spin = FxMath.Range(-720f, 720f);
                if (b.Spark) { b.R = 1f; b.G = 0.85f; b.B = 0.55f; } else { b.R = 0.9f; b.G = 0.9f; b.B = 0.86f; }
                b.A0 = 0.9f;
            }
            b.Shade = 0f; // 口の外へ抜けてからの秒数 (0 = まだ中)
            Spawn(b);
            return;
        }
    }

    private static void Step(Bit b, float dt)
    {
        b.Age += dt;
        if (b.Age >= b.Life) { Kill(b); return; }
        float u = b.Life < 1e9f ? b.Age / b.Life : 0f;
        float a, sx, sy;
        switch (b.Kind)
        {
            case Kind.Streak:
            case Kind.Scrap:
            case Kind.Dust:
                if (!Inside(b, dt)) { Kill(b); return; }
                a = b.A0 * FxMath.Min(1f, b.Age / 0.15f) * (1f - u * u) * (b.Shade > 0f ? 1f - b.Shade / 0.15f : 1f);
                if (b.Kind == Kind.Streak)
                {
                    float sp = FxMath.Sqrt(b.Vx * b.Vx + b.Vy * b.Vy);
                    sx = FxMath.Clamp(0.3f + sp * 0.11f, 0.35f, 1.5f);
                    sy = FxMath.Min(0.32f, sx * 0.26f);
                    b.Rot = FxMath.Atan2(b.Vy, b.Vx) * (180f / MathF.PI);
                }
                else if (b.Kind == Kind.Dust)
                {
                    float sp = FxMath.Sqrt(b.Vx * b.Vx + b.Vy * b.Vy);
                    b.Rot = FxMath.Atan2(b.Vy, b.Vx) * (180f / MathF.PI);
                    sy = b.Size;
                    sx = b.Size * (1f + FxMath.Min(1.5f, sp * 0.12f));
                }
                else
                {
                    b.Rot += b.Spin * dt;
                    sx = sy = b.Size;
                }
                break;
            case Kind.Puff:
            {
                float drag = 1f - PuffDrag * dt;
                b.Vx *= drag; b.Vy *= drag;
                b.X += b.Vx * dt; b.Y += b.Vy * dt;
                b.Rot += b.Spin * dt;
                float e = 1f - (1f - u) * (1f - u);
                sy = b.Size + (b.Size1 - b.Size) * e;
                sx = sy * (1f + 0.7f * (1f - u) * (1f - u)); // 出たばかりは進む向きに伸ばして流れに見せる
                a = b.A0 * FxMath.Min(1f, b.Age / 0.08f) * FxMath.Pow(1f - u, 1.5f);
                break;
            }
            case Kind.Ray:
                b.X += b.Vx * dt; b.Y += b.Vy * dt;
                sx = b.Size * (0.6f + 0.4f * u);
                sy = 0.09f;
                a = b.A0 * FxMath.Min(1f, b.Age / 0.04f) * (1f - u);
                break;
            case Kind.Chip:
                b.Vx *= 1f - 0.4f * dt; b.Vy *= 1f - 0.4f * dt;
                b.X += b.Vx * dt; b.Y += b.Vy * dt;
                b.Rot += b.Spin * dt;
                sx = sy = b.Size * (1f - 0.4f * u);
                a = u < 0.66f ? 1f : (1f - u) / 0.34f;
                break;
            default: // Flash と芯
                if (b.Life < 1e9f)
                {
                    sx = sy = b.Size * (1f + 0.3f * u);
                    a = b.A0 * (1f - u) * (1f - u);
                }
                else
                {
                    sx = b.Size; sy = b.Size1;
                    a = b.A0 * (0.85f + 0.15f * FxMath.Sin(_clock * 31f + b.Phase));
                }
                break;
        }
        b.Tr.localPosition = FxMath.V3((b.X - _ox) * _isx, (b.Y - _oy) * _isy, b.Z);
        b.Tr.localRotation = FxMath.RotZ(b.Rot);
        b.Tr.localScale = FxMath.V3(sx * b.Unit, sy * b.Unit, 1f);
        int q = (int)(FxMath.Clamp01(a) * 64f);
        if (q != b.AlphaQ)
        {
            b.AlphaQ = q;
            b.Sr.color = FxMath.Rgba(b.R, b.G, b.B, q / 64f);
        }
    }

    // 船の中の筋と紙くず: 流れに乗って口へ。口に着いたら外向きに少し抜けて消える。流れの無い所へ出たら消える
    private static bool Inside(Bit b, float dt)
    {
        if (b.Shade > 0f)
        {
            b.Shade += dt;
            if (b.Shade >= 0.15f) return false;
        }
        else
        {
            var p = FxMath.V2(b.X, b.Y);
            if (!Decompression.PullAt(p, out var dir, out float mul, out _)) return false;
            float md = Decompression.MouthDist(p, out _);
            if (md > 0.5f && SolidMap.Solid(p)) return false; // 船体の上 (部屋の外) を横切らせない
            float sp = StreakSpeed * mul;
            float vx = dir.x * sp, vy = dir.y * sp;
            if (b.Kind == Kind.Scrap)
            {
                // 渦: 流れに直角の揺れ
                float w = FxMath.Sin(b.Age * 9f + b.Phase) * sp * 0.5f;
                vx += -dir.y * w; vy += dir.x * w;
            }
            // 急に向きを変えない (角を回る時に筋が折れて見えないように)
            float f = FxMath.Min(1f, dt * 10f);
            b.Vx += (vx - b.Vx) * f; b.Vy += (vy - b.Vy) * f;
            if (md <= NearMouth) b.Shade = 1e-4f;
        }
        b.X += b.Vx * dt; b.Y += b.Vy * dt;
        return true;
    }

    // ── 置き場 ─────────────────────────────────────────────────────────

    private static Bit Take(List<Bit> free)
    {
        var b = free[free.Count - 1];
        free.RemoveAt(free.Count - 1);
        return b;
    }

    private static void Spawn(Bit b)
    {
        b.Alive = true;
        b.Age = 0f;
        b.AlphaQ = -1;
        b.Sr.color = FxMath.Rgba(b.R, b.G, b.B, 0f);
        b.Sr.enabled = true;
        _live++;
        Step(b, 0f);
    }

    private static void Kill(Bit b)
    {
        if (!b.Alive) return;
        b.Alive = false;
        b.Sr.enabled = false;
        _live--;
        if (b.Life >= 1e9f) return; // 芯は口が持つ (UpdateCore が返す)
        (b.Kind switch
        {
            Kind.Streak => FreeStreak,
            Kind.Scrap => FreeScrap,
            Kind.Puff => FreePuff,
            Kind.Ray => FreeRay,
            Kind.Dust => FreeDust,
            Kind.Chip => FreeChip,
            _ => FreeFlash,
        }).Add(b);
    }

    private static void HideAll()
    {
        if (_shaking) { try { StopShake(); } catch { _shaking = false; } }
        foreach (var b in All) if (b.Alive) Kill(b); // 芯 (寿命なし) は空き置き場へ戻さず、下で口から外す
        foreach (var j in Jets.Values) if (j.Core != null) { FreeCore.Add(j.Core); j.Core = null; }
        foreach (var j in Pre) if (j.Core != null) { FreeCore.Add(j.Core); j.Core = null; }
        Jets.Clear();
        Pre.Clear();
    }

    private static void Forget()
    {
        if (_shaking) { try { StopShake(); } catch { _shaking = false; } }
        All.Clear();
        FreeStreak.Clear(); FreeScrap.Clear(); FreePuff.Clear(); FreeRay.Clear(); FreeDust.Clear(); FreeChip.Clear(); FreeFlash.Clear(); FreeCore.Clear();
        Jets.Clear();
        Pre.Clear();
        Recent.Clear();
        _root = null;
        _live = 0;
        _meeting = false;
        _haveMe = false;
        _shipGen = GameClock.ShipGen;
    }

    private static bool ReadMe()
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp) { _haveMe = false; return false; }
        var p = lp.GetTruePosition();
        _meX = p.x; _meY = p.y; _haveMe = true;
        return true;
    }

    // 口の線の端どうしのいちばん遠い 2 点を口の両端とし、歩ける側の反対を外向きにする
    // away = 喉を掘った向き (斜めに張った口では線の法線と違う。噴き出しは喉に沿わせる)。無ければ線の法線
    private static void Frame(List<(Vector2 A, Vector2 B)> lines, Vector2 away, out float mx, out float my, out float nx, out float ny, out float w)
    {
        Vector2 a = lines[0].A, b = lines[0].B;
        float best = -1f;
        for (int i = 0; i < lines.Count * 2; i++)
        for (int k = i + 1; k < lines.Count * 2; k++)
        {
            Vector2 p = (i & 1) == 0 ? lines[i >> 1].A : lines[i >> 1].B, q = (k & 1) == 0 ? lines[k >> 1].A : lines[k >> 1].B;
            float d = (p.x - q.x) * (p.x - q.x) + (p.y - q.y) * (p.y - q.y);
            if (d > best) { best = d; a = p; b = q; }
        }
        w = FxMath.Sqrt(MathF.Max(0f, best));
        mx = (a.x + b.x) * 0.5f; my = (a.y + b.y) * 0.5f;
        float tx = b.x - a.x, ty = b.y - a.y, tl = FxMath.Sqrt(tx * tx + ty * ty);
        if (tl < 1e-4f) { tx = 1f; ty = 0f; tl = 1f; }
        nx = -ty / tl; ny = tx / tl;
        float al = FxMath.Sqrt(away.x * away.x + away.y * away.y);
        if (al > 1e-4f) { nx = away.x / al; ny = away.y / al; return; }
        // 向きの符号は最初の線の中点で: 外へずらした所が歩けず、内へずらした所が歩ける向き
        var l0 = lines[0];
        float cx = (l0.A.x + l0.B.x) * 0.5f, cy = (l0.A.y + l0.B.y) * 0.5f;
        int score = 0;
        foreach (float s in new[] { 0.3f, 0.6f })
        {
            if (SolidMap.Solid(FxMath.V2(cx + nx * s, cy + ny * s))) score++;
            if (!SolidMap.Solid(FxMath.V2(cx - nx * s, cy - ny * s))) score++;
            if (SolidMap.Solid(FxMath.V2(cx - nx * s, cy - ny * s))) score--;
            if (!SolidMap.Solid(FxMath.V2(cx + nx * s, cy + ny * s))) score--;
        }
        if (score < 0) { nx = -nx; ny = -ny; }
    }

    // 霧の塊: ぼかした玉を重ねた柔らかい雲 (縁に線を作らない・上がわずかに明るい)。種で形を変える
    private static Sprite Fog(int seed)
    {
        var rnd = new System.Random(4100 + seed);
        var balls = new (float X, float Y, float R, float W)[14];
        for (int i = 0; i < balls.Length; i++)
        {
            float ang = (float)(rnd.NextDouble() * Math.PI * 2), d = (float)Math.Sqrt(rnd.NextDouble()) * 0.22f;
            balls[i] = (MathF.Cos(ang) * d * 0.8f, MathF.Sin(ang) * d * 0.8f, 0.1f + 0.08f * (float)rnd.NextDouble(), 0.4f + 0.3f * (float)rnd.NextDouble());
        }
        return Bake(96, (x, y) =>
        {
            float a = 0f;
            foreach (var (bx, by, br, bw) in balls)
            {
                float dx = x - bx, dy = y - by;
                a += bw * MathF.Exp(-(dx * dx + dy * dy) / (2f * br * br));
            }
            float r = MathF.Sqrt(x * x + y * y);
            float edge = MathF.Max(0f, 1f - r / 0.5f);
            a = (1f - MathF.Exp(-a * 0.9f)) * edge * edge;
            return (a, 0.88f + 0.12f * (y + 0.5f));
        }, "MrpDecompFog" + seed);
    }

    // 白い絵 (n×n・1 単位)。f(x, y) は中心からの位置 (-0.5〜0.5) → (不透明度, 明るさ)
    private static unsafe Sprite Bake(int n, Func<float, float, (float A, float L)> f, string name)
    {
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            var (a, l) = f((x + 0.5f) / n - 0.5f, (y + 0.5f) / n - 0.5f);
            int i = (y * n + x) * 4;
            byte g = (byte)(Math.Clamp(l, 0f, 1f) * 255f + 0.5f);
            px[i] = px[i + 1] = px[i + 2] = g;
            px[i + 3] = (byte)(Math.Clamp(a, 0f, 1f) * 255f + 0.5f);
        }
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = name };
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    // ── 確認用 ─────────────────────────────────────────────────────────

    internal static void Register()
    {
        TestBridge.Register("decompfx", "[shake amp | burst | bits] 外壁が抜けた時の演出: 粒の数・口ごとの強さ (shake = 開通の揺れだけをその大きさで / burst = いちばん近い口で開通の瞬間をもう一度 / bits = 種類ごとの粒の位置と速さ)", (args, reply) =>
        {
            var a = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a.Length >= 2 && a[0] == "shake")
            {
                if (!EnsurePool()) { reply("ERR decompfx no ship"); return; }
                _hitT = 0f;
                _hitAmp = float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture);
                reply($"OK decompfx shake amp={_hitAmp:0.00}");
                return;
            }
            if (a.Length >= 1 && a[0] == "burst")
            {
                var list = Decompression.OpenedBreaches;
                if (list.Count == 0) { reply("ERR decompfx no breach"); return; }
                Recent.Clear();
                OnBreach(GameClock.Now, list[list.Count - 1].Lines, 1f, list[list.Count - 1].Away);
                reply($"OK decompfx {Last}");
                return;
            }
            if (a.Length >= 1 && a[0] == "bits")
            {
                // 種類ごとに、生きている粒の数と位置・速さ (はじめの 4 つ)
                foreach (Kind k in Enum.GetValues(typeof(Kind)))
                {
                    var kb = new System.Text.StringBuilder();
                    int n = 0;
                    foreach (var b in All)
                    {
                        if (!b.Alive || b.Kind != k) continue;
                        if (n++ < 4) kb.Append($" ({b.X:0.0},{b.Y:0.0} v={b.Vx:0.0},{b.Vy:0.0})");
                    }
                    reply($"BITS {k} n={n}{kb}");
                }
                reply("OK decompfx bits");
                return;
            }
            var sb = new System.Text.StringBuilder();
            foreach (var j in Jets.Values) sb.Append($" [m={j.Mx:0.00},{j.My:0.00} n={j.Nx:0.00},{j.Ny:0.00} w={j.Width:0.00} I={j.I:0.00}]");
            var cam2 = PlayerCam();
            string camZ = cam2 ? $"{cam2.transform.position.z:0.00}" : "-";
            var sq = GameObject.Find("Main Camera/ShadowQuad");
            string sqZ = sq ? $"{sq.transform.position.z:0.00}" : "-";
            reply($"OK decompfx pool={All.Count} live={_live} bursts={Bursts} last={Last} camZ={camZ} shadowZ={sqZ} jets={Jets.Count} pre={Pre.Count}{sb}");
        });
    }
}
