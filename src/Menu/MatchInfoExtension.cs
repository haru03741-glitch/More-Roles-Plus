using System;
using System.Collections.Generic;
using System.Text;
using AmongUs.GameOptions;
using HarmonyLib;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles;
using MoreRolesPlus.Roles.Builtin;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Menu;

// 本編の「試合の情報」(右上の「?」) に MRP の役職・アドオン・設定を足し、ロビーでも開けるようにする。
// 役職のタブ = 今の部屋で出る役職とアドオンを 1 列のカードで (自分の物が先頭)。カードは本編の作り方で作ってから
//   字だけ書き換え、横幅を板いっぱいに広げる。中身は説明とその役職の設定の値。
// 設定のタブ = 本編の項目のあとに MRP の全体の設定を見出し付きで足す。
// 開くたびに作り直す (開いている間の毎フレームの処理は無い)
internal static class MatchInfoExtension
{
    private const float CardPad = 0.1f;           // カードの下の余白
    private const float TextRight = 0.15f;        // 字の欄の右端とカードの右端の間
    private const float BlurbSize = 1.3f;         // 説明の字の大きさ (本編のカードと同じ)
    private const float BottomMargin = 0.3f;      // 役職の欄の最後のカードの下の余白
    private const float IconSize = 0.36f;         // MRP の役職の色の丸の直径
    private static readonly List<GameObject> Added = new();  // 設定のタブに足した物 (開き直す時に片付ける)
    private static Vector2 _cell0;                 // 本編のカードの大きさ (最初に見た値)
    private static float? _settingsBase;           // 本編の設定の欄のスクロールの下端 − 本編の行数 × 行の高さ (最初に見た時に 1 回)

    internal static bool InLobby => LobbyBehaviour.Instance && !ShipStatus.Instance;

    // ロビーでは本編の Open が何もせず戻るので、本編の Open と同じ部品を順に呼ぶ
    internal static void OpenInLobby(MatchInfoGuide g)
    {
        g.numOfTabs = g.TabButtons.Count;
        foreach (var go in g.NormalModeSettings)
            if (go)
            {
                go.transform.SetParent(null, false);
                Object.Destroy(go);
            }
        g.NormalModeSettings.Clear();
        g.CreateNormalModeSettings();
        g.CreatePlayerEntries();
        g.MatchInfoParent.SetActive(true);
        g.SetActiveTab(g.activeTabIndex < g.numOfTabs ? g.activeTabIndex : 0);
    }

    internal static void Extend(MatchInfoGuide g)
    {
        VanillaRoles.EnsureSettings();
        var client = AmongUsClient.Instance;
        bool waiting = client && !client.AmHost && !OptionSync.HoldsHostValues;
        BuildRoles(g, waiting);
        BuildSettings(g, waiting);
    }

    // ── 役職のタブ ──

    private static void BuildRoles(MatchInfoGuide g, bool waiting)
    {
        var scroller = g.MatchInfoRoleScroller;
        var inner = scroller ? scroller.Inner : null;
        if (!inner) return;
        for (int i = inner.childCount - 1; i >= 0; i--)
        {
            var c = inner.GetChild(i).gameObject;
            c.transform.SetParent(null, false);
            Object.Destroy(c);
        }

        var list = waiting ? new List<Assignable>() : Collect();
        var grid = inner.GetComponent<UnityEngine.UI.GridLayoutGroup>();
        var crew = Base(RoleTypes.Crewmate);
        var imp = Base(RoleTypes.Impostor);
        if (!grid || !crew || !imp) return;
        if (_cell0 == Vector2.zero) _cell0 = grid.cellSize;

        // 幅は欄いっぱい (マスクの幅 − 両端の余白)
        float width = g.MatchInfoRoleMaskArea ? g.MatchInfoRoleMaskArea.size.x - 0.2f : _cell0.x * 2f;
        float need = 0f;
        var cards = new List<MatchInfoRolePanel>();
        var mine = Mine();
        foreach (var a in list)
        {
            var b = a is VanillaRole vr ? vr.Behaviour() : null;
            g.CreateRoleEntry(b ? b : (a is RoleBase r && r.Team == Team.Impostor ? imp : crew));
            var card = inner.GetChild(inner.childCount - 1).GetComponent<MatchInfoRolePanel>();
            if (!card) continue;
            Fill(card, a, b, mine.Contains(a.Id), width);
            need = Math.Max(need, CardHeight(card));
            cards.Add(card);
        }

        float h = Math.Max(_cell0.y, need);
        grid.cellSize = new Vector2(width, h);
        // 本編のカードは中心から上下 _cell0.y / 2 に部品を置いている。高くした分だけ上へ寄せて、伸びた分は説明の下へ
        float dy = (h - _cell0.y) / 2f;
        foreach (var card in cards) Lift(card, dy);

        if (g.rolesEnabledMessage) g.rolesEnabledMessage.SetActive(list.Count == 0);
        float view = g.MatchInfoRoleMaskArea ? g.MatchInfoRoleMaskArea.size.y : 1.8f;
        // 欄の上端から 1 枚目のカードまでの余白があるので、最後のカードが下端で切れないよう同じだけ足す
        float content = list.Count * (h + grid.spacing.y) - grid.spacing.y + grid.padding.top + grid.padding.bottom + BottomMargin;
        scroller.SetYBoundsMax(Math.Max(0f, content - view));
        scroller.ScrollToTop();
    }

    // 自分の役職とアドオンが先頭、あとは設定画面と同じ並び (クルー → インポスター → 第三陣営 → アドオン)
    private static List<Assignable> Collect()
    {
        var mine = Mine();
        var first = new List<Assignable>();
        var rest = new List<Assignable>();
        foreach (var tab in new[] { Tab.Crew, Tab.Impostor, Tab.Neutral, Tab.Addon })
            foreach (var sec in Registry.SectionsOf(tab))
            {
                var a = sec.Role;
                if (a == null || a.Chance == null) continue;
                if (mine.Contains(a.Id)) first.Add(a);
                else if (a.Chance.Value > 0) rest.Add(a);
            }
        first.AddRange(rest);
        return first;
    }

    // 試合中の自分の役職とアドオンの Id。本編の役職のままの人は本編の役職の Id
    private static HashSet<string> Mine()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var me = PlayerControl.LocalPlayer;
        if (!ShipStatus.Instance || !me || me.Data == null) return ids;
        string id = RoleState.Local?.Id ?? (me.Data.Role ? me.Data.Role.Role.ToString() : null);
        if (id != null) ids.Add(id);
        foreach (var ad in RoleState.AddonsOf(me.PlayerId)) ids.Add(ad.Id);
        return ids;
    }

    private static RoleBehaviour Base(RoleTypes t)
    {
        var rm = RoleManager.Instance;
        if (!rm || rm.AllRoles == null) return null;
        foreach (var r in rm.AllRoles)
            if (r && r.Role == t) return r;
        return null;
    }

    private static void Fill(MatchInfoRolePanel card, Assignable a, RoleBehaviour vanilla, bool mine, float width)
    {
        card.name = "MrpRole_" + a.Id;
        var head = new StringBuilder(a.ColoredName);
        head.Append("  <size=70%><color=#A8A8B4>").Append(TeamLabel(a)).Append("</color></size>");
        if (mine) head.Append("  <size=70%><color=#FFD84D>").Append(new Text("MatchInfo.Mine").ToString()).Append("</color></size>");
        card.roleName.text = head.ToString();
        card.roleCount.text = a.Chance.Value > 0
            ? new Text("MatchInfo.Count").With("count", a.Count != null ? a.Count.Value : 1).With("chance", a.Chance.Value)
            : new Text("MatchInfo.NotInRoom");

        var body = new StringBuilder(a.Help.ToString());
        string opts = OptionLine(a);
        if (opts.Length > 0) body.Append("\n<color=#A8A8B4>").Append(opts).Append("</color>");
        card.roleDescription.text = body.ToString();

        // 本編の役職は本編のアイコン。MRP の物は役職の色の丸 (本編のクルーには白いアイコンが無い)。
        // 絵だけ差し替えて、マスクの設定が入った本編の SpriteRenderer はそのまま使う
        if (!vanilla && card.roleIcon)
        {
            var ic = card.roleIcon;
            // 丸の大きさはアイコンの縮尺 (本編は 0.18 倍) の中の単位で決める。開く途中の板は 0 倍から広がるので lossyScale は使わない
            float s = ic.transform.localScale.x;
            if (!(s > 0f)) s = 1f;
            ic.sprite = MenuArt.Round(IconSize / 2f / s);
            ic.drawMode = SpriteDrawMode.Sliced;
            ic.size = new Vector2(IconSize, IconSize) / s;
            ic.color = RoleDisplay.ParseColor(a.Color);
        }

        // 横に広げる: カードの左端は dx だけ左へ動くので部品も dx 左へ。字の欄は左端を保ち、右端をカードの右端の手前まで
        float dx = (width - _cell0.x) / 2f;
        for (int i = 0; i < card.transform.childCount; i++)
        {
            var c = card.transform.GetChild(i);
            var p = c.localPosition;
            var tmp = c.GetComponent<TextMeshPro>();
            if (!tmp) { c.localPosition = new Vector3(p.x - dx, p.y, p.z); continue; }
            var rt = tmp.rectTransform;
            float left = p.x - rt.sizeDelta.x * rt.pivot.x - dx;
            float w = width / 2f - TextRight - left;
            rt.sizeDelta = new Vector2(w, rt.sizeDelta.y);
            c.localPosition = new Vector3(left + w * rt.pivot.x, p.y, p.z);
            tmp.enableWordWrapping = true;
        }
        var d = card.roleDescription;
        d.enableAutoSizing = false;
        d.fontSize = BlurbSize;
        d.margin = Vector4.zero;
        d.alignment = TextAlignmentOptions.TopLeft;
    }

    // 説明の欄の上端 = 人数の欄の下端の少し下 (カードの中心が原点・カードの高さが本編のままの時の座標)
    private static float BlurbTop(MatchInfoRolePanel card)
    {
        var rt = card.roleCount.rectTransform;
        return card.roleCount.transform.localPosition.y - rt.sizeDelta.y * rt.pivot.y - 0.03f;
    }

    private static float BlurbHeight(MatchInfoRolePanel card)
    {
        var d = card.roleDescription;
        return d.GetPreferredValues(d.text, d.rectTransform.sizeDelta.x, 0f).y;
    }

    private static float CardHeight(MatchInfoRolePanel card) => _cell0.y / 2f - BlurbTop(card) + BlurbHeight(card) + CardPad;

    // カードを高くした分 (上下に dy ずつ) 部品を上へ寄せ、説明の欄は上端から文の高さだけ下へ取る
    private static void Lift(MatchInfoRolePanel card, float dy)
    {
        float top = BlurbTop(card) + dy, h = BlurbHeight(card);
        for (int i = 0; i < card.transform.childCount; i++)
        {
            var c = card.transform.GetChild(i);
            var p = c.localPosition;
            c.localPosition = new Vector3(p.x, p.y + dy, p.z);
        }
        var d = card.roleDescription;
        var rt = d.rectTransform;
        rt.sizeDelta = new Vector2(rt.sizeDelta.x, h);
        var lp = d.transform.localPosition;
        d.transform.localPosition = new Vector3(lp.x, top - h * (1f - rt.pivot.y), lp.z);
    }

    private static string TeamLabel(Assignable a) => a is RoleBase r
        ? r.Team switch
        {
            Team.Crew => new Text("InfoCommands.Crew"),
            Team.Impostor => new Text("InfoCommands.Impostor"),
            _ => new Text("InfoCommands.Neutral"),
        }
        : new Text("InfoCommands.Addon");

    // その役職の設定 (出現率と人数を除く) を「名前: 値」で 1 行ずつ
    private static string OptionLine(Assignable a)
    {
        var sb = new StringBuilder();
        foreach (var sec in Registry.SectionsOf(a.Tab))
        {
            if (sec.Role != a) continue;
            foreach (var o in sec.Opts)
            {
                if (o == a.Chance || o == a.Count) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(o.Label.ToString()).Append(": ").Append(Value(o));
            }
            break;
        }
        return sb.ToString();
    }

    private static string Value(Opt o)
    {
        o.Sync();
        var g = MatchInfoGuide.Instance;
        return o.IsToggle && g ? g.GetBoolString(o.Index != 0) : o.Display();
    }

    // ── 設定のタブ ──

    private static void BuildSettings(MatchInfoGuide g, bool waiting)
    {
        foreach (var go in Added)
            if (go)
            {
                go.transform.SetParent(null, false);
                Object.Destroy(go);
            }
        Added.Clear();
        if (g.NormalModeSettings.Count == 0) return;
        var grid = g.NormalModeSettings[0] ? g.NormalModeSettings[0].transform.parent : null;
        var scroller = ScrollerAbove(grid);
        if (!grid || !scroller) return;
        var lg = grid.GetComponent<UnityEngine.UI.GridLayoutGroup>();
        float rowH = lg ? lg.cellSize.y + lg.spacing.y : 0.5f;
        // 下端は前に開いた時に自分が書いた値かもしれないので、本編だけの行数との差を最初に 1 回控え、毎回そこから出す
        _settingsBase ??= scroller.ContentYBounds.max - (g.NormalModeSettings.Count + 1) / 2 * rowH;

        int n = g.NormalModeSettings.Count;
        if (waiting) Header(g, ref n, new Text("LobbyView.HostSettingsHaveNotArrived"), null);
        else
            foreach (var sec in Registry.SectionsOf(Tab.General))
            {
                bool any = false;
                foreach (var o in sec.Opts)
                {
                    if (!any) { Header(g, ref n, sec.Title, sec.Color); any = true; }
                    Entry(g, ref n, o.Label.ToString(), Value(o));
                }
            }

        // 1 行 = 本編の 2 項目
        scroller.SetYBoundsMax(Math.Max(0f, _settingsBase.Value + (n + 1) / 2 * rowH));
    }

    // GetComponentInParent は閉じたタブ (非表示) の中では見つけないので親を順に見る
    private static Scroller ScrollerAbove(Transform t)
    {
        for (; t; t = t.parent)
        {
            var s = t.GetComponent<Scroller>();
            if (s) return s;
        }
        return null;
    }

    // 見出しは行の左に置き、右は空ける (次の項目が新しい行から始まるように)
    private static void Header(MatchInfoGuide g, ref int n, Text title, string color)
    {
        if (n % 2 == 1) Blank(g, ref n);
        var e = Entry(g, ref n, color != null ? $"<color={color}><b>{title}</b></color>" : $"<b>{title}</b>", "");
        if (e)
        {
            var v = e.transform.Find("SettingValue");
            if (v) v.gameObject.SetActive(false);
        }
        Blank(g, ref n);
    }

    private static void Blank(MatchInfoGuide g, ref int n)
    {
        var e = Entry(g, ref n, "", "");
        if (!e) return;
        for (int i = 0; i < e.transform.childCount; i++) e.transform.GetChild(i).gameObject.SetActive(false);
    }

    // 本編の項目 (マスクの設定込み) を複製して字を書き換える。本編の一覧には入れない (開き直しで本編が消さないように)
    private static MatchInfoGuideSettingLabel Entry(MatchInfoGuide g, ref int n, string label, string value)
    {
        var src = g.NormalModeSettings[0];
        if (!src) return null;
        var go = Object.Instantiate(src, src.transform.parent);
        go.name = "MrpSetting";
        Added.Add(go);
        n++;
        var e = go.GetComponent<MatchInfoGuideSettingLabel>();
        if (e) e.SetInfo(label, value);
        return e;
    }

    // ── ロビーのボタン ──

    // ボタンは自分の人が出来てから出す (出来る前に出すと本編のボタンの Update が NRE)。部屋に着いた時に予約し、Tick で待つ。
    // ロビーではチャットのボタンが常に出ているので、本編が会議中に使う「チャットをよけた位置」を普段の位置にする
    // (ボタンは毎フレームどちらかの静的な位置へ自分を置くので、静的な値を 1 回書く)。船に移る時に戻す
    private static Vector3? _vanillaDefault;

    // 本編のボタンの普段の位置。練習の試合 (PracticeMatch) も同じ静的な値を書き換えるので、書き換える前に必ずここで 1 回控えて共有する
    internal static Vector3 VanillaButtonDefault => _vanillaDefault ??= MatchInfoHudButton.defaultDistanceFromEdge;
    private static bool _buttonPending;

    internal static void OnLobbyStart() => _buttonPending = true;

    public static void Tick()
    {
        if (!_buttonPending) return;
        if (!InLobby) { _buttonPending = false; return; }
        if (ShowLobbyButton()) _buttonPending = false;
    }

    private static bool ShowLobbyButton()
    {
        var hud = Vanilla.Hud;
        var me = PlayerControl.LocalPlayer;
        if (!hud || !hud.MatchInfoButton || !me || me.Data == null) return false;
        _ = VanillaButtonDefault;
        MatchInfoHudButton.defaultDistanceFromEdge = MatchInfoHudButton.adjustedDistanceFromEdge;
        if (!hud.MatchInfoButton.gameObject.activeSelf) hud.MatchInfoButton.gameObject.SetActive(true);
        return true;
    }

    internal static void LeaveLobby()
    {
        if (_vanillaDefault is { } v) MatchInfoHudButton.defaultDistanceFromEdge = v;
    }

    // dev: open / close / click (右上のボタンを押したのと同じ) / tab <n> / scroll <y> (今のタブの欄を y まで送る) / dump (カードと設定の欄の寸法)
    internal static string Simulate(string args)
    {
        var g = MatchInfoGuide.Instance;
        if (!g) return "ERR matchinfo no guide";
        var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = a.Length > 0 ? a[0] : "";
        try
        {
            if (verb == "open") { if (!g.IsActive) g.Open(); }
            else if (verb == "click") { if (Vanilla.Hud && Vanilla.Hud.MatchInfoButton) Vanilla.Hud.MatchInfoButton.OnClick.Invoke(); }
            else if (verb == "close") { if (g.IsActive) g.Close(); }
            else if (verb == "tab" && a.Length > 1 && int.TryParse(a[1], out int t)) g.SetActiveTab(t);
            else if (verb == "dump") return "OK matchinfo " + Dump(g);
            else if (verb == "scroll" && a.Length > 1 && float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y))
            {
                var s = g.activeTabIndex == 2 ? g.MatchInfoRoleScroller : (g.NormalModeSettings.Count > 0 && g.NormalModeSettings[0] ? ScrollerAbove(g.NormalModeSettings[0].transform) : null);
                if (s && s.Inner)
                {
                    var p = s.Inner.localPosition;
                    s.Inner.localPosition = new Vector3(p.x, Math.Clamp(y, s.ContentYBounds.min, s.ContentYBounds.max), p.z);
                }
            }
        }
        catch (Exception e) { return "ERR matchinfo " + e.Message; }
        var btn = Vanilla.Hud ? Vanilla.Hud.MatchInfoButton : null;
        return $"OK matchinfo active={g.IsActive} tab={g.activeTabIndex}/{g.numOfTabs} button={(btn && btn.gameObject.activeInHierarchy)} " +
               $"roles={(g.MatchInfoRoleScroller && g.MatchInfoRoleScroller.Inner ? g.MatchInfoRoleScroller.Inner.childCount : -1)} settings={g.NormalModeSettings.Count}+{Added.Count}";
    }

    private static string Dump(MatchInfoGuide g)
    {
        var sb = new StringBuilder();
        var inner = g.MatchInfoRoleScroller ? g.MatchInfoRoleScroller.Inner : null;
        var grid = inner ? inner.GetComponent<UnityEngine.UI.GridLayoutGroup>() : null;
        if (grid) sb.Append($"cell={grid.cellSize} spacing={grid.spacing} cell0={_cell0} ");
        if (g.MatchInfoRoleScroller) sb.Append($"rolesY={g.MatchInfoRoleScroller.ContentYBounds.min:0.##}..{g.MatchInfoRoleScroller.ContentYBounds.max:0.##} ");
        if (inner && inner.childCount > 0)
        {
            var card = inner.GetChild(0).GetComponent<MatchInfoRolePanel>();
            var ic = card.roleIcon;
            sb.Append($"icon={(ic ? $"{(ic.sprite ? ic.sprite.name : "-")} size={ic.size} col={ic.color}" : "null")} ");
            foreach (var t in new[] { card.roleName, card.roleCount, card.roleDescription })
            {
                var rt = t.rectTransform;
                sb.Append($"\n {t.name} pos={t.transform.localPosition} size={rt.sizeDelta} pivot={rt.pivot} align={t.alignment} fs={t.fontSize:0.##} auto={t.enableAutoSizing} wrap={t.enableWordWrapping} pref={t.GetPreferredValues(t.text, rt.sizeDelta.x, 0f)}");
            }
        }
        var s0 = g.NormalModeSettings.Count > 0 ? g.NormalModeSettings[0] : null;
        var sg = s0 ? s0.transform.parent : null;
        sb.Append($"\n settings0={(s0 ? s0.name : "null")} grid={(sg ? sg.name : "null")} scroller={(ScrollerAbove(sg) ? "yes" : "no")} general={Registry.SectionsOf(Tab.General).Count}");
        return sb.ToString();
    }
}

[HarmonyPatch(typeof(MatchInfoGuide), nameof(MatchInfoGuide.Open))]
internal static class MatchInfoOpenPatch
{
    public static bool Prefix(MatchInfoGuide __instance)
    {
        if (!MatchInfoExtension.InLobby) return true;
        try { MatchInfoExtension.OpenInLobby(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"match info (lobby): {e}"); }
        return false;
    }

    public static void Postfix(MatchInfoGuide __instance)
    {
        if (!__instance.MatchInfoParent || !__instance.MatchInfoParent.activeSelf) return;
        try { MatchInfoExtension.Extend(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"match info: {e}"); }
    }
}

// ロビーでは本編が「?」のボタンを隠すので出す (位置は本編のボタンが自分で決める)
[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
internal static class MatchInfoLobbyButtonPatch
{
    public static void Postfix() => MatchInfoExtension.OnLobbyStart();
}

[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Start))]
internal static class MatchInfoLeaveLobbyPatch
{
    public static void Postfix() => MatchInfoExtension.LeaveLobby();
}
