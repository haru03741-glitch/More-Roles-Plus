// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Neutral/Amnesia.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// Role idea: SuperNewRoles https://github.com/ykundesu/SuperNewRoles (Amnesiac)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// アムネシア (第三陣営): 死体を通報すると、その死体の役職になる (会議の始め・終わりのどちらで変わるかは設定)。
// 設定で死体への矢印が出る。ガーディアンを写すとタスクが 1 つ増える
public sealed class Amnesia : RoleBase
{
    public override Team BaseTeam => Team.Neutral;
    public override string Color => "#4F6E73";
    public override Text Name => new("Amnesia.Name");
    public override string Reading => "あむねしあ";
    public override Text Blurb => new("Amnesia.Blurb");
    public override string Origin => "Uchu, SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Amnesia.Remember") };

    public static readonly ChoiceOpt Timing = new("Amnesia.Timing", 0, new Text("Amnesia.Timing.Start"), new Text("Amnesia.Timing.End"));
    public static readonly BoolOpt ShowArrows = new("Amnesia.DeadBodyArrow", true);
    public static readonly BoolOpt Vent = new("Amnesia.CanVent", true);

    public override bool? CanVent => Vent;

    private BodyArrows _arrows;
    private byte _reported = 255; // ホスト: 通報した死体

    public override void OnFixedUpdate()
    {
        if (IsLocal) (_arrows ??= new BodyArrows(Lifespan, new Color32(79, 110, 115, 255))).Update(ShowArrows && PortKit.Alive(Player));
    }

    [HostOnly]
    private void OnMeetingCalled(MeetingCalledEvent e)
    {
        if (e.IsEmergency || !e.Reporter || e.Reporter.PlayerId != PlayerId) return;
        _reported = e.Body.PlayerId;
    }

    [HostOnly]
    private void OnMeetingStart(MeetingStartEvent e)
    {
        if (Timing.Value == 0) Copy();
    }

    [HostOnly]
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (Timing.Value == 1 && PortKit.Alive(Player)) Copy();
    }

    private void Copy()
    {
        if (_reported == 255) return;
        var dead = RoleState.Of(_reported);
        _reported = 255;
        if (dead == null || dead is Amnesia) return;
        var proto = Registry.Roles.Find(r => r.Id == dead.Id);
        if (proto == null) return;
        bool extra = dead is Crew.Guardian; // ガーディアンを写すとタスク +1
        var me = Player;
        RoleState.Change(me, proto);
        if (extra) ExtraTaskCall.Send(me.PlayerId);
    }

    // 写した人の端末で: タスクを 1 つ足す
    private static readonly RemoteCall<byte> ExtraTaskCall = new("Amnesia.ExtraTask", Route.HostToAll,
        (w, m) => w.Write(m), r => r.ReadByte(), (_, id) =>
        {
            var lp = PlayerControl.LocalPlayer;
            if (!lp || lp.PlayerId != id || lp.Data == null || lp.Data.Tasks == null) return;
            var o = GameOptionsManager.Instance.CurrentGameOptions;
            SnrKit.AssignOwnTasks(o.GetInt(AmongUs.GameOptions.Int32OptionNames.NumCommonTasks),
                o.GetInt(AmongUs.GameOptions.Int32OptionNames.NumShortTasks) + 1, o.GetInt(AmongUs.GameOptions.Int32OptionNames.NumLongTasks));
        });
}
