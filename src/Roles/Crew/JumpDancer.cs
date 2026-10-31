// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/JumpDancer.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ジャンプダンサー (クルー): 「ジャンプ！」で自分と周りの人を (見た目だけ) ジャンプさせる
public sealed class JumpDancer : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FF69B4";
    public override Text Name => new("JumpDancer.Name");
    public override string Reading => "じゃんぷだんさー";
    public override Text Blurb => new("JumpDancer.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("JumpDancer.Jump", "snr_jump") };
    public static readonly FloatOpt JumpCooldown = new("JumpDancer.Cooldown", 10f, 0f, 60f, 2.5f, "s");
    public static readonly FloatOpt JumpRange = new("JumpDancer.Range", 3f, 1f, 10f, 0.5f, "m");

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("JumpDancer.Jump"), "snr_jump")
        {
            Cooldown = () => JumpCooldown,
            OnUse = () => { JumpCall.Send(0); Jump(Player); return true; },
        });
    }

    private static readonly RemoteCall<byte> JumpCall = new("JumpDancer.Jump", Route.Anyone,
        (w, _) => w.Write((byte)0), r => r.ReadByte(),
        (sender, _) => { if (RoleState.Of(sender) is JumpDancer) Jump(sender); });

    // 全員の端末で: 踊り手の周りの生きている人の見た目を少し持ち上げて戻す
    private static void Jump(PlayerControl dancer)
    {
        if (!dancer) return;
        Vector2 at = dancer.GetTruePosition();
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || !p.cosmetics || Vector2.Distance(at, p.GetTruePosition()) > JumpRange) continue;
            var t = p.cosmetics.transform;
            var pc = p;
            t.localPosition += new Vector3(0f, 0.35f, 0f);
            PortKit.After(0.3f, () => { if (pc && pc.cosmetics) pc.cosmetics.transform.localPosition -= new Vector3(0f, 0.35f, 0f); });
        }
    }
}
