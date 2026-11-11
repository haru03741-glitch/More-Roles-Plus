using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// リモコンに操られている人が壁を殴る姿 (全員の端末で)。体ごと前へ踏み込み (突き飛ばしと同じ全身の描き替え)、
// 拳を壁の面へ突き出す。拳はその人の体に付いて動き、当たった所で震えて引き、消える
internal static class RemoteControllerFx
{
    internal const float Contact = PusherFx.Contact; // 踏み込んでから拳が壁に届くまで
    private const float Hold = 0.9f;
    private const float FistSize = 0.26f, FistY = -0.05f;
    // 拳を出す最短 / 伸びる最大 (体の中心から)。踏み込みの絵は体ごと前へ出るので、最短はその体の先 (壁に体が付いていても拳が胴に隠れない)
    private const float FistFrom = 0.7f, FistOut = 0.9f;
    private const float WindBack = 0.28f;

    internal static void Punch(PlayerControl p, Vector2 face, Vector2 toward)
    {
        if (!p || p.Data == null || p.Data.IsDead || !p.Visible) return; // 透明な人の拳は居場所を明かすので出さない
        float dx = toward.x, dy = toward.y, len = FxMath.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f) return;
        dx /= len; dy /= len;
        var up = p.transform.position;
        var feet = p.GetTruePosition();
        float gap = (face.x - feet.x) * dx + (face.y - feet.y) * dy; // 足元から壁の面まで (向きに沿って)
        float reach = FxMath.Clamp(gap, FistFrom, FistOut);
        if (FxMath.Abs(dx) > 0.1f) DuelFacing.Hold(p, dx, Hold);
        CrewFlip.Play(p, "pushshove");
        var sprite = ButtonIcons.Load("emote_fist", 100f, FxMath.V2(0.5f, 0.5f));
        if (!sprite) return;
        bool flip = dx < 0f;
        float sx = flip ? -1f : 1f; // FxHands は flip の時に横のずれと回転を鏡に映すので、右向きの値で渡す
        // 絵は指の付け根 (拳の打つ面) が上。打つ面を叩く向きへ回す
        float rot = FxMath.Atan2(dy, dx * sx) * FxMath.Rad2Deg - 90f;
        var keys = new[]
        {
            new FxHands.Key(0f, rot, 0.8f, 0f, -dx * WindBack * 0.6f * sx, -dy * WindBack * 0.6f),
            new FxHands.Key(0.07f, rot, 1f, 1f, -dx * WindBack * sx, -dy * WindBack),
            new FxHands.Key(Contact, rot, 1.1f, 1f, 0f, 0f, 0.025f),
            new FxHands.Key(Contact + 0.1f, rot, 1.05f, 1f, -dx * 0.05f * sx, -dy * 0.05f),
            new FxHands.Key(Contact + 0.32f, rot, 0.9f, 0f, -dx * 0.18f * sx, -dy * 0.18f),
        };
        int color = GatlingFx.ShownColor(p);
        var at = FxMath.V2(up.x + dx * reach, up.y + FistY + dy * reach);
        float z = up.y / 1000f - (dy > 0.5f ? -0.002f : 0.05f); // 上の壁を殴る時は体の奥・それ以外は体の手前
        FxHands.Play(keys, at, color, FistSize, flip, 0f, z, null, 0f, sprite, p.transform);
    }
}
