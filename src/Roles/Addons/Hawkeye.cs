using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Addons;

// 鷹の目 (アドオン): 「鷹の目」ボタンを押すと、少しの間だけ視界が広くなる (押した後は待ち時間)
public sealed class Hawkeye : AddonBase
{
    public override string Color => "#E8B84A";
    public override Text Name => new("Hawkeye.Name");
    public override string Reading => "たかのめ";
    public override Text Blurb => new("Hawkeye.Blurb");

    public static readonly IntOpt VisionPercent = new("Hawkeye.VisionPercent", 150, 110, 300, 10, "%");
    public static readonly FloatOpt Duration = new("Hawkeye.Duration", 10f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt Cooldown = new("Hawkeye.Cooldown", 25f, 2.5f, 60f, 2.5f, "s");

    private bool _active; // 自分の端末: 視界が広がっている間

    public override void OnAssigned()
    {
        var ability = new Ability(new Text("Hawkeye.Use"), "hawkeye_vision")
        {
            Cooldown = () => Cooldown,
            Duration = () => Duration,
            OnUse = () => { _active = true; return true; },
            OnEnd = () => _active = false,
        };
        if (AddAbility(ability) != null || !IsLocal) return;
        // 本編の役職のままの人 (MRP の役職が無い) には、ボタンだけを自分で出す
        var button = ModButton.Create(Lifespan, ButtonIcons.Get("hawkeye_vision"), new Text("Hawkeye.Use").ToString(), Cooldown,
            () => { if (!PortKit.Alive(Player)) return false; _active = true; return true; });
        if (button == null) return;
        button.Effect = () => Duration;
        button.OnEffectEnd = () => _active = false;
        button.ResetOnShow = true;
        button.StartCooldown();
    }

    private void OnMeetingStart(MeetingStartEvent e) => _active = false;

    public override void ModifyVision(ref float radius)
    {
        if (_active) radius *= VisionPercent.Value / 100f;
    }
}
