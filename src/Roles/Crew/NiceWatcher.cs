// Ported from https://github.com/yukinogatari/TheOtherRoles-GM TheOtherRoles/Roles/Watcher.cs, RoleInfo.cs, Patches/MeetingPatch.cs (GPL-3.0)
namespace MoreRolesPlus.Roles.Crew;

// ないすうぉっちゃー: 匿名投票でも、誰が誰に入れたかが見える (TOR-GM のウォッチャーは、クルーならナイス・インポスターならイビル)
public sealed class NiceWatcher : RoleBase
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#800080";
    public override Text Name => new("NiceWatcher.Name");
    public override string Reading => "ないすうぉっちゃー";
    public override Text Blurb => new("NiceWatcher.Blurb");
    public override string Origin => "TORGM";

    internal static bool Has(PlayerControl p) => RoleState.Of(p) is NiceWatcher;
}
