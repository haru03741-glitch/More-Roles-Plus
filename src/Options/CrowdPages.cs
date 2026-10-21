// Based on https://github.com/CrowdedMods/CrowdedMod src/CrowdedMod/Patches (MIT, Copyright (c) 2020-2022 andry08 & CrowdedMods; licenses/CrowdedMod-MIT.txt),
// by way of https://github.com/Gurge44/EndlessHostRoles Patches/Crowded.cs (GPL-3.0)
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace MoreRolesPlus.Options;

// 15 人を超える時の、会議の名札・変身の相手選び・バイタルのページ送り (本編と同じ 3 列 × 5 行を 1 ページに)。
// 送るのは矢印キー・ホイール・画面の矢印のボタン。端まで行くと反対の端へ回る。
// 並べ直すのはページが変わった時と、会議中に人が死んだ時だけ。
[HarmonyPatch]
internal static class CrowdPages
{
    private const int PerPage = 15, Cols = 3;
    private const int CheckFrames = 15; // バイタルの通信妨害・会議中の死を見る間隔

    private enum Kind { None, Meeting, Shapeshift, Vitals, Text }

    private static Kind _kind;
    private static MonoBehaviour _owner; // 開いている画面。閉じると Unity の null になる
    private static int _page, _pages, _frame, _dead;
    private static MeetingHud _meeting;
    private static VitalsMinigame _vitals;
    private static bool _sab;
    private static Vector3 _origin;
    private static Vector2 _step;
    private static readonly List<Transform> Items = new();
    private static readonly List<Transform> Dead = new();
    private static TextMeshPro _label;
    private static UnityAction _prev, _next;
    private static System.Action<int> _show; // 文のページ送りの中身を差し替える

    internal static int Page => _page;
    internal static int Pages => _pages;

    // ── 開く ──

    [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start)), HarmonyPostfix, HarmonyPriority(Priority.Last)]
    public static void MeetingStart(MeetingHud __instance) => OpenMeeting(__instance);

    private static void OpenMeeting(MeetingHud m)
    {
        var states = m.playerStates;
        if (states == null || states.Length <= PerPage) return;
        // 生きている人を先に (本編と同じ順)
        Items.Clear();
        Dead.Clear();
        foreach (var s in states)
        {
            if (!s) continue;
            (s.AmDead ? Dead : Items).Add(s.transform);
        }
        Items.AddRange(Dead);
        _meeting = m;
        _dead = Dead.Count;
        Open(Kind.Meeting, m, m.VoteOrigin, m.VoteButtonOffsets, m.TimerText, new Vector3(0.3f, -2.2f, -10f)); // 下の真ん中 (左はスキップ・右は残り時間)
    }

    [HarmonyPatch(typeof(ShapeshifterMinigame), nameof(ShapeshifterMinigame.Begin)), HarmonyPostfix]
    public static void ShapeshiftBegin(ShapeshifterMinigame __instance)
    {
        var list = __instance.potentialVictims;
        if (list == null || list.Count <= PerPage) return;
        Items.Clear();
        foreach (var p in list)
            if (p) Items.Add(p.transform);
        Open(Kind.Shapeshift, __instance, new Vector3(__instance.XStart, __instance.YStart, 0f),
            new Vector2(__instance.XOffset, __instance.YOffset), Proto(), new Vector3(3.6f, -2.36f, -1f));
    }

    [HarmonyPatch(typeof(VitalsMinigame), nameof(VitalsMinigame.Begin)), HarmonyPostfix]
    public static void VitalsBegin(VitalsMinigame __instance)
    {
        var vitals = __instance.vitals;
        if (vitals == null || vitals.Length <= PerPage) return;
        Items.Clear();
        foreach (var p in vitals)
            if (p) Items.Add(p.transform);
        _vitals = __instance;
        _sab = Sabotaged(__instance);
        Open(Kind.Vitals, __instance, new Vector3(__instance.XStart, __instance.YStart, 0f),
            new Vector2(__instance.XOffset, __instance.YOffset), Proto(), new Vector3(1.6f, -2f, -1f)); // 真ん中の電池と右下の飾りの文字の間
    }

    // 文の一覧のページ送り (終わりの画面の結果など)。show がページの番号を受けて中身を差し替える
    internal static void OpenText(MonoBehaviour owner, int pages, System.Action<int> show, TextMeshPro proto, Vector3 uiPos)
    {
        Items.Clear();
        _meeting = null;
        _vitals = null;
        _kind = Kind.Text;
        _owner = owner;
        _show = show;
        _pages = pages;
        _page = 0;
        BuildUi(owner.transform, proto, uiPos);
        Layout();
    }

    private static TextMeshPro Proto()
    {
        var hud = Vanilla.Hud;
        return hud && hud.KillButton ? hud.KillButton.cooldownTimerText : null;
    }

    private static void Open(Kind kind, MonoBehaviour owner, Vector3 origin, Vector2 step, TextMeshPro proto, Vector3 uiPos)
    {
        bool same = _kind == kind && _owner == owner;
        _kind = kind;
        _owner = owner;
        _origin = origin;
        _step = step;
        _pages = (Items.Count + PerPage - 1) / PerPage;
        if (!same) _page = 0;
        if (_page >= _pages) _page = _pages - 1;
        if (!same || !_label) BuildUi(owner.transform, proto, uiPos);
        Layout();
        Plugin.Logger.LogInfo($"[pages] {kind} items={Items.Count} pages={_pages}");
    }

    // ── 送る ──

    // Update (毎フレーム)。開いていない時は 1 回の比較だけ
    public static void Tick()
    {
        if (_kind == Kind.None) return;
        if (!_owner) { Close(); return; }
        if (++_frame >= CheckFrames)
        {
            _frame = 0;
            if (_kind == Kind.Vitals)
            {
                // 本編は通信妨害が直ると全員の板を出し直すので、ページの形に戻す
                bool sab = Sabotaged(_vitals);
                if (sab != _sab) { _sab = sab; Layout(); }
            }
            else if (_kind == Kind.Meeting && DeadCount(_meeting) != _dead)
                OpenMeeting(_meeting); // 会議中に人が死んだ。生きている人を先にして並べ直す
        }
        int d = 0;
        if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.PageDown)) d = 1;
        else if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.PageUp)) d = -1;
        else
        {
            float w = Input.mouseScrollDelta.y;
            if (w < 0f) d = 1;
            else if (w > 0f) d = -1;
        }
        if (d == 0) return;
        var chat = Vanilla.Chat;
        if (chat && chat.IsOpenOrOpening) return;
        Turn(d);
    }

    internal static void Turn(int d)
    {
        if (_kind == Kind.None || _pages <= 1) return;
        _page = ((_page + d) % _pages + _pages) % _pages;
        Layout();
    }

    private static void Close()
    {
        _kind = Kind.None;
        _owner = null;
        _meeting = null;
        _vitals = null;
        _show = null;
        _label = null; // 画面の子なので画面と一緒に消えている
        Items.Clear();
    }

    private static void Layout()
    {
        if (_kind == Kind.Text) _show?.Invoke(_page);
        bool hide = _kind == Kind.Vitals && _sab; // 通信妨害中のバイタルは本編が全部隠す
        for (int i = 0; i < Items.Count; i++)
        {
            var t = Items[i];
            if (!t) continue;
            bool on = i / PerPage == _page;
            if (on)
            {
                int r = i % PerPage;
                var p = t.localPosition;
                t.localPosition = new Vector3(_origin.x + _step.x * (r % Cols), _origin.y + _step.y * (r / Cols), p.z);
            }
            if (!hide) t.gameObject.SetActive(on);
        }
        if (_label) _label.text = $"{_page + 1}/{_pages}";
    }

    private static int DeadCount(MeetingHud m)
    {
        int n = 0;
        foreach (var st in m.playerStates)
            if (st && st.AmDead) n++;
        return n;
    }

    private static bool Sabotaged(VitalsMinigame v) => v.SabText && v.SabText.isActiveAndEnabled;

    // ── 画面の矢印とページの数字 ──

    private static void BuildUi(Transform parent, TextMeshPro proto, Vector3 pos)
    {
        var root = new GameObject("MrpPages") { layer = parent.gameObject.layer };
        root.transform.SetParent(parent, false);
        root.transform.localPosition = pos;
        _label = Text(root.transform, proto, Vector3.zero, 1.6f);
        _prev ??= (UnityAction)(() => Turn(-1));
        _next ??= (UnityAction)(() => Turn(1));
        Arrow(root.transform, proto, new Vector3(-0.75f, 0f, 0f), "<", _prev);
        Arrow(root.transform, proto, new Vector3(0.75f, 0f, 0f), ">", _next);
    }

    private static void Arrow(Transform parent, TextMeshPro proto, Vector3 pos, string glyph, UnityAction act)
    {
        var bg = MenuArt.Panel(parent, "MrpPage" + glyph, 0.08f, new Vector2(0.5f, 0.5f), pos, new Color(0.15f, 0.15f, 0.2f, 0.95f), null);
        bg.sortingOrder = 500;
        var col = bg.gameObject.AddComponent<BoxCollider2D>();
        col.size = new Vector2(0.5f, 0.5f);
        var pb = bg.gameObject.AddComponent<PassiveButton>();
        pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOut = new UnityEvent();
        pb.OnClick.AddListener(act);
        var t = Text(bg.transform, proto, new Vector3(0f, 0f, -0.1f), 1.8f);
        if (t) t.text = glyph;
    }

    private static TextMeshPro Text(Transform parent, TextMeshPro proto, Vector3 pos, float size)
    {
        if (!proto) return null;
        var t = Object.Instantiate(proto, parent);
        t.name = "MrpPageText";
        var tr = t.GetComponent<TextTranslatorTMP>(); // 元の文の翻訳が書き戻さないように
        if (tr) Object.Destroy(tr);
        t.gameObject.SetActive(true);
        t.transform.localPosition = pos;
        t.transform.localScale = Vector3.one;
        t.enableWordWrapping = false;
        t.enableAutoSizing = false;
        t.rectTransform.pivot = new Vector2(0.5f, 0.5f); // 元の文の枠の基準点が真ん中とは限らない (終わりの画面の文は上寄り)
        t.alignment = TextAlignmentOptions.Center;
        t.fontSize = size;
        t.color = Color.white;
        t.sortingOrder = 501;
        return t;
    }
}
