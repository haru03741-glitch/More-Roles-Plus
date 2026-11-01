using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MoreRolesPlus.Menu;
using MoreRolesPlus.UI;
using UnityEngine;
using UnityEngine.Events;

namespace MoreRolesPlus.Report;

// ロビーの右上の、本編の「?」(試合の情報) があった場所のバグ報告ボタン。「?」は絵と当たり判定を消して置き場としてだけ使い、
// その上に「?」と同じ台で絵 (赤いクルーと注意の印) のボタンを重ねる。ロビーに居る間だけ出し、試合中は役職辞典のボタンを「?」の場所へ寄せる。
// スタッフの返事が来ていれば緑の点を付ける。返事はロビーに居る間 3 分ごとに見に行く (開いている報告の新しい物から 5 件まで)
internal static class ReportNotifier
{
    private const string Name = "MrpReportButton";
    private const float Gap = 0.93f;            // 役職辞典のボタンと「?」の中心の間 (DictionaryButton と同じ)
    private static readonly string[] States = { "Inactive", "Active", "Selected" };
    private const float CheckEvery = 180f, FirstCheck = 8f;
    private const int MaxChecks = 5;

    private static readonly ConcurrentQueue<(ReportClient.Saved s, ReportClient.Thread t)> Done = new();
    private static readonly List<int> Slots = new();
    private static GameObject _button;
    private static Transform _anchor;          // 本編の「?」(絵を消した置き場)
    private static SpriteRenderer _dot;
    private static float _nextCheck;
    private static int _shownUnread = -1;
    private static UnityAction _onOver, _onOut;

    internal static void Ensure()
    {
        var hud = Vanilla.Hud;
        if (!hud || !hud.MatchInfoButton) return;
        var parent = hud.MatchInfoButton.transform;
        if (parent.Find(Name)) return;
        var menu = parent.parent ? parent.parent.Find("MenuButton") : null;
        if (!menu) return;

        var go = UnityEngine.Object.Instantiate(menu.gameObject, parent);
        go.name = Name;
        var aspect = go.GetComponent<AspectPosition>();
        if (aspect) UnityEngine.Object.DestroyImmediate(aspect);
        var ps = parent.localScale;
        go.transform.localScale = Vector3.one;
        go.transform.localPosition = new Vector3(0f, 0f, -0.05f);

        // 3 状態とも「?」の同じ状態の台にして、中の手帳を覆って絵を重ねる
        var icon = Roles.ButtonIcons.Get("bug_report");
        Vector3 size = Vector3.one * 0.8f;
        foreach (var state in States)
        {
            DictionaryButton.Restyle(go.transform, parent, state, icon);
            var sr = go.transform.Find(state)?.GetComponent<SpriteRenderer>();
            if (state == "Inactive" && sr) size = sr.bounds.size;
        }
        _anchor = parent;
        HideAnchor();

        var btn = go.GetComponent<PassiveButton>();
        if (btn)
        {
            UiActions.Release(Slots);
            btn.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            btn.OnClick.AddListener(UiActions.Take(Click, Slots));
            btn.OnMouseOver ??= new UnityEvent();
            btn.OnMouseOut ??= new UnityEvent();
            _onOver ??= (UnityAction)(() => { if (_button) Tooltip.Show(_button.transform, new Text("Report.ButtonTip")); });
            _onOut ??= (UnityAction)Tooltip.Hide;
            btn.OnMouseOver.AddListener(_onOver);
            btn.OnMouseOut.AddListener(_onOut);
        }

        // 返事の印 (右上の角の緑の点)。台の大きさは画面の単位なので、自分の倍率で割って置く
        float k = Math.Max(go.transform.lossyScale.x, 0.0001f);
        var dot = Slider.Disc(go.transform, "Unread", UiAssets.Circle, 0.26f / k, Color.black, go.layer);
        dot.transform.localPosition = new Vector3(size.x * 0.42f / k, size.y * 0.4f / k, -0.1f);
        Slider.Disc(dot.transform, "Dot", UiAssets.Circle, 0.72f, new Color(0f, 0.9f, 0.46f), go.layer);
        _dot = dot;
        _button = go;
        _shownUnread = -1;
        _nextCheck = Time.realtimeSinceStartup + FirstCheck;
        go.SetActive(MatchInfoExtension.InLobby);
        Refresh();
    }

    // 本編の「?」の絵と当たり判定を消す (本編が状態を切り替えるたびに出し直すので毎回)
    private static void HideAnchor()
    {
        if (!_anchor) return;
        foreach (var state in States)
        {
            var t = _anchor.Find(state);
            var sr = t ? t.GetComponent<SpriteRenderer>() : null;
            if (sr && sr.enabled) sr.enabled = false;
        }
        var col = _anchor.GetComponent<Collider2D>();
        if (col && col.enabled) col.enabled = false;
    }

    // ロビーでは役職辞典のボタンを左に (右がバグ報告)、試合中は「?」の場所へ
    private static void PlaceDictionary(bool lobby)
    {
        var dict = _anchor ? _anchor.Find("MrpDictionaryButton") : null;
        if (!dict) return;
        float sx = _anchor.localScale.x == 0f ? 1f : _anchor.localScale.x;
        float x = lobby ? -Gap / sx : 0f;
        var p = dict.localPosition;
        if (Math.Abs(p.x - x) > 0.001f) dict.localPosition = new Vector3(x, p.y, p.z);
    }

    private static void Click()
    {
        if (!ReportWindow.IsOpen) ReportWindow.Open();
    }

    private static void Refresh()
    {
        int unread = ReportClient.Unread;
        if (unread == _shownUnread || !_dot) return;
        _shownUnread = unread;
        _dot.gameObject.SetActive(unread > 0);
    }

    public static void Enqueue(ReportClient.Saved s, ReportClient.Thread t) => Done.Enqueue((s, t));

    // ReportWindow.Tick から (毎フレーム)
    public static void Tick()
    {
        while (Done.TryDequeue(out var r)) ReportWindow.MarkRead(r.s, r.t);
        if (!_button) return;
        bool lobby = MatchInfoExtension.InLobby;
        if (_button.activeSelf != lobby) _button.SetActive(lobby);
        HideAnchor();
        PlaceDictionary(lobby);
        if (!lobby) return;
        Refresh();
        if (Time.realtimeSinceStartup < _nextCheck) return;
        _nextCheck = Time.realtimeSinceStartup + CheckEvery;
        var targets = ReportClient.All.Where(s => !s.Closed && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - s.Created < 30 * 86400)
            .OrderByDescending(s => s.Created).Take(MaxChecks).ToList();
        foreach (var s in targets)
        {
            var saved = s;
            Task.Run(async () =>
            {
                try { Enqueue(saved, await ReportClient.Fetch(saved).ConfigureAwait(false)); }
                catch { }
            });
        }
    }

    // 確かめ用 (ブリッジ): info / click / ensure
    internal static string Command(string op)
    {
        if (op == "ensure") Ensure();
        if (op == "click")
        {
            if (!_button) return "no button";
            _button.GetComponent<PassiveButton>().OnClick.Invoke();
        }
        return _button ? $"button active={_button.activeInHierarchy} pos={_button.transform.position} unread={ReportClient.Unread}" : "no button";
    }
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
internal static class ReportButtonPatch
{
    public static void Postfix()
    {
        try { ReportNotifier.Ensure(); }
        catch (Exception e) { Plugin.Logger.LogError($"report button: {e}"); }
    }
}
