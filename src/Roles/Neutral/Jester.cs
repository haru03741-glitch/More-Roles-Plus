namespace MoreRolesPlus.Roles.Neutral;

// 見本の役職 (第三陣営・単独勝ち): 会議で追放されたら一人勝ち
public sealed class Jester : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#EC62A5";
    public override Text Name => new("道化", "Jester");
    public override Text Blurb => new("みんなをだまして追放されよう", "Get yourself voted out");

    // 追放は全員の端末で起きるが、勝ちを決めるのはホストだけ (GameEnd.Win はホストでだけ効く)
    public override void OnExiled() => GameEnd.Win(this);
}
