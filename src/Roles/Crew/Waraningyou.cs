// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Crewmate/Waraningyou.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// わら人形 (クルー): 会議で自分に入った票 (自分の票は除く) を貯める。会議中に 1 回「呪う」と、
// その会議の自分の票が「貯めた数 + 1」票になり、貯めた数は 0 に戻る
public sealed class Waraningyou : RoleBase
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#A39570";
    public override Text Name => new("Waraningyou.Name");
    public override string Reading => "わらにんぎょう";
    public override Text Blurb => new("Waraningyou.Blurb");
    public override string Origin => "Uchu";
    public override bool CanBeMadmate => true;
    public override HelpSection[] HelpSections => new HelpSection[] { new("Waraningyou.Curse", "waraningyou_curse") };

    public static readonly IntOpt DefaultVote = new("Waraningyou.DefaultVote", 0, 0, 15);
    public static readonly BoolOpt AllowSkip = new("Waraningyou.AllowSkip", false);

    private int _stored = -1;   // 貯めた票 (全員の端末で同じ。-1 = まだ初期値を入れていない)
    private bool _cursed;       // この会議で呪った

    private int Stored => _stored < 0 ? DefaultVote : _stored;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Waraningyou.Curse"), "waraningyou_curse")
        {
            Target = AbilityTarget.Meeting,
            MeetingSelf = true,
            UsesPerMeeting = 1,
            OnUsed = (_, _) =>
            {
                _cursed = true;
                if (IsLocal) PortKit.Notice(new Text("Waraningyou.Cursed").With("n", Stored + 1).ToString());
            },
        });
    }

    private void OnMeetingStart(MeetingStartEvent e) => _cursed = false;

    // ホスト: 呪った会議の票を重くし、自分に入った票を貯める
    [HostOnly]
    private void OnVoteTally(VoteTallyEvent e)
    {
        if (!PortKit.Alive(Player)) return;
        int got = 0;
        foreach (var v in e.Votes)
            if (v.Target == PlayerId && v.Voter != PlayerId) got++;
        int stored = Stored;
        if (_cursed)
        {
            byte to = e.TargetOf(PlayerId);
            bool skip = to == VoteTallyEvent.Skip;
            if (to != 255 && (!skip || AllowSkip))
            {
                e.SetWeight(PlayerId, stored + 1);
                stored = 0;
            }
        }
        stored += got;
        StoredCall.Send((PlayerId, (byte)System.Math.Min(stored, 255)));
        _stored = stored;
    }

    private static readonly RemoteCall<(byte id, byte n)> StoredCall = new("Waraningyou.Stored", Route.HostToAll,
        (w, m) => { w.Write(m.id); w.Write(m.n); }, r => (r.ReadByte(), r.ReadByte()),
        (_, m) => { if (RoleState.Of(m.id) is Waraningyou w) w._stored = m.n; });

    public override string NameMark(PlayerControl seen) =>
        seen.PlayerId == PlayerId && MeetingHud.Instance ? $"<color={Color}>({Stored})</color>" : null;
}
