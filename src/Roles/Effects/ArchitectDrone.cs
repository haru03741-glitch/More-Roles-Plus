using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 構築士のドローン: 本体は描かず、床に落ちる影と羽音だけ (全員に見える・聞こえる)。
// 設計図を開くと設計台から浮き上がり、台の上で小さく 8 の字に揺れて待つ。壁が立つ・配管が裂ける・瓦礫を拾うたびに
// その所へ飛んで少しとどまり、次へ。閉じると台へ戻って降りる。位置は端末ごとに少しずれてよい (出来事の届く時刻の差)。
// 影 = 4 枚羽根の機体の形のぼけた影 + 回る羽根の影 4 つ。絵は 1 回だけ作業スレッドで焼く
internal sealed class ArchitectDrone
{
    private const float Alpha = 0.35f;       // 影の濃さ
    private const float Width = 0.9f;        // 影の横幅 (世界の長さ)
    private const float RiseTime = 0.4f;     // 浮き上がる・降りる時間
    private const float RiseScale = 0.55f;   // 台の上にいる時の影の大きさの比 (低いほど小さく濃い)
    // 待つ所 = 設計台の床の点から右手前 (台の真上だと影が台の下に隠れ、手前だと台の前に立つ構築士に重なる)
    private const float HoverDx = 0.75f, HoverDy = -0.15f;
    private const float EightX = 0.18f, EightY = 0.09f, EightPeriod = 2.6f;
    private const float Speed = 9f, Accel = 5f; // 飛ぶ速さの上限・残りの距離に掛けて減速する係数
    private const float Hold = 0.6f;         // 着いた所にとどまる時間
    private const float BladeSpin = 760f;    // 羽根の影の回る速さ (度/秒)
    private const float SoundRange = 14f, SoundMuffle = 7f, SoundFull = 3f, SoundVolume = 0.55f;
    private const float FlyPitch = 1.08f;    // 飛んでいる間は少し高く唸る
    private const float SoundEvery = 0.1f;   // 音量と左右の見直しの間隔
    private const float ZEvery = 0.25f;      // 床の z を見直す間隔
    private const int Ppu = 200;

    // 機体の影の絵の中の羽根の軸 (絵の真ん中からの世界の長さ)。上から見た 3/4 視点で縦を少し潰す
    private static readonly float[] RotorX = { -0.28f, 0.28f, -0.28f, 0.28f };
    private static readonly float[] RotorY = { 0.17f, 0.17f, -0.17f, -0.17f };
    private const float RotorR = 0.16f;
    private const float Squash = 0.8f;

    private enum State { Rise, Hover, Fly, Hold, Return, Land, Done }

    private struct Target
    {
        public float X, Y;
        public Action OnArrive;
    }

    private static readonly List<ArchitectDrone> Flying = new();
    private static Task<(byte[] Body, byte[] Blade)> _bake;
    private static Sprite _bodySprite, _bladeSprite;

    private readonly Queue<Target> _targets = new();
    private GameObject _root;
    private Transform _tr;
    private readonly Transform[] _blades = new Transform[4];
    private readonly SpriteRenderer[] _srs = new SpriteRenderer[5];
    private AudioSource _src;
    private string _clip;
    private State _state;
    private float _t, _x, _y, _z, _vx, _vy, _homeX, _homeY, _spin, _soundT, _zT;
    private float _shownAlpha = -1f, _shownScale = -1f;
    private Target _cur;
    private bool _closing;
    private readonly int _gen;

    internal bool Done => _state == State.Done;
    internal float X => _x;
    internal float Y => _y;
    // 機体の影の絵 (飛んでいるドローンがあれば焼き終わっている)。設計図の印にも使う
    internal static Sprite BodySprite => _bodySprite;
    // 飛んでいて、まだ降り始めていない
    internal bool Airborne => !_closing && _state is not (State.Land or State.Done);
    internal string Describe() => $"{_state} at=({_x:0.00},{_y:0.00}) queue={_targets.Count} sound={(_src ? _src.volume : 0f):0.00}{(_clip != null && _clip.EndsWith("_m") ? "m" : "")}";

    internal static void Prewarm() => _bake ??= Task.Run(() => (BakeBody(), BakeBlade()));

    private ArchitectDrone(float deskX, float deskY)
    {
        _gen = GameClock.ShipGen;
        _homeX = deskX + HoverDx;
        _homeY = deskY + HoverDy;
        _x = _homeX; _y = _homeY;
    }

    // 設計台の脇から飛び立つ。絵が作れなければ null
    internal static ArchitectDrone Launch(float deskX, float deskY)
    {
        var d = new ArchitectDrone(deskX, deskY);
        if (!d.Build()) return null;
        Flying.Add(d);
        return d;
    }

    // 毎フレーム (飛んでいる物が無ければ何もしない)
    internal static void FrameAll()
    {
        if (Flying.Count == 0) return;
        float dt = Time.deltaTime;
        for (int i = Flying.Count - 1; i >= 0; i--)
        {
            bool alive;
            try { alive = Flying[i].Tick(dt); }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[ArchitectDrone] tick: {e}");
                Flying[i].Destroy();
                alive = false;
            }
            if (!alive) Flying.RemoveAt(i);
        }
    }

    // ここへ飛ぶ (着いたら onArrive)。閉じた後は飛ばずにすぐ onArrive
    internal void Visit(float x, float y, Action onArrive = null)
    {
        if (_closing || _state is State.Land or State.Done) { onArrive?.Invoke(); return; }
        _targets.Enqueue(new Target { X = x, Y = y, OnArrive = onArrive });
    }

    // 台へ戻って降りる (残りの行き先は飛ばずに済ませる)
    internal void Close()
    {
        _closing = true;
        while (_targets.Count > 0) _targets.Dequeue().OnArrive?.Invoke();
        if (_state is State.Fly or State.Hold) { _cur.OnArrive?.Invoke(); _cur = default; }
        if (_state is not (State.Land or State.Done)) { _state = State.Return; _t = 0f; }
    }

    // 残りの行き先の onArrive (瓦礫を消す等) は済ませる。船が変わった後は前の船の物なので呼ばない
    internal void Destroy()
    {
        if (_state != State.Done && _gen == GameClock.ShipGen && GameClock.ShipAlive)
        {
            while (_targets.Count > 0) Arrive(_targets.Dequeue().OnArrive);
            Arrive(_cur.OnArrive);
            _cur = default;
        }
        _state = State.Done;
        if (_root) UnityEngine.Object.Destroy(_root);
        _root = null;
    }

    private static void Arrive(Action a)
    {
        if (a == null) return;
        try { a(); }
        catch (Exception e) { Plugin.Logger.LogError($"[ArchitectDrone] arrive: {e}"); }
    }

    // 毎フレーム。終わったら false
    internal bool Tick(float dt)
    {
        if (_state == State.Done) return false;
        if (!_root || _gen != GameClock.ShipGen || !GameClock.ShipAlive) { Destroy(); return false; }
        _t += dt;
        float alpha = 1f, scale = 1f;
        bool flying = false;
        switch (_state)
        {
            case State.Rise:
                {
                    float k = FxMath.Clamp01(_t / RiseTime);
                    alpha = k; scale = RiseScale + (1f - RiseScale) * Ease(k);
                    if (k >= 1f) { _state = State.Hover; _t = 0f; }
                    break;
                }
            case State.Hover:
                {
                    if (_targets.Count > 0) { _cur = _targets.Dequeue(); _state = State.Fly; _t = 0f; break; }
                    float w = _t * (MathF.PI * 2f / EightPeriod);
                    Steer(_homeX + EightX * FxMath.Sin(w), _homeY + EightY * FxMath.Sin(w * 2f), dt);
                    break;
                }
            case State.Fly:
                flying = true;
                if (Steer(_cur.X, _cur.Y, dt))
                {
                    _cur.OnArrive?.Invoke();
                    _cur.OnArrive = null;
                    _state = State.Hold; _t = 0f;
                }
                break;
            case State.Hold:
                {
                    float w = _t * 9f;
                    Steer(_cur.X + 0.04f * FxMath.Sin(w), _cur.Y + 0.03f * FxMath.Sin(w * 1.3f), dt);
                    if (_t >= Hold)
                    {
                        if (_targets.Count > 0) { _cur = _targets.Dequeue(); _state = State.Fly; }
                        else _state = _closing ? State.Return : State.Hover;
                        _t = 0f;
                    }
                    break;
                }
            case State.Return:
                flying = true;
                if (Steer(_homeX, _homeY, dt)) { _state = State.Land; _t = 0f; }
                break;
            case State.Land:
                {
                    float k = FxMath.Clamp01(_t / RiseTime);
                    alpha = 1f - k; scale = 1f - (1f - RiseScale) * Ease(k);
                    if (k >= 1f) { Destroy(); return false; }
                    break;
                }
        }
        Draw(alpha, scale, dt);
        TickSound(dt, alpha, flying);
        return true;
    }

    private static float Ease(float k) => k * k * (3f - 2f * k);

    // 行き先へ寄せる (残りの距離に比例して減速・上限 Speed)。着いたら true
    private bool Steer(float tx, float ty, float dt)
    {
        float dx = tx - _x, dy = ty - _y, d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d < 0.03f) { _x = tx; _y = ty; _vx = _vy = 0f; return true; }
        float v = FxMath.Min(Speed, 0.4f + d * Accel);
        // 向きは少し遅れてついてくる (曲がる時にふくらむ)
        float k = FxMath.Clamp01(dt * 10f);
        _vx += (dx / d * v - _vx) * k;
        _vy += (dy / d * v - _vy) * k;
        float step = FxMath.Sqrt(_vx * _vx + _vy * _vy) * dt;
        if (step >= d) { _x = tx; _y = ty; _vx = _vy = 0f; return true; }
        _x += _vx * dt; _y += _vy * dt;
        return false;
    }

    private void Draw(float alpha, float scale, float dt)
    {
        _zT -= dt;
        if (_zT <= 0f)
        {
            _zT = ZEvery;
            float front = DamageMap.FrontZ(FxMath.V2(_x, _y));
            _z = front - 0.002f * DamageMap.ZScale(front); // 床の絵のすぐ手前 (人の下に入る)
        }
        _tr.position = FxMath.V3(_x, _y, _z);
        if (scale != _shownScale) { _shownScale = scale; _tr.localScale = FxMath.V3(scale, scale, 1f); }
        if (alpha != _shownAlpha)
        {
            _shownAlpha = alpha;
            var c = FxMath.Rgba(0f, 0f, 0f, Alpha * alpha);
            for (int i = 0; i < _srs.Length; i++) _srs[i].color = c;
        }
        _spin = (_spin + BladeSpin * dt) % 360f;
        // 対角の羽根どうしが同じ向きに回る (実機のドローンと同じ)
        for (int i = 0; i < 4; i++) _blades[i].localRotation = FxMath.RotZ((i == 0 || i == 3 ? _spin : -_spin) + 30f * i);
    }

    private void TickSound(float dt, float alpha, bool flying)
    {
        _soundT -= dt;
        if (_soundT > 0f || !_src) return;
        _soundT = SoundEvery;
        var lp = PlayerControl.LocalPlayer;
        float want = 0f, pan = 0f;
        bool muffle = false;
        if (lp && !MeetingHud.Instance)
        {
            Vector2 me = Watch.Ear(lp);
            float dx = _x - me.x, dy = _y - me.y, d = FxMath.Sqrt(dx * dx + dy * dy);
            if (d < SoundRange)
            {
                float near = d <= SoundFull ? 1f : 1f - (d - SoundFull) / (SoundRange - SoundFull);
                want = SoundVolume * near * FxMath.Sqrt(near) * alpha;
                pan = FxMath.Clamp(dx / 10f, -0.7f, 0.7f);
                muffle = d > SoundMuffle;
            }
        }
        string clip = muffle ? "noise_drone_loop_m" : "noise_drone_loop";
        if (want <= 0.005f)
        {
            if (_src.isPlaying) _src.Stop();
            return;
        }
        if (clip != _clip || !_src.isPlaying)
        {
            var c = MrpBundle.Clip(clip);
            if (!c) return;
            // こもりの切り替えでも鳴り始めの位置を揃える (同じ長さのループ)
            float at = _src.isPlaying && _src.clip ? _src.time / _src.clip.length : FxMath.Value;
            _clip = clip;
            _src.clip = c;
            _src.Play();
            _src.time = at * c.length;
        }
        _src.volume = want * (muffle ? 1.3f : 1f); // 本編の効果音の音量は効果音のミキサーが掛ける
        _src.panStereo = pan;
        _src.pitch = flying ? FlyPitch : 1f;
    }

    private bool Build()
    {
        var ship = ShipStatus.Instance;
        if (!ship || !GameClock.ShipAlive || !EnsureSprites()) return false;
        _root = new GameObject("MrpArchitectDrone");
        _root.transform.SetParent(ship.transform, false);
        // 船の倍率と反転を打ち消して世界の長さで描く
        Vector3 ls = ship.transform.lossyScale;
        var holder = new GameObject("Shadow");
        holder.transform.SetParent(_root.transform, false);
        _root.transform.localScale = FxMath.V3(1f / ls.x, 1f / ls.y, 1f / ls.z);
        _tr = holder.transform;
        _srs[0] = holder.AddComponent<SpriteRenderer>();
        _srs[0].sprite = _bodySprite;
        for (int i = 0; i < 4; i++)
        {
            var b = new GameObject("Blade");
            b.transform.SetParent(_tr, false);
            b.transform.localPosition = FxMath.V3(RotorX[i], RotorY[i] * Squash, -0.0001f);
            // 羽根は 3/4 視点で縦に潰れた円の中で回る
            var pivot = new GameObject("Spin");
            pivot.transform.SetParent(b.transform, false);
            b.transform.localScale = FxMath.V3(1f, Squash, 1f);
            var sr = pivot.AddComponent<SpriteRenderer>();
            sr.sprite = _bladeSprite;
            _srs[i + 1] = sr;
            _blades[i] = pivot.transform;
        }
        var sm = SoundManager.Instance;
        if (sm)
        {
            _src = _root.AddComponent<AudioSource>();
            _src.playOnAwake = false;
            _src.loop = true;
            _src.spatialBlend = 0f;
            _src.volume = 0f;
            _src.outputAudioMixerGroup = sm.SfxChannel;
        }
        _state = State.Rise;
        _t = 0f;
        _zT = 0f;
        Draw(0f, RiseScale, 0f);
        return true;
    }

    private static unsafe bool EnsureSprites()
    {
        if (_bodySprite && _bladeSprite) return true;
        (byte[] Body, byte[] Blade) px;
        try
        {
            Prewarm();
            // 焼き終わる前は待たない (遅い端末で開いた瞬間に止まらないよう、今回は飛ばさない)
            if (!_bake.IsCompleted) return false;
            px = _bake.Result;
        }
        catch (Exception e)
        {
            _bake = null;
            Plugin.Logger.LogError($"[ArchitectDrone] bake: {e}");
            return false;
        }
        _bodySprite = MakeSprite("MrpDroneBody", px.Body, BodyW, BodyH);
        _bladeSprite = MakeSprite("MrpDroneBlade", px.Blade, BladePx, BladePx);
        return _bodySprite && _bladeSprite;
    }

    private static unsafe Sprite MakeSprite(string name, byte[] px, int w, int h)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        fixed (byte* b = px) tex.LoadRawTextureData((IntPtr)b, px.Length);
        tex.Apply(false, true);
        tex.hideFlags |= HideFlags.HideAndDontSave;
        var sp = Sprite.Create(tex, new Rect(0, 0, w, h), FxMath.V2(0.5f, 0.5f), Ppu, 0, SpriteMeshType.FullRect);
        sp.hideFlags |= HideFlags.HideAndDontSave;
        return sp;
    }

    // ---- 焼く (作業スレッド・白の絵に濃さだけ。色は SpriteRenderer.color で黒にする) ----

    private static int BodyW => (int)MathF.Round((Width + 0.1f) * Ppu);
    private static int BodyH => (int)MathF.Round((Width * 0.75f + 0.1f) * Ppu);
    private const int BladePx = 80;          // 羽根の影の絵 (0.4 × 0.4)
    private const float Soft = 0.035f;       // 影の縁のぼけ (高く飛んでいるので輪郭は柔らかい)

    private static byte[] BakeBody()
    {
        int w = BodyW, h = BodyH;
        var px = new byte[w * h * 4];
        float hw = w * 0.5f / Ppu, hh = h * 0.5f / Ppu;
        for (int iy = 0; iy < h; iy++)
        {
            for (int ix = 0; ix < w; ix++)
            {
                float x = (ix + 0.5f) / Ppu - hw, y = ((iy + 0.5f) / Ppu - hh) / Squash;
                // 胴 (角の丸い四角)・腕 4 本 (胴から羽根の軸へ)・羽根の回る円 (半分の濃さ・縁の枠は濃い)・モーター (濃い点)。
                // 細い線だけだと床の傷に見えるので、塊として読める太さにする
                float a = Cover(RoundRect(x, y, 0.17f, 0.12f, 0.06f));
                for (int i = 0; i < 4; i++)
                {
                    float rx = RotorX[i], ry = RotorY[i];
                    a = MathF.Max(a, Cover(Segment(x, y, 0f, 0f, rx, ry) - 0.035f));
                    a = MathF.Max(a, Cover(Circle(x, y, rx, ry, 0.05f)));
                    float disc = Circle(x, y, rx, ry, RotorR);
                    a = MathF.Max(a, 0.5f * Cover(disc));
                    a = MathF.Max(a, 0.8f * Cover(MathF.Abs(disc) - 0.012f));
                }
                int k = (iy * w + ix) * 4;
                px[k] = px[k + 1] = px[k + 2] = 255;
                px[k + 3] = (byte)Math.Clamp((int)(a * 255f + 0.5f), 0, 255);
            }
        }
        return px;
    }

    // 羽根 2 枚の影。回る向きの後ろへ薄く尾を引く (動きのぼけ)
    private static byte[] BakeBlade()
    {
        int n = BladePx;
        var px = new byte[n * n * 4];
        float half = n * 0.5f / Ppu;
        const float len = RotorR * 0.95f, thick = 0.03f;
        for (int iy = 0; iy < n; iy++)
        {
            for (int ix = 0; ix < n; ix++)
            {
                float x = (ix + 0.5f) / Ppu - half, y = (iy + 0.5f) / Ppu - half;
                float r = MathF.Sqrt(x * x + y * y);
                float a = 0f;
                if (r <= len + Soft)
                {
                    float ang = MathF.Atan2(y, x);
                    // 羽根の線からの角度 (2 枚なので 180° ごと)・羽根の後ろ側 (負の角度) に尾
                    float rel = ang - MathF.Floor(ang / MathF.PI) * MathF.PI;
                    if (rel > MathF.PI * 0.5f) rel -= MathF.PI;
                    float across = MathF.Abs(MathF.Sin(rel)) * r;
                    float blade = Cover(across - thick * (1f - 0.4f * r / len)) * Cover(r - len);
                    float trail = rel < 0f && rel > -0.6f ? (1f + rel / 0.6f) * 0.35f * Cover(r - len) * FxMath.Clamp01(r / 0.03f) : 0f;
                    a = MathF.Max(blade, trail);
                }
                int k = (iy * n + ix) * 4;
                px[k] = px[k + 1] = px[k + 2] = 255;
                px[k + 3] = (byte)Math.Clamp((int)(a * 255f + 0.5f), 0, 255);
            }
        }
        return px;
    }

    // 符号付きの距離 (内側が負) → 覆う割合 (縁は Soft の幅でぼける)
    private static float Cover(float d) => FxMath.Clamp01(0.5f - d / (Soft * 2f));

    private static float Circle(float x, float y, float cx, float cy, float r)
    {
        float dx = x - cx, dy = y - cy;
        return MathF.Sqrt(dx * dx + dy * dy) - r;
    }

    private static float RoundRect(float x, float y, float hw, float hh, float r)
    {
        float qx = MathF.Abs(x) - hw + r, qy = MathF.Abs(y) - hh + r;
        float ox = MathF.Max(qx, 0f), oy = MathF.Max(qy, 0f);
        return MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(qx, qy), 0f) - r;
    }

    private static float Segment(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        float t = l2 > 0f ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l2, 0f, 1f) : 0f;
        float qx = ax + dx * t - px, qy = ay + dy * t - py;
        return MathF.Sqrt(qx * qx + qy * qy);
    }
}
