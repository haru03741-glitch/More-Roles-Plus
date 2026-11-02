using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Dev;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles;
using MoreRolesPlus.UI;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// ロビーのボットの窓。右上の「?」の並び (役職辞典のボタンの左) に、公式サーバーでない部屋のホストにだけボタンを出す。
// 窓 = ボットを出す・全部消す・試合中の動き・ボットごとの役職と消すボタン。役職は役職辞典の頁から選ぶ
// (辞典の役職の頁に「この役職のボットを出す」/「BOT n に付ける」が出る)。
internal static class BotPanel
{
    private const string ButtonName = "MrpBotButton";
    private const float Gap = 0.93f;            // 「?」の並びの間 (役職辞典のボタンと同じ)
    private const float WinW = 7.2f, WinH = 4.9f, InnerW = 6.6f;
    private static readonly Color TitleColor = new(1f, 0.85f, 0.35f);
    private static readonly Color SubText = new(0.78f, 0.8f, 0.86f, 1f);
    private static readonly Color ModeColor = new(0.5f, 0.85f, 0.75f);
    private static readonly Color RemoveColor = new(1f, 0.45f, 0.42f);

    // 役職辞典から役職を選んでいる間の相手 (255 = 選んでいない → 辞典の頁では「この役職のボットを出す」)
    internal static byte PickFor = 255;

    private static readonly List<int> Slots = new();
    private static Sprite _icon;
    private static bool _iconSearched; // 見つからなかった時に全部の絵を探し直さない
    // 本編の絵を名前で探す (ロビーの人数の欄のクルーの絵・無ければミニクルーのペットの絵)
    private static readonly string[] IconNames = { "ICON-Crewmate", "crewmin_walk0001" };

    public static bool IsOpen => UiWindow.Current != null && UiWindow.Current.Root && UiWindow.Current.Root.name == "MrpWindow_Bots";

    // ---- 右上のボタン ----

    internal static void Ensure()
    {
        var hud = Vanilla.Hud;
        if (!hud || !hud.MatchInfoButton) return;
        var parent = hud.MatchInfoButton.transform;
        if (parent.Find(ButtonName)) return;
        var menu = parent.parent ? parent.parent.Find("MenuButton") : null;
        var icon = Icon();
        if (!menu || !icon) return;
        var go = UnityEngine.Object.Instantiate(menu.gameObject, parent);
        go.name = ButtonName;
        var aspect = go.GetComponent<AspectPosition>();
        if (aspect) UnityEngine.Object.DestroyImmediate(aspect);
        var ps = parent.localScale;
        go.transform.localScale = Vector3.one;
        go.transform.localPosition = new Vector3(-2f * Gap / (ps.x == 0f ? 1f : ps.x), 0f, 0f);
        var src = hud.MatchInfoButton.transform;
        foreach (var state in new[] { "Inactive", "Active", "Selected" }) DictionaryButton.Restyle(go.transform, src, state, icon);
        var btn = go.GetComponent<PassiveButton>();
        if (btn)
        {
            UiActions.Release(Slots);
            btn.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            btn.OnClick.AddListener(UiActions.Take(() => { if (!IsOpen) Open(); }, Slots));
        }
        go.SetActive(Shown());
    }

    private static bool Shown() => BotPlay.CanUse && BotPlay.InLobby;

    private static float _nextCheck;
    private static bool _shown;
    private static int _frame;

    // 出す/隠すを 0.5 秒ごとに見直す (ホストが替わった・部屋を移った・試合が始まった)
    internal static void Tick()
    {
        // 時刻を読むのも 30 フレームに 1 回 (どの場面でも毎フレーム呼ばれるので)
        if (++_frame < 30) return;
        _frame = 0;
        float now = Time.unscaledTime;
        if (now < _nextCheck) return;
        _nextCheck = now + 0.5f;
        if (BotPlay.InLobby) Dev.DummyPatches.DropStaleInfos();
        bool on = Shown();
        // 出せない場面 (試合が始まった・ホストでなくなった) では窓も閉じる
        if (!on && IsOpen) UiWindow.Current.Close();
        if (on == _shown && !on) return;
        var hud = Vanilla.Hud;
        var t = hud && hud.MatchInfoButton ? hud.MatchInfoButton.transform.Find(ButtonName) : null;
        if (!t)
        {
            if (on) Ensure();
            _shown = on;
            return;
        }
        if (t.gameObject.activeSelf != on) t.gameObject.SetActive(on);
        _shown = on;
    }

    private static Sprite Icon()
    {
        if (_icon || _iconSearched) return _icon;
        _iconSearched = true;
        var found = new Dictionary<string, Sprite>();
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
        {
            var s = o.TryCast<Sprite>();
            if (s && Array.IndexOf(IconNames, s.name) >= 0 && !found.ContainsKey(s.name)) found[s.name] = s;
        }
        foreach (var n in IconNames)
            if (found.TryGetValue(n, out var s))
            {
                s.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                return _icon = s;
            }
        return null;
    }

    // ---- 窓 ----

    public static void Open()
    {
        var w = UiWindow.Open("Bots", new Vector2(WinW, WinH), () => { });
        if (w == null) return;
        Build();
    }

    private static void Build()
    {
        var w = UiWindow.Current;
        if (w == null || !w.Root || !IsOpen) return;
        var col = new Stack(false) { Gap = 0.12f, Width = InnerW };
        var bots = BotPlay.List();
        col.Add(new Stack(true,
            new Label(new Text("BotPanel.Title"), 2.4f) { MenuFont = true, Bold = true, Color = TitleColor },
            new Spacer(0.2f, 0.1f),
            new Label(new Text("BotPanel.Count").With("n", bots.Count), 1.5f) { Color = SubText }) { Gap = 0.1f, Align = Align.Start });

        col.Add(new Stack(true,
            Act(new Text("BotPanel.Add"), () => Do(BotPlay.Add(null))),
            Act(new Text("BotPanel.AddRole"), () => { PickFor = 255; RoleDictionary.Open(null); }),
            new Button(new Text("BotPanel.RemoveAll"), () => { BotPlay.RemoveAll(); Build(); }, 1.4f)
                { Size = new Vector2(0f, 0.42f), MinSize = new Vector2(1.3f, 0.42f), Color = RemoveColor }) { Gap = 0.12f, Align = Align.Start });

        col.Add(new Stack(true,
            new Label(new Text("BotPanel.Mode"), 1.5f) { Bold = true },
            ModeTab(BotPlay.Mode.Stand, "BotPanel.ModeStand"),
            ModeTab(BotPlay.Mode.Attack, "BotPanel.ModeAttack"),
            ModeTab(BotPlay.Mode.Auto, "BotPanel.ModeAuto")) { Gap = 0.1f, Align = Align.Start });
        string hint = BotPlay.Current switch
        {
            BotPlay.Mode.Stand => "BotPanel.HintStand",
            BotPlay.Mode.Auto => "BotPanel.HintAuto",
            _ => "BotPanel.HintAttack",
        };
        col.Add(new Label(new Text(hint), 1.3f) { Color = SubText, Width = InnerW, Align = Align.Start });

        var rows = new Stack(false) { Gap = 0.08f, Width = InnerW - 0.3f };
        if (bots.Count == 0) rows.Add(new Label(new Text("BotPanel.Empty"), 1.4f) { Color = SubText });
        foreach (var (pc, role) in bots)
        {
            byte id = pc.PlayerId;
            string roleText = role != null ? $"<color={role.Color}>{role.Name}</color>" : (string)new Text("BotPanel.RoleAny");
            rows.Add(new Plate(new Stack(true,
                new Label(pc.Data != null ? pc.Data.PlayerName : $"BOT {id}", 1.5f) { Bold = true, Width = 1.1f, NoWrap = true },
                new Label(roleText, 1.5f) { Width = 2.1f, NoWrap = true, MinFontSize = 1f },
                Small(new Text("BotPanel.ChangeRole"), () => { PickFor = id; RoleDictionary.Open(null); }),
                Small(new Text("BotPanel.RoleAny"), () => { BotPlay.SetRole(id, null); Build(); }),
                new Button("×", () => { BotPlay.Remove(id); Build(); }, 1.6f) { Size = new Vector2(0.38f, 0.38f), Color = RemoveColor })
                { Gap = 0.12f, Align = Align.Start }) { Width = InnerW - 0.3f });
        }
        col.Add(new ScrollView(new Vector2(InnerW, 2.1f), rows) { Tag = "bots:list" });
        w.Body.Set(col);
    }

    private static Button Act(string text, Action onClick) =>
        new(text, onClick, 1.4f) { Size = new Vector2(0f, 0.42f), MinSize = new Vector2(1.3f, 0.42f) };

    // 一覧の行の中のボタン (行に収まるよう小さめ)
    private static Button Small(string text, Action onClick) =>
        new(text, onClick, 1.3f) { Size = new Vector2(0f, 0.38f), MinSize = new Vector2(0.85f, 0.38f), Padding = new Vector2(0.1f, 0.03f) };

    private static Widget ModeTab(BotPlay.Mode mode, string key)
    {
        bool on = BotPlay.Current == mode;
        var label = new Label(new Text(key), 1.4f) { Bold = true, TextAlign = TextAlignmentOptions.Center, Color = on ? new Color(0.08f, 0.08f, 0.1f) : ModeColor };
        var b = new Button(label, () => { BotPlay.Current = mode; Build(); })
            { Size = new Vector2(0f, 0.42f), MinSize = new Vector2(1.4f, 0.42f), Padding = new Vector2(0.16f, 0.04f), Color = ModeColor };
        if (on) b.Fill = ModeColor;
        return b;
    }

    // 断られた時は窓の下に理由をチャットの注意書きで出す
    private static void Do(string why)
    {
        if (why != null) Notice(why);
        Build();
    }

    private static void Notice(string text)
    {
        var hud = Vanilla.Hud;
        if (hud && hud.Chat) Dev.DevChat.Notice(hud.Chat, text);
    }

    // ---- 役職辞典の頁の見出しに足すボタン ----

    internal static Widget DictionaryExtra(Registry.Section sec)
    {
        if (sec.Role is not RoleBase role || !BotPlay.CanUse || !BotPlay.InLobby) return null;
        bool picking = PickFor != 255 && BotPlay.IsOurs(PickFor) && PortKit.ById(PickFor);
        string text = picking
            ? new Text("BotPanel.GiveRole").With("name", PortKit.ById(PickFor).Data?.PlayerName ?? $"BOT {PickFor}")
            : new Text("BotPanel.AddThisRole");
        return new Button(text, () =>
        {
            if (picking) BotPlay.SetRole(PickFor, role);
            else
            {
                string why = BotPlay.Add(role);
                if (why != null) { Notice(why); return; }
            }
            PickFor = 255;
            Open();
        }, 1.35f) { Size = new Vector2(0f, 0.42f), MinSize = new Vector2(1.6f, 0.42f), Color = ModeColor };
    }

    // ---- 確かめ用 (bridge `botpanel`) ----

    internal static string Command(string[] a)
    {
        string op = a.Length > 1 ? a[1] : "";
        var hud = Vanilla.Hud;
        var t = hud && hud.MatchInfoButton ? hud.MatchInfoButton.transform.Find(ButtonName) : null;
        switch (op)
        {
            case "click":
                if (!t) return "no button";
                t.GetComponent<PassiveButton>().OnClick.Invoke();
                return $"clicked open={IsOpen}";
            case "open": Open(); return $"open={IsOpen}";
            case "close": if (IsOpen) UiWindow.Current.Close(); return "closed";
            case "ensure": Ensure(); break;
            case "pick" when a.Length > 2 && byte.TryParse(a[2], out byte id): PickFor = id; RoleDictionary.Open(null); return $"picking for {id}";
        }
        return t ? $"button active={t.gameObject.activeInHierarchy} pos={t.position} icon={(_icon ? _icon.name : "-")} open={IsOpen}" : $"no button icon={(_icon ? _icon.name : "-")} shown={Shown()}";
    }
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
internal static class BotPanelPatch
{
    public static void Postfix()
    {
        try { BotPanel.Ensure(); }
        catch (Exception e) { Plugin.Logger.LogError($"bot button: {e}"); }
    }
}
