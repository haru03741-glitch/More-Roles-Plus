namespace MoreRolesPlus.Roles;

// 場に出る物の素材。切り口の絵・音・水に入った時の振る舞いもこれで決まる
public enum BodyMaterial : byte
{
    Flesh = 0,      // 肉 (人・死体・キノコ)
    Electronic = 1, // 電子 (ドローン)
    Metal = 2,      // 金属 (弾・ロケット・装置・ランタン)
    Wood = 3,       // 木 (設計台)
    Stone = 4,      // 石・壁
    Energy = 5,     // エネルギー (光線・ポータル・燃える隕石)
}

// 一撃の属性
public enum StrikeElement : byte
{
    Slash = 0,  // 斬 (刀)
    Blunt = 1,  // 打 (ハンマー・押し出し)
    Bullet = 2, // 弾 (銃)
    Blast = 3,  // 爆 (爆発・隕石・ロケット)
    Heat = 4,   // 熱 (火・光線)
    Water = 5,  // 水 (水位・放水)
}

// 物の大きさ。斬るのに要る切断力が 1 段ずつ変わる (小さい弾なら普通の振りで斬れる)
public enum BodySize : sbyte { Small = -1, Medium = 0, Large = 1 }

internal enum StrikeResult : byte
{
    None,   // 効き目が無く、反応も要らない (エネルギーに水など)
    Pass,   // 素通り (光線を叩く・撃つ)
    Glance, // 効かない。弾かれた絵と音を出す (石を斬る・金属を弱い振りで斬る)
    Damage, // 耐久が減った (まだ壊れていない)
    Break,  // 耐久が尽きて壊れた
    Cut,    // 真っ二つに斬れた
    Douse,  // 水に浸かって止まった
}

// 素材 × 属性 の表だけで一撃の結果を決める。物ごとには素材・大きさ・耐久しか持たない
internal static class BodyRules
{
    private const int Elements = 6;

    // 斬るのに要る切断力の基準 (0 = どれだけ強く振っても斬れない)
    private static readonly byte[] CutNeed =
    {
        1, // 肉
        1, // 電子
        2, // 金属
        1, // 木
        0, // 石
        1, // エネルギー (割れる)
    };

    // 耐久の減り方の倍率 [素材 * 6 + 属性]。斬の列は使わない (斬るかどうかは CutNeed)。
    // 0 = 効かない (反応も要らない)・-1 = 素通り・-2 = 水で止まる
    private static readonly float[] Mult =
    {
        //  斬    打     弾     爆    熱     水
            0f,   1f,    1f,    2f,   1f,    0f,  // 肉
            0f,   1f,    1f,    2f,   1f,   -2f,  // 電子
            0f,   1f,  0.5f,    1f, 0.5f,    0f,  // 金属
            0f,   1f,    1f,    2f,   2f,    0f,  // 木
            0f,   1f,  0.3f,    2f, 0.5f,    0f,  // 石
            0f,  -1f,   -1f,   -1f,   0f,    0f,  // エネルギー
    };

    // 斬るのに要る切断力 (0 = 斬れない)
    internal static int CutNeedOf(BodyMaterial m, BodySize s)
    {
        int b = CutNeed[(int)m];
        if (b == 0) return 0;
        int n = b + (int)s;
        return n < 1 ? 1 : n;
    }

    internal static bool CanCut(int edge, BodyMaterial m, BodySize s)
    {
        int need = CutNeedOf(m, s);
        return need > 0 && edge >= need;
    }

    internal static float MultOf(BodyMaterial m, StrikeElement e) => Mult[(int)m * Elements + (int)e];

    // 一撃を当てた結果。hp は耐久を持つ物の残り (減らした後の値に書き換える)
    internal static StrikeResult Judge(StrikeElement e, float power, int edge, BodyMaterial m, BodySize s, ref float hp)
    {
        if (e == StrikeElement.Slash)
            return CanCut(edge, m, s) ? StrikeResult.Cut : StrikeResult.Glance;
        float k = MultOf(m, e);
        if (k == -1f) return StrikeResult.Pass;
        if (k == -2f) return StrikeResult.Douse;
        if (k <= 0f || power <= 0f) return StrikeResult.None;
        hp -= power * k;
        return hp <= 0f ? StrikeResult.Break : StrikeResult.Damage;
    }
}
