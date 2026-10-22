using System;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// 時戻し (クルー): 押すと世界ごと数秒前に戻す。人の位置・生き死に・タスク・待ち時間・サボタージュが戻る (TimeRewind)。
// 押した本人も一緒に戻り、残るのはこのボタンの回数と待ち時間だけ
public sealed class Rewinder : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#6FA8FF";
    public override Text Name => new("Rewinder.Name");
    public override string Reading => "ときもどし";
    public override Text Blurb => new("Rewinder.Blurb");
    public override string[] HelpSections => new[] { "Rewinder.Rewind" };

    public static readonly FloatOpt Seconds = new("Rewinder.Seconds", 5f, 3f, 15f, 1f, "s");
    public static readonly FloatOpt RewindCooldown = new("Rewinder.Cooldown", 40f, 10f, 120f, 5f, "s");
    public static readonly IntOpt RewindUses = new("Rewinder.Uses", 1, 0, 10);

    private const float CooldownSlack = 1.5f; // ホストが待ち時間を確かめる時の通信の遅れの分 (秒)

    private Ability _rewind;
    private int _hostUsed;

    public override void OnAssigned()
    {
        TimeRewind.Enable(RoleState.Match);
        _rewind = AddAbility(new Ability(new Text("Rewinder.Rewind"), "rewind")
        {
            Cooldown = () => RewindCooldown,
            MaxUses = () => RewindUses,
            KeepOnRewind = true,
            OnUse = TimeRewind.Ask,
        });
    }

    // ホスト: この人の依頼を通してよいか (回数と待ち時間)。通したら数える
    internal bool HostAllows()
    {
        long now = Environment.TickCount64;
        int max = RewindUses;
        if (max > 0 && _hostUsed >= max) return false;
        if (now - _rewind.HostLastMs < (long)((RewindCooldown - CooldownSlack) * 1000f)) return false;
        _hostUsed++;
        _rewind.HostLastMs = now;
        return true;
    }
}
