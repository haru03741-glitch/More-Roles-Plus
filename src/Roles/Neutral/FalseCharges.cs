// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/FalseCharges.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// 冤罪師 (第三陣営): 1 度だけ「冤罪」で人を選ぶと自分は死ぬ。選んだ人が決まった会議の数以内に追放されると単独で勝つ
public sealed class FalseCharges : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#8B4513";
    public override Text Name => new("FalseCharges.Name");
    public override string Reading => "えんざいし";
    public override Text Blurb => new("FalseCharges.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("FalseCharges.Frame", "snr_frame") };
    public static readonly IntOpt Meetings = new("FalseCharges.Meetings", 2, 1, 10);

    private byte _framed = 255;
    private int _meetings;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("FalseCharges.Frame"), "snr_frame")
        {
            Target = AbilityTarget.Player,
            MaxUses = () => 1,
            OnUsed = (user, target) =>
            {
                _framed = target.PlayerId;
                if (AmongUsClient.Instance.AmHost) PortKit.HostSuicide(user);
            },
        });
    }

    [HostOnly]
    private void OnExiled(PlayerExiledEvent e)
    {
        if (_framed != 255 && e.Player && e.Player.PlayerId == _framed && _meetings < Meetings) GameEnd.Win(this);
    }

    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (_framed != 255) _meetings++;
    }

    public override string NameMark(PlayerControl seen) =>
        seen.PlayerId == _framed ? "<color=#8B4513>冤</color>" : null;
}
