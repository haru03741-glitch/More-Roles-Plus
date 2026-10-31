// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/PoliceSurgeon.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// 警察医 (クルー): 会議が始まると、前の会議から後に倒された人が何秒前に死んだかが (誤差つきで) 分かる
public sealed class PoliceSurgeon : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#4169E1";
    public override Text Name => new("PoliceSurgeon.Name");
    public override string Reading => "けいさつい";
    public override Text Blurb => new("PoliceSurgeon.Blurb");
    public override string Origin => "SNR";
    public static readonly IntOpt Error = new("PoliceSurgeon.Error", 5, 0, 30, 1, "s");

    private float _since;

    public override void OnAssigned() => _since = Time.time;

    [LocalOnly]
    private void OnMeetingStart(MeetingStartEvent e)
    {
        if (!PortKit.Alive(Player)) return;
        var sb = new System.Text.StringBuilder();
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            var d = MatchLog.Of(p.PlayerId);
            if (d == null || d.Kind != MatchLog.Kind.Killed || d.At < _since) continue;
            int ago = Mathf.Max(0, Mathf.RoundToInt(Time.time - d.At + Random.Range(-Error.Value, Error.Value + 1)));
            sb.Append('\n').Append(new Text("PoliceSurgeon.Line").With("name", p.Data.PlayerName).With("s", ago));
        }
        _since = Time.time;
        if (sb.Length > 0) PortKit.Notice(new Text("PoliceSurgeon.Title") + sb.ToString());
    }
}
