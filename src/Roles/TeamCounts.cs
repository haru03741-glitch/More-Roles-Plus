using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles;

// 陣営ごとの最少〜最多のうち、クルーの役職 (素のクルーでない役職の付くクルー) とマッドメイトの分。
// インポスターは ImpostorRange と本編のインポスター数、第三陣営は NeutralKillSettings にある。
// 行は本編の「ゲーム設定」タブの「陣営の人数」の組にまとめて並べる (GameTabRows.Teams)。配り方 (AssignPlan) が使う
[Settings(Tab.General, "TeamCounts.Title", order: -98)]
public static class TeamCounts
{
    public static readonly IntOpt CrewMin = new("TeamCounts.CrewMin", 0, 0, Crowded.MaxPlayers) { OffTab = true };
    public static readonly IntOpt CrewMax = new("TeamCounts.CrewMax", Crowded.MaxPlayers, 0, Crowded.MaxPlayers) { OffTab = true, MaxLabel = new Text("TeamCounts.NoLimit") };
    public static readonly IntOpt MadMin = new("TeamCounts.MadMin", 0, 0, Crowded.MaxPlayers) { OffTab = true };
    public static readonly IntOpt MadMax = new("TeamCounts.MadMax", Crowded.MaxPlayers, 0, Crowded.MaxPlayers) { OffTab = true, MaxLabel = new Text("TeamCounts.NoLimit") };
}
