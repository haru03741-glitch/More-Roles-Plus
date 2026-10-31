// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Sidekick.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Neutral;

// サイドキック (第三陣営): ジャッカルに仲間にされた人。キルはできず、ジャッカルと一緒に勝つ。
// ジャッカルが全員死ぬとジャッカルに上がる。最初から配られることはない
public sealed class Sidekick : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => Jackal.PackColor;
    public override Text Name => new("Sidekick.Name");
    public override string Reading => "さいどきっく";
    public override Text Blurb => new("Sidekick.Blurb");
    public override string Origin => "SNR";
    public override string Faction => Jackal.Pack;
    public override bool MadeByOthers => true;

    public static readonly BoolOpt Vent = new("Sidekick.CanVent", true);

    public override bool? CanVent => Vent;

    private void OnMurdered(PlayerMurderedEvent e) => Jackal.PromoteIfLeaderless();

    private void OnMeetingEnd(MeetingEndEvent e) => Jackal.PromoteIfLeaderless();

    public override string NameMark(PlayerControl seen) =>
        RoleState.Of(seen) is Jackal ? $"<color={Jackal.PackColor}>◆</color>" : null;
}
