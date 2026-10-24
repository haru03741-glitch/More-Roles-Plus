using System;
using System.Collections.Generic;
using MoreRolesPlus.Roles;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace MoreRolesPlus.Menu;

// /r・/m の役職の説明の吹き出しで、節の行を押すとその節の詳しい文を開く / 閉じる (試合の情報のカードと同じ動き)。
// 吹き出しの背景に当たり判定を付け、押された所の <link> (節の鍵) を字から探して、説明を組み立て直して書き直す。
// 吹き出しは使い回されるので、押された時に字が自分の書いた物のままかを確かめてから触る
internal static class ChatRoleDoc
{
    private sealed class State
    {
        public Assignable Role;
        public string Prefix, Suffix, Shown; // 説明の前後 (役職名の行・アドオンの行) と、いま吹き出しに出している字
        public readonly HashSet<string> Open = new(StringComparer.Ordinal);
        public ChatBubble Bubble;
        public BoxCollider2D Box;
    }

    private static Assignable _pendingRole;
    private static string _pendingBody;
    private static readonly Dictionary<int, State> States = new();          // 吹き出しの InstanceID ごと
    private static readonly Dictionary<int, UnityAction> Clicks = new();     // 吹き出しごとに 1 回だけ作る (押すたびに足すと漏れる)

    // RoleDoc.Chat が組み立てた直後に呼ぶ。次にチャットへ出す文がこれを含んでいれば、その吹き出しを押せるようにする
    public static void Pending(Assignable a, string body)
    {
        _pendingRole = a;
        _pendingBody = body;
    }

    // チャットへ文を出した直後に呼ぶ
    public static void Attach(ChatController chat)
    {
        var a = _pendingRole;
        string body = _pendingBody;
        _pendingRole = null;
        _pendingBody = null;
        DropReused();
        if (a == null || body == null || !chat || !chat.chatBubblePool) return;
        var list = chat.chatBubblePool.activeChildren;
        if (list == null) return;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var b = list[i] ? list[i].TryCast<ChatBubble>() : null;
            if (!b || !b.TextArea) continue;
            string shown = b.TextArea.text ?? "";
            int at = shown.IndexOf(body, StringComparison.Ordinal);
            if (at < 0) continue;
            int id = b.GetInstanceID();
            // プールが新しく作った吹き出しには、出す前に絵の素材を付けられていない → 付けて書き直す
            if (ChatIcons.Apply(b.TextArea))
            {
                b.SetText(shown);
                b.AlignChildren();
                chat.AlignAllBubbles();
                shown = b.TextArea.text ?? "";
                at = shown.IndexOf(body, StringComparison.Ordinal);
                if (at < 0) return;
            }
            var st = new State { Role = a, Prefix = shown[..at], Suffix = shown[(at + body.Length)..], Shown = shown, Bubble = b };
            States[id] = st;
            AddButton(chat, st, id);
            return;
        }
    }

    // 別の発言に使い回された吹き出しは、押せる当たり判定を外して覚えている状態を捨てる
    private static void DropReused()
    {
        List<int> gone = null;
        foreach (var kv in States)
        {
            var st = kv.Value;
            if (st.Bubble && st.Bubble.TextArea && st.Bubble.TextArea.text == st.Shown) continue;
            if (st.Box) st.Box.enabled = false;
            (gone ??= new List<int>()).Add(kv.Key);
        }
        if (gone != null) foreach (int id in gone) States.Remove(id);
    }

    private static void AddButton(ChatController chat, State st, int id)
    {
        var bg = st.Bubble.Background;
        if (!bg) return;
        var go = bg.gameObject;
        st.Box = go.GetComponent<BoxCollider2D>() ?? go.AddComponent<BoxCollider2D>();
        st.Box.size = bg.size;
        var pb = go.GetComponent<PassiveButton>();
        if (!pb)
        {
            pb = go.AddComponent<PassiveButton>();
            pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            pb.OnMouseOver = new UnityEvent();
            pb.OnMouseOut = new UnityEvent();
            var s = chat.scroller;
            var mask = s ? (s.ClickMask ? s.ClickMask : s.GetComponent<Collider2D>()) : null;
            if (mask) pb.ClickMask = mask;
            if (!Clicks.TryGetValue(id, out var act)) Clicks[id] = act = (UnityAction)(() => Click(id));
            pb.OnClick.AddListener(act);
        }
        pb.enabled = true;
        st.Box.enabled = true;
    }

    private static void Click(int id)
    {
        if (!States.TryGetValue(id, out var st)) return;
        var b = st.Bubble;
        var t = b ? b.TextArea : null;
        // 吹き出しが別の発言に使い回された後は触らない
        if (!t || t.text != st.Shown)
        {
            States.Remove(id);
            if (st.Box) st.Box.enabled = false;
            return;
        }
        var cam = Camera.main;
        if (!cam) return;
        int li = TMP_TextUtilities.FindIntersectingLink(t, Input.mousePosition, cam);
        if (li < 0 || t.textInfo == null || li >= t.textInfo.linkCount) return;
        Toggle(st, t.textInfo.linkInfo[li].GetLinkID());
    }

    // 確かめ用 (bridge の chatdoc): いちばん新しい説明の吹き出しの節を、押したのと同じに開く / 閉じる
    internal static string Simulate(string key)
    {
        State last = null;
        foreach (var st in States.Values) last = st;
        if (last == null || !last.Bubble || !last.Bubble.TextArea || last.Bubble.TextArea.text != last.Shown) return "ERR chatdoc no bubble";
        Toggle(last, key);
        return $"OK chatdoc open=[{string.Join(",", last.Open)}] box={(last.Box ? last.Box.size.ToString() : "-")}";
    }

    private static void Toggle(State st, string key)
    {
        var b = st.Bubble;
        var t = b.TextArea;
        if (!st.Open.Remove(key)) st.Open.Add(key);

        var chat = Vanilla.Hud ? Vanilla.Hud.Chat : null;
        var inner = chat && chat.scroller ? chat.scroller.Inner : null;
        float y = inner ? inner.localPosition.y : 0f;
        st.Shown = st.Prefix + RoleDoc.Chat(st.Role, st.Open) + st.Suffix;
        ChatIcons.Apply(t);
        b.SetText(st.Shown); // 吹き出しの背景の大きさは本編の SetText が字に合わせて決める
        b.AlignChildren();
        st.Shown = t.text; // 本編が手を入れた後の字で、使い回されたかを見分ける
        if (chat) chat.AlignAllBubbles();
        if (st.Box && b.Background) st.Box.size = b.Background.size;
        // 並べ直しで一番下へ送られないよう、押した時の位置へ戻す
        if (inner)
        {
            var p = inner.localPosition;
            var r = chat.scroller.ContentYBounds;
            inner.localPosition = new Vector3(p.x, Math.Clamp(y, r.min, r.max), p.z);
        }
    }
}
