// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Truelover.cs (GPL-3.0)
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles.Addons;

namespace MoreRolesPlus.Roles.Neutral;

// 純愛者 (第三陣営): 「一目惚れ」で近くの人と 2 人でラバーズになる。
// 時間の制限がある時は、それまでに相手を決めないと死ぬ
public sealed class Truelover : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => Lovers.Pink;
    public override Text Name => new("Truelover.Name");
    public override string Reading => "じゅんあいしゃ";
    public override Text Blurb => new("Truelover.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Truelover.Arrow", "snr_heart") };

    public static readonly FloatOpt ArrowCooldown = new("Truelover.Cooldown", 0f, 0f, 180f, 2.5f, "s");
    public static readonly BoolOpt TimeLimit = new("Truelover.TimeLimitOn", true);
    public static readonly FloatOpt TimeLimitSeconds = new("Truelover.TimeLimit", 120f, 30f, 600f, 15f, "s") { When = () => TimeLimit };

    private byte _first = 255, _second = 255;
    private float _left = -1f;
    private bool _shown;

    private void OnMeetingStart(MeetingStartEvent e) => _shown = false;

    private bool Made => _second != 255;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Truelover.Arrow"), "snr_heart")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => ArrowCooldown,
            MaxUses = () => 1,
            OnUsed = (user, target) =>
            {
                _first = user.PlayerId;
                _second = target.PlayerId;
                StatusEffects.Hide("Truelover");
                if (!AmongUsClient.Instance.AmHost) return;
                var proto = RoleState.AddonProto<Lovers>();
                RoleState.AddAddon(user, proto);
                RoleState.AddAddon(target, proto);
                AddonGroups.HostAdd(_first, _second);
            },
        });
        if (TimeLimit) _left = TimeLimitSeconds;
    }

    // 勝ち方はラバーズのまま (2 人とも生きていれば勝ちを奪う)。相方が勝ちに入っていれば一緒に勝つ
    public override bool AlsoWins(GameResult result) => Made && result.Winners.Contains(_second);

    // 歩ける時間だけ制限を数える
    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (Made || _left < 0f || !PortKit.Alive(Player) || MeetingHud.Instance || ExileController.Instance) return;
        if (!_shown)
        {
            _shown = true; // 会議の後に残りを出し直す
            StatusEffects.ShowFor("Truelover", new Text("Truelover.Timer"), Color, _left);
        }
        _left -= UnityEngine.Time.fixedDeltaTime;
        if (_left > 0f) return;
        _left = -1f;
        StatusEffects.Hide("Truelover");
        PortKit.Suicide();
    }
}
