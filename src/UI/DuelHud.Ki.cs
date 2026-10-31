using MoreRolesPlus.Fx;
using MoreRolesPlus.Roles;
using MoreRolesPlus.Roles.Neutral;
using UnityEngine;

namespace MoreRolesPlus.UI;

// 立ち合いの気のバー (体力のバーのすぐ下・左右とも)。サムライでない側には出さない。KiSteps 個の区切り付き。
// 減った瞬間は白く光り、一振りに足りない間は橙、尽きている間は地が赤く点滅する。
// 書き直すのは見せる値が 1 変わった時と色が変わる時だけ (溜まっている間でも 1 秒に数十回まで)
internal static partial class DuelHud
{
    private const int KiSteps = 5;
    private const float KiH = 0.07f, KiBelow = 0.24f, KiFlash = 0.14f, KiBlink = 9f;

    private static readonly GameObject[] _kiRoot = new GameObject[2];
    private static readonly SpriteRenderer[] _kiBack = new SpriteRenderer[2], _kiFill = new SpriteRenderer[2];
    private static readonly Samurai[] _kiOf = new Samurai[2];
    private static readonly int[] _kiShown = { -1, -1 }, _kiTone = { -1, -1 };
    private static readonly float[] _kiFlashT = { -10f, -10f };

    private static void BuildKi(Sprite white, int layer, float y)
    {
        float ky = y - KiBelow;
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f, outer = side * (BarGap + _barW), mid = side * (BarGap + _barW * 0.5f);
            var root = new GameObject(i == 0 ? "KiL" : "KiR") { layer = layer };
            var tf = root.transform;
            tf.SetParent(_rootTf, false);
            _kiRoot[i] = root;
            _kiBack[i] = Quad("Back", white, mid, ky, 0f, _barW + 0.06f, KiH + 0.05f, KiBackColor, layer, tf);
            _kiFill[i] = Quad("Fill", white, mid, ky, -0.1f, _barW, KiH, KiColor(2), layer, tf);
            // 一区切り = 気 KiMax / KiSteps
            for (int j = 1; j < KiSteps; j++)
                Quad("Step", white, outer - side * _barW * j / KiSteps, ky, -0.2f, 0.03f, KiH, KiBackColor, layer, tf);
            root.SetActive(false);
        }
    }

    private static Color KiBackColor => FxMath.Rgba(0.04f, 0.05f, 0.09f, 0.88f);

    // 0 = 減った瞬間の白・1 = 一振りに足りない橙・2 = いつもの水色
    private static Color KiColor(int tone) =>
        tone == 0 ? FxMath.Rgba(1f, 1f, 1f, 1f) : tone == 1 ? FxMath.Rgba(1f, 0.6f, 0.16f, 1f) : FxMath.Rgba(0.42f, 0.88f, 1f, 1f);

    private static void KiShow()
    {
        if (_kiRoot[0] == null) return;
        _kiOf[0] = RoleState.Of(_me) as Samurai;
        _kiOf[1] = RoleState.Of(_opp) as Samurai;
        for (int i = 0; i < 2; i++)
        {
            _kiShown[i] = _kiTone[i] = -1;
            _kiFlashT[i] = -10f;
            _kiRoot[i].SetActive(_kiOf[i] != null);
        }
    }

    private static void KiTick(float now)
    {
        for (int i = 0; i < 2; i++)
        {
            var s = _kiOf[i];
            if (s == null) continue;
            int v = (int)s.ShownKi;
            if (v < _kiShown[i] - 2) _kiFlashT[i] = now;
            // 3 / 4 = 尽きて点滅の明 / 暗
            int tone = s.ShownSpent ? (FxMath.Sin(now * KiBlink) > 0f ? 3 : 4)
                : now - _kiFlashT[i] < KiFlash ? 0 : v < Samurai.StrokeCost ? 1 : 2;
            if (v == _kiShown[i] && tone == _kiTone[i]) continue;
            bool backChanged = (tone >= 3) != (_kiTone[i] >= 3) || tone >= 3;
            _kiShown[i] = v;
            _kiTone[i] = tone;
            float side = i == 0 ? -1f : 1f;
            Set(_kiFill[i], side * (BarGap + _barW), v / Samurai.KiMax, side, KiColor(tone >= 3 ? 1 : tone), KiH);
            if (backChanged) _kiBack[i].color = tone == 3 ? FxMath.Rgba(0.75f, 0.08f, 0.08f, 0.95f) : KiBackColor;
        }
    }

    internal static string DescribeKi() =>
        $"ki={_kiShown[0]}/{_kiShown[1]} tone={_kiTone[0]}/{_kiTone[1]} on={_kiOf[0] != null}/{_kiOf[1] != null}";
}
