// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Tuna.cs (GPL-3.0)
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// マグロ (第三陣営): 決まった秒数立ち止まると死ぬ。試合の終わりまで生きていれば一緒に勝つ (設定で単独勝ち)
public sealed class Tuna : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#00FFFF";
    public override Text Name => new("Tuna.Name");
    public override string Reading => "まぐろ";
    public override Text Blurb => new("Tuna.Blurb");
    public override string Origin => "SNR";

    public static readonly FloatOpt StopLimit = new("Tuna.StopLimit", 2f, 1f, 3f, 0.5f, "s");
    public static readonly BoolOpt SoloWin = new("Tuna.SoloWin", false);
    public static readonly BoolOpt Vent = new("Tuna.CanVent", false);

    public override bool? CanVent => Vent;

    public override bool AlsoWins(GameResult result) => !SoloWin && PortKit.Alive(Player);

    public override bool StealsWin(GameResult result) => SoloWin && PortKit.Alive(Player);

    private Vector2 _last;
    private float _still;
    private bool _dying;

    // 歩ける時 (会議・演出・ベントの中は数えない) に止まっている時間を数える
    [LocalOnly]
    public override void OnFixedUpdate()
    {
        var lp = Player;
        if (_dying || !PortKit.Alive(lp)) return;
        Vector2 pos = lp.GetTruePosition();
        if (!lp.CanMove || lp.inVent || MeetingHud.Instance || ExileController.Instance || (pos - _last).sqrMagnitude > 0.0001f)
        {
            _last = pos;
            _still = 0f;
            StatusEffects.Hide("Tuna");
            return;
        }
        _still += Time.fixedDeltaTime;
        if (_still > 0.5f && _still - Time.fixedDeltaTime <= 0.5f) StatusEffects.ShowFor("Tuna", new Text("Tuna.Timer"), Color, StopLimit - _still);
        if (_still < StopLimit) return;
        _dying = true;
        StatusEffects.Hide("Tuna");
        PortKit.Suicide();
    }
}
