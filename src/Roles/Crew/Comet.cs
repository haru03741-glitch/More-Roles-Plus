// Ported from https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Roles/Crewmate/Comet.cs (GPL-3.0)
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

// コメット (クルー): 「ブースト」で決めた時間だけ透明になり、足が速くなる (視界も広がり、画面も少し引く)。
// 透明の間は他の人に見えず、キルも能力も受けない。始めと終わりは姿が薄れて消える・現れる。停電で視界が狭まらない
public sealed class Comet : RoleBase
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#79AFCE";
    public override Text Name => new("Comet.Name");
    public override string Reading => "こめっと";
    public override Text Blurb => new("Comet.Blurb");
    public override string Origin => "Nebula";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Comet.Blaze", "comet_boost") };

    public static readonly FloatOpt Cooldown = new("Comet.Cooldown", 20f, 5f, 60f, 2.5f, "s");
    public static readonly FloatOpt Duration = new("Comet.Duration", 15f, 5f, 60f, 2.5f, "s");
    public static readonly FloatOpt Speed = new("Comet.Speed", 1.5f, 0.5f, 3f, 0.125f, "x");
    public static readonly FloatOpt Vision = new("Comet.Vision", 1.5f, 1f, 3f, 0.125f, "x");
    public static readonly FloatOpt Screen = new("Comet.Screen", 1.125f, 1f, 2f, 0.125f, "x");

    private bool _boosting;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Comet.Blaze"), "comet_boost")
        {
            Cooldown = () => Cooldown,
            Duration = () => Duration,
            OnUse = () =>
            {
                _boosting = true;
                MoveSpeed.Add("Comet", Speed, Duration);
                Cloak.Self(Duration);
                if (Screen > 1f) ViewScale.Set(Screen);
                Fx.Hearing.Self("comet_boost", 0.7f); // 透明になるので本人にだけ
                return true;
            },
            OnEnd = Stop,
        });
        if (IsLocal) Lifespan.OnRelease(() => { _boosting = false; ViewScale.Set(1f); });
    }

    private void Stop()
    {
        if (!_boosting) return;
        _boosting = false;
        Fx.Hearing.Self("comet_boost_end", 0.55f);
        MoveSpeed.Remove("Comet");
        Cloak.Self(0f);
        ViewScale.Set(1f);
    }

    // 停電の影響を受けない (クルーの一番明るい視界のまま)。ブーストの間は広がる
    public override void ModifyVision(ref float radius)
    {
        var ship = ShipStatus.Instance;
        if (ship) radius = ship.MaxLightRadius * GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.CrewLightMod);
        if (_boosting) radius *= Vision;
    }

    // 透明の間はキルされない
    [HostOnly, OnlyMine]
    private void OnBeforeMurder(BeforeMurderEvent e)
    {
        if (!e.Killer || e.Killer.PlayerId == PlayerId || !Cloak.IsOn(PlayerId)) return;
        e.Cancel("cloaked");
        PortKit.HostBlocked(e.Killer, Player); // キルした人の待ち時間を戻す (戻さないと連打できる)
    }
}
