// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Impostor/Commander.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to https://github.com/yukieiji/ExtremeRoles (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// コマンダー (インポスター): 「指令」の間、インポスター全員のキルの待ち時間を短くする。
// 終わるとインポスターのキルの待ち時間は最初からになり、しばらく進まない
public sealed class Commander : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Commander.Name");
    public override string Reading => "こまんだー";
    public override Text Blurb => new("Commander.Blurb");
    public override string Origin => "Uchu, ER";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Commander.Command", "commander_command") };

    public static readonly FloatOpt CommandCooldown = new("Commander.CommandCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt NumOfCommand = new("Commander.NumOfCommand", 2, 1, 15);
    public static readonly FloatOpt CommandDuration = new("Commander.CommandDuration", 15f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt CommandKillCooldown = new("Commander.CommandKillCooldown", 0f, 0f, 30f, 0.5f, "s");
    public static readonly FloatOpt KillButtonLostTime = new("Commander.KillButtonLostTime", 30f, 0f, 60f, 2.5f, "s");

    // 自分の端末 (インポスターなら): 指令の終わる時刻・キルの待ち時間が進まない間の終わる時刻
    private static float _commandUntil = -1f, _lostUntil = -1f;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Commander.Command"), "commander_command")
        {
            Cooldown = () => CommandCooldown,
            MaxUses = () => NumOfCommand,
            Duration = () => CommandDuration,
            OnUse = () =>
            {
                CommandCall.Send(0);
                OnCommand();
                return true;
            },
        });
    }

    private static readonly RemoteCall<byte> CommandCall = new("Commander.Command", Route.Anyone,
        (w, _) => w.Write((byte)0), r => r.ReadByte(),
        (sender, _) => { if (RoleState.Of(sender) is Commander) OnCommand(); }, perSecond: 2);

    // 全員の端末で: インポスターにだけ効く
    private static void OnCommand()
    {
        var lp = PlayerControl.LocalPlayer;
        if (!PortKit.Alive(lp) || !PortKit.IsImpostor(lp) || MeetingHud.Instance) return;
        _commandUntil = Time.time + CommandDuration;
        _lostUntil = -1f;
        lp.SetKillTimer(CommandKillCooldown);
        PortSound.Play("commander_command", () => PortSound.Fallback("commander_alert"), 0.8f);
        for (int i = 0; i < 3; i++) PortKit.After(0.35f * i, () => PortKit.Flash(new Color(1f, 0.1f, 0.1f, 0.45f), 0.3f));
        PortKit.Notice(new Text("Commander.Ordered").ToString());
        StatusEffects.ShowFor("CommanderCommand", new Text("Commander.Status"), "#FF1919", CommandDuration);
        Tick();
    }

    private static bool _ticking;

    private static void Tick()
    {
        if (_ticking) return;
        _ticking = true;
        UchuKit.Every(() =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp || MeetingHud.Instance) { _commandUntil = _lostUntil = -1f; _ticking = false; return false; }
            float now = Time.time;
            if (_commandUntil > 0f)
            {
                if (now < _commandUntil)
                {
                    if (lp.killTimer > CommandKillCooldown) lp.SetKillTimer(CommandKillCooldown);
                    return true;
                }
                _commandUntil = -1f;
                float full = GameManager.Instance.LogicOptions.GetKillCooldown();
                lp.SetKillTimer(full);
                if (KillButtonLostTime > 0f)
                {
                    _lostUntil = now + KillButtonLostTime;
                    StatusEffects.ShowFor("CommanderLost", new Text("Commander.Lost"), "#808080", KillButtonLostTime);
                }
            }
            if (_lostUntil > 0f && now < _lostUntil)
            {
                // 待ち時間が進まない
                float full = GameManager.Instance.LogicOptions.GetKillCooldown();
                if (lp.killTimer < full) lp.SetKillTimer(full);
                return true;
            }
            _lostUntil = -1f;
            _ticking = false;
            return false;
        });
    }
}
