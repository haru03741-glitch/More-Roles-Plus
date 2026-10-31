// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Cupid.cs (GPL-3.0)
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles.Addons;

namespace MoreRolesPlus.Roles.Neutral;

// キューピッド (第三陣営): 「恋の矢」を 2 人に撃つと、その 2 人がラバーズになる。ラバーズが勝つと一緒に勝つ。
// 時間の制限がある時は、それまでにラバーズを作らないと死ぬ
public sealed class Cupid : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => Lovers.Pink;
    public override Text Name => new("Cupid.Name");
    public override string Reading => "きゅーぴっど";
    public override Text Blurb => new("Cupid.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Cupid.Arrow", "snr_arrow") };

    public static readonly FloatOpt ArrowCooldown = new("Cupid.Cooldown", 0f, 0f, 180f, 2.5f, "s");
    public static readonly BoolOpt TimeLimit = new("Cupid.TimeLimitOn", true);
    public static readonly FloatOpt TimeLimitSeconds = new("Cupid.TimeLimit", 120f, 30f, 600f, 15f, "s") { When = () => TimeLimit };

    private byte _first = 255, _second = 255;
    private float _left = -1f;
    private bool _shown;

    private void OnMeetingStart(MeetingStartEvent e) => _shown = false;

    private bool Made => _second != 255;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Cupid.Arrow"), "snr_arrow")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => ArrowCooldown,
            MaxUses = () => 2,
            CanTarget = (_, t) => t.PlayerId != _first,
            OnUsed = (_, target) =>
            {
                if (_first == 255) { _first = target.PlayerId; return; }
                _second = target.PlayerId;
                StatusEffects.Hide("Cupid");
                if (!AmongUsClient.Instance.AmHost) return;
                var proto = RoleState.AddonProto<Lovers>();
                var a = PortKit.ById(_first);
                RoleState.AddAddon(a, proto);
                RoleState.AddAddon(target, proto);
                AddonGroups.HostAdd(_first, _second);
            },
        });
        if (TimeLimit) _left = TimeLimitSeconds;
    }

    // 作ったラバーズの片方でも勝ちに入っていれば一緒に勝つ
    public override bool AlsoWins(GameResult result) =>
        Made && (result.Winners.Contains(_first) || result.Winners.Contains(_second));

    public override string NameMark(PlayerControl seen) =>
        seen.PlayerId == _first || seen.PlayerId == _second ? $"<color={Lovers.Pink}>♥</color>" : null;

    // 歩ける時間だけ制限を数える
    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (Made || _left < 0f || !PortKit.Alive(Player) || MeetingHud.Instance || ExileController.Instance) return;
        if (!_shown)
        {
            _shown = true; // 会議の後に残りを出し直す
            StatusEffects.ShowFor("Cupid", new Text("Cupid.Timer"), Color, _left);
        }
        _left -= UnityEngine.Time.fixedDeltaTime;
        if (_left > 0f) return;
        _left = -1f;
        StatusEffects.Hide("Cupid");
        PortKit.Suicide();
    }
}
