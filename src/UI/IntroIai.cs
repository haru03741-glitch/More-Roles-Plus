using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.UI;

// 画面の上の演出 (役職の発表など) で使う、段 4 (溜め切り) の居合の斬撃。試合の居合と同じ数値・同じ色で描く:
// 本体の斬線 (桃 → 青・芯が白く焼ける・伸びる → 止める → 細りながら薄れる)・追いかける細い線・
// 遅れて上下へ開く裂け目・破片・霜のもや、と白飛び → 暗転に白い線が焼き付く一コマ。
// 試合の居合は刀ごとの網に描くので、ここは板 (SpriteRenderer) で組み直している。出ている間だけ動かす
internal static class IntroIai
{
    private const int Lines = 7, Shards = 26, Puffs = 8;
    private const float White = 0.04f, Dark = 0.16f, Out = 0.34f;

    private struct Line
    {
        public float Delay, Len, W, Grow, Hold, Fade, Drift, A, Ox, Oy;
        public Color C0, C1;
        public bool Baked; // 桃 → 青を絵に焼いた本体 (色は白で掛ける)
    }

    private struct Shard
    {
        public float Delay, Life, X, Y, Vx, Vy, Rot, Spin, Size;
        public Color C;
    }

    private struct Puff
    {
        public float Delay, Life, X, Y, Vx, Vy, Len, W;
    }

    private static readonly Line[] _ln = new Line[Lines];
    private static readonly Shard[] _sh = new Shard[Shards];
    private static readonly Puff[] _pf = new Puff[Puffs];
    private static readonly SpriteRenderer[] _lnSr = new SpriteRenderer[Lines], _shSr = new SpriteRenderer[Shards], _pfSr = new SpriteRenderer[Puffs];
    private static readonly Transform[] _lnTf = new Transform[Lines];
    private static Transform _root, _flashLine;
    private static SpriteRenderer _flash, _flashCore, _flashGlow;
    private static Sprite _lineSprite, _shardSprite, _puffSprite, _flashSprite;
    private static float _t0, _ang, _co, _si, _flashLen;
    private static bool _live;

    // parent の中の (cx, cy, z) を通る ang 度の斬撃。w×h = 画面の広さ (parent の単位)
    // (sx, sy) = 画面の中心 (一コマの板はここに置く)
    public static void Play(Transform parent, float cx, float cy, float z, float ang, int layer, float w, float h, float sx, float sy)
    {
        if (_root) Object.Destroy(_root.gameObject);
        var go = new GameObject("IntroIai") { layer = layer };
        _root = go.transform;
        _root.SetParent(parent, false);
        _root.localPosition = FxMath.V3(cx, cy, z);
        _ang = ang;
        float ar = ang * FxMath.Deg2Rad;
        _co = FxMath.Cos(ar);
        _si = FxMath.Sin(ar);
        float diag = FxMath.Sqrt(w * w + h * h);
        // 試合の居合と同じ色 (桃・青)
        Color pink = FxMath.Rgba(1f, 0.37f, 0.66f, 1f), blue = FxMath.Rgba(0.29f, 0.55f, 1f, 1f), white = FxMath.Rgba(1f, 1f, 1f, 1f);
        float len = diag * 1.1f;
        int n = 0;
        _ln[n++] = new Line { Len = len, W = 0.46f, Grow = 0.06f, Hold = 0.20f, Fade = 0.45f, A = 1f, C0 = pink, C1 = blue, Baked = true };
        for (int i = 0; i < 4; i++)
        {
            float side = (i % 2 == 0 ? 1f : -1f) * (0.07f + 0.06f * i);
            _ln[n++] = new Line
            {
                Delay = 0.02f + 0.025f * i, Len = len * FxMath.Range(0.55f, 0.9f), W = 0.035f, Grow = 0.05f, Hold = 0.08f, Fade = 0.35f,
                Drift = side * 0.4f, A = 0.9f, Ox = -_si * side, Oy = _co * side, C0 = white, C1 = blue,
            };
        }
        // 少し遅れて斬線の上下に裂け目が開く
        _ln[n++] = new Line { Delay = 0.14f, Len = len * 0.96f, W = 0.16f, Grow = 0.03f, Hold = 0.10f, Fade = 0.55f, Drift = 0.26f, A = 0.8f, C0 = blue, C1 = blue };
        _ln[n++] = new Line { Delay = 0.14f, Len = len * 0.96f, W = 0.16f, Grow = 0.03f, Hold = 0.10f, Fade = 0.55f, Drift = -0.26f, A = 0.8f, C0 = pink, C1 = pink };

        for (int i = 0; i < Lines; i++)
        {
            var sr = Quad("Line", _ln[i].Baked ? LineSprite(true) : LineSprite(false), layer, -0.01f * i);
            _lnSr[i] = sr;
            _lnTf[i] = sr.transform;
            _lnTf[i].localRotation = FxMath.RotZ(ang);
        }
        // 破片 (白・氷の青・桃) と霜のもやを線に沿って
        for (int i = 0; i < Shards; i++)
        {
            float t = FxMath.Range(-0.5f, 0.5f) * w * 0.9f;
            float side = i % 2 == 0 ? 1f : -1f;
            float sp = FxMath.Range(0.4f, 1.3f) * 1.1f;
            float pick = FxMath.Range(0f, 1f);
            Color c = pick < 0.5f ? white : pick < 0.85f ? FxMath.Rgba(0.70f, 0.88f, 1f, 1f) : FxMath.Rgba(1f, 0.62f, 0.86f, 1f);
            _sh[i] = new Shard
            {
                Delay = 0.02f + FxMath.Range(0f, 0.04f), Life = FxMath.Range(0.8f, 1.5f), X = _co * t, Y = _si * t,
                Vx = -_si * side * sp + _co * FxMath.Range(-0.3f, 0.3f), Vy = _co * side * sp + _si * FxMath.Range(-0.3f, 0.3f),
                Rot = FxMath.Range(0f, 360f), Spin = FxMath.Range(-540f, 540f), Size = FxMath.Range(0.08f, 0.21f), C = c,
            };
            _shSr[i] = Quad("Shard", ShardSprite, layer, -0.1f);
        }
        for (int i = 0; i < Puffs; i++)
        {
            float t = FxMath.Range(-0.45f, 0.45f) * w;
            float side = FxMath.Range(-1f, 1f);
            float size = FxMath.Range(0.7f, 1.4f) * 1.3f;
            _pf[i] = new Puff
            {
                Delay = 0.03f + 0.02f * i, Life = FxMath.Range(0.7f, 1.2f), X = _co * t, Y = _si * t,
                Vx = -_si * side * 0.35f, Vy = _co * side * 0.35f + 0.05f, Len = 0.55f * size, W = 0.22f * size,
            };
            var sr = Quad("Puff", PuffSprite, layer, 0.02f);
            sr.transform.localRotation = FxMath.RotZ(ang);
            _pfSr[i] = sr;
        }
        // 一コマ: 画面全体の板 + 白い線 (斬線と同じ所)
        _flash = Quad("Flash", DuelHud.WhiteSprite(), layer, -0.4f);
        _flash.transform.localPosition = FxMath.V3(sx - cx, sy - cy, -0.4f);
        _flash.transform.localScale = FxMath.V3(w + 1f, h + 1f, 1f);
        var fl = new GameObject("FlashLine") { layer = layer };
        _flashLine = fl.transform;
        _flashLine.SetParent(_root, false);
        _flashLine.localPosition = FxMath.V3(0f, 0f, -0.45f);
        _flashLine.localRotation = FxMath.RotZ(ang);
        _flashGlow = Quad("Glow", FlashSprite, layer, 0f, _flashLine);
        _flashCore = Quad("Core", FlashSprite, layer, -0.01f, _flashLine);
        _flashLen = diag * 1.2f;

        _t0 = Time.time;
        _live = true;
        Tick();
    }

    private static SpriteRenderer Quad(string name, Sprite sp, int layer, float z, Transform parent = null)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(parent ? parent : _root, false);
        go.transform.localPosition = FxMath.V3(0f, 0f, z);
        go.transform.localScale = FxMath.V3(0.0001f, 0.0001f, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        sr.color = FxMath.Rgba(1f, 1f, 1f, 0f);
        return sr;
    }

    internal static void Tick()
    {
        if (!_live) return;
        if (!_root)
        {
            _live = false;
            return;
        }
        float now = Time.time - _t0;
        bool any = false;
        for (int i = 0; i < Lines; i++)
        {
            ref var ln = ref _ln[i];
            float u = now - ln.Delay;
            float total = ln.Grow + ln.Hold + ln.Fade;
            var sr = _lnSr[i];
            if (u < 0f) { any = true; continue; }
            if (u >= total)
            {
                if (ln.A > 0f)
                {
                    sr.color = FxMath.Rgba(1f, 1f, 1f, 0f);
                    ln.A = 0f;
                }
                continue;
            }
            any = true;
            // 伸びる (始めの端から先へ一気に)・止める・細りながら薄れる・止めの間は白く焼ける
            float g = FxMath.Clamp01(u / ln.Grow);
            g = 1f - (1f - g) * (1f - g) * (1f - g);
            float f = u > ln.Grow + ln.Hold ? (u - ln.Grow - ln.Hold) / ln.Fade : 0f;
            float width = ln.W * (1f - 0.85f * f);
            float alpha = ln.A * (1f - f) * (1f - f);
            float hot = f <= 0f ? 0.35f : 0.35f * (1f - f);
            float off = ln.Drift * (1f - (1f - f) * (1f - f));
            float L = ln.Len * g * (1f + 0.05f * f);
            // 始めの端 (-Len/2) から伸びる
            float start = -ln.Len * 0.5f;
            float mid = start + L * 0.5f;
            float px = -_si * off, py = _co * off;
            _lnTf[i].localPosition = FxMath.V3(ln.Ox + _co * mid + px, ln.Oy + _si * mid + py, -0.01f * i);
            // 絵は 128×32 で幅 1・高さ 0.25 なので縦は 4 倍して幅の単位に合わせる
            _lnTf[i].localScale = FxMath.V3(FxMath.Max(L, 0.0001f), FxMath.Max(width * 4f, 0.0001f), 1f);
            // 色は線の真ん中の色 (桃 → 青の流れは絵の側に焼いてある) に白の焼けを足す
            float cr = ln.Baked ? 1f : (ln.C0.r + ln.C1.r) * 0.5f, cg = ln.Baked ? 1f : (ln.C0.g + ln.C1.g) * 0.5f, cb = ln.Baked ? 1f : (ln.C0.b + ln.C1.b) * 0.5f;
            sr.color = FxMath.Rgba(cr + (1f - cr) * hot, cg + (1f - cg) * hot, cb + (1f - cb) * hot, alpha);
        }
        for (int i = 0; i < Shards; i++)
        {
            ref var s = ref _sh[i];
            float u = (now - s.Delay) / s.Life;
            if (u < 0f) { any = true; continue; }
            if (u >= 1f)
            {
                if (s.Size > 0f)
                {
                    _shSr[i].color = FxMath.Rgba(1f, 1f, 1f, 0f);
                    s.Size = 0f;
                }
                continue;
            }
            any = true;
            // 一気に飛んで止まり、ゆっくり沈みながら回ってきらめく
            float travel = 1f - (1f - u) * (1f - u) * (1f - u);
            float tw = 0.6f + 0.4f * FxMath.Sin((now + i) * 40f);
            var tf = _shSr[i].transform;
            tf.localPosition = FxMath.V3(s.X + s.Vx * travel, s.Y + s.Vy * travel - 0.15f * u * u, -0.1f);
            tf.localRotation = FxMath.RotZ(s.Rot + s.Spin * u);
            float sz = s.Size * (1f - 0.5f * u);
            tf.localScale = FxMath.V3(sz, sz, 1f);
            _shSr[i].color = FxMath.Rgba(s.C.r, s.C.g, s.C.b, (1f - u) * tw);
        }
        for (int i = 0; i < Puffs; i++)
        {
            ref var p = ref _pf[i];
            float u = (now - p.Delay) / p.Life;
            if (u < 0f) { any = true; continue; }
            if (u >= 1f)
            {
                if (p.W > 0f)
                {
                    _pfSr[i].color = FxMath.Rgba(1f, 1f, 1f, 0f);
                    p.W = 0f;
                }
                continue;
            }
            any = true;
            float grow = 0.6f + 0.6f * u;
            var tf = _pfSr[i].transform;
            tf.localPosition = FxMath.V3(p.X + p.Vx * u, p.Y + p.Vy * u, 0.02f);
            tf.localScale = FxMath.V3(p.Len * grow, p.W * grow, 1f);
            _pfSr[i].color = FxMath.Rgba(0.78f, 0.90f, 1f, 0.42f * FxMath.Sin(u * 3.14159f));
        }
        // 一コマ: 白く飛ぶ → 暗転に白い線だけが焼き付く → 戻る
        if (now < Out + 0.05f)
        {
            any = true;
            float darkA, lineA, r, g, b, lw;
            if (now < White) { r = g = b = 1f; darkA = 0.85f; lineA = 0f; lw = 0.02f; }
            else if (now < Dark) { r = 0.03f; g = 0.02f; b = 0.07f; darkA = 0.88f; lineA = 1f; lw = 0.07f; }
            else
            {
                float u = FxMath.Clamp01((now - Dark) / (Out - Dark));
                r = 0.03f; g = 0.02f; b = 0.07f;
                darkA = 0.88f * (1f - u) * (1f - u);
                lineA = 1f - u;
                lw = 0.07f * (1f - 0.6f * u);
            }
            _flash.color = FxMath.Rgba(r, g, b, darkA);
            _flashCore.color = FxMath.Rgba(1f, 1f, 1f, lineA);
            _flashGlow.color = FxMath.Rgba(0.55f, 0.72f, 1f, 0.75f * lineA);
            _flashCore.transform.localScale = FxMath.V3(_flashLen, lw, 1f);
            _flashGlow.transform.localScale = FxMath.V3(_flashLen, lw * 7f, 1f);
        }
        if (!any)
        {
            Object.Destroy(_root.gameObject);
            _root = null;
            _live = false;
        }
    }

    // ---- 絵 (式から 1 回だけ焼く) ----

    // 斬線: 縦は真ん中が白く両脇が色で外へ消える・横は両端が尖る・色は左 (始め) の桃から右の青へ
    private static Sprite _lineWhite;

    private static unsafe Sprite LineSprite(bool colored)
    {
        {
            if (colored && _lineSprite) return _lineSprite;
            if (!colored && _lineWhite) return _lineWhite;
            const int w = 128, h = 32;
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float along = (x + 0.5f) / w, across = FxMath.Abs((y + 0.5f) / h * 2f - 1f);
                float taper = FxMath.Min(1f, FxMath.Min(along, 1f - along) / 0.08f);
                float rel = across / FxMath.Max(taper, 0.001f);
                // 縦の形 (試合の居合の網と同じ 5 段: 外 0・芯の脇 0.14・芯)
                float a = rel >= 1f ? 0f : rel <= 0.14f ? 1f - 0.1f * rel / 0.14f : 0.9f * (1f - (rel - 0.14f) / 0.86f);
                float wh = rel >= 1f ? 0f : rel <= 0.14f ? 1f - 0.55f * rel / 0.14f : 0.45f * (1f - (rel - 0.14f) / 0.86f);
                float r = colored ? 1f + (0.29f - 1f) * along : 1f, g = colored ? 0.37f + (0.55f - 0.37f) * along : 1f, b = colored ? 0.66f + (1f - 0.66f) * along : 1f;
                int i = (y * w + x) * 4;
                px[i] = (byte)(255f * (r + (1f - r) * wh));
                px[i + 1] = (byte)(255f * (g + (1f - g) * wh));
                px[i + 2] = (byte)(255f * (b + (1f - b) * wh));
                px[i + 3] = (byte)(255f * FxMath.Clamp01(a));
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "MrpIntroIaiLine", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            fixed (byte* p = px) tex.LoadRawTextureData((System.IntPtr)p, px.Length);
            tex.Apply(false, true);
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            var sp = Sprite.Create(tex, new Rect(0, 0, w, h), FxMath.V2(0.5f, 0.5f), w);
            sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
            if (colored) _lineSprite = sp; else _lineWhite = sp;
            return sp;
        }
    }

    // 破片: 細長い三角
    private static Sprite ShardSprite => _shardSprite ? _shardSprite : _shardSprite = FxSprites.Rect(16, 16, (x, y) =>
    {
        float v = y + 0.5f;
        return FxMath.Abs(x) * 2f <= 0.6f * (1f - v) ? 1f : 0f;
    }, "MrpIntroIaiShard");

    private static Sprite PuffSprite => _puffSprite ? _puffSprite : _puffSprite = FxSprites.Rect(32, 32, FxSprites.Soft, "MrpIntroIaiPuff");

    // 一コマの線: 縦に柔らかく消え、両端が尖る帯 (試合の一コマと同じ形)
    private static Sprite FlashSprite => _flashSprite ? _flashSprite : _flashSprite = FxSprites.Rect(256, 32, (x, y) =>
    {
        float ax = FxMath.Abs(x) * 2f, ay = FxMath.Abs(y) * 2f;
        float end = FxMath.Clamp01((1f - ax) / 0.15f);
        float across = 1f - ay;
        return across <= 0f ? 0f : across * across * end;
    }, "MrpIntroIaiFlash");
}
