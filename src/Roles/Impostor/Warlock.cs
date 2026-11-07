// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Impostor/Warlock.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to https://github.com/TheOtherRolesAU/TheOtherRoles (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// ウォーロック (インポスター): 本編のキルは無い。「呪う」で近くの人を呪い (本人は気付かない)、
// 呪った人のすぐ近くに別の人がいる時に「呪殺」で、呪った人にその人を倒させる。倒した後は少しの間動けない
public sealed class Warlock : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Warlock.Name");
    public override string Reading => "うぉーろっく";
    public override Text Blurb => new("Warlock.Blurb");
    public override string Origin => "Uchu, TOR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Warlock.Curse", "warlock_curse"), new("Warlock.CurseKill", "warlock_cursekill") };

    public static readonly FloatOpt CurseCooldown = new("Warlock.CurseCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt CurseKillCooldown = new("Warlock.CurseKillCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly BoolOpt CanKillAllies = new("Warlock.CanKillAllies", true);
    public static readonly FloatOpt KillFreezeTime = new("Warlock.KillFreezeTime", 3f, 0f, 10f, 0.5f, "s");

    private byte _cursed = 255; // 全員の端末で同じ (本人以外には見せない)
    private Ability _curse, _kill;

    public override void OnAssigned()
    {
        _curse = AddAbility(new Ability(new Text("Warlock.Curse"), "warlock_curse")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => CurseCooldown,
            Usable = () => _cursed == 255, // 呪っている間はボタンが暗い
            OnUsed = (_, t) => _cursed = t.PlayerId,
        });
        _kill = AddAbility(new Ability(new Text("Warlock.CurseKill"), "warlock_cursekill")
        {
            Cooldown = () => CurseKillCooldown,
            Usable = () => Victim() != null, // 使えない間はボタンを暗くする
            OnUse = () =>
            {
                if (Victim() is not { } v) return false;
                KillCall.Send(v.PlayerId); // ホストなら手元で通す
                return true;
            },
        });
    }

    // 呪った人のすぐ近くの、呪った人とは別の生きている人 (いちばん近い人)
    private PlayerControl Victim()
    {
        var cursed = PortKit.ById(_cursed);
        if (!PortKit.Alive(cursed)) return null;
        float reach = GameManager.Instance.LogicOptions.GetKillDistance();
        PlayerControl best = null;
        float bestD = reach;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p.PlayerId == _cursed || p.inVent) continue;
            if (!CanKillAllies && PortKit.IsImpostor(p) && p.PlayerId != PlayerId) continue;
            float d = Vector2.Distance(p.GetTruePosition(), cursed.GetTruePosition());
            if (d <= bestD) { best = p; bestD = d; }
        }
        return best;
    }

    private static readonly RemoteCall<byte> KillCall = new("Warlock.CurseKill", Route.ToHost,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (sender, id) => { if (RoleState.Of(sender) is Warlock w) HostCurseKill(w, id); }, perSecond: 2);

    // ホスト: 呪った人に倒させる (本編のキルの見た目で、呪った人が倒された人の所へ移る)
    private static void HostCurseKill(Warlock w, byte victimId)
    {
        var cursed = PortKit.ById(w._cursed);
        var victim = PortKit.ById(victimId);
        if (!PortKit.Alive(w.Player) || !PortKit.Alive(cursed) || !PortKit.Alive(victim)) return;
        if (Vector2.Distance(cursed.GetTruePosition(), victim.GetTruePosition()) > GameManager.Instance.LogicOptions.GetKillDistance() + 1f) return;
        UchuKit.HostAbilityKill(cursed, victim);
        DoneCall.Send(w.PlayerId);
        OnDone(w.PlayerId);
    }

    private static readonly RemoteCall<byte> DoneCall = new("Warlock.Done", Route.HostToAll,
        (w, id) => w.Write(id), r => r.ReadByte(), (_, id) => OnDone(id));

    private static void OnDone(byte id)
    {
        if (RoleState.Of(id) is not Warlock w) return;
        w._cursed = 255;
        if (!w.IsLocal) return;
        MoveSpeed.Add("WarlockFreeze", 0f, KillFreezeTime);
        w._curse?.Button?.StartCooldown();
        PortKit.After(KillFreezeTime, () => w._kill?.Button?.StartCooldown());
    }

    // 呪った人が死ぬ・会議で呪いが消える
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (e.Target && e.Target.PlayerId == _cursed) _cursed = 255;
    }

    private void OnMeetingStart(MeetingStartEvent e) => _cursed = 255;

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        UchuKit.HideKillButton();
        _kill?.Button?.SetUsable(Victim() != null);
    }

    public override string NameMark(PlayerControl seen) =>
        seen.PlayerId == _cursed && IsLocal ? "<color=#FF1919>†</color>" : null;

    // ホスト: 本編のキルボタンでのキルは通さない
    [HostOnly]
    private void OnBeforeMurder(BeforeMurderEvent e)
    {
        if (e.Canceled || !e.Killer || e.Killer.PlayerId != PlayerId || e.Target.PlayerId == PlayerId) return;
        if (UchuKit.AbilityKill != PlayerId) e.Cancel("warlock has no normal kill");
    }
}
