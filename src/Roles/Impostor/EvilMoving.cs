// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/EvilMoving.cs, Roles/Ability/MovingAbility.cs (GPL-3.0)
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// イビルムービング (インポスター): 1 回目に押した場所を覚え、2 回目に押すとそこへ飛ぶ
public sealed class EvilMoving : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("EvilMoving.Name");
    public override string Reading => "いびるむーびんぐ";
    public override Text Blurb => new("EvilMoving.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("EvilMoving.Move", "snr_move") };

    public static readonly FloatOpt MoveCooldown = new("EvilMoving.Cooldown", 45f, 5f, 120f, 5f, "s");

    private Vector2? _saved;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("EvilMoving.Move"), "snr_move")
        {
            Cooldown = () => MoveCooldown,
            OnUse = () =>
            {
                var lp = PlayerControl.LocalPlayer;
                if (_saved is not Vector2 to)
                {
                    _saved = (Vector2)lp.transform.position;
                    StatusEffects.ShowUntil("EvilMoving", new Text("EvilMoving.Saved"), Color, new Text("EvilMoving.UntilMove"));
                    return false; // 覚えるだけでは待ち時間を使わない
                }
                _saved = null;
                StatusEffects.Hide("EvilMoving");
                Net.Teleport.Self(to);
                return true;
            },
        });
    }
}
