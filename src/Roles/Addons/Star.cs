// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Modifier/Star.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to SuperNewRoles https://github.com/ykundesu/SuperNewRoles
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Addons;

// スター (アドオン): 名前が全員に黄色で見える (設定で虹色)
public sealed class Star : AddonBase
{
    public override string Color => "#FFFF32";
    public override Text Name => new("Star.Name");
    public override string Reading => "すたー";
    public override Text Blurb => new("Star.Blurb");
    public override string Origin => "Uchu, SNR";

    public static readonly BoolOpt RainbowStar = new("Star.RainbowStar", false);
    public static readonly IntOpt RainbowSpeed = new("Star.RainbowSpeed", 8, 5, 15) { When = () => RainbowStar };

    private static UnityEngine.Color Now() =>
        RainbowStar ? UnityEngine.Color.HSVToRGB(Time.time * RainbowSpeed / 40f % 1f, 0.8f, 1f) : new Color32(255, 255, 50, 255);

    public override void OnFixedUpdate()
    {
        if (!Player || !Player.cosmetics || !Player.cosmetics.nameText) return;
        var c = Now();
        Player.cosmetics.nameText.color = c;
        if (MeetingHud.Instance)
            foreach (var pva in MeetingHud.Instance.playerStates)
                if (pva && pva.PlayerId.Value == PlayerId && pva.NameText) pva.NameText.color = c;
    }
}
