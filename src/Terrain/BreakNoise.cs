using System;
using System.Collections.Generic;
using MoreRolesPlus.Bridge;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 壁が壊れた音とその方向のマーク。破壊が適用されるたびに全員の手元で鳴らす (電文は増やさない)。
// 音は種類で違う (爆発 / 叩いた / 叩いて崩れた)。叩いた音と崩れた音はマップの壁の素材 (船体の金属・岩・木) で変える。
// 遠いほど小さく、左右は発生した側へ寄せる。遠い時と、間に壁がある時はこもった音 (<名前>_m) にする。
// 崩れ方で鳴り方を変える: 割れた塊と大きな瓦礫が多いほど低く大きく。爆発で壁が抜けた時は素材の崩れ落ちる音を少し遅れて重ねる
// マークは本編のノイズメーカーの矢印 (矢じり・音の波紋・真ん中の絵) を借り、真ん中の絵を音の種類の絵に替える。
// 画面の真ん中を囲む楕円の上、発生した方向に出る。遠いほど小さい。矢じりと波紋だけ回し、真ん中の絵は回さない。
// 毎フレームの計算は managed の float だけで (Unity の演算子・コンストラクタは interop を通ってゴミを出す)
internal static class BreakNoise
{
    private enum Sound { Boom, Hit, Crumble }

    private readonly struct Spec
    {
        public readonly float Range, Muffle, Volume, Scale;
        public readonly string Icon;
        public Spec(float range, float muffle, float volume, float scale, string icon)
        {
            Range = range; Muffle = muffle; Volume = volume; Scale = scale; Icon = icon;
        }
    }

    private static readonly Spec[] Specs =
    {
        // 聞こえる距離・ここより遠いとこもる・音量・マークの大きさ
        new(40f, 14f, 1.0f, 1.2f, "noise_blast"),  // 爆発
        new(16f, 7f, 0.7f, 0.85f, "noise_hit"),    // 叩いた
        new(22f, 10f, 0.95f, 1.0f, "noise_break"), // 叩いて崩れた
    };

    private const float FullVolumeDist = 3f; // ここまでは小さくならない
    private const float NoMarkDist = 2f;     // 自分のすぐ近くはマークを出さない
    private const float RingX = 2.5f, RingY = 1.75f; // マークを並べる楕円 (HUD の単位。画面の縦の半分 = 3)
    private const float MarkSize = 0.6f;     // 一番近い時の大きさ (本編の矢印の大きさと同じ)
    private const float IconScale = 0.85f;   // 真ん中の絵 (1.28 単位) を本編の死体の絵 (約 1.1 単位) に合わせる
    private const float FarScale = 0.6f;     // 聞こえる端での大きさの比
    private const float FadeIn = 0.1f, Hold = 1.4f, FadeOut = 0.4f;
    private const float PopScale = 1.3f;     // 出た瞬間の膨らみ
    private const float MergeDist = 1.5f;    // 同じ種類のマークが残っている近さなら 1 つにまとめる
    private const int MaxMarks = 6;
    private const float PanDist = 10f, PanMax = 0.7f;
    private const float MuffledGain = 0.8f;  // 壁越しは少し小さく
    private const float WallProbeBack = 0.3f; // 叩いた点は壁の上なので、叩いた側へ戻した所から壁越しかを見る
    private const int BurstLimit = 4;        // 0.3 秒にこれより多くは鳴らさない (追いつきの一斉適用)
    private const long BurstWindowMs = 300;
    private const int Voices = 6;           // 爆発は爆発の音と崩れ落ちる音の 2 つを使う
    private const float RubbleDelay = 0.18f; // 爆発の音から崩れ落ちる音までの遅れ (種で 0.12 秒まで揺らす)
    private const float RubbleVolume = 0.85f;
    private const float PiecesFull = 70f, BlocksFull = 4f; // ここまで割れたら一番大きい崩れ方として扱う

    private sealed class Mark
    {
        public Sound Kind;
        public float Sx, Sy;      // 発生した所 (ワールド)
        public float T, Size, Alpha = -1f;
        public Transform Root, Pivot;
        public SpriteRenderer Icon, Arrow, Waves;
    }

    private static readonly List<Mark> Marks = new();
    private static readonly Queue<long> Recent = new();
    private static GameObject _voiceHost;
    private static AudioSource[] _voices;
    private static int _nextVoice;
    private static long _lastMs;
    private static IntPtr _matShip;
    private static GameObject _prefab;
    private static string _hitClip, _crumbleClip, _rubbleClip;

    // テスト用
    internal static int Emitted, Dropped;
    internal static string Last = "-";

    // 破壊を適用した直後に呼ぶ (ホスト・客・一人の全員)
    public static void Emit(in ResolvedDamage r)
    {
        try { EmitCore(r); }
        catch (Exception e) { Plugin.Logger.LogError($"[BreakNoise] {e}"); }
    }

    private static void EmitCore(in ResolvedDamage r)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || !ShipStatus.Instance || MeetingHud.Instance) return;
        long now = Environment.TickCount64;
        while (Recent.Count > 0 && now - Recent.Peek() > BurstWindowMs) Recent.Dequeue();
        if (Recent.Count >= BurstLimit) { Dropped++; return; }
        Recent.Enqueue(now);

        var kind = r.Kind == DamageKind.Explosion ? Sound.Boom : r.Hp <= 0 ? Sound.Crumble : Sound.Hit;
        var spec = Specs[(int)kind];
        Vector2 me = lp.GetTruePosition();
        float dx = r.Position.x - me.x, dy = r.Position.y - me.y;
        float d = MathF.Sqrt(dx * dx + dy * dy);
        if (d >= spec.Range) { Last = $"{kind} d={d:0.0} out of range"; return; }

        float near = d <= FullVolumeDist ? 1f : 1f - (d - FullVolumeDist) / (spec.Range - FullVolumeDist);
        float vol = spec.Volume * MathF.Pow(near, 1.5f);
        float pan = Math.Clamp(dx / PanDist, -PanMax, PanMax);
        float jitter = (r.Seed % 7) * 0.01f - 0.03f;
        bool muffled = d > spec.Muffle || ThroughWall(lp, me, r);
        if (muffled && d <= spec.Muffle) vol *= MuffledGain;
        string m = muffled ? "_m" : "";
        string clip = ClipName(kind) + m;

        // 崩れ方の大きさ 0..1 (割れた塊の数と大きな瓦礫の数の平均)
        float amount = 0.5f * (Math.Min(1f, TerrainDamage.LastPieces / PiecesFull) + Math.Min(1f, TerrainDamage.LastBlocks / BlocksFull));
        float pitch;
        string extra = "";
        if (kind == Sound.Boom)
        {
            // 大きな爆発ほど低い
            pitch = 1.06f - 0.12f * Math.Clamp((r.Size - 1f) * 0.5f, 0f, 1f) + jitter;
            if (TerrainDamage.LastCut > 0)
            {
                string rubble = _rubbleClip + m;
                float rv = vol * RubbleVolume * (0.55f + 0.45f * amount);
                float rp = 1.08f - 0.16f * amount + jitter;
                float delay = RubbleDelay + (r.Seed % 5) * 0.03f;
                Play(rubble, rv, pan, rp, delay);
                extra = $" +{rubble} vol={rv:0.00} pitch={rp:0.00} delay={delay:0.00}";
            }
        }
        else if (kind == Sound.Crumble)
        {
            pitch = 1.07f - 0.15f * amount + jitter;
            vol *= 0.75f + 0.25f * amount;
        }
        else
        {
            // 崩れる手前ほど低く鈍い
            float worn = 1f - Math.Clamp(r.Hp / (float)WallDurability.MaxHp, 0f, 1f);
            pitch = 1.03f - 0.08f * worn + jitter;
            vol *= 0.9f + 0.2f * worn;
        }
        Play(clip, vol, pan, pitch, 0f);
        Emitted++;
        Last = $"{clip} d={d:0.0} vol={vol:0.00} pan={pan:0.00} pitch={pitch:0.00} amount={amount:0.00}{extra}";

        if (d >= NoMarkDist) AddMark(kind, r.Position.x, r.Position.y, d / spec.Range, me.x, me.y);
    }

    // 地形の破壊以外の音 (置いた爆弾の導火線など) を、その場所からの距離で鳴らす。muffle より遠いとこもった音
    internal static void PlayAt(string clip, Vector2 at, float range, float muffle, float volume)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || MeetingHud.Instance) return;
        Vector2 me = lp.GetTruePosition();
        float dx = at.x - me.x, dy = at.y - me.y;
        float d = MathF.Sqrt(dx * dx + dy * dy);
        if (d >= range) return;
        float near = d <= FullVolumeDist ? 1f : 1f - (d - FullVolumeDist) / (range - FullVolumeDist);
        Play(d > muffle ? clip + "_m" : clip, volume * MathF.Pow(near, 1.5f), Math.Clamp(dx / PanDist, -PanMax, PanMax), 1f, 0f);
    }

    // 幽霊は壁を抜けるのでこもらない。すぐそばは短い区間の壁判定が当てにならないので直接聞こえる扱い
    private static bool ThroughWall(PlayerControl lp, Vector2 me, in ResolvedDamage r)
    {
        if (lp.Data == null || lp.Data.IsDead) return false;
        Vector2 src = r.Position;
        if (r.Kind == DamageKind.Blunt) { src.x += r.Normal.x * WallProbeBack; src.y += r.Normal.y * WallProbeBack; }
        float dx = src.x - me.x, dy = src.y - me.y;
        if (dx * dx + dy * dy < 1f) return false;
        return PhysicsHelpers.AnythingBetween(me, src, Constants.ShipOnlyMask, false);
    }

    private static string ClipName(Sound kind)
    {
        var ship = ShipStatus.Instance;
        if (ship.Pointer != _matShip)
        {
            _matShip = ship.Pointer;
            // 壁の素材: ポーラスは岩とコンクリート・ファングルは木の小屋と崖・ほかは船体の金属
            string mat = ship.Type switch
            {
                ShipStatus.MapType.Pb => "stone",
                ShipStatus.MapType.Fungle => "wood",
                _ => "metal",
            };
            _hitClip = "noise_hit_" + mat;
            _crumbleClip = "noise_crumble_" + mat;
            _rubbleClip = "noise_rubble_" + mat;
        }
        return kind == Sound.Boom ? "noise_boom" : kind == Sound.Hit ? _hitClip : _crumbleClip;
    }

    private static void Play(string name, float vol, float pan, float pitch, float delay)
    {
        var clip = Fx.MrpBundle.Clip(name);
        if (!clip || !EnsureVoices()) return;
        var src = _voices[_nextVoice];
        _nextVoice = (_nextVoice + 1) % Voices;
        src.Stop();
        src.clip = clip;
        src.volume = vol;
        src.panStereo = pan;
        src.pitch = pitch;
        if (delay > 0f) src.PlayDelayed(delay);
        else src.Play();
    }

    // 本編の効果音の音量設定に従うよう、効果音のミキサーへ流す
    private static bool EnsureVoices()
    {
        if (_voiceHost) return true;
        var sm = SoundManager.Instance;
        if (!sm) return false;
        _voiceHost = new GameObject("MrpBreakNoise");
        UnityEngine.Object.DontDestroyOnLoad(_voiceHost);
        _voices = new AudioSource[Voices];
        for (int i = 0; i < Voices; i++)
        {
            var s = _voiceHost.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.loop = false;
            s.spatialBlend = 0f;
            s.outputAudioMixerGroup = sm.SfxChannel;
            _voices[i] = s;
        }
        return true;
    }

    private static void AddMark(Sound kind, float x, float y, float far, float mx, float my)
    {
        float size = MarkSize * Specs[(int)kind].Scale * (1f - (1f - FarScale) * far);
        // 同じ種類 (叩いた・崩れたは同じ壁の続き) が近くに残っていれば、それを出し直す
        foreach (var m in Marks)
        {
            bool same = m.Kind == kind || m.Kind != Sound.Boom && kind != Sound.Boom;
            float ex = m.Sx - x, ey = m.Sy - y;
            if (!same || ex * ex + ey * ey > MergeDist * MergeDist || !m.Root) continue;
            if (kind == Sound.Crumble && m.Kind == Sound.Hit) SetKind(m, kind);
            m.Sx = x; m.Sy = y;
            m.Size = MathF.Max(m.Size, size);
            m.T = MathF.Min(m.T, FadeIn); // 消えかけていても見える所まで戻す (膨らみは繰り返さない)
            return;
        }
        if (Marks.Count >= MaxMarks) { DestroyMark(Marks[0]); Marks.RemoveAt(0); }
        var mk = Create(kind);
        if (mk == null) return;
        mk.Sx = x; mk.Sy = y; mk.Size = size;
        Marks.Add(mk);
        UpdateMark(mk, mx, my);
    }

    // 本編のノイズメーカーが死んだ時に出す矢印。役職の一覧の中のノイズメーカーから取る
    private static GameObject Prefab()
    {
        if (_prefab) return _prefab;
        var rm = RoleManager.Instance;
        if (!rm) return null;
        var roles = rm.AllRoles;
        for (int i = 0; i < roles.Count; i++)
        {
            var r = roles[i];
            if (!r || r.Role != AmongUs.GameOptions.RoleTypes.Noisemaker) continue;
            var nm = r.TryCast<NoisemakerRole>();
            if (nm) _prefab = nm.deathArrowPrefab;
            break;
        }
        if (!_prefab) Plugin.Logger.LogWarning("[BreakNoise] noisemaker arrow not found");
        return _prefab;
    }

    private static Mark Create(Sound kind)
    {
        var hud = HudManager.Instance;
        var prefab = Prefab();
        if (!hud || !prefab) return null;
        var go = UnityEngine.Object.Instantiate(prefab, hud.transform);
        go.name = "MrpNoiseMark";
        // 位置と消え方はこちらで動かすので、本編の矢印の動き (死体を追う・時間で消す) を外す。コマ送りの絵はそのまま動かす
        UnityEngine.Object.DestroyImmediate(go.GetComponent<NoisemakerArrow>());
        foreach (var f in go.GetComponentsInChildren<NoisemakerFade>(true)) UnityEngine.Object.DestroyImmediate(f);
        var root = go.transform;
        var pivot = root.Find("Pivot");
        var body = root.Find("DeadBody");
        if (!pivot || !body) { UnityEngine.Object.Destroy(go); return null; }
        // 真ん中の死体の絵はコマ送りを止めて種類の絵にする
        UnityEngine.Object.DestroyImmediate(body.GetComponent<PowerTools.SpriteAnim>());
        UnityEngine.Object.DestroyImmediate(body.GetComponent<Animator>());
        body.localScale = V3(IconScale, IconScale, 1f);
        var m = new Mark
        {
            Root = root,
            Pivot = pivot,
            Icon = body.GetComponent<SpriteRenderer>(),
            Arrow = pivot.Find("Arrow").GetComponent<SpriteRenderer>(),
            Waves = pivot.Find("Soundwaves").GetComponent<SpriteRenderer>(),
        };
        SetKind(m, kind);
        return m;
    }

    private static void SetKind(Mark m, Sound kind)
    {
        m.Kind = kind;
        m.Icon.sprite = Roles.ButtonIcons.Get(Specs[(int)kind].Icon);
    }

    public static void Tick()
    {
        if (Marks.Count == 0) { _lastMs = 0; return; }
        long now = Environment.TickCount64;
        float dt = _lastMs == 0 ? 0.016f : Math.Min(0.05f, (now - _lastMs) / 1000f);
        _lastMs = now;

        var lp = PlayerControl.LocalPlayer;
        if (!lp || MeetingHud.Instance) { Clear(); return; }
        Vector2 me = lp.GetTruePosition();
        for (int i = Marks.Count - 1; i >= 0; i--)
        {
            var m = Marks[i];
            m.T += dt;
            if (!m.Root || m.T >= FadeIn + Hold + FadeOut)
            {
                DestroyMark(m);
                Marks.RemoveAt(i);
                continue;
            }
            UpdateMark(m, me.x, me.y);
        }
    }

    private static void UpdateMark(Mark m, float mx, float my)
    {
        float dx = m.Sx - mx, dy = m.Sy - my;
        float ang = MathF.Atan2(dy, dx);
        float c = MathF.Cos(ang), s = MathF.Sin(ang);
        float a, k;
        if (m.T < FadeIn) { float u = m.T / FadeIn; a = u; k = PopScale - (PopScale - 1f) * u; }
        else if (m.T < FadeIn + Hold) { a = 1f; k = 1f; }
        else { a = 1f - (m.T - FadeIn - Hold) / FadeOut; k = 1f; }
        float size = m.Size * k;

        m.Root.localPosition = V3(c * RingX, s * RingY, -50f);
        m.Root.localScale = V3(size, size, 1f);
        // 本編の矢印は Pivot の +x 側に矢じり (外向き)・-x 側に波紋 (内向き) があるので、Pivot ごと発生した方向へ回す
        m.Pivot.localRotation = RotZ(ang * (180f / MathF.PI));
        if (a == m.Alpha) return; // 出ている間は色を書き直さない
        m.Alpha = a;
        var col = Rgba(1f, 1f, 1f, a);
        m.Icon.color = col;
        m.Arrow.color = col;
        m.Waves.color = col;
    }

    private static void Clear()
    {
        foreach (var m in Marks) DestroyMark(m);
        Marks.Clear();
    }

    private static void DestroyMark(Mark m)
    {
        if (m.Root) UnityEngine.Object.Destroy(m.Root.gameObject);
    }

    private static Vector3 V3(float x, float y, float z)
    {
        Vector3 v = default;
        v.x = x; v.y = y; v.z = z;
        return v;
    }

    private static Quaternion RotZ(float deg)
    {
        float h = deg * (MathF.PI / 360f);
        Quaternion q = default;
        q.z = MathF.Sin(h); q.w = MathF.Cos(h);
        return q;
    }

    private static Color Rgba(float r, float g, float b, float a)
    {
        Color c = default;
        c.r = r; c.g = g; c.b = b; c.a = a;
        return c;
    }

    internal static void Register()
    {
        TestBridge.Register("noise", "[clear] 壁を壊した音: 鳴らした数・捨てた数・最後の音・出ているマーク", (args, reply) =>
        {
            if (args.Trim() == "clear") { Emitted = Dropped = 0; Last = "-"; Clear(); }
            var sb = new System.Text.StringBuilder();
            foreach (var m in Marks) sb.Append($" {m.Kind}@{m.Sx:0.0},{m.Sy:0.0} t={m.T:0.00} s={m.Size:0.00}");
            reply($"OK noise emitted={Emitted} dropped={Dropped} clips={Fx.MrpBundle.ClipCount} last={Last} marks={Marks.Count}{sb}");
        });
    }
}
