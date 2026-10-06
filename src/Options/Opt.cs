using System;
using System.Globalization;

namespace MoreRolesPlus.Options;

// 設定画面のどのタブに並ぶか
public enum Tab
{
    General,
    Crew,
    Impostor,
    Neutral,
}

// 設定項目の共通部分。値は「選択肢の何番目か」(Index) で持ち、保存・同期もこの番号で行う。
// 役職クラスや [Settings] クラスの static readonly フィールドに置くと、名前 (Key) は
// 「役職名.フィールド名」で自動的に付き、設定画面に並ぶ。番号を手で振る必要はない。
public abstract class Opt
{
    public readonly Text Label;
    public string Key { get; internal set; }

    internal int Index;
    internal readonly int DefaultIndex;
    internal abstract int Count { get; }

    protected Opt(Text label, int defaultIndex)
    {
        Label = label;
        DefaultIndex = defaultIndex;
        Index = defaultIndex;
    }

    // 設定画面の値の欄に出す文字
    internal abstract string Display();

    // 左右のボタン。数値は端で止まる (押しすぎて反対の端へ飛ばないように)。ON/OFF と文字の選択肢は回る
    internal void Step(int delta)
    {
        int n = Count;
        Index = Wraps ? ((Index + delta) % n + n) % n : Math.Clamp(Index + delta, 0, n - 1);
    }

    protected virtual bool Wraps => false;

    internal void SetIndex(int index)
    {
        if (index >= 0 && index < Count) Index = index;
    }

    // 保存は番号でなく値で書く (範囲や刻みを後で変えても、近い値に戻るように)
    internal virtual string Save() => Index.ToString(CultureInfo.InvariantCulture);
    internal virtual void Load(string s)
    {
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) SetIndex(i);
    }
}

public sealed class BoolOpt : Opt
{
    public BoolOpt(string ja, string en, bool defaultValue) : base(new Text(ja, en), defaultValue ? 1 : 0) { }

    public bool Value => Index != 0;
    internal override int Count => 2;
    protected override bool Wraps => true;
    internal override string Display() => Value ? "ON" : "OFF";

    public static implicit operator bool(BoolOpt o) => o.Value;
}

public sealed class IntOpt : Opt
{
    public readonly int Min, Max, StepSize;
    public readonly string Suffix;

    public IntOpt(string ja, string en, int defaultValue, int min, int max, int step = 1, string suffix = "")
        : base(new Text(ja, en), Math.Clamp((defaultValue - min) / Math.Max(step, 1), 0, (max - min) / Math.Max(step, 1)))
    {
        Min = min;
        Max = max;
        StepSize = Math.Max(step, 1);
        Suffix = suffix;
    }

    public int Value => Min + Index * StepSize;
    internal override int Count => (Max - Min) / StepSize + 1;
    internal override string Display() => Value.ToString(CultureInfo.InvariantCulture) + Suffix;

    internal override string Save() => Value.ToString(CultureInfo.InvariantCulture);
    internal override void Load(string s)
    {
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            Index = Math.Clamp((int)Math.Round((v - Min) / (double)StepSize), 0, Count - 1);
    }

    public static implicit operator int(IntOpt o) => o.Value;
}

public sealed class FloatOpt : Opt
{
    public readonly float Min, Max, StepSize;
    public readonly string Suffix;

    public FloatOpt(string ja, string en, float defaultValue, float min, float max, float step, string suffix = "")
        : base(new Text(ja, en), Math.Clamp((int)Math.Round((defaultValue - min) / step), 0, (int)Math.Round((max - min) / step)))
    {
        Min = min;
        Max = max;
        StepSize = step;
        Suffix = suffix;
    }

    public float Value => Min + Index * StepSize;
    internal override int Count => (int)Math.Round((Max - Min) / StepSize) + 1;
    internal override string Display() => Value.ToString("0.##", CultureInfo.InvariantCulture) + Suffix;

    internal override string Save() => Value.ToString("R", CultureInfo.InvariantCulture);
    internal override void Load(string s)
    {
        if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            Index = Math.Clamp((int)Math.Round((v - Min) / StepSize), 0, Count - 1);
    }

    public static implicit operator float(FloatOpt o) => o.Value;
}

// 文字の選択肢から 1 つ選ぶ。Value は選ばれた選択肢の番号 (0 から)
public sealed class ChoiceOpt : Opt
{
    private readonly Text[] _choices;

    public ChoiceOpt(string ja, string en, int defaultIndex, params Text[] choices)
        : base(new Text(ja, en), Math.Clamp(defaultIndex, 0, Math.Max(choices.Length - 1, 0)))
    {
        _choices = choices.Length > 0 ? choices : [new Text("-", "-")];
    }

    public int Value => Index;
    internal override int Count => _choices.Length;
    protected override bool Wraps => true;
    internal override string Display() => _choices[Index];

    public static implicit operator int(ChoiceOpt o) => o.Value;
}
