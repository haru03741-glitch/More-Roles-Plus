// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Impostor/SerialKiller.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to https://github.com/yukinogatari/TheOtherRoles-GM (GPL-3.0)
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// シリアルキラー (インポスター): キルの待ち時間が短い。最初のキルの後から自殺までの時間が数え始まり、
// キルするたび・会議の後に戻る (会議中は止まる)。0 になると死ぬ。設定で自分から死ぬボタンを持つ
public sealed class SerialKiller : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("SerialKiller.Name");
    public override string Reading => "しりあるきらー";
    public override Text Blurb => new("SerialKiller.Blurb");
    public override string Origin => "Uchu, TORGM";
    public override HelpSection[] HelpSections => SuicideButton
        ? new HelpSection[] { new("SerialKiller.Timer"), new("SerialKiller.Suicide", "serialkiller_suicide") }
        : new HelpSection[] { new("SerialKiller.Timer") };

    public static readonly FloatOpt KillCooldown = new("SerialKiller.KillCooldown", 20f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt SuicideTime = new("SerialKiller.SuicideTime", 60f, 5f, 180f, 5f, "s");
    public static readonly BoolOpt SuicideButton = new("SerialKiller.SuicideButton", false);

    private float _left = -1f; // 自分の端末: 自殺までの残り (-1 = まだ数えない)

    public override void ModifyKillCooldown(ref float seconds) => seconds = KillCooldown;

    public override void OnAssigned()
    {
        if (!SuicideButton) return;
        AddAbility(new Ability(new Text("SerialKiller.Suicide"), "serialkiller_suicide")
        {
            CooldownAtStart = false,
            OnUse = () => { PortKit.Suicide(); return true; },
        });
    }

    [LocalOnly]
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (e.Killer && e.Killer.PlayerId == PlayerId && e.Target.PlayerId != PlayerId) _left = SuicideTime;
    }

    [LocalOnly]
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (_left >= 0f) _left = SuicideTime;
    }

    private HudReadout _readout;

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        // 自害までの秒は画面の下の札に出す (元は名前の横の字)
        _readout ??= new HudReadout(Lifespan, UI.RoleArt.Own(Id), new Color32(255, 25, 25, 255));
        bool show = _left >= 0f && PortKit.Alive(Player);
        _readout.Set(show, show ? $"<color=#FF5050>{Mathf.CeilToInt(_left)}s</color>" : null, show && SuicideTime > 0f ? _left / SuicideTime : -1f);
        if (_left < 0f || !PortKit.Alive(Player) || MeetingHud.Instance || ExileController.Instance || !ShipStatus.Instance) return;
        _left -= Time.fixedDeltaTime;
        if (_left > 0f) return;
        _left = -1f;
        PortKit.Suicide();
    }
}
