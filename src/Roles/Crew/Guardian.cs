// Ported from https://github.com/Tomi-1103/TownOfHost-hamo Roles/Crewmate/Guardian.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// 守護者 (クルー): 会議で人を選んで守る (タスクを決めた数だけ終えていること)。守っている人がキルされそうになると、
// その人は生き残り、代わりに守護者が死ぬ (次の会議で守護天使の知らせが出る)。守っている人がゲッサーに当てられた時は撃った人が死ぬ。
// 守りは決めた数の会議が終わると切れる。守った人にはすぐ知らせる。守護者自身がキルされても何も起きない。
// 死体を残さずに倒す能力 (吹き飛ばしなど・守りを貫く) は守れず、狙われた人だけが死ぬ。
// 元の mod (ホストだけの mod) は自分へ投票して守るモードにしてから投票で選ぶが、ここでは会議の名札のボタンで選ぶ
public sealed class Guardian : RoleBase, IGuessShield
{
    public override Team Team => Team.Crew;
    public override string Color => "#67E9FF";
    public override Text Name => new("Guardian.Name");
    public override string Reading => "しゅごしゃ";
    public override Text Blurb => new("Guardian.Blurb");
    public override string Origin => "TOH-hamo";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Guardian.Guard", "guardian_guard") };

    public static readonly IntOpt Duration = new("Guardian.Duration", 2, 1, 10);
    public static readonly IntOpt Simultaneous = new("Guardian.Simultaneous", 1, 1, 14);
    public static readonly IntOpt Lifetime = new("Guardian.Lifetime", 3, 1, 14);
    public static readonly IntOpt RequiredTasks = new("Guardian.RequiredTasks", 3, 0, 8);

    // 守っている人と残りの会議の数 / これまでに守った人 (全員の端末で同じ)
    private readonly Dictionary<byte, int> _guards = new();
    private readonly HashSet<byte> _ever = new();
    private readonly List<byte> _savedQueue = new(); // 次の会議で知らせる、身代わりで救われた人

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Guardian.Guard"), "guardian_guard")
        {
            Target = AbilityTarget.Meeting,
            CanTarget = CanGuard,
            OnUsed = Guard,
        });
    }

    private bool CanGuard(PlayerControl user, PlayerControl target)
    {
        if (!PortKit.Alive(target) || target.PlayerId == PlayerId || _guards.ContainsKey(target.PlayerId)) return false;
        if (_guards.Count >= Simultaneous) return false;
        if (!_ever.Contains(target.PlayerId) && _ever.Count >= Lifetime) return false;
        return RequiredTasks <= 0 || PortKit.TasksDone(user) >= RequiredTasks;
    }

    // 全員の端末で: 守りを始める
    private void Guard(PlayerControl user, PlayerControl target)
    {
        _guards[target.PlayerId] = Duration;
        _ever.Add(target.PlayerId);
        if (IsLocal)
        {
            PortKit.Notice(new Text("Guardian.StartGuarding").With("name", target.Data.PlayerName).ToString());
            MeetingAbilities.Refresh();
        }
        if (target.AmOwner) PortKit.Notice(new Text("Guardian.YouAreGuarded").ToString());
    }

    public override string NameMark(PlayerControl seen) =>
        _guards.ContainsKey(seen.PlayerId) ? $"<color={Color}>✚</color>" : null;

    // 会議が終わるたびに残りを減らし、0 になった守りを外す
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        var expired = new List<byte>();
        foreach (var id in new List<byte>(_guards.Keys))
            if (--_guards[id] <= 0) expired.Add(id);
        foreach (var id in expired)
        {
            _guards.Remove(id);
            var p = PortKit.ById(id);
            if (p && p.AmOwner) PortKit.Notice(new Text("Guardian.GuardExpired").ToString());
            if (IsLocal && p) PortKit.Notice(new Text("Guardian.GuardExpiredSelf").With("name", p.Data.PlayerName).ToString());
        }
    }

    // ホスト: 守っている人へのキルを止め、守護者が身代わりになる
    [HostOnly]
    private void OnBeforeMurder(BeforeMurderEvent e)
    {
        var target = e.Target;
        if (!target || !e.Killer || e.Killer.PlayerId == target.PlayerId || !_guards.ContainsKey(target.PlayerId)) return;
        if (!PortKit.Alive(Player)) return;
        e.Cancel("guardian");
        PortKit.HostBlocked(e.Killer, target);
        SacrificeCall.Send((PlayerId, target.PlayerId));
        OnSacrificed(this, target.PlayerId);
        var me = Player;
        PortKit.After(0.05f, () => { if (PortKit.Alive(me)) PortKit.HostSuicide(me); });
    }

    private static readonly RemoteCall<(byte guardian, byte target)> SacrificeCall = new("Guardian.Sacrifice", Route.HostToAll,
        (w, m) => { w.Write(m.guardian); w.Write(m.target); }, r => (r.ReadByte(), r.ReadByte()),
        (_, m) => { if (RoleState.Of(m.guardian) is Guardian g) OnSacrificed(g, m.target); });

    // 全員の端末で: 守りを外し、次の会議で知らせる
    private static void OnSacrificed(Guardian g, byte target)
    {
        g._guards.Remove(target);
        g._savedQueue.Add(target);
    }

    private void OnMeetingStart(MeetingStartEvent e)
    {
        foreach (var id in _savedQueue)
        {
            var p = PortKit.ById(id);
            if (p) PortKit.Notice(new Text("Guardian.SacrificeAnnounce").With("name", p.Data.PlayerName).ToString());
        }
        _savedQueue.Clear();
    }

    // ゲッサーに当てられた: 守っている間は撃った人が死ぬ (守護者が生きている間だけ)
    public bool ShieldsFromGuess(byte target) => PortKit.Alive(Player) && _guards.ContainsKey(target);
}
