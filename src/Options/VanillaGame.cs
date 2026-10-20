using System.Collections.Generic;
using MoreRolesPlus.Roles.Builtin;

namespace MoreRolesPlus.Options;

// 本編の「ゲーム設定」タブの項目 (インポスター数・キルのクールダウンなど) を、MRP の設定項目の形 (VanillaOpt) で持つ。
// 検索の結果とプリセットの見分けに使う。値は本編の設定にあり、保存と同期は本編の仕組みのまま。
// 見出しの並びは今のゲームモードの本編の一覧のとおり。設定画面を開くたびに作り直す (言語とゲームモードが変わりうるので)
internal static class VanillaGame
{
    internal sealed class Section
    {
        public Text Title;
        public readonly List<VanillaOpt> Opts = new();
    }

    public static readonly Text TabName = new("VanillaGame.TabName");

    private static readonly List<Section> Sections = new();

    public static IReadOnlyList<Section> All => Sections;

    public static void Rebuild()
    {
        Sections.Clear();
        var gm = GameManager.Instance;
        var list = gm ? gm.GameSettingsList : null;
        var cats = list ? list.AllCategories : null;
        if (cats == null) return;
        var tc = TranslationController.Instance;
        foreach (var cat in cats)
        {
            if (cat == null || cat.AllGameSettings == null) continue;
            string title = tc ? tc.GetString(cat.CategoryName) : cat.CategoryName.ToString();
            var sec = new Section { Title = Text.Raw(title) };
            foreach (var s in cat.AllGameSettings)
            {
                var opt = VanillaOpt.From(s);
                if (opt == null) continue;
                opt.Key = "vanilla." + s.Title;
                sec.Opts.Add(opt);
            }
            if (sec.Opts.Count > 0) Sections.Add(sec);
        }
    }
}
