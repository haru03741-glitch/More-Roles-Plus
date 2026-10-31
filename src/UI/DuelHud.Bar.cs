using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.UI;

// 体力のバーの格闘ゲーム風の見た目 (MRP/UiBar のシェーダ): 斜めに切った黒漆の枠に金の縁・朱と金の中身・中央の ∞ の後ろの鍔・顔の後ろの額縁。
// シェーダはバンドルの読み込みの後に届くので、届くまでは四角の板 (枠・地・影・体力) で出し、届いたら板 1 枚に置き換える。
// 色 (= 体力と影の値) を書くのは値が変わった時だけ。脈打ちと流れる光はシェーダの中の時刻で動く
internal static partial class DuelHud
{
    private const float HpPlateH = 0.34f, SubPlateH = 0.17f, Slant = 0.45f, EmblemSize = 0.8f, PortraitW = 0.95f, PortraitH = 0.7f;

    private static readonly SpriteRenderer[] _plate = new SpriteRenderer[2], _subPlate = new SpriteRenderer[2], _portrait = new SpriteRenderer[2];
    private static readonly int[] _plateKey = { -1, -1 }, _subPlateKey = { -1, -1 };
    private static SpriteRenderer _emblem;
    private static readonly List<SpriteRenderer> _flat = new();
    private static bool _barMat;

    // 大きなバー i の板と顔の額縁 (Build から)。板の横幅は斜めの分だけ広く、どの高さでも中身の長さが _barW になる
    private static void BuildPlate(int i, Sprite white, int layer, float y, float mid, float outer, float side)
    {
        _plate[i] = Quad("Plate", white, mid, y, -0.3f, (_barW + Slant * HpPlateH) * -side, HpPlateH, FxMath.Rgba(1f, 1f, 0f, 1f), layer);
        _portrait[i] = Quad("Portrait", white, outer + side * 0.24f, y, -0.305f, PortraitW * -side, PortraitH, FxMath.Rgba(0f, 0f, 1f, 1f), layer);
        _plate[i].enabled = _portrait[i].enabled = false;
        _plateKey[i] = -1;
    }

    private static void BuildEmblem(Sprite white, int layer, float y)
    {
        _emblem = Quad("Emblem", white, 0f, y, 0.12f, EmblemSize, EmblemSize, FxMath.Rgba(0f, 0f, 0.5f, 1f), layer);
        _emblem.enabled = false;
        _barMat = false;
    }

    private static void BuildSubPlate(int j, Sprite white, int layer, float sy, float mid, float w, float side, Transform parent)
    {
        _subPlate[j] = Quad("Plate", white, mid, sy, -0.3f, (w + Slant * SubPlateH) * -side, SubPlateH, FxMath.Rgba(1f, 1f, 0f, 1f), layer, parent);
        _subPlate[j].enabled = false;
        _subPlateKey[j] = -1;
    }

    // シェーダが届いたら板 1 枚に置き換える (毎フレーム呼ぶ・届くまでは managed のフラグを見るだけ)
    private static void BarTick()
    {
        if (_barMat || !MrpBundle.UiBarMaterial || !_emblem) return;
        _barMat = true;
        var mat = MrpBundle.UiBarMaterial;
        foreach (var sr in _flat)
            if (sr) sr.enabled = false;
        for (int i = 0; i < 2; i++)
        {
            _plate[i].sharedMaterial = mat;
            _plate[i].enabled = true;
            _portrait[i].sharedMaterial = mat;
            _portrait[i].enabled = true;
            _subPlate[i].sharedMaterial = mat;
            _subPlate[i].enabled = true;
            _plateKey[i] = _subPlateKey[i] = -1;
        }
        _emblem.sharedMaterial = mat;
        _emblem.enabled = true;
        WritePlates();
    }

    // 体力と影を板の色へ (Write から・値が変わった時だけ書く)
    private static void WritePlates()
    {
        if (!_barMat) return;
        PlateColor(_plate[0], ref _plateKey[0], _leftHp, _leftGhostHp);
        PlateColor(_plate[1], ref _plateKey[1], _rightHp, _rightGhostHp);
        for (int j = 0; j < 2; j++)
            if (_slot[SlotL1 + j] != 255) PlateColor(_subPlate[j], ref _subPlateKey[j], _subHp[j], _subGhostHp[j]);
    }

    private static void PlateColor(SpriteRenderer sr, ref int key, float hp, float ghost)
    {
        hp = FxMath.Clamp01(hp);
        ghost = FxMath.Clamp01(ghost);
        int k = (int)(hp * 255f) * 256 + (int)(ghost * 255f);
        if (k == key) return;
        key = k;
        sr.color = FxMath.Rgba(hp, ghost, 0f, 1f);
    }

    internal static string DescribeBar() => $"barMat={_barMat} flat={_flat.Count} plate={_plateKey[0]}/{_plateKey[1]}";
}
