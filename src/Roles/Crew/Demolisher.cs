using MoreRolesPlus.Options;
using MoreRolesPlus.Terrain;

namespace MoreRolesPlus.Roles.Crew;

// 見本の役職 (クルー・能力ボタン): ハンマーで向いている方向の壁を叩いて壊す
public sealed class Demolisher : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#C9873A";
    public override Text Name => new("解体屋", "Demolisher");
    public override string Reading => "かいたいや";
    public override Text Blurb => new("ハンマーで壁を壊して道を作れ", "Smash walls to open new paths");

    public static readonly FloatOpt SmashCooldown = new("ハンマーの待ち時間", "Hammer Cooldown", 20f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt SmashUses = new("ハンマーの回数 (0 = 何回でも)", "Hammer Uses (0 = unlimited)", 5, 0, 20);

    private const float Force = 0.5f;

    // ホストが客の叩く依頼を確かめる時も、ボタンと同じ待ち時間で見る
    public override TerrainPermit TerrainPermit => TerrainPermit.None.WithHammer(cooldown: SmashCooldown);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("ハンマー", "Hammer"), "hammer")
        {
            Cooldown = () => SmashCooldown,
            MaxUses = () => SmashUses,
            OnUse = Smash,
        });
    }

    private static bool Smash()
    {
        var res = TerrainApi.Hammer(Force);
        if (!res.Ok) Plugin.Logger.LogInfo($"Demolisher: {res.Why}");
        return res.Ok;
    }
}
