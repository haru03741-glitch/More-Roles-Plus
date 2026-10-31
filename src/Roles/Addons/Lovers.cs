// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Modifiers/Lovers.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Addons;

// ラバーズ (アドオン・2 人 1 組): 相方が誰か分かる。相方が死ぬと後を追って死ぬ (設定)。
// 試合の終わりに 2 人とも生きていれば、勝った陣営から勝ちを奪う
public sealed class Lovers : AddonBase, PortKit.ILovers
{
    public const string Pink = "#FF66CC";

    public override string Color => Pink;
    public override Text Name => new("Lovers.Name");
    public override string Reading => "らばーず";
    public override Text Blurb => new("Lovers.Blurb");
    public override string Origin => "SNR";
    public override int GroupSize => 2;

    public static readonly BoolOpt FollowDeath = new("Lovers.FollowDeath", true);
    public static readonly BoolOpt SoloWin = new("Lovers.SoloWin", true);

    private byte Partner => AddonGroups.PartnerOf(PlayerId);

    public override bool StealsWin(GameResult result)
    {
        if (!SoloWin || !PortKit.Alive(Player)) return false;
        var mate = PortKit.ById(Partner);
        return mate && PortKit.Alive(mate);
    }

    public override string NameMark(PlayerControl seen) =>
        seen.PlayerId == PlayerId || seen.PlayerId == Partner ? $"<color={Pink}>♥</color>" : null;

    // 相方が倒された / 追放された: ホストが後を追わせる
    [HostOnly]
    private void OnMurdered(PlayerMurderedEvent e) => Follow(e.Target, false);

    [HostOnly]
    private void OnExiled(PlayerExiledEvent e) => Follow(e.Player, true);

    private void Follow(PlayerControl dead, bool exiled)
    {
        if (!FollowDeath || !dead || dead.PlayerId != Partner || !PortKit.Alive(Player)) return;
        // 倒された処理の途中で次の死を起こさないよう、少し後で
        PortKit.After(0.05f, () =>
        {
            if (!PortKit.Alive(Player)) return;
            if (exiled || MeetingHud.Instance || ExileController.Instance) PortKit.HostVanish(Player, Player);
            else PortKit.HostSuicide(Player);
        });
    }
}
