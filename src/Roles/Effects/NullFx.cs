using AmongUs.Data;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// ヌルの段階が進んだ時の本人の画面 (他の人には何も出さない)。
// 第 2 段階へ: まわりの音が遠のいて耳鳴りが残り、視界の外側が少し暗くなってから戻る (人から離れて一人になる感じ)
// 条件達成: 低い持続音が膨らんで消え、外側の暗さが一度だけ寄って引く
internal static class NullFx
{
    private enum Kind : byte { None, Isolate, Achieve }

    // 第 2 段階
    private const float DuckIn = 0.8f;        // 音が絞りきるまで
    private const float DuckTo = 0.35f;       // 絞った時のまわりの音 (元の何倍)
    private const float DuckBack = 3.0f;      // 絞った所から戻りきるまで
    private const float IsoDark = 0.25f;      // 外側の暗さ
    // 条件達成
    private const float AchIn = 0.5f, AchBack = 2.1f, AchDark = 0.2f;

    private static Kind _kind;
    private static float _t, _baseSfx = 1f;
    private static int _gen, _sfxStep = -1;
    private static bool _ducked;
    private static GameObject _root;
    private static SpriteRenderer _edge;
    private static Sprite _edgeSprite;

    internal static string State => $"kind={_kind} t={_t:0.00} ducked={_ducked} root={(bool)_root} lastSfx={_lastSfx:0.###} base={_baseSfx:0.###}";
    private static float _lastSfx = -1f;

    internal static void Isolate() => Begin(Kind.Isolate);
    internal static void Achieve() => Begin(Kind.Achieve);

    private static void Begin(Kind kind)
    {
        var hud = Vanilla.Hud;
        if (!hud || !ShipStatus.Instance) return;
        Stop();
        _kind = kind;
        _t = 0f;
        _gen = GameClock.ShipGen;
        _root = FxRoot.NewHud("MrpNullFx", 5);
        var made = _root;
        Rewind.Undo(_ => { if (_root == made) Stop(); });
        _root.transform.localPosition = FxMath.V3(0f, 0f, -60f);
        var go = new GameObject("Edge") { layer = 5 };
        go.transform.SetParent(_root.transform, false);
        _edge = go.AddComponent<SpriteRenderer>();
        _edge.sprite = EdgeSprite();
        _edge.sortingOrder = 509;
        // HUD のカメラの縦の半分は 3。縁の絵を画面ちょうどに合わせる
        float aspect = Screen.height > 0 ? Screen.width / (float)Screen.height : 16f / 9f;
        var eb = _edge.sprite.bounds.size;
        go.transform.localScale = FxMath.V3(6.1f * aspect / eb.x, 6.1f / eb.y, 1f);
        _edge.color = FxMath.Rgba(1f, 1f, 1f, 0f);
        _baseSfx = DataManager.Settings != null && DataManager.Settings.Audio != null ? DataManager.Settings.Audio.SfxVolume : 1f;
        _ducked = kind == Kind.Isolate;
        // 鳴らす音は効果音の混ぜの外 (絞った音量に巻き込まれない)。音量は設定の効果音に合わせる
        var clip = PortSound.Get(kind == Kind.Isolate ? "null_isolate" : "null_achieve");
        if (clip && _baseSfx > 0f)
        {
            var src = _root.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.clip = clip;
            src.volume = 0.85f * _baseSfx;
            src.Play();
        }
    }

    internal static void Tick()
    {
        if (_kind == Kind.None) return;
        if (_gen != GameClock.ShipGen || !GameClock.ShipAlive || !_root || MeetingHud.Instance) { Stop(); return; }
        _t += Time.deltaTime;
        float t = _t;
        float inT = _kind == Kind.Isolate ? DuckIn : AchIn;
        float backT = _kind == Kind.Isolate ? DuckBack : AchBack;
        if (t >= inT + backT) { Stop(); return; }
        float k = t < inT ? Ease(t / inT) : 1f - Ease((t - inT) / backT);
        if (_edge) _edge.color = FxMath.Rgba(1f, 1f, 1f, k * (_kind == Kind.Isolate ? IsoDark : AchDark));
        if (!_ducked) return;
        // 音量の書き換えは 0.1 秒ごと。撃たれた本人の画面が音を絞っている間は触らない
        int step = (int)(t * 10f);
        if (step == _sfxStep || VictimView.Active) return;
        _sfxStep = step;
        SetSfx(_baseSfx * (1f - (1f - DuckTo) * k));
    }

    private static void SetSfx(float v)
    {
        var sm = SoundManager.Instance;
        if (sm) sm.ChangeSfxVolume(v);
        _lastSfx = v;
    }

    private static float Ease(float t)
    {
        t = FxMath.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    // 画面の外側ほど黒い (中央の 55% は素通し)。1 回だけ焼いて使い回す
    private static Sprite EdgeSprite()
    {
        if (_edgeSprite) return _edgeSprite;
        return _edgeSprite = FxSprites.RectRgb(160, 64, (float x, float y, out float r, out float g, out float b) =>
        {
            float lx = x * 2f, ly = y * 2f;
            float len = FxMath.Sqrt(lx * lx + ly * ly);
            float a = FxMath.Clamp01((len - 0.55f) / 0.7f);
            r = g = b = 0f;
            return a * a * (3f - 2f * a);
        }, "MrpNullEdge");
    }

    private static void Stop()
    {
        if (_kind == Kind.None) return;
        if (_ducked && !VictimView.Active) SetSfx(_baseSfx);
        _ducked = false;
        _sfxStep = -1;
        if (_root) Object.Destroy(_root);
        _root = null;
        _edge = null;
        _kind = Kind.None;
    }
}
