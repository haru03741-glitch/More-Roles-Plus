using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// 壊れたドローンの残骸。斬られたら 2 つの半分が切り口を見せて離れて落ちる・撃たれた/叩かれた/爆発/焼けたら羽が止まって機首から落ち、
// 床に当たって前右の筒が腕から折れて転がる・水に浸かったら水へ落ちて沈む。
// 飛んでいるドローンの残骸は本人の端末だけ (設定「飛ぶドローンを全員に見せる」なら全員)・止まったドローンの残骸は全員
public sealed partial class Ubiquitous
{
    private const float FlyLift = 0.46f;  // 飛んでいる時の胴の真ん中の床からの高さ (影を置く高さと同じ)
    private const float IdleLift = 0.22f; // 止まっている時の胴の真ん中の床からの高さ
    private const float HalfApart = 0.55f; // 斬られた半分が離れる速さ
    private const float DuctThrow = 0.9f; // 折れた筒が飛ぶ速さ

    private static WreckFate FateOf(StrikeResult r, in Strike s) => r switch
    {
        StrikeResult.Cut => WreckFate.Cut,
        StrikeResult.Douse => WreckFate.Douse,
        _ => s.Element == StrikeElement.Heat ? WreckFate.Burn : WreckFate.Break,
    };

    // 向きを 1 バイトに (256 で 1 周)
    private static byte AngleByte(float dx, float dy)
    {
        if (dx == 0f && dy == 0f) return 0;
        int k = (int)FxMath.Floor(FxMath.Atan2(dy, dx) * (256f / (FxMath.PI * 2f)) + 0.5f);
        return (byte)(k & 255);
    }

    // 斬った線の法線 (刃に直交) または撃たれた向き
    private static byte StrikeAngle(in Strike s, float x, float y)
    {
        float dx = s.Dx, dy = s.Dy;
        if (dx == 0f && dy == 0f) { dx = x - s.X; dy = y - s.Y; }
        return AngleByte(dx, dy);
    }

    // 残骸を出す。at = 胴の真ん中・lift = その時の胴の真ん中の床からの高さ・yaw = 機首の向きの番号・angle = 斬った線の法線/撃たれた向き
    private static void SpawnWreck(Vector2 at, float lift, WreckFate fate, int yaw, byte angle)
    {
        float a = angle * (FxMath.PI * 2f / 256f), nx = FxMath.Cos(a), ny = FxMath.Sin(a);
        float gx = at.x, gy = at.y - lift;
        if (fate == WreckFate.Cut)
        {
            float lh = DroneArt.RestLift(DroneArt.Shape.Half);
            float fall = FxMath.Sqrt(2f * FxMath.Max(lift - lh, 0.02f) / 9f);
            var parts = new WreckPart[2];
            for (int k = 0; k < 2; k++)
            {
                float side = k == 0 ? 1f : -1f;
                // 切り口が相手の半分の側 (-side × 法線) を向く向き。そこから少し回りながら落ちて、着いた時にちょうどその向き
                int rest = FaceYaw(-side * nx, -side * ny);
                parts[k] = new WreckPart
                {
                    Look = HalfLook,
                    Lift = lh,
                    X = gx + nx * side * 0.06f, Y = gy + ny * side * 0.06f,
                    H = lift - lh,
                    Vx = nx * side * HalfApart, Vy = ny * side * HalfApart * 0.6f,
                    Vh = 0.4f,
                    Yaw = rest - side * 2f, Spin = side * 2f / FxMath.Max(fall, 0.15f),
                    ShadowArt = DroneArt.Shadow, Shadow = 0.22f, Pitch = 1.1f, Sparks = true,
                };
            }
            BodyWreck.Add(fate, "ubiq_drone_crash", "ubiq_drone_splash", parts);
            // 刃が通った所: 中の線が切れて火花が両側へ
            GatlingFx.Deflect(at.x, at.y, nx, ny);
            GatlingFx.Deflect(at.x, at.y, -nx, -ny);
            Hearing.At("ubiq_drone_cut", at.x, at.y, HumRange, 0.9f, null, Ears.All);
            return;
        }
        float lw = DroneArt.RestLift(DroneArt.Shape.Wreck);
        int spin = 0;
        var body = new WreckPart
        {
            Look = (int y, bool landed, float age, out bool flip) =>
            {
                if (landed) return DroneArt.WreckFrame(DroneArt.Shape.Wreck, y, out flip);
                // 動力が切れて落ちる間: 機首が下がり、羽の残像が間遠になって止まる
                int pitch = age < 0.08f ? DroneArt.Level : age < 0.18f ? 2 : 3;
                int every = 1 + (int)(age * 12f);
                if (age > 0.45f) return DroneArt.Frame(y, DroneArt.Level, DroneArt.Still, out flip);
                return DroneArt.Frame(y, pitch, (++spin / every) & 1, out flip);
            },
            Lift = lw,
            X = gx, Y = gy,
            H = lift - lw,
            Vx = nx * 0.35f, Vy = ny * 0.2f,
            Yaw = yaw, Spin = 1.5f,
            ShadowArt = DroneArt.Shadow, Shadow = 0.32f, Smoke = true, Sparks = fate != WreckFate.Douse,
            OnLand = fate == WreckFate.Douse ? null : BreakDuct,
        };
        BodyWreck.Add(fate, "ubiq_drone_crash", "ubiq_drone_splash", body);
    }

    private static Sprite HalfLook(int yaw, bool landed, float age, out bool flip) => DroneArt.WreckFrame(DroneArt.Shape.Half, yaw, out flip);

    private static Sprite DuctLook(int yaw, bool landed, float age, out bool flip) => DroneArt.WreckFrame(DroneArt.Shape.Duct, yaw, out flip);

    // 床に当たった衝撃で前右の筒が腕から折れて、外へ転がる
    private static void BreakDuct(BodyWreck.Wreck w, WreckPart body)
    {
        if (body.Wet) return;
        int yaw = (int)FxMath.Floor(body.Yaw + 0.5f) & 15;
        DroneArt.DuctDir(yaw, out float dx, out float dy);
        float ld = DroneArt.RestLift(DroneArt.Shape.Duct);
        w.Add(new WreckPart
        {
            Look = DuctLook,
            Lift = ld,
            X = body.X + dx * 0.26f, Y = body.Y + dy * 0.2f,
            H = 0.05f, Vh = 1.3f,
            Vx = dx * DuctThrow, Vy = dy * DuctThrow * 0.6f,
            Yaw = yaw, Spin = 7f,
            ShadowArt = DroneArt.Shadow, Shadow = 0.13f, Pitch = 1.3f,
        });
    }

    // 切り口が床の上で (dx, dy) を向く向きの番号
    private static int FaceYaw(float dx, float dy)
    {
        int best = 0;
        float bd = -9f;
        for (int k = 0; k < DroneArt.Yaws; k++)
        {
            DroneArt.HalfFaceDir(k, out float fx, out float fy);
            float d = fx * dx + fy * dy;
            if (d > bd) { bd = d; best = k; }
        }
        return best;
    }

    // ---- 電文 ----

    // 飛んでいるドローンが壊れた (本人の端末から・見せている時だけ): ほかの端末にも同じ残骸を出す
    private static readonly RemoteCall<(Vector2 at, byte fate, byte yaw, byte angle)> WreckCall = new("Ubiquitous.Wreck", Route.Anyone,
        (w, v) => { w.Write(v.at.x); w.Write(v.at.y); w.Write(v.fate); w.Write(v.yaw); w.Write(v.angle); },
        r => (new Vector2(r.ReadSingle(), r.ReadSingle()), r.ReadByte(), r.ReadByte(), r.ReadByte()),
        (sender, v) =>
        {
            if (!sender || sender.AmOwner || RoleState.Of(sender) is not Ubiquitous u) return;
            // ドローンは 1 機ずつしか壊れない (出し直しには待ち時間がある)。数でない座標と連打は捨てる
            float now = Time.time;
            if (!float.IsFinite(v.at.x) || !float.IsFinite(v.at.y) || now < u._nextWreck) return;
            u._nextWreck = now + 1f;
            u.DropSeen();
            if (v.fate <= (byte)WreckFate.Douse) SpawnWreck(v.at, FlyLift, (WreckFate)v.fate, v.yaw & 15, v.angle);
        });

    private float _nextWreck; // ほかの端末: この人の残骸を次に受ける時刻

    // 本人の端末: 飛んでいるドローンが壊れた
    private void WreckFlying(StrikeResult r, in Strike s)
    {
        if (!_drone) return;
        Vector2 at = _rend ? (Vector2)_rend.transform.position : (Vector2)_drone.transform.position;
        var fate = FateOf(r, s);
        byte yaw = (byte)DroneArt.YawIndex(_yaw), angle = StrikeAngle(s, at.x, at.y);
        float lift = FlyLift + (at.y - _drone.transform.position.y);
        if (ShowFlying) WreckCall.Send((at, (byte)fate, yaw, angle));
        SpawnWreck(at, lift, fate, yaw, angle);
    }

    // 確かめ用: その場所に残骸を出す (この端末だけ・fly = 飛んでいる高さから)
    internal static void DevWreck(WreckFate fate, Vector2 at, bool fly, int yaw, float angleDeg)
    {
        byte angle = (byte)((int)FxMath.Floor(angleDeg * (256f / 360f) + 0.5f) & 255);
        SpawnWreck(at, fly ? FlyLift : IdleLift, fate, yaw & 15, angle);
    }
}
