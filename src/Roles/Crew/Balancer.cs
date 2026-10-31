// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Crewmate/Balancer.cs (GPL-3.0)
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// バランサー (クルー): 会議中に 2 人を選ぶと「天秤」にかける。ほかの人の名札とスキップが消え、全員がその 2 人のどちらかへ投票し直す
// (投票の時間は設定の秒数)。票の多い方が追放され、同じ票なら 2 人とも追放される。途中で片方が死んだら残った方が追放される
public sealed class Balancer : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FF8000";
    public override Text Name => new("Balancer.Name");
    public override string Reading => "ばらんさー";
    public override Text Blurb => new("Balancer.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Balancer.Balance", "snr_balance") };

    public static readonly FloatOpt VoteTime = new("Balancer.VoteTime", 30f, 0f, 180f, 2.5f, "s");
    public static readonly IntOpt UseCount = new("Balancer.UseCount", 1, 1, 10);
    public static readonly BoolOpt RandomVote = new("Balancer.RandomVote", true);

    private static readonly string[] Titles =
        { "Balancer.Title.Either", "Balancer.Title.Average", "Balancer.Title.YouVote", "Balancer.Title.EitherExile",
          "Balancer.Title.WhoIsImpostor", "Balancer.Title.GetBalance", "Balancer.Title.PleaseVote" };

    // 今かけている天秤 (全員の端末で同じ)
    private static Balancer _active;
    private static byte _left = 255, _right = 255;
    private static float _timer;
    private static bool _ended;
    // 結果: 0 = 普通・1 = 2 人とも追放・2 = 片方が死んだ・3 = 2 人とも死んだ
    private static byte _result;
    private static byte _secondExile = 255;

    private byte _first = 255;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Balancer.Balance"), "snr_balance")
        {
            Target = AbilityTarget.Meeting,
            MaxUses = () => UseCount * 2,
            UsesPerMeeting = 2,
            CanTarget = (_, t) => _active == null && t.PlayerId != _first,
            OnUsed = (_, target) =>
            {
                if (_first == 255) { _first = target.PlayerId; return; }
                byte a = _first;
                _first = 255;
                Begin(this, a, target.PlayerId);
            },
        });
        Lifespan.OnRelease(() => { if (_active == this) Clear(); });
    }

    private static void Clear()
    {
        _active = null;
        _left = _right = 255;
        _ended = false;
    }

    // 全員の端末で: 天秤にかける
    private static void Begin(Balancer b, byte left, byte right)
    {
        var hud = MeetingHud.Instance;
        if (!hud || _active != null) return;
        _active = b;
        _left = left;
        _right = right;
        _ended = false;
        _result = 0;
        _secondExile = 255;
        _timer = VoteTime;
        foreach (var area in hud.playerStates)
        {
            if (!area) continue;
            area.UnsetVote();
            if (area.PlayerId != left && area.PlayerId != right) area.gameObject.SetActive(false);
        }
        if (hud.state == MeetingHud.MeetingStates.Voted) hud.state = MeetingHud.MeetingStates.NotVoted;
        if (hud.SkipVoteButton) hud.SkipVoteButton.gameObject.SetActive(false);
        int voting = GameOptionsManager.Instance.CurrentGameOptions.GetInt(AmongUs.GameOptions.Int32OptionNames.VotingTime);
        if (voting > 0) hud.discussionTimer = voting - VoteTime;
        if (hud.TitleText)
        {
            var tr = hud.TitleText.GetComponent<TextTranslatorTMP>();
            if (tr) tr.enabled = false;
            hud.TitleText.text = new Text(Titles[Random.Range(0, Titles.Length)]).ToString();
        }
        PortKit.Flash(new Color32(255, 128, 0, 255), 1f);
        PortKit.Notice(new Text("Balancer.Started").ToString());
    }

    public override void OnFixedUpdate()
    {
        if (_active != this) return;
        var hud = MeetingHud.Instance;
        if (!hud)
        {
            if (!ExileController.Instance) Clear();
            return;
        }
        if (hud.state is MeetingHud.MeetingStates.Results or MeetingHud.MeetingStates.Proceeding) return;
        foreach (var area in hud.playerStates)
            if (area && area.PlayerId != _left && area.PlayerId != _right && area.gameObject.activeSelf) area.gameObject.SetActive(false);
        if (hud.SkipVoteButton && hud.SkipVoteButton.gameObject.activeSelf) hud.SkipVoteButton.gameObject.SetActive(false);
        if (!AmongUsClient.Instance.AmHost) return;
        if (VoteTime > 0f) _timer -= Time.fixedDeltaTime;
        HostTryEnd(hud, VoteTime > 0f && _timer <= 0f);
    }

    private static bool Gone(byte id)
    {
        var info = GameData.Instance ? GameData.Instance.GetPlayerById(id) : null;
        return info == null || info.IsDead || info.Disconnected;
    }

    // ホスト: 本編が締めようとした時に呼ばれる。天秤の間は本編に締めさせない
    internal static bool HostCheck(MeetingHud hud)
    {
        if (_active == null) return false;
        HostTryEnd(hud, false);
        return true;
    }

    private static void HostTryEnd(MeetingHud hud, bool timeUp)
    {
        if (_ended || !hud) return;
        bool leftGone = Gone(_left), rightGone = Gone(_right);
        if (leftGone || rightGone)
        {
            _ended = true;
            byte survivor = !leftGone ? _left : !rightGone ? _right : (byte)255;
            Send(hud, new MeetingHud.VoterState[0], survivor, survivor == 255 ? (byte)3 : (byte)2, 255);
            return;
        }
        bool all = true;
        foreach (var a in hud.playerStates) if (a && !a.AmDead && !a.DidVote) { all = false; break; }
        if (!all && !timeUp) return;
        _ended = true;
        byte[] targets = { _left, _right };
        foreach (var a in hud.playerStates)
        {
            if (!a || a.AmDead) continue;
            byte v = a.VotedForId.Value;
            if (v == _left || v == _right) continue;
            if (RandomVote) a.SetVote(targets[Random.Range(0, 2)]);
            else a.SetVoteMissed();
        }
        int l = 0, r = 0;
        var rows = new List<MeetingHud.VoterState>();
        foreach (var a in hud.playerStates)
        {
            if (!a) continue;
            byte v = a.VotedForId.Value;
            if (v == _left) l++;
            else if (v == _right) r++;
            rows.Add(new MeetingHud.VoterState { VoterId = a.PlayerId, VotedForId = v });
        }
        if (l == r) Send(hud, rows.ToArray(), _left, 1, _right);
        else Send(hud, rows.ToArray(), l > r ? _left : _right, 0, 255);
    }

    private static void Send(MeetingHud hud, MeetingHud.VoterState[] rows, byte exiled, byte result, byte second)
    {
        ResultCall.Send((result, second));
        SetResult(result, second);
        var sent = new Il2CppStructArray<MeetingHud.VoterState>(rows.Length);
        for (int i = 0; i < rows.Length; i++) sent[i] = rows[i];
        var info = exiled != 255 && GameData.Instance ? GameData.Instance.GetPlayerById(exiled) : null;
        hud.RpcVotingComplete(sent, info, false, false, 0);
    }

    private static readonly RemoteCall<(byte result, byte second)> ResultCall = new("Balancer.Result", Route.HostToAll,
        (w, m) => { w.Write(m.result); w.Write(m.second); }, r => (r.ReadByte(), r.ReadByte()), (_, m) => SetResult(m.result, m.second));

    private static void SetResult(byte result, byte second)
    {
        _result = result;
        _secondExile = second;
    }

    // 追放の画面の文を変える
    private void OnExileShown(ExileShownEvent e)
    {
        if (_active != this || !e.Controller) return;
        string key = _result switch { 1 => "Balancer.DoubleExile", 2 => "Balancer.OnePlayerDead", 3 => "Balancer.NoPlayer", _ => null };
        if (key != null) e.Controller.completeString = new Text(key).ToString();
    }

    // 2 人とも追放の時は、もう 1 人も追放する (全員の端末で。本編の追放と同じく手元で)
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (_active != this) return;
        byte second = _secondExile;
        Clear();
        _secondExile = 255;
        if (second == 255) return;
        var p = PortKit.ById(second);
        if (PortKit.Alive(p)) p.Exiled();
    }

    public override string NameMark(PlayerControl seen) =>
        seen && seen.PlayerId == _first ? "<color=#FF8000>⚖</color>" : null;
}
