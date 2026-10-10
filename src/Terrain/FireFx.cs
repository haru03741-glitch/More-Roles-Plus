using System;
using System.Collections.Generic;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 火の周りの演出 (全員の手元・電文は増やさない): 煙・火の粉・湯気・油の噴き上がり・濡れた配線の火花・音・燃え尽きた床の焦げ。
// 計算 (FireSim) の今の状態と、そのフレームの出来事 (Steam / FlareFx / Arcs / Chars) から作る。
// 粒は上限つきの使い回し (MaxPuffs)。出ていない時は Unity に触らない
internal static class FireFx
{
    private const int MaxPuffs = 96;
    private const float SmokeRate = 0.22f;    // 燃えている升 1 つあたり 1 秒の煙
    private const float EmberRate = 0.5f;     // 同じく火の粉
    private const int RateCells = 120;        // 出す量を数える升の上限 (広い火事でも粒が溢れない)
    private const float ScorchEvery = 0.5f;
    private const float ScorchR = 0.24f;
    private const float SoundRange = 9f, SoundFull = 2.5f, SoundMuffle = 5f;
    private const float SteamGap = 0.3f;

    private enum Kind : byte { Smoke, Steam, Ember, Flame, Spark }

    private struct Puff
    {
        public bool Alive;
        public Kind K;
        public float X, Y, Z, Vx, Vy, Age, Life, S0, S1, R, G, B, A;
        public SpriteRenderer Sr;
        public Transform Tr;
    }

    private static readonly Puff[] Puffs = new Puff[MaxPuffs];
    private static int _live;
    private static GameObject _root;
    private static Sprite _puffSprite, _dotSprite;
    private static int _shipGen = -1;
    private static float _smokeAcc, _emberAcc, _scorchAcc, _soundAcc, _steamSoundAt = -10f, _clock;
    private static long _lastMs;
    private static readonly List<(float X, float Y, float A)> Scorch = new();
    internal static readonly List<(float X, float Y)> Ignited = new(); // FireSim が点火を受けた瞬間 (音)

    // 焼けて崩れた壁の燻り: 線 (中心から Tx,Ty の向きに ±Half) の上から、薄れながら煙と火の粉を上げる
    private sealed class Smolder { public float X, Y, Tx, Ty, Half, Age, Life, SmokeAcc, EmberAcc; public bool Burst; }
    private static readonly List<Smolder> Smolders = new();
    private const int MaxSmolders = 8;

    // 持続音
    private static GameObject _host;
    private static AudioSource _loop;
    private static bool _loopPlaying, _loopMuffled;
    private static float _loopVol, _loopWant, _loopPan;
    private static int _qVol = -1, _qPan = int.MinValue;

    internal static int Made;
    private static int _frame;
    private static string _scorchWhy = "-";

    public static void Tick()
    {
        try { TickCore(); }
        catch (Exception e) { Plugin.Logger.LogError($"[FireFx] {e}"); Clear(); }
    }

    private static void TickCore()
    {
        if (GameClock.ShipGen != _shipGen) { _shipGen = GameClock.ShipGen; Clear(); }
        long now = Environment.TickCount64;
        float dt = _lastMs == 0 ? 0.016f : Math.Min(0.1f, (now - _lastMs) / 1000f);
        _lastMs = now;
        _clock += dt;
        bool fire = FireSim.Ready && (FireSim.BurningCount > 0 || FireSim.Steam.Count > 0 || FireSim.FlareFx.Count > 0
            || FireSim.Arcs.Count > 0 || FireSim.Chars.Count > 0 || Ignited.Count > 0) || Smolders.Count > 0;
        if (!fire && _live == 0 && Scorch.Count == 0 && !_loopPlaying) return;
        var ship = ShipStatus.Instance;
        if (!ship || MeetingHud.Instance)
        {
            // 会議中・船が無い間は持続音を絞って止める (ここで返すと下の音の処理に届かない)
            Ignited.Clear();
            _loopWant = 0f;
            TickLoop(dt);
            return;
        }
        if (!EnsureArt(ship)) return;
        _frame++;

        if (FireSim.Ready)
        {
            Emit(dt);
            foreach (var s in FireSim.Steam) SteamAt(s.X, s.Y, s.Amount);
            foreach (var f in FireSim.FlareFx) FlareAt(f.X, f.Y);
            foreach (var a in FireSim.Arcs) ArcAt(a.X, a.Y);
            Scorch.AddRange(FireSim.Chars);
        }
        TickSmolders(dt);
        foreach (var g in Ignited) BreakNoise.PlayAt("noise_fire_ignite", g.X, g.Y, SoundRange, SoundMuffle, 0.8f, FxMath.Range(0.9f, 1.1f));
        Ignited.Clear();

        _scorchAcc += dt;
        if (_scorchAcc >= ScorchEvery && Scorch.Count > 0)
        {
            _scorchAcc = 0f;
            _scorchWhy = DamageMap.Scorch(Scorch, ScorchR) ?? $"ok n={Scorch.Count}";
            Scorch.Clear();
        }

        Move(dt);
        _soundAcc += dt;
        if (_soundAcc >= 0.2f) { _soundAcc = 0f; FeedLoop(); }
        TickLoop(dt);
    }

    // ── 出す ───────────────────────────────────────────────────────────

    private static void Emit(float dt)
    {
        int burning = FireSim.BurningCount;
        if (burning <= 0) return;
        int n = Math.Min(burning, RateCells);
        _smokeAcc += n * SmokeRate * dt;
        _emberAcc += n * EmberRate * dt;
        var act = FireSim.ActiveCells;
        while (_smokeAcc >= 1f)
        {
            _smokeAcc -= 1f;
            if (!PickBurning(act, out int k)) break;
            Cell(k, out float x, out float y);
            var m = FireSim.MatOf(k);
            // 油は真っ黒・草は白っぽい・木と配線は灰
            float dark = m == FireSim.Mat.Fuel ? 0.12f : m == FireSim.Mat.Electric ? 0.25f : m == FireSim.Mat.Grass ? 0.5f : 0.32f;
            float l = FxMath.Range(2.2f, 3.2f);
            Spawn(Kind.Smoke, x + FxMath.Range(-0.15f, 0.15f), y + 0.85f, FxMath.Range(-0.08f, 0.08f), FxMath.Range(0.45f, 0.7f),
                l, FxMath.Range(0.3f, 0.4f), FxMath.Range(1.0f, 1.4f), dark, dark * 0.97f, dark * 0.95f, 0.65f);
        }
        while (_emberAcc >= 1f)
        {
            _emberAcc -= 1f;
            if (!PickBurning(act, out int k)) break;
            Cell(k, out float x, out float y);
            Spawn(Kind.Ember, x + FxMath.Range(-0.12f, 0.12f), y + 0.15f, FxMath.Range(-0.25f, 0.25f), FxMath.Range(0.7f, 1.3f),
                FxMath.Range(0.5f, 0.9f), 0.05f, 0.03f, 1f, FxMath.Range(0.55f, 0.85f), 0.2f, 1f);
        }
    }

    // 焼けて崩れた所 (burst = 崩れた瞬間に火の粉がぱっと舞う) を足す。崩れた時に全員が呼ぶ
    internal static void AddSmolder(float x, float y, float tx, float ty, float half, float life, bool burst)
    {
        if (Smolders.Count >= MaxSmolders) Smolders.RemoveAt(0);
        Smolders.Add(new Smolder { X = x, Y = y, Tx = tx, Ty = ty, Half = half, Life = life, Burst = burst });
    }

    private static void TickSmolders(float dt)
    {
        for (int i = Smolders.Count - 1; i >= 0; i--)
        {
            var s = Smolders[i];
            if (s.Burst)
            {
                s.Burst = false;
                for (int k = 0; k < 16; k++)
                {
                    float u = FxMath.Range(-s.Half, s.Half);
                    Spawn(Kind.Ember, s.X + s.Tx * u, s.Y + s.Ty * u + 0.2f, FxMath.Range(-0.9f, 0.9f), FxMath.Range(1f, 2.2f),
                        FxMath.Range(0.6f, 1.2f), 0.06f, 0.03f, 1f, FxMath.Range(0.5f, 0.85f), 0.18f, 1f);
                }
                for (int k = 0; k < 4; k++)
                {
                    float u = FxMath.Range(-s.Half, s.Half);
                    Spawn(Kind.Smoke, s.X + s.Tx * u, s.Y + s.Ty * u + 0.4f, FxMath.Range(-0.15f, 0.15f), FxMath.Range(0.5f, 0.8f),
                        FxMath.Range(2.2f, 3f), 0.4f, FxMath.Range(1.3f, 1.7f), 0.2f, 0.19f, 0.185f, 0.7f);
                }
            }
            s.Age += dt;
            if (s.Age >= s.Life) { Smolders.RemoveAt(i); continue; }
            float left = 1f - s.Age / s.Life; // 燃え残りが冷えて煙も火の粉も減っていく
            s.SmokeAcc += 2.5f * left * dt;
            s.EmberAcc += 4f * left * left * dt;
            while (s.SmokeAcc >= 1f)
            {
                s.SmokeAcc -= 1f;
                float u = FxMath.Range(-s.Half, s.Half);
                Spawn(Kind.Smoke, s.X + s.Tx * u, s.Y + s.Ty * u + 0.15f, FxMath.Range(-0.06f, 0.06f), FxMath.Range(0.3f, 0.5f),
                    FxMath.Range(2f, 2.8f), 0.18f, FxMath.Range(0.7f, 1f), 0.26f, 0.25f, 0.24f, 0.45f * left + 0.15f);
            }
            while (s.EmberAcc >= 1f)
            {
                s.EmberAcc -= 1f;
                float u = FxMath.Range(-s.Half, s.Half);
                Spawn(Kind.Ember, s.X + s.Tx * u, s.Y + s.Ty * u + 0.05f, FxMath.Range(-0.2f, 0.2f), FxMath.Range(0.4f, 0.9f),
                    FxMath.Range(0.4f, 0.8f), 0.045f, 0.025f, 1f, FxMath.Range(0.45f, 0.75f), 0.15f, 1f);
            }
        }
    }

    // 燃えている升を数回くじで引く (広い火事でも 1 フレームの手間を一定にする)
    private static bool PickBurning(IReadOnlyList<int> act, out int k)
    {
        k = 0;
        if (act.Count == 0) return false;
        for (int i = 0; i < 8; i++)
        {
            k = act[FxMath.Range(0, act.Count)];
            if (FireSim.Burning(k)) return true;
        }
        return false;
    }

    private static void Cell(int k, out float x, out float y)
    {
        x = FireSim.Origin.x + (k % FireSim.W + 0.5f) * FireSim.Cell;
        y = FireSim.Origin.y + (k / FireSim.W + 0.5f) * FireSim.Cell;
    }

    private static void SteamAt(float x, float y, int amount)
    {
        int n = Math.Min(3, 1 + amount / 160);
        for (int i = 0; i < n; i++)
            Spawn(Kind.Steam, x + FxMath.Range(-0.12f, 0.12f), y + 0.1f, FxMath.Range(-0.15f, 0.15f), FxMath.Range(0.8f, 1.2f),
                FxMath.Range(1.1f, 1.6f), 0.2f, FxMath.Range(0.9f, 1.3f), 0.93f, 0.95f, 0.98f, 0.55f);
        if (_clock - _steamSoundAt >= SteamGap)
        {
            _steamSoundAt = _clock;
            BreakNoise.PlayAt("noise_steam", x, y, SoundRange, SoundMuffle, 0.7f, FxMath.Range(0.9f, 1.15f));
        }
    }

    private static void FlareAt(float x, float y)
    {
        for (int i = 0; i < 10; i++)
        {
            float a = FxMath.Range(-0.6f, 0.6f);
            Spawn(Kind.Flame, x + FxMath.Range(-0.25f, 0.25f), y, FxMath.Sin(a) * 0.6f, FxMath.Range(1.6f, 2.6f),
                FxMath.Range(0.45f, 0.7f), 0.25f, FxMath.Range(0.6f, 0.9f), 1f, FxMath.Range(0.45f, 0.8f), 0.12f, 0.95f);
        }
        for (int i = 0; i < 12; i++)
            Spawn(Kind.Ember, x, y + 0.2f, FxMath.Range(-1.2f, 1.2f), FxMath.Range(1f, 2.4f),
                FxMath.Range(0.5f, 1f), 0.06f, 0.03f, 1f, FxMath.Range(0.6f, 0.9f), 0.2f, 1f);
        for (int i = 0; i < 3; i++)
            Spawn(Kind.Steam, x + FxMath.Range(-0.3f, 0.3f), y + 0.3f, FxMath.Range(-0.2f, 0.2f), FxMath.Range(1f, 1.4f),
                FxMath.Range(1.2f, 1.6f), 0.3f, 1.3f, 0.93f, 0.95f, 0.98f, 0.6f);
        BreakNoise.PlayAt("noise_flare", x, y, SoundRange + 3f, SoundMuffle, 1f, FxMath.Range(0.9f, 1.1f));
    }

    private static void ArcAt(float x, float y)
    {
        for (int i = 0; i < 6; i++)
            Spawn(Kind.Spark, x + FxMath.Range(-0.15f, 0.15f), y + FxMath.Range(0f, 0.2f), FxMath.Range(-1.5f, 1.5f), FxMath.Range(-0.5f, 1.5f),
                FxMath.Range(0.12f, 0.25f), 0.06f, 0.02f, 0.75f, 0.9f, 1f, 1f);
        BreakNoise.PlayAt("noise_arc", x, y, SoundRange, SoundMuffle, 0.75f, FxMath.Range(0.9f, 1.2f));
    }

    private static void Spawn(Kind k, float x, float y, float vx, float vy, float life, float s0, float s1, float r, float g, float b, float a)
    {
        int slot = -1;
        for (int i = 0; i < MaxPuffs; i++) if (!Puffs[i].Alive) { slot = i; break; }
        if (slot < 0) return;
        ref var p = ref Puffs[slot];
        p.Alive = true; p.K = k;
        p.X = x; p.Y = y; p.Vx = vx; p.Vy = vy; p.Age = 0f; p.Life = life;
        p.S0 = s0; p.S1 = s1; p.R = r; p.G = g; p.B = b; p.A = a;
        // 炎の板はタイルの下の縁の奥行きに立つ (升から最大 1 タイル下)。煙や火の粉はそれより手前に出す
        p.Z = (y - 1.6f) / 1000f - (k == Kind.Smoke || k == Kind.Steam ? 0f : 0.0004f);
        p.Sr.sprite = k == Kind.Smoke || k == Kind.Steam || k == Kind.Flame ? _puffSprite : _dotSprite;
        // 前に使った粒の色と大きさが 1 フレーム残らないよう、出す時に一度書く (煙は 2 フレームに 1 回しか書かない)
        p.Tr.localScale = FxMath.V3(s0, s0, 1f);
        p.Sr.color = FxMath.Rgba(r, g, b, 0f);
        p.Sr.enabled = true;
        _live++;
        Made++;
    }

    private static void Move(float dt)
    {
        if (_live == 0) return;
        float t = _clock;
        for (int i = 0; i < MaxPuffs; i++)
        {
            ref var p = ref Puffs[i];
            if (!p.Alive) continue;
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                p.Alive = false;
                p.Sr.enabled = false;
                _live--;
                continue;
            }
            float u = p.Age / p.Life;
            switch (p.K)
            {
                case Kind.Smoke:
                    // ゆっくり昇りながら横へ揺れ、広がって薄れる
                    p.Vx += FxMath.Sin(t * 1.3f + i) * 0.15f * dt;
                    p.Vy *= 1f - 0.25f * dt;
                    break;
                case Kind.Steam:
                    p.Vy *= 1f - 0.6f * dt;
                    p.Vx += FxMath.Sin(t * 2.1f + i * 0.7f) * 0.3f * dt;
                    break;
                case Kind.Ember:
                    p.Vx += FxMath.Sin(t * 6f + i * 1.7f) * 1.2f * dt;
                    p.Vy -= 0.3f * dt;
                    break;
                case Kind.Flame:
                    p.Vy *= 1f - 1.8f * dt;
                    break;
                case Kind.Spark:
                    p.Vy -= 6f * dt;
                    break;
            }
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            float s = p.S0 + (p.S1 - p.S0) * (1f - (1f - u) * (1f - u));
            float a = p.K == Kind.Flame ? p.A * (1f - u * u) : p.K == Kind.Ember || p.K == Kind.Spark ? p.A * (1f - u) : p.A * FxMath.Min(1f, u * 4f) * (1f - u);
            // 炎の塊は黄 → 橙 → 暗い赤へ冷えていく
            float r = p.R, g = p.G, b = p.B;
            if (p.K == Kind.Flame) { g *= 1f - 0.7f * u; b *= 1f - u; r *= 1f - 0.4f * u * u; }
            p.Tr.position = FxMath.V3(p.X, p.Y, p.Z);
            // 煙と湯気は色と大きさの変わりがゆっくりなので 2 フレームに 1 回だけ書く
            if ((p.K != Kind.Smoke && p.K != Kind.Steam) || ((i + _frame) & 1) == 0)
            {
                p.Tr.localScale = FxMath.V3(s, s, 1f);
                p.Sr.color = FxMath.Rgba(r, g, b, a);
            }
        }
    }

    // ── 音 ─────────────────────────────────────────────────────────────

    // 一番近い燃えている升の距離と、近くで燃えている升の数から持続音の大きさを決める
    private static void FeedLoop()
    {
        _loopWant = 0f;
        if (!FireSim.Ready || FireSim.BurningCount == 0) return;
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return;
        Vector2 me = lp.GetTruePosition();
        float best = float.MaxValue, bx = 0f;
        int near = 0;
        var act = FireSim.ActiveCells;
        int step = Math.Max(1, act.Count / 400);
        for (int i = 0; i < act.Count; i += step)
        {
            int k = act[i];
            if (!FireSim.Burning(k)) continue;
            Cell(k, out float x, out float y);
            float dx = x - me.x, dy = y - me.y, d2 = dx * dx + dy * dy;
            if (d2 < best) { best = d2; bx = dx; }
            if (d2 < 16f) near += step;
        }
        if (best == float.MaxValue) return;
        float d = FxMath.Sqrt(best);
        if (d >= SoundRange) return;
        float k2 = d <= SoundFull ? 1f : 1f - (d - SoundFull) / (SoundRange - SoundFull);
        _loopWant = k2 * FxMath.Sqrt(k2) * FxMath.Min(1f, 0.35f + near / 40f) * 0.8f;
        _loopPan = FxMath.Clamp(bx / 10f, -0.7f, 0.7f);
        bool muffle = d > SoundMuffle;
        if (_loopPlaying && muffle != _loopMuffled && _loopVol < 0.02f) { _loop.Stop(); _loopPlaying = false; }
        _loopMuffled = muffle;
    }

    private static void TickLoop(float dt)
    {
        if (!EnsureHost()) return;
        float want = MeetingHud.Instance ? 0f : _loopWant;
        _loopVol = FxMath.MoveTowards(_loopVol, want, dt * 2f);
        if (!_loopPlaying)
        {
            if (want <= 0.005f) return;
            var clip = MrpBundle.Clip(_loopMuffled ? "noise_fire_loop_m" : "noise_fire_loop");
            if (!clip) return;
            _loop.clip = clip;
            _qVol = -1; _qPan = int.MinValue;
            _loopPlaying = true;
            ApplyLoop();
            _loop.Play();
            return;
        }
        if (want <= 0f && _loopVol < 0.005f) { _loop.Stop(); _loopPlaying = false; _loopVol = 0f; return; }
        ApplyLoop();
    }

    private static void ApplyLoop()
    {
        int qv = (int)(_loopVol * (_loopMuffled ? 1.3f : 1f) * 64f), qn = (int)(_loopPan * 32f);
        if (qv != _qVol) { _qVol = qv; _loop.volume = qv / 64f; }
        if (qn != _qPan) { _qPan = qn; _loop.panStereo = qn / 32f; }
    }

    // 本編の効果音の音量設定に従うよう効果音のミキサーへ流す。置き場は消えないので作った後は bool だけ見る
    private static bool _hostReady;
    private static bool EnsureHost()
    {
        if (_hostReady) return true;
        var sm = SoundManager.Instance;
        if (!sm) return false;
        _host = new GameObject("MrpFireSound");
        UnityEngine.Object.DontDestroyOnLoad(_host);
        _loop = _host.AddComponent<AudioSource>();
        _loop.playOnAwake = false;
        _loop.loop = true;
        _loop.spatialBlend = 0f;
        _loop.volume = 0f;
        _loop.outputAudioMixerGroup = sm.SfxChannel;
        _hostReady = true;
        return true;
    }

    // ── 絵 ─────────────────────────────────────────────────────────────

    private static bool EnsureArt(ShipStatus ship)
    {
        if (_root) return true;
        _puffSprite ??= MakeSprite(32, true);
        _dotSprite ??= MakeSprite(8, false);
        _root = new GameObject("MrpFireFx");
        _root.transform.SetParent(ship.transform, false);
        for (int i = 0; i < MaxPuffs; i++)
        {
            var go = new GameObject("MrpFirePuff") { layer = 0 };
            go.transform.SetParent(_root.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.enabled = false;
            Puffs[i] = new Puff { Sr = sr, Tr = go.transform };
        }
        _live = 0;
        return true;
    }

    // 煙の塊 (縁へ向けて柔らかく薄れるぼけた塊・下側が少し暗い) と、火の粉の丸。どちらも白で作り色は頂点色で付ける
    private static unsafe Sprite MakeSprite(int n, bool puff)
    {
        var px = new byte[n * n * 4];
        float c = (n - 1) * 0.5f, r = n * (puff ? 0.34f : 0.46f);
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float dx = x - c, dy = y - c;
            float d = FxMath.Sqrt(dx * dx + dy * dy);
            if (puff)
            {
                // 3 つの丸を重ねたもこもこの形 (どの丸も絵の枠の内側に収める。はみ出すと枠の端の画素が伸びて四角く切れる)
                float d2 = FxMath.Sqrt((dx + n * 0.15f) * (dx + n * 0.15f) + (dy + n * 0.08f) * (dy + n * 0.08f)) * 1.25f;
                float d3 = FxMath.Sqrt((dx - n * 0.15f) * (dx - n * 0.15f) + (dy + n * 0.1f) * (dy + n * 0.1f)) * 1.3f;
                d = FxMath.Min(d, FxMath.Min(d2, d3));
            }
            float a;
            float shade = 1f;
            if (puff)
            {
                // 縁をくっきり切らず、真ん中から外へなめらかに薄れる (煙は輪郭の無い物)
                float q = FxMath.Clamp01(1f - d / r);
                a = q * q * (3f - 2f * q);
                shade = 1f - 0.2f * FxMath.Clamp01(-dy / (n * 0.5f));
            }
            else a = FxMath.Clamp01(r - d + 0.5f);
            int i = (y * n + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = (byte)(255f * shade);
            px[i + 3] = (byte)(255f * a);
        }
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "MrpFirePuff", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        fixed (byte* b = px) tex.LoadRawTextureData((IntPtr)b, px.Length);
        tex.Apply(false, true);
        tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        var sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n, 0, SpriteMeshType.FullRect);
        sp.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    internal static void Clear()
    {
        for (int i = 0; i < MaxPuffs; i++) Puffs[i] = default;
        _live = 0;
        if (_root) UnityEngine.Object.Destroy(_root);
        _root = null;
        Scorch.Clear();
        Ignited.Clear();
        Smolders.Clear();
        _smokeAcc = _emberAcc = _scorchAcc = 0f;
        _loopWant = _loopVol = 0f;
        if (_loopPlaying) { try { if (_loop) _loop.Stop(); } catch { } _loopPlaying = false; }
    }

    internal static string Describe() => $"puffs={_live}/{MaxPuffs} made={Made} loop={(_loopPlaying ? (_loopMuffled ? "on(m)" : "on") : "off")} vol={_loopVol:0.00} scorchQ={Scorch.Count} scorch={_scorchWhy}";

    // ── 確認用 ─────────────────────────────────────────────────────────

    internal static void Register()
    {
        TestBridge.Register("fire", "[ignite x y r [heat] | spill x y r [amount] | at x y | reset | hide | show | wall wood|metal|stone [min] [stage]] 火 (x y を省くと自分の位置): 点火 / 油をまく / その升の材質と熱 / 全部消す", (args, reply) =>
        {
            var q = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string a = q.Length > 0 ? q[0] : "";
            if (a == "reset") { FireSim.Reset(); FireArt.Clear(); Clear(); reply("OK fire reset"); return; }
            if (a == "wall" && q.Length >= 2)
            {
                int min = q.Length > 2 && int.TryParse(q[2], out int mm) ? mm : 0, st = q.Length > 3 && int.TryParse(q[3], out int ss) ? ss : 0;
                reply(FireSim.TuneWall(q[1], min, st));
                return;
            }
            if (a == "hide" || a == "show") { reply($"OK fire {a} tiles={FireArt.DebugHide(a == "hide")}"); return; }
            if (a == "ignite" || a == "spill" || a == "at")
            {
                var lp = PlayerControl.LocalPlayer;
                Vector2 at = lp ? lp.GetTruePosition() : default;
                int o = 1;
                if (q.Length >= 3 && float.TryParse(q[1], out float x) && float.TryParse(q[2], out float y)) { at = new Vector2(x, y); o = 3; }
                if (a == "at") { reply(FireSim.CellInfo(at)); return; }
                float r = q.Length > o && float.TryParse(q[o], out float rr) ? rr : 0.5f;
                float f = q.Length > o + 1 && float.TryParse(q[o + 1], out float ff) ? ff : 1f;
                bool guest = TerrainSync.IsGuest();
                TerrainResult res = a == "ignite"
                    ? guest ? TerrainApi.Ignite(at, r, f) : TerrainApi.WorldIgnite(at, r, f)
                    : guest ? TerrainApi.Spill(at, r, f) : TerrainApi.WorldSpill(at, r, f);
                reply($"{(res.Ok ? "OK" : "ERR")} fire {a} ({at.x:0.00},{at.y:0.00}) r={r:0.0} f={f:0.0} {res.Why}");
                return;
            }
            reply($"{FireSim.Describe()} art: {FireArt.Describe()} fx: {Describe()}");
        });
    }
}
