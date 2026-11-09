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

    private static readonly UnityEngine.Color Yellow = Fx.FxMath.Rgba(1f, 1f, 50f / 255f);

    private static UnityEngine.Color Now() => RainbowStar ? Rainbow(Time.time * RainbowSpeed / 40f) : Yellow;

    // 色相 h (1 で一周)・彩度 0.8・明るさ 1 の色。毎ティック呼ぶので Unity の Color.HSVToRGB を通さず手元で計算する
    private static UnityEngine.Color Rainbow(float h)
    {
        const float s = 0.8f;
        h = (h - System.MathF.Floor(h)) * 6f;
        int i = (int)h;
        float f = h - i, p = 1f - s, q = 1f - s * f, t = 1f - s * (1f - f);
        return (i % 6) switch
        {
            0 => Fx.FxMath.Rgba(1f, t, p),
            1 => Fx.FxMath.Rgba(q, 1f, p),
            2 => Fx.FxMath.Rgba(p, 1f, t),
            3 => Fx.FxMath.Rgba(p, q, 1f),
            4 => Fx.FxMath.Rgba(t, p, 1f),
            _ => Fx.FxMath.Rgba(1f, p, q),
        };
    }

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
