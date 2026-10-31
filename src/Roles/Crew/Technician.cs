// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Technician.cs (GPL-3.0)
namespace MoreRolesPlus.Roles.Crew;

// 技術者 (クルー): サボタージュが起きている間だけベントを使える
public sealed class Technician : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FF9900";
    public override Text Name => new("Technician.Name");
    public override string Reading => "ぎじゅつしゃ";
    public override Text Blurb => new("Technician.Blurb");
    public override string Origin => "SNR";

    private bool _sabotage;

    public override bool? CanVent => _sabotage;

    // サボタージュの直しはどの端末のタスク欄にも入るので、全員の端末で同じように切り替わる
    public override void OnFixedUpdate()
    {
        bool now = PortKit.AnySabotage();
        if (now == _sabotage) return;
        _sabotage = now;
        Rules.GameRules.ApplyRoleVent(this);
    }
}
