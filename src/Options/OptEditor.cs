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
    public const float NameW = 3.1f, ValueW = 1.25f, FontSize = 1.7f, StepW = 0.42f, RowH = 0.4f;

    private readonly List<(Opt opt, Label value, Button toggle, Button minus, Button plus)> _rows = new();
    private readonly List<Opt> _watched = new();
    private bool[] _shown;
    private readonly Action _onShownChanged;
    private readonly Action<Opt> _onChanged;

    public OptEditor(Action onShownChanged, Action<Opt> onChanged = null)
    {
        _onShownChanged = onShownChanged;
        _onChanged = onChanged;
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
        Roles.TeamSizes.OnEdited(opt);
        GameTabRows.OnEdited(opt);
    }

    public Widget Row(Opt opt, bool editable, float indent = 0f)
    {
        opt.Sync();
        var name = new Label(opt.Label, FontSize) { Width = NameW - indent, Bold = true };
        var colon = new Label(":", FontSize) { Width = 0.2f, TextAlign = TextAlignmentOptions.Center };
        var row = new Stack(true) { Gap = 0.08f, Align = Align.Start };
        if (indent > 0f) row.Add(new Spacer(indent, 0.1f));
        row.Add(name).Add(colon);

        if (opt.IsToggle)
        {
            var value = new Label(opt.Display(), FontSize) { Width = ValueW - 0.2f, TextAlign = TextAlignmentOptions.Center, Bold = true, Color = ToggleColor(opt) };
            Button toggle = null;
            if (editable)
            {
                toggle = new Button(value, () => Changed(opt, 1)) { Size = new Vector2(ValueW, RowH), Color = ToggleColor(opt) };
                row.Add(toggle);
            }
            else row.Add(value);
            _rows.Add((opt, value, toggle, null, null));
            return row;
        }

        var val = new Label(opt.Display(), FontSize) { Width = ValueW, TextAlign = TextAlignmentOptions.Center, Bold = true };
        if (!editable)
        {
            row.Add(val);
            _rows.Add((opt, val, null, null, null));
            return row;
        }
        var minus = new Button("-", () => Changed(opt, -1), FontSize) { Size = new Vector2(StepW, RowH - 0.04f) };
        var plus = new Button("+", () => Changed(opt, 1), FontSize) { Size = new Vector2(StepW, RowH - 0.04f) };
        row.Add(minus).Add(val).Add(plus);
        _rows.Add((opt, val, null, minus, plus));
        return row;
    }

    private static Color ToggleColor(Opt opt) => opt.Index != 0 ? OnColor : OffColor;

    private void Changed(Opt opt, int delta)
    {
        Step(opt, delta);
        _onChanged?.Invoke(opt);
        if (ShownChanged()) { _onShownChanged?.Invoke(); return; }
        Refresh();
    }

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
        foreach (var (opt, value, toggle, minus, plus) in _rows)
        {
            opt.Sync();
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
