// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Bait.cs (GPL-3.0)
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Addons;

// ベイティング (アドオン): 倒されると、少し後に倒した人が自分の死体を通報してしまう
public sealed class Baiting : AddonBase
{
    public const string Cyan = "#00F7FF";

    public override string Color => Cyan;
    public override Text Name => new("Baiting.Name");
    public override string Reading => "べいてぃんぐ";
    public override Text Blurb => new("Baiting.Blurb");
    public override string Origin => "TOR";

    protected internal override bool DefaultOnMadmate => false;
    protected internal override bool DefaultOnImpostor => false;
    protected internal override bool DefaultOnNeutral => false;

    public static readonly FloatOpt ReportTime = new("Baiting.ReportTime", 0.5f, 0.5f, 10f, 0.1f, "s");
    public static readonly BoolOpt WarnKiller = new("Baiting.WarnKiller", true);
    public static readonly BoolOpt RandomDelay = new("Baiting.RandomDelay", false);
    public static readonly IntOpt DelayVariation = new("Baiting.DelayVariation", 2, 0, 30, 1, "s") { When = () => RandomDelay };
    public static readonly BoolOpt KnowSelf = new("Baiting.KnowSelf", true);
    public static readonly BoolOpt EveryoneKnows = new("Baiting.EveryoneKnows", false);

    private const string Mark = "<color=" + Cyan + ">◎</color>";

    // 本人の画面で自分の名前に (全員に分かる時は下の MarkSeenBy が出すので出さない)
    public override string NameMark(PlayerControl seen) =>
        KnowSelf && !EveryoneKnows && seen.PlayerId == PlayerId ? Mark : null;

    public override string MarkSeenBy(PlayerControl viewer) => EveryoneKnows ? Mark : null;

    // 倒した人の端末で、その人に通報させる
    [OnlyMine]
    private void OnMurdered(PlayerMurderedEvent e)
    {
        Report(e.Killer);
    }

    // 焼死・溺死・下敷きは、その火・水・崩れを起こした人に通報させる (船外へ吸い出された時は死体が無い)
    [OnlyMine]
    private void OnTerrainKilled(TerrainKilledEvent e)
    {
        if (e.Kind != MatchLog.Kind.Spaced) Report(e.Culprit);
    }

    private void Report(PlayerControl killer)
    {
        if (!killer || killer.PlayerId == PlayerId || !killer.AmOwner) return;
        if (WarnKiller) PortKit.Flash(new Color(0f, 0.97f, 1f, 0.5f));
        float delay = ReportTime + (RandomDelay ? Random.Range(0f, DelayVariation) : 0f);
        var body = Player.Data;
        PortKit.After(delay, () =>
        {
            if (!PortKit.Alive(killer) || MeetingHud.Instance || ExileController.Instance) return;
            killer.CmdReportDeadBody(body);
        });
    }
}
