// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Crewmate/Shaman.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to https://github.com/yukieiji/ExtremeRoles (GPL-3.0)
using System.Collections.Generic;
using System.Linq;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// シャーマン (クルー): 無作為の部屋 (換気室以外) への矢印が出る。その部屋で止まって「降霊術」を使う (動くと中断)。
// 成功すると次の会議で、生きている非クルーメイトの数が分かる。タスクを決めた数だけ終えると、その役職名も分かる
public sealed class Shaman : RoleBase
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#CA50F2";
    public override Text Name => new("Shaman.Name");
    public override string Reading => "しゃーまん";
    public override Text Blurb => new("Shaman.Blurb");
    public override string Origin => "Uchu, ER";
    public override bool CanBeMadmate => true;
    public override HelpSection[] HelpSections => new HelpSection[] { new("Shaman.Necromancy", "shaman_necromancy") };

    public static readonly FloatOpt NecromancyCooldown = new("Shaman.NecromancyCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt NecromancyDuration = new("Shaman.NecromancyDuration", 3f, 0.25f, 10f, 0.25f, "s");
    public static readonly IntOpt NumOfNecromancy = new("Shaman.NumOfNecromancy", 1, 1, 15);
    public static readonly BoolOpt ResultViewAlivePlayers = new("Shaman.ResultViewAlivePlayers", false);
    public static readonly BoolOpt TaskCompletesRoomReroll = new("Shaman.TaskCompletesRoomReroll", false);
    public static readonly BoolOpt SkillBoost = new("Shaman.SkillBoost", true);
    public static readonly IntOpt NumOfBoostTask = new("Shaman.NumOfBoostTask", 8, 1, 12) { When = () => SkillBoost };

    private PlainShipRoom _room;
    private WorldArrow _arrow;
    private Vector2 _castAt;
    private bool _casting, _failed;
    private string _pending;                          // 降霊が成功した時点で決めた結果 (次の会議で見せる)
    private readonly List<string> _marks = new();     // 今までの結果の短い形 (会議の間、自分の名札の横に出し続ける)
    private string _pendingMark;
    private Ability _ability;

    public override void OnAssigned()
    {
        _ability = AddAbility(new Ability(new Text("Shaman.Necromancy"), "shaman_necromancy")
        {
            Cooldown = () => NecromancyCooldown,
            Duration = () => NecromancyDuration,
            MaxUses = () => NumOfNecromancy,
            Usable = () => InRoom(), // 使えない間はボタンを暗くする
            OnUse = () =>
            {
                if (!InRoom()) return false;
                _casting = true;
                _failed = false;
                _castAt = Player.GetTruePosition();
                SpiritFx.LocalBreath(Player);
                return true;
            },
            OnEnd = () =>
            {
                SpiritFx.LocalStop(Player);
                if (!_casting) return;
                _casting = false;
                if (_failed || MeetingHud.Instance) return;
                MakeResult(); // 数えるのは成功した時点 (次の会議の始まりではない)
                PortKit.Notice(new Text("Shaman.Done").ToString());
                Reroll();
            },
        });
        if (IsLocal) _arrow = new WorldArrow(Lifespan, new Color32(202, 80, 242, 255));
    }

    internal PlainShipRoom Room => _room;

    private bool InRoom()
    {
        if (!_room || !_room.roomArea) return false;
        return _room.roomArea.OverlapPoint(Player.GetTruePosition());
    }

    private void Reroll()
    {
        var ship = ShipStatus.Instance;
        if (!ship) return;
        var rooms = ship.AllRooms.Where(r => r && r.roomArea && r.RoomId != SystemTypes.Ventilation && r.RoomId != SystemTypes.Hallway && r.RoomId != _room?.RoomId).ToList();
        if (rooms.Count == 0) return;
        _room = rooms[Random.Range(0, rooms.Count)];
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (!ShipStatus.Instance) return;
        if (!_room) Reroll();
        bool show = _room && PortKit.Alive(Player) && !MeetingHud.Instance && _ability.UsesLeft != 0;
        _arrow?.SetActive(show);
        if (show) _arrow.Update(_room.roomArea.bounds.center);
        // 動くと中断
        if (_casting && Vector2.Distance(Player.GetTruePosition(), _castAt) > 0.05f)
        {
            _failed = true;
            _casting = false;
            SpiritFx.LocalStop(Player);
            _ability.EndEffect();
            PortKit.Notice(new Text("Shaman.Interrupted").ToString());
        }
    }

    [LocalOnly, OnlyMine]
    private void OnTaskCompleted(TaskCompletedEvent e)
    {
        if (TaskCompletesRoomReroll) Reroll();
    }

    [LocalOnly]
    private void OnMeetingStart(MeetingStartEvent e)
    {
        _casting = false;
        if (_pending == null) return;
        string text = _pending;
        _pending = null;
        if (_pendingMark != null) _marks.Add(_pendingMark);
        _pendingMark = null;
        PortKit.After(1.5f, () => PortKit.Notice(text));
    }

    // 降霊が成功した時点の、生きている非クルーメイトの数 (と、タスクを終えていれば役職名) で結果を確定する
    private void MakeResult()
    {
        var others = RoleState.All.Where(r => PortKit.Alive(r.Player) && !UchuKit.TrueCrew(r.Player)).ToList();
        string text = new Text("Shaman.Result").With("n", others.Count).ToString();
        string mark = others.Count.ToString();
        if (ResultViewAlivePlayers) text += "\n" + new Text("Shaman.ResultAlive").With("n", PortKit.AliveCount()).ToString();
        if (SkillBoost && PortKit.TasksDone(Player) >= NumOfBoostTask && others.Count > 0)
        {
            var names = new List<string>();
            foreach (var r in others) { string s = r.ShownColoredName; if (!names.Contains(s)) names.Add(s); }
            text += "\n" + string.Join(", ", names);
            mark += ": " + string.Join(", ", names);
        }
        _pending = text;
        _pendingMark = mark;
    }

    // 会議の間ずっと、今までの結果を自分の名札の横に出し続ける (見返せる)
    public override string NameMark(PlayerControl seen)
    {
        if (!MeetingHud.Instance || seen.PlayerId != PlayerId || _marks.Count == 0) return null;
        return $" <size=60%><color={Color}>({string.Join(" / ", _marks)})</color></size>";
    }

    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (IsLocal) Reroll();
    }
}
