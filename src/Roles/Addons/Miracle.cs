// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Ghost/Complex/Miracle.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using System.Collections.Generic;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Addons;

// ミラクル (幽霊役職・クルーとインポスターの死者に): 「スワップ」で近くの生きている人を選ぶと、次の会議の終わりに
// ミラクル (死者) とその人の役職が入れ替わる。選べる相手は設定 (同じ陣営だけ・クルーだけ・誰でも)。同じ相手は 1 人のミラクルだけが選べる
public sealed class Miracle : AddonBase
{
    public override string Color => "#F3FFA3";
    public override Text Name => new("Miracle.Name");
    public override string Reading => "みらくる";
    public override Text Blurb => new("Miracle.Blurb");
    public override string Origin => "Uchu";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Miracle.Swap", "miracle_swap") };
    public override bool Ghost => true;

    protected internal override bool DefaultOnNeutral => false;
    public override bool CanAttach(Team team, RoleBase role) => team != Team.Neutral && base.CanAttach(team, role);

    public static readonly FloatOpt SwapCooldown = new("Miracle.SwapCooldown", 30f, 5f, 60f, 2.5f, "s");
    public static readonly IntOpt NumOfSwap = new("Miracle.NumOfSwap", 1, 1, 7);
    public static readonly ChoiceOpt Action = new("Miracle.Action", 0,
        new Text("Miracle.Action.MyTeam"), new Text("Miracle.Action.OtherNeutral"), new Text("Miracle.Action.All"));

    // 全員の端末で同じ: 選ばれている相手 (会議の終わりに空にする)
    private static readonly HashSet<byte> Taken = new();
    private static object _match;

    private byte _target = 255;
    private bool _swapped;

    public override void OnAssigned()
    {
        if (!ReferenceEquals(_match, RoleState.Match))
        {
            _match = RoleState.Match;
            Taken.Clear();
        }
        AddAbility(new Ability(new Text("Miracle.Swap"), "miracle_swap")
        {
            Ghost = true,
            Target = AbilityTarget.Player,
            Cooldown = () => SwapCooldown,
            MaxUses = () => NumOfSwap,
            CanTarget = (_, t) => _target == 255 && !_swapped && !Taken.Contains(t.PlayerId) && Allowed(t),
            OnUsed = (_, t) =>
            {
                if (Taken.Contains(t.PlayerId)) return;
                Taken.Add(t.PlayerId);
                _target = t.PlayerId;
            },
        });
    }

    private bool Allowed(PlayerControl t)
    {
        var tr = RoleState.Of(t);
        if (tr == null) return false;
        switch (Action.Value)
        {
            case 0:
                bool meImp = PortKit.IsImpostor(Player);
                return meImp ? PortKit.IsImpostor(t) : !PortKit.IsImpostor(t) && tr.Team == Team.Crew;
            case 1:
                return !PortKit.IsImpostor(t) && tr.Team == Team.Crew;
            default:
                return true;
        }
    }

    // ホスト: 会議の終わりにミラクルと相手の役職を入れ替える
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        byte target = _target;
        _target = 255;
        Taken.Clear();
        if (!AmongUsClient.Instance.AmHost || target == 255 || _swapped) return;
        var t = PortKit.ById(target);
        var mine = RoleState.Of(PlayerId);
        var theirs = RoleState.Of(target);
        if (!PortKit.Alive(t) || mine == null || theirs == null) return;
        var myProto = Registry.Roles.Find(r => r.Id == mine.Id);
        var theirProto = Registry.Roles.Find(r => r.Id == theirs.Id);
        if (myProto == null || theirProto == null) return;
        _swapped = true;
        var me = Player;
        RoleState.Change(me, theirProto);
        RoleState.Change(t, myProto);
        SenseFx.HostSwapped(me.PlayerId, target);
    }
}
