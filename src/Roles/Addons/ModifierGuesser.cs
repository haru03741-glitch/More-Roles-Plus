// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Modifiers/ModifierGuesser.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Addons;

// モディファイアゲッサー (アドオン): 元の役職に加えて、会議中に人の役職を当てて撃てる。外すと自分が死ぬ
public sealed class ModifierGuesser : AddonBase, IGuesser
{
    public override string Color => "#FFFF00";
    public override Text Name => new("ModifierGuesser.Name");
    public override string Reading => "もでぃふぁいあげっさー";
    public override Text Blurb => new("ModifierGuesser.Blurb");
    public override string Origin => "SNR";

    public static readonly IntOpt Shots = new("ModifierGuesser.Shots", 3, 1, 15);
    public static readonly IntOpt PerMeeting = new("ModifierGuesser.PerMeeting", 1, 1, 15);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("ModifierGuesser.Shoot"), "snr_guess")
        {
            Target = AbilityTarget.Meeting,
            MaxUses = () => Shots,
            UsesPerMeeting = PerMeeting,
            OnUsed = GuessKit.OnTarget,
        });
    }
}
