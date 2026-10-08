using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles;

// アドオンの土台。役職の上に重ねて付く特性 (1 人に複数)。アドオン 1 つ = このクラスを継承したクラス 1 つ。
// 継承したクラスを置くだけで、設定画面の「アドオン」タブ (出現率・人数・付く陣営・下の static な設定項目)・
// 試合での割り当て・表示まで自動で行われる。書き方は役職と同じ (設定は static readonly・起きた事はメソッド)。
//
// 役職と違うところ:
//   - 陣営を持たない。どの陣営の人に付くかは設定 (クルー / インポスター / 第三陣営) で、ホストが配る時に見る。
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
        _ => OnCrew.Value,
    };

    // 付く陣営。設定画面に自動で付く
    internal BoolOpt OnCrew, OnImpostor, OnNeutral;
}
