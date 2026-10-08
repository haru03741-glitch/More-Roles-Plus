using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles;

// 役職 (RoleBase) とアドオン (AddonBase) に共通の土台: 誰に付いているか・寿命・設定の出現率と人数・
// 試合で起きた事の購読 (引数がイベント 1 つのメソッド)・値を変える問い合わせ (視界・キルの待ち時間)。
// 1 人に役職は 1 つ、アドオンは複数付く。問い合わせは役職 → アドオン (付いた順) の順に重ねて呼ばれる
public abstract class Assignable
{
    // 保存と同期に使う名前。既定はクラス名 (変えると保存済みの設定値が引き継がれない)
    public virtual string Id => GetType().Name;

    // 色 ("#RRGGBB")
    public abstract string Color { get; }

    public abstract Text Name { get; }

    // 名前の読み (ひらがな)。設定画面の検索で、漢字の名前をかなで打っても見つかるようにする
    public virtual string Reading => null;

    // 設定画面の説明 (タスク欄の先頭にも出る)
    public abstract Text Description { get; }

    // 設定画面の「人数」の上限
    public virtual int MaxCount => 15;

    // 設定画面のどのタブに並ぶか
    internal abstract Tab Tab { get; }

    // ここから下は試合中のインスタンスだけが持つ
    public PlayerControl Player { get; internal set; }
    public byte PlayerId { get; internal set; }
    public bool IsLocal => Player && Player.AmOwner;

    // 付いている間の寿命。外れる (試合が終わる・配り直す) と切れる。
    // 中で作った GameObject は Lifespan.Bind、自分で購読したイベントもこの寿命で
    public Lifespan Lifespan { get; internal set; }

    // 割り当てられた直後 (全員の端末で呼ばれる)。起きた事のメソッドはもう購読済み
    public virtual void OnAssigned() { }

    // 視界の広さ (radius は本編が計算した値)。自分の画面の計算でだけ呼ばれる
    public virtual void ModifyVision(ref float radius) { }

    // キルの待ち時間 (seconds は部屋の設定の秒数)。自分の端末のほか、ホストがキルの依頼を確かめる時にも呼ぶので、
    // 設定値だけから決める (その端末の状態を見ない)
    public virtual void ModifyKillCooldown(ref float seconds) { }

    // 出現率と人数。設定画面の欄の先頭に自動で付く
    internal IntOpt Chance;
    internal IntOpt Count;

    internal string ColoredName => $"<color={Color}>{Name}</color>";
}
