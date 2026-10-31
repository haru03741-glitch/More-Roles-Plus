// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/ShermansServant.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// 式神 (第三陣営): 陰陽師に変えられた人。陰陽師が勝つと一緒に勝つ。しばらく陰陽師の姿になれ、自決もできる。
// 最初から配られることはない
public sealed class ShermansServant : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#FFE4B5";
    public override Text Name => new("ShermansServant.Name");
    public override string Reading => "しきがみ";
    public override Text Blurb => new("ShermansServant.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("ShermansServant.Morph", "snr_morph"), new("ShermansServant.Suicide", "snr_suicide") };
    public override bool MadeByOthers => true;

    public static readonly FloatOpt MorphCooldown = new("ShermansServant.Cooldown", 30f, 2.5f, 120f, 2.5f, "s");
    public static readonly FloatOpt MorphDuration = new("ShermansServant.Duration", 15f, 2.5f, 60f, 2.5f, "s");

    // 式神 → 陰陽師 (全員の端末で持つ)
    internal static readonly Dictionary<byte, byte> Master = new();
    public byte MasterId => Master.TryGetValue(PlayerId, out var m) ? m : (byte)255;

    private bool _morphed;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("ShermansServant.Morph"), "snr_morph")
        {
            Cooldown = () => MorphCooldown,
            Duration = () => MorphDuration,
            OnUse = () =>
            {
                var m = PortKit.ById(MasterId);
                if (!m) return false;
                _morphed = true;
                PortKit.Morph(m);
                return true;
            },
            OnEnd = Revert,
        });
        AddAbility(new Ability(new Text("ShermansServant.Suicide"), "snr_suicide")
        {
            OnUse = () => { PortKit.Suicide(); return true; },
        });
        Lifespan.OnRelease(Revert);
    }

    private void Revert()
    {
        if (!_morphed) return;
        _morphed = false;
        PortKit.Morph(null);
    }

    public override bool AlsoWins(GameResult result) => result.Winners.Contains(MasterId);

    public override string NameMark(PlayerControl seen) =>
        seen.PlayerId == MasterId ? "<color=#FFE4B5>主</color>" : null;
}
