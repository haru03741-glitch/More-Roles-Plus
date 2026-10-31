// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/BodyBuilder.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ボディービルダー (クルー): タスクを全部終えると「ポーズ」をとれる。ポーズの間は全員の画面で名前に 💪 が出る
public sealed class BodyBuilder : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#E06C3C";
    public override Text Name => new("BodyBuilder.Name");
    public override string Reading => "ぼでぃーびるだー";
    public override Text Blurb => new("BodyBuilder.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("BodyBuilder.Pose", "snr_pose") };
    public static readonly FloatOpt PoseCooldown = new("BodyBuilder.Cooldown", 10f, 0f, 60f, 2.5f, "s");

    private float _until;

    public override string PublicMark => Time.time < _until ? " <size=120%>💪</size>" : null;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("BodyBuilder.Pose"), "snr_pose")
        {
            Cooldown = () => PoseCooldown,
            OnUse = () =>
            {
                if (!PortKit.AllTasksDone(Player)) return false;
                PoseCall.Send(0);
                _until = Time.time + 3f;
                return true;
            },
        });
    }

    private static readonly RemoteCall<byte> PoseCall = new("BodyBuilder.Pose", Route.Anyone,
        (w, _) => w.Write((byte)0), r => r.ReadByte(),
        (sender, _) => { if (RoleState.Of(sender) is BodyBuilder b) b._until = Time.time + 3f; });
}
