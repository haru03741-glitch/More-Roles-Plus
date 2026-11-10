// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/JackalFriends.cs, Roles/Ability/JFriendAbility.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Neutral;

// ジャッカルフレンズ (クルーの姿・ジャッカルの味方): ジャッカルの「仲間の種類」でフレンズを選ぶと、仲間にされた人がなる。
// ジャッカルの仲間が勝つと一緒に勝つ。キルはできず、ジャッカルが死んでも昇格しない。
// 設定でタスクを決めた数だけ終えるとジャッカル (とサイドキック) が誰か分かる。ジャッカルからはフレンズは分からない
public sealed class JackalFriends : RoleBase
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => Jackal.PackColor;
    public override Text Name => new("JackalFriends.Name");
    public override string Reading => "じゃっかるふれんず";
    public override Text Blurb => new("JackalFriends.Blurb");
    public override string Origin => "SNR";
    public override string WinsWithFaction => Jackal.Pack;
    public override bool MadeByOthers => true;

    public static readonly BoolOpt Vent = new("JackalFriends.CanVent", true);
    public static readonly BoolOpt ImpostorSight = new("JackalFriends.ImpostorVision", true);
    public static readonly BoolOpt KnowJackals = new("JackalFriends.KnowJackals", true);
    public static readonly IntOpt NeededTasks = new("JackalFriends.NeededTasks", 0, 0, 10) { When = () => KnowJackals };
    // 原作の専用タスク (JackalFriendsCustomTaskCount / JackalFriendsTaskOption): 分かるためのタスクの数を共通・ショート・ロングで決める
    public static readonly BoolOpt SpecialTasks = new("JackalFriends.SpecialTasks", false) { When = () => KnowJackals };
    public static readonly IntOpt SpecialCommon = new("JackalFriends.SpecialCommon", 1, 0, 15) { When = () => KnowJackals && SpecialTasks };
    public static readonly IntOpt SpecialShort = new("JackalFriends.SpecialShort", 1, 0, 15) { When = () => KnowJackals && SpecialTasks };
    public static readonly IntOpt SpecialLong = new("JackalFriends.SpecialLong", 1, 0, 15) { When = () => KnowJackals && SpecialTasks };

    public override bool? CanVent => Vent ? true : null;

    public override void ModifyVision(ref float radius)
    {
        if (ImpostorSight) PortKit.ImpostorVision(ref radius);
    }

    public override void OnAssigned()
    {
        if (IsLocal && KnowJackals && SpecialTasks)
            PortKit.After(0.5f, () =>
            {
                if (RoleState.Local == this) SnrKit.AssignOwnTasks(SpecialCommon, SpecialShort, SpecialLong);
            });
    }

    // ジャッカルとサイドキックに ◆ (フレンズ同士は見えない)
    public override string NameMark(PlayerControl seen) =>
        KnowJackals && Known(Player) && Jackal.InPack(seen) ? $"<color={Jackal.PackColor}>◆</color>" : null;

    // 原作の complete >= Min(必要数, 全タスク数)。タスクが 1 つも無い時は必要数が 0 の時だけ (Madmate と同じ)
    private static bool Known(PlayerControl me)
    {
        if (!me || me.Data == null || me.Data.Tasks == null) return false;
        int all = me.Data.Tasks.Count;
        if (all <= 0) return NeededTasks <= 0;
        return PortKit.TasksDone(me) >= System.Math.Min(NeededTasks.Value, all);
    }
}
