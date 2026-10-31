// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/PavlovsDog.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Neutral;

// パブロフの犬 (第三陣営・キル役): 飼い主にしつけられた人。キルでき、飼い主と一緒に勝つ。
// 飼い主が全員死ぬと暴走し、キルのクールダウンが短くなる代わりに、決まった時間キルしないと死ぬ。最初から配られることはない
public sealed class PavlovsDog : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => PavlovsOwner.PackColor;
    public override Text Name => new("PavlovsDog.Name");
    public override string Reading => "ぱぶろふのいぬ";
    public override Text Blurb => new("PavlovsDog.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("PavlovsDog.Kill") };
    public override bool IsKiller => true;
    public override string Faction => PavlovsOwner.Pack;
    public override bool MadeByOthers => true;

    public static readonly BoolOpt Vent = new("PavlovsDog.CanVent", true);
    public static readonly BoolOpt ImpostorSight = new("PavlovsDog.ImpostorVision", true);
    public static readonly FloatOpt KillCooldown = new("PavlovsDog.KillCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt RampageCooldown = new("PavlovsDog.RampageKillCooldown", 15f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt RampageSuicideTime = new("PavlovsDog.RampageSuicideTime", 60f, 10f, 120f, 5f, "s");
    public static readonly BoolOpt ResetOnMeeting = new("PavlovsDog.ResetOnMeeting", true);

    public override bool? CanVent => Vent;

    // 暴走中か (飼い主が誰も生きていない)。ホストのキルの確かめでも同じ値になる
    private static bool Rampage => !PavlovsOwner.AnyOwnerAlive();

    public override void ModifyKillCooldown(ref float seconds) => seconds = Rampage ? RampageCooldown : KillCooldown;

    public override void ModifyVision(ref float radius)
    {
        if (ImpostorSight) PortKit.ImpostorVision(ref radius);
    }

    private float _left = -1f; // 暴走して死ぬまでの残り (-1 = 数えていない)

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (!PortKit.Alive(Player) || MeetingHud.Instance || ExileController.Instance) return;
        if (!Rampage) { _left = -1f; return; }
        if (_left < 0f) Restart();
        _left -= UnityEngine.Time.fixedDeltaTime;
        if (_left > 0f) return;
        _left = float.MaxValue;
        StatusEffects.Hide("PavlovsDog");
        PortKit.Suicide();
    }

    private void Restart()
    {
        _left = RampageSuicideTime;
        StatusEffects.ShowFor("PavlovsDog", new Text("PavlovsDog.Timer"), Color, _left);
    }

    [LocalOnly]
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (e.Killer && e.Killer.PlayerId == PlayerId && e.Target.PlayerId != PlayerId && _left >= 0f) Restart();
    }

    [LocalOnly]
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (_left >= 0f && ResetOnMeeting) Restart();
    }

    // 飼い主の陣営の人は倒せない
    [HostOnly]
    private void OnBeforeMurder(BeforeMurderEvent e)
    {
        if (e.Canceled || !e.Killer || e.Killer.PlayerId != PlayerId || e.Target.PlayerId == PlayerId) return;
        if (PavlovsOwner.InPack(e.Target)) e.Cancel("pavlov pack");
    }

    public override string NameMark(PlayerControl seen) =>
        RoleState.Of(seen) is PavlovsOwner ? $"<color={PavlovsOwner.PackColor}>●</color>" : null;
}
