// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Crewmate/Admiral.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// アドミラル (クルー): 会議中に「点呼」をかける。全員の画面が光り、生きている他の人は地図を押して自分のいた場所を答える。
// アドミラルが「締め切り」と、答えた人の色のピンが地図に並ぶ (アドミラルにだけ)。設定で、匿名投票でも投票先が見える
public sealed class Admiral : RoleBase
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#3D58B3";
    public override Text Name => new("Admiral.Name");
    public override string Reading => "あどみらる";
    public override Text Blurb => new("Admiral.Blurb");
    public override string Origin => "Uchu";
    public override bool CanBeMadmate => true;
    public override HelpSection[] HelpSections => new HelpSection[] { new("Admiral.Call", "admiral_call"), new("Admiral.Close", "admiral_close") };

    public static readonly IntOpt NumOfCall = new("Admiral.NumOfCall", 2, 1, 15);
    public static readonly BoolOpt VoteWatching = new("Admiral.VoteWatching", true);

    // 点呼の最中 (全員の端末で同じ)。どのアドミラルの点呼か
    private static byte _calling = 255;
    private static bool _answered;   // 自分の端末: もう答えた
    private readonly Dictionary<byte, Vector2> _answers = new(); // アドミラルの端末: 答え
    private bool _showPins;          // アドミラルの端末: 締め切った後、地図にピンを出す
    private readonly List<SpriteRenderer> _pins = new();

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Admiral.Call"), "admiral_call")
        {
            Target = AbilityTarget.Meeting,
            MeetingSelf = true,
            MaxUses = () => NumOfCall,
            UsesPerMeeting = 1,
            CanTarget = (_, _) => _calling == 255,
            OnUsed = (user, _) => StartCall(user.PlayerId),
        });
        AddAbility(new Ability(new Text("Admiral.Close"), "admiral_close")
        {
            Target = AbilityTarget.Meeting,
            MeetingSelf = true,
            // 会議の始めにボタンを作るので、狙いの条件は付けない (点呼の最中でなければ何もしない)
            OnUsed = (user, _) =>
            {
                if (_calling != user.PlayerId) return;
                _calling = 255;
                if (!IsLocal) return;
                _showPins = true;
                PortKit.Notice(new Text("Admiral.Result").With("n", _answers.Count).ToString());
                UchuKit.OpenMap();
            },
        });
    }

    private void StartCall(byte admiral)
    {
        _calling = admiral;
        _answered = false;
        _answers.Clear();
        _showPins = false;
        ClearPins();
        PortKit.Flash(new Color32(61, 88, 179, 160), 0.8f);
        var lp = PlayerControl.LocalPlayer;
        if (!PortKit.Alive(lp) || lp.PlayerId == admiral) return;
        PortKit.Notice(new Text("Admiral.Answer").ToString());
        UchuKit.OpenMap();
    }

    // 毎フレーム (自分の端末): 点呼の最中に地図を押すと答える
    internal static void FrameTick()
    {
        if (_calling == 255 || _answered || !MeetingHud.Instance) return;
        var lp = PlayerControl.LocalPlayer;
        if (!PortKit.Alive(lp) || lp.PlayerId == _calling) return;
        if (UchuKit.MapClick() is not Vector2 at) return;
        _answered = true;
        AnswerCall.Send((_calling, at.x, at.y));
        PortKit.Notice(new Text("Admiral.Answered").ToString());
    }

    private static readonly RemoteCall<(byte admiral, float x, float y)> AnswerCall = new("Admiral.Answer", Route.Anyone,
        (w, m) => { w.Write(m.admiral); w.Write(m.x); w.Write(m.y); }, r => (r.ReadByte(), r.ReadSingle(), r.ReadSingle()),
        (sender, m) =>
        {
            if (!sender || RoleState.Of(m.admiral) is not Admiral a || !a.IsLocal || _calling != m.admiral) return;
            a._answers[sender.PlayerId] = new Vector2(m.x, m.y);
        }, perSecond: 2);

    // アドミラルの地図: 答えた人のピン
    internal void UpdateMap(MapBehaviour map)
    {
        if (!_showPins || !map.HerePoint) return;
        if (_pins.Count > 0 && _pins[0]) return;
        _pins.Clear();
        foreach (var kv in _answers) _pins.Add(UchuKit.MapPin(map, PortKit.ById(kv.Key), kv.Value));
    }

    private void ClearPins()
    {
        foreach (var p in _pins) if (p) Object.Destroy(p.gameObject);
        _pins.Clear();
    }

    private void OnMeetingStart(MeetingStartEvent e)
    {
        _calling = 255;
        _showPins = false;
        if (IsLocal) ClearPins();
    }

    private void OnMeetingEnd(MeetingEndEvent e)
    {
        _calling = 255;
        _showPins = false;
        if (IsLocal) ClearPins();
    }

    internal static bool SeesVotes(PlayerControl p) => RoleState.Of(p) is Admiral && VoteWatching;
}

[HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.FixedUpdate))]
internal static class AdmiralMapPatch
{
    public static void Postfix(MapBehaviour __instance)
    {
        if (RoleState.Local is not Admiral a) return;
        try { a.UpdateMap(__instance); }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"admiral map: {e.Message}"); }
    }
}
