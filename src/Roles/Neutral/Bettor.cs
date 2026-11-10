// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Neutral/Bettor.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using System.Collections.Generic;
using System.Linq;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using MoreRolesPlus.UI;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// ベッター (第三陣営): 「推測」で他の全員の役職を予想して書き込み、全員分を埋めて賭けると判定が出る
// (HIT = 役職が当たり・BLOW = その役職は試合にいる・MISS = いない)。賭けは 1 ターンに 1 回。HIT の人の役職は見える。
// 「分析」で近くの人の陣営が分かる。HIT が半分以上になると、会議で 1 人の役職を見る「特定」が使える。
// 会議の終わりに全員 HIT なら単独で勝つ
public sealed class Bettor : RoleBase
{
    public override Team BaseTeam => Team.Neutral;
    public override string Color => "#F79D48";
    public override Text Name => new("Bettor.Name");
    public override string Reading => "べったー";
    public override Text Blurb => new("Bettor.Blurb");
    public override string Origin => "Uchu";
    public override HelpSection[] HelpSections => new HelpSection[]
        { new("Bettor.Guess", "bettor_guess"), new("Bettor.Analyze", "bettor_analyze"), new("Bettor.Identify", "bettor_identify") };

    public static readonly IntOpt DisableGuessTurn = new("Bettor.DisableGuessTurn", 1, 0, 10);
    public static readonly FloatOpt AnalyzeCooldown = new("Bettor.AnalyzeCooldown", 15f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt NumOfAnalyze = new("Bettor.NumOfAnalyze", 3, 1, 15);
    public static readonly BoolOpt IdentifyDead = new("Bettor.IdentifyDead", true);
    public static readonly FloatOpt IdentifyPercentage = new("Bettor.IdentifyPercentage", 50f, 0f, 100f, 10f, "%"); // 看破が使えるようになる当たり率

    private enum Judge : byte { None, Hit, Blow, Miss }

    private readonly Dictionary<byte, RoleBase> _guess = new();   // 自分の端末: 書き込んだ予想
    private readonly Dictionary<byte, Judge> _judge = new();       // 自分の端末: 最後の判定
    private readonly HashSet<string> _exists = new(), _absent = new(); // 自分の端末: いると分かった・いないと分かった役職
    private int _turn, _betTurn = -1;
    private bool _allHit;     // 全員の端末で同じ (賭けた時に配る)
    private float _hitRate;   // 全員の端末で同じ
    private bool _gotIdentify; // 全員の端末で同じ: 当たり率が設定に届いたことがある (原作 gottenIdentify・一度届けば下がっても使える)
    private readonly Dictionary<byte, string> _analyzed = new(); // 自分の端末: 分析で分かった陣営 (溜めて名札の横に出す)
    private readonly HashSet<byte> _identified = new();          // 自分の端末: 看破した人 (役職が見える)
    private readonly HashSet<byte> _scramble = new();            // ほかの人の端末: この会議で看破された人 (名札の横に文字化けを出す)
    private static readonly System.Random Noise = new();

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Bettor.Guess"), "bettor_guess")
        {
            CooldownAtStart = false,
            OnUse = () => { OpenWindow(); return false; },
        });
        AddAbility(new Ability(new Text("Bettor.Analyze"), "bettor_analyze")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => AnalyzeCooldown,
            MaxUses = () => NumOfAnalyze,
            CanTarget = (_, t) => !(_judge.TryGetValue(t.PlayerId, out var j) && j == Judge.Hit), // HIT 済みの人はもう役職が見えるので選べない
            OnUsed = (user, t) =>
            {
                if (!user.AmOwner) return;
                var r = RoleState.Of(t);
                string team = r == null ? "?" : PortKit.IsImpostor(t) ? new Text("Bettor.TeamImpostor").ToString() : new Text("Bettor.Team" + r.Team).ToString();
                string col = r == null ? "#AAAAAA" : PortKit.IsImpostor(t) ? "#FF1919" : r.Team == Team.Crew ? "#8CFFFF" : "#F4D335";
                _analyzed[t.PlayerId] = $"<color={col}>{team}</color>";
                PortKit.Notice(new Text("Bettor.Analyzed").With("name", t.Data.PlayerName).With("team", team).ToString());
            },
        });
        AddAbility(new Ability(new Text("Bettor.Identify"), "bettor_identify")
        {
            Target = AbilityTarget.Meeting,
            MaxUses = () => 1,
            MeetingAnyLife = IdentifyDead,
            CanTarget = (_, t) => (_gotIdentify || _hitRate >= IdentifyPercentage / 100f) && !_identified.Contains(t.PlayerId),
            OnUsed = (user, t) =>
            {
                if (!user.AmOwner) { _scramble.Add(t.PlayerId); return; } // ほかの人には、この会議のあいだ名札の横が文字化けして見える
                _identified.Add(t.PlayerId);
                var r = RoleState.Of(t);
                PortKit.Notice(new Text("Bettor.Identified").With("name", t.Data.PlayerName).With("role", r != null ? r.ShownColoredName : "?").ToString());
            },
        });
        // 看破された人の名札の横の文字化け (看破した本人以外の端末・会議の間だけ)
        RoleFrame.AddExtraMark(Lifespan, seen => !IsLocal && MeetingHud.Instance && _scramble.Contains(seen.PlayerId) ? Scrambled() : null);
    }

    private static string Scrambled()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz!#$%&=^@:+-";
        var c = new char[8];
        for (int i = 0; i < c.Length; i++) c[i] = chars[Noise.Next(chars.Length)];
        return $" <size=70%><color=#999999>{new string(c)}</color></size>";
    }

    private void OnMeetingEnd(MeetingEndEvent e)
    {
        _turn++;
        _scramble.Clear();
    }

    private List<PlayerControl> Others() =>
        PlayerControl.AllPlayerControls.ToArray().Where(p => p && p.Data != null && !p.Data.Disconnected && p.PlayerId != PlayerId).ToList();

    // ---- 予想の窓 ----

    private UiWindow _win;

    private void OpenWindow()
    {
        if (MeetingHud.Instance) return;
        _win = UiWindow.Open("Bettor", new Vector2(8.6f, 4.8f), () => _win = null);
        if (_win == null) return;
        ShowPlayers();
    }

    private void ShowPlayers()
    {
        if (_win == null || !_win.Root) return;
        var rows = new Stack(false) { Gap = 0.06f };
        foreach (var p in Others())
        {
            var pc = p;
            _guess.TryGetValue(pc.PlayerId, out var g);
            _judge.TryGetValue(pc.PlayerId, out var j);
            string jt = j == Judge.None ? "" : j == Judge.Hit ? "<color=#40FF40>HIT</color>" : j == Judge.Blow ? "<color=#FFD040>BLOW</color>" : "<color=#FF5050>MISS</color>";
            rows.Add(new Stack(true,
                new Label(pc.Data.PlayerName, 1.5f) { Width = 2.2f, NoWrap = true },
                new Button(g != null ? g.ShownColoredName : new Text("Bettor.NoGuess").ToString(), () => ShowRoles(pc.PlayerId), 1.4f),
                new Label(jt, 1.4f) { Width = 1.2f }) { Gap = 0.15f });
        }
        bool full = Others().All(p => _guess.ContainsKey(p.PlayerId));
        bool can = full && _turn >= DisableGuessTurn && _betTurn != _turn;
        string why = _turn < DisableGuessTurn ? new Text("Bettor.TooEarly").ToString()
            : _betTurn == _turn ? new Text("Bettor.AlreadyBet").ToString()
            : !full ? new Text("Bettor.FillAll").ToString() : "";
        var body = new Stack(false,
            new Label(new Text("Bettor.Title").ToString(), 2.2f) { Bold = true, MenuFont = true, Color = new Color32(247, 157, 72, 255) },
            new Stack(true,
                new ScrollView(new Vector2(5.4f, 3.2f), rows),
                new Stack(false,
                    new Button(new Text("Bettor.Bet").ToString(), () => { if (can) Bet(); }, 1.6f),
                    new Label(why, 1.2f) { Width = 2.4f, Color = UnityEngine.Color.gray },
                    new Button(new Text("Bettor.RoleList").ToString(), ShowKnown, 1.4f)) { Gap = 0.15f }) { Gap = 0.2f })
        { Gap = 0.1f };
        _win.Body.Set(body);
    }

    // 試合に出うる役職の一覧から選ぶ
    private void ShowRoles(byte target)
    {
        if (_win == null || !_win.Root) return;
        var list = new Stack(false) { Gap = 0.04f };
        foreach (var team in new[] { Team.Crew, Team.Impostor, Team.Neutral, Team.Madmate })
            foreach (var r in Registry.Roles.Where(r => r.Team == team && (r.Appears || r.MadeByOthers || r.IsVanilla)))
            {
                var role = r;
                list.Add(new Button(role.ColoredName, () => { _guess[target] = role; ShowPlayers(); }, 1.4f));
            }
        var body = new Stack(false,
            new Label(new Text("Bettor.PickRole").With("name", PortKit.ById(target)?.Data?.PlayerName ?? "?").ToString(), 1.8f) { Bold = true },
            new ScrollView(new Vector2(7.6f, 3.4f), list),
            new Button(new Text("Bettor.Back").ToString(), ShowPlayers, 1.4f))
        { Gap = 0.1f };
        _win.Body.Set(body);
    }

    // 分かっている役職 (いる・いない)
    private void ShowKnown()
    {
        if (_win == null || !_win.Root) return;
        string Names(IEnumerable<string> ids) =>
            string.Join("、", ids.Select(id => Registry.Roles.Find(r => r.Id == id)?.ColoredName ?? id));
        var body = new Stack(false,
            new Label(new Text("Bettor.Exists").ToString(), 1.7f) { Bold = true },
            new Label(_exists.Count > 0 ? Names(_exists) : "-", 1.4f) { Width = 7.6f },
            new Label(new Text("Bettor.Absent").ToString(), 1.7f) { Bold = true },
            new Label(_absent.Count > 0 ? Names(_absent) : "-", 1.4f) { Width = 7.6f },
            new Button(new Text("Bettor.Back").ToString(), ShowPlayers, 1.4f))
        { Gap = 0.12f };
        _win.Body.Set(body);
    }

    // 賭ける: 自分の端末で判定し、結果 (HIT の割合・全員 HIT か) を全員へ配る
    private void Bet()
    {
        _betTurn = _turn;
        int hit = 0, n = 0;
        var inPlay = new HashSet<string>(RoleState.All.Select(r => r.Id));
        foreach (var p in Others())
        {
            n++;
            var g = _guess[p.PlayerId];
            var real = RoleState.Of(p);
            Judge j = real != null && real.Id == g.Id ? Judge.Hit : inPlay.Contains(g.Id) ? Judge.Blow : Judge.Miss;
            _judge[p.PlayerId] = j;
            if (j == Judge.Hit) hit++;
            if (j != Judge.Miss) _exists.Add(g.Id); else _absent.Add(g.Id);
        }
        float rate = n == 0 ? 0f : (float)hit / n;
        ResultCall.Send(((byte)(rate * 100f), hit == n && n > 0));
        SetResult(rate, hit == n && n > 0);
        PortKit.Notice(new Text("Bettor.Result").With("hit", hit).With("n", n).ToString());
        ShowPlayers();
    }

    private static readonly RemoteCall<(byte rate, bool all)> ResultCall = new("Bettor.Result", Route.Anyone,
        (w, m) => { w.Write(m.rate); w.Write(m.all); }, r => (r.ReadByte(), r.ReadBoolean()),
        (sender, m) => { if (RoleState.Of(sender) is Bettor b) b.SetResult(m.rate / 100f, m.all); }, perSecond: 2);

    private void SetResult(float rate, bool all)
    {
        _hitRate = rate;
        _allHit = all;
        if (rate >= IdentifyPercentage / 100f) _gotIdentify = true;
    }

    // HIT の人の役職は見える
    // HIT の人・看破した人は役職、分析した人は陣営 (溜まって、いつでも見返せる)
    public override string NameMark(PlayerControl seen)
    {
        bool hit = _judge.TryGetValue(seen.PlayerId, out var j) && j == Judge.Hit;
        if ((hit || _identified.Contains(seen.PlayerId)) && RoleState.Of(seen) is { } r) return $"<size=70%>{r.ShownColoredName}</size>";
        return _analyzed.TryGetValue(seen.PlayerId, out var team) ? $" <size=65%>({team})</size>" : null;
    }

    // ホスト: 会議の終わりに全員 HIT なら勝ち
    [HostOnly]
    private void OnMeetingEndWin(MeetingEndEvent e)
    {
        if (_allHit && PortKit.Alive(Player)) PortKit.After(0.5f, () => { if (PortKit.Alive(Player)) GameEnd.Win(this); });
    }
}
