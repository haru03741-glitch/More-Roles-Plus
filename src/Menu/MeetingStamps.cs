using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Roles;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// 会議のスタンプ: 会議の画面の下段の「スタンプ」ボタンで 8 つの札 (怪しい・白・?・!・賛成・反対・待って・ごめん) を開いて選ぶと、
// 自分の名札の横に吹き出しが 2.5 秒出る。全員の画面に出て、字は受け手の言語で出す (送るのは札の番号だけ)。
// PC は B キーで開閉・開いている間は 1〜8 キーでも選べる。生きている人だけが出せる (幽霊の言葉を生きている人へ漏らさない)。
// 枠は本編のチャットの吹き出しの絵・字は会議の名札の字の写し
internal static class MeetingStamps
{
    internal const byte Count = 8;
    private const float ShowSeconds = 2.5f;
    private const float FadeSeconds = 0.25f;
    private const float PopSeconds = 0.12f;
    private const long CooldownMs = 1500;     // 出した人の次まで
    private const long MinGapMs = 1200;       // 受け手が同じ人から受け付ける間隔 (届く時刻の揺れの分だけ短く)
    private const float FontSize = 1.6f;
    private const float PadX = 0.2f, BubbleH = 0.36f, MinW = 0.42f;
    private const float ButtonScale = 0.55f;
    private const float MenuW = 0.62f;        // 札の見本の幅を揃える (字の長さで並びがばらつかないように)
    private const float ButtonZ = -2f;
    private static readonly Vector3 OnCard = new(1.0f, 0.28f, -3.5f);       // 名札の右上の縁に掛かる位置 (名前と投票の印に被らない)
    private const float MenuDx = 1.35f, MenuY0 = 1.0f, MenuDy = 0.62f;      // 札の並び (ボタンの単位・2 段 4 列・ボタンの上に右端を揃える)

    private static readonly string[] Keys =
        { "Stamp.Sus", "Stamp.Safe", "Stamp.Question", "Stamp.Exclaim", "Stamp.Agree", "Stamp.Disagree", "Stamp.Wait", "Stamp.Sorry" };

    private static readonly RemoteCall<byte> ShowCall = new("Stamp.Show", Route.Anyone,
        (w, kind) => w.Write(kind), r => r.ReadByte(), Receive, Delivery.Queued);

    private sealed class Bubble
    {
        public byte Id;
        public GameObject Go;
        public Transform Tf;
        public SpriteRenderer Bg;
        public TextMeshPro Text;
        public float Start;
        public bool Popped;
    }

    private static readonly List<Bubble> Shown = new();
    private static readonly long[] LastMs = new long[256];
    private static Lifespan _life;
    private static ModButton _main;
    private static GameObject _menu;
    private static SpriteRenderer _mainBg;
    private static long _nextMs;
    private static bool _dim;
    internal static int Dropped;

    private static bool Alive(PlayerControl p) => p && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected;

    private static void Receive(PlayerControl sender, byte kind)
    {
        long now = Environment.TickCount64;
        if (!sender || kind >= Count || !Alive(sender) || !MeetingHud.Instance || now - LastMs[sender.PlayerId] < MinGapMs)
        {
            Dropped++;
            return;
        }
        LastMs[sender.PlayerId] = now;
        Show(sender.PlayerId, kind);
    }

    // 自分がスタンプを出す (手元に出して全員へ送る)
    internal static bool PlayLocal(byte kind)
    {
        var lp = PlayerControl.LocalPlayer;
        long now = Environment.TickCount64;
        if (kind >= Count || !Alive(lp) || !MeetingHud.Instance || now < _nextMs) return false;
        if (!Show(lp.PlayerId, kind)) return false;
        _nextMs = now + CooldownMs;
        LastMs[lp.PlayerId] = now;
        ShowCall.Send(kind);
        return true;
    }

    // ── 名札の横の吹き出し ───────────────────────────────────────

    private static bool Show(byte id, byte kind)
    {
        var meeting = MeetingHud.Instance;
        if (!meeting || meeting.playerStates == null) return false;
        PlayerVoteArea card = null;
        foreach (var s in meeting.playerStates)
            if (s && s.PlayerId.Value == id) { card = s; break; }
        if (!card) return false;

        for (int i = Shown.Count - 1; i >= 0; i--)
            if (Shown[i].Id == id)
            {
                if (Shown[i].Go) UnityEngine.Object.Destroy(Shown[i].Go);
                Shown.RemoveAt(i);
            }

        var b = MakeBubble(card.transform, card.NameText, new Text(Keys[kind]).ToString());
        if (b == null) return false;
        b.Id = id;
        b.Start = Time.time;
        b.Tf.localPosition = OnCard;
        b.Tf.localScale = FxMath.V3(0.6f, 0.6f, 1f);
        Shown.Add(b);
        return true;
    }

    // 吹き出し 1 つ (枠 = チャットの吹き出しの絵を字の幅に合わせて伸ばす・字 = textProto の写し)
    private static Bubble MakeBubble(Transform parent, TextMeshPro textProto, string text, float minW = MinW)
    {
        var bubbleProto = BubbleProto();
        if (!bubbleProto || !textProto) return null;
        var go = new GameObject("MrpStamp") { layer = parent.gameObject.layer };
        go.transform.SetParent(parent, false);

        var bg = go.AddComponent<SpriteRenderer>();
        // 材質は写さない (チャットの吹き出しの材質はチャット欄の中だけに描くマスク付き)
        bg.sprite = bubbleProto.sprite;
        bg.drawMode = SpriteDrawMode.Sliced;
        bg.color = bubbleProto.color;

        var t = UnityEngine.Object.Instantiate(textProto, go.transform);
        t.name = "Text";
        t.transform.localPosition = FxMath.V3(0f, 0f, -0.01f);
        t.transform.localScale = FxMath.V3(1f, 1f, 1f);
        t.enableAutoSizing = false;
        // 名札の字は見る人の陣営で色が変わる (インポスター同士は赤) ので色は写さない
        t.color = Color.black;
        t.fontSize = FontSize;
        t.margin = Vector4.zero;
        t.alignment = TextAlignmentOptions.Center;
        t.enableWordWrapping = false;
        t.rectTransform.sizeDelta = new Vector2(3f, BubbleH);
        t.text = text;
        float w = Math.Max(minW, t.GetPreferredValues(text, 3f, 0f).x + PadX);
        bg.size = new Vector2(w, BubbleH);
        return new Bubble { Go = go, Tf = go.transform, Bg = bg, Text = t };
    }

    private static SpriteRenderer _bubbleProto;

    private static SpriteRenderer BubbleProto()
    {
        if (_bubbleProto) return _bubbleProto;
        var chat = Vanilla.Chat;
        var pool = chat ? chat.chatBubblePool : null;
        var bubble = pool && pool.Prefab ? pool.Prefab.TryCast<ChatBubble>() : null;
        return _bubbleProto = bubble ? bubble.Background : null;
    }

    // 毎フレーム: 吹き出しの出入り・PC のキー
    internal static void Tick()
    {
        if (Shown.Count > 0) Animate();
        if (_main == null) return;
        bool cool = Environment.TickCount64 < _nextMs;
        if (cool != _dim && _mainBg)
        {
            _dim = cool;
            _mainBg.color = cool ? new Color(0.55f, 0.55f, 0.55f, 1f) : Color.white;
        }
        if (Keybinds.Down(Keybinds.Slot.Emote))
        {
            var chat = Vanilla.Chat;
            if (!(chat && chat.IsOpenOrOpening)) SetMenu(!MenuOpen);
            return;
        }
        if (!MenuOpen) return;
        for (byte k = 0; k < Count; k++)
            if (Input.GetKeyDown(KeyCode.Alpha1 + k))
            {
                Choose(k);
                return;
            }
    }

    private static void Animate()
    {
        float now = Time.time;
        for (int i = Shown.Count - 1; i >= 0; i--)
        {
            var b = Shown[i];
            float t = now - b.Start;
            if (!b.Go || t >= ShowSeconds)
            {
                if (b.Go) UnityEngine.Object.Destroy(b.Go);
                Shown.RemoveAt(i);
                continue;
            }
            if (t < PopSeconds)
            {
                float s = 0.6f + 0.4f * (t / PopSeconds);
                b.Tf.localScale = FxMath.V3(s, s, 1f);
            }
            else if (!b.Popped)
            {
                b.Popped = true;
                b.Tf.localScale = FxMath.V3(1f, 1f, 1f);
            }
            if (t > ShowSeconds - FadeSeconds)
            {
                float a = FxMath.Clamp01((ShowSeconds - t) / FadeSeconds);
                b.Bg.color = FxMath.Rgba(1f, 1f, 1f, a);
                b.Text.alpha = a;
            }
        }
    }

    // ── ボタンと札の並び ─────────────────────────────────────────

    internal static void OnMeetingStart(MeetingHud meeting)
    {
        Clear();
        if (!meeting || !Alive(PlayerControl.LocalPlayer)) return;
        var parent = meeting.ButtonParent;
        var proto = meeting.playerStates != null && meeting.playerStates.Length > 0 ? meeting.playerStates[0] : null;
        if (!parent || !proto) return;

        _life = RoleState.Match.Child();
        _main = ModButton.Create(_life, null, new Text("Stamp.Button"), 0f, () =>
        {
            SetMenu(!MenuOpen);
            return false;
        }, parent);
        if (_main == null || !_main.Transform)
        {
            Clear();
            return;
        }
        // 会議の間は HUD が隠れているので、ボタンの出し入れは会議の画面に任せる
        var tf = _main.Transform;
        tf.gameObject.SetActive(true);
        ToCorner(tf);
        tf.localScale = FxMath.V3(ButtonScale, ButtonScale, 1f);
        _mainBg = Icon(_main, proto.NameText, "!");

        _menu = new GameObject("MrpStampMenu") { layer = tf.gameObject.layer };
        _menu.transform.SetParent(tf, false);
        _menu.SetActive(false);
        for (byte k = 0; k < Count; k++)
        {
            byte kind = k;
            var b = ModButton.Create(_life, null, "", 0f, () => Choose(kind), _menu.transform);
            if (b == null || !b.Transform) continue;
            b.Transform.gameObject.SetActive(true);
            b.Transform.localPosition = FxMath.V3((kind % 4 - 3) * MenuDx, MenuY0 + (kind / 4) * MenuDy, -0.5f);
            Icon(b, proto.NameText, new Text(Keys[kind]).ToString());
        }
    }

    // ボタンの絵を吹き出し (札の見本) に替える
    private static SpriteRenderer Icon(ModButton b, TextMeshPro textProto, string text)
    {
        var g = b.Graphic;
        if (!g) return null;
        g.enabled = false;
        var bubble = MakeBubble(g.transform, textProto, text, MenuW);
        if (bubble == null) return null;
        bubble.Tf.localPosition = FxMath.V3(0f, 0f, -0.05f);
        bubble.Tf.localScale = FxMath.V3(2.4f, 2.4f, 1f);
        return bubble.Bg;
    }

    private static bool MenuOpen => _menu && _menu.activeSelf;

    private static void SetMenu(bool open)
    {
        if (!_menu) return;
        if (open && Environment.TickCount64 < _nextMs) return;
        if (open && _main != null && _main.Transform) ToCorner(_main.Transform);
        _menu.SetActive(open);
    }

    // ボタンは画面の右下の角 (試合中にキルボタン等が並ぶ所) に置く。HUD は隠れていても並びの場所は残っている。
    // 角の位置は画面の真ん中 (HUD の根) からのずれで測り、会議のボタンの置き場の原点から同じだけずらす
    // (会議の画面は横から滑り込むので世界座標では合わせない)。縦横比は会議の間に変わりうるので開く時にも測り直す
    private static void ToCorner(Transform tf)
    {
        var hud = Vanilla.Hud;
        if (!hud || !hud.AbilityButton) return;
        var corner = hud.AbilityButton.transform.parent.position;
        var center = hud.transform.position;
        float s = tf.parent ? tf.parent.lossyScale.x : 1f;
        tf.localPosition = FxMath.V3((corner.x - center.x) / s, (corner.y - center.y) / s, ButtonZ);
    }

    private static bool Choose(byte kind)
    {
        SetMenu(false);
        PlayLocal(kind);
        return false;
    }

    internal static void Clear()
    {
        _life?.Release();
        _life = null;
        _main = null;
        _menu = null;
        _mainBg = null;
        _dim = false;
        foreach (var b in Shown)
            if (b.Go) UnityEngine.Object.Destroy(b.Go);
        Shown.Clear();
    }

    // 確認用: dev `stamp`
    internal static string Command(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string head = parts.Length > 0 ? parts[0] : "stats";
        switch (head)
        {
            case "menu":
                SetMenu(!MenuOpen);
                break;
            case "pick" when parts.Length > 1 && byte.TryParse(parts[1], out byte pk):
                if (!MenuOpen) SetMenu(true);
                if (!_menu || pk >= _menu.transform.childCount) return $"ERR no menu {Stats()}";
                var pb = _menu.transform.GetChild(pk).GetComponent<PassiveButton>();
                if (pb) pb.OnClick.Invoke();
                break;
            case "stats":
                break;
            default:
                if (!byte.TryParse(head, out byte kind)) return "ERR stamp [menu | pick <0-7> | stats | <0-7> [番号]]";
                if (parts.Length > 1 && byte.TryParse(parts[1], out byte pid))
                {
                    // 他人 (ダミー) が送ってきたのと同じ受け手の道を通す
                    var p = GameData.Instance ? GameData.Instance.GetPlayerById(pid)?.Object : null;
                    if (!p) return $"ERR no player {pid}";
                    Receive(p, kind);
                }
                else if (!PlayLocal(kind)) return $"ERR stamp refused {Stats()}";
                break;
        }
        return $"OK stamp {Stats()}";
    }

    private static string Stats() =>
        $"shown={Shown.Count} dropped={Dropped} sent={ShowCall.Sent} recv={ShowCall.Received} button={(_main != null && _main.Transform ? "on" : "none")} menu={(MenuOpen ? "open" : "closed")} cool={Math.Max(0, _nextMs - Environment.TickCount64)}ms bubble={(BubbleProto() ? "ok" : "none")}";
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
internal static class StampMeetingStartPatch
{
    public static void Postfix(MeetingHud __instance) => MeetingStamps.OnMeetingStart(__instance);
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Close))]
internal static class StampMeetingClosePatch
{
    public static void Postfix() => MeetingStamps.Clear();
}
