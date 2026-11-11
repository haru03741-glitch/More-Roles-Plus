// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Crewmate/Alchemist.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// アルケミスト (クルー): 「錬金」で近くの死体を消して錬金弾に変える (1 回で決めた数)。
// 会議中、名札の錬金弾のボタンで選んだ人を撃ち抜く (1 回の会議で撃てる数まで)。ゲッサーが撃てない相手は撃てない。
// 「変成」で足元の近くの瓦礫を鉄板に変え、いちばん近い壁の穴をふさぐ (外壁の穴なら吸い出しも止まる・叩くか斬ると割れる)
public sealed class Alchemist : RoleBase
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#5A5FC0";
    public override Text Name => new("Alchemist.Name");
    public override string Reading => "あるけみすと";
    public override Text Blurb => new("Alchemist.Blurb");
    public override string Origin => "Uchu";
    public override bool CanBeMadmate => true;
    public override HelpSection[] HelpSections => new HelpSection[] { new("Alchemist.Alchemy", "alchemist_alchemy"), new("Alchemist.Shoot", "alchemist_bullet"), new("Alchemist.Plate", "alchemist_plate") };

    public static readonly FloatOpt AlchemyCooldown = new("Alchemist.AlchemyCooldown", 30f, 0f, 60f, 2.5f, "s");
    public static readonly IntOpt PerAlchemy = new("Alchemist.PerAlchemy", 3, 1, 15);
    public static readonly IntOpt PerMeeting = new("Alchemist.PerMeeting", 5, 1, 15);
    public static readonly FloatOpt PlateCooldown = new("Alchemist.PlateCooldown", 20f, 0f, 60f, 2.5f, "s");

    private int _bullets; // 全員の端末で同じ
    private Ability _alchemy;

    public override void OnAssigned()
    {
        _alchemy = AddAbility(new Ability(new Text("Alchemist.Alchemy"), "alchemist_alchemy")
        {
            Cooldown = () => AlchemyCooldown,
            OnUse = () =>
            {
                var lp = PlayerControl.LocalPlayer;
                var body = PortKit.NearestBody(lp.GetTruePosition(), lp.MaxReportDistance);
                if (!body || PhysicsHelpers.AnythingBetween(lp.GetTruePosition(), body.TruePosition, Constants.ShipAndObjectsMask, false)) return false;
                AlchemyCall.Send(body.ParentId);
                OnAlchemy(this, body.ParentId);
                return true;
            },
        });
        AddAbility(new Ability(new Text("Alchemist.Shoot"), "alchemist_bullet")
        {
            Target = AbilityTarget.Meeting,
            UsesPerMeeting = PerMeeting,
            GuessLike = true,
            CanTarget = (_, t) => _bullets > 0 && PortKit.Alive(t),
            OnUsed = Shoot,
        });
        AddAbility(new Ability(new Text("Alchemist.Plate"), "alchemist_plate")
        {
            Cooldown = () => PlateCooldown,
            OnUse = TryPlate,
        });
        RubbleBlocks.Keepers++; // 瓦礫の塊の絵を床に焼かずに残す (使うと消える)
        Lifespan.OnRelease(() => RubbleBlocks.Keepers--);
        if (IsLocal) ShowCount();
    }

    // 自分: 使える瓦礫とふさげる穴を手元で確かめてからホストへ頼む (無ければ待ち時間を使わない)
    private bool TryPlate()
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.inVent || lp.onLadder || lp.inMovingPlat) return false;
        var at = lp.GetTruePosition();
        string why = IronPlate.Why(at);
        if (why != null)
        {
            Plugin.Logger.LogInfo($"Alchemist: plate refused here {why}");
            PortKit.Notice(new Text(why == "no rubble" ? "Alchemist.NoRubble" : "Alchemist.NoHole").ToString());
            return false;
        }
        if (AmongUsClient.Instance.AmHost) HostPlate(lp);
        else PlateCall.Send(0);
        return true;
    }

    private static readonly RemoteCall<byte> PlateCall = new("Alchemist.Plate", Route.ToHost,
        (w, v) => w.Write(v), r => r.ReadByte(), (sender, _) => { if (sender) HostPlate(sender); }, perSecond: 2);

    // ホスト: 頼んだ人の足元で、瓦礫と穴を決めて鉄板を立てる (地形の出来事として全員へ)
    private static void HostPlate(PlayerControl by)
    {
        var acting = RoleState.ActingOf(by);
        string bad = null;
        if (acting is not Alchemist) bad = $"role {acting?.Id}";
        else if (!PortKit.Alive(by)) bad = "dead";
        else if (by.inVent) bad = "vent";
        else if (MeetingHud.Instance) bad = "meeting";
        if (bad != null) { Plugin.Logger.LogInfo($"Alchemist: plate by {by.PlayerId} refused {bad}"); return; }
        var res = TerrainSync.RequestAs(new DamageEvent(DamageKind.Plate, by.GetTruePosition(), Vector2.zero, 0f, 0f, (ushort)Random.Range(0, 65536)), by.PlayerId, out bool ok);
        Plugin.Logger.LogInfo($"Alchemist: plate by {by.PlayerId} {(ok ? "" : "refused ")}{res}");
    }

    // 刀が鉄板を斬った端末から (練習の相手はホスト)。ホストが崩す
    internal static void ReportCut(Vector2 at, byte by)
    {
        if (AmongUsClient.Instance.AmHost) HostCut(PortKit.ById(by), at);
        else CutCall.Send(at);
    }

    private static readonly RemoteCall<Vector2> CutCall = new("Alchemist.PlateCut", Route.ToHost,
        (w, v) => { w.Write(v.x); w.Write(v.y); }, r => new Vector2(r.ReadSingle(), r.ReadSingle()), (sender, at) => HostCut(sender, at), perSecond: 4);

    private const float CutReach = 3f;

    private static void HostCut(PlayerControl by, Vector2 at)
    {
        if (!Bodies.FromTest(by))
        {
            if (!PortKit.Alive(by) || RoleState.Of(by) is not Neutral.Samurai) return;
            if (Vector2.Distance(by.GetTruePosition(), at) > CutReach) return;
        }
        IronPlate.HostCut(at, by ? by.PlayerId : ResolvedDamage.NoActor);
    }

    private static readonly RemoteCall<byte> AlchemyCall = new("Alchemist.Alchemy", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (sender, id) => { if (RoleState.ActingOf(sender) is Alchemist a) OnAlchemy(a, id); });

    // 全員の端末で: 死体を消して弾を足す
    private static void OnAlchemy(Alchemist a, byte bodyId)
    {
        foreach (var b in Object.FindObjectsOfType<DeadBody>())
            if (b && b.ParentId == bodyId)
            {
                if (a.Player) BodyConsume.Start(b, BodyConsume.Kind.Transmute, a.Player.GetTruePosition());
                Object.Destroy(b.gameObject);
            }
        a._bullets += PerAlchemy;
        if (a.IsLocal) PortKit.After(0f, a.ShowCount);
    }

    // 全員の端末で: 弾を 1 つ使って撃ち抜く (倒すのはホスト・撃ち抜く演出の後)
    private void Shoot(PlayerControl user, PlayerControl target)
    {
        _bullets = Mathf.Max(_bullets - 1, 0);
        GuessShotFx.Begin(target.PlayerId);
        if (IsLocal)
        {
            ShowCount();
            MeetingAbilities.Refresh();
        }
        if (!AmongUsClient.Instance.AmHost) return;
        PortKit.After(GuessShotFx.HitAt + 0.15f, () =>
        {
            if (!PortKit.Alive(target)) return;
            // 試合中のキルと同じく守り (会議中に効くのは守護者だけ) に聞く。止められたら弾は使ったまま、知らせは守る側が出す
            if (user && user.PlayerId != target.PlayerId && BeforeMurderEvent.Ask(user, target) != null) return;
            PortKit.HostVanish(user ? user : target, target, PortKit.Harm.Fated);
            ShotCall.Send(target.PlayerId);
            Announce(target.PlayerId);
        });
    }

    private static readonly RemoteCall<byte> ShotCall = new("Alchemist.Shot", Route.HostToAll,
        (w, id) => w.Write(id), r => r.ReadByte(), (_, id) => Announce(id));

    private static void Announce(byte id)
    {
        var p = PortKit.ById(id);
        if (p && p.Data != null) PortKit.Notice(new Text("Alchemist.Hit").With("name", p.Data.PlayerName).ToString());
    }

    // 自分の端末: 錬金ボタンの数の欄とタスク欄に残りの弾を出す
    private void ShowCount()
    {
        _alchemy?.Button?.SetUses(_bullets);
        StatusEffects.ShowUntil("Alchemist", new Text("Alchemist.Bullets").With("n", _bullets), Color, Text.Raw(""));
    }
}
