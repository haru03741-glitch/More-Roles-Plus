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
//   (GameEvents.cs)。値を返す問い合わせ (視界・キルの待ち時間・相乗り) は下の Modify〜 / AlsoWins を上書きする。
public abstract class RoleBase
{
    // 保存と同期に使う名前。既定はクラス名 (変えると保存済みの設定値が引き継がれない)
    public virtual string Id => GetType().Name;

    public abstract Team Team { get; }

    // 役職の色 ("#RRGGBB")
    public abstract string Color { get; }

    public abstract Text Name { get; }

    // イントロに出る一行の説明
    public abstract Text Blurb { get; }

    // タスク欄の先頭に出す説明 (既定はイントロの一行)
    public virtual Text Description => Blurb;

    // 第三陣営でキルする役職なら true。生き残りの人数で勝敗を決める時、この役職だけで 1 つの陣営として数える
    // (インポスターが全滅し、他の生存者がこの役職の人数以下になったら勝ち)
    public virtual bool IsKiller => false;

    // 本編のキルボタンを使えるか (インポスター以外の役職でキルさせる時に true)。既定はキル役なら使える
    public virtual bool CanKill => IsKiller;

    // 本編のどの役職の上に乗るか (ベントを使うなら Engineer など)。既定はクルー / インポスター
    public virtual RoleTypes BaseRole => Team == Team.Impostor ? RoleTypes.Impostor : RoleTypes.Crewmate;

    // 設定画面の「人数」の上限
    public virtual int MaxCount => 15;

    // ここから下は試合中のインスタンスだけが持つ
    public PlayerControl Player { get; internal set; }
    public byte PlayerId { get; internal set; }
    public bool IsLocal => Player && Player.AmOwner;

    // この役職が付いている間の寿命。役職が外れる (試合が終わる・配り直す) と切れる。
    // 役職の中で作った GameObject は Lifespan.Bind、自分で購読したイベントもこの寿命で
    public Lifespan Lifespan { get; internal set; }

    // 割り当てられた直後 (全員の端末で呼ばれる)。下の起きた事のメソッドはもう購読済み
    public virtual void OnAssigned() { }

    // 誰かの勝ちで試合が終わる時に、自分も一緒に勝つか (第三陣営の相乗り)。ホストの端末でだけ呼ばれる。
    // 自分だけで勝って試合を終わらせる時は GameEnd.Win(this) を呼ぶ
    public virtual bool AlsoWins(GameResult result) => false;

    // 視界の広さ (radius は本編が計算した値)。自分の画面の計算でだけ呼ばれる
    public virtual void ModifyVision(ref float radius) { }

    // キルの待ち時間 (seconds は部屋の設定の秒数)。自分の端末のほか、ホストがキルの依頼を確かめる時にも呼ぶので、
    // 設定値だけから決める (その端末の状態を見ない)
    public virtual void ModifyKillCooldown(ref float seconds) { }

    // 出現率と人数。設定画面の役職の欄の先頭に自動で付く
    internal IntOpt Chance;
    internal IntOpt Count;

    internal Tab Tab => Team switch
    {
        Team.Impostor => Tab.Impostor,
        Team.Neutral => Tab.Neutral,
        _ => Tab.Crew,
    };

    internal string ColoredName => $"<color={Color}>{Name}</color>";
}
