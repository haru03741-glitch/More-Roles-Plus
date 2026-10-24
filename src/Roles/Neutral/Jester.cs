namespace MoreRolesPlus.Roles.Neutral;

// 見本の役職 (第三陣営・単独勝ち): 会議で追放されたら一人勝ち
public sealed class Jester : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#EC62A5";
    public override Text Name => new("Jester.Name");
    public override string Reading => "どうけ";
    public override Text Blurb => new("Jester.Blurb");
    public override string Origin => "Jester Mod";

    public override System.Type[] NotWith => new[] { typeof(Survivor) };

    // 追放は全員の端末で起きるが、勝ちを決めるのはホストだけ (GameEnd.Win はホストでだけ効く)
    [OnlyMine]
    private void OnExiled(PlayerExiledEvent e) => GameEnd.Win(this);
}
