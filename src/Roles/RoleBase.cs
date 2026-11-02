using AmongUs.GameOptions;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles;

public enum Team
{
    Crew,
    Impostor,
    // インポスターと一緒に勝つクルー側の人 (生き残りの人数はクルーとして数え、タスクは勝ちに数えない)
    Madmate,
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
    // 陣営。役職のクラスは BaseTeam を書く。マッドメイトとして配られた人 (Madden) はマッドメイトになる
    public Team Team => Madden ? Team.Madmate : BaseTeam;
    public abstract Team BaseTeam { get; }

    // マッドメイトとしても配れる役職か (NoS の「役職の能力を持つマッドメイト」)。true にすると設定に
    // マッドメイトとして出る確率と人数が付く。クルーの役職だけ (配る席をクルーの席で選ぶため)。
    // WinsWithTeam・CanVent・ModifyVision・NameMark を上書きする役職は base を呼ぶ。試合中に役職が替わる (Change) と素の役職に戻る
    public virtual bool CanBeMadmate => false;

    // マッドメイトとして配られた (全員の端末で同じ・割り当ての直後に決まる)
    public bool Madden { get; internal set; }

    // マッドメイトとして出る確率と人数 (CanBeMadmate の役職だけ・Registry が作る)
    internal Options.IntOpt MadChance, MadCount;

    internal override bool Appears => base.Appears || MadChance != null && Crew.Madmate.MaddenRoles && MadChance.Value > 0;

    // 名前と色。マッドメイトとして配られた人は「マッド」を付けてマッドメイトの色にする
    public Text ShownName => Madden ? Text.Raw(new Text("Madmate.Prefix").ToString() + Name) : Name;
    public string ShownColor => Madden ? Crew.Madmate.MadColor : Color;
    internal string ShownColoredName => $"<color={ShownColor}>{ShownName}</color>";

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

    // ベントを使えるか (null = 本編の役職のまま)。インポスターの役職で false にすると使えなくなり、クルーの役職で true にすると
    // インポスターと同じように (待ち時間無しで) 使える。設定値だけから決める
    public virtual bool? CanVent => Madden && Crew.Madmate.Vent ? true : null;

    // 陣営チャットを使えるか。同じ陣営で true の人同士がつながる (既定はインポスターだけ)
    public virtual bool HasTeamChat => Team == Team.Impostor;

    // 生きている間は本人にも役職を見せない (本編の役職のままに見える)。死ぬと分かる
    public virtual bool HiddenWhileAlive => false;

    // 本編の役職そのもの (Roles/Builtin)。表示は本編に任せる
    internal virtual bool IsVanilla => false;

    // 幽霊になってから本編が配る役職 (MRP の配り方では配らない)
    internal virtual bool AssignedOnDeath => false;

    // 出るマップ (本編の MapId: 0 Skeld・1 Mira・2 Polus・4 Airship・5 Fungle)。null = どのマップでも
    public virtual byte[] OnlyMaps => null;

    internal bool AllowedOnMap()
    {
        var only = OnlyMaps;
        if (only == null || GameOptionsManager.Instance == null) return true;
        byte map = GameOptionsManager.Instance.CurrentGameOptions.MapId;
        return System.Array.IndexOf(only, map) >= 0;
    }

    // 試合の始めには配られず、他の役職の能力で変わる時だけ付く役職 (サイドキックなど)
    public virtual bool MadeByOthers => false;

    // 全員の画面で、この役職の人の名前の後ろに付く印 (スターなど)。null なら付けない
    public virtual string PublicMark => null;

    // viewer の画面で、この役職の人の名前の後ろに付く印 (見る人で変える時。既定は PublicMark)
    public virtual string MarkSeenBy(PlayerControl viewer) => PublicMark;

    // 付いているアドオン (割り当て後・付いた順)
    public System.Collections.Generic.IReadOnlyList<AddonBase> Addons => RoleState.AddonsOf(PlayerId);

    // この役職が出た試合には出ない役職 (どちらの側に書いてあっても効く)
    public virtual System.Type[] NotWith => null;

    // 設定で選んだ「一緒に出ない役職」(幽霊になってから付く役職は持たない)・Registry.Roles の中の番号
    internal RoleSetOpt Excluded;
    internal int Order = -1;

    // 誰かの勝ちで試合が終わる時に、自分も一緒に勝つか (第三陣営の相乗り)。ホストの端末でだけ呼ばれる。
    // 自分だけで勝って試合を終わらせる時は GameEnd.Win(this) を呼ぶ
    public virtual bool AlsoWins(GameResult result) => false;

    // 誰かの勝ちで試合が終わる時に、その勝ちを横取りするか (true の人だけが勝者になる)。ホストの端末で、相乗りの後に呼ばれる
    public virtual bool StealsWin(GameResult result) => false;

    // 横取りした時に一緒に勝者にする人 (スカーレットの本命・妖狐の仲間など)。StealsWin が true の役職でだけ読む
    public virtual System.Collections.Generic.IEnumerable<byte> StealsWith => null;

    // 決まった勝者からその人を外すか (スカーレットの本命など)。ホストの端末で、相乗り・横取りの後に呼ばれる
    public virtual bool BlocksWinOf(byte playerId, GameResult result) => false;

    // クルーのタスク勝利に数えないタスクの持ち主 (スカーレットの本命など)。全員の端末で読む
    public virtual System.Collections.Generic.IEnumerable<byte> IgnoresTasksOf => null;

    // 第三陣営の陣営の名前。同じ名前の役職は生き残りの数え方と勝ちで 1 つの陣営になる (ジャッカルとサイドキックなど)。
    // 既定はキル役なら自分の Id。陣営の中にキル役が生きている間だけ、キルしない仲間もその陣営として数える
    public virtual string Faction => IsKiller ? Id : null;

    // クルー / 第三陣営の姿のまま、別の陣営と一緒に勝つ (マッドメイトなら Team.Impostor)。自分の陣営の勝ちには入らない
    public virtual Team? WinsWithTeam => Madden ? Team.Impostor : null;

    // マッドメイトとして配られた人はマッドメイトの設定 (インポスターの視界・インポスターが分かる) も持つ
    public override void ModifyVision(ref float radius)
    {
        if (Madden) Crew.Madmate.ApplyVision(ref radius);
    }

    public override string NameMark(PlayerControl seen) => Madden ? Crew.Madmate.Mark(Player, seen) : null;

    // 第三陣営の陣営 (Faction) と一緒に勝つ (ジャッカルフレンズなら "Jackal")。自分の陣営の勝ちには入らない
    public virtual string WinsWithFaction => null;

    // タスクがクルーのタスク勝利に数えられるか (第三陣営と、別の陣営と勝つ役職は数えない)
    public virtual bool TasksCount => Team != Team.Neutral && WinsWithTeam == null && WinsWithFaction == null;

    // 勝敗を MRP が決める必要がある役職か (本編の判定では勝者が合わない)
    internal bool NeedsMrpEnd => Team == Team.Neutral || WinsWithTeam != null || WinsWithFaction != null || !TasksCount;

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
        Team.Madmate => Tab.Madmate,
        Team.Neutral => Tab.Neutral,
        _ => Tab.Crew,
    };
}
