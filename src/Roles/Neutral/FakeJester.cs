// Ported from https://github.com/rar006/TownOfHost-hamo Roles/Neutral/FakeJester.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Neutral;

// フェイクジェスター (第三陣営): 自分はジェスターだと思い込んでいる。決めた数の会議が始まると、本当はフェイクジェスターだと自覚する。
// 自覚するまでは、本人の画面 (イントロ・役職名・タスク欄・説明) も、役職が見える人の画面もジェスターのまま。
// 自覚した後は本人にはフェイクジェスター、他の人には「ジェスター/フェイクジェスター」と見える。
// 自覚したまま試合の終わりに生きていれば勝ち (勝った陣営に相乗り / 単独で勝ちを奪う)。追放されても勝ちにならない
public sealed class FakeJester : RoleBase
{
    public override Team BaseTeam => Team.Neutral;
    public override string Color => "#EC62A5";
    public override Text Name => Disguised ? Jester.Name : _awake && !IsLocal && !_revealed
        ? Text.Raw($"{Jester.Name}/{new Text("FakeJester.Name")}") : new Text("FakeJester.Name");
    public override string Reading => "ふぇいくじぇすたー";
    public override Text Blurb => Disguised ? Jester.Blurb : new Text("FakeJester.Blurb");
    public override Text Help => Disguised ? Jester.Help : base.Help;
    public override string Origin => Disguised ? Jester.Origin : "TOH-hamo";

    public static readonly IntOpt AwakenMeetings = new("FakeJester.AwakenMeetings", 3, 3, 5);
    public static readonly BoolOpt AdditionalWin = new("FakeJester.AdditionalWin", true);

    private int _meetings;
    private bool _awake, _revealed;

    // 試合の中で、まだ自覚していない (設定画面の見本と試合の終わりの後は本当の名前)
    private bool Disguised => Player && !_awake && !_revealed;

    private static RoleBase Jester => RoleState.Proto<Neutral.Jester>();

    public override RoleBase ShownAs => Disguised ? Jester : this;

    public override System.Type[] NotWith => new[] { typeof(Survivor) };

    private void OnMeetingStart(MeetingStartEvent e)
    {
        if (_awake || ++_meetings < AwakenMeetings) return;
        _awake = true;
        if (!IsLocal) return;
        PortKit.Notice(new Text("FakeJester.Awaken").ToString());
        PortKit.Flash(RoleDisplay.ParseColor(Color), 0.8f);
        RoleDisplay.Relabel(this);
        StatusEffects.ShowUntil("FakeJester", new Text("FakeJester.AwakenedLabel"), Color, Text.Raw(""));
    }

    private void OnGameEnd(GameEndEvent e) => _revealed = true;

    public override bool AlsoWins(GameResult result) => AdditionalWin && Won;
    public override bool StealsWin(GameResult result) => !AdditionalWin && Won;

    private bool Won => _awake && PortKit.Alive(Player);
}
