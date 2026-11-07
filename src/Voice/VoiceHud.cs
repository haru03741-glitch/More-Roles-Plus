using MoreRolesPlus.Fx;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Voice;

// マイクの印 (色で状態)・自分の声の大きさのバー・状態の一言と、今話している人の名前を出す。
// PC = 画面の左下 (バーと字は印の右・名前は上へ積む)。スマホ = 右上のボタンの並びの左隣 (左下は移動パッドがあり押し間違えるので)。
// バーと字は印の下に右揃え・名前は下へ積む
internal static class VoiceHud
{
    private const int Every = 5;
    private const float BarW = 1.5f, BarH = 0.13f;
    private static float Plate = 0.7f; // 丸の直径 = 右下のボタン (使う) の絵の高さ (ボタンの大きさの設定に合わせて作り直す)
    private const float FloorDb = -50f, TopDb = -10f; // バーの左端と右端の大きさ (dB)

    private static readonly Color PlateIdle = new(0f, 0f, 0f, 0.65f);
    private static readonly Color PlateTalk = new(0.18f, 0.7f, 0.3f, 0.9f);
    private static readonly Color Gray = new(0.6f, 0.6f, 0.6f, 1f);
    private static readonly Color Red = new(1f, 0.38f, 0.38f, 1f);
    private static readonly Color Green = new(0.49f, 1f, 0.49f, 1f);

    private static int _tick;
    private static readonly System.Collections.Generic.List<int> Slots = new();
    private static GameObject _root;
    private static SpriteRenderer _plate, _rim, _mic, _slash, _barBack, _barFill, _barMark;
    private static TextMeshPro _text;
    private static string _shown;
    private static float _shownLevel;
    private static Sprite _circle, _rimSprite, _micSprite, _slashSprite, _rect;
    private static UI.TopRowDock _dock;
    private static float _barLeft, _barY; // バーの左端と高さ (根から)
    private const float Glyph = 1.35f; // マイクの絵は丸に対してこの倍 (右下のボタンの絵と同じくらい丸いっぱいに)

    internal static void Tick(bool on)
    {
        var hud = Vanilla.Hud;
        if (!on || !hud)
        {
            if (_root) _root.SetActive(false);
            return;
        }
        if (!_root) Make(hud);
        if (!_text) return;
        if (!_root.activeSelf) _root.SetActive(true);
        _dock?.Update();
        Meter();
        if (++_tick % Every != 0) return;
        if (Mathf.Abs(ButtonSize(hud) - Plate) > 0.03f)
        {
            Object.Destroy(_root);
            _root = null;
            return;
        }
        Icon();
        string text = Line();
        if (text == _shown) return;
        _shown = text;
        _text.text = text;
    }

    // 声の大きさ: 上がる時はすぐ、下がる時はゆっくり
    private static void Meter()
    {
        bool open = Mic.Open;
        float target = open ? Fill(Mic.Level) : 0f;
        _shownLevel = target > _shownLevel ? target : Mathf.Max(target, _shownLevel - 0.04f);
        _barBack.enabled = _barMark.enabled = open;
        _barFill.enabled = open && _shownLevel > 0.001f;
        var t = _barFill.transform;
        t.localScale = new Vector3(BarW * _shownLevel, BarH, 1f);
        t.localPosition = new Vector3(_barLeft + BarW * _shownLevel * 0.5f, _barY, -0.01f);
        _barFill.color = Mic.Talking ? Green : new Color(0.85f, 0.85f, 0.85f, 1f);
    }

    private static float Fill(float rms) => rms <= 0f ? 0f : Mathf.Clamp01((20f * Mathf.Log10(rms) - FloorDb) / (TopDb - FloorDb));

    private static void Icon()
    {
        bool mic = Menu.ClientOptions.VoiceMic.Value && Mic.Available;
        bool muted = mic && VoiceChat.Muted;
        bool talk = mic && !muted && VoiceRules.CanTalk(PlayerControl.LocalPlayer);
        _mic.color = muted ? Red : talk ? Color.white : Gray;
        _slash.enabled = !mic || muted;
        _slash.color = muted ? Red : Gray;
        _plate.color = talk && Mic.Talking ? PlateTalk : PlateIdle;
        _rim.color = talk && Mic.Talking ? Green : muted ? Red : new Color(1f, 1f, 1f, 0.85f);
    }

    private static string Line()
    {
        string key = Menu.KeyHints.SlotName(Menu.Keybinds.Slot.VoiceMute);
        string me;
        if (!Menu.ClientOptions.VoiceMic.Value) me = $"<color=#9A9A9A>{new Text("VoiceChat.MicOff")}</color>";
#if ANDROID
        else if (!Mic.Available) me = $"<color=#9A9A9A>{new Text("VoiceChat.NoMicAndroid")}</color>";
#else
        else if (!Mic.Available) me = $"<color=#9A9A9A>{new Text("VoiceChat.NoMic")}</color>";
#endif
#if ANDROID
        else if (VoiceChat.Muted) me = $"<color=#FF6060>{new Text("VoiceChat.MutedTouch")}</color>";
#else
        else if (VoiceChat.Muted) me = $"<color=#FF6060>{new Text("VoiceChat.Muted").With("key", key)}</color>";
#endif
        else if (!VoiceRules.CanTalk(PlayerControl.LocalPlayer)) me = $"<color=#9A9A9A>{new Text("VoiceChat.Listening")}</color>";
        else if (VoiceChat.RadioOn) me = $"<color=#FF8A80>{new Text("VoiceChat.RadioOn").With("key", Menu.KeyHints.SlotName(Menu.Keybinds.Slot.TeamRadio))}</color>";
        else if (Mic.Talking) me = $"<color=#7CFF7C>{new Text("VoiceChat.Talking")}</color>";
#if ANDROID
        else me = new Text("VoiceChat.MicOnTouch").ToString(); // スマホはキーで押さない
#else
        else me = new Text("VoiceChat.MicOn").With("key", key).ToString();
#endif

        var sb = new System.Text.StringBuilder();
        bool radio = false;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.AmOwner || p.Data == null || !Speakers.Speaking(p.PlayerId)) continue;
            // 無線の声は誰の声か出さない
            if (Speakers.SpeakingRadio(p.PlayerId))
            {
                if (VoiceRules.RadioVolume(PlayerControl.LocalPlayer, p) > 0f) radio = true;
                continue;
            }
            if (VoiceRules.Volume(PlayerControl.LocalPlayer, p) <= 0f || !VoiceChat.ShowSpeaker.Value) continue;
            sb.Append("<color=#7CFF7C>♪</color> ").Append(p.Data.PlayerName).Append('\n');
        }
        if (radio) sb.Append("<color=#FF8A80>♪ ").Append(new Text("VoiceChat.RadioHeard").ToString()).Append("</color>\n");
#if ANDROID
        // 下へ積むので自分の状態を先に
        return sb.Length == 0 ? me : me + "\n" + sb.ToString(0, sb.Length - 1);
#else
        return sb.Append(me).ToString();
#endif
    }

    // 右下の「使う」の絵の高さ (HUD の中の大きさ)。測れない時は前の値
    private static float ButtonSize(HudManager hud)
    {
        // ボタンが隠れている時 (ロビー) も測れるように、絵そのものの大きさ × 倍率で見る
        var g = hud.UseButton ? hud.UseButton.graphic : null;
        if (!g || !g.sprite) return Plate;
        float h = g.sprite.bounds.size.y * Mathf.Abs(g.transform.lossyScale.y) / Mathf.Max(hud.transform.lossyScale.y, 0.0001f);
        return h > 0.2f && h < 3f ? h : Plate;
    }

    private static void Make(HudManager hud)
    {
        Plate = ButtonSize(hud);
        _root = new GameObject("MrpVoiceHud") { layer = hud.gameObject.layer };
        _root.transform.SetParent(hud.transform, false);
        var aspect = _root.AddComponent<AspectPosition>();
#if ANDROID
        // 初めの位置は並びの左隣のおおよそ。出てから並びを測って寄せる
        aspect.Alignment = AspectPosition.EdgeAlignments.RightTop;
        aspect.DistanceFromEdge = new Vector3(4f, 0.25f + Plate * 0.5f, -20f);
        _barLeft = Plate * 0.5f - BarW;
        _barY = -(Plate * 0.5f + 0.15f);
#else
        aspect.Alignment = AspectPosition.EdgeAlignments.LeftBottom;
        aspect.DistanceFromEdge = new Vector3(0.3f + Plate * 0.5f, 0.3f + Plate * 0.5f, -20f);
        _barLeft = Plate * 0.5f + 0.15f;
        _barY = -0.17f;
#endif
        aspect.AdjustPosition();

        _circle ??= FxSprites.Rect(128, 128, (x, y) => Mathf.Clamp01((0.5f - Mathf.Sqrt(x * x + y * y)) * 64f), "MrpVoicePlate");
        _micSprite ??= MicSprite();
        _slashSprite ??= FxSprites.Rect(128, 128, (x, y) => Edge(Segment(new Vector2(x, y), new Vector2(-0.33f, 0.33f), new Vector2(0.33f, -0.33f)) - 0.045f), "MrpVoiceSlash");
        _rect ??= FxSprites.Rect(4, 4, (x, y) => 1f, "MrpVoiceRect");

        _plate = Sprite("Plate", _circle, Vector3.zero, Vector3.one * Plate);
        // 印を押すとボイスチャットの窓
        var col = _plate.gameObject.AddComponent<CircleCollider2D>();
        col.radius = 0.5f;
        var pb = _plate.gameObject.AddComponent<PassiveButton>();
        pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        pb.OnMouseOver = new UnityEngine.Events.UnityEvent();
        pb.OnMouseOut = new UnityEngine.Events.UnityEvent();
        UI.UiActions.Release(Slots);
        pb.OnClick.AddListener(UI.UiActions.Take(VoicePanel.Toggle, Slots));
        // 縁: 暗い背景でも丸が見えるように
        _rimSprite ??= FxSprites.Rect(128, 128, (x, y) => Edge(Mathf.Abs(Mathf.Sqrt(x * x + y * y) - 0.47f) - 0.022f), "MrpVoiceRim");
        _rim = Sprite("Rim", _rimSprite, new Vector3(0f, 0f, -0.005f), Vector3.one * Plate);
        _mic = Sprite("Mic", _micSprite, new Vector3(0f, 0f, -0.01f), Vector3.one * (Plate * Glyph));
        _slash = Sprite("Slash", _slashSprite, new Vector3(0f, 0f, -0.02f), Vector3.one * (Plate * Glyph));
        _barBack = Sprite("BarBack", _rect, new Vector3(_barLeft + BarW * 0.5f, _barY, 0f), new Vector3(BarW, BarH, 1f));
        _barBack.color = new Color(0f, 0f, 0f, 0.6f);
        _barFill = Sprite("BarFill", _rect, Vector3.zero, Vector3.one);
        // 声とみなす大きさの印
        float mark = Fill(Mic.VoiceLevel);
        _barMark = Sprite("BarMark", _rect, new Vector3(_barLeft + BarW * mark, _barY, -0.02f), new Vector3(0.025f, BarH + 0.08f, 1f));
        _barMark.color = new Color(1f, 0.85f, 0.3f, 0.9f);
#if ANDROID
        _dock = new UI.TopRowDock(aspect, Plate * 0.5f);
#else
        _dock = null;
#endif

        var src = hud.TaskPanel ? hud.TaskPanel.GetComponentInChildren<TextMeshPro>(true) : null;
        if (!src) return;
        var go = Object.Instantiate(src.gameObject, _root.transform);
        go.name = "Text";
        var tr = go.GetComponent<TextTranslatorTMP>();
        if (tr) Object.Destroy(tr);
#if ANDROID
        go.transform.localPosition = new Vector3(Plate * 0.5f, _barY - BarH * 0.5f - 0.08f, 0f);
#else
        go.transform.localPosition = new Vector3(_barLeft, -0.05f, 0f);
#endif
        go.transform.localScale = Vector3.one;
        _text = go.GetComponent<TextMeshPro>();
        _text.enableWordWrapping = false;
        _text.fontSize = 1.6f;
#if ANDROID
        _text.alignment = TextAlignmentOptions.TopRight;
        _text.rectTransform.pivot = new Vector2(1f, 1f);
#else
        _text.alignment = TextAlignmentOptions.BottomLeft;
        _text.rectTransform.pivot = new Vector2(0f, 0f);
#endif
        _text.rectTransform.sizeDelta = new Vector2(6f, 2f);
        _text.color = Color.white;
        _shown = null;
        _shownLevel = 0f;
    }

    private static SpriteRenderer Sprite(string name, Sprite sprite, Vector3 pos, Vector3 scale)
    {
        var go = new GameObject(name) { layer = _root.layer };
        go.transform.SetParent(_root.transform, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = _root.transform.childCount; // 作った順に上へ重ねる
        return sr;
    }

    // マイクの印: 丸い頭・下半分の受け・軸・台
    private static Sprite MicSprite() => FxSprites.Rect(128, 128, (x, y) =>
    {
        var p = new Vector2(x, y);
        float d = Segment(p, new Vector2(0f, 0.04f), new Vector2(0f, 0.2f)) - 0.11f;
        float ring = Mathf.Abs((p - new Vector2(0f, 0.08f)).magnitude - 0.19f) - 0.028f;
        if (y > 0.08f) ring = Mathf.Min(Segment(p, new Vector2(-0.19f, 0.08f), new Vector2(-0.19f, 0.14f)), Segment(p, new Vector2(0.19f, 0.08f), new Vector2(0.19f, 0.14f))) - 0.028f;
        d = Mathf.Min(d, ring);
        d = Mathf.Min(d, Segment(p, new Vector2(0f, -0.11f), new Vector2(0f, -0.23f)) - 0.028f);
        d = Mathf.Min(d, Segment(p, new Vector2(-0.12f, -0.24f), new Vector2(0.12f, -0.24f)) - 0.028f);
        return Edge(d);
    }, "MrpVoiceMic");

    private static float Edge(float dist) => 1f - Mathf.Clamp01((dist + 0.008f) / 0.016f);

    private static float Segment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
        return (p - (a + ab * t)).magnitude;
    }
}
