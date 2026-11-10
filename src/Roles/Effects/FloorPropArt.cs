using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 役職が床に置く小物の絵 (立体で描いた絵・Resources/Icons/prop_<名前>.png と床の影 prop_<名前>_shadow.png)。
// 絵の原点は床の点 (物の真ん中の真下)。範囲は絵の寸法 (1 単位 = 200px) と同じ値
internal static class FloorPropArt
{
    private enum Stand
    {
        Upright, // 立っている物: 奥行きは人と同じ決め方 (床の点の y)
        Flat,    // 床に寝ている物: 奥の縁の y で決める (上に乗った人が物の下に潜らない)
        Air,     // 宙を飛ぶ・体に刺さる物: 原点は絵の真ん中・床の影なし
    }

    private const int Ppu = 200;
    private const float BodyLift = 0.3636f; // 人の奥行きは体の y/1000 で、体は足元よりこれだけ上

    private static (float l, float r, float b, float t, Stand s) Spec(string name) => name switch
    {
        "camera" => (-0.416f, 0.416f, -0.182f, 0.65f, Stand.Upright),
        "antenna" or "antenna_broken" => (-0.36f, 0.36f, -0.192f, 1.032f, Stand.Upright),
        "kunai" => (-0.29f, 0.29f, -0.07f, 0.07f, Stand.Air),
        "anchor" => (-0.437f, 0.437f, -0.23f, 0.414f, Stand.Flat),
        _ => (-0.5f, 0.5f, -0.5f, 0.5f, Stand.Air),
    };

    private static Sprite Load(string name, bool shadow)
    {
        var (l, r, b, t, s) = Spec(name);
        var pivot = s == Stand.Air ? FxMath.V2(0.5f, 0.5f) : FxMath.V2(-l / (r - l), -b / (t - b));
        return ButtonIcons.Load(shadow ? $"prop_{name}_shadow" : $"prop_{name}", Ppu, pivot);
    }

    // 床の点 pos に置く。life が終わると消え、置く前へ時を戻しても消える
    internal static SpriteRenderer Place(Lifespan life, Vector2 pos, string name, Color? tint = null)
    {
        var (_, _, _, top, stand) = Spec(name);
        float depth = stand switch
        {
            Stand.Upright => pos.y + BodyLift,
            Stand.Flat => pos.y + top + BodyLift,
            _ => pos.y - 1f, // 体に刺さった物は体と死体の手前に出す
        };
        var go = FxRoot.New("MrpFloorProp"); // 演出の根の下なので逆再生に入る
        go.transform.position = FxMath.V3(pos.x, pos.y, depth / 1000f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = Load(name, false);
        if (tint is Color c) sr.color = c;
        if (stand != Stand.Air) AddShadow(go, name, tint);
        life.Bind(go);
        Rewind.Undo(_ => { if (go) Object.Destroy(go); }); // 置く前へ戻ると消える
        return sr;
    }

    // 同じ場所のまま別の絵へ (アンテナが焼き切れた時など)。影も替える
    internal static void Swap(SpriteRenderer sr, string name)
    {
        if (!sr) return;
        sr.sprite = Load(name, false);
        var sh = sr.transform.Find("Shadow");
        if (sh) sh.GetComponent<SpriteRenderer>().sprite = Load(name, true);
    }

    // 左右の向きを替える (床の影も同じ向きに)
    internal static void FlipX(SpriteRenderer sr, bool flip)
    {
        if (!sr) return;
        sr.flipX = flip;
        var sh = sr.transform.Find("Shadow");
        if (sh) sh.GetComponent<SpriteRenderer>().flipX = flip;
    }

    private static void AddShadow(GameObject root, string name, Color? tint)
    {
        var sp = Load(name, true);
        if (!sp) return;
        var sh = FxRoot.New("Shadow", 0, root.transform);
        sh.transform.localPosition = FxMath.V3(0f, 0f, 0.002f); // 床の影なので、すぐ奥にいる人より奥に敷く (影が人の体に重ならない)
        var sr = sh.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        if (tint is Color c) sr.color = c;
    }
}
