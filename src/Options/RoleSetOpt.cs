using System;
using System.Text;

namespace MoreRolesPlus.Options;

// 役職の集まり。役職は Registry.Roles の並び (Id の文字順) の番号のビットで持つ (同期は項目ごとに int 1 つのため)。
// int 1 つで持てるのは Bits 個までなので、それより後ろの役職は隠れた項目 (RoleSetPart) に Bits 個ずつ分けて持つ。
// 保存は役職の Id の並びで書くので、役職が増えて並びが変わっても保存した値は崩れない。
// 画面には並べない (役職のタブが札で操作する)。空の間はロビーの一覧にも出さない
internal sealed class RoleSetOpt : Opt
{
    // int 1 つのビットで持てる役職の数 (Index は 0 以上なので符号のビットは使わない)
    internal const int Bits = 30;

    // Bits 番目以降の役職のビット。同期と元に戻すのためだけに Registry.All に入る
    internal readonly RoleSetPart[] Parts;

    public RoleSetOpt(string key, int roles) : base(new Text(key), 0)
    {
        OffTab = true;
        Parts = new RoleSetPart[Math.Max(0, (roles - 1) / Bits)];
        for (int i = 0; i < Parts.Length; i++) Parts[i] = new RoleSetPart(this, key);
    }

    // 持てる役職の数
    private int Capacity => Bits * (Parts.Length + 1);

    internal override int Count => 1 << Bits;
    internal override bool Blank
    {
        get
        {
            if (Index != 0) return false;
            foreach (var p in Parts) if (p.Index != 0) return false;
            return true;
        }
    }

    private Opt Word(int order) => order < Bits ? this : Parts[order / Bits - 1];

    internal bool Has(int order) => order >= 0 && order < Capacity && (Word(order).Index >> (order % Bits) & 1) != 0;

    internal void Set(int order, bool on)
    {
        if (order < 0 || order >= Capacity) return;
        var w = Word(order);
        int bit = 1 << (order % Bits);
        w.Index = on ? w.Index | bit : w.Index & ~bit;
    }

    // 値の欄 (ロビーの一覧では狭い) に収まるように、名前は先頭の 1 つだけ (残りは数で)
    internal override string Display()
    {
        if (Index == 0) return Tr.Get("RoleSetOpt.None");
        var me = Section?.Role;
        var picked = new System.Collections.Generic.List<string>();
        var rest = new System.Collections.Generic.List<string>();
        for (int i = 0; i < Registry.Roles.Count && i < Capacity; i++)
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
        for (int i = 0; i < Registry.Roles.Count && i < Capacity; i++)
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
        foreach (var p in Parts) p.Index = 0;
        foreach (string id in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Set(Registry.Roles.FindIndex(r => r.Id == id), true);
    }
}

// RoleSetOpt の Bits 番目以降のビット。画面・一覧・保存には出さず (保存は持ち主が Id の並びで書く)、
// 同期・元に戻す・変更の知らせでは持ち主と同じ項目として扱う
internal sealed class RoleSetPart : Opt
{
    internal readonly RoleSetOpt Owner;

    public RoleSetPart(RoleSetOpt owner, string key) : base(new Text(key), 0)
    {
        Owner = owner;
        OffTab = true;
    }

    internal override int Count => 1 << RoleSetOpt.Bits;
    internal override bool Blank => true;
    internal override string Display() => Owner.Display();
    internal override string Save() => "";
    internal override void Load(string s) { }
}
