// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Madmates/MadKiller.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// マッドキラー (クルーの姿・インポスターの味方): サイドキラーに変えられた人。サイドキラーが死ぬと、
// インポスターになってキルできるようになる。最初から配られることはない
public sealed class MadKiller : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FF1919";
    public override Text Name => new("MadKiller.Name");
    public override string Reading => "まっどきらー";
    public override Text Blurb => new("MadKiller.Blurb");
    public override string Origin => "SNR";
    public override Team? WinsWithTeam => Team.Impostor;
    public override bool MadeByOthers => true;

    // 変えた人 (サイドキラー)。ホストが全員へ配る
    internal static readonly System.Collections.Generic.Dictionary<byte, byte> Master = new();
    public byte MasterId => Master.TryGetValue(PlayerId, out var m) ? m : (byte)255;

    internal static void SendMaster(byte who, byte master) => MasterCall.Send((who, master));

    private static readonly RemoteCall<(byte who, byte master)> MasterCall = new("MadKiller.Master", Route.HostToAll,
        (w, m) => { w.Write(m.who); w.Write(m.master); }, r => (r.ReadByte(), r.ReadByte()),
        (_, m) => Master[m.who] = m.master);

    public override void OnAssigned() => RoleState.Match.OnRelease(Master.Clear);

    // サイドキラーが死んだら、ホストがインポスターに上げる
    [HostOnly]
    private void OnMurdered(PlayerMurderedEvent e) => Check(e.Target);

    [HostOnly]
    private void OnExiled(PlayerExiledEvent e) => Check(e.Player);

    private void Check(PlayerControl dead)
    {
        if (!dead || dead.PlayerId != MasterId || !PortKit.Alive(Player)) return;
        var imp = Options.Registry.Roles.Find(r => r.IsVanilla && r.Team == Team.Impostor && r.BaseRole == AmongUs.GameOptions.RoleTypes.Impostor);
        PortKit.After(0.1f, () => RoleState.Change(Player, imp));
    }

    public override string NameMark(PlayerControl seen) =>
        seen.PlayerId == MasterId ? "<color=#FF1919>◇</color>" : null;
}
