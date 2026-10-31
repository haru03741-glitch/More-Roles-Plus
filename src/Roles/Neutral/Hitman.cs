// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Hitman.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// 殺し屋 (第三陣営・キル役): 自分にだけ分かるターゲットが決まり、矢印で場所が分かる。ターゲットを決まった数キルすると単独で勝つ。
// 決まった時間の中にターゲットをキルできないか、ターゲット以外をキルすると失敗になり、ターゲットが変わる。
// 失敗の上限の設定があれば、失敗が重なると死ぬ。会議の後はターゲットと時間が新しくなる (設定)
public sealed class Hitman : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#562912";
    public override Text Name => new("Hitman.Name");
    public override string Reading => "ころしや";
    public override Text Blurb => new("Hitman.Blurb");
    public override string Origin => "SNR";
    public override bool IsKiller => true;

    public static readonly FloatOpt KillCooldown = new("Hitman.KillCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt ChangeTargetTime = new("Hitman.ChangeTargetTime", 60f, 10f, 60f, 2.5f, "s");
    public static readonly IntOpt NeededHits = new("Hitman.NeededHits", 3, 1, 10);
    public static readonly BoolOpt OutMission = new("Hitman.OutMission", false);
    public static readonly IntOpt FailLimit = new("Hitman.FailLimit", 3, 0, 10) { When = () => OutMission };
    public static readonly BoolOpt Vent = new("Hitman.CanVent", false);
    public static readonly BoolOpt ImpostorSight = new("Hitman.ImpostorVision", false);
    public static readonly BoolOpt ResetOnMeeting = new("Hitman.ResetOnMeeting", true);

    public override bool? CanVent => Vent;
    public override void ModifyKillCooldown(ref float seconds) => seconds = KillCooldown;

    public override void ModifyVision(ref float radius)
    {
        if (ImpostorSight) PortKit.ImpostorVision(ref radius);
    }

    private byte _target = 255;   // 自分の端末だけ
    private int _hits, _fails;
    private float _timer = -1f;
    private WorldArrow _arrow;

    // 自分の端末で: 新しいターゲットを選ぶ
    private void Pick()
    {
        var pool = new List<PlayerControl>();
        foreach (var p in PlayerControl.AllPlayerControls)
            if (PortKit.Alive(p) && p.PlayerId != PlayerId) pool.Add(p);
        _target = pool.Count == 0 ? (byte)255 : pool[Random.Range(0, pool.Count)].PlayerId;
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        _arrow ??= new WorldArrow(Lifespan, UnityEngine.Color.red);
        var hud = Vanilla.Hud;
        bool idle = !PortKit.Alive(Player) || MeetingHud.Instance || ExileController.Instance || hud && hud.IsIntroDisplayed;
        if (idle)
        {
            _arrow.SetActive(false);
            StatusEffects.Hide("Hitman");
            return;
        }
        if (_timer < 0f) _timer = ChangeTargetTime;
        _timer -= Time.fixedDeltaTime;
        var t = PortKit.ById(_target);
        if (!PortKit.Alive(t))
        {
            Pick();
            return;
        }
        _arrow.SetActive(true);
        _arrow.Update(t.transform.position);
        StatusEffects.ShowUntil("Hitman", new Text("Hitman.Timer").With("n", _timer.ToString("F1")), "#562912", Text.Raw(""));
        if (_timer <= 0f)
        {
            Fail();
            Pick();
        }
    }

    // 自分の端末で: 自分のキルを数える
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (!IsLocal || !PortKit.Alive(Player) || !e.Killer || e.Killer.PlayerId != PlayerId || !e.Target || e.Target == e.Killer) return;
        if (e.Target.PlayerId == _target)
        {
            _timer = ChangeTargetTime;
            _hits++;
            Sync();
            if (_hits >= NeededHits)
            {
                WinCall.Send(0);
                if (AmongUsClient.Instance.AmHost) GameEnd.Win(this);
            }
            Pick();
        }
        else Fail();
    }

    private void Fail()
    {
        _timer = ChangeTargetTime;
        _fails++;
        Sync();
        if (OutMission && _fails >= FailLimit) PortKit.Suicide();
    }

    [LocalOnly]
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (!ResetOnMeeting || !PortKit.Alive(Player)) return;
        Pick();
        _timer = ChangeTargetTime;
    }

    private void Sync()
    {
        CountCall.Send((_hits, _fails));
    }

    private static readonly RemoteCall<(int hits, int fails)> CountCall = new("Hitman.Count", Route.Anyone,
        (w, m) => { w.WritePacked(m.hits); w.WritePacked(m.fails); }, r => (r.ReadPackedInt32(), r.ReadPackedInt32()),
        (sender, m) => { if (RoleState.Of(sender) is Hitman h) { h._hits = m.hits; h._fails = m.fails; } });
    private static readonly RemoteCall<byte> WinCall = new("Hitman.Win", Route.ToHost,
        (w, _) => w.Write((byte)0), r => r.ReadByte(),
        (sender, _) => { if (RoleState.Of(sender) is Hitman h && h._hits >= NeededHits) GameEnd.Win(h); });

    public override string NameMark(PlayerControl seen)
    {
        if (!seen) return null;
        if (seen.PlayerId == PlayerId)
            return $"<color=#00FFFF>({_hits}/{NeededHits.Value})</color>" + (OutMission ? $"<color=#FF0000>({_fails}/{FailLimit.Value})</color>" : "");
        return IsLocal && seen.PlayerId == _target ? "<color=#FF1919>◎</color>" : null;
    }
}
