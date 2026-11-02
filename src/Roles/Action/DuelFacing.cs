using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Roles.Neutral;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 立ち合いの間は体を相手へ向ける (本編は歩く向きへ顔を向けるので、下がりながら戦うと背中を見せる)。
// 向く相手 = 同じ組の一番近い敵 (サムライは組の他の全員・サムライでない側は組のサムライだけ = 味方のインポスターは向かない)。
// 今の相手より Switch だけ近い敵が出た時だけ向き直す (乱戦で二人の間に立つと毎回振り返らないように)。
// 全員の端末が自分の画面の位置から決める (見た目だけ・電文なし)。本編が向きを決めた直後に書くので、向きの揺れは出ない
// 手に物を持って狙っている人 (機関銃・キャノン) は、立ち合いより先に狙いの側へ向ける (右を向いたまま左へ撃たないように)。
// 持つ側の効果が毎フレーム Hold で狙いの横向きを渡し、渡されなくなって HoldFor 経ったら本編の向きに戻る
[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.HandleAnimation))]
internal static class DuelFacing
{
    private const float Switch = 0.8f;   // 向く相手を替えるのに要る近さの差
    private const float DeadX = 0.2f;    // 相手が真上・真下に近い時は向きを変えない
    private const byte None = 255;
    private static readonly byte[] Foe = new byte[256];
    private static readonly bool[] Turns = new bool[256]; // 前のフレームで本編の向きを直した人
    private static int _turning;                          // Turns が立っている人数
    internal static int Turned; // 確かめ用: 本編の向きを相手の方へ直した回数
    private const float HoldFor = 0.25f; // 狙いの向きを渡されなくなってから本編の向きへ戻すまでの秒
    private const float HoldDead = 0.25f; // 狙いの横向き (cos) がこれより小さい = ほぼ真上・真下は今の向きのまま
    private static readonly float[] HeldUntil = new float[256];
    private static readonly bool[] HeldLeft = new bool[256];
    private static float _heldMax; // 一番遅い HeldUntil (0 = 誰も持っていない)

    // 物を持って狙っている: dx = 狙いの横向き (右が正)。毎フレーム (少なくとも HoldFor より短い間で) 呼ぶ
    internal static void Hold(PlayerControl p, float dx)
    {
        if (!p) return;
        byte id = p.PlayerId;
        float now = Time.time;
        if (dx > HoldDead) HeldLeft[id] = false;
        else if (dx < -HoldDead) HeldLeft[id] = true;
        else if (now >= HeldUntil[id]) HeldLeft[id] = p.cosmetics && p.cosmetics.FlipX;
        float until = now + HoldFor;
        HeldUntil[id] = until;
        if (until > _heldMax) _heldMax = until;
    }

    // 持っている人がいるか (期限が過ぎたら畳む)
    private static bool AnyHeld()
    {
        if (_heldMax <= 0f) return false;
        if (Time.time < _heldMax) return true;
        _heldMax = 0f;
        return false;
    }

    static DuelFacing()
    {
        for (int i = 0; i < Foe.Length; i++) Foe[i] = None;
    }

    // 本編は向きが変わると走りの絵を頭から流し直すので、毎フレーム直したままだと下がりながら歩く足が止まる。
    // 本編が向きを決める前に、前のフレームで直した向きを本編の向きへ戻しておく (本編からは向きが変わっていないように見える)
    public static void Prefix(PlayerPhysics __instance)
    {
        if (_turning == 0) return;
        // 立ち合いが終わった・逆再生中は直しておらず、戻すものもない (抜けた人の印が残らないように畳む)
        if (!Duel.Any && !AnyHeld() || RewindPlayback.Active)
        {
            System.Array.Clear(Turns);
            _turning = 0;
            return;
        }
        var pc = __instance.myPlayer;
        if (!pc || !Turns[pc.PlayerId]) return;
        Turns[pc.PlayerId] = false;
        _turning--;
        var cos = pc.cosmetics;
        if (cos) cos.SetFlipX(!cos.FlipX);
    }

    public static void Postfix(PlayerPhysics __instance)
    {
        bool duel = Duel.Any;
        if (!duel && _heldMax <= 0f || RewindPlayback.Active) return;
        var pc = __instance.myPlayer;
        if (!pc) return;
        byte id = pc.PlayerId;
        if (DuelTumble.Busy(id)) return;
        bool held = _heldMax > 0f && Time.time < HeldUntil[id];
        bool left = false, want = duel && FoeSide(pc, id, out left);
        if (held) { want = true; left = HeldLeft[id]; }
        if (!want) return;
        if (pc.inVent || pc.onLadder || pc.Data == null || pc.Data.IsDead) return;
        var cos = pc.cosmetics;
        if (!cos || cos.FlipX == left) return;
        cos.SetFlipX(left);
        if (!Turns[id]) { Turns[id] = true; _turning++; }
        Turned++;
    }

    // 立ち合いの相手の側 (left)。向きを変えない時は false
    private static bool FoeSide(PlayerControl pc, byte id, out bool left)
    {
        left = false;
        byte g = Duel.GroupOf(id);
        if (g == 0) { Foe[id] = None; return false; }
        if (pc.inVent || pc.onLadder || pc.Data == null || pc.Data.IsDead || !GameData.Instance) return false;
        if (!pc.cosmetics) return false;
        Vector2 me = pc.GetTruePosition();
        DuelTrail.Record(id, me, Time.time);
        bool samurai = RoleState.Of(id) is Samurai;
        byte cur = Foe[id], best = None;
        float bestD = float.MaxValue, curD = float.MaxValue, curX = 0f, bestX = 0f;
        int n = Duel.CountOf(g);
        for (int i = 0; i < n; i++)
        {
            byte o = Duel.MemberOf(g, i);
            if (o == id || !samurai && RoleState.Of(o) is not Samurai) continue;
            var op = GameData.Instance.GetPlayerById(o)?.Object;
            if (!op || op.Data == null || op.Data.IsDead) continue;
            Vector2 at = op.GetTruePosition();
            float dx = at.x - me.x, dy = at.y - me.y, d = dx * dx + dy * dy;
            if (o == cur) { curD = d; curX = dx; }
            if (d < bestD) { bestD = d; best = o; bestX = dx; }
        }
        if (best == None) { Foe[id] = None; return false; }
        // 距離の 2 乗で比べる: 今の相手の距離から Switch 以上近い時だけ替える
        if (curD < float.MaxValue && best != cur)
        {
            float c = FxMath.Sqrt(curD) - Switch;
            if (c <= 0f || bestD > c * c) { best = cur; bestX = curX; }
        }
        Foe[id] = best;
        if (bestX > -DeadX && bestX < DeadX) return false;
        left = bestX < 0f;
        return true;
    }
}
