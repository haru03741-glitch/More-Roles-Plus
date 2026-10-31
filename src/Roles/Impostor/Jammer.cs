// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Jammer.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// ジャマー (インポスター): 「透明化」で近くの人をしばらく他の人から見えなくする (本人とインポスターには見える)
public sealed class Jammer : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Jammer.Name");
    public override string Reading => "じゃまー";
    public override Text Blurb => new("Jammer.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Jammer.Jam", "snr_invisible") };
    public static readonly FloatOpt JamCooldown = new("Jammer.Cooldown", 30f, 2.5f, 120f, 2.5f, "s");
    public static readonly FloatOpt JamDuration = new("Jammer.Duration", 10f, 2.5f, 60f, 2.5f, "s");

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Jammer.Jam"), "snr_invisible")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => JamCooldown,
            OnUsed = (_, target) =>
            {
                Show(target, false);
                var t = target;
                PortKit.After(JamDuration, () => Show(t, true));
            },
        });
    }

    private void OnMeetingStart(MeetingStartEvent e)
    {
        foreach (var p in PlayerControl.AllPlayerControls) if (p) Show(p, true);
    }

    // 全員の端末で: 見えなくする / 戻す。本人・インポスター・幽霊には見えたまま
    private static void Show(PlayerControl p, bool visible)
    {
        if (!p || !p.cosmetics) return;
        var lp = PlayerControl.LocalPlayer;
        if (!visible && (p.AmOwner || PortKit.IsImpostor(lp) || (lp && lp.Data != null && lp.Data.IsDead))) return;
        float a = visible ? 1f : 0f;
        foreach (var r in p.cosmetics.GetComponentsInChildren<SpriteRenderer>(true))
        {
            var c = r.color;
            r.color = new Color(c.r, c.g, c.b, a);
        }
        foreach (var t in p.cosmetics.GetComponentsInChildren<TMPro.TextMeshPro>(true)) t.alpha = a;
    }
}
