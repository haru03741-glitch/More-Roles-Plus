// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Crewmate/Sonar.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ソナー (クルー): 足元に自分にだけ見えるアンテナを置く。会議の外でアンテナの近くでキルが起きると、
// 全員の画面にその場所への矢印が出て、アンテナは壊れる
public sealed class Sonar : RoleBase
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#347344";
    public override Text Name => new("Sonar.Name");
    public override string Reading => "そなー";
    public override Text Blurb => new("Sonar.Blurb");
    public override string Origin => "Uchu";
    public override bool CanBeMadmate => true;
    public override HelpSection[] HelpSections => new HelpSection[] { new("Sonar.Place", "sonar_antenna") };

    public static readonly FloatOpt PlaceCooldown = new("Sonar.PlaceCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt NumOfAntenna = new("Sonar.NumOfAntenna", 2, 1, 10);
    public static readonly FloatOpt Range = new("Sonar.Range", 1f, 0.25f, 5f, 0.25f, "");
    public static readonly FloatOpt ArrowDuration = new("Sonar.ArrowDuration", 3f, 1f, 10f, 0.5f, "s");
    public static readonly ChoiceOpt ArrowAt = new("Sonar.ArrowAt", 0, new Text("Sonar.ArrowAt.Body"), new Text("Sonar.ArrowAt.Antenna"));
    public static readonly BoolOpt ShareBroken = new("Sonar.ShareBroken", false);

    private sealed class Antenna { public Vector2 At; public SpriteRenderer Art; public bool Broken; }
    private readonly List<Antenna> _antennas = new();
    private static readonly BodyKind AntennaKind = new("SonarAntenna", BodyMaterial.Metal, BodySize.Small, 0f, shared: false);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Sonar.Place"), "sonar_antenna")
        {
            Cooldown = () => PlaceCooldown,
            MaxUses = () => NumOfAntenna,
            OnUse = () =>
            {
                Vector2 at = Player.GetTruePosition();
                var art = PortKit.Marker(Lifespan, at, "sonar_antenna", 0.7f);
                _antennas.Add(new Antenna { At = at, Art = art });
                Bodies.Prop(AntennaKind, art.transform, 0f, 0.25f); // 壊れると聞けなくなるので、当たっても火花だけ
                return true;
            },
        });
    }

    // 自分の端末: アンテナの近くでキル
    [LocalOnly]
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (!e.Target || MeetingHud.Instance) return;
        Vector2 dead = e.Target.GetTruePosition();
        foreach (var a in _antennas)
        {
            if (a.Broken || Vector2.Distance(a.At, dead) > Range) continue;
            a.Broken = true;
            Vector2 to = ArrowAt.Value == 0 ? dead : a.At;
            AlarmCall.Send((to.x, to.y, a.At.x, a.At.y));
            OnAlarm(to, a.At);
            if (a.Art) a.Art.sprite = ButtonIcons.Get("sonar_antenna_broken");
        }
    }

    private static readonly RemoteCall<(float x, float y, float ax, float ay)> AlarmCall = new("Sonar.Alarm", Route.Anyone,
        (w, m) => { w.Write(m.x); w.Write(m.y); w.Write(m.ax); w.Write(m.ay); }, r => (r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        (sender, m) => { if (RoleState.ActingOf(sender) is Sonar) OnAlarm(new Vector2(m.x, m.y), new Vector2(m.ax, m.ay)); }, perSecond: 4);

    // 全員の端末で: 本編のノイズメーカーのような矢印
    private static void OnAlarm(Vector2 to, Vector2 antenna)
    {
        UchuKit.Arrow(to, new UnityEngine.Color(0.2f, 0.45f, 0.27f), ArrowDuration);
        var lp = PlayerControl.LocalPlayer;
        if (lp && lp.Data != null && !lp.Data.IsDead)
            PortSound.PlayVanilla("notice", 0.6f);
        if (ShareBroken && !(RoleState.Acting(RoleState.Local) is Sonar)) PortKit.Marker(RoleState.Match, antenna, "sonar_antenna_broken", 0.7f);
    }
}
