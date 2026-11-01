using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// サムライが刀で光線を押し止めている間: 光線とキャノンの照射を伸ばし、押し切られたら細って消す。
// 刀に当たった所で光線は割れる: 押し止めている間は刃の両側へ二手に分かれて流れ、弾いた時は横へ逸れて細い筋に散る
internal static partial class NovaFx
{
    // p の今の光線を、今から seconds 秒は照射し続ける (キャノンも撃った形のまま)
    internal static void HoldBeam(PlayerControl p, float seconds)
    {
        float now = Time.time;
        foreach (var b in Beams)
        {
            if (b.P != p) continue;
            float life = now - b.T0 + seconds;
            if (life > b.Life) b.Life = life;
        }
        foreach (var s in Shots)
        {
            if (s.P != p || s.FiredAt < 0f) continue;
            float extra = now - s.FiredAt + seconds - BeamLife;
            if (extra > s.Extra) s.Extra = extra;
        }
    }

    // p の光線を今すぐ細らせて消す (押し切られた・会議)。キャノンはここから冷まして湯気を吐く
    internal static void CutBeam(PlayerControl p)
    {
        float now = Time.time;
        foreach (var b in Beams)
        {
            if (b.P != p) continue;
            float t = now - b.T0;
            if (t < b.Life - Thread) b.Life = t + Thread;
        }
        foreach (var s in Shots)
        {
            if (s.P != p || s.FiredAt < 0f) continue;
            float extra = now - s.FiredAt - BeamLife;
            s.Extra = extra > 0f ? extra : 0f;
        }
    }
    // ---- 刀で割れる光線 ----
    private const float SplitAngleMin = 24f, SplitAngleMax = 30f; // 押し合い: 刃の両側へ分かれる開き (度)
    private const float SplitSize = 0.55f, SplitHalf = 0.5f;      // 枝の太さ・広がり (元の光線に対して)
    private const float BranchReach = 6.5f;                       // 枝が届く長さ (壁で止まる)
    private const float DeflectTurnMin = 110f, DeflectTurnMax = 140f; // 弾き: 来た向きから逸れる角度
    private const float DeflectSize = 0.7f, DeflectHalf = 0.7f;
    private const int Splinters = 4;                              // 弾いた時に散る細い筋
    private const float TailMin = 0.6f, TailMax = 1.6f;         // 元の光線が刃の手前で絞られて消える長さ
    private const float PendingFor = 1f;                          // 光線より先に届いた割れ方を待つ秒

    private static readonly List<(PlayerControl p, bool deflect, float t, float holdUntil)> PendingSplit = new();

    // p の光線を刀の所で割る (deflect = 弾いて逸らす・false = 押し止めて二手に分ける)。光線がまだ出ていなければ出た時に割る
    internal static void SplitBeam(PlayerControl p, bool deflect, float holdSeconds = 0f)
    {
        if (RewindPlayback.Active) return;
        float now = Time.time;
        var b = Parent(p, now);
        if (b == null)
        {
            for (int i = PendingSplit.Count - 1; i >= 0; i--) if (PendingSplit[i].p == p) PendingSplit.RemoveAt(i);
            PendingSplit.Add((p, deflect, now, now + holdSeconds));
            return;
        }
        // 元の光線は刃の手前で細って消え、枝はその消えかけの所から出る (先で四角く切れて見えない)
        b.Tail = FxMath.Clamp(b.Spread, TailMin, TailMax);
        b.MpbLen = -1f;
        if (deflect) Deflect(b, now);
        else Part(b, now);
    }

    // Fire の終わり: 先に届いていた割れ方をこの光線に当てる (撃った人がホストだと、客には結果が光線より先に届く)
    private static void TakePendingSplit(PlayerControl p)
    {
        if (PendingSplit.Count == 0) return;
        float now = Time.time;
        for (int i = PendingSplit.Count - 1; i >= 0; i--)
        {
            var e = PendingSplit[i];
            if (now - e.t > PendingFor || !e.p) { PendingSplit.RemoveAt(i); continue; }
            if (e.p != p) continue;
            PendingSplit.RemoveAt(i);
            if (!e.deflect && e.holdUntil > now) HoldBeam(p, e.holdUntil - now);
            SplitBeam(p, e.deflect);
        }
    }

    // p の今照射している元の光線 (枝でない・細り始めていない物)
    private static Beam Parent(PlayerControl p, float now)
    {
        for (int i = Beams.Count - 1; i >= 0; i--)
        {
            var b = Beams[i];
            if (b.P == p && !b.Branch && now - b.T0 < b.Life - Thread) return b;
        }
        return null;
    }

    // 押し止め: 刃に割られた流れが両側へ分かれ、刀の後ろへ斜めに抜けていく (元の光線が続く間)
    private static void Part(Beam b, float now)
    {
        for (int sgn = -1; sgn <= 1; sgn += 2)
            Branch(b, now, b.Angle + sgn * FxMath.Range(SplitAngleMin, SplitAngleMax), SplitSize, SplitHalf, BranchReach, b.T0 + b.Life - now, true);
    }

    // 弾き: 刀の面で光線が横へ逸れ (太い 1 本)、まわりに細い筋が散って早く消える。当たった所から逸れる向きへ火花
    private static void Deflect(Beam b, float now)
    {
        float sgn = FxMath.Range(0f, 1f) < 0.5f ? -1f : 1f;
        float turn = b.Angle + sgn * FxMath.Range(DeflectTurnMin, DeflectTurnMax);
        float left = b.T0 + b.Life - now;
        Branch(b, now, turn, DeflectSize, DeflectHalf, BranchReach, left, true);
        for (int i = 0; i < Splinters; i++)
        {
            float a = turn + FxMath.Range(-40f, 40f);
            Branch(b, now, a, FxMath.Range(0.15f, 0.25f), FxMath.Range(0.12f, 0.22f), FxMath.Range(1.2f, 3f), left * FxMath.Range(0.25f, 0.6f), false);
        }
        float ex = b.X + b.Dx * b.Len, ey = b.Y + b.Dy * b.Len;
        for (int i = 0; i < 18; i++)
        {
            float a = turn + FxMath.Range(-35f, 35f), v = FxMath.Range(3f, 7f);
            SpawnPuff(ex, ey, FxMath.Cos(a * Deg) * v, FxMath.Sin(a * Deg) * v + 1f, 0.13f, 0.09f, FxMath.Range(0.3f, 0.6f), b.Z - 0.0005f, PuffKind.Spark);
        }
        SpawnPuff(ex, ey, 0f, 0f, 0.6f * b.Size + 0.4f, 1.1f * b.Size + 0.7f, 0.12f, b.Z - 0.0008f, PuffKind.Flash);
    }

    // 元の光線の先 (刀の当たる所) から angle の向きへ枝を 1 本。壁までの長さは出した時に 1 回だけ測る
    private static void Branch(Beam b, float now, float angle, float size, float half, float reach, float life, bool sparkAtWall)
    {
        if (life <= Thread + 0.05f) return;
        float back = b.Tail * 0.25f < b.Len - 0.2f ? b.Tail * 0.25f : 0f;
        float ex = b.X + b.Dx * (b.Len - back), ey = b.Y + b.Dy * (b.Len - back), floor = ey - b.Height;
        float dx = FxMath.Cos(angle * Deg), dy = FxMath.Sin(angle * Deg);
        var hit = Physics2D.Raycast(FxMath.V2(ex, floor), FxMath.V2(dx, dy), reach, Constants.ShipOnlyMask);
        bool wall = hit.collider;
        float len = wall ? hit.distance : reach;
        if (len < 0.3f) return;
        var nb = NewBeam(b.P, ex, ey, angle, len, b.Size * size, b.Spread * half, wall && sparkAtWall, floor, b.Height, floor);
        nb.Branch = true;
        nb.Life = life;
    }
}
