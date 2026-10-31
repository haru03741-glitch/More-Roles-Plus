// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/VampireDependent.cs, Roles/Ability/ReverseVisionAbility.cs (GPL-3.0)
namespace MoreRolesPlus.Roles.Impostor;

// 眷属 (インポスター): ヴァンパイアに作られた人。キルとベント (設定) ができ、ヴァンパイアは倒せない。
// 設定で視界が逆になる (明るい時は狭く、停電中は広い)。ヴァンパイアが死ぬと一緒に死ぬ。最初から配られることはない
public sealed class VampireDependent : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("VampireDependent.Name");
    public override string Reading => "けんぞく";
    public override Text Blurb => new("VampireDependent.Blurb");
    public override string Origin => "SNR";
    public override bool MadeByOthers => true;

    public override bool? CanVent => Vampire.DependentCanVent;

    private Vampire Master => Vampire.MasterOf(PlayerId);

    public override void ModifyKillCooldown(ref float seconds) => seconds = Vampire.DependentKillCooldown;

    public override void ModifyVision(ref float radius)
    {
        var ship = ShipStatus.Instance;
        if (!Vampire.DependentReverseVision || !ship) return;
        if (!PortKit.LightsOut())
        {
            radius = ship.MinLightRadius;
            return;
        }
        if (Vampire.DependentImpostorVisionInLightsOff) PortKit.ImpostorVision(ref radius);
        else radius = ship.MaxLightRadius * GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.CrewLightMod);
    }

    // 親のヴァンパイアは倒せない
    [HostOnly]
    private void OnBeforeMurder(BeforeMurderEvent e)
    {
        if (e.Canceled || !e.Killer || e.Killer.PlayerId != PlayerId) return;
        var m = Master;
        if (m != null && e.Target.PlayerId == m.PlayerId) e.Cancel("vampire master");
    }

    // ヴァンパイアが死ぬと一緒に死ぬ (追放なら死体は残らない)
    [HostOnly]
    private void OnMasterMurdered(PlayerMurderedEvent e)
    {
        var m = Master;
        if (m != null && e.Target && e.Target.PlayerId == m.PlayerId && PortKit.Alive(Player)) PortKit.HostSuicide(Player);
    }

    [HostOnly]
    private void OnMasterExiled(PlayerExiledEvent e)
    {
        var m = Master;
        if (m != null && e.Player && e.Player.PlayerId == m.PlayerId && PortKit.Alive(Player)) PortKit.HostVanish(Player, Player);
    }

    public override string NameMark(PlayerControl seen)
    {
        var m = Master;
        return m != null && seen.PlayerId == m.PlayerId ? "<color=#FF1919>◆</color>" : null;
    }
}
