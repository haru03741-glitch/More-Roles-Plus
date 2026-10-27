using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Impostor;

// 見本の役職 (インポスター): キルの待ち時間が短い
public sealed class Quickdraw : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#E04040";
    public override Text Name => new("Quickdraw.Name");
    public override string Reading => "はやうち";
    public override Text Blurb => new("Quickdraw.Blurb");

    // キルのクールダウンの決め方: 0 = 固定の秒数・1 = 部屋の値に足す秒数・2 = 部屋の値に掛ける倍率。
    // 部屋の値 (本編のキルのクールダウン) を基にすると、部屋の値を変えた時に早撃ちの秒数も付いてくる
    public static readonly ChoiceOpt KillCooldownMode = new("Quickdraw.KillCooldownMode", 0,
        new Text("CooldownMode.Fixed"), new Text("CooldownMode.Offset"), new Text("CooldownMode.Ratio"));

    public static readonly FloatOpt KillCooldown = new("Quickdraw.KillCooldown", 15f, 2.5f, 60f, 2.5f, "s")
        { When = () => KillCooldownMode.Value == 0 };

    public static readonly FloatOpt KillCooldownOffset = new("Quickdraw.KillCooldownOffset", -10f, -30f, 30f, 2.5f, "s")
        { When = () => KillCooldownMode.Value == 1, Format = v => RoomRelative.Offset(v, RoomRelative.KillCooldown()) };

    public static readonly FloatOpt KillCooldownRatio = new("Quickdraw.KillCooldownRatio", 0.5f, 0.1f, 2f, 0.05f)
        { When = () => KillCooldownMode.Value == 2, Format = v => RoomRelative.Ratio(v, RoomRelative.KillCooldown()) };

    public override void ModifyKillCooldown(ref float seconds) =>
        seconds = KillCooldownMode.Value switch
        {
            1 => RoomRelative.Apply(seconds + KillCooldownOffset),
            2 => RoomRelative.Apply(seconds * KillCooldownRatio),
            _ => KillCooldown,
        };
}
