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

    // 確かめ用 (bridge `chicken omen`): 当たりを引いた時と同じ流れを今すぐ始める
    internal void OmenForTest() => _fear.OmenForTest(Player);

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

// チキンの死に方 (役職とアドオンで共通・自分の端末)。当たりを引くとその場で固まって震え (全員に見える)、少しして倒れる。
// 無事に通った時は本人の画面にだけ冷や汗が出る
internal sealed class ChickenFear
{
    private bool _nearVent, _ladder, _plat, _door;
    private float _dark, _omenAt = -1f, _sentAt = -99f;

    internal void Tick(PlayerControl p, int vent, int ladder, int zip, int plat, int door, float blackout)
    {
        if (!PortKit.Alive(p) || MeetingHud.Instance || ExileController.Instance || !ShipStatus.Instance)
        {
            // 震えている間に会議が始まった・ほかの理由で死んだ = 倒れずに済む
            _dark = 0f;
            EndOmen();
            return;
        }
        if (_omenAt >= 0f)
        {
            if (Time.time - _omenAt < ChickenFx.OmenTime) return;
            EndOmen();
            _sentAt = Time.time;
            PortKit.Suicide();
            return;
        }
        // 自決を送ってから倒れたと届くまでの間 (客は数 tick かかる) に、もう一度引かない
        if (Time.time - _sentAt < 2f) return;

        // 縁の旗は引く前に書く (当たった後に同じ縁でまた引かない)
        bool near = PortKit.NearestVent(p.GetTruePosition(), 0.6f);
        bool hit = near && !_nearVent;
        _nearVent = near;
        if (hit && Roll(p, vent)) { Omen(p); return; }

        bool onLadder = p.MyPhysics && p.MyPhysics.Animations != null && p.MyPhysics.Animations.IsPlayingAnyLadderAnimation();
        hit = onLadder && !_ladder;
        _ladder = onLadder;
        if (hit && Roll(p, ladder)) { Omen(p); return; }

        bool onPlat = p.inMovingPlat;
        bool fungle = ShipStatus.Instance.Type == ShipStatus.MapType.Fungle;
        hit = onPlat && !_plat;
        _plat = onPlat;
        if (hit && Roll(p, fungle ? zip : plat)) { Omen(p); return; }

        var mg = Minigame.Instance;
        bool doorGame = mg && mg.GetIl2CppType().Name.Contains("Door");
        hit = doorGame && !_door;
        _door = doorGame;
        if (hit && Roll(p, door)) { mg.Close(); Omen(p); return; }

        if (PortKit.LightsOut())
        {
            _dark += Time.fixedDeltaTime;
            if (_dark >= blackout) { _dark = 0f; Omen(p); }
        }
        else _dark = 0f;
    }

    // 当たれば死ぬ。外れたら冷や汗 (確率 0 = 危なくない所なので何も出さない)
    private static bool Roll(PlayerControl p, int percent)
    {
        if (percent <= 0) return false;
        if (Random.Range(0, 100) < percent) return true;
        ChickenFx.Sweat(p);
        return false;
    }

    internal void OmenForTest(PlayerControl p)
    {
        if (PortKit.Alive(p) && _omenAt < 0f) Omen(p);
    }

    private void Omen(PlayerControl p)
    {
        _omenAt = Time.time;
        ChickenFx.LocalOmen = p.PlayerId;
        ChickenFx.LocalOmenAt = Time.time;
        ChickenFx.Tremble(p);
        OmenCall.Send(0);
    }

    private void EndOmen()
    {
        if (_omenAt < 0f) return;
        _omenAt = -1f;
        ChickenFx.LocalOmen = 255;
    }

    // 震え始めたことをほかの人へ (受け手は送り主の体を震わせるだけ)
    private static readonly Net.RemoteCall<byte> OmenCall = new("Chicken.Omen", Net.Route.Anyone,
        (w, _) => w.Write((byte)0), r => r.ReadByte(),
        (sender, _) => { if (sender && !MeetingHud.Instance) ChickenFx.Tremble(sender); }, perSecond: 4);
}
