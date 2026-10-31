// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Bakery.cs (GPL-3.0)
namespace MoreRolesPlus.Roles.Crew;

// パン屋 (クルー): 生きている間、追放の画面に「今日もパンを焼いています」と出る (生きていることが皆に分かる)
public sealed class Bakery : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#F0A04B";
    public override Text Name => new("Bakery.Name");
    public override string Reading => "ぱんや";
    public override Text Blurb => new("Bakery.Blurb");
    public override string Origin => "SNR";

    // パン屋が何人いても 1 行だけ出す: 生きているパン屋のうち番号のいちばん小さい人の端末の役職だけが書く
    private void OnExileShown(ExileShownEvent e)
    {
        if (!PortKit.Alive(Player)) return;
        if (e.Exiled != null && e.Exiled.PlayerId == PlayerId) return;
        foreach (var r in RoleState.All)
            if (r is Bakery b && b != this && PortKit.Alive(b.Player) && b.PlayerId < PlayerId && (e.Exiled == null || e.Exiled.PlayerId != b.PlayerId)) return;
        var t = e.Controller ? e.Controller.ImpostorText : null;
        if (!t) return;
        t.text = string.IsNullOrEmpty(t.text) ? new Text("Bakery.ExileText").ToString() : t.text + "\n" + new Text("Bakery.ExileText");
    }
}
