// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Crewmate/Polaris.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ポラリス (クルー): 誰でも倒せるキル (回数つき・誤爆で死なない) と、死体を見る「幻視」を持つ。
// 幻視は近くの死体に使い、少しの間動けなくなる。終わると次の会議でその死者の名前と役職が分かる (自分だけ)。
// 設定で、幻視に終えたタスクを使う (使うとタスクが未完了に戻る)
public sealed class Polaris : RoleBase
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#82B2FF";
    public override Text Name => new("Polaris.Name");
    public override string Reading => "ぽらりす";
    public override Text Blurb => new("Polaris.Blurb");
    public override string Origin => "Uchu";
    public override bool CanBeMadmate => true;
    public override HelpSection[] HelpSections => new HelpSection[] { new("Polaris.Kill", "polaris_kill"), new("Polaris.Vision", "polaris_vision") };

    public static readonly IntOpt KillUses = new("Polaris.KillUses", 3, 1, 15);
    public static readonly FloatOpt KillCooldown = new("Polaris.KillCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly BoolOpt CanKillHidden = new("Polaris.CanKillHidden", false);
    public static readonly BoolOpt SealUntilReport = new("Polaris.SealUntilReport", false);
    public static readonly IntOpt VisionUses = new("Polaris.VisionUses", 3, 1, 15);
    public static readonly FloatOpt VisionCooldown = new("Polaris.VisionCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt VisionDuration = new("Polaris.VisionDuration", 3f, 0.5f, 10f, 0.5f, "s");
    public static readonly BoolOpt RewindTask = new("Polaris.RewindTask", true);
    public static readonly IntOpt MediumRewindTask = new("Polaris.MediumRewindTask", 3, 1, 10) { When = () => RewindTask };

    private bool _sealed;                    // 全員の端末で同じ (キルの後、死体が通報されるまで)
    private DeadBody _seeing;                // 自分の端末: 幻視している死体
    private readonly List<byte> _seen = new(); // 次の会議で見せる死者
    private readonly List<byte> _known = new(); // 自分の端末: 今までに幻視で分かった死者 (会議で名札の横に出し続ける・原作の会議の結果欄)

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Polaris.Kill"), "polaris_kill")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => KillCooldown,
            MaxUses = () => KillUses,
            CanTarget = (_, t) => !(SealUntilReport && _sealed) && (CanKillHidden || !Hidden(t)),
            OnUsed = (user, t) =>
            {
                if (SealUntilReport) _sealed = true;
                if (AmongUsClient.Instance.AmHost) PortKit.HostKill(user, t, PortKit.Harm.Slash);
            },
        });
        AddAbility(new Ability(new Text("Polaris.Vision"), "polaris_vision")
        {
            Cooldown = () => VisionCooldown,
            Duration = () => VisionDuration,
            MaxUses = () => VisionUses,
            OnUse = () =>
            {
                var body = PortKit.NearestBody(Player.GetTruePosition(), 1.5f);
                if (!body) return false;
                if (RewindTask && PortKit.TasksDone(Player) < MediumRewindTask) return false;
                _seeing = body;
                MoveSpeed.Add("PolarisVision", 0f, VisionDuration);
                SpiritFx.LocalMist(Player, body);
                MoreRolesPlus.Fx.Hearing.Self("polaris_vision", 0.5f);
                return true;
            },
            OnEnd = () =>
            {
                MoveSpeed.Remove("PolarisVision");
                SpiritFx.LocalStop(Player);
                var body = _seeing;
                _seeing = null;
                if (!body || MeetingHud.Instance) return;
                if (!_seen.Contains(body.ParentId)) _seen.Add(body.ParentId);
                if (RewindTask) UndoTasks(MediumRewindTask);
                PortKit.Notice(new Text("Polaris.Seen").ToString());
            },
        });
    }

    private static bool Hidden(PlayerControl t) => Cloak.IsOn(t.PlayerId) || UchuKit.Hidden.IsHiddenFromMe(t.PlayerId) || t.inVent;

    // 死体が通報されたら封印を解く
    private void OnMeetingCalled(MeetingCalledEvent e)
    {
        if (!e.IsEmergency) _sealed = false;
    }

    // 次の会議で、幻視した死者の名前と役職を自分にだけ
    [LocalOnly]
    private void OnMeetingStart(MeetingStartEvent e)
    {
        if (_seen.Count == 0) return;
        var ids = new List<byte>(_seen);
        _seen.Clear();
        foreach (var id in ids) if (!_known.Contains(id)) _known.Add(id);
        PortKit.After(1.5f, () =>
        {
            foreach (var id in ids)
            {
                var info = GameData.Instance ? GameData.Instance.GetPlayerById(id) : null;
                var role = RoleState.Of(id);
                if (info == null) continue;
                var result = new Text("Polaris.Result").With("name", info.PlayerName).With("role", role != null ? role.ShownColoredName : "?").ToString();
                PortKit.Notice(result + TerrainSight(id));
            }
        });
    }

    // 火・水・瓦礫で死んだ人は、死に方と、その時に近くにいた人の色も見える
    private static string TerrainSight(byte id)
    {
        var d = MatchLog.Of(id);
        string cause = d?.Kind switch
        {
            MatchLog.Kind.Burned => new Text("Autopsy.Burned").ToString(),
            MatchLog.Kind.Drowned => new Text("Autopsy.Drowned").ToString(),
            MatchLog.Kind.Crushed => new Text("Autopsy.Crushed").ToString(),
            _ => null,
        };
        if (cause == null) return "";
        if (d.Near == null || d.Near.Count == 0) return new Text("Polaris.TerrainAlone").With("cause", cause).ToString();
        string colors = null;
        foreach (int c in d.Near)
        {
            if (c < 0 || c >= Palette.PlayerColors.Length) continue;
            // 名前は会議の名札と同じ引き方 (自前の色は部屋ごとの名前になる)
            string one = $"<color=#{ColorUtility.ToHtmlStringRGB(Palette.PlayerColors[c])}>{Palette.GetColorName(c)}</color>";
            colors = colors == null ? one : new Text("Polaris.ColorJoin").With("a", colors).With("b", one).ToString();
        }
        if (colors == null) return new Text("Polaris.TerrainAlone").With("cause", cause).ToString();
        return new Text("Polaris.TerrainNear").With("cause", cause).With("colors", colors).ToString();
    }

    // 会議の間ずっと、幻視で分かった死者の名札の横に役職を出す (見返せる)
    public override string NameMark(PlayerControl seen)
    {
        if (!MeetingHud.Instance || !_known.Contains(seen.PlayerId)) return null;
        var role = RoleState.Of(seen.PlayerId);
        return role == null ? null : $" <size=70%>{role.ShownColoredName}</size>";
    }

    // 自分の端末: 終えたタスクを n 個未完了に戻す (全員の端末のタスクの数え方も戻す)
    private void UndoTasks(int n)
    {
        var lp = Player;
        if (!lp || lp.myTasks == null) return;
        var ids = new List<uint>();
        foreach (var t in lp.myTasks)
        {
            if (ids.Count >= n) break;
            var nt = t ? t.TryCast<NormalPlayerTask>() : null;
            if (nt == null || !nt.IsComplete) continue;
            nt.taskStep = 0;
            ids.Add(nt.Id);
        }
        foreach (var id in ids)
        {
            UndoCall.Send((PlayerId, id));
            Undo(PlayerId, id);
        }
    }

    private static readonly RemoteCall<(byte pid, uint task)> UndoCall = new("Polaris.UndoTask", Route.Anyone,
        (w, m) => { w.Write(m.pid); w.Write(m.task); }, r => (r.ReadByte(), r.ReadUInt32()),
        (sender, m) => { if (sender && sender.PlayerId == m.pid && RoleState.ActingOf(sender) is Polaris) Undo(m.pid, m.task); }, perSecond: 10);

    private static void Undo(byte pid, uint task)
    {
        var info = GameData.Instance ? GameData.Instance.GetPlayerById(pid) : null;
        if (info == null || info.Tasks == null) return;
        foreach (var t in info.Tasks)
            if (t != null && t.Id == task) t.Complete = false;
        GameData.Instance.RecomputeTaskCounts();
    }
}
