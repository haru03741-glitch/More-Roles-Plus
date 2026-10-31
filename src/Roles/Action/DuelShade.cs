using System;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 立ち合いの外から見た当事者 = 影。自分が入っていない組で戦っている人は、自分の画面では色も名札も着せ替えも無い黒い姿に見える
// (戦いがあることと場所は分かるが、誰かは分からない)。同じ組の人同士は普通に見える。
// 組を抜けた後も、自分の視界に映っている間は影のまま (畳まれた瞬間に戻すと勝った方の正体が出る)。視界から外れて ForgetMs 経ったら戻す。
// 幽霊と会議の間は全部戻す。塗るのは本編の着せ替え層 (色番号) なので、化け物の口や刀の拳も同じ影の色になる
internal static class DuelShade
{
    private const int Every = 6;          // 確かめるフレームの間隔
    private const long ForgetMs = 2500;   // 視界から外れてこの間で戻す
    internal const int ShadeColor = 6;    // 本編の黒の上に Tint を掛ける (黒の人と見分けが付くよう、もっと暗く)
    internal static Color Tint => FxMath.Rgba(0.3f, 0.3f, 0.36f, 1f);

    private struct Shade
    {
        public bool On, NameHidden, PetHidden, CosHidden;
        public long OutSince;
    }

    private static readonly Shade[] Shades = new Shade[256];
    private static int _count, _tick;
    internal static int Shaded, Restored;

    internal static bool On(byte id) => Shades[id].On;

    // 毎フレーム (Plugin から)。組も影も無ければ比べるだけで帰る
    internal static void Tick()
    {
        if (!Duel.Any && _count == 0) return;
        if (++_tick < Every) return;
        _tick = 0;
        // 暗視カメラは全員を同じ色に塗り着せ替えも隠すので、その間は塗り合わない (影の状態は保ち、閉じた後に塗り直す)
        if (Rules.NightVisionCams.Active) return;
        var lp = PlayerControl.LocalPlayer;
        bool viewer = lp && lp.Data != null && !lp.Data.IsDead && ShipStatus.Instance && !MeetingHud.Instance && GameData.Instance;
        if (!viewer)
        {
            if (_count > 0) RestoreAll();
            return;
        }
        byte me = lp.PlayerId;
        // 自分の組の外で戦っている人を影に
        for (byte g = 1; g <= Duel.MaxGroups; g++)
        {
            int n = Duel.CountOf(g);
            for (int i = 0; i < n; i++)
            {
                byte id = Duel.MemberOf(g, i);
                if (id == me || Duel.SameGroup(id, me)) continue;
                var pc = GameData.Instance.GetPlayerById(id)?.Object;
                if (!pc || pc.cosmetics == null) continue;
                Apply(id, pc);
                Shades[id].OutSince = 0;
            }
        }
        if (_count == 0) return;
        long now = Environment.TickCount64;
        Vector2 eye = lp.GetTruePosition();
        float reach = -1f;
        for (int i = 0; i < 255; i++)
        {
            ref var s = ref Shades[i];
            if (!s.On) continue;
            byte id = (byte)i;
            var pc = GameData.Instance.GetPlayerById(id)?.Object;
            // 同じ組に入った (目の前で斬り合う相手になった)・抜けた人は戻す
            if (!pc || pc.Data == null || pc.Data.Disconnected || Duel.SameGroup(id, me)) { Restore(id, pc); continue; }
            if (Duel.InDuel(id)) continue;
            if (reach < 0f) reach = ShipStatus.Instance.CalculateLightRadius(lp.Data);
            if (Sees(eye, reach, pc)) { s.OutSince = 0; Apply(id, pc); continue; }
            if (s.OutSince == 0) s.OutSince = now;
            else if (now - s.OutSince >= ForgetMs) Restore(id, pc);
        }
    }

    // 自分の視界に映っているか (光の届く所で、間に視界を遮る壁が無い)
    private static bool Sees(Vector2 eye, float reach, PlayerControl pc)
    {
        if (!pc.Visible || pc.Data.IsDead) return false;
        Vector2 at = pc.GetTruePosition();
        float dx = at.x - eye.x, dy = at.y - eye.y;
        if (dx * dx + dy * dy > reach * reach) return false;
        return !PhysicsHelpers.AnythingBetween(eye, at, Constants.ShadowMask, false);
    }

    // 影にする。本編が色や着せ替えを戻していたら塗り直す (変身・ベントの出入り・化け物の姿の終わり)
    private static void Apply(byte id, PlayerControl pc)
    {
        ref var s = ref Shades[id];
        var c = pc.cosmetics;
        if (!s.On)
        {
            s.On = true;
            _count++;
            Shaded++;
        }
        // 本編が色を戻した (変身など) か、見えない所から出てきた時は着せ替えも出し直されているので隠し直す
        bool visible = pc.Visible;
        if (c.ColorId != ShadeColor)
        {
            c.SetColor(ShadeColor);
            s.CosHidden = false;
        }
        if (!visible) s.CosHidden = false;
        var body = c.currentBodySprite != null ? c.currentBodySprite.BodySprite : null;
        if (body)
        {
            var col = body.color;
            var t = Tint;
            if (col.r != t.r) body.color = FxMath.Rgba(t.r, t.g, t.b, col.a);
        }
        if (visible && !s.CosHidden)
        {
            c.SetBodyCosmeticsVisible(false);
            s.CosHidden = true;
        }
        if (c.nameText && c.nameText.gameObject.activeSelf)
        {
            c.nameText.gameObject.SetActive(false);
            s.NameHidden = true;
        }
        var pet = c.currentPet;
        if (pet && pet.Visible)
        {
            c.SetPetVisible(false);
            s.PetHidden = true;
        }
    }

    private static void Restore(byte id, PlayerControl pc)
    {
        ref var s = ref Shades[id];
        if (!s.On) return;
        bool name = s.NameHidden, pet = s.PetHidden;
        s = default;
        _count--;
        Restored++;
        if (!pc || pc.cosmetics == null || pc.Data == null) return;
        var c = pc.cosmetics;
        c.SetColor(pc.CurrentOutfit != null ? pc.CurrentOutfit.ColorId : pc.Data.DefaultOutfit.ColorId);
        var body = c.currentBodySprite != null ? c.currentBodySprite.BodySprite : null;
        if (body) body.color = FxMath.Rgba(1f, 1f, 1f, body.color.a);
        // ベントの中などで本編が隠している人と、化け物の姿の最中の人の着せ替えは出さない (それぞれ終わる時に戻る)
        if (pc.Visible && !ImpostorMaw.HidesCosmetics(id)) c.SetBodyCosmeticsVisible(true);
        if (name && c.nameText) c.nameText.gameObject.SetActive(true);
        if (pet) c.SetPetVisible(true);
    }

    internal static void RestoreAll()
    {
        for (int i = 0; i < 256; i++)
            if (Shades[i].On) Restore((byte)i, GameData.Instance ? GameData.Instance.GetPlayerById((byte)i)?.Object : null);
        _count = 0;
    }

    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder($"count={_count} shaded={Shaded} restored={Restored}");
        for (int i = 0; i < 256; i++)
            if (Shades[i].On) sb.Append(' ').Append(i).Append(Shades[i].OutSince > 0 ? "(out)" : "");
        return sb.ToString();
    }
}
