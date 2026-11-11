using System;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Roles;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 自分の息の残り: 足の着かない深さの水にいる間、頭の上に泡を 10 個並べ、息が減るたびに右から 1 つずつ割る。
// 水から出ると同じ速さで戻り、満ちて少し待つと消える。自分の画面にだけ出す (他の人の残りは見えない)。
// 溺れるかを決めるのはホストなので、ここは同じ規則 (足の着かない深さ・ベント/はしご/動く床では戻す・会議で忘れる) を
// 手元で数えた見込み。溺れると決まった知らせで空に、守りに弾かれた知らせでホストと同じ所へ合わせ直す。
// 泡はクルーの子なので動きには触らず、泡の数が変わった時と割れる/満ちる動きの間だけ書く
internal static class AirGauge
{
    private const int Count = 10;
    private const float PollEvery = 0.1f;
    private const float Size = 0.18f;         // 泡 1 つの直径 (単位)
    private const float Gap = 0.014f;
    private const float Above = 0.98f;         // 足元から泡の列の真ん中まで (名前の字が無い時)
    private const float OverName = 0.13f;      // 名前の字 (役職の行も含む) の上の端から泡の列の真ん中まで
    private const float FrontZ = -0.002f;      // 自分の体と水面の板より手前
    private const float PopTime = 0.2f;        // 割れる長さ
    private const float FillTime = 0.14f;      // 戻る泡がふくらむ長さ
    private const float FadeIn = 0.15f, Hold = 0.8f, FadeOut = 0.35f;
    private const float LowLeft = 3;           // 残りがこれ以下の泡で、次に割れる泡が震え、残りの泡が赤く脈打つ

    private static GameObject _go;
    private static SpriteRenderer[] _srs;
    private static Transform[] _tfs;
    private static float[] _anim;              // 泡ごとの動きの始まり (負 = 動いていない)
    private static bool[] _popping;
    private static float _used;                // 息を止めていた秒 (ホストの SwimFor の見込み)
    private static float _poll, _fullAt = -1f, _alpha;
    private static int _left = Count, _shipGen = -1;
    private static bool _shown, _animating;
    private static Sprite _bubble;

    internal static float Used => _used;
    internal static int Left => _left;

    internal static void Tick()
    {
        try { TickCore(); }
        catch (Exception e) { Plugin.Logger.LogError($"[AirGauge] {e}"); Clear(); }
    }

    private static void TickCore()
    {
        if (!_shown && _used <= 0f && !WaterSim.Running) return;
        if (GameClock.ShipGen != _shipGen) { Clear(); _shipGen = GameClock.ShipGen; }
        if (RewindPlayback.Active) return;
        float dt = Time.deltaTime;
        _poll += dt;
        if (_poll >= PollEvery)
        {
            float step = _poll;
            _poll = 0f;
            Count1(step);
        }
        if (_shown || _alpha > 0f) Draw(dt);
    }

    // 息を数える (ホストの TerrainDeath.Tick と同じ規則)
    private static void Count1(float dt)
    {
        var lp = PlayerControl.LocalPlayer;
        bool on = TerrainDeath.Enabled && lp && lp.Data != null && !lp.Data.IsDead && !MeetingHud.Instance && !ExileController.Instance;
        if (!on) { _used = 0f; Show(false); return; }
        float limit = TerrainDeath.DrownTime;
        bool deep = WaterSim.Running && WaterSim.Ready && !lp.inVent && !lp.onLadder && !lp.inMovingPlat
            && WaterSim.CurrentAt(lp.GetTruePosition(), out _, out _) >= CrewSwim.BraceMaxDepth
            && !CrewTraits.Has(lp.PlayerId, CrewTraits.Trait.Skim); // 水面を走っている間は息を数えない (ホストと同じ)
        if (deep) _used = FxMath.Min(limit, _used + dt * Roles.Impostor.RemoteController.BreathRate(lp.PlayerId));
        else if (_used > 0f) _used = FxMath.Max(0f, _used - dt);
        Set(Bubbles(limit));
        float now = Time.time;
        if (deep || _used > 0f) { _fullAt = -1f; Show(lp.Visible); return; }
        if (_fullAt < 0f) _fullAt = now;
        if (now - _fullAt > Hold) Show(false);
    }

    private static int Bubbles(float limit) => limit <= 0f ? 0 : FxMath.Clamp((int)MathF.Ceiling((limit - _used) / limit * Count - 1e-3f), 0, Count);

    // 溺れると決まった (自分): 息は空
    internal static void Empty()
    {
        _used = TerrainDeath.DrownTime;
        Set(0);
    }

    // 守りに弾かれた (自分): ホストは溺れる手前 back 秒の所から数え直す。溺れかけていた時だけ合わせる
    internal static void Guarded(float back)
    {
        float limit = TerrainDeath.DrownTime;
        if (_used >= limit - back) _used = limit - back;
    }

    // 時戻し・試合の替わり目: 数え直す
    internal static void Reset()
    {
        _used = 0f;
        if (_srs != null) Set(Count);
    }

    private static void Show(bool on)
    {
        if (on == _shown) return;
        _shown = on;
        if (on) { Make(); Place(); }
    }

    // 残りの泡の数を変える。減った分は割り、増えた分はふくらませる
    private static void Set(int left)
    {
        if (left == _left) return;
        float now = Time.time;
        if (_srs != null)
        {
            for (int i = left; i < _left; i++) { _anim[i] = now; _popping[i] = true; }
            for (int i = _left; i < left; i++) { _anim[i] = now; _popping[i] = false; }
            _animating = true;
        }
        _left = left;
    }

    private static void Make()
    {
        if (_go) return;
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return;
        var t = lp.transform;
        var scale = t.lossyScale;
        float sx = scale.x != 0f ? 1f / scale.x : 1f, sy = scale.y != 0f ? 1f / scale.y : 1f;
        _go = new GameObject("MrpAirGauge") { layer = lp.gameObject.layer };
        var root = _go.transform;
        root.SetParent(t, false);
        root.localScale = FxMath.V3(sx, sy, 1f);
        _srs = new SpriteRenderer[Count];
        _tfs = new Transform[Count];
        _anim = new float[Count];
        _popping = new bool[Count];
        float pitch = Size + Gap, x0 = -(Count - 1) * pitch * 0.5f;
        var sp = Bubble();
        for (int i = 0; i < Count; i++)
        {
            var go = new GameObject("Bubble") { layer = _go.layer };
            var bt = go.transform;
            bt.SetParent(root, false);
            bt.localPosition = FxMath.V3(x0 + i * pitch, 0f, 0f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sp;
            _srs[i] = sr;
            _tfs[i] = bt;
            _anim[i] = -1f;
        }
        _alpha = 0f;
        _animating = true;
    }

    // 名前の字 (役職などの行が上に足されていることがある) の上の端の上へ置く。字は出すたびに変わりうるので出す時に測る
    private static void Place()
    {
        var lp = PlayerControl.LocalPlayer;
        if (!_go || !lp) return;
        var t = lp.transform;
        Vector3 at = t.position, scale = t.lossyScale;
        Vector2 feet = lp.GetTruePosition();
        float y = feet.y + Above;
        // 役職名の札は名前の字の子
        var name = lp.cosmetics ? lp.cosmetics.nameText : null;
        if (name && name.gameObject.activeInHierarchy)
        {
            float top = feet.y;
            foreach (var mr in name.GetComponentsInChildren<MeshRenderer>())
                if (mr && mr.enabled) top = FxMath.Max(top, mr.bounds.max.y);
            if (top > feet.y) y = top + OverName;
        }
        float sx = scale.x != 0f ? 1f / scale.x : 1f, sy = scale.y != 0f ? 1f / scale.y : 1f;
        _go.transform.localPosition = FxMath.V3((feet.x - at.x) * sx, (y - at.y) * sy, FrontZ);
    }

    private static void Draw(float dt)
    {
        if (!_go) { _shown = false; _alpha = 0f; return; }
        float target = _shown ? 1f : 0f;
        float a = target > _alpha ? FxMath.Min(1f, _alpha + dt / FadeIn) : FxMath.Max(0f, _alpha - dt / FadeOut);
        bool fade = a != _alpha;
        _alpha = a;
        if (a <= 0f && !_shown) { Clear(); return; }
        float now = Time.time;
        bool low = _left > 0 && _left <= LowLeft;
        if (!fade && !_animating && !low) return;
        bool still = false;
        // 残りが少ない: 残りの泡が赤く脈打つ
        float warn = low ? 0.5f + 0.5f * FxMath.Sin(now * 9f) : 0f;
        for (int i = 0; i < Count; i++)
        {
            float s = 1f, al = i < _left ? 1f : 0f, dx = 0f;
            if (_anim[i] >= 0f)
            {
                float k = now - _anim[i];
                if (_popping[i])
                {
                    // 割れる: 少し膨らんで薄くなる
                    float u = k / PopTime;
                    if (u < 1f) { s = 1f + 0.4f * u; al = (1f - u) * (1f - u); still = true; }
                    else _anim[i] = -1f;
                }
                else
                {
                    float u = k / FillTime;
                    if (u < 1f) { s = u * (1.15f - 0.15f * u); still = true; }
                    else _anim[i] = -1f;
                }
            }
            // 次に割れる泡は残りが少ないと震える
            if (low && i == _left - 1) dx = 0.008f * FxMath.Sin(now * 47f);
            var bt = _tfs[i];
            float pitch = Size + Gap, x0 = -(Count - 1) * pitch * 0.5f;
            bt.localPosition = FxMath.V3(x0 + i * pitch + dx, 0f, 0f);
            bt.localScale = FxMath.V3(Size * s, Size * s, 1f);
            float w = i < _left ? warn : 0f;
            _srs[i].color = FxMath.Rgba(1f, 1f - 0.55f * w, 1f - 0.6f * w, al * _alpha);
        }
        _animating = still;
    }

    internal static void Clear()
    {
        if (_go) UnityEngine.Object.Destroy(_go);
        _go = null;
        _srs = null;
        _tfs = null;
        _shown = false;
        _animating = false;
        _alpha = 0f;
        _left = Count;
        _used = 0f;
        _fullAt = -1f;
    }

    // 泡: 本編の絵と同じ描き方。紺の太い縁・水色の平塗り・左下の影の三日月・右上の白い照り (光は右上から)。
    // 縁があるので明るい床でも水の上でも泡だけで見える。1 画素を 4×4 で測って縁をなめらかにする (1 単位四方 = 直径 1)
    internal static Sprite BubbleSprite => Bubble();

    private static Sprite Bubble()
    {
        if (_bubble) return _bubble;
        const int n = 64, ss = 4;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float r = 0f, g = 0f, b = 0f, a = 0f;
            for (int sy = 0; sy < ss; sy++)
            for (int sx = 0; sx < ss; sx++)
            {
                float u = (x + (sx + 0.5f) / ss) / n * 2f - 1f, v = (y + (sy + 0.5f) / ss) / n * 2f - 1f;
                float d = FxMath.Sqrt(u * u + v * v);
                if (d > 0.97f) continue;
                float cr, cg, cb;
                if (d > 0.78f) { cr = 14f; cg = 22f; cb = 48f; }       // 縁 (本編の縁の紺)
                else
                {
                    // 右上へずらした円の外 = 影の三日月
                    float lx = u - 0.17f, ly = v - 0.17f;
                    bool lit = lx * lx + ly * ly < 0.62f * 0.62f;
                    cr = lit ? 150f : 86f; cg = lit ? 222f : 160f; cb = lit ? 255f : 222f;
                    // 右上の照り (斜めの楕円) と、その左の小さな点
                    float hx = u - 0.26f, hy = v - 0.28f;
                    float hu = (hx + hy) * 0.7071f, hv = (hx - hy) * 0.7071f;
                    bool shine = hu * hu / (0.24f * 0.24f) + hv * hv / (0.12f * 0.12f) < 1f;
                    float dx = u + 0.06f, dy = v - 0.44f;
                    if (shine || dx * dx + dy * dy < 0.065f * 0.065f) { cr = 255f; cg = 255f; cb = 255f; }
                }
                r += cr; g += cg; b += cb; a += 1f;
            }
            if (a <= 0f) continue;
            int i = (y * n + x) * 4;
            px[i] = (byte)(r / a); px[i + 1] = (byte)(g / a); px[i + 2] = (byte)(b / a);
            px[i + 3] = (byte)(255f * a / (ss * ss));
        }
        _bubble = ToSprite(px, n, "MrpAirBubble");
        return _bubble;
    }

    private static unsafe Sprite ToSprite(byte[] px, int n, string name)
    {
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
        {
            name = name,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, true);
        // 1 単位四方 (大きさは泡ごとの localScale で決める)
        var sp = Sprite.Create(tex, new Rect(0, 0, n, n), FxMath.V2(0.5f, 0.5f), n);
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    internal static string Describe() => $"air used={_used:0.0}/{(float)TerrainDeath.DrownTime} left={_left} shown={_shown} alpha={_alpha:0.00}";
}
