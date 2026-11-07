// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Impostor/Magician.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to https://github.com/yukieiji/ExtremeRoles (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// マジシャン (インポスター): 「マジック」で生きている全員の位置を無作為に入れ替える。
// 全員の画面が赤く光り、タスクの画面は閉じ、ベントの中の人は外に出る
public sealed class Magician : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Magician.Name");
    public override string Reading => "まじしゃん";
    public override Text Blurb => new("Magician.Blurb");
    public override string Origin => "Uchu, ER";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Magician.Magic", "magician_magic") };

    public static readonly FloatOpt MagicCooldown = new("Magician.MagicCooldown", 20f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt NumOfMagic = new("Magician.NumOfMagic", 4, 1, 15);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Magician.Magic"), "magician_magic")
        {
            Cooldown = () => MagicCooldown,
            MaxUses = () => NumOfMagic,
            OnUse = () =>
            {
                MagicRequest.Send(0); // ホストなら手元で
                return true;
            },
        });
    }

    private static readonly RemoteCall<byte> MagicRequest = new("Magician.Request", Route.ToHost,
        (w, _) => w.Write((byte)0), r => r.ReadByte(),
        (sender, _) => { if (RoleState.Of(sender) is Magician && PortKit.Alive(sender)) HostMagic(); }, perSecond: 2);

    // ホスト: 並べ替えを決めて配る
    private static void HostMagic()
    {
        if (MeetingHud.Instance || ExileController.Instance) return;
        var alive = new List<PlayerControl>();
        foreach (var p in PlayerControl.AllPlayerControls) if (PortKit.Alive(p)) alive.Add(p);
        if (alive.Count < 2) return;
        // 並べ替えた i 番目の人を、i+1 番目の人の元の位置へ (全員が元と違う場所へ行く)
        for (int i = alive.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (alive[i], alive[j]) = (alive[j], alive[i]);
        }
        var orig = new Dictionary<byte, Vector2>();
        foreach (var p in alive) orig[p.PlayerId] = p.transform.position;
        var moves = new List<(byte id, float x, float y)>();
        for (int i = 0; i < alive.Count; i++)
        {
            var to = orig[alive[(i + 1) % alive.Count].PlayerId];
            moves.Add((alive[i].PlayerId, to.x, to.y));
        }
        MagicCall.Send(moves);
        OnMagic(moves);
    }

    private static readonly RemoteCall<List<(byte id, float x, float y)>> MagicCall = new("Magician.Magic", Route.HostToAll,
        (w, list) => { w.Write((byte)list.Count); foreach (var m in list) { w.Write(m.id); w.Write(m.x); w.Write(m.y); } },
        r => { int n = r.ReadByte(); var l = new List<(byte, float, float)>(); for (int i = 0; i < n; i++) l.Add((r.ReadByte(), r.ReadSingle(), r.ReadSingle())); return l; },
        (_, list) => OnMagic(list));

    // 全員の端末で: 光って、自分の体 (と自分の動かすダミー) を動かす
    private static void OnMagic(List<(byte id, float x, float y)> moves)
    {
        var lp = PlayerControl.LocalPlayer;
        PortKit.Flash(new Color(1f, 0.1f, 0.1f, 0.5f));
        foreach (var (id, x, y) in moves)
        {
            var p = PortKit.ById(id);
            if (!p || !(p.AmOwner || Dev.Dummy.Owns(p))) continue;
            if (p.AmOwner && Minigame.Instance) Minigame.Instance.Close();
            if (p.inVent)
            {
                var vent = PortKit.NearestVent(p.GetTruePosition(), 5f);
                if (vent) p.MyPhysics.RpcExitVent(vent.Id);
                p.inVent = false;
            }
            var to = new Vector2(x, y);
            p.NetTransform.RpcSnapTo(to);
        }
    }
}
