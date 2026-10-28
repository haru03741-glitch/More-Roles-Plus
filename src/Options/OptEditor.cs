// Based on https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Configuration/ConfigurationVariations.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using MoreRolesPlus.UI;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.Options;

// 設定項目 1 つの編集の行 (「名前 : 値」と -/+、ON/OFF は押すと切り替わる箱)。
// 設定画面の役職の頁と、役職辞典の中の編集で同じ物を使う (値は Opt の 1 か所だけ)。
// 値を変えた時は字と縁の色だけ書き換え、行を作り直さない。出る行が変わった時だけ onShownChanged を呼ぶ。
internal sealed class OptEditor
{
    public static readonly Color OnColor = new(0.35f, 1f, 0.45f, 1f);
    public static readonly Color OffColor = new(1f, 0.38f, 0.38f, 1f);
    public static readonly Color Dim = new(0.45f, 0.45f, 0.48f, 1f);
    // 既定から変えた値の字
    public static readonly Color ChangedColor = new(1f, 0.72f, 0.3f, 1f);
    public const float NameW = 2.72f, ValueW = 1.25f, FontSize = 1.7f, StepW = 0.42f, RowH = 0.4f, ResetW = 0.6f;
    // ± を押し続けた時に 1 つ進める間隔 (秒)
    private const float RepeatSeconds = 0.12f;

    private readonly List<(Opt opt, Label name, Label value, Button toggle, Button minus, Button plus, Button reset)> _rows = new();

    // 項目の名前の右の小さい点 (初期値から変えた項目・値の字は白のまま)
    internal static readonly string ChangedMark = $"<size=60%> <color=#{ColorUtility.ToHtmlStringRGB(ChangeDot.Color)}>●</color></size>";

    private static string NameText(Opt opt) => opt.Changed && !opt.NoChangeMark ? opt.Label + ChangedMark : opt.Label;
    private readonly List<Opt> _watched = new();
    private bool[] _shown;
    private readonly Action _onShownChanged;
    private readonly Action<Opt> _onChanged;

    // canEdit = 押した時に、まだ値を変えてよいか (開いた後に試合が始まった時など)
    private readonly Func<bool> _canEdit;

    public OptEditor(Action onShownChanged, Action<Opt> onChanged = null, Func<bool> canEdit = null)
    {
        _onShownChanged = onShownChanged;
        _onChanged = onChanged;
        _canEdit = canEdit;
    }

    // 出る行が変わったかを見張る項目 (並べる候補を全部)。Rows を組む前に渡す
    public void Watch(IEnumerable<Opt> opts)
    {
        _watched.Clear();
        _watched.AddRange(opts);
        _shown = new bool[_watched.Count];
        for (int i = 0; i < _watched.Count; i++) _shown[i] = _watched[i].Shown;
    }

    public void Clear() => _rows.Clear();

    // 値を 1 つ動かす (設定画面の本編の行と同じ後始末)
    public static void Step(Opt opt, int delta)
    {
        opt.Step(delta);
        AfterStep(opt);
    }

    // 押した以外の道 (既定へ戻す・取り消し) で値を変えた後も同じ後始末
    public static void AfterStep(Opt opt)
    {
        Roles.TeamSizes.OnEdited(opt);
        SettingsPages.RefreshMarks();
    }

    // 狭い欄 (設定画面の右の欄) の行: 字を小さく・「初期値」のボタンは無し (変えた項目は名前の右の点で分かり、戻すのは欄の下のボタン)。
    // 名前が 1 行に入らない時は、名前を上の段 (幅いっぱい)・値を下の段の右に置く
    public const float NarrowFont = 1.35f, NarrowStepW = 0.3f, NarrowValueW = 0.74f, NarrowRowH = 0.34f;

    // compactToggle = ON/OFF の箱を数値の値の欄と同じ幅に (名前を 1 行に収めやすくする)
    public Widget NarrowRow(Opt opt, bool editable, float width, float indent = 0f, Color? nameColor = null, bool compactToggle = false)
    {
        opt.Sync();
        var info = InfoButton(opt);
        const float gap = 0.06f;
        float controls = compactToggle && opt.IsToggle ? NarrowValueW + 0.12f : NarrowStepW * 2f + NarrowValueW + gap * 2f;
        float infoW = info != null ? InfoW + gap : 0f;
        float nameW = width - indent - controls - gap - infoW;
        bool twoLines = Label.Measure(opt.Label, NarrowFont, true) > nameW;
        if (twoLines) nameW = width - indent - infoW;
        var name = new Label(NameText(opt), NarrowFont) { Width = nameW, NoWrap = !twoLines, Bold = true, Color = nameColor ?? Color.white };
        var head = new Stack(true) { Gap = gap, Align = Align.Start };
        if (indent > 0f) head.Add(new Spacer(indent - gap, 0.1f));
        head.Add(name);
        if (info != null) head.Add(info);
        // 値の部品を足す先 (1 行なら名前の右・2 段なら下の段の右寄せ)
        Widget row = head;
        var ctl = head;
        if (twoLines)
        {
            ctl = new Stack(true) { Gap = gap, Align = Align.Start };
            ctl.Add(new Spacer(width - controls - gap, 0.1f));
            row = new Stack(false, head, ctl) { Gap = 0.03f, Width = width, Align = Align.Start };
        }

        if (opt.IsToggle)
        {
            var value = new Label(opt.Display(), NarrowFont) { Width = controls - 0.2f, TextAlign = TextAlignmentOptions.Center, Bold = true, Color = ToggleColor(opt) };
            Button toggle = null;
            if (editable)
            {
                toggle = new Button(value, () => Changed(opt, 1)) { Size = new Vector2(controls, NarrowRowH), Color = ToggleColor(opt) };
                ctl.Add(toggle);
            }
            else ctl.Add(value);
            _rows.Add((opt, name, value, toggle, null, null, null));
            return row;
        }

        var val = new Label(opt.Display(), NarrowFont) { Width = editable ? NarrowValueW : controls, TextAlign = TextAlignmentOptions.Center, Bold = true, Color = Color.white };
        if (!editable)
        {
            ctl.Add(val);
            _rows.Add((opt, name, val, null, null, null, null));
            return row;
        }
        var minus = new Button("-", () => Changed(opt, -1), NarrowFont) { Size = new Vector2(NarrowStepW, NarrowRowH), Repeat = RepeatSeconds };
        var plus = new Button("+", () => Changed(opt, 1), NarrowFont) { Size = new Vector2(NarrowStepW, NarrowRowH), Repeat = RepeatSeconds };
        ctl.Add(minus).Add(val).Add(plus);
        _rows.Add((opt, name, val, null, minus, plus, null));
        return row;
    }

    public Widget Row(Opt opt, bool editable, float indent = 0f, Color? nameColor = null)
    {
        opt.Sync();
        var info = InfoButton(opt);
        float nameW = NameW - indent - (info != null ? InfoW + 0.08f : 0f);
        var name = new Label(NameText(opt), FontSize) { Width = nameW, Bold = true, Color = nameColor ?? Color.white };
        var colon = new Label(":", FontSize) { Width = 0.2f, TextAlign = TextAlignmentOptions.Center };
        var row = new Stack(true) { Gap = 0.08f, Align = Align.Start };
        if (indent > 0f) row.Add(new Spacer(indent, 0.1f));
        row.Add(name);
        if (info != null) row.Add(info);
        row.Add(colon);

        if (opt.IsToggle)
        {
            var value = new Label(opt.Display(), FontSize) { Width = ValueW - 0.2f, TextAlign = TextAlignmentOptions.Center, Bold = true, Color = ToggleColor(opt) };
            Button toggle = null, treset = null;
            if (editable)
            {
                toggle = new Button(value, () => Changed(opt, 1)) { Size = new Vector2(ValueW, RowH), Color = ToggleColor(opt) };
                row.Add(toggle);
                treset = ResetButton(opt);
                // 「初期値」を数値の行 (- 値 +) と同じ列に置く
                if (treset != null) row.Add(new Spacer(StepW * 2f + 0.08f, 0.1f)).Add(treset);
            }
            else row.Add(value);
            _rows.Add((opt, name, value, toggle, null, null, treset));
            return row;
        }

        var val = new Label(opt.Display(), FontSize) { Width = ValueW, TextAlign = TextAlignmentOptions.Center, Bold = true, Color = Color.white };
        if (!editable)
        {
            row.Add(val);
            _rows.Add((opt, name, val, null, null, null, null));
            return row;
        }
        var minus = new Button("-", () => Changed(opt, -1), FontSize) { Size = new Vector2(StepW, RowH - 0.04f), Repeat = RepeatSeconds };
        var plus = new Button("+", () => Changed(opt, 1), FontSize) { Size = new Vector2(StepW, RowH - 0.04f), Repeat = RepeatSeconds };
        row.Add(minus).Add(val).Add(plus);
        var reset = ResetButton(opt);
        if (reset != null) row.Add(reset);
        _rows.Add((opt, name, val, null, minus, plus, reset));
        return row;
    }

    private const float InfoW = 0.3f;
    private static readonly bool Touch = OperatingSystem.IsAndroid();

    // 項目の説明 (文の鍵 <名前の鍵>.Detail) がある時だけ、名前の後ろに「?」の箱。PC は当てると・スマホは押すと札が出る
    private static Button InfoButton(Opt opt)
    {
        string key = opt.Label.Key;
        if (key == null || !Tr.Has(key + ".Detail")) return null;
        var detail = new Text(key + ".Detail");
        Button b = null;
        b = new Button("?", () =>
        {
            if (!Touch) return;
            var t = b.BackRenderer ? b.BackRenderer.transform : null;
            if (Tooltip.ShownFor(t)) Tooltip.Hide();
            else Tooltip.Show(t, detail);
        }, 1.2f)
        {
            Size = new Vector2(InfoW, InfoW),
            Padding = Vector2.zero,
            Color = InfoColor,
            OnHover = () => { if (!Touch && b.BackRenderer) Tooltip.Show(b.BackRenderer.transform, detail); },
            OnUnhover = () => { if (!Touch) Tooltip.Hide(); },
        };
        return b;
    }

    private static readonly Color InfoColor = new(0.55f, 0.8f, 1f, 1f);

    // 既定へ戻すボタン (既定の時は暗く、押しても何もしない)。既定を持たない項目には付けない
    private Button ResetButton(Opt opt) =>
        opt.HasDefault ? new Button(new Text("OptEditor.Reset"), () => Reset(opt), 1.15f) { Size = new Vector2(ResetW, RowH - 0.04f), Color = opt.Changed ? ChangedColor : Dim } : null;

    private void Reset(Opt opt)
    {
        if (_canEdit != null && !_canEdit()) return;
        int before = opt.Index;
        if (!opt.ResetToDefault()) return;
        OptHistory.Push(opt, before);
        Edited(opt);
    }

    private static Color ToggleColor(Opt opt) => opt.Index != 0 ? OnColor : OffColor;

    private void Changed(Opt opt, int delta)
    {
        if (_canEdit != null && !_canEdit()) return;
        opt.Sync();
        int before = opt.Index;
        Step(opt, delta);
        if (opt.Index != before) OptHistory.Push(opt, before);
        Edited(opt);
    }

    private void Edited(Opt opt)
    {
        _onChanged?.Invoke(opt);
        if (ShownChanged()) { _onShownChanged?.Invoke(); return; }
        Refresh();
    }

    internal bool ShownChangedForTest() => ShownChanged();

    private bool ShownChanged()
    {
        if (_shown == null) return false;
        bool changed = false;
        for (int i = 0; i < _watched.Count; i++)
        {
            bool s = _watched[i].Shown;
            if (s != _shown[i]) changed = true;
            _shown[i] = s;
        }
        return changed;
    }

    // 並んでいる行の字を今の値に (外から値を変えた後も)
    public void Refresh()
    {
        foreach (var (opt, name, value, toggle, minus, plus, reset) in _rows)
        {
            opt.Sync();
            bool changed = opt.Changed;
            reset?.SetColor(changed ? ChangedColor : Dim);
            var nt = name.Built;
            string ns = NameText(opt);
            if (nt && ns != name.Content) { name.Content = ns; nt.text = ns; }
            var t = value.Built;
            if (t) t.text = opt.Display();
            if (opt.IsToggle)
            {
                if (t) t.color = ToggleColor(opt);
                toggle?.SetColor(ToggleColor(opt));
                continue;
            }
            // 数値は端で止まるので、それ以上動かない側を暗くする
            minus?.SetColor(opt.WrapsAround || opt.Index > 0 ? Color.white : Dim);
            plus?.SetColor(opt.WrapsAround || opt.Index < opt.Count - 1 ? Color.white : Dim);
        }
    }
}

// 設定の値の取り消しの履歴。設定画面と役職辞典は同じ項目を編集するので 1 か所に持つ。
// 1 回の操作 (行の 1 押し・まとめて変える・役職ごとに既定へ戻す) が 1 段
internal static class OptHistory
{
    private const int Max = 50;
    private static readonly List<List<(Opt opt, int index)>> Steps = new();
    private static List<(Opt opt, int index)> _group;

    public static bool Any => Steps.Count > 0;

    public static void Clear()
    {
        Steps.Clear();
        _group = null;
        _lastAt = -100f;
    }

    // 変える前の番号を積む。BeginGroup と EndGroup の間は 1 段にまとめる
    public static void Push(Opt opt, int before)
    {
        if (_group != null) { _group.Add((opt, before)); return; }
        // 同じ項目を続けて動かした分 (± の長押し・連打) は 1 段にまとめ、最初の値へ戻す
        float now = UnityEngine.Time.unscaledTime;
        bool same = Steps.Count > 0 && Steps[^1].Count == 1 && Steps[^1][0].opt == opt && now - _lastAt < MergeSeconds;
        _lastAt = now;
        if (same) return;
        Add(new List<(Opt, int)> { (opt, before) });
    }

    private const float MergeSeconds = 0.6f;
    private static float _lastAt = -100f;

    public static void BeginGroup() => _group = new List<(Opt, int)>();

    public static void EndGroup()
    {
        var g = _group;
        _group = null;
        if (g != null && g.Count > 0) Add(g);
    }

    private static void Add(List<(Opt, int)> step)
    {
        if (Steps.Count >= Max) Steps.RemoveAt(0);
        Steps.Add(step);
    }

    // 最後の 1 段を戻す。戻した項目を返す (無ければ空)
    public static List<Opt> Undo()
    {
        var done = new List<Opt>();
        if (Steps.Count == 0) return done;
        var step = Steps[^1];
        Steps.RemoveAt(Steps.Count - 1);
        _lastAt = -100f;
        for (int i = step.Count - 1; i >= 0; i--)
        {
            var (opt, index) = step[i];
            if (opt.Restore(index)) OptEditor.AfterStep(opt);
            done.Add(opt);
        }
        return done;
    }
}
