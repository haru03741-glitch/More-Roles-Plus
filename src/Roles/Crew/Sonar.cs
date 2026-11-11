// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Crewmate/Sonar.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ソナー (クルー): 足元に自分にだけ見えるアンテナを置く。会議の外でアンテナの近くでキルが起きると、
// 全員の画面にその場所への矢印が出て、アンテナは壊れる。壁の崩れ・爆発・浸水・点火の音はもっと遠くまで拾い、
// ソナー本人の画面にだけ矢印を出す (アンテナは壊れない)
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
                var art = FloorPropArt.Place(Lifespan, at, "antenna");
                _antennas.Add(new Antenna { At = at, Art = art });
                Bodies.Prop(AntennaKind, art.transform, 0.2f, 0.25f); // 壊れると聞けなくなるので、当たっても火花だけ
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
            FloorPropArt.Swap(a.Art, "antenna_broken");
        }
    }

    // ---- 地形の音 (ソナー本人の画面だけ・アンテナは壊れない) ----
    private const float HearMul = 3f;       // キルを拾う範囲の何倍まで聞こえるか
    private const float WetMul = 2f;        // 水に沈んだアンテナはさらに何倍か (水の中は音がよく通る)
    private const float WetDepth = 0.25f;   // アンテナの高さ。これより深い水で沈んだと数える
    private const float SameNoise = 2f;     // 直前に知らせた所からこの距離の中の音は同じ物として数えない
    private static readonly UnityEngine.Color NoiseColor = new(0.85f, 0.55f, 0.2f);
    private readonly List<(Vector2 At, float Until)> _heard = new();

    // 壁の崩れ・爆発 (全員の端末に同じ順で届く。何も抜けなかった打撃は数えない)
    [LocalOnly]
    private void OnTerrainBroken(TerrainBrokenEvent e)
    {
        if (e.Kind is Terrain.DamageKind.Explosion or Terrain.DamageKind.Crumble || e.WallsCut > 0 || e.PropsBroken)
            Hear(e.Position, e.Kind == Terrain.DamageKind.Explosion ? "blast" : "wall");
    }

    // 浸水の始まり・水漏れの噴き出し・点火
    [LocalOnly]
    private void OnTerrainStirred(TerrainStirredEvent e) => Hear(e.Position, e.Kind == Terrain.DamageKind.Ignite ? "fire" : "water");

    private void Hear(Vector2 at, string what)
    {
        if (_antennas.Count == 0 || MeetingHud.Instance || !Player || Player.Data == null || Player.Data.IsDead) return;
        float now = Time.time;
        for (int i = _heard.Count - 1; i >= 0; i--)
        {
            if (_heard[i].Until < now) { _heard.RemoveAt(i); continue; }
            if (Vector2.Distance(_heard[i].At, at) < SameNoise) return;
        }
        Antenna best = null;
        float bestD = float.MaxValue;
        foreach (var a in _antennas)
        {
            if (a.Broken) continue;
            float reach = Range * HearMul, d = Vector2.Distance(a.At, at);
            if (d >= bestD || d > reach * WetMul) continue;
            // 水の深さは水の計算の手が空くのを待つので、届きうるアンテナだけで読む
            if (d > reach && Terrain.WaterSim.CurrentAt(a.At, out _, out _) < WetDepth) continue;
            best = a;
            bestD = d;
        }
        if (best == null) return;
        _heard.Add((at, now + ArrowDuration));
        Vector2 to = ArrowAt.Value == 0 ? at : best.At;
        Plugin.Logger.LogInfo($"[Sonar] heard {what} {at.x:0.00},{at.y:0.00} d={bestD:0.00}");
        UchuKit.Arrow(to, NoiseColor, ArrowDuration);
        PortSound.PlayVanilla("notice", 0.35f);
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
        if (ShareBroken && !(RoleState.Acting(RoleState.Local) is Sonar)) FloorPropArt.Place(RoleState.Match, antenna, "antenna_broken");
    }
}
