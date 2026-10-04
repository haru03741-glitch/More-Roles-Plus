using System;

namespace MoreRolesPlus.Options;

// 役職以外の設定のまとまり。static class に付けると、その static readonly な設定項目が
// 指定したタブに見出し付きで並ぶ。
//   [Settings(Tab.General, "役職", "Roles")]
//   public static class RoleSettings { public static readonly BoolOpt Enabled = new("役職を使う", "Use roles", true); }
[AttributeUsage(AttributeTargets.Class)]
public sealed class SettingsAttribute : Attribute
{
    public readonly Tab Tab;
    public readonly string Ja, En;
    public readonly int Order;

    public SettingsAttribute(Tab tab, string ja, string en, int order = 0)
    {
        Tab = tab;
        Ja = ja;
        En = en;
        Order = order;
    }
}
