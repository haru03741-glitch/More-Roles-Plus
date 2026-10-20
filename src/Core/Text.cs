using System.Text;
using AmongUs.Data;

namespace MoreRolesPlus;

// 画面に出す文字。文そのものは Resources/Lang/<言語>.dat に「鍵: 文」で書き、コードは鍵だけを持つ。
// 遊ぶ人はゲームのフォルダの MRP_Language/<言語>.dat を書き換えて好きな文にできる (LangFiles)。
//   new Text("Hawkeye.Name")
//   new Text("AfkGuard.IsAFK").With("name", name)   // 文の中の {name} に入る
public readonly struct Text
{
    public readonly string Key;
    private readonly object[] _fill; // 名前, 値, 名前, 値 …
    private readonly string _raw;    // 鍵の無い出来上がりの文 (本編から取った名前など)

    public Text(string key)
    {
        Key = key;
        _fill = null;
        _raw = null;
    }

    private Text(string key, object[] fill, string raw)
    {
        Key = key;
        _fill = fill;
        _raw = raw;
    }

    // 文のファイルを通さずにそのまま出す文
    public static Text Raw(string s) => new(null, null, s ?? "");

    // 文の中の {name} に入れる値。値が Text ならその時の言語の文になる
    public Text With(string name, object value)
    {
        int n = _fill?.Length ?? 0;
        var fill = new object[n + 2];
        if (n > 0) System.Array.Copy(_fill, fill, n);
        fill[n] = name;
        fill[n + 1] = value;
        return new Text(Key, fill, _raw);
    }

    public override string ToString() => _raw ?? Fill(Tr.Get(Key));

    // 組み込みの日本語 / 英語の文 (検索や名前で引く時に、上書きした文と並べて使う)
    public string Builtin(bool japanese) => _raw ?? Fill(Tr.Builtin(Key, japanese));

    // 名前で引く時の一致 (Id・組み込みの日本語と英語・書き換えた今の文のどれか)
    public bool Matches(string s, string id = null)
    {
        if (string.IsNullOrEmpty(s)) return false;
        return (id != null && string.Equals(id, s, System.StringComparison.OrdinalIgnoreCase))
            || string.Equals(Builtin(true), s, System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(Builtin(false), s, System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(ToString(), s, System.StringComparison.OrdinalIgnoreCase);
    }

    private string Fill(string s)
    {
        if (_fill == null) return s;
        var sb = new StringBuilder(s);
        for (int i = 0; i + 1 < _fill.Length; i += 2)
            sb.Replace("{" + _fill[i] + "}", _fill[i + 1]?.ToString() ?? "");
        return sb.ToString();
    }

    public static implicit operator string(Text t) => t.ToString();
}

internal static class Lang
{
    // 言語設定の読み出しはゲーム側の呼び出しになるので、メニューや試合の始まり・言語を変えた時にだけ読み直す
    public static SupportedLangs Current { get; private set; } = SupportedLangs.English;
    public static bool IsJapanese => Current == SupportedLangs.Japanese;

    public static void Refresh()
    {
        SupportedLangs now;
        try { now = DataManager.Settings.Language.CurrentLanguage; }
        catch { return; /* 設定がまだ読めない起動直後は前の値のまま */ }
        if (now == Current && Tr.Loaded) return;
        Current = now;
        Tr.Load(now);
    }

    // 本編が言語を切り替えた時 (設定の画面)
    public static void Use(SupportedLangs lang)
    {
        if (lang == Current && Tr.Loaded) return;
        Current = lang;
        Tr.Load(lang);
    }

    // 文のファイルを書き換えた後に読み直す
    public static void Reload()
    {
        Refresh();
        Tr.Load(Current);
    }
}
