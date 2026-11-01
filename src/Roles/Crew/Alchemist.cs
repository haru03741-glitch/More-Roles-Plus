// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Crewmate/Alchemist.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// アルケミスト (クルー): 「錬金」で近くの死体を消して錬金弾に変える (1 回で決めた数)。
// 会議中、名札の錬金弾のボタンで選んだ人を撃ち抜く (1 回の会議で撃てる数まで)。ゲッサーが撃てない相手は撃てない
public sealed class Alchemist : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#5A5FC0";
    public override Text Name => new("Alchemist.Name");
    public override string Reading => "あるけみすと";
    public override Text Blurb => new("Alchemist.Blurb");
    public override string Origin => "Uchu";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Alchemist.Alchemy", "alchemist_alchemy"), new("Alchemist.Shoot", "alchemist_bullet") };

    public static readonly FloatOpt AlchemyCooldown = new("Alchemist.AlchemyCooldown", 30f, 0f, 60f, 2.5f, "s");
    public static readonly IntOpt PerAlchemy = new("Alchemist.PerAlchemy", 3, 1, 15);
    public static readonly IntOpt PerMeeting = new("Alchemist.PerMeeting", 5, 1, 15);

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
        if (IsLocal) ShowCount();
    }

    private static readonly RemoteCall<byte> AlchemyCall = new("Alchemist.Alchemy", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (sender, id) => { if (RoleState.Of(sender) is Alchemist a) OnAlchemy(a, id); });

    // 全員の端末で: 死体を消して弾を足す
    private static void OnAlchemy(Alchemist a, byte bodyId)
    {
        foreach (var b in Object.FindObjectsOfType<DeadBody>())
            if (b && b.ParentId == bodyId) Object.Destroy(b.gameObject);
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
            PortKit.HostVanish(user ? user : target, target);
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
