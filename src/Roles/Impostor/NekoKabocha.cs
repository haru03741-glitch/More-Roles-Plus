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
        var killer = e.Killer;
        if (!killer || killer.PlayerId == PlayerId || !PortKit.Alive(killer)) return;
        var me = Player;
        PortKit.After(0.2f, () => { if (PortKit.Alive(killer)) PortKit.HostKill(me, killer); });
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
        PortKit.After(0.2f, () => { if (PortKit.Alive(victim)) PortKit.HostVanish(me, victim); });
    }
}
