using MoreRolesPlus.Fx;
using MoreRolesPlus.Roles;
using TMPro;
using UnityEngine;

namespace MoreRolesPlus.UI;

// 入りの幕で、二つ名を持つ側 (特別枠) の名札だけを格上げする: 名前の下に二つ名、名前の中央側に印、
// 縁と上下の線がオーロラの色を巡り、線の上で星が瞬く。相手が普通の役職なら相手の名札は今のまま
internal static partial class DuelHud
{
    private const int EpiStars = 6;
    private const float EpiNameUp = 0.13f, EpiLineY = -0.27f, EpiSize = 2.4f, EpiMarkH = 0.42f;
    private const float EpiCycle = 0.6f, EpiTwinkle = 2.5f;
    // 星の置き場 = 板の外側の端からの割合 (中央の側ほど多い)。偶数番は上の線・奇数番は下の線
    private static readonly float[] EpiStarAt = { 0.18f, 0.36f, 0.54f, 0.68f, 0.81f, 0.93f };

    private static readonly TextMeshPro[] _epiText = new TextMeshPro[2], _epiName = new TextMeshPro[2];
    private static readonly SpriteRenderer[] _epiMark = new SpriteRenderer[2], _epiEdge = new SpriteRenderer[2];
    private static readonly SpriteRenderer[][] _epiLines = new SpriteRenderer[2][], _epiStar = { new SpriteRenderer[EpiStars], new SpriteRenderer[EpiStars] };
    private static readonly bool[] _epiOn = new bool[2];
    private static float _epiPw;

    private static void BuildEpithet(int layer, TextMeshPro src, float pw)
    {
        _epiPw = pw;
        var mark = RoleArt.Mark(Epithet.Wayfinder);
        var spark = Aura.SparkSprite;
        for (int s = 0; s < 2; s++)
        {
            var plate = s == 0 ? _plateL : _plateR;
            float side = s == 0 ? -1f : 1f;
            _epiName[s] = s == 0 ? _plateNameL : _plateNameR;
            _epiEdge[s] = s == 0 ? _plateEdgeL : _plateEdgeR;
            _epiLines[s] = s == 0 ? _plateLinesL : _plateLinesR;

            var t = Text(src, "Epithet", 0f, 0f, EpiSize, side < 0 ? TextAlignmentOptions.Left : TextAlignmentOptions.Right, layer, 3.2f);
            t.transform.SetParent(plate, true);
            t.transform.localPosition = FxMath.V3(_epiName[s].transform.localPosition.x, EpiLineY, -0.1f);
            t.fontStyle = FontStyles.Bold;
            t.characterSpacing = 4f;
            t.enableVertexGradient = true;
            // 刀と同じ 青 → 桃 (中央の側が桃)
            Color blue = FxMath.Rgba(0.5f, 0.78f, 1f, 1f), pink = FxMath.Rgba(1f, 0.6f, 0.82f, 1f);
            t.colorGradient = side < 0 ? new VertexGradient(blue, pink, blue, pink) : new VertexGradient(pink, blue, pink, blue);
            Outline(t, 0.2f, FxMath.Rgba(0.05f, 0.04f, 0.1f, 1f));
            t.gameObject.SetActive(false);
            _epiText[s] = t;

            float h = mark ? mark.bounds.size.y : 1f;
            _epiMark[s] = Quad("Mark", mark, 0f, EpiNameUp, -0.12f, EpiMarkH / h, EpiMarkH / h, FxMath.Rgba(1f, 1f, 1f, 1f), layer, plate);
            _epiMark[s].gameObject.SetActive(false);

            for (int i = 0; i < EpiStars; i++)
            {
                float x = side * pw * 0.5f - side * pw * EpiStarAt[i];
                float y = (i % 2 == 0 ? 1f : -1f) * (PlateH * 0.5f - 0.02f);
                var st = Quad("Star", spark, x, y, -0.13f, 0.16f, 0.16f, FxMath.Rgba(1f, 1f, 1f, 0f), layer, plate);
                st.gameObject.SetActive(false);
                _epiStar[s][i] = st;
            }
        }
    }

    // 幕を出す時に 1 回: 二つ名を持つ側の名札に飾りを付ける (持たない側は名前を板の真ん中へ戻す)
    private static void PaintEpithet()
    {
        if (!_epiText[0]) return;
        for (int s = 0; s < 2; s++)
        {
            var role = RoleState.Of(s == 0 ? _me : _opp);
            bool on = role != null && role.Epithet != Epithet.None;
            _epiOn[s] = on;
            var name = _epiName[s];
            var np = name.transform.localPosition;
            name.transform.localPosition = FxMath.V3(np.x, on ? EpiNameUp : 0f, np.z);
            _epiText[s].gameObject.SetActive(on);
            _epiMark[s].gameObject.SetActive(on && _epiMark[s].sprite);
            for (int i = 0; i < EpiStars; i++) _epiStar[s][i].gameObject.SetActive(on);
            if (!on) continue;

            _epiText[s].text = Tr.Get("Epithet." + role.Epithet) + "【" + Tr.Get(role.Id + ".Epithet") + "】";
            // 印は名前の中央の側の端のすぐ外 (左の札 = 名前の右・右の札 = 名前の左)
            float side = s == 0 ? -1f : 1f;
            float w = FxMath.Min(name.GetPreferredValues(name.text).x, 3.2f);
            float start = np.x + side * 1.6f; // 字の箱の顔の側の端 (字はここから中央へ伸びる)
            float x = start - side * (w + 0.06f + EpiMarkH * 0.5f);
            _epiMark[s].transform.localPosition = FxMath.V3(x, EpiNameUp, -0.12f);
        }
    }

    // 幕が動いている間だけ (now = 幕の始まりからの秒)
    private static void EpithetTick(float t)
    {
        for (int s = 0; s < 2; s++)
        {
            if (!_epiOn[s]) continue;
            var c = Aura.Hue(t * EpiCycle + s * 0.5f, 1f);
            _epiEdge[s].color = c;
            _epiLines[s][0].color = _epiLines[s][1].color = c;
            for (int i = 0; i < EpiStars; i++)
            {
                float ph = FxMath.Repeat(t * EpiTwinkle + i * 0.17f, 1f);
                float k = FxMath.Sin(ph * 3.14159265f);
                k *= k;
                var sc = Aura.Hue(t * EpiCycle + i * 0.2f, k);
                var st = _epiStar[s][i];
                st.color = FxMath.Rgba(1f + (sc.r - 1f) * 0.25f, 1f + (sc.g - 1f) * 0.25f, 1f + (sc.b - 1f) * 0.25f, k);
                float z = 0.14f + 0.22f * k;
                st.transform.localScale = FxMath.V3(z, z, 1f);
            }
        }
    }
}
