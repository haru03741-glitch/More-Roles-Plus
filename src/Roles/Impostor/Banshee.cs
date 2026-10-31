// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Banshee.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// バンシー (インポスター): 「解放」で誰かに妖精を付ける。妖精は近づいた人へ次々に移る。「囁き」の後、
// 妖精を持つ人に新しく近づいた人を倒す。設定で普通のキルをできなくする
public sealed class Banshee : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Banshee.Name");
    public override string Reading => "ばんしー";
    public override Text Blurb => new("Banshee.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Banshee.Release", "snr_fairy"), new("Banshee.Whisper", "snr_whisper") };

    public static readonly FloatOpt ReleaseCooldown = new("Banshee.ReleaseCooldown", 25f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt WhisperCooldown = new("Banshee.WhisperCooldown", 0f, 0f, 10f, 10f, "s");
    public static readonly ChoiceOpt FairyRange = new("Banshee.FairyRange", 0,
        new Text("Banshee.Range.Short"), new Text("Banshee.Range.Medium"), new Text("Banshee.Range.Long"));
    public static readonly BoolOpt CanKillImpostor = new("Banshee.CanKillImpostor", false);
    public static readonly BoolOpt CanNormalKill = new("Banshee.CanNormalKill", false);

    private static float Range => FairyRange.Value switch { 1 => 1.8f, 2 => 2.5f, _ => 1f };

    private byte _fairy = 255;
    private bool _whisper;
    private readonly HashSet<byte> _inRange = new();
    private bool _hostWhisperKill;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Banshee.Release"), "snr_fairy")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => ReleaseCooldown,
            CanTarget = (_, _) => !IsLocal || (_fairy == 255 && !_whisper),
            OnUsed = (user, target) =>
            {
                if (!user.AmOwner) return;
                _fairy = target.PlayerId;
                _whisper = false;
            },
        });
        AddAbility(new Ability(new Text("Banshee.Whisper"), "snr_whisper")
        {
            Cooldown = () => WhisperCooldown,
            OnUse = () =>
            {
                if (_fairy == 255 || _whisper) return false;
                _whisper = true;
                return true;
            },
        });
    }

    private void ResetStatus()
    {
        _fairy = 255;
        _whisper = false;
        _inRange.Clear();
    }

    [LocalOnly]
    private void OnMeeting(MeetingStartEvent e) => ResetStatus();

    // 普通のキルで倒したら妖精は消える
    [LocalOnly]
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (e.Killer && e.Killer.PlayerId == PlayerId) ResetStatus();
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        var hud = Vanilla.Hud;
        if ((!CanNormalKill || _whisper) && hud && hud.KillButton && hud.KillButton.gameObject.activeSelf) hud.KillButton.gameObject.SetActive(false);
        if (!PortKit.Alive(Player))
        {
            if (_fairy != 255) ResetStatus();
            return;
        }
        if (_fairy == 255) return;
        var holder = PortKit.ById(_fairy);
        if (!PortKit.Alive(holder))
        {
            ResetStatus();
            return;
        }
        float range = Range;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p.AmOwner || p.PlayerId == _fairy) continue;
            if (Vector2.Distance(holder.transform.position, p.transform.position) <= range)
            {
                if (_inRange.Contains(p.PlayerId)) continue;
                _inRange.Add(p.PlayerId);
                if (_whisper)
                {
                    if (PortKit.IsImpostor(p) && !CanKillImpostor) continue;
                    if (AmongUsClient.Instance.AmHost) HostWhisperKill(Player, p);
                    else KillCall.Send(p.PlayerId);
                    Player.SetKillTimer(GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.KillCooldown));
                    ResetStatus();
                    break;
                }
                _fairy = p.PlayerId;
            }
            else _inRange.Remove(p.PlayerId);
        }
    }

    // 妖精を持っている人の名前に印 (自分の画面だけ)
    public override string NameMark(PlayerControl seen) =>
        seen && seen.PlayerId == _fairy ? "<color=#FF77FF>✧</color>" : null;

    [HostOnly]
    private void OnKill(BeforeMurderEvent e)
    {
        if (!CanNormalKill && !_hostWhisperKill && e.Killer && e.Killer.PlayerId == PlayerId && e.Target && e.Killer != e.Target)
            e.Cancel("banshee cannot kill normally");
    }

    private static readonly RemoteCall<byte> KillCall = new("Banshee.Kill", Route.ToHost,
        (w, id) => w.Write(id), r => r.ReadByte(), (sender, id) => HostWhisperKill(sender, PortKit.ById(id)));

    private static void HostWhisperKill(PlayerControl banshee, PlayerControl target)
    {
        if (RoleState.Of(banshee) is not Banshee b || !PortKit.Alive(banshee) || !PortKit.Alive(target)) return;
        b._hostWhisperKill = true;
        try { PortKit.HostKill(banshee, target); }
        finally { b._hostWhisperKill = false; }
    }
}
