// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/EvilGambler.cs, Roles/Ability/EvilGamblerAbility.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Impostor;

// イビルギャンブラー (インポスター): キルするたびに、次のキルのクールダウンが運で短く / 長くなる。会議の後は部屋の値に戻る
public sealed class EvilGambler : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("EvilGambler.Name");
    public override string Reading => "いびるぎゃんぶらー";
    public override Text Blurb => new("EvilGambler.Blurb");
    public override string Origin => "SNR";

    public static readonly FloatOpt WinCooldown = new("EvilGambler.WinCooldown", 15f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt LoseCooldown = new("EvilGambler.LoseCooldown", 45f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt WinChance = new("EvilGambler.WinChance", 50, 10, 90, 10, "%");

    // 本編がキルした人の待ち時間を戻した後 (キルの出来事は本編のキルの後) に書き換える
    [LocalOnly]
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (!e.Killer || e.Killer.PlayerId != PlayerId || e.Target.PlayerId == PlayerId) return;
        bool win = UnityEngine.Random.Range(0, 100) < WinChance;
        Player.SetKillTimer(win ? WinCooldown : LoseCooldown);
    }
}
