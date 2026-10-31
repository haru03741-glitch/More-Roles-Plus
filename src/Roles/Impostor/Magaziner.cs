// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Magaziner.cs, Roles/Ability/MagazinerAbility.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Impostor;

// マガジナー (インポスター): 「補充」でキルできる状態を弾にためておき、「消費」でキルのクールダウンを短い値に戻す
public sealed class Magaziner : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Magaziner.Name");
    public override string Reading => "まがじなー";
    public override Text Blurb => new("Magaziner.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Magaziner.Store", "snr_store"), new("Magaziner.Fire", "snr_fire") };

    public static readonly FloatOpt SetKillTime = new("Magaziner.SetKillTime", 10f, 0f, 60f, 2.5f, "s");
    public static readonly BoolOpt AmmoLimit = new("Magaziner.AmmoLimit", true);
    public static readonly IntOpt MaxAmmo = new("Magaziner.MaxAmmo", 6, 1, 15) { When = () => AmmoLimit };

    private int _ammo;

    public override void OnAssigned()
    {
        // 補充: キルできる状態 (待ち時間 0) の時、それを弾にしてキルの待ち時間を最初から数え直す
        AddAbility(new Ability(new Text("Magaziner.Store"), "snr_store")
        {
            OnUse = () =>
            {
                var lp = PlayerControl.LocalPlayer;
                if (lp.killTimer > 0f || (AmmoLimit && _ammo >= MaxAmmo)) return false;
                _ammo++;
                lp.SetKillTimer(GameManager.Instance.LogicOptions.GetKillCooldown());
                return true;
            },
        });
        // 消費: 弾を 1 つ使い、キルの待ち時間を設定の値にする
        AddAbility(new Ability(new Text("Magaziner.Fire"), "snr_fire")
        {
            OnUse = () =>
            {
                if (_ammo <= 0) return false;
                _ammo--;
                var lp = PlayerControl.LocalPlayer;
                lp.SetKillTimer(System.Math.Min(lp.killTimer, SetKillTime));
                return true;
            },
        });
    }

    public override string NameMark(PlayerControl seen) =>
        seen.PlayerId == PlayerId ? $" <color=#FF1919>[{_ammo}]</color>" : null;
}
