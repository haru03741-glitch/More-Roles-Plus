using System;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// ハンマーを振る: 本人の色の拳がハンマーを握って振りかぶり、壁に当たった瞬間に崩れる。
//   自分の振り = 押した瞬間から全部を見せる。当たるのは HitAt 秒後。
//     客は押した時に依頼を出し、結果が早く届いても当たる時刻まで適用を待たせる (TerrainSync.HoldOwnHitUntil)。
//     ホストと一人の時は当たる時刻に依頼する (その場で決まって適用される)。
//   人の振り = 結果が届いた (崩れた) 瞬間から、当たった姿 → 跳ね返り → 消える、を見せる。
// 絵は側面から見た振り: 当たる時は柄が壁と平行で、頭の打つ面が壁を向く。
internal static class HammerSwing
{
    public const float HitAt = 0.22f;      // 押してから壁に当たるまで
    private const float Size = 0.22f;      // 拳の絵の倍率 (140 px が 0.31 単位)
    // ハンマーの絵 (Resources/Icons/hammer_held.png・220 px): 握る所 (55, 110)・打つ面の真ん中 (170, 158)。原点 = 握る所
    private const float PropPpu = 60f;
    private const float GripPx = 55f, GripPy = 110f, FacePx = 170f, FacePy = 158f, SpritePx = 220f;
    private const float RaiseDeg = 75f;    // 振りかぶった時の柄の傾き (当たる姿から)
    private const float BounceDeg = 15f;
    private const float PullBack = 0.06f;  // 振りかぶる時に壁から離れる量
    private const float Shake = 0.03f;
    // 拳の絵は指の帯が横に並ぶので、握った柄は縦 (親指の側=上) に通る。ハンマーの絵 (柄が +x) を手の中で 90° 立てる
    private const float GripTurn = 90f;
    private const float AirReach = 0.6f;   // 空振りの時、打つ面を自分からどれだけ先に出すか
    // 壁の当たり判定は壁の絵の根元 (足の高さ) にあるので、見せる時は上げる。横の壁は柄が縦になって拳が打つ面の下に来るので、
    // 拳が胴の高さに来るまで上げる。上の壁は壁の絵の面まで・下の壁は根元の手前が見えている所なので少しだけ
    private const float LiftSide = 0.7f, LiftUp = 0.3f, LiftDown = 0.1f;
    private static readonly float[] FanDeg = { 0f, 30f, -30f, 60f, -60f, 90f, -90f };

    private static DamageEvent _pending;
    private static float _pendingAt;       // ホスト・一人: この時刻に依頼する (0 = 無し)
    private static IntPtr _ship;           // 試合の切り替わりを見る (Unity の == は毎フレーム呼ばない)
    private static string _last = "-";     // 確認用: 最後に見せた振り

    // 自分のハンマーを振る。返り値は結果の説明、ok = 振れた (空振りを含まない)
    // aim = 狙う向きの指定 (テスト用。null = 歩いている向き・向いている左右)
    public static string Swing(float force, out bool ok, Vector2? aim = null)
    {
        ok = false;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.Data == null || lp.Data.IsDead) return new Text("生きている時だけ使えます", "Only while alive");
        if (_pendingAt > 0f) return new Text("振っている途中です", "Already swinging");
        Vector2 at = lp.GetTruePosition();
        Vector2 dirAim = Aim(lp, out bool facingLeft);
        if (aim is { } forced && forced.sqrMagnitude > 1e-6f) { dirAim = forced.normalized; facingLeft = forced.x < 0f; }
        float reach = DamageProfile.Of(DamageKind.Blunt).Reach;
        int color = lp.Data.DefaultOutfit.ColorId;

        if (!FindTarget(at, dirAim, reach, out Vector2 dir, out Vector2 hit, out Vector2 normal))
        {
            Play(at + dirAim * AirReach, dirAim, facingLeft, color, own: true, air: true);
            return new Text("その方向に壁がありません", "No wall that way");
        }

        ok = true;
        Play(hit, -normal, facingLeft, color, own: true, air: false);
        var e = new DamageEvent(DamageKind.Blunt, at, dir, 0f, Math.Clamp(force, 0f, 1f), (ushort)Environment.TickCount);
        if (TerrainSync.IsGuest())
        {
            TerrainSync.HoldOwnHitUntil(Time.time + HitAt);
            TerrainSync.Request(e);
        }
        else
        {
            _pending = e;
            _pendingAt = Time.time + HitAt;
        }
        return new Text("ハンマーで叩いた", "Hammer hit");
    }

    // 地形に結果を適用した直後 (全員の手元)。人の打撃なら、その人の色で当たった姿から見せる
    internal static void OnApplied(in ResolvedDamage r)
    {
        try { OnAppliedCore(r); }
        catch (Exception e) { Plugin.Logger.LogError($"[HammerSwing] {e}"); }
    }

    private static void OnAppliedCore(in ResolvedDamage r)
    {
        if (r.Kind != DamageKind.Blunt || r.Actor == ResolvedDamage.NoActor) return;
        var lp = PlayerControl.LocalPlayer;
        if (lp && lp.PlayerId == r.Actor) return; // 自分の振りは押した時から出ている
        var info = GameData.Instance ? GameData.Instance.GetPlayerById(r.Actor) : null;
        if (info == null) return;
        Vector2 face = -r.Normal;
        Play(r.Position, face, face.x < 0f, info.DefaultOutfit.ColorId, own: false, air: false);
    }

    // 毎フレーム。待っている依頼が無ければ比較 2 つで帰る
    public static void Tick()
    {
        var ship = ShipStatus.Instance;
        IntPtr sp = ReferenceEquals(ship, null) ? IntPtr.Zero : ship.Pointer;
        if (sp != _ship)
        {
            _ship = sp;
            _pendingAt = 0f;
            FxHands.ClearAll();
        }
        if (_pendingAt <= 0f || Time.time < _pendingAt) return;
        _pendingAt = 0f;
        var lp = PlayerControl.LocalPlayer;
        if (!ship || !lp || lp.Data == null || lp.Data.IsDead || MeetingHud.Instance) return;
        TerrainSync.Request(_pending);
    }

    // 狙う向き: 歩いていればその向き、止まっていればクルーが向いている左右
    private static Vector2 Aim(PlayerControl lp, out bool facingLeft)
    {
        facingLeft = lp.cosmetics && lp.cosmetics.FlipX;
        Vector2 v = lp.MyPhysics ? lp.MyPhysics.Velocity : Vector2.zero;
        float len = MathF.Sqrt(v.x * v.x + v.y * v.y);
        if (len > 0.1f)
        {
            if (MathF.Abs(v.x) > 0.1f) facingLeft = v.x < 0f;
            return new Vector2(v.x / len, v.y / len);
        }
        return new Vector2(facingLeft ? -1f : 1f, 0f);
    }

    // 狙う向きから順に左右へ広げて、最初に壁が見つかった角度の中でいちばん近い壁 (ホストも同じ位置と向きで壁を探す)
    private static bool FindTarget(Vector2 at, Vector2 aim, float reach, out Vector2 dir, out Vector2 hit, out Vector2 normal)
    {
        dir = hit = normal = default;
        float best = float.MaxValue;
        float baseRad = MathF.Atan2(aim.y, aim.x);
        for (int i = 0; i < FanDeg.Length; i++)
        {
            float a = baseRad + FanDeg[i] * FxMath.Deg2Rad;
            var d = new Vector2(MathF.Cos(a), MathF.Sin(a));
            if (TerrainDamage.FindWall(at, d, reach, out Vector2 p, out Vector2 n))
            {
                float dx = p.x - at.x, dy = p.y - at.y, dist = dx * dx + dy * dy;
                if (dist < best) { best = dist; dir = d; hit = p; normal = n; }
            }
            // 0° と ±30° のように左右の組を見終わった所で、見つかっていれば止める
            if (best < float.MaxValue && (i == 0 || i % 2 == 0)) return true;
        }
        return best < float.MaxValue;
    }

    // face = 打つ面を当てる点、toward = 打つ面が向く向き。flip = 左向きの絵 (向きが真上・真下の時の左右)
    private static void Play(Vector2 face, Vector2 toward, bool flipHint, int color, bool own, bool air)
    {
        // 奥行きは本編のクルーと同じ数え方 (y/1000)。壁に当てる所なので少し手前
        float z = face.y / 1000f - 0.01f;
        face.y += toward.y > 0.5f ? LiftUp : toward.y < -0.5f ? LiftDown : LiftSide;
        bool flip = MathF.Abs(toward.x) > 0.1f ? toward.x < 0f : flipHint;
        float sx = flip ? -1f : 1f;
        float alpha = MathF.Atan2(toward.y, toward.x) * FxMath.Rad2Deg;
        // 当たる姿: 打つ面 (絵の下の端) が toward を向く = 世界の回転 alpha + 90。左向きは鏡に映した角度で持つ
        float r0 = flip ? (180f - alpha) + 90f : alpha + 90f;
        float w = sx * r0;

        // 握る所 = 打つ面 - (握る所から打つ面への向き) を世界の回転で回した物
        float s = Size / PropPpu;
        float vx = sx * (FacePx - GripPx) * s, vy = -(FacePy - GripPy) * s;
        var off = FxMath.RotateZ(w, vx, vy);
        var grip = new Vector2(face.x - off.x, face.y - off.y);
        float pbx = sx * -toward.x * PullBack, pby = -toward.y * PullBack;
        float shake = air ? 0f : Shake;

        FxHands.Key[] keys = own
            ? new[]
            {
                new FxHands.Key(0.00f, r0 + RaiseDeg * 0.5f, alpha: 0f),
                new FxHands.Key(0.05f, r0 + RaiseDeg * 0.75f),
                new FxHands.Key(0.14f, r0 + RaiseDeg, dx: pbx, dy: pby),
                new FxHands.Key(HitAt, r0, shake: shake),
                new FxHands.Key(0.28f, r0 + BounceDeg),
                new FxHands.Key(0.40f, r0 + BounceDeg * 0.5f),
                new FxHands.Key(0.48f, r0 + BounceDeg * 0.5f, alpha: 0f),
            }
            : new[]
            {
                new FxHands.Key(0.00f, r0, shake: shake),
                new FxHands.Key(0.06f, r0 + BounceDeg),
                new FxHands.Key(0.18f, r0 + BounceDeg * 0.5f),
                new FxHands.Key(0.26f, r0 + BounceDeg * 0.5f, alpha: 0f),
            };
        _last = $"face={face.x:0.00},{face.y:0.00} toward={toward.x:0.00},{toward.y:0.00} grip={grip.x:0.00},{grip.y:0.00} flip={flip} own={own} air={air}";
        // キーは拳の回転。ハンマーは拳から GripTurn 立てて持つので、拳はその分戻す
        for (int i = 0; i < keys.Length; i++) keys[i].Rot -= GripTurn;
        FxHands.Play(keys, grip, color, Size, flip, 0f, z, HammerSprite(), GripTurn);
    }

    private static Sprite HammerSprite() =>
        Roles.ButtonIcons.Load("hammer_held", PropPpu, FxMath.V2(GripPx / SpritePx, 1f - GripPy / SpritePx));

    internal static string Describe() => $"pending={(_pendingAt > 0f ? "yes" : "no")} last[{_last}] {FxHands.Describe()}";
}
