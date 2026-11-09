// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Crewmate/DJ.cs (role idea and logic; UchuAddon allows porting them, no assets used)
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// DJ (クルー): 「スクラッチ」で、範囲の中の全員 (自分も) にスクラッチの音を聞かせ、画面を色とりどりに何度も光らせる
public sealed class DJ : RoleBase
{
    public override Team BaseTeam => Team.Crew;
    public override string Color => "#AD03FC";
    public override Text Name => new("DJ.Name");
    public override string Reading => "でぃーじぇー";
    public override Text Blurb => new("DJ.Blurb");
    public override string Origin => "Uchu";
    public override bool CanBeMadmate => true;
    public override HelpSection[] HelpSections => new HelpSection[] { new("DJ.Scratch", "dj_scratch") };

    public static readonly FloatOpt ScratchCooldown = new("DJ.ScratchCooldown", 20f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt Range = new("DJ.Range", 7f, 1f, 20f, 0.5f, "");
    public static readonly IntOpt NumOfFlash = new("DJ.NumOfFlash", 10, 1, 30);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("DJ.Scratch"), "dj_scratch")
        {
            Cooldown = () => ScratchCooldown,
            OnUse = () =>
            {
                Vector2 at = Player.GetTruePosition();
                ScratchCall.Send((at.x, at.y));
                OnScratch(at);
                ShowRange(at);
                return true;
            },
        });
    }

    private static readonly RemoteCall<(float x, float y)> ScratchCall = new("DJ.Scratch", Route.Anyone,
        (w, m) => { w.Write(m.x); w.Write(m.y); }, r => (r.ReadSingle(), r.ReadSingle()),
        (sender, m) => { if (RoleState.ActingOf(sender) is DJ) OnScratch(new Vector2(m.x, m.y)); }, perSecond: 2);

    // 全員の端末で: 範囲の中にいれば音と光
    private static void OnScratch(Vector2 at)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || MeetingHud.Instance) return;
        if (Vector2.Distance(lp.GetTruePosition(), at) > Range) return;
        PortSound.PlayVanilla("notice", 0.9f);
        int n = NumOfFlash;
        for (int i = 0; i < n; i++)
            PortKit.After(0.2f * i, () =>
            {
                if (MeetingHud.Instance) return;
                var c = UnityEngine.Color.HSVToRGB(Random.value, 1f, 1f);
                c.a = 0.55f;
                PortKit.Flash(c, 0.2f);
            });
    }

    // 自分にだけ、届く範囲の円を一瞬見せる
    private void ShowRange(Vector2 at)
    {
        var sr = PortKit.Marker(Lifespan, at, "range_ring", Range * 2f, new UnityEngine.Color(0.68f, 0.01f, 0.99f, 0.5f));
        float end = Time.time + 0.8f;
        UchuKit.Every(() =>
        {
            if (!sr) return false;
            float left = end - Time.time;
            if (left <= 0f) { Object.Destroy(sr.gameObject); return false; }
            var c = sr.color; c.a = 0.5f * left / 0.8f; sr.color = c;
            return true;
        });
    }
}
