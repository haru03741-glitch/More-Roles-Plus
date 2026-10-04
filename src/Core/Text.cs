using AmongUs.Data;

namespace MoreRolesPlus;

// 画面に出す文字。日本語と英語を並べて書き、ゲームの言語設定で出し分ける。
//   new Text("保安官", "Sheriff")
public readonly struct Text
{
    public readonly string Ja;
    public readonly string En;

    public Text(string ja, string en)
    {
        Ja = ja;
        En = en;
    }

    public override string ToString() => Lang.IsJapanese ? Ja ?? En : En ?? Ja;

    public static implicit operator string(Text t) => t.ToString();
}

internal static class Lang
{
    // 言語設定の読み出しはゲーム側の呼び出しになるので、メニューや試合の始まりでだけ読み直す
    public static bool IsJapanese { get; private set; }

    public static void Refresh()
    {
        try { IsJapanese = DataManager.Settings.Language.CurrentLanguage == SupportedLangs.Japanese; }
        catch { /* 設定がまだ読めない起動直後は前の値のまま */ }
    }
}
