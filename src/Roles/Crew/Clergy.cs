// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/Clergy.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// 聖職者 (クルー): 「聖なる光」でしばらくインポスターの視界を狭くする (設定でキルできる第三陣営も)
public sealed class Clergy : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FFE9A6";
    public override Text Name => new("Clergy.Name");
    public override string Reading => "せいしょくしゃ";
    public override Text Blurb => new("Clergy.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Clergy.Light", "snr_holylight") };

    public static readonly FloatOpt LightCooldown = new("Clergy.Cooldown", 30f, 5f, 120f, 5f, "s");
    public static readonly FloatOpt EffectDuration = new("Clergy.Duration", 15f, 5f, 60f, 5f, "s");
    public static readonly FloatOpt VisionMultiplier = new("Clergy.VisionMultiplier", 0.3f, 0.1f, 1f, 0.1f, "x");
    public static readonly BoolOpt ForNeutralKiller = new("Clergy.ForNeutralKiller", false);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Clergy.Light"), "snr_holylight")
        {
            Cooldown = () => LightCooldown,
            OnUse = () =>
            {
                LightCall.Send(0);
                OnLight();
                return true;
            },
        });
    }

    private static readonly RemoteCall<byte> LightCall = new("Clergy.Light", Route.Anyone,
        (w, _) => w.Write((byte)0), r => r.ReadByte(),
        (sender, _) =>
        {
            if (RoleState.Of(sender) is Clergy) OnLight();
        });

    private static int _gen; // 重ねて使われた時、最後の光が切れるまで戻さない

    // 全員の端末で: 自分が対象なら視界を狭め、効果の時間が過ぎたら戻す
    private static void OnLight()
    {
        var lp = PlayerControl.LocalPlayer;
        bool hit = PortKit.IsImpostor(lp) || (ForNeutralKiller && PortKit.IsNeutral(lp) && RoleState.Of(lp) is { CanKill: true });
        if (!hit) return;
        int gen = ++_gen;
        VisionEffects.Multiplier = VisionMultiplier;
        StatusEffects.ShowFor("Clergy", new Text("Clergy.Effect"), "#FFE9A6", EffectDuration);
        PortKit.After(EffectDuration, () =>
        {
            if (gen == _gen) VisionEffects.Multiplier = 1f;
        });
    }
}
