// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/WaveCannon.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Impostor;

// 波動砲 (インポスター): 「波動砲」でその場に砲台を置いて充填し、充填が終わると左右の向いている方へ壁を抜けるビーム
// (戦車) か走っていくサンタ (サンタ) を撃つ。当たった人は陣営に関係なく死ぬ。賢者のバリアに当たると跳ね返る
public sealed class WaveCannon : RoleBase, IWaveCannon
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("WaveCannon.Name");
    public override string Reading => "はどうほう";
    public override Text Blurb => new("WaveCannon.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("WaveCannon.Beam", "snr_wave") };

    public static readonly FloatOpt BeamCooldown = new("WaveCannon.Cooldown", 20f, 2.5f, 180f, 2.5f, "s");
    public static readonly FloatOpt ChargeTime = new("WaveCannon.Duration", 3f, 0.5f, 15f, 0.5f, "s");
    public static readonly BoolOpt SyncKillCooldown = new("WaveCannon.SyncKillCooldown", false);
    public static readonly ChoiceOpt AnimationType = new("WaveCannon.AnimationType", 0,
        new Text("WaveCannon.AnimationType.Tank"), new Text("WaveCannon.AnimationType.Santa"));
    public static readonly BoolOpt KillSound = new("WaveCannon.KillSound", true);
    public static readonly BoolOpt KillSoundDistributed = new("WaveCannon.KillSoundDistributed", false) { When = () => KillSound };

    internal WaveCannonGun Gun { get; private set; }
    WaveCannonGun IWaveCannon.Gun => Gun;

    public override void OnAssigned()
    {
        Gun = new WaveCannonGun(this, () => AnimationType, () => ChargeTime, () => SyncKillCooldown, () => KillSound, () => KillSoundDistributed,
            () => GameManager.Instance.LogicOptions.GetKillCooldown());
        AddAbility(Gun.Make(new Text("WaveCannon.Beam"), () => BeamCooldown));
    }

    public override void OnFixedUpdate() => Gun?.Tick();

    [LocalOnly]
    private void OnMurdered(PlayerMurderedEvent e) => Gun?.OnMurdered(e);
}
