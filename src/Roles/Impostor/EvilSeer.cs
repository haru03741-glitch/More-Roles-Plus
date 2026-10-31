// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/EvilSeer.cs, Roles/Ability/SeerAbility.cs (GPL-3.0)
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// イビルシーア (インポスター): 誰かが死ぬと画面が光り、死んだ場所に魂が見える (追放では出ない)。死体への矢印も見える。
// 設定で、光・魂・矢印に死体の明暗や色を出せる。取り憑く設定なら、自分をキルした人に狼憑きが付く。
// 取り憑かせる設定なら、1 回だけ近くの人に狼憑きを付けられる
public sealed class EvilSeer : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("EvilSeer.Name");
    public override string Reading => "いびるしーあ";
    public override Text Blurb => new("EvilSeer.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("EvilSeer.Haunt", "snr_curse") };

    public static readonly ChoiceOpt ColorMode = new("EvilSeer.ColorMode", 0,
        new Text("EvilSeer.ColorMode.None"), new Text("EvilSeer.ColorMode.LightDark"), new Text("EvilSeer.ColorMode.Adaptive"));
    public static readonly ChoiceOpt ColorTarget = new("EvilSeer.ColorTarget", 2,
        new Text("EvilSeer.ColorTarget.All"), new Text("EvilSeer.ColorTarget.Flash"), new Text("EvilSeer.ColorTarget.Soul"), new Text("EvilSeer.ColorTarget.Arrow"))
        { When = () => ColorMode.Value != 0 };
    public static readonly ChoiceOpt Mode = new("EvilSeer.Mode", 0,
        new Text("EvilSeer.Mode.Both"), new Text("EvilSeer.Mode.Flash"), new Text("EvilSeer.Mode.Soul"));
    public static readonly BoolOpt LimitSoul = new("EvilSeer.LimitSoul", false) { When = () => Mode.Value != 1 };
    public static readonly FloatOpt SoulDuration = new("EvilSeer.SoulDuration", 30f, 2.5f, 60f, 2.5f, "s") { When = () => Mode.Value != 1 && LimitSoul };
    public static readonly BoolOpt ShowArrows = new("EvilSeer.ShowArrows", true);
    public static readonly ChoiceOpt HauntMode = new("EvilSeer.HauntMode", 0,
        new Text("EvilSeer.HauntMode.None"), new Text("EvilSeer.HauntMode.Both"), new Text("EvilSeer.HauntMode.Others"), new Text("EvilSeer.HauntMode.Self"));
    public static readonly FloatOpt HauntCooldown = new("EvilSeer.HauntCooldown", 30f, 2.5f, 60f, 2.5f, "s") { When = () => HauntMode.Value is 1 or 2 };

    private BodyArrows _arrows;

    // 色を出す先 (1 = 光・2 = 魂・3 = 矢印) で使う色の出し方
    private static int ModeFor(int target) =>
        ColorMode.Value != 0 && (ColorTarget.Value == 0 || ColorTarget.Value == target) ? ColorMode.Value : 0;

    public override void OnAssigned()
    {
        if (HauntMode.Value is 1 or 2)
            AddAbility(new Ability(new Text("EvilSeer.Haunt"), "snr_curse")
            {
                Target = AbilityTarget.Player,
                Cooldown = () => HauntCooldown,
                MaxUses = () => 1,
                CanTarget = (_, t) => !HasWolf(t),
                OnUsed = (_, target) => { if (AmongUsClient.Instance.AmHost) GiveWolf(target); },
            });
    }

    private static bool HasWolf(PlayerControl p)
    {
        foreach (var a in RoleState.AddonsOf(p.PlayerId)) if (a is Addons.ModifierHauntedWolf) return true;
        return false;
    }

    private static void GiveWolf(PlayerControl p)
    {
        if (!p || HasWolf(p)) return;
        var proto = RoleState.AddonProto<Addons.ModifierHauntedWolf>();
        if (proto != null) RoleState.AddAddon(p, proto);
    }

    [LocalOnly]
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (!PortKit.Alive(Player) || !e.Target) return;
        if (Mode.Value != 2)
        {
            int m = ModeFor(1);
            PortKit.Flash(SeerKit.BodyColor(e.Target, m) is Color c ? new Color(c.r, c.g, c.b, 0.6f) : Crew.Seer.FlashColor);
            string text = SeerKit.ColorText(e.Target, m);
            if (text != null) StatusEffects.ShowFor("EvilSeerColor", Text.Raw(text), "#FF1919", 3f);
        }
        if (Mode.Value != 1) SeerKit.Soul(Lifespan, e.Target.transform.position, SeerKit.BodyColor(e.Target, ModeFor(2)), LimitSoul, SoulDuration);
    }

    // 取り憑く: 自分をキルした人に狼憑きを付ける (ホスト)
    [HostOnly]
    private void OnKilled(PlayerMurderedEvent e)
    {
        if (HauntMode.Value is not (1 or 3) || !e.Target || e.Target.PlayerId != PlayerId || !e.Killer || e.Killer == e.Target) return;
        var killer = e.Killer;
        PortKit.After(1f, () => GiveWolf(killer));
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        _arrows ??= new BodyArrows(Lifespan, new Color32(255, 25, 25, 255));
        _arrows.Mode = ModeFor(3);
        _arrows.Update(ShowArrows && PortKit.Alive(Player));
    }
}
