// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Amnesiac.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Neutral;

// 忘却者 (第三陣営): 勝ち方を持たない。死体を通報すると、その死体の人の役職になる
public sealed class Amnesiac : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#808080";
    public override Text Name => new("Amnesiac.Name");
    public override string Reading => "ぼうきゃくしゃ";
    public override Text Blurb => new("Amnesiac.Blurb");
    public override string Origin => "SNR";

    [HostOnly, OnlyMine]
    private void OnReport(MeetingCalledEvent e)
    {
        if (e.IsEmergency) return;
        var dead = RoleState.Of(e.Body.PlayerId);
        if (dead == null || dead is Amnesiac) return;
        var proto = Options.Registry.Roles.Find(r => r.Id == dead.Id);
        if (proto != null) RoleState.Change(Player, proto);
    }
}
