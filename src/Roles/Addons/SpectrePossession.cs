// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Modifier/Spectre.Possession.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles.Neutral;
using UnityEngine;

namespace MoreRolesPlus.Roles.Addons;

// 狐憑き (アドオン・クルーとインポスターだけ): 妖狐 (スペクター) の陣営になる。妖狐が勝った時だけ勝ち、タスクは数えない。
// 透視 (設定) で壁越しに見える。狂信 (設定) で妖狐の名前が妖狐の色に見える
// (制限ありなら、クルーはタスクを決めた数・インポスターはキルを決めた数こなすと狂信になる)
public sealed class SpectrePossession : AddonBase
{
    public override string Color => Spectre.Hex;
    public override Text Name => new("SpectrePossession.Name");
    public override string Reading => "きつねつき";
    public override Text Blurb => new("SpectrePossession.Blurb");
    public override string Origin => "Uchu";

    protected internal override bool DefaultOnNeutral => false;
    public override bool CanAttach(Team team, RoleBase role) => team != Team.Neutral && base.CanAttach(team, role);

    public static readonly BoolOpt Clairvoyance = new("SpectrePossession.Clairvoyance", true);
    public static readonly BoolOpt Fanaticism = new("SpectrePossession.Fanaticism", true);
    public static readonly BoolOpt FanaticismLimit = new("SpectrePossession.FanaticismLimit", false) { When = () => Fanaticism };
    public static readonly IntOpt NumOfTask = new("SpectrePossession.NumOfTaskFanaticism", 4, 1, 10) { When = () => FanaticismLimit };
    public static readonly IntOpt NumOfKill = new("SpectrePossession.NumOfKillFanaticism", 1, 1, 10) { When = () => FanaticismLimit };

    public override bool AffectsWin => true;
    public override bool TasksCount => false;

    private int _kills;
    private bool _fanatic = !FanaticismLimit;

    public override bool AlsoWins(GameResult r) => r.Role is Spectre && r.Team == null;
    public override bool LosesWin(GameResult r) => !(r.Role is Spectre && r.Team == null);

    // 自分の端末が透視しているか
    internal static bool LocalClairvoyant
    {
        get
        {
            if (!Clairvoyance || MeetingHud.Instance) return false;
            var lp = PlayerControl.LocalPlayer;
            if (!PortKit.Alive(lp)) return false;
            foreach (var a in RoleState.AddonsOf(lp.PlayerId)) if (a is SpectrePossession) return true;
            return false;
        }
    }

    [LocalOnly, OnlyMine]
    private void OnTaskCompleted(TaskCompletedEvent e)
    {
        if (!PortKit.IsImpostor(Player) && PortKit.TasksDone(Player) >= NumOfTask) _fanatic = true;
    }

    [LocalOnly]
    private void OnPlayerMurdered(PlayerMurderedEvent e)
    {
        if (!e.Killer || e.Killer.PlayerId != PlayerId || e.Target.PlayerId == PlayerId || !PortKit.IsImpostor(Player)) return;
        if (++_kills >= NumOfKill) _fanatic = true;
    }

    // 狂信: 妖狐の名前が妖狐の色に見える
    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (!Fanaticism || !_fanatic) return;
        UnityEngine.Color c = new Color32(185, 152, 197, 255);
        foreach (var r in RoleState.All)
        {
            if (r is not Spectre || !r.Player || !r.Player.cosmetics || !r.Player.cosmetics.nameText) continue;
            r.Player.cosmetics.nameText.color = c;
            if (MeetingHud.Instance)
                foreach (var pva in MeetingHud.Instance.playerStates)
                    if (pva && pva.PlayerId.Value == r.PlayerId && pva.NameText) pva.NameText.color = c;
        }
    }
}
