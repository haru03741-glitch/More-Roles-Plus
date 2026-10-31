// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/EvilScientist.cs (GPL-3.0)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// イビルサイエンティスト (インポスター): 「透明化」でしばらく姿が見えなくなる (能力の対象にはなる)
public sealed class EvilScientist : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("EvilScientist.Name");
    public override string Reading => "いびるさいえんてぃすと";
    public override Text Blurb => new("EvilScientist.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("EvilScientist.Hide", "snr_invisible") };
    public static readonly FloatOpt HideCooldown = new("EvilScientist.Cooldown", 30f, 2.5f, 120f, 2.5f, "s");
    public static readonly FloatOpt HideDuration = new("EvilScientist.Duration", 10f, 2.5f, 60f, 2.5f, "s");

    private bool _hidden;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("EvilScientist.Hide"), "snr_invisible")
        {
            Cooldown = () => HideCooldown,
            Duration = () => HideDuration,
            OnUse = () => { Set(true); return true; },
            OnEnd = () => Set(false),
        });
        Lifespan.OnRelease(() => { if (_hidden) Show(Player, false); });
    }

    private void Set(bool on)
    {
        _hidden = on;
        HideCall.Send(on);
        Show(Player, on);
    }

    private static readonly RemoteCall<bool> HideCall = new("EvilScientist.Hide", Route.Anyone,
        (w, on) => w.Write(on), r => r.ReadBoolean(),
        (sender, on) => { if (RoleState.Of(sender) is EvilScientist s) { s._hidden = on; Show(sender, on); } });

    private void OnMeetingStart(MeetingStartEvent e)
    {
        if (_hidden) { _hidden = false; Show(Player, false); }
    }

    // 全員の端末で: 透明にする / 戻す。本人と幽霊には薄く見える
    private static void Show(PlayerControl p, bool hidden)
    {
        if (!p || !p.cosmetics) return;
        var lp = PlayerControl.LocalPlayer;
        bool faint = p.AmOwner || (lp && lp.Data != null && lp.Data.IsDead);
        float a = !hidden ? 1f : faint ? 0.35f : 0f;
        foreach (var r in p.cosmetics.GetComponentsInChildren<SpriteRenderer>(true))
        {
            var c = r.color;
            r.color = new Color(c.r, c.g, c.b, a);
        }
        foreach (var t in p.cosmetics.GetComponentsInChildren<TMPro.TextMeshPro>(true)) t.alpha = a;
    }
}
