// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Impostor/Nekokabocha.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to https://github.com/yukinogatari/TheOtherRoles-GM (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Impostor;

// ネコカボチャ (インポスター): 自分を倒した人を道連れにする。追放された時は、設定 (または仲間のインポスターが
// もういない時) で自分に投票した人を 1 人道連れにする
public sealed class NekoKabocha : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("NekoKabocha.Name");
    public override string Reading => "ねこかぼちゃ";
    public override Text Blurb => new("NekoKabocha.Blurb");
    public override string Origin => "Uchu, TORGM";
    public override bool CanBeMadmate => true;

    public static readonly BoolOpt ExileEmbroil = new("NekoKabocha.ExileEmbroil", false);

    // ホスト: 最後の会議で自分に入れた人
    private readonly List<byte> _voters = new();

    [HostOnly, OnlyMine]
    private void OnMurdered(PlayerMurderedEvent e)
    {
        Embroil(e.Killer, Killed);
    }

    // 火・水・瓦礫・船外で死んだ時は、その地形を起こした人を道連れにする
    [HostOnly, OnlyMine]
    private void OnTerrainKilled(TerrainKilledEvent e) => Embroil(e.Culprit, Caused);

    private void Embroil(PlayerControl killer, byte why)
    {
        if (!killer || killer.PlayerId == PlayerId || !PortKit.Alive(killer)) return;
        var me = Player;
        PortKit.After(0.2f, () =>
        {
            if (!PortKit.Alive(killer)) return;
            Tell(killer, why);
            PortKit.HostKill(me, killer, PortKit.Harm.Curse);
        });
    }

    [HostOnly]
    private void OnVoteTally(VoteTallyEvent e)
    {
        _voters.Clear();
        foreach (var v in e.Votes)
            if (v.Target == PlayerId && v.Voter != PlayerId && !_voters.Contains(v.Voter)) _voters.Add(v.Voter);
    }

    [HostOnly, OnlyMine]
    private void OnExiled(PlayerExiledEvent e)
    {
        bool impostorAlive = false;
        foreach (var p in PlayerControl.AllPlayerControls)
            if (PortKit.Alive(p) && PortKit.IsImpostor(p) && p.PlayerId != PlayerId) { impostorAlive = true; break; }
        if (impostorAlive && !ExileEmbroil) return;
        var pool = new List<PlayerControl>();
        foreach (var id in _voters) { var p = PortKit.ById(id); if (PortKit.Alive(p)) pool.Add(p); }
        if (pool.Count == 0) return;
        var victim = pool[UnityEngine.Random.Range(0, pool.Count)];
        var me = Player;
        PortKit.After(0.2f, () =>
        {
            if (!PortKit.Alive(victim)) return;
            Tell(victim, Voted);
            PortKit.HostVanish(me, victim, PortKit.Harm.Curse);
        });
    }

    // 道連れにされた本人に、なぜ死んだかを知らせる (倒れる電文より先に送る)
    private const byte Killed = 0, Voted = 1, Caused = 2;

    private static void Tell(PlayerControl who, byte why)
    {
        if (who.AmOwner) Show(why);
        else EmbroilCall.Send((who.PlayerId, why));
    }

    private static readonly Net.RemoteCall<(byte id, byte why)> EmbroilCall = new("NekoKabocha.Embroil", Net.Route.HostToAll,
        (w, m) => { w.Write(m.id); w.Write(m.why); }, r => (r.ReadByte(), r.ReadByte()),
        (_, m) => { var lp = PlayerControl.LocalPlayer; if (lp && lp.PlayerId == m.id) Show(m.why); });

    private static void Show(byte why) =>
        PortKit.Notice(new Text(why switch { Killed => "NekoKabocha.EmbroilKill", Caused => "NekoKabocha.EmbroilCaused", _ => "NekoKabocha.EmbroilVote" }).ToString());
}
