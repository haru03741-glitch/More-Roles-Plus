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

    // 試合の情報 (右上の「?」) と /r に出す長い説明。文のファイルに「<Id>.Summary」があれば部品から組み立て (RoleDoc)、
    // 無ければ「<Id>.Help」、それも無ければ設定画面の説明
    public virtual Text Help => Text.Raw(RoleDoc.OriginLine(this) + (RoleDoc.Has(this) ? RoleDoc.Compose(this)
        : Tr.Has(Id + ".Help") ? new Text(Id + ".Help").ToString() : Description.ToString()));

    // この役職を最初に作った Among Us の mod (RoleDoc.Origins の鍵・複数ならカンマ区切り)。説明の先頭に「From:」で出す。
    // MRP で作った役職は null のまま
    public virtual string Origin => null;

    // 長い説明の節。ボタンの名前の鍵を押す順に並べ、試合の情報で並べるボタンの絵 (ButtonIcons の名前) を添える。
    // ボタンでない仕組みも節にしてよい (絵は無くてよい)
    //   public override HelpSection[] HelpSections => new HelpSection[] { new("Demolisher.Hammer", "hammer") };
    public virtual HelpSection[] HelpSections => null;

    // 節のボタンの絵。既定は ButtonIcons の名前から (本編の役職は本編の絵を返す)
    public virtual UnityEngine.Sprite SectionIcon(HelpSection s) => s.Icon != null ? ButtonIcons.Get(s.Icon) : null;

    // 設定画面の「人数」の上限
    public virtual int MaxCount => Options.Crowded.MaxPlayers;

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

    // 本編の FixedUpdate ごと (1 回の間隔は Time.fixedDeltaTime)。上書きした役職だけが呼ばれる (会議中も・死んだ後も呼ばれるので要るなら自分で見る)。
    // 既定は全員の端末で呼ぶ。[LocalOnly] を付けると持ち主の端末だけ、[HostOnly] を付けるとホストの端末だけ。
    // 中で Unity の計算 (Vector2 の演算・Mathf など) を毎回するとゴミが出て重くなるので、要る時だけにする
    public virtual void OnFixedUpdate() { }

    // 自分の画面で、seen の名前のすぐ後ろに足す文字 (null か空 = 足さない)。試合中と会議の名札の両方に出る。
    // 上書きした役職の持ち主の端末でだけ 0.25 秒ごとに呼ばれ、文字が変わった時だけ書き直す
    //   public override string NameMark(PlayerControl seen) => seen.PlayerId == _marked ? "<color=#FF0000>★</color>" : null;
    public virtual string NameMark(PlayerControl seen) => null;

    // キルの待ち時間 (seconds は部屋の設定の秒数)。自分の端末のほか、ホストがキルの依頼を確かめる時にも呼ぶので、
    // 設定値だけから決める (その端末の状態を見ない)
    public virtual void ModifyKillCooldown(ref float seconds) { }

    // 会議で入れた票が何票分になるか (weight は 1 から始まる・0 で数えない)。ホストが票を数える時に呼ぶ。
    // 投票先を変える・票を足す時は VoteTallyEvent で
    public virtual void ModifyVoteWeight(ref int weight) { }

    // 時戻しで戻す手元の値 (ゲージなど) の数。全員の端末で 0.25 秒ごとに SaveRewind で書き、戻す時に同じ数の欄で LoadRewind が呼ばれる。
    // 能力の回数・待ち時間・位置は時戻しが自分で戻すので、それ以外の役職だけが持つ値を書く。数は試合の間変えない
    internal virtual int RewindFloats => 0;
    internal virtual void SaveRewind(System.Span<float> into) { }
    internal virtual void LoadRewind(System.ReadOnlySpan<float> from) { }

    // 時戻しで戻し終わった後 (全員の端末で)。Tracked の値はもう戻っているので、それに合わせて見た目を作り直す
    public virtual void OnRewound() { }

    private ITracked[] _tracked;
    internal ITracked[] TrackedValues => _tracked ??= TrackedFields.Of(this);

    // 出現率と人数。設定画面の欄の先頭に自動で付く
    internal IntOpt Chance;
    internal IntOpt Count;

    internal string ColoredName => $"<color={Color}>{Name}</color>";
}

// 長い説明の 1 節: 見出しの鍵 (本文 = 鍵 + ".Help"・試合の情報の 1 行 = 鍵 + ".Short") とボタンの絵の名前 (無ければ null)
public readonly struct HelpSection
{
    public readonly string Key;
    public readonly string Icon;

    public HelpSection(string key, string icon = null)
    {
        Key = key;
        Icon = icon;
    }
}
