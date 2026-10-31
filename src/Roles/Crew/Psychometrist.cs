// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Psychometrist.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// サイコメトラー (クルー): 死体に「よみとり」をすると、死因と何秒前に死んだかが (誤差つきで) 分かる
public sealed class Psychometrist : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#9370DB";
    public override Text Name => new("Psychometrist.Name");
    public override string Reading => "さいこめとらー";
    public override Text Blurb => new("Psychometrist.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Psychometrist.Read", "snr_read") };
    public static readonly FloatOpt ReadCooldown = new("Psychometrist.Cooldown", 20f, 0f, 60f, 2.5f, "s");
    public static readonly IntOpt Error = new("Psychometrist.Error", 5, 0, 30, 1, "s");

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Psychometrist.Read"), "snr_read")
        {
            Cooldown = () => ReadCooldown,
            OnUse = () =>
            {
                var body = PortKit.NearestBody(Player.GetTruePosition(), 1.5f);
                if (!body) return false;
                var d = MatchLog.Of(body.ParentId);
                var dead = PortKit.ById(body.ParentId);
                if (d == null || !dead) return false;
                int ago = Mathf.Max(0, Mathf.RoundToInt(Time.time - d.At + Random.Range(-Error.Value, Error.Value + 1)));
                var cause = new Text(d.Kind == MatchLog.Kind.Suicide ? "Psychometrist.Suicide" : "Psychometrist.Killed");
                PortKit.Notice(new Text("Psychometrist.Result").With("name", dead.Data.PlayerName).With("cause", cause.ToString()).With("s", ago).ToString());
                return true;
            },
        });
    }
}
