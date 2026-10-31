using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 立ち合いの場の暗がり。戦っている組の足元に黒い溜まりと、ゆっくり渦を巻く暗い煙を敷く (全員の画面で)。
// 当事者が影になる場の雰囲気を作り、離れた所からも「あそこで斬り合っている」と分かる。人より奥 (床の斬り跡と同じ置き方)。
// 組ごとに 1 つ。中心と広さは数フレームごとに当事者の位置から決め直し、間は滑らかに寄せる。組が畳まれたら薄れて消える。
// 戦いの近くにいる (生きている) 人の画面は、縁から暗く沈む (近いほど濃い・HUD は暗くしない = ゲームのカメラの手前に置く)
internal static class DuelGloom
{
    private const int Wisps = 4;
    private const float FadeIn = 0.5f, FadeOut = 1.2f;
    private const float PoolA = 0.6f, WispA = 0.24f;
    private const float Pad = 2.0f, MinR = 2.4f, MaxR = 6f, Follow = 6f;
    // 画面の沈み: 戦いの広さの VigInside 倍より内側で一番濃く、そこから VigFade 離れると消える
    private const float VigA = 0.55f, VigInner = 0.4f, VigInside = 0.6f, VigFade = 7f, VigEase = 3f;
    private const int MeasureEvery = 3;

    private sealed class Pool
    {
        public GameObject Go;
        public SpriteRenderer Core;
        public Transform CoreTf;
        public readonly SpriteRenderer[] W = new SpriteRenderer[Wisps];
        public readonly Transform[] WTf = new Transform[Wisps];
        public float A, Cx, Cy, R, GoalX, GoalY, GoalR, Seed;
        public bool Placed;
    }

    private static readonly Pool[] Pools = new Pool[Duel.MaxGroups + 1];
    private static int _live, _frame, _gen = -1;
    private static Sprite _soft;
    internal static int Made;

    // 毎フレーム (Plugin から)。組も残りの暗がりも無ければ比べるだけで帰る
    internal static void Tick()
    {
        if (!Duel.Any && _live == 0 && _vigA <= 0f) return;
        if (_gen != Terrain.GameClock.ShipGen)
        {
            // 船が替わった (暗がりは船の子なので一緒に消えている)
            _gen = Terrain.GameClock.ShipGen;
            for (int g = 0; g < Pools.Length; g++) Pools[g] = null;
            _live = 0;
        }
        var ship = ShipStatus.Instance;
        if (!ship || !GameData.Instance)
        {
            // 試合が終わった (カメラはロビーへ持ち越すので沈みを外す)
            _vigA = _vigGoal = 0f;
            if (_vigGo) _vigGo.SetActive(false);
            return;
        }
        float dt = Time.deltaTime, now = Time.time;
        bool measure = ++_frame % MeasureEvery == 0;
        for (byte g = 1; g <= Duel.MaxGroups; g++)
        {
            int n = Duel.CountOf(g);
            var p = Pools[g];
            if (n > 0 && measure)
            {
                if (p == null || !p.Go) p = Pools[g] = Make(ship, g);
                if (p != null) Measure(p, g, n);
            }
            if (p == null || !p.Go || !p.Placed) continue;
            float goal = n > 0 ? 1f : 0f;
            float was = p.A;
            p.A = goal > p.A ? FxMath.Min(1f, p.A + dt / FadeIn) : FxMath.Max(0f, p.A - dt / FadeOut);
            if (p.A <= 0f)
            {
                if (was > 0f) { p.Go.SetActive(false); _live--; }
                continue;
            }
            if (was <= 0f) { p.Go.SetActive(true); _live++; }
            float k = FxMath.Min(1f, Follow * dt);
            p.Cx += (p.GoalX - p.Cx) * k;
            p.Cy += (p.GoalY - p.Cy) * k;
            p.R += (p.GoalR - p.R) * k;
            Draw(p, now);
        }
        Vignette(dt, measure);
    }

    // ---- 画面の沈み ----

    private static GameObject _vigGo;
    private static Transform _vigTf;
    private static SpriteRenderer _vigSr;
    private static Camera _vigCam;
    private static float _vigA, _vigGoal, _vigShownA = -1f;

    private static void Vignette(float dt, bool measure)
    {
        if (measure) _vigGoal = VigGoal();
        if (_vigA <= 0f && _vigGoal <= 0f) return;
        _vigA += (_vigGoal - _vigA) * FxMath.Min(1f, VigEase * dt);
        if (_vigGoal <= 0f && _vigA < 0.01f) _vigA = 0f;
        if (!_vigGo || !_vigCam)
        {
            if (_vigA <= 0f || !MakeVignette()) return;
        }
        bool on = _vigA > 0f;
        if (_vigGo.activeSelf != on) _vigGo.SetActive(on);
        if (!on) return;
        if (measure)
        {
            // カメラの写す範囲 (寄せ/引きで変わる) に合わせて画面いっぱいに
            float h = _vigCam.orthographicSize * 2.06f, w = h * _vigCam.aspect;
            _vigTf.localScale = FxMath.V3(w, h, 1f);
        }
        float a = VigA * _vigA;
        if (FxMath.Abs(a - _vigShownA) < 0.004f) return;
        _vigShownA = a;
        _vigSr.color = FxMath.Rgba(0f, 0f, 0.015f, a);
    }

    // 自分から一番近い暗がりまでの近さ (0〜1)。幽霊・会議の間は沈めない
    private static float VigGoal()
    {
        var lp = PlayerControl.LocalPlayer;
        if (_live == 0 || !lp || lp.Data == null || lp.Data.IsDead || MeetingHud.Instance) return 0f;
        Vector2 me = lp.GetTruePosition();
        float best = 0f;
        for (int g = 1; g < Pools.Length; g++)
        {
            var p = Pools[g];
            if (p == null || !p.Go || p.A <= 0f) continue;
            float dx = me.x - p.Cx, dy = me.y - p.Cy;
            float d = FxMath.Sqrt(dx * dx + dy * dy) - p.R * VigInside;
            float k = FxMath.Clamp01(1f - d / VigFade) * p.A;
            if (k > best) best = k;
        }
        return best * best * (3f - 2f * best);
    }

    // ゲームのカメラの子 (世界より手前・HUD は別のカメラなので暗くならない)
    private static bool MakeVignette()
    {
        var cam = Camera.main;
        if (!cam) return false;
        if (_vigGo) Object.Destroy(_vigGo);
        _vigCam = cam;
        _vigGo = new GameObject("MrpDuelVignette") { layer = cam.gameObject.layer };
        _vigTf = _vigGo.transform;
        _vigTf.SetParent(cam.transform, false);
        _vigTf.localPosition = FxMath.V3(0f, 0f, cam.nearClipPlane + 0.5f);
        _vigSr = _vigGo.AddComponent<SpriteRenderer>();
        _vigSr.sprite = VigSprite();
        _vigShownA = -1f;
        float h = cam.orthographicSize * 2.06f;
        _vigTf.localScale = FxMath.V3(h * cam.aspect, h, 1f);
        return true;
    }

    private static Sprite _vigSprite;

    // 縁ほど濃い黒 (真ん中は VigInner)。1 単位の四角・1 回だけ作る
    private static Sprite VigSprite()
    {
        if (_vigSprite) return _vigSprite;
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float r = FxMath.Clamp01(FxMath.Sqrt(dx * dx + dy * dy) / 1.4142f);
                float t = FxMath.Clamp01((r - 0.2f) / 0.8f);
                t = t * t * (3f - 2f * t);
                px[y * n + x] = FxMath.Rgba32(255, 255, 255, (byte)(255f * (VigInner + (1f - VigInner) * t)));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.HideAndDontSave;
        _vigSprite = Sprite.Create(tex, new Rect(0, 0, n, n), FxMath.V2(0.5f, 0.5f), n);
        _vigSprite.hideFlags = HideFlags.HideAndDontSave;
        return _vigSprite;
    }

    // 当事者の真ん中と、いちばん離れた人までの広さ
    private static void Measure(Pool p, byte g, int n)
    {
        float sx = 0f, sy = 0f;
        int c = 0;
        for (int i = 0; i < n; i++)
        {
            var pc = GameData.Instance.GetPlayerById(Duel.MemberOf(g, i))?.Object;
            if (!pc) continue;
            var at = pc.GetTruePosition();
            P[c * 2] = at.x;
            P[c * 2 + 1] = at.y;
            sx += at.x;
            sy += at.y;
            c++;
        }
        if (c == 0) return;
        float cx = sx / c, cy = sy / c, far = 0f;
        for (int i = 0; i < c; i++)
        {
            float dx = P[i * 2] - cx, dy = P[i * 2 + 1] - cy;
            far = FxMath.Max(far, FxMath.Sqrt(dx * dx + dy * dy));
        }
        p.GoalX = cx;
        p.GoalY = cy;
        p.GoalR = FxMath.Clamp(far + Pad, MinR, MaxR);
        if (p.Placed) return;
        p.Placed = true;
        p.Cx = cx;
        p.Cy = cy;
        p.R = p.GoalR;
    }

    private static readonly float[] P = new float[Duel.MaxMembers * 2];

    private static void Draw(Pool p, float now)
    {
        float a = p.A * p.A * (3f - 2f * p.A);
        // 溜まりの上の端より奥 = 中の人は全員その手前に立つ
        float z = (p.Cy + p.R) / 1000f + 0.004f;
        p.CoreTf.position = FxMath.V3(p.Cx, p.Cy, z);
        float d = p.R * 2.2f;
        p.CoreTf.localScale = FxMath.V3(d, d * 0.8f, 1f);
        p.Core.color = FxMath.Rgba(0f, 0f, 0.02f, PoolA * a);
        for (int i = 0; i < Wisps; i++)
        {
            // 煙は溜まりの中を回り (2 つずつ逆向き)、伸び縮みしながら向きを変える
            float ph = p.Seed + i * 1.7f, dir = (i & 1) == 0 ? 1f : -1f;
            float ang = ph + now * 0.22f * dir;
            float orbit = p.R * (0.38f + 0.12f * FxMath.Sin(now * 0.5f + ph));
            float x = p.Cx + FxMath.Cos(ang) * orbit, y = p.Cy + FxMath.Sin(ang) * orbit * 0.75f;
            float s = p.R * 1.1f;
            float sx = s * (1f + 0.22f * FxMath.Sin(now * 0.7f + ph * 2f)), sy = s * 0.55f * (1f + 0.22f * FxMath.Cos(now * 0.6f + ph));
            var tf = p.WTf[i];
            tf.position = FxMath.V3(x, y, z - 0.0002f * (i + 1));
            tf.rotation = FxMath.RotZ(ang * FxMath.Rad2Deg * 0.6f + i * 40f);
            tf.localScale = FxMath.V3(sx, sy, 1f);
            float pulse = 0.75f + 0.25f * FxMath.Sin(now * 0.9f + ph * 3f);
            p.W[i].color = FxMath.Rgba(0.05f, 0.02f, 0.08f, WispA * a * pulse);
        }
    }

    private static Pool Make(ShipStatus ship, byte g)
    {
        var soft = Soft();
        if (!soft) return null;
        var go = new GameObject("MrpDuelGloom");
        go.transform.SetParent(ship.transform, true);
        go.SetActive(false);
        var p = new Pool { Go = go, Seed = g * 2.39f };
        p.Core = Renderer(go, soft, "Pool");
        p.CoreTf = p.Core.transform;
        for (int i = 0; i < Wisps; i++)
        {
            p.W[i] = Renderer(go, soft, "Wisp");
            p.WTf[i] = p.W[i].transform;
        }
        Made++;
        return p;
    }

    private static SpriteRenderer Renderer(GameObject parent, Sprite sprite, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = FxMath.Rgba(0f, 0f, 0f, 0f);
        return sr;
    }

    // 縁へ向けて滑らかに薄れる丸 (1 単位の大きさ)。1 回だけ作る
    private static Sprite Soft()
    {
        if (_soft) return _soft;
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float t = FxMath.Clamp01(1f - FxMath.Sqrt(dx * dx + dy * dy));
                px[y * n + x] = FxMath.Rgba32(255, 255, 255, (byte)(255f * t * t * (3f - 2f * t)));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.HideAndDontSave;
        _soft = Sprite.Create(tex, new Rect(0, 0, n, n), FxMath.V2(0.5f, 0.5f), n);
        _soft.hideFlags = HideFlags.HideAndDontSave;
        return _soft;
    }

    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder($"live={_live} made={Made} vig={_vigA:0.00}/{_vigGoal:0.00}");
        for (int g = 1; g < Pools.Length; g++)
        {
            var p = Pools[g];
            if (p != null && p.Go && p.A > 0f) sb.Append($" [g{g} a={p.A:0.00} c={p.Cx:0.0},{p.Cy:0.0} r={p.R:0.0}]");
        }
        return sb.ToString();
    }
}
