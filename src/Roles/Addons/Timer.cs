// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Modifier/Timer.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using UnityEngine;

namespace MoreRolesPlus.Roles.Addons;

// タイマー (アドオン): タスクの時間 (会議の後からの経過) を画面に出す。会議中は止まり、会議の後は 0 から数える
public sealed class Timer : AddonBase
{
    public override string Color => "#03FCC2";
    public override Text Name => new("Timer.Name");
    public override string Reading => "たいまー";
    public override Text Blurb => new("Timer.Blurb");
    public override string Origin => "Uchu";

    private float _time;

    private void OnMeetingEnd(MeetingEndEvent e) => _time = 0f;

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (!MeetingHud.Instance && !ExileController.Instance) _time += Time.fixedDeltaTime;
    }

    public override string NameMark(PlayerControl seen) =>
        IsLocal && seen.PlayerId == PlayerId ? $"<size=70%><color=#03FCC2>{new Text("Timer.Text").With("t", _time.ToString("0.0"))}</color></size>" : null;
}
