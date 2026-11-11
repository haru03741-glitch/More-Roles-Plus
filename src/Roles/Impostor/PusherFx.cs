using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// 突き落と師の体の動き (全員の端末で)。どちらも本編のキルの絵を流す全身の描き替え (CrewFlip)。
// - 突き飛ばす: 体ごと前へ踏み込んでぶつかる。当たったコマ (Contact 秒) で相手がよじれて斜めに倒れながら口の方へ飛び、起き直る
// - 泡を剥がす: 口の方を向いて腰を落とし、両手で泡をつかんで震えながら引く。泡が抜けた所 (Rip 秒・剥がしが刻みで効く頃) で
//   吹き出す空気に体を口の方へ持っていかれ、踏みとどまって立ち直る。手は泡をつかんだ所に出し、抜けたら体の方へ離れて消える
internal static class PusherFx
{
    internal const float Contact = 0.16f;     // 踏み込んでから当たるまで (pushshove の 11→13 のコマ)
    private const float ShoveHold = 0.9f;     // 向きを相手の方へ保つ長さ (連番の長さ)
    private const float StripHold = 1.15f;
    private const float Rip = 0.58f;          // 泡が抜けるまで (剥がしの電文から刻みで効くまでとほぼ同じ)
    private const float HandSize = 0.3f, HandGap = 0.2f, HandIn = 0f, HandInto = 0.3f;
    internal const float ArmReach = 1f;       // 突き飛ばせる相手との間 (中心どうし)。手の届く隣の人だけ
    private const float BodyHalf = 0.3f;      // 体の中心から横の面まで
    private const float PalmFrom = 0.3f, ArmOut = 0.75f; // 手を出し始める所 / 手が伸びる最大 (押す人の中心から)
    private const float PalmSize = 0.24f, PalmGap = 0.11f, PalmY = -0.05f;

    internal static void Shove(PlayerControl user, PlayerControl target, bool blocked)
    {
        if (!user || !target) return;
        var up = user.transform.position;
        var tp = target.transform.position;
        DuelFacing.Hold(user, tp.x - up.x, ShoveHold);
        CrewFlip.Play(user, "pushshove");
        BreakNoise.PlayAt("noise_pusher_shove", up.x, up.y, 12f, 5f, 0.8f, 1f);
        Palms(user, up, tp, blocked);
        if (blocked) return;
        // 口を背にして後ろ向きに倒れ込む (口の方と反対を向く)。口への道が無ければ突き飛ばした人の方を向く
        float face = Decompression.PathAt(new Vector2(tp.x, tp.y), out var dir) ? -dir.x : up.x - tp.x;
        PortKit.After(Contact, () =>
        {
            if (!target || target.Data == null || target.Data.IsDead) return;
            DuelFacing.Hold(target, face, ShoveHold);
            CrewFlip.Play(target, "pushshoved");
        });
    }

    // 両手の平を相手の体の面へ突き出して押す。手は押す人の体に付いて動く (2 人とも流れに乗っているので宙に残さない)。
    // 届く先は各端末の手元の位置から決め、ArmOut より先へは伸ばさない (通信の遅れで相手が遠く見えても手が飛んで行かない)
    private static void Palms(PlayerControl user, Vector3 up, Vector3 tp, bool blocked)
    {
        float dx = tp.x - up.x, dy = tp.y - up.y, len = FxMath.Sqrt(dx * dx + dy * dy);
        if (len > 1e-3f) { dx /= len; dy /= len; }
        else { dx = user.cosmetics && user.cosmetics.FlipX ? -1f : 1f; dy = 0f; } // 重なっている時は向いている方へ
        Plugin.Logger.LogInfo($"[Pusher] shove palms gap={len:0.00} blocked={blocked}");
        float reach = FxMath.Clamp(len - BodyHalf, PalmFrom, ArmOut); // 押す人の中心から相手の体の面まで
        var sprite = Roles.ButtonIcons.Load("emote_open", 100f, FxMath.V2(0.5f, 0.5f));
        if (!sprite) return;
        bool flip = dx < 0f;
        float sx = flip ? -1f : 1f; // FxHands は flip の時に横のずれを鏡に映すので、先に戻しておく
        float back = reach - PalmFrom, push = blocked ? 0.03f : 0.2f;
        var keys = new[]
        {
            new FxHands.Key(0f, -10f, 0.6f, 0f, -dx * back * sx, -dy * back),
            new FxHands.Key(0.07f, -10f, 1f, 1f, -dx * back * 0.5f * sx, -dy * back * 0.5f),
            new FxHands.Key(Contact, -18f, 1.05f, 1f, 0f, 0f),
            new FxHands.Key(Contact + 0.12f, -22f, 1.05f, 1f, dx * push * sx, dy * push, blocked ? 0.02f : 0f),
            new FxHands.Key(Contact + 0.3f, -12f, 0.9f, 0f, dx * push * 0.6f * sx, dy * push * 0.6f),
        };
        int color = user.Data != null ? user.Data.DefaultOutfit.ColorId : 0;
        float z = (up.y < tp.y ? up.y : tp.y) / 1000f - 0.05f; // 2 人より手前
        for (int side = -1; side <= 1; side += 2)
        {
            // 体の面に沿って上下 (斜めなら押す向きと直角) に PalmGap ずつ離して 2 つ
            var hp = FxMath.V2(up.x + dx * reach - dy * PalmGap * side, up.y + PalmY + dy * reach + dx * PalmGap * side);
            FxHands.Play(keys, hp, color, PalmSize, flip, side > 0 ? 0.02f : 0f, z, null, 0f, sprite, user.transform);
        }
    }

    internal static void Strip(byte by, Vector2 asked)
    {
        var p = PortKit.ById(by);
        if (!p || p.Data == null || p.Data.IsDead) return;
        var pos = p.GetTruePosition();
        if (!Decompression.SealedMouthAt(pos, Decompression.StripReach + 1f, out var at, out var away)
            && !Decompression.SealedMouthAt(asked, Decompression.StripReach + 1f, out at, out away)) return;
        // 手は口の線から泡の面の中へ (宇宙の側へ) HandInto。北の壁なら壁の正面の泡の上に来る
        float al = FxMath.Sqrt(away.x * away.x + away.y * away.y);
        if (al > 1e-3f) at = FxMath.V2(at.x + away.x / al * HandInto, at.y + away.y / al * HandInto);
        DuelFacing.Hold(p, at.x - pos.x, StripHold);
        CrewFlip.Play(p, "pushstrip");
        BreakNoise.PlayAt("noise_foam_tear", at.x, at.y, 14f, 6f, 0.9f, 1f);

        // 両手は口の線に沿って左右へ HandGap ずつ・体の側へ少し手前。泡が抜けると体の方へ離れて消える
        float dx = at.x - pos.x, dy = at.y - pos.y, len = FxMath.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f) return;
        dx /= len; dy /= len;
        float rot = FxMath.Atan2(dy, dx) * 57.29578f + 90f;
        float bx = -dx * 0.45f, by2 = -dy * 0.45f;
        var keys = new[]
        {
            new FxHands.Key(0f, rot, 0f, 0f, -dx * 0.1f, -dy * 0.1f),
            new FxHands.Key(0.1f, rot, 1f, 1f),
            new FxHands.Key(0.16f, rot, 1f, 1f, 0f, 0f, 0.012f),
            new FxHands.Key(Rip - 0.02f, rot, 1f, 1f, -dx * 0.06f, -dy * 0.06f, 0.03f),
            new FxHands.Key(Rip + 0.08f, rot + 25f, 1.05f, 1f, bx, by2),
            new FxHands.Key(Rip + 0.26f, rot + 35f, 0.8f, 0f, bx * 1.3f, by2 * 1.3f),
        };
        int color = p.Data.DefaultOutfit.ColorId;
        float z = pos.y / 1000f - 0.05f; // 剥がす人より手前 (泡と壁の絵より手前)
        for (int side = -1; side <= 1; side += 2)
        {
            var hp = FxMath.V2(at.x - dx * HandIn - dy * HandGap * side, at.y - dy * HandIn + dx * HandGap * side);
            FxHands.Play(keys, hp, color, HandSize, false, 0.03f * (side + 1), z, null);
        }
    }
}
