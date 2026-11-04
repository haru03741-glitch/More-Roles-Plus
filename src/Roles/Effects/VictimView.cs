using AmongUs.Data;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 遠くから撃たれた / 吹き飛ばされた本人の画面。本編のキルの絵の代わりに、その場の画面のまま倒れる様子を見せる。
// Blast: 一瞬白く飛び、耳鳴りが残り、まわりの音が遠のいて暗くなる
// Shot : 撃たれた向きの縁から赤黒く染まり、画面が傾きながら下へ落ちて (倒れる目線) 暗くなる
// どちらも終わると暗さが引いて、ふつうの幽霊に戻る。近くでのキルは本編の絵のまま
internal static class VictimView
{
    private enum Kind : byte { None, Blast, Shot }

    // Blast
    private const float FlashHold = 0.06f, FlashFade = 0.45f;
    private const float BlastDark = 1.5f, BlastDarkA = 0.85f;
    private const float Muffle = 0.22f;        // 遠のいたまわりの音の大きさ (元の何倍)
    private const float MuffleBack = 3.2f;     // まわりの音が戻りきるまで
    // Shot
    private const float FallT = 0.7f;          // 倒れきるまで
    private const float FallTilt = 14f;        // 倒れた時の傾き (度)
    private const float FallDrop = 0.45f;      // 目線が下がる長さ (世界の単位)
    private const float ShotDark = 1.2f, ShotDarkA = 0.8f;
    private const float ShotMuffle = 0.4f, ShotMuffleBack = 2.4f;
    // 共通
    private const float Hold = 0.35f;          // 暗いまま止まる秒
    private const float Recover = 0.6f;        // 暗さが引く秒

    private static Kind _kind;
    private static float _t, _dirX, _dirY, _tilt;
    private static GameObject _root;
    private static SpriteRenderer _flash, _dark, _edge;
    private static AudioSource _ring;
    private static FollowerCamera _follow;
    private static Camera _cam;
    private static Vector2 _baseOffset;
    private static Quaternion _baseRot;
    private static float _baseSfx = 1f;     // 設定の効果音の音量 (戻す先)
    private static int _gen;
    private static Sprite _white;

    internal static bool Active => _kind != Kind.None;

    // 本編がキルの絵を出す直前 (倒された本人の端末)。true = こちらで見せるので本編の絵は出さない
    internal static int Calls, Taken; // 確かめ用: 本編がキルの絵を出そうとした回数・こちらで代わった回数

    internal static bool TryStart(NetworkedPlayerInfo killer, NetworkedPlayerInfo victim)
    {
        Calls++;
        var lp = PlayerControl.LocalPlayer;
        if (killer == null || victim == null || !lp || victim.PlayerId != lp.PlayerId || killer.PlayerId == victim.PlayerId) return false;
        var k = killer.Object;
        if (!k || !ShipStatus.Instance) return false;
        // 爆発した直後・乱射している間のキルだけ (全員に届いている状態で決める。近くでのふつうのキルは本編の絵)
        Kind kind = Impostor.RocketLauncher.RangedKill(k) ? Kind.Blast : Impostor.TriggerHappy.RangedKill(k) ? Kind.Shot : Kind.None;
        if (kind == Kind.None) return false;
        Vector2 a = k.GetTruePosition(), b = lp.GetTruePosition();
        float dx = a.x - b.x, dy = a.y - b.y;
        float d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d < 0.01f) { dx = 1f; dy = 0f; d = 1f; }
        Stop();
        Begin(kind, dx / d, dy / d);
        Taken++;
        return true;
    }

    // 確かめ用: 自分の画面で流す (blast = true で爆発・false で撃たれた)。dx, dy = 撃った人のいる向き
    internal static void Preview(bool blast, float dx, float dy)
    {
        float d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d < 0.01f) { dx = 1f; dy = 0f; d = 1f; }
        Stop();
        Begin(blast ? Kind.Blast : Kind.Shot, dx / d, dy / d);
    }

    private static void Begin(Kind kind, float dirX, float dirY)
    {
        var hud = Vanilla.Hud;
        if (!hud) return;
        _kind = kind;
        _t = 0f;
        _dirX = dirX;
        _dirY = dirY;
        _gen = GameClock.ShipGen;
        _root = FxRoot.NewHud("MrpVictimView", 5);
        var made = _root;
        Rewind.Undo(_ => { if (_root == made) Stop(); }); // 倒れる前へ戻ると (生き返る) 画面も戻す
        _root.transform.localPosition = FxMath.V3(0f, 0f, -60f);
        if (!_white) _white = FxSprites.Rect(4, 4, (_, _) => 1f, "MrpVictimWhite");
        _dark = Full("Dark", _white, 10);
        _dark.color = FxMath.Rgba(0f, 0f, 0f, 0f);
        if (kind == Kind.Blast)
        {
            _flash = Full("Flash", _white, 12);
            _flash.color = FxMath.Rgba(1f, 1f, 1f, 1f);
        }
        else
        {
            _edge = Full("Edge", EdgeSprite(dirX, dirY), 11);
            // 縁の絵は画面ちょうどに合わせる (縦の半分 3・横は画面の縦横比)
            float aspect = Screen.height > 0 ? Screen.width / (float)Screen.height : 16f / 9f;
            var eb = _edge.sprite.bounds.size;
            _edge.transform.localScale = FxMath.V3(6.1f * aspect / eb.x, 6.1f / eb.y, 1f);
            _edge.color = FxMath.Rgba(1f, 1f, 1f, 0f);
            // 倒れる向き: 撃たれた向きと反対の側へ傾く
            _tilt = dirX >= 0f ? FallTilt : -FallTilt;
        }
        _follow = hud.PlayerCam;
        _cam = _follow ? _follow.GetComponent<Camera>() : null;
        _baseOffset = _follow ? _follow.Offset : default;
        _baseRot = _cam ? _cam.transform.localRotation : Quaternion.identity;
        // まわりの音を遠のかせる = 本編の効果音の音量を一時的に下げる (耳鳴りはその音量の外で鳴らす)
        var sm = SoundManager.Instance;
        _baseSfx = DataManager.Settings != null && DataManager.Settings.Audio != null ? DataManager.Settings.Audio.SfxVolume : 1f;
        SetSfx(_baseSfx * (kind == Kind.Blast ? Muffle : ShotMuffle));
        var clip = PortSound.Get(kind == Kind.Blast ? "victim_blast" : "victim_shot");
        if (clip && sm && _baseSfx > 0f)
        {
            _ring = _root.AddComponent<AudioSource>();
            _ring.playOnAwake = false;
            _ring.spatialBlend = 0f;
            _ring.clip = clip;
            _ring.volume = 0.9f * _baseSfx;
            _ring.Play();
        }
        if (kind == Kind.Blast && _follow) _follow.ShakeScreen(0.18f, 0.5f);
    }

    private static SpriteRenderer Full(string name, Sprite sp, int order)
    {
        var go = new GameObject(name) { layer = 5 };
        go.transform.SetParent(_root.transform, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        sr.sortingOrder = 500 + order;
        // HUD のカメラの縦の半分は 3。どの縦横比でも覆う大きさ
        var b = sp.bounds.size;
        go.transform.localScale = FxMath.V3(20f / b.x, 8f / b.y, 1f);
        return sr;
    }

    // 撃たれた向きの縁ほど濃い赤黒 (外側の縁は黒に近い)。撃たれるたびに 1 回だけ焼く
    private static Sprite EdgeSprite(float dx, float dy)
    {
        return FxSprites.RectRgb(160, 64, (float x, float y, out float r, out float g, out float b) =>
        {
            // 画面の中心から縁までを 0〜1 (上下左右の縁の中ほどで 1・角で約 1.4)
            float lx = x * 2f, ly = y * 2f;
            float len = FxMath.Sqrt(lx * lx + ly * ly);
            // 撃たれた向きの側ほど濃い (画面の横は縦の 2.5 倍あるので向きもその比で見る)
            float side = FxMath.Clamp01((lx * dx * 2.5f + ly * dy) / FxMath.Max(FxMath.Sqrt(lx * lx * 6.25f + ly * ly), 0.001f) * 0.5f + 0.5f);
            float ring = FxMath.Clamp01((len - 0.5f) / 0.55f);
            float a = ring * FxMath.Sqrt(ring) * (0.3f + 0.7f * side * side) * 0.95f;
            float deep = FxMath.Clamp01((len - 0.95f) / 0.35f);
            r = 0.38f * (1f - deep) + 0.06f * deep;
            g = 0.02f;
            b = 0.02f;
            return a;
        }, "MrpVictimEdge");
    }

    internal static void Tick()
    {
        if (_kind == Kind.None) return;
        if (_gen != GameClock.ShipGen || !GameClock.ShipAlive || !_root || MeetingHud.Instance) { Stop(); return; }
        _t += Time.deltaTime;
        float t = _t;
        float darkEnd = _kind == Kind.Blast ? BlastDark : ShotDark;
        float darkA = _kind == Kind.Blast ? BlastDarkA : ShotDarkA;
        float end = darkEnd + Hold + Recover;
        if (t >= end) { Stop(); return; }
        // 暗くなって止まり、引く
        float dk = t < darkEnd ? Ease(t / darkEnd) : t < darkEnd + Hold ? 1f : 1f - Ease((t - darkEnd - Hold) / Recover);
        _dark.color = FxMath.Rgba(0f, 0f, 0f, darkA * dk);
        // まわりの音がゆっくり戻る
        float back = _kind == Kind.Blast ? MuffleBack : ShotMuffleBack;
        float m = _kind == Kind.Blast ? Muffle : ShotMuffle;
        // 音量の書き換えは 0.1 秒ごと (本編の混ぜの値を毎フレーム書かない)
        int step = (int)(t * 10f);
        if (step != _sfxStep) { _sfxStep = step; SetSfx(_baseSfx * (m + (1f - m) * FxMath.Clamp01(t / back))); }
        if (_kind == Kind.Blast)
        {
            if (_flash)
            {
                float f = t < FlashHold ? 1f : FxMath.Exp(-(t - FlashHold) / (FlashFade * 0.35f));
                if (t > FlashHold + FlashFade * 2f) f = 0f;
                _flash.color = FxMath.Rgba(1f, 1f, 0.97f, f);
            }
            return;
        }
        // 撃たれた縁はすぐ染まり、暗さが引く時に一緒に引く
        float e = FxMath.Clamp01(t / 0.12f) * (t < darkEnd + Hold ? 1f : 1f - Ease((t - darkEnd - Hold) / Recover));
        if (_edge) _edge.color = FxMath.Rgba(1f, 1f, 1f, e);
        // 倒れる目線: 膝が抜けるように加速して落ち、床で止まる。暗さが引く時に元へ戻す
        float fall = t < FallT ? (t / FallT) * (t / FallT) : t < darkEnd + Hold ? 1f : 1f - Ease((t - darkEnd - Hold) / Recover);
        if (_follow) _follow.Offset = FxMath.V2(_baseOffset.x, _baseOffset.y - FallDrop * fall);
        if (_cam) _cam.transform.localRotation = FxMath.RotZ(_tilt * fall);
    }

    private static int _sfxStep = -1;

    private static void SetSfx(float v)
    {
        var sm = SoundManager.Instance;
        if (sm) sm.ChangeSfxVolume(v);
    }

    private static float Ease(float t)
    {
        t = FxMath.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    private static void Stop()
    {
        if (_kind == Kind.None) return;
        if (_kind == Kind.Shot)
        {
            if (_follow) _follow.Offset = _baseOffset;
            if (_cam) _cam.transform.localRotation = _baseRot;
        }
        SetSfx(_baseSfx);
        _sfxStep = -1;
        // 撃たれるたびに焼いた縁の絵は残さない
        if (_edge && _edge.sprite)
        {
            var sp = _edge.sprite;
            if (sp.texture) Object.Destroy(sp.texture);
            Object.Destroy(sp);
        }
        if (_root) Object.Destroy(_root);
        _root = null;
        _flash = _dark = _edge = null;
        _ring = null;
        _follow = null;
        _cam = null;
        _kind = Kind.None;
    }
}

[HarmonyPatch(typeof(KillOverlay), nameof(KillOverlay.ShowKillAnimation), typeof(NetworkedPlayerInfo), typeof(NetworkedPlayerInfo))]
internal static class VictimViewPatch
{
    public static bool Prefix([HarmonyArgument(0)] NetworkedPlayerInfo killer, [HarmonyArgument(1)] NetworkedPlayerInfo victim)
    {
        try { return !VictimView.TryStart(killer, victim); }
        catch (System.Exception e)
        {
            Plugin.Logger.LogError($"victim view: {e}");
            return true;
        }
    }
}
