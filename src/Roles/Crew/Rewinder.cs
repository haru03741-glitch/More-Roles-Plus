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
            // 押した本人の画面に全画面の演出を流し、真っ白になった所で依頼を出す (戻す区間は押した時から)。出せない時はすぐ戻す
            OnUse = () => RewindCinematic.Play(ms => TimeRewind.Ask(ms)) || TimeRewind.Ask(),
        });
    }

    // 押した後に依頼を出さずにやめた時 (発動者の演出の途中で会議になったなど): 手元で使った回数を返す。
    // ホストは依頼を通した時にだけ数えるので、手元の回数をホストに合わせる
    internal void Refund()
    {
        if (_rewind == null || _rewind.UsesLeft < 0) return;
        _rewind.RestoreUses(_rewind.UsesLeft + 1);
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
