// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Crewmate/Chicken.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to https://github.com/ykundesu/SuperNewRoles (GPL-3.0)
using System.Linq;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// チキン (クルー): 臆病で、ベントに近づく・はしご・ジップライン・昇降機・ドアの操作画面を開くたびに決めた確率で死に、
// 停電が長く続いても死ぬ。試合の終わり (破壊工作以外) に、生きている本当のクルーのチキンがタスクを決めた割合だけ終えていれば、
// クルーの勝ちに替える (チキンの勝ち)
public sealed class Chicken : RoleBase, IWinOverride
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#FFEE00";
    public override Text Name => new("Chicken.Name");
    public override string Reading => "ちきん";
    public override Text Blurb => new("Chicken.Blurb");
    public override string Origin => "Uchu, SNR";

    public static readonly IntOpt VentChance = new("Chicken.VentChance", 20, 0, 100, 5, "%");
    public static readonly IntOpt LadderChance = new("Chicken.LadderChance", 20, 0, 100, 5, "%");
    public static readonly IntOpt ZiplineChance = new("Chicken.ZiplineChance", 20, 0, 100, 5, "%");
    public static readonly IntOpt PlatformChance = new("Chicken.PlatformChance", 20, 0, 100, 5, "%");
    public static readonly IntOpt DoorChance = new("Chicken.DoorChance", 20, 0, 100, 5, "%");
    public static readonly FloatOpt BlackoutTime = new("Chicken.BlackoutTime", 40f, 5f, 120f, 5f, "s");
    public static readonly IntOpt TaskPercent = new("Chicken.TaskPercent", 60, 0, 100, 5, "%");

    private readonly ChickenFear _fear = new();

    [LocalOnly]
    public override void OnFixedUpdate() => _fear.Tick(Player, VentChance, LadderChance, ZiplineChance, PlatformChance, DoorChance, BlackoutTime);

    private bool Reached => Player && Player.Data != null && Player.Data.Tasks != null && Player.Data.Tasks.Count > 0
        && PortKit.TasksDone(Player) * 100 >= TaskPercent * Player.Data.Tasks.Count;

    public GameResult OverrideWin(GameResult r)
    {
        if (r.BySabotage || r.Team == Team.Crew) return null;
        var chicken = RoleState.All.OfType<Chicken>().FirstOrDefault(c => PortKit.Alive(c.Player) && UchuKit.TrueCrew(c.Player) && c.Reached);
        if (chicken != this) return null;
        var nr = GameEnd.TeamResult(Team.Crew);
        nr.Role = this;
        return nr;
    }

    // 達成前は名前の横に灰色の印 (自分にだけ)
    public override string NameMark(PlayerControl seen) =>
        seen.PlayerId == PlayerId && !Reached ? "<color=#808080>◆</color>" : null;
}

// チキンの死に方 (役職とアドオンで共通・自分の端末)
internal sealed class ChickenFear
{
    private bool _nearVent, _ladder, _plat, _door;
    private float _dark;

    internal void Tick(PlayerControl p, int vent, int ladder, int zip, int plat, int door, float blackout)
    {
        if (!PortKit.Alive(p) || MeetingHud.Instance || ExileController.Instance || !ShipStatus.Instance) { _dark = 0f; return; }
        bool near = PortKit.NearestVent(p.GetTruePosition(), 0.6f);
        if (near && !_nearVent && Roll(vent)) { Die(); return; }
        _nearVent = near;

        bool onLadder = p.MyPhysics && p.MyPhysics.Animations != null && p.MyPhysics.Animations.IsPlayingAnyLadderAnimation();
        if (onLadder && !_ladder && Roll(ladder)) { Die(); return; }
        _ladder = onLadder;

        bool onPlat = p.inMovingPlat;
        bool fungle = ShipStatus.Instance.Type == ShipStatus.MapType.Fungle;
        if (onPlat && !_plat && Roll(fungle ? zip : plat)) { Die(); return; }
        _plat = onPlat;

        var mg = Minigame.Instance;
        bool doorGame = mg && mg.GetIl2CppType().Name.Contains("Door");
        if (doorGame && !_door && Roll(door)) { mg.Close(); Die(); return; }
        _door = doorGame;

        if (PortKit.LightsOut())
        {
            _dark += Time.fixedDeltaTime;
            if (_dark >= blackout) { _dark = 0f; Die(); }
        }
        else _dark = 0f;
    }

    private static bool Roll(int percent) => percent > 0 && Random.Range(0, 100) < percent;

    private static void Die() => PortKit.Suicide();
}
