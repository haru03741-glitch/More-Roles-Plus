// Ported from https://github.com/yukinogatari/TheOtherRoles-GM TheOtherRoles/Roles/Watcher.cs, RoleInfo.cs, Patches/MeetingPatch.cs (GPL-3.0)
namespace MoreRolesPlus.Roles.Impostor;

// いびるうぉっちゃー: 匿名投票でも、誰が誰に入れたかが見える (TOR-GM のウォッチャーは、クルーならナイス・インポスターならイビル)
public sealed class EvilWatcher : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("EvilWatcher.Name");
    public override string Reading => "いびるうぉっちゃー";
    public override Text Blurb => new("EvilWatcher.Blurb");
    public override string Origin => "TORGM";

    internal static bool Has(PlayerControl p) => RoleState.Of(p) is EvilWatcher;
}
