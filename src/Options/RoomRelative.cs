// Based on https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Configuration/CoolDownConfiguration.cs (GPL-3.0)
using System;
using System.Globalization;

namespace MoreRolesPlus.Options;

// 部屋の値 (本編の設定) を基にした秒数の項目の書き方。値の後ろに、今の部屋の値で計算した実際の秒数を灰色で添える
internal static class RoomRelative
{
    private const string Gray = "#9A9AA0";
    private const float Min = 0.5f;

    // 本編のキルのクールダウン (読めない時は本編の既定)
    internal static float KillCooldown()
    {
        var m = GameOptionsManager.Instance;
        var o = m != null ? m.CurrentGameOptions : null;
        return o != null ? o.GetFloat(AmongUs.GameOptions.FloatOptionNames.KillCooldown) : 25f;
    }

    // 計算した秒数の下限 (0 秒にするとキルの間が無くなる)
    internal static float Apply(float seconds) => Math.Max(seconds, Min);

    internal static string Offset(float v, float room) =>
        $"{(v > 0f ? "+" : "")}{v.ToString("0.##", CultureInfo.InvariantCulture)}s {Actual(room + v)}";

    internal static string Ratio(float v, float room) =>
        $"×{v.ToString("0.##", CultureInfo.InvariantCulture)} {Actual(room * v)}";

    private static string Actual(float seconds) =>
        $"<color={Gray}>({Apply(seconds).ToString("0.##", CultureInfo.InvariantCulture)}s)</color>";
}
