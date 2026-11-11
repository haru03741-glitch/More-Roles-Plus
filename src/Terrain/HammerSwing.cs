using System;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
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
    private static byte _fixtureOwner, _fixtureDesk;
    private static bool _fixtureIsDesk;
    private static float _fixtureAt;       // ポータルの装置か設計台を叩いた: この時刻にホストへ伝える (0 = 無し)
    private static float _bodyAt;          // 場に置かれた物 (ドローン等) を叩いた: この時刻に当てて全員へ伝える (0 = 無し)
    private static Vector2 _bodyHit, _bodyDir;
    private const float BodyPad = 0.4f;
    private static readonly float[] BodyNext = new float[256]; // 送り主ごとの、次に受ける時刻
    private static int _shipGen;           // 試合の切り替わり (GameClock.ShipGen)
    private static string _last = "-";     // 確認用: 最後に見せた振り

    // 自分のハンマーを振る。返り値は結果の説明、ok = 振れた (空振りを含まない)
    // aim = 狙う向きの指定 (テスト用。null = 歩いている向き・向いている左右)
    public static string Swing(float force, out bool ok, Vector2? aim = null)
    {
        ok = false;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.Data == null || lp.Data.IsDead) return new Text("HammerSwing.OnlyWhileAlive");
        if (_pendingAt > 0f || _fixtureAt > 0f || _bodyAt > 0f) return new Text("HammerSwing.AlreadySwinging");
        Vector2 at = lp.GetTruePosition();
        Vector2 dirAim = Aim(lp, out bool facingLeft);
        if (aim is { } forced && forced.sqrMagnitude > 1e-6f) { dirAim = forced.normalized; facingLeft = forced.x < 0f; }
        float reach = DamageProfile.Of(DamageKind.Blunt).Reach;
        int color = Roles.GatlingFx.ShownColor(lp);

        bool wall = FindTarget(at, dirAim, reach, out Vector2 dir, out Vector2 hit, out Vector2 normal);
        float wallDist = wall ? MathF.Sqrt((hit.x - at.x) * (hit.x - at.x) + (hit.y - at.y) * (hit.y - at.y)) : reach;
        // 壁より手前にポータルの装置か設計台があれば、近い方を叩く (回数はホストが数える)
        bool portal = Roles.Crew.PortalMaker.HammerTarget(at, dirAim, wallDist, out byte owner, out Vector2 face, out Vector2 toward);
        float portalDist = portal ? MathF.Sqrt((face.x - at.x) * (face.x - at.x) + (face.y - at.y) * (face.y - at.y)) : wallDist;
        if (Roles.Impostor.Architect.HammerTarget(at, dirAim, portalDist, out byte deskOwner, out byte desk, out Vector2 deskFace, out Vector2 deskToward))
        {
            ok = true;
            Play(deskFace, deskToward, facingLeft, color, own: true, air: false);
            _fixtureOwner = deskOwner;
            _fixtureDesk = desk;
            _fixtureIsDesk = true;
            _fixtureAt = Time.time + HitAt;
            return new Text("HammerSwing.HitDesk");
        }
        if (portal)
        {
            ok = true;
            Play(face, toward, facingLeft, color, own: true, air: false);
            _fixtureOwner = owner;
            _fixtureIsDesk = false;
            _fixtureAt = Time.time + HitAt;
            return new Text("HammerSwing.HitPortal");
        }
        // 壁より手前に場に置かれた物 (止まったドローン等) があれば、それを叩く (壊れるかは物を決める端末が決める)
        // 足元の高さで振るので、浮いている物 (胴の真ん中で当たりを持つ) にも届くよう少し広く見る
        if (Roles.Bodies.Count > 0 && Roles.Bodies.Probe(at, dirAim, wallDist, BodyPad, out Vector2 bodyFace, out Vector2 bodyCenter))
        {
            ok = true;
            Play(bodyFace, dirAim, facingLeft, color, own: true, air: false);
            _bodyHit = bodyCenter;
            _bodyDir = dirAim;
            _bodyAt = Time.time + HitAt;
            return new Text("HammerSwing.HammerHit");
        }
        if (!wall)
        {
            Play(at + dirAim * AirReach, dirAim, facingLeft, color, own: true, air: true);
            return new Text("HammerSwing.NoWallThatWay");
        }

        var e = new DamageEvent(DamageKind.Blunt, at, dir, 0f, Math.Clamp(force, 0f, 1f), (ushort)Environment.TickCount);
        // 外壁はホストが断る。叩く姿だけ見せて回数と待ち時間は使わない (形は全員の端末で同じなので手元で同じ確かめができる)
        if (!TerrainDamage.TryResolve(e, out _, out string why) && why == TerrainDamage.OuterWallWhy)
        {
            Play(hit, -normal, facingLeft, color, own: true, air: false);
            return new Text("HammerSwing.OuterWall");
        }
        ok = true;
        Play(hit, -normal, facingLeft, color, own: true, air: false);
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
        return new Text("HammerSwing.HammerHit");
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
        // リモコンに操られている人の打撃は拳で見せている (ハンマーを持っていない)
        if (Roles.Impostor.RemoteController.Controlled.Contains(r.Actor)) return;
        var lp = PlayerControl.LocalPlayer;
        if (lp && lp.PlayerId == r.Actor) return; // 自分の振りは押した時から出ている
        var info = GameData.Instance ? GameData.Instance.GetPlayerById(r.Actor) : null;
        if (info == null) return;
        // 透明な人の振りは見せない (手で居場所と色が分かる)。手の色は見えている服の色
        if (info.Object && !info.Object.Visible) return;
        Vector2 face = -r.Normal;
        Play(r.Position, face, face.x < 0f, info.Object ? Roles.GatlingFx.ShownColor(info.Object) : info.DefaultOutfit.ColorId, own: false, air: false);
    }

    // 毎フレーム。待っている依頼が無ければ比較 2 つで帰る
    public static void Tick()
    {
        if (GameClock.ShipGen != _shipGen)
        {
            _shipGen = GameClock.ShipGen;
            _pendingAt = 0f;
            _fixtureAt = 0f;
            _bodyAt = 0f;
            FxHands.ClearAll();
        }
        if (_bodyAt > 0f && Time.time >= _bodyAt)
        {
            _bodyAt = 0f;
            var me = PlayerControl.LocalPlayer;
            if (GameClock.ShipAlive && me && me.Data != null && !me.Data.IsDead && !MeetingHud.Instance)
            {
                HitBody(me.PlayerId, _bodyHit, _bodyDir);
                BodyCall.Send((_bodyHit, _bodyDir));
            }
        }
        if (_fixtureAt > 0f && Time.time >= _fixtureAt)
        {
            _fixtureAt = 0f;
            var me = PlayerControl.LocalPlayer;
            if (GameClock.ShipAlive && me && me.Data != null && !me.Data.IsDead && !MeetingHud.Instance)
            {
                if (_fixtureIsDesk) Roles.Impostor.Architect.SendHammer(_fixtureOwner, _fixtureDesk);
                else Roles.Crew.PortalMaker.SendHammer(_fixtureOwner);
            }
        }
        if (_pendingAt <= 0f || Time.time < _pendingAt) return;
        _pendingAt = 0f;
        var lp = PlayerControl.LocalPlayer;
        if (!GameClock.ShipAlive || !lp || lp.Data == null || lp.Data.IsDead || MeetingHud.Instance) return;
        TerrainSync.Request(_pending);
    }

    private static void HitBody(byte by, Vector2 at, Vector2 dir) =>
        Roles.Bodies.Circle(new Roles.Strike(Roles.StrikeElement.Blunt, Roles.Bodies.BluntPower, 0, at.x, at.y, dir.x, dir.y, by), 0.1f);

    // ほかの人が場に置かれた物を叩いた (全員の端末): 叩いた姿を見せ、同じ一撃を当てる (物を決める端末で耐久が減る)
    private static readonly RemoteCall<(Vector2 at, Vector2 dir)> BodyCall = new("HammerSwing.Body", Route.Anyone,
        (w, v) => { w.Write(v.at.x); w.Write(v.at.y); w.Write(v.dir.x); w.Write(v.dir.y); },
        r => (new Vector2(r.ReadSingle(), r.ReadSingle()), new Vector2(r.ReadSingle(), r.ReadSingle())),
        (sender, v) =>
        {
            if (!sender || sender.AmOwner || sender.Data == null || sender.Data.IsDead || !GameClock.ShipAlive || MeetingHud.Instance) return;
            // 1 回の振りより速い連打は捨てる
            float now = Time.time;
            int id = sender.PlayerId & 255;
            if (now < BodyNext[id]) return;
            BodyNext[id] = now + HitAt * 0.8f;
            // 叩いた人の手の届く所だけ (離れた所・数でない座標の送りつけは捨てる)。手元で拾う広さ (半径 + BodyPad) に合わせる
            Vector2 p = sender.GetTruePosition();
            float dx = v.at.x - p.x, dy = v.at.y - p.y, reach = DamageProfile.Of(DamageKind.Blunt).Reach + BodyPad + 0.4f;
            if (!(dx * dx + dy * dy <= reach * reach) || !float.IsFinite(v.dir.x) || !float.IsFinite(v.dir.y)) return;
            float dl = MathF.Sqrt(v.dir.x * v.dir.x + v.dir.y * v.dir.y);
            Vector2 dir = dl > 1e-4f ? new Vector2(v.dir.x / dl, v.dir.y / dl) : new Vector2(1f, 0f);
            if (sender.Visible) PlayOther(v.at - dir * 0.2f, dir, Roles.GatlingFx.ShownColor(sender));
            HitBody(sender.PlayerId, v.at, dir);
        });

    // 他の人がポータルの装置を叩いた姿 (その人の色で当たった姿から)
    internal static void PlayOther(Vector2 face, Vector2 toward, int color) => Play(face, toward, toward.x < 0f, color, own: false, air: false);

    // 狙う向き: 歩いていればその向き、止まっていればクルーが向いている左右
    internal static Vector2 Aim(PlayerControl lp, out bool facingLeft)
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
    internal static bool FindTarget(Vector2 at, Vector2 aim, float reach, out Vector2 dir, out Vector2 hit, out Vector2 normal)
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
            // 物ごと壊れる家具と仕掛け壁は壁の探し方では見えないので別に見る (手前にあればそちらを叩く)
            if (BreakableProps.Probe(at, d, reach, out Vector2 pp, out Vector2 pn))
            {
                float dx = pp.x - at.x, dy = pp.y - at.y, dist = dx * dx + dy * dy;
                if (dist < best) { best = dist; dir = d; hit = pp; normal = pn; }
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
