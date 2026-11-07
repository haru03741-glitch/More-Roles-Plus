// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Impostor/Witch.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to https://github.com/TheOtherRolesAU/TheOtherRoles (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// ウィッチ (インポスター): 「魔術」で近くの人に呪文を唱え続ける (離れると中断・1 ターンに 1 回)。
// 唱え終えた人は会議の名札に魔術の印が付き (全員に見える)、会議の終わりにウィッチが生きていれば死ぬ
public sealed class Witch : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Witch.Name");
    public override string Reading => "うぃっち";
    public override Text Blurb => new("Witch.Blurb");
    public override string Origin => "Uchu, TOR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Witch.Spell", "witch_spell") };

    public static readonly FloatOpt SpellCooldown = new("Witch.SpellCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt SpellDuration = new("Witch.SpellDuration", 3f, 0.5f, 10f, 0.5f, "s");
    public static readonly BoolOpt NormalKill = new("Witch.NormalKill", true);

    // 呪われた人 (全員の端末で同じ)
    private static readonly HashSet<byte> Spelled = new();
    private static object _match;

    private PlayerControl _target; // 自分の端末: 唱えている相手
    private bool _broken;
    private Ability _spell;

    public override void OnAssigned()
    {
        if (!ReferenceEquals(_match, RoleState.Match))
        {
            _match = RoleState.Match;
            RoleState.Match.OnRelease(() => { Spelled.Clear(); _match = null; });
            RoleFrame.AddExtraMark(RoleState.Match, seen => MeetingHud.Instance && Spelled.Contains(seen.PlayerId) ? "<color=#BF40BF>☆</color>" : null);
        }
        _spell = AddAbility(new Ability(new Text("Witch.Spell"), "witch_spell")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => SpellCooldown,
            Duration = () => SpellDuration,
            UsesPerMeeting = 1,
            CanTarget = (_, t) => !PortKit.IsImpostor(t) && !Spelled.Contains(t.PlayerId),
            OnUsed = (user, t) =>
            {
                if (!user.AmOwner) return;
                _target = t;
                _broken = false;
            },
            OnEnd = () =>
            {
                var t = _target;
                _target = null;
                if (_broken || !PortKit.Alive(t) || MeetingHud.Instance) return;
                SpellCall.Send(t.PlayerId);
                OnSpelled(t.PlayerId);
                PortKit.Flash(new Color(1f, 0.1f, 0.1f, 0.5f));
            },
        });
    }

    private static readonly RemoteCall<byte> SpellCall = new("Witch.Spell", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (sender, id) => { if (RoleState.Of(sender) is Witch) OnSpelled(id); }, perSecond: 2);

    private static void OnSpelled(byte id) => Spelled.Add(id);

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (!NormalKill) UchuKit.HideKillButton();
        if (_target == null) return;
        // 離れたら中断
        if (!PortKit.Alive(_target) || !PortKit.Alive(Player)
            || Vector2.Distance(_target.GetTruePosition(), Player.GetTruePosition()) > GameManager.Instance.LogicOptions.GetKillDistance() * 1.5f)
        {
            _broken = true;
            _spell?.EndEffect();
            PortKit.Notice(new Text("Witch.Broken").ToString());
        }
    }

    // ホスト: 会議の終わりに、生きているウィッチがいれば呪われた人が死ぬ (死体は残らない)
    [HostOnly]
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (Spelled.Count == 0) return;
        Witch alive = null;
        foreach (var r in RoleState.All)
            if (r is Witch w && PortKit.Alive(w.Player)) { alive = w; break; }
        if (alive != this) { if (alive == null) Spelled.Clear(); return; }
        var ids = new List<byte>(Spelled);
        Spelled.Clear();
        foreach (var id in ids)
        {
            var p = PortKit.ById(id);
            if (PortKit.Alive(p)) PortKit.HostVanish(Player, p);
        }
        ClearCall.Send(0);
    }

    private static readonly RemoteCall<byte> ClearCall = new("Witch.Clear", Route.HostToAll,
        (w, _) => w.Write((byte)0), r => r.ReadByte(), (_, _) => Spelled.Clear());

    [HostOnly]
    private void OnBeforeMurder(BeforeMurderEvent e)
    {
        if (NormalKill || e.Canceled || !e.Killer || e.Killer.PlayerId != PlayerId || e.Target.PlayerId == PlayerId) return;
        if (UchuKit.AbilityKill != PlayerId) e.Cancel("witch has no normal kill");
    }
}
