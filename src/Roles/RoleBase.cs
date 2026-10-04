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
// - 試合中の処理は下の On〜 / Modify〜 を上書きする。
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

    // 本編のどの役職の上に乗るか (ベントを使うなら Engineer など)。既定はクルー / インポスター
    public virtual RoleTypes BaseRole => Team == Team.Impostor ? RoleTypes.Impostor : RoleTypes.Crewmate;

    // 設定画面の「人数」の上限
    public virtual int MaxCount => 15;

    // ここから下は試合中のインスタンスだけが持つ
    public PlayerControl Player { get; internal set; }
    public byte PlayerId { get; internal set; }
    public bool IsLocal => Player && Player.AmOwner;

    // 割り当てられた直後 (全員の端末で呼ばれる)
    public virtual void OnAssigned() { }

    // 試合が終わった時 (全員の端末で呼ばれる)
    public virtual void OnGameEnd() { }

    // 視界の広さ (radius は本編が計算した値)。自分の画面の計算でだけ呼ばれる
    public virtual void ModifyVision(ref float radius) { }

    // キルの待ち時間 (seconds は本編が設定しようとしている秒数)。自分の端末でだけ呼ばれる
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
