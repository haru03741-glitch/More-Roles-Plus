// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Neutral/Tuna.cs (TunaU; role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to SuperNewRoles https://github.com/ykundesu/SuperNewRoles (Roles/Neutral/Tuna.cs, a neutral role there too)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// マグロ (第三陣営): 止まっていると死ぬ。常時 = 続けて止まれる時間・累計 = 止まった時間の合計。
// 生きている人の近く (1.4 以内) にいるとゲージが貯まり (止まっていると多めに貯まる)、満タンで単独勝ち。
// 試合の始めは 10 秒・会議の後は設定の秒だけタイマーが止まる。会議の後 5 秒はゲージも貯まらない
public sealed class Tuna : RoleBase
{
    public override Team BaseTeam => Team.Neutral;
    public override string Color => "#ABF5FF";
    public override Text Name => new("Tuna.Name");
    public override string Reading => "まぐろ";
    public override Text Blurb => new("Tuna.Blurb");
    public override string Origin => "Uchu, SNR";

    public static readonly ChoiceOpt Mode = new("Tuna.Mode", 0, new Text("TunaMod.Mode.Usually"), new Text("TunaMod.Mode.Total"));
    public static readonly FloatOpt UsuallyStop = new("Tuna.UsuallyStop", 3f, 1f, 10f, 1f, "s") { When = () => Mode.Value == 0 };
    public static readonly FloatOpt TotalStop = new("Tuna.TotalStop", 20f, 2.5f, 60f, 2.5f, "s") { When = () => Mode.Value == 1 };
    public static readonly BoolOpt MeetingReset = new("Tuna.MeetingReset", false) { When = () => Mode.Value == 1 };
    public static readonly FloatOpt MeetingStop = new("Tuna.MeetingStop", 10f, 1f, 10f, 1f, "s");
    public static readonly BoolOpt Vent = new("Tuna.CanVent", true);
    public static readonly FloatOpt RequiredGauge = new("Tuna.RequiredGauge", 130f, 50f, 640f, 5f, "s");
    public static readonly FloatOpt StopRatio = new("Tuna.StopRatio", 1.25f, 1f, 10f, 0.25f, "x");
    public static readonly FloatOpt WaterRatio = new("Tuna.WaterRatio", 2f, 1f, 5f, 0.25f, "x");

    public override bool? CanVent => Vent;

    // 泳ぎが得意: 水の中でも歩きと同じ速さで泳ぎ、溺れない
    public override void OnAssigned() => CrewTraits.Set(PlayerId, CrewTraits.Trait.Fin, () => !Lifespan.IsDead); // 役職が外れたら効かない

    private float _left = -1f, _total = -1f, _grace = 10f, _gauge, _gaugeGrace;
    private int _warnSec = -1;
    private bool _counting, _hasAt, _still, _died, _won, _wasDead;
    private Vector2 _at;
    private HudReadout _readout;

    private void OnMeetingStart(MeetingStartEvent e)
    {
        _counting = false;
        _grace = 10f;
        if (MeetingReset) _total = TotalStop;
    }

    private void OnMeetingEnd(MeetingEndEvent e)
    {
        _counting = false;
        _grace = MeetingStop;
        _gaugeGrace = 5f;
        if (MeetingReset) _total = TotalStop;
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (_left < 0f) _left = UsuallyStop;
        if (_total < 0f) _total = TotalStop;
        // 時戻し・蘇生で生き返ったら、止まっていた時間を数え直し、もう一度死ねるようにする (勝ちのゲージはそのまま)
        if (!PortKit.Alive(Player)) _wasDead = true;
        else if (_wasDead)
        {
            _wasDead = _died = _counting = _hasAt = false;
            _left = UsuallyStop;
            _total = TotalStop;
            _grace = MeetingStop;
        }
        float dt = Time.fixedDeltaTime;
        bool paused = MeetingHud.Instance || ExileController.Instance;
        if (!_counting && !paused)
        {
            _grace -= dt;
            if (_grace <= 0f) _counting = true;
        }
        Timer(dt, paused);
        Gauge(dt, paused);
        ShowReadout();
    }

    // 止まっている時間を数える (動けない時・会議の間は数えない)
    private void Timer(float dt, bool paused)
    {
        if (!_counting || !PortKit.Alive(Player) || !Player.CanMove || paused) return;
        Vector2 now = Player.GetTruePosition();
        if (!_hasAt) { _at = now; _hasAt = true; }
        // 泳いでいる間は流されて動いているので、止まっていても数えない
        _still = (now - _at).magnitude < 0.001f && !Terrain.CrewSwim.SwimmingNow;
        _at = now;
        if (Mode.Value == 1)
        {
            if (_still && (_total -= dt) <= 0f) Die();
            return;
        }
        if (!_still) { _left = UsuallyStop; return; }
        if ((_left -= dt) <= 0f) Die();
    }

    // 生きている人の近くにいると貯まる (止まっていると StopRatio 倍)。満タンで単独勝ち
    private void Gauge(float dt, bool paused)
    {
        if (_won || !PortKit.Alive(Player)) return;
        if (_gauge >= RequiredGauge) { _won = true; WinCall.Send(0); return; }
        if (paused) return;
        if (_gaugeGrace > 0f) { _gaugeGrace -= dt; return; }
        if (!Player.CanMove || !_counting) return;
        Vector2 me = Player.GetTruePosition();
        bool near = false;
        foreach (var p in PlayerControl.AllPlayerControls)
            if (p && p != Player && PortKit.Alive(p) && Vector2.Distance(p.GetTruePosition(), me) < 1.4f) { near = true; break; }
        if (!near) return;
        float rate = (_still ? (float)StopRatio : 1f) * (Terrain.CrewSwim.SwimmingNow ? (float)WaterRatio : 1f);
        _gauge = Mathf.Min(_gauge + dt * rate, RequiredGauge);
    }

    private void Die()
    {
        if (_died) return;
        _died = true;
        PortKit.Suicide();
    }

    private static readonly RemoteCall<byte> WinCall = new("Tuna.Win", Route.ToHost,
        (w, m) => w.Write(m), r => r.ReadByte(),
        (sender, _) => { if (RoleState.Of(sender) is Tuna t && PortKit.Alive(sender)) GameEnd.Win(t); }, perSecond: 2);

    // 画面の札: 死ぬまでの秒 (猶予中は「停止中」) と勝ちのゲージ
    private void ShowReadout()
    {
        _readout ??= new HudReadout(Lifespan, UI.RoleArt.Own(Id), new Color32(171, 245, 255, 255));
        if (!PortKit.Alive(Player)) { _readout.Set(false); return; }
        float fill = RequiredGauge > 0f ? _gauge / RequiredGauge : 0f;
        if (!_counting) { _readout.Set(true, null, fill); return; }
        float t = Mode.Value == 1 ? _total : _left;
        CountdownWarn.Tick(ref _warnSec, t, 3);
        _readout.Set(true, $"<color={(t > 1f ? "#FFFF00" : "#FF0000")}>{t:0.0}s</color>", fill);
    }
}
