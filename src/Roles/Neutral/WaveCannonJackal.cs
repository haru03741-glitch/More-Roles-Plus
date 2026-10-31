// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/WaveCannonJackal.cs, Roles/Ability/JackalAbility.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Neutral;

// 波動砲ジャッカル (第三陣営・キル役): ジャッカルの仲間。キルボタンに加えて波動砲 (その場で充填して左右へ撃つ) を持つ。
// 波動砲は仲間にも当たる。1 度だけ近くの人を仲間 (サイドキック / フレンズ / 波動砲サイドキック / 弾) にできる
public sealed class WaveCannonJackal : RoleBase, IWaveCannon
{
    public override Team Team => Team.Neutral;
    public override string Color => "#00B4EB";
    public override Text Name => new("WaveCannonJackal.Name");
    public override string Reading => "はどうほうじゃっかる";
    public override Text Blurb => new("WaveCannonJackal.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("WaveCannonJackal.Kill"), new("WaveCannonJackal.Beam", "snr_wave"), new("WaveCannonJackal.Sidekick", "snr_sidekick") };
    public override bool IsKiller => true;
    public override string Faction => Jackal.Pack;

    public static readonly FloatOpt KillCooldown = new("WaveCannonJackal.KillCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly BoolOpt Vent = new("WaveCannonJackal.CanVent", true);
    public static readonly BoolOpt ImpostorVision = new("WaveCannonJackal.ImpostorVision", true);
    public static readonly BoolOpt CanSidekick = new("WaveCannonJackal.CanSidekick", true);
    public static readonly FloatOpt SidekickCooldown = new("WaveCannonJackal.SidekickCooldown", 30f, 2.5f, 60f, 2.5f, "s") { When = () => CanSidekick };
    public static readonly BoolOpt Infinite = new("WaveCannonJackal.Infinite", true);
    public static readonly ChoiceOpt SidekickKind = new("WaveCannonJackal.SidekickKind", 0,
        new Text("Jackal.SidekickKind.Sidekick"), new Text("Jackal.SidekickKind.Friends"),
        new Text("WaveCannonJackal.SidekickKind.SidekickWaveCannon"), new Text("WaveCannonJackal.SidekickKind.Bullet")) { When = () => CanSidekick };
    public static readonly BoolOpt BulletToJackal = new("WaveCannonJackal.BulletToJackal", true) { When = () => CanSidekick && SidekickKind == 3 };
    public static readonly BoolOpt BulletHasWaveCannon = new("WaveCannonJackal.BulletHasWaveCannon", true) { When = () => CanSidekick && SidekickKind == 3 && BulletToJackal };
    public static readonly FloatOpt BulletLoadCooldown = new("WaveCannonJackal.BulletLoadCooldown", 20f, 0f, 180f, 2.5f, "s") { When = () => CanSidekick && SidekickKind == 3 };
    public static readonly FloatOpt BulletChargeTime = new("WaveCannonJackal.BulletChargeTime", 5f, 0f, 15f, 0.5f, "s") { When = () => CanSidekick && SidekickKind == 3 };
    public static readonly BoolOpt BulletSeesParent = new("WaveCannonJackal.BulletSeesParent", true) { When = () => CanSidekick && SidekickKind == 3 };
    public static readonly BoolOpt ParentSeesBullet = new("WaveCannonJackal.ParentSeesBullet", true) { When = () => CanSidekick && SidekickKind == 3 };
    public static readonly FloatOpt BeamCooldown = new("WaveCannonJackal.Cooldown", 20f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt ChargeTime = new("WaveCannonJackal.Duration", 3f, 1f, 10f, 1f, "s");
    public static readonly BoolOpt SyncKillCooldown = new("WaveCannonJackal.SyncKillCooldown", true);
    public static readonly ChoiceOpt AnimationType = new("WaveCannonJackal.AnimationType", 0,
        new Text("WaveCannon.AnimationType.Tank"), new Text("WaveCannon.AnimationType.Santa"));
    public static readonly BoolOpt KillSound = new("WaveCannonJackal.KillSound", true);
    public static readonly BoolOpt KillSoundDistributed = new("WaveCannonJackal.KillSoundDistributed", false) { When = () => KillSound };

    // サイドキックから上がった波動砲ジャッカル (無限ジャッカルでなければもう仲間を作れない)。全員の端末で同じように付ける
    internal static readonly HashSet<byte> Promoted = new();

    internal WaveCannonGun Gun { get; private set; }
    WaveCannonGun IWaveCannon.Gun => Gun;

    public override bool? CanVent => Vent;

    public override void ModifyKillCooldown(ref float seconds) => seconds = KillCooldown;

    public override void ModifyVision(ref float radius)
    {
        if (ImpostorVision) PortKit.ImpostorVision(ref radius);
    }

    public override void OnAssigned()
    {
        Gun = new WaveCannonGun(this, () => AnimationType, () => ChargeTime, () => SyncKillCooldown, () => KillSound, () => KillSoundDistributed,
            () => KillCooldown);
        AddAbility(Gun.Make(new Text("WaveCannonJackal.Beam"), () => BeamCooldown));
        if (!CanSidekick || (Promoted.Contains(PlayerId) && !Infinite)) return;
        AddAbility(new Ability(new Text("WaveCannonJackal.Sidekick"), "snr_sidekick")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => SidekickCooldown,
            MaxUses = () => 1,
            CanTarget = (_, t) => !Jackal.InPack(t),
            OnUsed = (_, target) =>
            {
                if (!AmongUsClient.Instance.AmHost) return;
                switch (SidekickKind.Value)
                {
                    case 0: RoleState.Change(target, RoleState.Proto<Sidekick>()); break;
                    case 1: RoleState.Change(target, RoleState.Proto<Crew.JackalFriends>()); break;
                    case 2: RoleState.Change(target, RoleState.Proto<SidekickWaveCannon>()); break;
                    default:
                        RoleState.Change(target, RoleState.Proto<Bullet>());
                        Bullet.ParentCall.Send((target.PlayerId, PlayerId));
                        Bullet.SetParent(target.PlayerId, PlayerId);
                        break;
                }
            },
        });
    }

    public override void OnFixedUpdate() => Gun?.Tick();

    [LocalOnly]
    private void OnMurdered(PlayerMurderedEvent e) => Gun?.OnMurdered(e);

    [HostOnly]
    private void OnBeforeMurder(BeforeMurderEvent e)
    {
        if (e.Canceled || !e.Killer || e.Killer.PlayerId != PlayerId || e.Target.PlayerId == PlayerId) return;
        if (Jackal.InPack(e.Target)) e.Cancel("jackal pack");
    }

    // 込められた弾は会議で外れる
    private void OnMeetingStart(MeetingStartEvent e)
    {
        if (Gun != null) Gun.BulletId = -1;
    }

    public override string NameMark(PlayerControl seen) =>
        seen.PlayerId != PlayerId && Jackal.InPack(seen) ? $"<color={Jackal.PackColor}>◆</color>" : null;
}
