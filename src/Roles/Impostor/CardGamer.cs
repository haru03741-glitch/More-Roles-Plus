using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// カードゲーマー (インポスター): 会議中に名札のボタンで相手を選ぶと、その人とカードバトルを始める。
// 勝てば相手が、負ければ (設定で) 自分が倒れる。バトルの間は全員が観戦になり、投票はできずチャットだけできる。
// 設定で、挑んだのが誰かを相手にも観戦者にも伏せられる (相手は見える)
public sealed class CardGamer : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("CardGamer.Name");
    public override string Reading => "かーどげーまー";
    public override Text Blurb => new("CardGamer.Blurb");
    public override HelpSection[] HelpSections => new HelpSection[] { new("CardGamer.Duel", "cardgamer_duel"), new("CardGamer.Rules", "cardgamer_duel") };

    public static readonly IntOpt Duels = new("CardGamer.Duels", 2, 1, 15);
    public static readonly IntOpt PerMeeting = new("CardGamer.PerMeeting", 1, 1, 5);
    public static readonly IntOpt Hp = new("CardGamer.Hp", 3, 1, 10);
    public static readonly IntOpt HandSize = new("CardGamer.HandSize", 4, 3, 6);
    public static readonly IntOpt StartEnergy = new("CardGamer.StartEnergy", 1, 0, 5);
    public static readonly IntOpt MaxEnergy = new("CardGamer.MaxEnergy", 5, 2, 10);
    public static readonly FloatOpt TurnTime = new("CardGamer.TurnTime", 15f, 5f, 60f, 5f, "s");
    public static readonly IntOpt MaxRounds = new("CardGamer.MaxRounds", 10, 3, 30);
    public static readonly BoolOpt Anonymous = new("CardGamer.Anonymous", true);
    public static readonly BoolOpt LoserDies = new("CardGamer.LoserDies", true);
    public static readonly ChoiceOpt DrawResult = new("CardGamer.DrawResult", 0,
        new Text("CardGamer.DrawResult.None"), new Text("CardGamer.DrawResult.Both"), new Text("CardGamer.DrawResult.Gamer"));
    public static readonly BoolOpt ShowHands = new("CardGamer.ShowHands", false);
    public static readonly BoolOpt PauseMeeting = new("CardGamer.PauseMeeting", true);
    public static readonly BoolOpt CanDuelImpostor = new("CardGamer.CanDuelImpostor", false);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("CardGamer.Duel"), "cardgamer_duel")
        {
            Target = AbilityTarget.Meeting,
            MaxUses = () => Duels,
            UsesPerMeeting = PerMeeting,
            CanTarget = (u, t) => !CardDuelHost.Busy && t.PlayerId != u.PlayerId && (CanDuelImpostor || !PortKit.IsImpostor(t)),
            OnUsed = CardDuelHost.Begin,
        });
    }
}

// ホストが進めるバトル。状態は全員へ配り、画面 (CardDuelView) はそれを映すだけ
internal static class CardDuelHost
{
    private static DuelState _live;     // ホストだけ
    private static float _phaseEnd, _botAt;
    private static System.Random _rng;
    private static System.IntPtr _meeting;
    private static bool _viewBusy;      // 全員: バトルの最中 (始まってから閉じるまで)

    internal const float RevealSeconds = 3.2f, EndSeconds = 4.5f;

    internal static bool Busy => _viewBusy;

    // 全員の端末で: ホストが通した後
    internal static void Begin(PlayerControl user, PlayerControl target)
    {
        if (!user || !target || !MeetingHud.Instance) return;
        _viewBusy = true;
        CardDuelView.Open(user.PlayerId, target.PlayerId);
        if (!AmongUsClient.Instance.AmHost) return;
        _rng = new System.Random(System.Environment.TickCount ^ user.PlayerId * 7919 ^ target.PlayerId);
        _meeting = MeetingHud.Instance.Pointer;
        var d = new DuelState
        {
            Gamer = user.PlayerId, Target = target.PlayerId,
            Phase = DuelPhase.Pick, Round = 1,
            MaxRounds = CardGamer.MaxRounds, MaxHp = CardGamer.Hp, MaxEnergy = CardGamer.MaxEnergy,
            Anonymous = CardGamer.Anonymous, ShowHands = CardGamer.ShowHands,
        };
        foreach (var (s, id) in new[] { (d.A, user.PlayerId), (d.B, target.PlayerId) })
        {
            s.Id = id;
            s.Hp = d.MaxHp;
            s.Energy = Mathf.Min((int)CardGamer.StartEnergy, d.MaxEnergy);
            s.Deck = CardBook.NewDeck(_rng);
            DuelState.Fill(s, CardGamer.HandSize, _rng);
        }
        _live = d;
        _phaseEnd = Time.time + CardGamer.TurnTime + 1.5f; // 始めの 1 回は配る演出の分だけ長く
        _botAt = Time.time + Random.Range(2f, 4f);
        Broadcast();
    }

    // ── 通信 ──

    private static readonly RemoteCall<(DuelState st, byte viewer)> StateCall = new("CardGamer.State", Route.HostToAll,
        (w, m) => m.st.Write(w, m.viewer), r => (DuelState.Read(r), (byte)0),
        (_, m) => CardDuelView.Apply(m.st));

    private static readonly RemoteCall<byte> PickCall = new("CardGamer.Pick", Route.ToHost,
        (w, v) => w.Write(v), r => r.ReadByte(),
        (sender, slot) => { if (sender) HostPick(sender.PlayerId, slot); }, perSecond: 6);

    private static readonly RemoteCall<byte> CloseCall = new("CardGamer.Close", Route.HostToAll,
        (w, v) => w.Write(v), r => r.ReadByte(), (_, _) => Closed());

    private static readonly RemoteCall<(byte gamer, byte target, byte winner, byte deaths, bool anon)> ResultCall = new("CardGamer.Result", Route.HostToAll,
        (w, m) => { w.Write(m.gamer); w.Write(m.target); w.Write(m.winner); w.Write(m.deaths); w.Write(m.anon); },
        r => (r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadBoolean()),
        (_, m) => Announce(m.gamer, m.target, m.winner, m.deaths, m.anon));

    // 自分の端末から: 手札の slot 番目 (CardBook.Charge = ためる) を出す
    internal static void Pick(byte slot) => PickCall.Send(slot);

    // 人ごとに見せる手札が違うので、1 人ずつ送る (ホストの手元は写しを直接渡す)
    private static void Broadcast()
    {
        var d = _live;
        if (d == null) return;
        d.Left = Mathf.Max(0f, _phaseEnd - Time.time);
        var client = AmongUsClient.Instance;
        var sent = new HashSet<int>();
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.AmOwner || Dev.Dummy.Owns(p)) continue;
            int cid = p.OwnerId;
            if (cid == client.ClientId || !sent.Add(cid)) continue;
            StateCall.Send((d, p.PlayerId), cid);
        }
        var lp = PlayerControl.LocalPlayer;
        if (lp) CardDuelView.Apply(d.CopyFor(lp.PlayerId));
    }

    private static void HostPick(byte who, byte slot)
    {
        var d = _live;
        if (d == null || d.Phase != DuelPhase.Pick) return;
        var s = d.Of(who);
        if (s == null || s.Picked || !DuelState.CanPlay(s, slot)) return;
        s.Pick = slot;
        s.Picked = true;
        // 二人とも出したら少しだけ待って開く (伏せたカードが出揃う所を見せる)
        if (d.A.Picked && d.B.Picked) _phaseEnd = Mathf.Min(_phaseEnd, Time.time + 0.6f);
        Broadcast();
    }

    // ── 進め方 (ホストの毎フレーム) ──

    internal static void Tick()
    {
        var d = _live;
        if (d == null) return;
        var hud = MeetingHud.Instance;
        // 会議が閉じた・別の会議になった時は、誰も倒さずにやめる
        if (!hud || hud.Pointer != _meeting || hud.state is MeetingHud.VoteStates.Results or MeetingHud.VoteStates.Proceeding)
        {
            _live = null;
            CloseCall.Send(0);
            Closed();
            return;
        }
        float now = Time.time;
        if (d.Phase == DuelPhase.Pick)
        {
            // 中の人のいない相手 (ダミー・抜けた人) は少し考えてから出す
            if (now >= _botAt)
            {
                _botAt = now + Random.Range(1.5f, 3.5f);
                foreach (var s in new[] { d.A, d.B })
                    if (!s.Picked && IsBot(s.Id)) HostPick(s.Id, BotChoice(d, s));
            }
            if (now < _phaseEnd && !(d.A.Picked && d.B.Picked)) return;
            d.Resolve();
            d.Phase = DuelPhase.Reveal;
            _phaseEnd = now + RevealSeconds;
            Broadcast();
            return;
        }
        if (now < _phaseEnd) return;
        if (d.Phase == DuelPhase.Reveal)
        {
            if (d.Decided())
            {
                d.Phase = DuelPhase.End;
                _phaseEnd = now + EndSeconds;
                Broadcast();
                return;
            }
            d.Round++;
            foreach (var s in new[] { d.A, d.B })
            {
                s.Shown = CardBook.None;
                s.Took = s.Healed = s.Gained = s.Lost = 0;
                DuelState.Fill(s, CardGamer.HandSize, _rng);
            }
            d.Phase = DuelPhase.Pick;
            _phaseEnd = now + CardGamer.TurnTime;
            _botAt = now + Random.Range(1.5f, 4f);
            Broadcast();
            return;
        }
        // 終わり: 倒す人を決めて閉じる
        _live = null;
        Finish(d);
        CloseCall.Send(0);
        Closed();
    }

    private static bool IsBot(byte id)
    {
        var p = PortKit.ById(id);
        return !p || p.Data == null || p.Data.Disconnected || Dev.Dummy.Owns(p) && !p.AmOwner;
    }

    // 中の人のいない人の手: エナジーが少なければためがち、HP が減っていれば手当、あとは出せる中から適当に
    private static byte BotChoice(DuelState d, DuelSide s)
    {
        var can = new List<byte>();
        for (byte i = 0; i < s.Hand.Count; i++)
            if (DuelState.CanPlay(s, i)) can.Add(i);
        if (can.Count == 0 || s.Energy == 0 && Random.value < 0.45f) return CardBook.Charge;
        if (Random.value < 0.18f && s.Energy < d.MaxEnergy) return CardBook.Charge;
        foreach (var i in can)
            if (CardBook.Get(s.Hand[i]).Kind == CardKind.Heal && s.Hp < d.MaxHp && Random.value < 0.7f) return i;
        return can[Random.Range(0, can.Count)];
    }

    private static void Finish(DuelState d)
    {
        var gamer = PortKit.ById(d.A.Id);
        var target = PortKit.ById(d.B.Id);
        var dead = new List<PlayerControl>();
        if (d.Winner == d.A.Id) dead.Add(target);
        else if (d.Winner == d.B.Id) { if (CardGamer.LoserDies) dead.Add(gamer); }
        else
        {
            int mode = CardGamer.DrawResult.Value;
            if (mode == 1) { dead.Add(gamer); dead.Add(target); }
            else if (mode == 2) dead.Add(gamer);
        }
        byte deaths = 0;
        foreach (var p in dead)
        {
            if (!PortKit.Alive(p)) continue;
            deaths |= (byte)(p == gamer ? 1 : 2);
            PortKit.HostVanish(p == target && gamer ? gamer : p, p);
        }
        ResultCall.Send((d.A.Id, d.B.Id, d.Winner, deaths, d.Anonymous));
        Announce(d.A.Id, d.B.Id, d.Winner, deaths, d.Anonymous);
    }

    // 全員の端末で: 結果をチャットに出す (伏せる設定なら、カードゲーマーの名前は出さない)
    private static void Announce(byte gamerId, byte targetId, byte winner, byte deaths, bool anon)
    {
        var lp = PlayerControl.LocalPlayer;
        string Name(byte id)
        {
            if (anon && id == gamerId && (!lp || lp.PlayerId != gamerId)) return new Text("CardGamer.Unknown").ToString();
            var p = PortKit.ById(id);
            return p && p.Data != null ? p.Data.PlayerName : "?";
        }
        string msg = winner == 255
            ? new Text("CardGamer.ResultDraw").ToString()
            : new Text("CardGamer.ResultWin").With("name", Name(winner)).ToString();
        if ((deaths & 1) != 0) msg += "\n" + new Text("CardGamer.ResultDied").With("name", Name(gamerId));
        if ((deaths & 2) != 0) msg += "\n" + new Text("CardGamer.ResultDied").With("name", Name(targetId));
        PortKit.Notice(msg);
    }

    private static void Closed()
    {
        _viewBusy = false;
        CardDuelView.Close();
    }

    // 試合の終わり・会議の終わりに片付ける
    internal static void Reset()
    {
        _live = null;
        _viewBusy = false;
    }
}
