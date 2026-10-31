// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/NiceTeleporter.cs, Roles/Ability/TeleporterAbility.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ナイステレポーター (クルー): 「テレポート！」で、少し待ってから生きている全員を生きている誰かの所へ集める
public sealed class NiceTeleporter : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FF9900";
    public override Text Name => new("NiceTeleporter.Name");
    public override string Reading => "ないすてれぽーたー";
    public override Text Blurb => new("NiceTeleporter.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("NiceTeleporter.Teleport", "snr_teleport") };

    public static readonly FloatOpt TeleportCooldown = new("NiceTeleporter.Cooldown", 45f, 5f, 120f, 5f, "s");
    public static readonly FloatOpt WaitingTime = new("NiceTeleporter.WaitingTime", 3f, 0f, 10f, 1f, "s");

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("NiceTeleporter.Teleport"), "snr_teleport")
        {
            Cooldown = () => TeleportCooldown,
            OnUse = () =>
            {
                var alive = new List<PlayerControl>();
                foreach (var p in PlayerControl.AllPlayerControls)
                    if (PortKit.Alive(p)) alive.Add(p);
                if (alive.Count == 0) return false;
                var dest = alive[Random.Range(0, alive.Count)];
                var msg = (dest.PlayerId, (Vector2)dest.transform.position);
                Gather.Send(msg);
                OnGather(msg.Item1, msg.Item2);
                return true;
            },
        });
    }

    // 全員の端末で: 待ってから、生きていれば自分を飛ばす
    private static readonly RemoteCall<(byte dest, Vector2 pos)> Gather = new("NiceTeleporter.Gather", Route.Anyone,
        (w, m) => { w.Write(m.dest); w.Write(m.pos.x); w.Write(m.pos.y); },
        r => (r.ReadByte(), new Vector2(r.ReadSingle(), r.ReadSingle())),
        (sender, m) =>
        {
            if (RoleState.Of(sender) is NiceTeleporter) OnGather(m.dest, m.pos);
        });

    private static void OnGather(byte destId, Vector2 pos)
    {
        var dest = PortKit.ById(destId);
        StatusEffects.ShowFor("NiceTeleporter", new Text("NiceTeleporter.Soon").With("name", dest ? dest.Data.PlayerName : "?"), "#FF9900", WaitingTime);
        PortKit.After(WaitingTime, () =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!PortKit.Alive(lp) || MeetingHud.Instance || ExileController.Instance) return;
            if (lp.inVent) return;
            Net.Teleport.Self(pos);
        });
    }
}
