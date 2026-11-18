using System.Globalization;

namespace MoreRolesPlus.Flight;

// 3D の艦隊戦の調整の値。並びは C++ の flt::Param と同じ (足す時は両方の最後に足す)。
// 単位: 速さ = 単位/秒・角速度 = 度/秒・時間 = 秒・応答 = 1/秒 (大きいほど早く追いつく)
internal static class FlightParams
{
    internal static readonly (string Name, float Value)[] Table =
    {
        ("cruise", 42f), ("boost", 88f), ("brake", 18f), ("speedResp", 2.6f),
        ("pitchRate", 95f), ("yawRate", 80f), ("rollRate", 220f), ("turnResp", 9f), ("bankMax", 55f), ("bankResp", 7f),
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
        ("flameLen", 30f), ("flameWidth", 2.9f), ("flameGlow", 1.25f), ("exhRate", 34f), ("exhSpeed", 55f), ("exhLife", 0.7f), ("igniteShake", 0.35f), ("rcsRate", 45f), ("rcsSpeed", 22f),
        ("bankDamp", 1f), ("pitchLean", 9f), ("idleSway", 0.6f), ("camTurn", 6f), ("dodgeYaw", 14f), ("dodgeEase", 2.4f),
        ("joltHit", 2.2f), ("joltFreq", 13f), ("joltDamp", 0.9f), ("kickBoost", 1.6f), ("kickCharge", 2.8f), ("uturnCamBack", 0.35f), ("knock", 25f),
        ("timeScale", 1f), // 確かめ用: 時間の速さ (0.1 = 10 倍遅く)
        ("floorY", -1e6f), ("floorMargin", 70f), ("dustRed", 0f), ("enemyWarm", 0f), // 飛べる高さの下限 (場所が決める・-1e6 = 無し)・引き起こしを始める高さ・塵の色 (0 = 青白い宇宙・1 = 赤い街の明かり)・敵の弾を琥珀色に (赤い街の上で読めるように)
        ("crashSpeed", 26f), ("crashDamage", 22f), ("crashBounce", 0.9f), ("scrapeDamage", 2f), ("scrapeDrag", 2.5f), ("scrapeTurn", 70f), // ビル: 壁へ向かう速さがこれを超えると正面衝突・その削れ (最大)・跳ね返り・擦る削れ (毎秒)・擦る減速・機首が壁に沿う速さ (度/秒)
        ("groundY", -1e6f), // 弾が止まる地面の高さ (街の場面だけ・-1e6 = 無し)
        ("carTime", 1f), // 確かめ用: 夜の街の車の時計の速さ (シムは読まない)
    };

    internal static readonly float[] Values = MakeValues();

    private static float[] MakeValues()
    {
        var v = new float[96]; // flight.h の MaxParams と同じ長さ
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
