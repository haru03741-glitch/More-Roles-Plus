using System;
using System.Text;

namespace MoreRolesPlus.Options;

// 役職の集まり。役職は Registry.Roles の並び (Id の文字順) の番号のビットで持つ (同期は項目ごとに int 1 つのため)。
// 保存は役職の Id の並びで書くので、役職が増えて並びが変わっても保存した値は崩れない。
// 画面には並べない (役職のタブが札で操作する)。空の間はロビーの一覧にも出さない
internal sealed class RoleSetOpt : Opt
{
    // int のビットで持てる役職の数 (Index は 0 以上なので符号のビットは使わない)
    internal const int MaxRoles = 30;

    public RoleSetOpt(string key) : base(new Text(key), 0)
    {
        OffTab = true;
    }

    internal override int Count => 1 << MaxRoles;
    internal override bool Blank => Index == 0;

    internal bool Has(int order) => order >= 0 && order < MaxRoles && (Index >> order & 1) != 0;

    internal void Set(int order, bool on)
    {
        if (order < 0 || order >= MaxRoles) return;
        Index = on ? Index | 1 << order : Index & ~(1 << order);
    }

    // 値の欄 (ロビーの一覧では狭い) に収まるように、名前は先頭の 1 つだけ (残りは数で)
    internal override string Display()
    {
        if (Index == 0) return Tr.Get("RoleSetOpt.None");
        var me = Section?.Role;
        var picked = new System.Collections.Generic.List<string>();
        var rest = new System.Collections.Generic.List<string>();
        for (int i = 0; i < Registry.Roles.Count && i < MaxRoles; i++)
        {
            var r = Registry.Roles[i];
            if (Has(i)) picked.Add(r.Name.ToString());
            else if (r != me && r is Roles.RoleBase rb && rb.Excluded != null) rest.Add(r.Name.ToString());
        }
        return Summary(picked, rest, 1);
    }

    // 選んだ役職の要約。選んだ方と選ばなかった方の少ない方を名前で書く (「A, B を除く全部」)。
    // 名前は max 個まで・残りは数で
    internal static string Summary(System.Collections.Generic.List<string> picked, System.Collections.Generic.List<string> rest, int max)
    {
        if (picked.Count == 0) return Tr.Get("RoleSetOpt.None");
        if (rest.Count == 0) return Tr.Get("RoleSetOpt.All");
        bool except = rest.Count < picked.Count;
        var names = except ? rest : picked;
        var sb = new StringBuilder();
        int shown = Math.Min(max, names.Count);
        for (int i = 0; i < shown; i++)
        {
            if (i > 0) sb.Append(Tr.Get("RoleSetOpt.Sep"));
            sb.Append(names[i]);
        }
        string list = names.Count > shown ? new Text("RoleSetOpt.More").With("name", sb.ToString()).With("n", names.Count - shown).ToString() : sb.ToString();
        return except ? new Text("RoleSetOpt.Except").With("names", list).ToString() : list;
    }

    internal override string Save()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Registry.Roles.Count && i < MaxRoles; i++)
        {
            if (!Has(i)) continue;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(Registry.Roles[i].Id);
        }
        return sb.ToString();
    }

    internal override void Load(string s)
    {
        Index = 0;
        foreach (string id in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Set(Registry.Roles.FindIndex(r => r.Id == id), true);
    }
}
