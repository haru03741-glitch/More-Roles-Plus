using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 何かをされた本人の画面にだけ出る体の感覚 (字で知らせる代わりに、見え方で伝える)。どれもこの端末の見た目だけ。
//   Dizzy   = 役職が入れ替わった: 視界が縁から狭まって暗くなり、揺れながらゆっくり戻る (めまい)
//   Carried = 担がれている間: 視界が少し狭く、運ぶ人の歩みに合わせて画面が揺れる
//   Pulled  = 潜伏したボイジャーに倒された: 潜伏の側の紫の暗がりが視界を一気に閉じ、ほどけていく
// 視界の暗がりはゲームのカメラの子 (HUD は暗くしない)。真ん中が抜けた暗い板を、写す範囲より大きく広げて置き、
// 縮めるほど抜けた所が狭くなる。誰も使っていない間は Unity に触らない
internal static class SenseFx
{
    // 板の大きさ (画面の何倍か): 抜けた所の縁がほぼ画面の外 = Wide・一番狭い = 各感覚の値
    private const float Wide = 6f;
    // めまい: 閉じる → 少し保つ → ほどける (秒)・一番狭い時の大きさ・濃さ・揺れ (画面の高さに対して)
    private const float DizzyIn = 0.45f, DizzyHold = 0.35f, DizzyOut = 1.3f, DizzyS = 1.7f, DizzyA = 0.92f, DizzySway = 0.05f;
    // 担がれている: 大きさ・濃さ・寄せる速さ (1/秒)・歩みの揺れの間隔 (秒)・長さ・強さ
    private const float CarryS = 2.7f, CarryA = 0.55f, CarryEase = 3f, StepGap = 0.32f, StepTime = 0.08f, StepAmp = 0.12f;
    // 引き込まれる: 閉じる → ほどける (秒)・一番狭い時の大きさ・濃さ
    private const float PullIn = 0.18f, PullOut = 1.4f, PullS = 1.4f, PullA = 0.9f;
    private static readonly Color Dark = new(0f, 0f, 0.02f, 1f);
    private static readonly Color Violet = new(0.3f, 0.04f, 0.48f, 1f);

    private static float _dizzyAt = -99f, _pullAt = -99f, _carry, _carryUntil = -99f, _nextStep;
    private static bool _carryMoving;
    private static GameObject _go;
    private static Transform _tf;
    private static SpriteRenderer _sr;
    private static Camera _cam;
    private static Sprite _sprite;
    internal static int Dizzies, Pulls;

    internal static void Dizzy()
    {
        if (!ShipStatus.Instance) return;
        _dizzyAt = Time.time;
        Dizzies++;
    }

    internal static void Pulled()
    {
        if (!ShipStatus.Instance) return;
        _pullAt = Time.time;
        Pulls++;
    }

    // 担がれている間は呼び続ける (moving = 運ぶ人が歩いている)。呼ばれなくなったら Tick が戻す
    internal static void Carried(bool moving)
    {
        _carryUntil = Time.time + 0.15f;
        _carryMoving = moving;
    }

    // 役職の入れ替えに巻き込まれた 2 人へ (ホストが呼ぶ・ホスト自身にも出す)
    internal static void HostSwapped(byte a, byte b)
    {
        SwapCall.Send((a, b));
        Swapped(a, b);
    }

    private static void Swapped(byte a, byte b)
    {
        var lp = PlayerControl.LocalPlayer;
        if (lp && (lp.PlayerId == a || lp.PlayerId == b)) Dizzy();
    }

    private static readonly Net.RemoteCall<(byte a, byte b)> SwapCall = new("Sense.Swapped", Net.Route.HostToAll,
        (w, m) => { w.Write(m.a); w.Write(m.b); }, r => (r.ReadByte(), r.ReadByte()),
        (_, m) => Swapped(m.a, m.b));

    // 毎フレーム (Plugin の Update から)
    internal static void Tick()
    {
        float now = Time.time;
        // 担がれている間は呼び直され続ける (途切れて少し経ったら戻す)
        float goal = now < _carryUntil ? 1f : 0f;
        bool dizzy = now - _dizzyAt < DizzyIn + DizzyHold + DizzyOut;
        bool pull = now - _pullAt < PullIn + PullOut;
        if (!dizzy && !pull && _carry <= 0f && goal <= 0f) return;
        // 船が無い (試合が終わった)・逆再生の間は畳む。感覚はどれも 2 秒ほどで切れるので、船が替わった時の持ち越しは無い
        if (!ShipStatus.Instance || RewindPlayback.Active)
        {
            _dizzyAt = _pullAt = _carryUntil = -99f;
            _carry = 0f;
            if (_go) _go.SetActive(false);
            return;
        }
        float dt = Time.deltaTime;
        _carry += (goal - _carry) * FxMath.Min(1f, CarryEase * dt);
        if (goal <= 0f && _carry < 0.01f) _carry = 0f;
        if (goal > 0f && _carryMoving && now >= _nextStep)
        {
            _nextStep = now + StepGap;
            var hud = Vanilla.Hud;
            if (hud && hud.PlayerCam) hud.PlayerCam.ShakeScreen(StepTime, StepAmp);
        }

        // 一番強い感覚を見せる (重なった時は濃い方)
        float a = 0f, s = Wide, sway = 0f;
        Color c = Dark;
        if (_carry > 0f)
        {
            a = CarryA * _carry;
            s = Wide + (CarryS - Wide) * _carry;
        }
        if (dizzy)
        {
            float t = now - _dizzyAt;
            float k = t < DizzyIn ? Smooth(t / DizzyIn) : t < DizzyIn + DizzyHold ? 1f : 1f - Smooth((t - DizzyIn - DizzyHold) / DizzyOut);
            if (DizzyA * k > a)
            {
                a = DizzyA * k;
                s = Wide + (DizzyS - Wide) * k;
                c = Dark;
                // ふらつき: ゆっくり大きく回る揺れ (狭いほど強い)
                sway = DizzySway * k;
            }
        }
        if (pull)
        {
            float t = now - _pullAt;
            float k = t < PullIn ? Smooth(t / PullIn) : 1f - Smooth((t - PullIn) / PullOut);
            if (PullA * k > a)
            {
                a = PullA * k;
                s = Wide + (PullS - Wide) * k;
                c = Violet;
                sway = 0f;
            }
        }
        if (a <= 0.002f)
        {
            if (_go && _go.activeSelf) _go.SetActive(false);
            return;
        }
        if ((!_go || !_cam) && !Make()) return;
        if (!_go.activeSelf) _go.SetActive(true);
        float h = _cam.orthographicSize * 2f, w = h * _cam.aspect;
        float ox = FxMath.Sin(now * 2.3f) * sway * h, oy = FxMath.Sin(now * 1.7f + 1f) * sway * h * 0.6f;
        _tf.localPosition = FxMath.V3(ox, oy, _cam.nearClipPlane + 0.45f);
        // 板は正方形の絵なので、長い辺に合わせる (短い辺は外へはみ出す)
        float side = (w > h ? w : h) * s;
        _tf.localScale = FxMath.V3(side, side, 1f);
        _sr.color = FxMath.Rgba(c.r, c.g, c.b, a);
    }

    private static float Smooth(float u)
    {
        u = FxMath.Clamp01(u);
        return u * u * (3f - 2f * u);
    }

    private static bool Make()
    {
        var cam = Camera.main;
        if (!cam) return false;
        if (_go) Object.Destroy(_go);
        _cam = cam;
        _go = FxRoot.NewOnCamera("MrpSenseVignette", cam);
        _tf = _go.transform;
        _sr = _go.AddComponent<SpriteRenderer>();
        _sr.sprite = Sprite();
        return true;
    }

    // 真ん中が抜けて縁へ向けて濃くなる板 (1 単位の正方形)。抜けた所 = 半径 0.12 まで・半径 0.45 から先は真っ暗
    private static Sprite Sprite()
    {
        if (_sprite) return _sprite;
        const int n = 128;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n - 0.5f, dy = (y + 0.5f) / n - 0.5f;
                float r = FxMath.Sqrt(dx * dx + dy * dy) * 2f;
                float t = Smooth((r - 0.12f) / 0.33f);
                px[y * n + x] = FxMath.Rgba32(255, 255, 255, (byte)(255f * t));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.HideAndDontSave;
        _sprite = UnityEngine.Sprite.Create(tex, new Rect(0, 0, n, n), FxMath.V2(0.5f, 0.5f), n);
        _sprite.hideFlags = HideFlags.HideAndDontSave;
        return _sprite;
    }

    internal static string Describe() =>
        $"dizzy={Time.time - _dizzyAt:0.0}s pull={Time.time - _pullAt:0.0}s carry={_carry:0.00} dizzies={Dizzies} pulls={Pulls} on={(_go && _go.activeSelf)}";
}
