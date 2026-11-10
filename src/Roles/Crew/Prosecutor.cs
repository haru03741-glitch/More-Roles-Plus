// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Crewmate/Prosecutor.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to https://github.com/eDonnes124/Town-Of-Us-R (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// プロセキューター (クルー): 会議で名札のボタンから「プロセキュート」すると、投票をすぐ締めてその人を追放する。
// 追放した人が本当のクルーなら自分も死ぬ (設定)
public sealed class Prosecutor : RoleBase
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#3477EB";
    public override Text Name => new("Prosecutor.Name");
    public override string Reading => "ぷろせきゅーたー";
    public override Text Blurb => new("Prosecutor.Blurb");
    public override string Origin => "Uchu, TOU";
    public override bool CanBeMadmate => true;
    public override HelpSection[] HelpSections => new HelpSection[] { new("Prosecutor.Prosecute", "prosecutor_hand") };

    public static readonly IntOpt NumOfProsecute = new("Prosecutor.NumOfProsecute", 2, 1, 15);
    public static readonly BoolOpt DieOnCrew = new("Prosecutor.DieOnCrew", true);
    public static readonly BoolOpt CanCallEmergency = new("Prosecutor.CanCallEmergency", true);

    private byte _prosecuted = 255; // 強制追放した相手 (全員の端末で同じ)

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Prosecutor.Prosecute"), "prosecutor_hand")
        {
            Target = AbilityTarget.Meeting,
            MaxUses = () => NumOfProsecute,
            UsesPerMeeting = 1,
            GuessLike = true,
            OnUsed = (_, t) =>
            {
                _prosecuted = t.PlayerId;
                // 全員に、プロセキュートで締められたことだけを知らせる (誰が使ったかは出さない)
                PortKit.Notice(new Text("Prosecutor.Announced").ToString());
                if (AmongUsClient.Instance.AmHost) Rules.VoteTally.ForceExile(MeetingHud.Instance, t.PlayerId, PlayerId);
            },
        });
    }

    private void OnMeetingStart(MeetingStartEvent e) => _prosecuted = 255;

    // ホスト: 本当のクルーを追放したら自分も死ぬ
    [HostOnly]
    private void OnExiled(PlayerExiledEvent e)
    {
        if (!DieOnCrew || !e.Player || e.Player.PlayerId != _prosecuted || !PortKit.Alive(Player)) return;
        var me = Player;
        if (UchuKit.TrueCrew(e.Player)) PortKit.After(0.2f, () => { if (PortKit.Alive(me)) PortKit.HostSuicide(me); });
    }

    internal static bool BlocksEmergency(PlayerControl p) => RoleState.ActingOf(p) is Prosecutor && !CanCallEmergency;
}
