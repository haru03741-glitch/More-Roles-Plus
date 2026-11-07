// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Neutral/Tuna.cs (TunaModiU; role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to SuperNewRoles https://github.com/ykundesu/SuperNewRoles
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Addons;

// マグロ (アドオン): 止まっていると死ぬ。常時 = 続けて止まれる時間・累計 = 止まった時間の合計。
// 試合の始めと会議の後は少しの間タイマーが止まる。名前の横に死ぬまでの秒が出る (本人にだけ)
public sealed class TunaMod : AddonBase
{
    public override string Color => "#ABF5FF";
    public override Text Name => new("TunaMod.Name");
    public override string Reading => "まぐろ";
    public override Text Blurb => new("TunaMod.Blurb");
    public override string Origin => "Uchu, SNR";

    public static readonly ChoiceOpt Mode = new("TunaMod.Mode", 0, new Text("TunaMod.Mode.Usually"), new Text("TunaMod.Mode.Total"));
    public static readonly FloatOpt UsuallyStop = new("TunaMod.UsuallyStop", 3f, 1f, 10f, 1f, "s") { When = () => Mode.Value == 0 };
    public static readonly FloatOpt TotalStop = new("TunaMod.TotalStop", 20f, 2.5f, 60f, 2.5f, "s") { When = () => Mode.Value == 1 };
    public static readonly BoolOpt MeetingReset = new("TunaMod.MeetingReset", false) { When = () => Mode.Value == 1 };
    public static readonly FloatOpt MeetingStop = new("TunaMod.MeetingStop", 10f, 1f, 10f, 1f, "s");
    public static readonly BoolOpt CountInTask = new("TunaMod.CountInTask", true);

    private float _left = -1f, _total = -1f, _grace = 10f;
    private bool _counting;
    private Vector2 _at;
    private bool _hasAt;

    private void OnMeetingStart(MeetingStartEvent e)
    {
        _counting = false;
        _grace = MeetingStop;
        if (MeetingReset) _total = TotalStop;
    }

    private void OnMeetingEnd(MeetingEndEvent e)
    {
        _counting = false;
        _grace = MeetingStop;
        _hasAt = false;
        if (MeetingReset) _total = TotalStop;
    }

    private bool _died;

    private void Die()
    {
        if (_died) return;
        _died = true;
        PortKit.Suicide();
    }

    private bool CanMove() => Player.CanMove || (CountInTask && Minigame.Instance);

    private HudReadout _readout;

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (_left < 0f) _left = UsuallyStop;
        if (_total < 0f) _total = TotalStop;
        ShowReadout();
        float dt = Time.fixedDeltaTime;
        if (!_counting && !MeetingHud.Instance && !ExileController.Instance)
        {
            _grace -= dt;
            if (_grace <= 0f) _counting = true;
        }
        if (!_counting || !PortKit.Alive(Player) || !CanMove() || MeetingHud.Instance || ExileController.Instance) return;
        Vector2 now = Player.GetTruePosition();
        if (!_hasAt) { _at = now; _hasAt = true; }
        bool still = (now - _at).magnitude < 0.001f;
        _at = now;
        if (Mode.Value == 1)
        {
            if (still && (_total -= dt) <= 0f) Die();
            return;
        }
        if (!still) { _left = UsuallyStop; return; }
        if ((_left -= dt) <= 0f) Die();
    }

    // 死ぬまでの秒は画面の下の札とゲージに出す (元は名前の横の字)
    private void ShowReadout()
    {
        _readout ??= new HudReadout(Lifespan, UI.RoleArt.Own(Id), new Color32(171, 245, 255, 255));
        if (!PortKit.Alive(Player)) { _readout.Set(false); return; }
        if (!_counting) { _readout.Set(true, $"<color=#FFFF00>{new Text("TunaMod.Paused")}</color>"); return; }
        float t = Mode.Value == 1 ? _total : _left;
        float max = Mode.Value == 1 ? TotalStop : UsuallyStop;
        _readout.Set(true, $"<color={(t > 1f ? "#FFFF00" : "#FF0000")}>{t:0.0}s</color>", max > 0f ? t / max : -1f);
    }
}
