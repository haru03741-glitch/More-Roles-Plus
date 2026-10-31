// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/Safecracker.cs, Roles/Ability/SafecrackerAbility.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// 金庫破り (第三陣営): 自分のタスクを全部終えると、その時点で単独で勝つ。タスクを設定の割合こなすごとに力が増える:
// キルを防ぐ (回数)・追放されても生き返る (回数)・ベントを使える・インポスターの視界・インポスターが分かる
public sealed class Safecracker : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#F8D958";
    public override Text Name => new("Safecracker.Name");
    public override string Reading => "きんこやぶり";
    public override Text Blurb => new("Safecracker.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Safecracker.Unlock") };

    public static readonly IntOpt KillGuardTask = new("Safecracker.KillGuardTask", 50, 0, 100, 5, "%");
    public static readonly IntOpt MaxKillGuard = new("Safecracker.MaxKillGuard", 1, 1, 15) { When = () => KillGuardTask > 0 };
    public static readonly IntOpt ExiledGuardTask = new("Safecracker.ExiledGuardTask", 50, 0, 100, 5, "%");
    public static readonly IntOpt MaxExiledGuard = new("Safecracker.MaxExiledGuard", 1, 1, 15) { When = () => ExiledGuardTask > 0 };
    public static readonly IntOpt VentTask = new("Safecracker.VentTask", 50, 0, 100, 5, "%");
    public static readonly IntOpt ImpostorLightTask = new("Safecracker.ImpostorLightTask", 50, 0, 100, 5, "%");
    public static readonly IntOpt CheckImpostorTask = new("Safecracker.CheckImpostorTask", 50, 0, 100, 5, "%");
    public static readonly IntOpt CommonTasks = new("Safecracker.CommonTasks", 3, 0, 10);
    public static readonly IntOpt ShortTasks = new("Safecracker.ShortTasks", 3, 0, 20);
    public static readonly IntOpt LongTasks = new("Safecracker.LongTasks", 3, 0, 10);

    public override bool TasksCount => false;

    private int _killGuards, _exileGuards;
    private bool _vent;

    public override bool? CanVent => _vent;

    public override void OnAssigned()
    {
        if (IsLocal && CommonTasks + ShortTasks + LongTasks > 0)
            PortKit.After(0.5f, () => { if (RoleState.Local == this) SnrKit.AssignOwnTasks(CommonTasks, ShortTasks, LongTasks); });
    }

    private bool Reached(int rate)
    {
        if (rate <= 0 || !Player || Player.Data == null) return false;
        int total = Player.Data.Tasks != null && Player.Data.Tasks.Count > 0 ? Player.Data.Tasks.Count : CommonTasks + ShortTasks + LongTasks;
        return PortKit.TasksDone(Player) >= Mathf.CeilToInt(total * (rate / 100f));
    }

    public override void ModifyVision(ref float radius)
    {
        if (Reached(ImpostorLightTask)) PortKit.ImpostorVision(ref radius);
    }

    public override string NameMark(PlayerControl seen) =>
        Reached(CheckImpostorTask) && PortKit.IsImpostor(seen) ? "<color=#FF1919>●</color>" : null;

    // タスクが進むたびに: ベントの解放と勝ちを確かめる (全員の端末で)
    [OnlyMine]
    private void OnTask(TaskCompletedEvent e)
    {
        if (!_vent && Reached(VentTask))
        {
            _vent = true;
            Rules.GameRules.ApplyRoleVent(this);
        }
        if (AmongUsClient.Instance.AmHost && PortKit.Alive(Player) && PortKit.AllTasksDone(Player)) GameEnd.Win(this);
    }

    // ホスト: キルを防ぐ
    [HostOnly]
    private void OnKill(BeforeMurderEvent e)
    {
        if (e.Canceled || !e.Target || e.Target.PlayerId != PlayerId || !e.Killer || e.Killer == e.Target) return;
        if (!Reached(KillGuardTask) || _killGuards >= MaxKillGuard) return;
        _killGuards++;
        e.Cancel("safecracker guard");
        PortKit.HostBlocked(e.Killer, e.Target);
        PopupCall.Send((PlayerId, 0));
        Popup(PlayerId, 0);
    }

    // ホスト: 追放されても生き返る
    [HostOnly]
    private void OnExiled(PlayerExiledEvent e)
    {
        if (!e.Player || e.Player.PlayerId != PlayerId) return;
        if (!Reached(ExiledGuardTask) || _exileGuards >= MaxExiledGuard) return;
        _exileGuards++;
        var me = Player;
        PortKit.After(0.5f, () =>
        {
            if (!me || me.Data == null || !me.Data.IsDead) return;
            PortKit.HostRevive(me);
            PopupCall.Send((PlayerId, 1));
            Popup(PlayerId, 1);
        });
    }

    private static readonly RemoteCall<(byte id, byte kind)> PopupCall = new("Safecracker.Popup", Route.HostToAll,
        (w, m) => { w.Write(m.id); w.Write(m.kind); }, r => (r.ReadByte(), r.ReadByte()), (_, m) => Popup(m.id, m.kind));

    private static void Popup(byte id, byte kind)
    {
        var p = PortKit.ById(id);
        if (p && p.AmOwner)
            StatusEffects.ShowFor("Safecracker", new Text(kind == 0 ? "Safecracker.KillGuarded" : "Safecracker.ExileGuarded"), "#F8D958", 4f);
    }
}
