// Building role buttons out of the meeting's nameplate parts based on https://github.com/TheOtherRolesAU/TheOtherRoles
// (GuesserGM / Guesser meeting UI, GPL-3.0) via https://github.com/ykundesu/SuperNewRoles (GPL-3.0)
using System;
using System.Collections.Generic;
using MoreRolesPlus.Options;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 役職を選ぶ画面 (会議中だけ)。会議の名札を隠し、名札の部品を写したボタンで、この試合に出うる役職を陣営ごとに並べる。
// 下の段は 陣営の切り替え・ページ送り・閉じる
public static class RolePicker
{
    private const int Cols = 5, Rows = 6, PerPage = Cols * Rows;

    private static GameObject _root;
    private static readonly System.Collections.Generic.List<PassiveButton> _buttons = new();

    // 作ったボタンの管理側の受け手を外してから消す (外さないと Unity とまたいだ参照が残って解放されない)
    private static void DestroyRoot()
    {
        foreach (var pb in _buttons) if (pb) pb.OnClick.RemoveAllListeners();
        _buttons.Clear();
        if (_root) Object.Destroy(_root);
        _root = null;
    }
    private static Action<RoleBase> _onPick;
    private static Team _team;
    private static int _page;
    private static readonly List<GameObject> Hidden = new();

    public static bool IsOpen => _root;

    public static bool Open(Action<RoleBase> onPick)
    {
        var hud = MeetingHud.Instance;
        if (!hud || IsOpen || hud.playerStates == null || hud.playerStates.Length == 0) return false;
        _onPick = onPick;
        _team = Team.Crew;
        _page = 0;
        Hidden.Clear();
        foreach (var pva in hud.playerStates)
            if (pva && pva.gameObject.activeSelf) { pva.gameObject.SetActive(false); Hidden.Add(pva.gameObject); }
        if (hud.SkipVoteButton && hud.SkipVoteButton.gameObject.activeSelf) { hud.SkipVoteButton.gameObject.SetActive(false); Hidden.Add(hud.SkipVoteButton.gameObject); }
        Build();
        return true;
    }

    public static void Close()
    {
        DestroyRoot();
        foreach (var go in Hidden) if (go) go.SetActive(true);
        Hidden.Clear();
    }

    // この試合に出うる役職 (出現率が 0 でない物・誰かに作られる物・本編の役職)
    private static List<RoleBase> Candidates(Team team)
    {
        var list = new List<RoleBase>();
        foreach (var r in Registry.Roles)
        {
            if (r.Team != team) continue;
            if (r.IsVanilla || r.MadeByOthers || (r.Chance != null && r.Chance.Value > 0)) list.Add(r);
        }
        return list;
    }

    private static void Build()
    {
        var hud = MeetingHud.Instance;
        if (!hud) { Close(); return; }
        DestroyRoot();
        _root = new GameObject("MrpRolePicker");
        _root.transform.SetParent(hud.transform, false);
        _root.transform.localPosition = new Vector3(0f, 0f, -5f);

        var all = Candidates(_team);
        int pages = Math.Max(1, (all.Count + PerPage - 1) / PerPage);
        if (_page >= pages) _page = pages - 1;
        for (int i = 0; i < PerPage; i++)
        {
            int k = _page * PerPage + i;
            if (k >= all.Count) break;
            var r = all[k];
            Button($"<color={r.Color}>{r.Name}</color>", new Vector2(-3.47f + 1.73f * (i % Cols), 1.6f - 0.5f * (i / Cols)), 0.55f, () =>
            {
                var cb = _onPick;
                Close();
                try { cb?.Invoke(r); }
                catch (Exception e) { Plugin.Logger.LogError($"role picker: {e}"); }
            });
        }
        float y = 1.6f - 0.5f * Rows - 0.15f;
        Button(Tab(Team.Crew, new Text("RolePicker.Crew")), new Vector2(-3.47f, y), 0.5f, () => Switch(Team.Crew));
        Button(Tab(Team.Impostor, new Text("RolePicker.Impostor")), new Vector2(-1.74f, y), 0.5f, () => Switch(Team.Impostor));
        Button(Tab(Team.Neutral, new Text("RolePicker.Neutral")), new Vector2(-0.01f, y), 0.5f, () => Switch(Team.Neutral));
        Button($"◀ {_page + 1}/{pages}", new Vector2(1.72f, y), 0.5f, () => { _page = (_page + pages - 1) % pages; Build(); });
        Button("▶", new Vector2(2.6f, y), 0.3f, () => { _page = (_page + 1) % pages; Build(); });
        Button($"<color=#FF5555>{new Text("RolePicker.Close")}</color>", new Vector2(3.45f, y), 0.3f, Close);
    }

    private static string Tab(Team t, Text label) => t == _team ? $"<b><u>{label}</u></b>" : label.ToString();

    private static void Switch(Team t)
    {
        _team = t;
        _page = 0;
        Build();
    }

    // 名札の土台 (votePlayerBase) を写したボタン
    private static void Button(string text, Vector2 at, float width, Action onClick)
    {
        var hud = MeetingHud.Instance;
        var src = hud.playerStates[0];
        var baseTf = src.transform.Find("votePlayerBase");
        if (!baseTf) return;
        var holder = new GameObject("MrpRoleButton");
        holder.transform.SetParent(_root.transform, false);
        holder.transform.localPosition = new Vector3(at.x, at.y, 0f);
        holder.transform.localScale = new Vector3(width, 0.55f, 1f);
        var b = Object.Instantiate(baseTf.gameObject, holder.transform);
        b.SetActive(true);
        b.transform.localPosition = Vector3.zero;
        var label = Object.Instantiate(src.NameText, holder.transform);
        label.transform.localPosition = new Vector3(0f, 0f, -1f);
        label.transform.localScale = new Vector3(1.7f * 0.55f / width, 1.7f, 1f);
        label.alignment = TextAlignmentOptions.Center;
        label.text = text;
        label.gameObject.SetActive(true);
        var pb = b.GetComponent<PassiveButton>();
        if (!pb) return;
        pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        pb.OnClick.AddListener((UnityAction)(() => onClick()));
        _buttons.Add(pb);
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOut = new UnityEvent();
    }
}
