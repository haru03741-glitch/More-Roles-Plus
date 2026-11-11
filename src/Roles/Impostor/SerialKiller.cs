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
    private float _fitAt = -1f; // 自分の端末: 時間切れの発作が始まった時刻 (-1 = 起きていない)

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

    // 自分の起こした火・水・崩れ・穴で死んだ人もキル 1 回に数える
    [LocalOnly]
    private void OnTerrainKilled(TerrainKilledEvent e)
    {
        if (!e.Culprit || e.Culprit.PlayerId != PlayerId || e.Target.PlayerId == PlayerId) return;
        _left = SuicideTime;
        Plugin.Logger.LogInfo($"[SerialKiller] terrain kill {e.Target.PlayerId} resets the timer");
    }

    [LocalOnly]
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (_left >= 0f || _fitAt >= 0f) _left = SuicideTime;
        EndFit(); // 発作の途中で会議になった = 本編どおり数え直して助かる
    }

    private HudReadout _readout;

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        // 自害までの秒は画面の下の札に出す (元は名前の横の字)
        _readout ??= new HudReadout(Lifespan, UI.RoleArt.Own(Id), new Color32(255, 25, 25, 255));
        bool show = _left >= 0f && PortKit.Alive(Player);
        _readout.Set(show, show ? $"<color=#FF5050>{Mathf.CeilToInt(_left)}s</color>" : null, show && SuicideTime > 0f ? _left / SuicideTime : -1f);
        if (_fitAt >= 0f)
        {
            // 発作の間は動けない (ChickenFx.LocalOmen を PortFreezePatch が読む)。崩れ落ちた所で死ぬ
            if (!PortKit.Alive(Player) || !ShipStatus.Instance) { EndFit(); return; }
            if (MeetingHud.Instance || ExileController.Instance || Time.time - _fitAt < FitTime) return;
            EndFit();
            PortKit.Suicide();
            return;
        }
        if (_left < 0f || !PortKit.Alive(Player) || MeetingHud.Instance || ExileController.Instance || !ShipStatus.Instance) return;
        _left -= Time.fixedDeltaTime;
        if (_left > 0f) return;
        _left = -1f;
        // 時間切れ: その場で体を折って苦しみ、崩れ落ちて死ぬ (全員に見える)
        _fitAt = Time.time;
        ChickenFx.LocalOmen = PlayerId;
        ChickenFx.LocalOmenAt = Time.time;
        Fit(Player);
        FitCall.Send(0);
    }

    // 発作の全身の絵: 本編の首を折られる被害者の連番 (立つ → 体を折って震える → 崩れ落ちる) を流す。
    // 倒れる流し方なので着せ替えは周りへ落ち、崩れた姿のまま死体になる (死の知らせが届くまで崩れた絵で待つ)
    private const float FitTime = 1.9f;      // 発作が始まってから死ぬまで (連番が崩れ落ちきる所)
    private const float FitLinger = 1.5f;    // 死ぬ時刻の後も崩れた絵で待つ長さ

    private static void Fit(PlayerControl p)
    {
        if (!PortKit.Alive(p)) return;
        Fx.CrewFlip.Play(p, "seizure", FitTime + FitLinger, fallen: true);
    }

    private void EndFit()
    {
        if (_fitAt < 0f) return;
        _fitAt = -1f;
        if (ChickenFx.LocalOmen == PlayerId) ChickenFx.LocalOmen = 255;
    }

    // 発作が始まったことをほかの人へ (受け手は送り主の体を動かすだけ)
    private static readonly Net.RemoteCall<byte> FitCall = new("SerialKiller.Fit", Net.Route.Anyone,
        (w, _) => w.Write((byte)0), r => r.ReadByte(),
        (sender, _) => { if (sender && !MeetingHud.Instance) Fit(sender); }, perSecond: 4);

    internal void FitForTest() { if (PortKit.Alive(Player) && _fitAt < 0f) _left = 0.01f; }
}
