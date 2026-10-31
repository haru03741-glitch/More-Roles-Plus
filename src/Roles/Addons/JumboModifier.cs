// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Modifiers/JumboModifier.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Addons;

// ジャンボ (アドオン): 時間が経つほど体が大きくなる (全員の画面で同じように)。名前の横に育ち具合 (%) が出る。
// 死体や追放の画面の姿も大きいまま
public sealed class JumboModifier : AddonBase
{
    public override string Color => "#FFFFFF";
    public override Text Name => new("JumboModifier.Name");
    public override string Reading => "じゃんぼ";
    public override Text Blurb => new("JumboModifier.Blurb");
    public override string Origin => "SNR";

    public static readonly BoolOpt OnImpostorJumbo = new("JumboModifier.OnImpostor", true);
    public static readonly BoolOpt OnNeutralJumbo = new("JumboModifier.OnNeutral", true);
    public static readonly FloatOpt MaxSize = new("JumboModifier.MaxSize", 24f, 1f, 72f, 1f);
    public static readonly FloatOpt GrowSeconds = new("JumboModifier.GrowSeconds", 60f, 0f, 360f, 2.5f, "s");
    public static readonly BoolOpt GrowInMeeting = new("JumboModifier.GrowInMeeting", true);

    private float _size;       // 0 〜 MaxSize/10 (大きさは 1 + これ)
    private float _sync;

    private float Max => MaxSize / 10f;
    private float Scale => _size + 1f;

    public override bool CanAttach(Team team, RoleBase role) =>
        base.CanAttach(team, role) && (team != Team.Impostor || OnImpostorJumbo) && (team != Team.Neutral || OnNeutralJumbo);

    public override void OnAssigned() => Lifespan.OnRelease(() => Apply(0f));

    public override void OnFixedUpdate()
    {
        var hud = Vanilla.Hud;
        if (!GrowInMeeting && (MeetingHud.Instance || ExileController.Instance) || hud && hud.IsIntroDisplayed) return;
        if (!Player) return;
        if (_size < Max)
        {
            _size = GrowSeconds <= 0f ? Max : Mathf.Min(Max, _size + Time.fixedDeltaTime * (Max / GrowSeconds));
            if (Player.AmOwner && (_sync += Time.fixedDeltaTime) >= 5f)
            {
                _sync = 0f;
                SizeCall.Send(_size);
            }
        }
        Apply(_size);
    }

    private void Apply(float size)
    {
        if (!Player) return;
        float s = size + 1f;
        if (Player.cosmetics) Player.cosmetics.transform.localScale = Vector3.one * (s * 0.5f);
        var body = Player.transform.Find("BodyForms");
        if (body) body.localScale = Vector3.one * s;
        var anim = Player.transform.Find("Animations");
        if (anim) anim.localScale = Vector3.one * s;
    }

    private static readonly RemoteCall<float> SizeCall = new("JumboModifier.Size", Route.Anyone,
        (w, v) => w.Write(v), r => r.ReadSingle(),
        (sender, v) =>
        {
            foreach (var a in RoleState.AddonsOf(sender.PlayerId)) if (a is JumboModifier j) j._size = v;
        });

    // 死体も大きいまま
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (!e.Target || e.Target.PlayerId != PlayerId) return;
        float s = Scale;
        PortKit.After(0.1f, () =>
        {
            foreach (var b in Object.FindObjectsOfType<DeadBody>())
                if (b && b.ParentId == PlayerId) b.transform.localScale = Vector3.one * s;
        });
    }

    // 追放の画面の姿も大きいまま
    private void OnExileShown(ExileShownEvent e)
    {
        if (e.Exiled == null || e.Exiled.PlayerId != PlayerId || !e.Controller || !e.Controller.Player) return;
        e.Controller.Player.transform.localScale = Vector3.one * Scale;
    }

    public override string NameMark(PlayerControl seen) =>
        seen && seen.PlayerId == PlayerId ? $" ({Mathf.RoundToInt(_size / Max * 100f)})" : null;
}
