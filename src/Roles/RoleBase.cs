using AmongUs.GameOptions;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles;

public enum Team
{
    Crew,
    Impostor,
    Neutral,
}

// 役職の土台。役職 1 つ = このクラスを継承したクラス 1 つ (ファイル 1 つ)。
// 継承したクラスを置くだけで、設定画面 (出現率・人数・下の static な設定項目)・試合での割り当て・
// 役職名の表示まで自動で行われる。登録の作業は要らない。
//
// - 設定項目は `static readonly` のフィールドに置く (全員で共通の値なので static)。
// - プレイヤーごとの状態 (残り回数など) は普通のフィールドに置く。試合ごと・プレイヤーごとに
//   新しいインスタンスが作られる。
// - 試合で起きた事 (倒された・追放・会議・試合の終わり) は、引数がそのイベント 1 つのメソッドを書くだけで呼ばれる
//   (GameEvents.cs)。値を返す問い合わせ (視界・キルの待ち時間・相乗り) は Assignable の Modify〜 / 下の AlsoWins を上書きする。
public abstract class RoleBase : Assignable
{
    public abstract Team Team { get; }

    // イントロに出る一行の説明
    public abstract Text Blurb { get; }

    // タスク欄の先頭に出す説明 (既定はイントロの一行)
    public override Text Description => Blurb;

    // 第三陣営でキルする役職なら true。生き残りの人数で勝敗を決める時、この役職だけで 1 つの陣営として数える
    // (インポスターが全滅し、他の生存者がこの役職の人数以下になったら勝ち)
    public virtual bool IsKiller => false;

    // 本編のキルボタンを使えるか (インポスター以外の役職でキルさせる時に true)。既定はキル役なら使える
    public virtual bool CanKill => IsKiller;

    // 本編のどの役職の上に乗るか (ベントを使うなら Engineer など)。既定はクルー / インポスター
    public virtual RoleTypes BaseRole => Team == Team.Impostor ? RoleTypes.Impostor : RoleTypes.Crewmate;

    // 本編の役職そのもの (Roles/Builtin)。表示は本編に任せる
    internal virtual bool IsVanilla => false;

    // 幽霊になってから本編が配る役職 (MRP の配り方では配らない)
    internal virtual bool AssignedOnDeath => false;

    // 付いているアドオン (割り当て後・付いた順)
    public System.Collections.Generic.IReadOnlyList<AddonBase> Addons => RoleState.AddonsOf(PlayerId);

    // この役職が出た試合には出ない役職 (どちらの側に書いてあっても効く)
    public virtual System.Type[] NotWith => null;

    // 誰かの勝ちで試合が終わる時に、自分も一緒に勝つか (第三陣営の相乗り)。ホストの端末でだけ呼ばれる。
    // 自分だけで勝って試合を終わらせる時は GameEnd.Win(this) を呼ぶ
    public virtual bool AlsoWins(GameResult result) => false;

    // 地形をどう壊してよいか (既定は壊せない)。壊す時は Terrain.TerrainApi から呼ぶ。
    // ホストが客の依頼を確かめる時にも呼ぶので、設定値だけから決める
    //   public override Terrain.TerrainPermit TerrainPermit => Terrain.TerrainPermit.None.WithBomb(radius: 1.5f, cooldown: 20f);
    public virtual Terrain.TerrainPermit TerrainPermit => Terrain.TerrainPermit.None;

    // 能力ボタン。OnAssigned の中で AddAbility する (全員の端末で作る。ボタンを出すのは自分の端末だけで、
    // 他の端末ではホストが客の依頼を確かめるのに使う)。作り方は Ability を参照
    internal readonly System.Collections.Generic.List<Ability> Abilities = new();

    protected internal Ability AddAbility(Ability ability)
    {
        ability.Bind(this, Abilities.Count);
        Abilities.Add(ability);
        return ability;
    }

    internal override Tab Tab => Team switch
    {
        Team.Impostor => Tab.Impostor,
        Team.Neutral => Tab.Neutral,
        _ => Tab.Crew,
    };
}
