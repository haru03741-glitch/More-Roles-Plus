// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Moving.cs, Roles/Ability/MovingAbility.cs (GPL-3.0)
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ムービング (クルー): 1 回目に押した場所を覚え、2 回目に押すとそこへ飛ぶ
public sealed class Moving : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#00FF00";
    public override Text Name => new("Moving.Name");
    public override string Reading => "むーびんぐ";
    public override Text Blurb => new("Moving.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Moving.Move", "snr_move") };

    public static readonly FloatOpt MoveCooldown = new("Moving.Cooldown", 45f, 5f, 120f, 5f, "s");

    private Vector2? _saved;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Moving.Move"), "snr_move")
        {
            Cooldown = () => MoveCooldown,
            OnUse = () =>
            {
                var lp = PlayerControl.LocalPlayer;
                if (_saved is not Vector2 to)
                {
                    _saved = (Vector2)lp.transform.position;
                    StatusEffects.ShowUntil("Moving", new Text("Moving.Saved"), Color, new Text("Moving.UntilMove"));
                    return false; // 覚えるだけでは待ち時間を使わない
                }
                _saved = null;
                StatusEffects.Hide("Moving");
                Net.Teleport.Self(to);
                return true;
            },
        });
    }
}
