// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/DyingMessenger.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// 遺言伝達者 (クルー): 死んでから決まった秒数のうちに死体を通報すると、倒した人の役職が分かる
public sealed class DyingMessenger : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#8B008B";
    public override Text Name => new("DyingMessenger.Name");
    public override string Reading => "ゆいごんでんたつしゃ";
    public override Text Blurb => new("DyingMessenger.Blurb");
    public override string Origin => "SNR";
    public static readonly FloatOpt WithinSeconds = new("DyingMessenger.Within", 15f, 2.5f, 60f, 2.5f, "s");

    [LocalOnly, OnlyMine]
    private void OnReport(MeetingCalledEvent e)
    {
        if (e.IsEmergency) return;
        var d = MatchLog.Of(e.Body.PlayerId);
        if (d == null || d.Kind != MatchLog.Kind.Killed || Time.time - d.At > WithinSeconds) return;
        var killerRole = RoleState.Of(d.KillerId);
        if (killerRole == null) return;
        PortKit.Notice(new Text("DyingMessenger.Message").With("name", e.Body.PlayerName).With("role", $"<color={killerRole.Color}>{killerRole.Name}</color>").ToString());
    }
}
