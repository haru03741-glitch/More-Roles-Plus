using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles;

// アドオンの土台。役職の上に重ねて付く特性 (1 人に複数)。アドオン 1 つ = このクラスを継承したクラス 1 つ。
// 継承したクラスを置くだけで、設定画面の「アドオン」タブ (出現率・人数・付く陣営・下の static な設定項目)・
// 試合での割り当て・表示まで自動で行われる。書き方は役職と同じ (設定は static readonly・起きた事はメソッド)。
//
// 役職と違うところ:
//   - 陣営を持たない。どの陣営の人に付くかは設定 (クルー / マッドメイト / インポスター / 第三陣営) で、ホストが配る時に見る。
//   - 能力ボタン・単独の勝ち (GameEnd.Win) は持てない。
public abstract class AddonBase : Assignable
{
    // イントロと設定画面に出る一行の説明
    public abstract Text Blurb { get; }

    public override Text Description => Blurb;

    internal override Tab Tab => Tab.Addon;

    // 付いている役職 (割り当て後)
    public RoleBase Role => RoleState.Of(PlayerId);

    // この人に付けてよいか。team = その人の陣営、role = MRP の役職 (本編の役職のままの人は null)。
    // 既定は設定 (付く陣営) どおり。役職ごとの相性はこれを上書きして決める
    //   public override bool CanAttach(Team team, RoleBase role) => base.CanAttach(team, role) && role is not Jester;
    public virtual bool CanAttach(Team team, RoleBase role) => team switch
    {
        Team.Impostor => OnImpostor.Value,
        Team.Neutral => OnNeutral.Value,
        Team.Madmate => OnMadmate.Value,
        _ => OnCrew.Value,
    };

    // 付いている役職に能力ボタンを足す (OnAssigned の中で。役職の無い人には足せないので null)
    protected Ability AddAbility(Ability ability) => Role?.AddAbility(ability);

    // 何人 1 組で付くか (ラバーズなら 2)。2 以上なら配る時に同じ数の人へまとめて付け、組は AddonGroups で分かる
    public virtual int GroupSize => 1;

    // 幽霊役職か。試合の始めには配らず、死んだ人に (出現率・人数どおりに) ホストが後から付ける (GhostRoles)。
    // 能力は Ability.Ghost = true にして、死んでいる間に使う
    public virtual bool Ghost => false;

    // 他の役職から付けられるだけのアドオン (配る確率を出さない)
    public virtual bool MadeByOthers => false;

    // 試合の終わりに、勝った陣営から勝ちを奪うか (RoleBase.StealsWin と同じ)
    public virtual bool StealsWin(GameResult result) => false;

    // 試合の終わりに、勝った人に加わるか (RoleBase.AlsoWins と同じ)
    public virtual bool AlsoWins(GameResult result) => false;

    // 試合の終わりに、勝った人から外れるか (陣営が勝っても負けにする)
    public virtual bool LosesWin(GameResult result) => false;

    // この人のタスクをクルーのタスク勝利に数えるか
    public virtual bool TasksCount => true;

    // AlsoWins・LosesWin を使うアドオンは true (本編の終わり方でも MRP が結果を配る)
    public virtual bool AffectsWin => false;

    // 本人は生きている間このアドオンに気付かない (自分の役職の表示・説明に出さない)
    public virtual bool HiddenFromSelf => false;

    // 自分の端末で、この人のこのアドオンを今見せてよいか
    internal bool ShownToLocal()
    {
        if (!HiddenFromSelf) return true;
        var lp = PlayerControl.LocalPlayer;
        return !lp || lp.PlayerId != PlayerId || (lp.Data != null && lp.Data.IsDead && SelfSeesWhenDead);
    }

    // 気付かないアドオンが、死んだ後に本人に分かるか
    protected internal virtual bool SelfSeesWhenDead => true;

    // 付く陣営。設定画面に自動で付く
    internal BoolOpt OnCrew, OnMadmate, OnImpostor, OnNeutral;

    // 付く陣営の設定の初めの値 (クルーだけに付くアドオンなら OnImpostor 等を false に)
    protected internal virtual bool DefaultOnCrew => true;
    protected internal virtual bool DefaultOnMadmate => true;
    protected internal virtual bool DefaultOnImpostor => true;
    protected internal virtual bool DefaultOnNeutral => true;

    // 全員の画面で、このアドオンの人の名前の後ろに付く印 (RoleBase.MarkSeenBy と同じ)。null なら付けない
    public virtual string MarkSeenBy(PlayerControl viewer) => null;
}
