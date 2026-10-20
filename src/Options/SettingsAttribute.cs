using System;

namespace MoreRolesPlus.Options;

// 役職以外の設定のまとまり。static class に付けると、その static readonly な設定項目が
// 指定したタブに見出し付きで並ぶ。見出しと項目の名前は文のファイル (Resources/Lang) の鍵で書く。
//   [Settings(Tab.General, "RoleSettings.Title")]
//   public static class RoleSettings { public static readonly BoolOpt Enabled = new("RoleSettings.Enabled", true); }
[AttributeUsage(AttributeTargets.Class)]
public sealed class SettingsAttribute : Attribute
{
    public readonly Tab Tab;
    public readonly string Key;
    public readonly int Order;

    public SettingsAttribute(Tab tab, string key, int order = 0)
    {
        Tab = tab;
        Key = key;
        Order = order;
    }
}
