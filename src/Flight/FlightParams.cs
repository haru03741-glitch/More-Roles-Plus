using System.Globalization;

namespace MoreRolesPlus.Flight;

// 3D の艦隊戦の調整の値。並びは C++ の flt::Param と同じ (足す時は両方の最後に足す)。
// 単位: 速さ = 単位/秒・角速度 = 度/秒・時間 = 秒・応答 = 1/秒 (大きいほど早く追いつく)
internal static class FlightParams
{
    internal static readonly (string Name, float Value)[] Table =
    {
        ("cruise", 42f), ("boost", 88f), ("brake", 18f), ("speedResp", 2.6f),
        ("pitchRate", 95f), ("yawRate", 80f), ("rollRate", 220f), ("turnResp", 9f), ("bankMax", 38f), ("bankResp", 6f),
        ("boostMax", 1f), ("boostDrain", 0.42f), ("boostRegen", 0.3f), ("boostDelay", 0.6f),
        ("boltSpeed", 260f), ("boltLife", 1.5f), ("boltInterval", 0.11f), ("boltLen", 12f), ("boltWidth", 0.7f), ("boltDamage", 4f),
        ("camDist", 62f), ("camHeight", 21f), ("camLag", 7.5f), ("camLook", 95f), ("camSteer", 9f), ("fov", 62f), ("fovBoost", 14f), ("fovResp", 5f),
        ("arena", 900f), ("arenaTurn", 2.5f),
        ("dodgeTime", 0.42f), ("dodgeSide", 9f), ("dodgeCool", 1.1f),
        ("shakeHit", 0.12f), ("shakeHurt", 0.9f), ("shakeDecay", 7f), ("interpDelay", 0.12f), ("extrapMax", 0.35f),
        ("uturnTime", 1.25f), ("uturnCool", 2.5f), ("converge", 120f),
        ("dustCell", 180f), ("dustStreak", 0.06f), ("dustAlpha", 0.8f),
        ("chargeTime", 1.1f), ("chargeDelay", 0.35f), ("chargeDamage", 48f), ("chargeScale", 4.5f), ("chargeRecover", 0.4f),
        ("grazeDist", 10f), ("tailDist", 200f), ("tailCone", 0.75f),
        ("flameLen", 30f), ("flameWidth", 2.9f), ("flameGlow", 1.25f), ("exhRate", 34f), ("exhSpeed", 55f), ("exhLife", 0.7f), ("igniteShake", 0.35f),
    };

    internal static readonly float[] Values = MakeValues();

    private static float[] MakeValues()
    {
        var v = new float[64];
        for (int i = 0; i < Table.Length; i++) v[i] = Table[i].Value;
        return v;
    }

    internal static int Find(string name)
    {
        for (int i = 0; i < Table.Length; i++)
            if (Table[i].Name == name) return i;
        return -1;
    }

    internal static bool Set(string name, string value)
    {
        int i = Find(name);
        if (i < 0 || !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return false;
        Values[i] = f;
        return true;
    }

    internal static string Dump()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Table.Length; i++) sb.Append(Table[i].Name).Append('=').Append(Values[i].ToString("0.###", CultureInfo.InvariantCulture)).Append(' ');
        return sb.ToString();
    }
}
