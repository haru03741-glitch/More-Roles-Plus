// Building role buttons out of the meeting's nameplate parts based on https://github.com/TheOtherRolesAU/TheOtherRoles
// (GuesserGM / Guesser meeting UI, GPL-3.0) via https://github.com/ykundesu/SuperNewRoles (GPL-3.0).
// The small phone over the meeting (a copy of the meeting's PhoneUI), team tabs on top, 5 columns, pick then press again
// to confirm, and the exit button from the nameplate's cancel button are based on
// https://github.com/Gurge44/EndlessHostRoles Modules/GuessManager.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using MoreRolesPlus.Options;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 役職を選ぶ画面 (会議中だけ)。会議の名札を隠し、名札の部品を写したボタンで、この試合に出うる役職を並べる。
// 上の段 = 陣営のタブ・検索欄・閉じる。中 = 役職 (1 回目で選び、もう一度押すと決まる)。下の段 = ページ送りと案内。
// 検索欄に字があれば、陣営をまたいで名前の一致した役職だけを出す
public static class RolePicker
{
    private const int Cols = 5, Rows = 7, PerPage = Cols * Rows;
    private const float GridX = -3.47f, GridDx = 1.735f, GridTop = 1.42f, GridDy = 0.47f;
    private const float TopY = 1.95f, BottomY = -1.98f, TabX = -3.05f, TabDx = 1.42f;
    private const float PhoneScale = 0.75f; // 会議の電話より一回り小さい端末にする

    private static readonly Color CrewColor = new Color32(140, 255, 255, 255);
    private static readonly Color ImpColor = new Color32(255, 25, 25, 255);
    private static readonly Color NeutralColor = new Color32(255, 171, 27, 255);
    private static readonly Color PlateOn = new(1f, 0.92f, 0.55f, 1f);

    private static GameObject _phone, _root, _body;
    private static SearchBox _search;
    private static readonly List<PassiveButton> _buttons = new();
    private static Action<RoleBase> _onPick;
    private static Team _team;
    private static int _page;
    private static string[] _tokens = Array.Empty<string>();
    private static RoleBase _selected;
    private static readonly List<GameObject> Hidden = new();
    private static readonly Dictionary<RoleBase, string> Hay = new();

    public static bool IsOpen => _root;
    internal static bool OpenFlag; // 毎フレームの確かめ用 (Unity の物に触らずに読める)
    internal static bool SkipHidden; // 開いている間に本編が出し直した「投票スキップ」を隠した (閉じたら戻す)

    public static bool Open(Action<RoleBase> onPick)
    {
        var hud = MeetingHud.Instance;
        if (!hud || IsOpen || hud.playerStates == null || hud.playerStates.Length == 0) return false;
        _onPick = onPick;
        SkipHidden = false;
        _team = Team.Crew;
        _page = 0;
        _tokens = Array.Empty<string>();
        _selected = null;
        Hidden.Clear();
        foreach (var pva in hud.playerStates)
            if (pva && pva.gameObject.activeSelf) { pva.gameObject.SetActive(false); Hidden.Add(pva.gameObject); }
        if (hud.SkipVoteButton && hud.SkipVoteButton.gameObject.activeSelf) { hud.SkipVoteButton.gameObject.SetActive(false); Hidden.Add(hud.SkipVoteButton.gameObject); }
        // 会議の題 (「インポスターは誰だ」) は上の段と重なるので隠す
        if (hud.TitleText && hud.TitleText.gameObject.activeSelf) { hud.TitleText.gameObject.SetActive(false); Hidden.Add(hud.TitleText.gameObject); }

        // 会議の電話の枠 (本体・ガラス・電池と電波) を写して小さくし、会議の上にもう 1 台の端末として出す。
        // 中身はその端末の画面 (本体より手前・ガラスより奥) に並べる
        var phoneSrc = hud.meetingContents ? hud.meetingContents.transform.Find("PhoneUI") : null;
        if (phoneSrc)
        {
            _phone = Object.Instantiate(phoneSrc.gameObject, hud.transform);
            _phone.name = "MrpRolePickerPhone";
            _phone.SetActive(true);
            _phone.transform.localPosition = new Vector3(0f, 0f, -50f);
            _phone.transform.localScale = new Vector3(PhoneScale, PhoneScale, 1f);
        }
        _root = new GameObject("MrpRolePicker");
        OpenFlag = true;
        _root.transform.SetParent(_phone ? _phone.transform : hud.transform, false);
        _root.transform.localPosition = new Vector3(0f, 0f, _phone ? -1f : -5f);
        // 検索欄は開いた時に 1 回だけ作る (打つたびに作り直すと入力が切れる)
        _search = SearchBox.Create(_root.transform, new Vector3(1.75f, TopY, -1f), 2.3f, 0.36f, new Text("RolePicker.SearchHint"), OnSearch);
        CloseButton(new Vector2(3.3f, TopY)); // 右上の角は端末の電池のアイコンがあるので空ける
        Build();
        return true;
    }

    public static void Close()
    {
        DestroyRoot();
        foreach (var go in Hidden) if (go) go.SetActive(true);
        Hidden.Clear();
        Hay.Clear();
        var hud = MeetingHud.Instance;
        if (SkipHidden && hud && hud.SkipVoteButton) hud.SkipVoteButton.gameObject.SetActive(true);
        SkipHidden = false;
    }

    // 作ったボタンの管理側の受け手を外してから消す (外さないと Unity とまたいだ参照が残って解放されない)
    private static void DestroyRoot()
    {
        foreach (var pb in _buttons) if (pb) pb.OnClick.RemoveAllListeners();
        _buttons.Clear();
        if (_search != null) SearchBox.Forget(_search);
        _search = null;
        if (_phone) Object.Destroy(_phone);
        else if (_root) Object.Destroy(_root);
        _phone = _root = _body = null;
        OpenFlag = false;
    }

    // この試合に出うる役職 (出現率が 0 でない物・誰かに作られる物・本編の役職)
    private static bool InPlay(RoleBase r) => r.IsVanilla || r.MadeByOthers || (r.Chance != null && r.Chance.Value > 0);

    private static List<RoleBase> Candidates()
    {
        var list = new List<RoleBase>();
        bool searching = _tokens.Length > 0;
        foreach (var r in Registry.Roles)
        {
            if (!InPlay(r)) continue;
            if (searching ? !Matches(r) : r.Team != _team) continue;
            list.Add(r);
        }
        return list;
    }

    // 語がすべて名前に含まれる時だけ当たる (日本語名・英語名・かなの揺れはそろえて比べる)
    private static bool Matches(RoleBase r)
    {
        // 名前と読み (漢字の名前は読みでも当たる・設定画面の検索と同じ)
        if (!Hay.TryGetValue(r, out var hay)) Hay[r] = hay = RoleSearch.Haystack(r.Name) + "\n" + RoleSearch.Normalize(r.Reading);
        foreach (var t in _tokens)
            if (!hay.Contains(t, StringComparison.Ordinal)) return false;
        return true;
    }

    private static void OnSearch(string q)
    {
        var tokens = RoleSearch.Tokens(q);
        if (string.Join("\n", tokens) == string.Join("\n", _tokens)) return;
        _tokens = tokens;
        _page = 0;
        Build();
    }

    // 下の段とタブと役職だけを作り直す (検索欄と閉じるはそのまま)
    private static void Build()
    {
        var hud = MeetingHud.Instance;
        if (!hud || !_root) { Close(); return; }
        if (_body)
        {
            foreach (var pb in _body.GetComponentsInChildren<PassiveButton>(true))
                if (pb) { pb.OnClick.RemoveAllListeners(); _buttons.Remove(pb); }
            _body.transform.SetParent(null);
            Object.Destroy(_body);
        }
        _body = new GameObject("MrpRolePickerBody");
        _body.transform.SetParent(_root.transform, false);

        bool searching = _tokens.Length > 0;
        Tab(Team.Crew, new Text("RolePicker.Crew"), CrewColor, 0, searching);
        Tab(Team.Impostor, new Text("RolePicker.Impostor"), ImpColor, 1, searching);
        Tab(Team.Neutral, new Text("RolePicker.Neutral"), NeutralColor, 2, searching);

        var all = Candidates();
        int pages = Math.Max(1, (all.Count + PerPage - 1) / PerPage);
        if (_page >= pages) _page = pages - 1;
        for (int i = 0; i < PerPage; i++)
        {
            int k = _page * PerPage + i;
            if (k >= all.Count) break;
            var r = all[k];
            var plate = Button(_body.transform, $"<color={r.Color}>{r.Name}</color>",
                new Vector2(GridX + GridDx * (i % Cols), GridTop - GridDy * (i / Cols)), 0.55f, () => Press(r));
            if (plate && r == _selected) plate.color = PlateOn;
        }

        if (all.Count == 0)
            Label(_body.transform, $"<color=#AAAAAA>{new Text("RolePicker.NoMatch")}</color>", new Vector2(0f, GridTop - GridDy), 2.2f);

        var prev = Button(_body.transform, "◀", new Vector2(GridX, BottomY), 0.3f, () => { if (_page > 0) { _page--; Build(); } });
        var next = Button(_body.transform, "▶", new Vector2(-GridX, BottomY), 0.3f, () => { if (_page < pages - 1) { _page++; Build(); } });
        if (prev && _page == 0) prev.color = new Color(1f, 1f, 1f, 0.25f);
        if (next && _page >= pages - 1) next.color = new Color(1f, 1f, 1f, 0.25f);
        string hint = _selected != null
            ? new Text("RolePicker.Confirm").With("role", $"<color={_selected.Color}>{_selected.Name}</color>").ToString()
            : new Text("RolePicker.Pick").ToString();
        Label(_body.transform, $"{hint}   <color=#AAAAAA>{_page + 1}/{pages}</color>", new Vector2(0f, BottomY), 6.0f);
    }

    // 1 回目は選ぶだけ。同じ役職をもう一度押すと決まる (押し間違いで撃たないように)
    private static void Press(RoleBase r)
    {
        if (_selected != r)
        {
            _selected = r;
            Build();
            return;
        }
        var cb = _onPick;
        Close();
        try { cb?.Invoke(r); }
        catch (Exception e) { Plugin.Logger.LogError($"role picker: {e}"); }
    }

    private static void Tab(Team t, Text label, Color color, int slot, bool searching)
    {
        bool on = !searching && t == _team;
        string hex = ColorUtility.ToHtmlStringRGB(color);
        string text = on ? $"<b><color=#{hex}>{label}</color></b>" : $"<color=#{hex}{(searching ? "66" : "")}>{label}</color>";
        var plate = Button(_body.transform, text, new Vector2(TabX + TabDx * slot, TopY), 0.45f, () =>
        {
            if (_team == t && _tokens.Length == 0) return;
            _team = t;
            _page = 0;
            _search?.Clear(); // 検索をやめて陣営の一覧へ戻る (検索の後置の呼び出しで作り直す)
            if (_tokens.Length > 0) { _tokens = Array.Empty<string>(); }
            Build();
        });
        if (plate && on) plate.color = PlateOn;
    }

    // 名札の土台 (votePlayerBase) を写したボタン。土台の絵を返す (選んだ色を塗る用)
    private static SpriteRenderer Button(Transform parent, string text, Vector2 at, float width, Action onClick)
    {
        var hud = MeetingHud.Instance;
        var src = hud.playerStates[0];
        var baseTf = src.transform.Find("votePlayerBase");
        if (!baseTf) return null;
        var holder = new GameObject("MrpRoleButton");
        holder.transform.SetParent(parent, false);
        holder.transform.localPosition = new Vector3(at.x, at.y, 0f);
        holder.transform.localScale = new Vector3(width, 0.55f, 1f);
        var b = Object.Instantiate(baseTf.gameObject, holder.transform);
        b.SetActive(true);
        b.transform.localPosition = Vector3.zero;
        var hl = b.transform.Find("ControllerHighlight");
        if (hl) hl.gameObject.SetActive(false);
        // 名札の土台はマスクの中でだけ描かれるので、名札のマスクも一緒に写す
        var mask = src.transform.Find("MaskArea");
        if (mask) Object.Instantiate(mask.gameObject, holder.transform).SetActive(true);
        var label = Object.Instantiate(src.NameText, holder.transform);
        label.transform.localPosition = new Vector3(0f, 0f, -1f);
        float sx = 1.7f * 0.55f / width;
        label.transform.localScale = new Vector3(sx, 1.7f, 1f);
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;
        // 字の箱を板 (幅 2.7) の 9 割に収め、はみ出す長い名前だけ字を縮める
        label.enableWordWrapping = false;
        label.rectTransform.sizeDelta = new Vector2(2.43f / sx, label.rectTransform.sizeDelta.y);
        label.fontSizeMax = label.fontSize;
        label.fontSizeMin = label.fontSize * 0.6f;
        label.enableAutoSizing = true;
        label.text = text;
        label.gameObject.SetActive(true);
        var pb = b.GetComponent<PassiveButton>();
        if (pb)
        {
            pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            pb.OnClick.AddListener((UnityAction)(() => onClick()));
            pb.OnMouseOver = new UnityEvent();
            pb.OnMouseOut = new UnityEvent();
            _buttons.Add(pb);
        }
        return b.GetComponent<SpriteRenderer>();
    }

    // 押せない字だけの欄 (名札の名前の字を写す)
    private static void Label(Transform parent, string text, Vector2 at, float width)
    {
        var src = MeetingHud.Instance.playerStates[0];
        var label = Object.Instantiate(src.NameText, parent);
        label.transform.localPosition = new Vector3(at.x, at.y, -1f);
        label.transform.localScale = new Vector3(0.935f, 0.935f, 1f);
        label.rectTransform.sizeDelta = new Vector2(width / 0.935f * 1.0f, label.rectTransform.sizeDelta.y);
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;
        label.enableWordWrapping = false;
        label.text = text;
        label.gameObject.SetActive(true);
    }

    // 閉じる = 名札の投票の取り消し (✕) のボタンを写す
    private static void CloseButton(Vector2 at)
    {
        var src = MeetingHud.Instance.playerStates[0];
        var cancel = src.Buttons ? src.Buttons.transform.Find("CancelButton") : null;
        if (!cancel) { Button(_root.transform, $"<color=#FF5555>{new Text("RolePicker.Close")}</color>", at, 0.3f, Close); return; }
        // 取り消しのボタンも名札のマスクの中でだけ描かれるので、マスクと一緒に置く
        var holder = new GameObject("MrpRolePickerClose");
        holder.transform.SetParent(_root.transform, false);
        holder.transform.localPosition = new Vector3(at.x, at.y, -1f);
        var mask = src.transform.Find("MaskArea");
        if (mask)
        {
            var m = Object.Instantiate(mask.gameObject, holder.transform);
            m.SetActive(true);
            m.transform.localPosition = new Vector3(0f, 0f, 1f);
            m.transform.localScale = new Vector3(0.3f, 1.6f, 1f); // ボタン (0.56 四方) が欠けない高さ
        }
        var b = Object.Instantiate(cancel.gameObject, holder.transform);
        b.SetActive(true);
        b.transform.localPosition = Vector3.zero;
        // 本体の絵が赤い ✕。子の強調 (コントローラーで選んだ時の印) は使わないので消す
        var hl = b.transform.Find("ControllerHighlight");
        if (hl) hl.gameObject.SetActive(false);
        var pb = b.GetComponent<PassiveButton>();
        if (!pb) return;
        pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        pb.OnClick.AddListener((UnityAction)Close);
        _buttons.Add(pb);
    }

    // ---- 試験用 (bridge) ----

    internal static string Dump()
    {
        if (!IsOpen) return "closed";
        var names = new List<string>();
        foreach (var r in Candidates()) names.Add(r.Id);
        return $"team={_team} page={_page} q={string.Join(" ", _tokens)} selected={(_selected != null ? _selected.Id : "-")} roles={string.Join(",", names)}";
    }

    internal static bool TestType(string q)
    {
        if (_search == null) return false;
        _search.Type(q);
        return true;
    }

    internal static bool TestPress(string id)
    {
        foreach (var r in Candidates())
            if (string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)) { Press(r); return true; }
        return false;
    }

    internal static void TestTab(Team t)
    {
        _team = t;
        _page = 0;
        _search?.Clear();
        _tokens = Array.Empty<string>();
        Build();
    }
}

// 投票の時間に入ると本編が「投票スキップ」を出し直すので、選ぶ画面が開いている間は隠したままにする
[HarmonyLib.HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
internal static class RolePickerSkipPatch
{
    public static void Postfix(MeetingHud __instance)
    {
        if (!RolePicker.OpenFlag) return;
        // 会議ごと消えた (画面も一緒に消えた) 後は印を下ろす
        if (!RolePicker.IsOpen) { RolePicker.OpenFlag = RolePicker.SkipHidden = false; return; }
        var skip = __instance.SkipVoteButton;
        if (skip && skip.gameObject.activeSelf) { skip.gameObject.SetActive(false); RolePicker.SkipHidden = true; }
    }
}
