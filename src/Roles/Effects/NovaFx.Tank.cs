using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 溜まり具合の筒: 溜めている間、ノヴァの体のキャノンと反対の脇に立つ縦長の筒。下から光が溜まっていき、満タンで白熱して脈打つ。
// ノヴァが見えている人全員に出る (撃たれる側も溜まり具合を見て動ける)。キャノンと同じく、ノヴァが見えない時は出さない
internal static partial class NovaFx
{
    private const float TankPpu = 200f;
    private const float TankSide = 0.6f, TankY = -0.02f;   // 体の芯から脇へ・上下
    private const float TankFillBottom = -0.26f;           // 筒の中身の底 (筒の真ん中から)

    private static Sprite _tankBack, _tankFill, _tankFront;

    private static bool TankArt()
    {
        _tankBack = ButtonIcons.Load("nova_tank_back", TankPpu, FxMath.V2(0.5f, 0.5f));
        _tankFill = ButtonIcons.Load("nova_tank_fill", TankPpu, FxMath.V2(0.5f, 0f));
        _tankFront = ButtonIcons.Load("nova_tank_front", TankPpu, FxMath.V2(0.5f, 0.5f));
        return _tankBack && _tankFill && _tankFront;
    }

    private sealed class Tank
    {
        public GameObject Go;
        public Transform Tf, FillTf, GlowTf;
        public SpriteRenderer Back, Fill, Front, Glow;
        public bool On;
        public int KeyLevel = -1, KeyColor = -1, KeyAlpha = -1;
    }

    private static Tank MakeTank(Transform parent)
    {
        var t = new Tank { Go = new GameObject("Tank") };
        t.Tf = t.Go.transform;
        t.Tf.SetParent(parent, false);
        t.Back = Make("Back", _tankBack, t.Tf);
        t.Fill = Make("Fill", _tankFill, t.Tf); t.FillTf = t.Fill.transform;
        t.Front = Make("Front", _tankFront, t.Tf);
        t.Glow = Make("Glow", Terrain.DebrisArt.SoftGlow, t.Tf); t.GlowTf = t.Glow.transform;
        t.FillTf.localPosition = FxMath.V3(0f, TankFillBottom, -0.00002f);
        t.Front.transform.localPosition = FxMath.V3(0f, 0f, -0.00004f);
        t.GlowTf.localPosition = FxMath.V3(0f, 0.02f, -0.00006f);
        t.GlowTf.localScale = FxMath.V3(0.55f, 0.9f, 1f);
        t.Glow.enabled = false;
        t.Go.SetActive(false);
        t.On = false;
        return t;
    }

    // level = 溜まり (0..1)・show = 出すか・fade = 消え方 (撃った後に薄れる)
    private static void TickTank(Shot s, Vector3 pos, float now, float level, bool show, float fade)
    {
        if (s.Tank == null)
        {
            if (!show) return;
            s.Tank = MakeTank(s.Root.transform);
        }
        var t = s.Tank;
        if (t.On != show) { t.On = show; t.Go.SetActive(show); }
        if (!show) return;
        // キャノンを持つ手の反対の脇・体の少し手前
        t.Tf.position = FxMath.V3(pos.x - s.Side * TankSide, pos.y + TankY, pos.z - 0.0003f);
        int kl = (int)(level * 100f);
        if (kl != t.KeyLevel)
        {
            t.KeyLevel = kl;
            t.FillTf.localScale = FxMath.V3(1f, kl / 100f, 1f);
        }
        // 色: 光線と同じ色の組の暗い段 → 本体 → 明るい筋 → 満タンで芯の色へ脈打つ
        bool full = level >= 0.999f;
        float pulse = full ? 0.5f + 0.5f * FxMath.Sin(now * 9f) : 0f;
        int kc = full ? 100 + (int)(pulse * 20f) : kl;
        int ka = (int)(fade * 32f);
        if (kc != t.KeyColor || ka != t.KeyAlpha)
        {
            t.KeyColor = kc; t.KeyAlpha = ka;
            float a = ka / 32f;
            t.Fill.color = full ? Pm(3, 0.3f + 0.5f * pulse, a) : level < 0.5f ? Pm(1, level / 0.5f, a) : Pm(2, (level - 0.5f) / 0.5f, a);
            t.Back.color = FxMath.Rgba(1f, 1f, 1f, a);
            t.Front.color = FxMath.Rgba(1f, 1f, 1f, a);
            bool glow = level > 0.6f;
            if (t.Glow.enabled != glow) t.Glow.enabled = glow;
            if (glow) t.Glow.color = full ? Pm(2, 0.7f, a * (0.35f + 0.3f * pulse)) : Pm(2, 0.1f, a * 0.25f * (level - 0.6f) / 0.4f);
        }
    }
}
