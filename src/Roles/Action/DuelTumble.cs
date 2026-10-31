using System;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 立ち合いで当たった人の体の崩れ方。
// - のけぞり (ふつうの当たり・弾き合い・口で受けられた): 当てた人と反対へ体が傾き、小さく跳ねて戻る
// - 吹っ飛び (Duel.LastSmash の当たり): 回りながら弧を描いて飛び、着地で跳ねて滑り、横倒しから起き上がる。
//   着地の前後 (TechWindow) に回避のキーで受け身を取ると、転がらずにすぐ起き上がる (受け身は本人が全員へ知らせる)
// 見た目は全員の端末で同じ式から描く (体の絵・着せ替え・名札を回して持ち上げる)。体を動かすのは持ち主の端末だけで、位置は本編の同期で届く
internal static partial class DuelTumble
{
    private enum Mode : byte { None, Recoil, Launch, Roll }

    // 吹っ飛び: 空中・跳ね・滑り・起き上がり (秒)
    private const float Air = 0.45f, Bounce = 0.2f, Slide = 0.35f, Rise = 0.3f;
    private const float TechRise = 0.25f, TechEarly = 0.25f, TechLate = 0.1f;
    private const float AirHeight = 0.9f, BounceHeight = 0.28f, RiseHop = 0.12f, TechHop = 0.3f;
    private const float AirSpeed0 = 6f, AirSpeed1 = 3.5f, BounceSpeed = 2.5f, SlideSpeed = 2.5f;
    private const float AirTurn = 360f, Lying = 90f; // 空中で 1 回り・着地の跳ねで横倒しまで
    // のけぞり
    private const float RecoilTime = 0.35f, RecoilLean = 25f, RecoilHop = 0.12f, RecoilSpeed = 3f, RecoilPush = 0.12f;
    // 回避の転がり: 飛び退く向きへ 1 回りして立つ (体を動かすのは回避の側・ここは見た目だけ)
    private const float RollTime = 0.34f, RollTurn = 360f, RollHop = 0.07f;

    private sealed class State
    {
        public Mode Mode;
        public float T0, Dx = 1f, Dy, Spin = 1f;
        public bool Tech, TechSpent, Landed;
        public PlayerControl Pc;
        public Transform Body, Cos, Names;
        public Vector3 BodyBase, CosBase, NamesBase;
        public float Px, Py, Scale = 1f; // 回す中心 (体の中心・根の手元の座標) と根の倍率
        public bool Moved;               // 部品を動かした (戻す要がある)
        public float FootY;              // 足元 (根の手元の座標)
        public SpriteRenderer Shadow;    // 浮いている間の床の影 (根の子・使い回す)
        public IntPtr ShadowOwner;       // 影を付けた人 (同じ番号に別の人が入ったら作り直す)
        public float NextDust;
    }

    private static readonly State[] States = new State[256];
    private static int _live;
    internal static int Recoils, Launches, Techs, TechMisses, Rolls;

    // ---- 始める ----

    private static bool _hooked;

    internal static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        Duel.OnHit += OnHit;
    }

    private static void OnHit(byte a, byte t, int hp)
    {
        if (a == t) return;
        // 吹き飛ばす一撃で倒れた: 飛んで落ちた所に倒れる。ふつうの当たりで倒れた時は本編のキルの演出のまま
        if (hp <= 0)
        {
            if (Duel.LastSmash) KoLaunch(t, a);
            return;
        }
        if (Duel.LastSmash) Launch(t, a);
        else if (Duel.LastDamage > 0) Recoil(t, a);
    }

    // 当てた人 from と反対へのけぞる (吹っ飛んでいる最中は何もしない)
    internal static void Recoil(byte id, byte from)
    {
        var s = Begin(id, from, Mode.Recoil);
        if (s != null) Recoils++;
    }

    // 回避した人が (dx, dy) の向きへ転がる (全員の端末)
    internal static void Roll(byte id, float dx, float dy)
    {
        var s = Begin(id, 255, Mode.Roll, dx, dy);
        if (s == null) return;
        Rolls++;
        Dust(s, 3, 0.55f);
    }

    internal static void Launch(byte id, byte from)
    {
        var s = Begin(id, from, Mode.Launch);
        if (s == null) return;
        Launches++;
        var pc = s.Pc;
        try { pc.MyPhysics.Animations.PlayIdleAnimation(); }
        catch (Exception e) { Plugin.Logger.LogWarning($"tumble idle: {e.Message}"); }
        SamuraiSound.At("noise_samurai_dodge", pc, 0.55f);
    }

    // dirX/dirY = 向きを渡す時 (0, 0 なら from から離れる向き)
    private static State Begin(byte id, byte from, Mode mode, float dirX = 0f, float dirY = 0f)
    {
        var pc = Player(id);
        if (!pc || pc.Data == null || pc.Data.IsDead || pc.inVent || pc.onLadder || pc.inMovingPlat || !pc.cosmetics) return null;
        var s = States[id] ??= new State();
        if (s.Mode == Mode.Launch && mode != Mode.Launch) return null;
        if (s.Mode == Mode.None) _live++;
        else Restore(s);
        var src = Player(from);
        Vector2 me = pc.GetTruePosition();
        float dx = dirX, dy = dirY;
        if (dx == 0f && dy == 0f && src)
        {
            Vector2 at = src.GetTruePosition();
            dx = me.x - at.x;
            dy = me.y - at.y;
        }
        float d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d < 0.01f) { dx = pc.cosmetics.FlipX ? 1f : -1f; dy = 0f; d = 1f; }
        s.Dx = dx / d;
        s.Dy = dy / d;
        // 右へ飛ぶ人は時計回り (頭から先に倒れる)
        s.Spin = s.Dx >= 0f ? -1f : 1f;
        s.Mode = mode;
        s.T0 = Time.time;
        s.Tech = s.TechSpent = s.Landed = false;
        s.Pc = pc;
        if (s.Shadow && s.ShadowOwner != pc.Pointer)
        {
            UnityEngine.Object.Destroy(s.Shadow.gameObject);
            s.Shadow = null;
        }
        Grab(s, pc);
        return s;
    }

    // 回す部品と、回す中心 (体の絵の中心) を控える
    private static void Grab(State s, PlayerControl pc)
    {
        var root = pc.transform;
        s.Body = root.Find("BodyForms");
        s.Cos = pc.cosmetics.transform;
        s.Names = root.Find("Names");
        if (s.Body) s.BodyBase = s.Body.localPosition;
        s.CosBase = s.Cos.localPosition;
        if (s.Names) s.NamesBase = s.Names.localPosition;
        float sc = root.localScale.y;
        s.Scale = sc > 0.01f ? sc : 1f;
        var bodyRef = pc.cosmetics.currentBodySprite;
        var body = bodyRef != null ? bodyRef.BodySprite : null;
        if (body)
        {
            var b = body.bounds;
            Vector3 c = b.center, r = root.position;
            s.Px = (c.x - r.x) / s.Scale;
            s.Py = (c.y - r.y) / s.Scale;
            s.FootY = (b.min.y - r.y) / s.Scale + FootLift;
        }
        else { s.Px = s.Py = 0f; s.FootY = -0.5f; }
        s.Moved = true;
    }

    // ---- 受け身 ----

    // 持ち主の端末: 回避のキーが押された。吹っ飛んでいる間なら受け身を試す (早すぎる押しはこの吹っ飛びの受け身を失う)
    internal static bool TryTech(PlayerControl pc)
    {
        var s = States[pc.PlayerId];
        if (s == null || s.Mode != Mode.Launch || s.Tech || s.TechSpent) return s != null && s.Mode == Mode.Launch;
        float t = Time.time - s.T0;
        if (t < Air - TechEarly || t > Air + TechLate)
        {
            if (t < Air - TechEarly) { s.TechSpent = true; TechMisses++; }
            return true;
        }
        TechCall.Send(pc.PlayerId);
        ApplyTech(pc.PlayerId);
        return true;
    }

    // 受け身を取った (全員の端末で)。今が着地より前なら着地まで待ってから起き上がる
    private static void ApplyTech(byte id)
    {
        var s = States[id];
        if (s == null || s.Mode != Mode.Launch || s.Tech) return;
        s.Tech = true;
        Techs++;
        if (s.Pc) SamuraiSound.At("noise_samurai_dash", s.Pc, 0.5f);
    }

    private static readonly RemoteCall<byte> TechCall = new("Duel.Tech", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (sender, id) =>
        {
            // 本人か、ホスト (ホストが動かす練習の相手) からだけ受ける
            if (!sender || sender.PlayerId != id && !Rpc.FromHost(sender)) return;
            ApplyTech(id);
        });

    // ---- 毎フレーム ----

    // 吹っ飛んでいる (歩きのアニメと向きの変更・攻撃と回避を止める)
    internal static bool Busy(byte id) => _live > 0 && States[id] is { Mode: Mode.Launch };

    internal static bool AnyLive => _live > 0;

    private static float LaunchLen(State s) => Air + (s.Tech ? TechRise : Bounce + Slide + Rise);

    // 持ち主の端末: 吹っ飛び・のけぞりの間の体の速さ
    internal static bool Velocity(byte id, out Vector2 v)
    {
        v = default;
        if (_live == 0) return false;
        var s = States[id];
        if (s == null || s.Mode == Mode.None) return false;
        float t = Time.time - s.T0, sp;
        if (s.Mode == Mode.Recoil)
        {
            if (t >= RecoilPush) return false;
            sp = RecoilSpeed;
        }
        else if (t < Air) sp = AirSpeed0 + (AirSpeed1 - AirSpeed0) * (t / Air);
        else if (s.Tech) sp = 0f;
        else if (t < Air + Bounce) sp = BounceSpeed;
        else if (t < Air + Bounce + Slide) sp = SlideSpeed * (1f - (t - Air - Bounce) / Slide);
        else sp = 0f;
        v = FxMath.V2(s.Dx * sp, s.Dy * sp);
        return true;
    }

    internal static void Tick()
    {
        if (_kos.Count > 0) KoTick();
        if (_live == 0) return;
        float now = Time.time;
        bool stop = MeetingHud.Instance || RewindPlayback.Active;
        for (int i = 0; i < States.Length; i++)
        {
            var s = States[i];
            if (s == null || s.Mode == Mode.None) continue;
            var pc = s.Pc;
            float t = now - s.T0;
            float len = s.Mode == Mode.Recoil ? RecoilTime : s.Mode == Mode.Roll ? RollTime : LaunchLen(s);
            if (stop || t >= len || !pc || pc.Data == null || pc.Data.IsDead || pc.inVent)
            {
                End(s);
                continue;
            }
            if (s.Mode == Mode.Recoil) PoseRecoil(s, t);
            else if (s.Mode == Mode.Roll) PoseRoll(s, t);
            else
            {
                if (!s.Tech && !s.TechSpent && t >= Air - BotTechLead) BotTech(s);
                PoseLaunch(s, t);
            }
        }
    }

    private static void PoseRecoil(State s, float t)
    {
        float k = FxMath.Sin(FxMath.PI * t / RecoilTime);
        // 当てた人と反対へ倒れる = 右へ押されたら時計回り
        Pose(s, s.Spin * RecoilLean * k, RecoilHop * k);
    }

    private static void PoseRoll(State s, float t)
    {
        float k = t / RollTime;
        // 踏み切りで速く回り、立つ前に緩む
        float e = k * k * (3f - 2f * k);
        Pose(s, s.Spin * RollTurn * e, RollHop * FxMath.Sin(FxMath.PI * k));
        if (!s.Landed && k >= 0.8f)
        {
            s.Landed = true;
            Dust(s, 2, 0.45f);
        }
    }

    private static void PoseLaunch(State s, float t)
    {
        float ang, h;
        if (t < Air)
        {
            float k = t / Air;
            // 速く回り出して着地の前に緩む
            ang = AirTurn * (1f - (1f - k) * (1f - k));
            h = AirHeight * FxMath.Sin(FxMath.PI * k);
        }
        else if (s.Tech)
        {
            float k = (t - Air) / TechRise;
            if (!s.Landed) Land(s, true);
            // 受け身: 跳ね上がりながらもう半回りして足から立つ
            ang = AirTurn + 360f * (1f - (1f - k) * (1f - k));
            h = TechHop * FxMath.Sin(FxMath.PI * k);
        }
        else if (t < Air + Bounce)
        {
            float k = (t - Air) / Bounce;
            if (!s.Landed) Land(s, false);
            ang = AirTurn + Lying * k;
            h = BounceHeight * FxMath.Sin(FxMath.PI * k);
        }
        else if (t < Air + Bounce + Slide)
        {
            float k = (t - Air - Bounce) / Slide;
            // 横倒しのまま滑り、止まりかけで少し揺れる
            ang = AirTurn + Lying + 6f * FxMath.Sin(k * 9f) * (1f - k);
            h = 0f;
        }
        else
        {
            float k = (t - Air - Bounce - Slide) / Rise;
            // 起き上がり: 横倒しから戻りつつ小さく跳ねる
            float e = 1f - (1f - k) * (1f - k);
            ang = AirTurn + Lying * (1f - e);
            h = RiseHop * FxMath.Sin(FxMath.PI * k);
        }
        Pose(s, s.Spin * ang, h);
        PoseShadow(s, h);
        // 横倒しで滑っている間は擦った跡の土ぼこり
        float now = Time.time;
        if (!s.Tech && t >= Air + Bounce && t < Air + Bounce + Slide && now >= s.NextDust)
        {
            s.NextDust = now + DustEvery;
            Dust(s, 1, 0.55f);
        }
    }

    private const float BotTechLead = 0.08f;

    // ホスト: 練習の相手は強さに応じた割合で受け身を取る (1 回の吹っ飛びに 1 回だけ振る)
    private static void BotTech(State s)
    {
        s.TechSpent = true;
        if (!s.Pc || !AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;
        float chance = DuelBot.TechChance(s.Pc.PlayerId);
        if (chance <= 0f || FxMath.Range(0f, 1f) >= chance) return;
        TechCall.Send(s.Pc.PlayerId);
        ApplyTech(s.Pc.PlayerId);
    }

    private const float FootLift = 0.08f;                     // 影の中心を足の絵の下端から少し上に
    private const float ShadowW = 0.75f, ShadowH = 0.26f, ShadowA = 0.38f;
    private const float DustEvery = 0.08f;

    private static Sprite _shadowSprite;

    // 床の影: 高く上がるほど小さく薄く
    private static void PoseShadow(State s, float h)
    {
        if (!s.Shadow)
        {
            _shadowSprite ??= FxSprites.Rect(32, 32, FxSprites.Soft, "MrpTumbleShadow");
            var go = new GameObject("MrpTumbleShadow") { layer = s.Pc.gameObject.layer };
            go.transform.SetParent(s.Pc.transform, false);
            s.Shadow = go.AddComponent<SpriteRenderer>();
            s.Shadow.sprite = _shadowSprite;
            s.ShadowOwner = s.Pc.Pointer;
        }
        float k = FxMath.Clamp01(h / AirHeight);
        var tf = s.Shadow.transform;
        tf.localPosition = FxMath.V3(s.Px, s.FootY, 0.002f);
        float sc = 1f / s.Scale;
        tf.localScale = FxMath.V3(ShadowW * (1f - 0.35f * k) * sc, ShadowH * (1f - 0.35f * k) * sc, 1f);
        s.Shadow.color = FxMath.Rgba(0f, 0f, 0f, ShadowA * (1f - 0.55f * k));
        if (!s.Shadow.enabled) s.Shadow.enabled = true;
    }

    // 足元の土ぼこり (n 個・大きさ size)
    private static void Dust(State s, int n, float size)
    {
        var pc = s.Pc;
        if (!pc) return;
        Vector3 r = pc.transform.position;
        DuelHitFx.Dust(r.x + s.Px * s.Scale, r.y + s.FootY * s.Scale, r.z + 0.001f, pc.gameObject.layer, s.Dx, s.Dy, n, size);
    }

    private static void Land(State s, bool tech)
    {
        s.Landed = true;
        if (!s.Pc) return;
        Dust(s, tech ? 3 : 6, tech ? 0.6f : 0.9f);
        SamuraiSound.At(tech ? "noise_samurai_dodge" : "noise_samurai_dash", s.Pc, tech ? 0.4f : 0.6f);
    }

    // 体の中心のまわりに ang 度回し、h (世界の単位) だけ持ち上げる。名札は回さず持ち上げるだけ
    private static void Pose(State s, float ang, float h)
    {
        float lift = h / s.Scale;
        var off = FxMath.RotateZ(ang, s.Px, s.Py);
        float ox = s.Px - off.x, oy = s.Py - off.y + lift;
        var rot = FxMath.RotZ(ang);
        if (s.Body)
        {
            s.Body.localPosition = FxMath.V3(s.BodyBase.x + ox, s.BodyBase.y + oy, s.BodyBase.z);
            s.Body.localRotation = rot;
        }
        if (s.Cos)
        {
            s.Cos.localPosition = FxMath.V3(s.CosBase.x + ox, s.CosBase.y + oy, s.CosBase.z);
            s.Cos.localRotation = rot;
        }
        if (s.Names) s.Names.localPosition = FxMath.V3(s.NamesBase.x, s.NamesBase.y + lift, s.NamesBase.z);
    }

    private static void End(State s)
    {
        Restore(s);
        s.Mode = Mode.None;
        s.Pc = null;
        _live--;
    }

    private static void Restore(State s)
    {
        if (s.Shadow) s.Shadow.enabled = false;
        if (!s.Moved) return;
        s.Moved = false;
        if (s.Body)
        {
            s.Body.localPosition = s.BodyBase;
            s.Body.localRotation = FxMath.RotZ(0f);
        }
        if (s.Cos)
        {
            s.Cos.localPosition = s.CosBase;
            s.Cos.localRotation = FxMath.RotZ(0f);
        }
        if (s.Names) s.Names.localPosition = s.NamesBase;
    }

    // 試合の終わり・ロビーで全部やめる
    internal static void Reset()
    {
        KoReset();
        for (int i = 0; i < States.Length; i++)
        {
            var s = States[i];
            if (s == null || s.Mode == Mode.None) continue;
            End(s);
        }
        _live = 0;
    }

    private static PlayerControl Player(byte id)
    {
        if (id == 255 || !GameData.Instance) return null;
        return GameData.Instance.GetPlayerById(id)?.Object;
    }

    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder($"tumble live={_live} rolls={Rolls} recoils={Recoils} launches={Launches} techs={Techs} techMiss={TechMisses}");
        for (int i = 0; i < States.Length; i++)
        {
            var s = States[i];
            if (s == null || s.Mode == Mode.None) continue;
            sb.Append($" [{i} {s.Mode} t={Time.time - s.T0:0.00} tech={s.Tech} dir={s.Dx:0.0},{s.Dy:0.0}]");
        }
        return sb.ToString();
    }
}

// 吹っ飛んでいる間は本編の歩きのアニメを止める (体は飛ばされているだけで歩いていない)
[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.HandleAnimation))]
internal static class DuelTumbleAnimPatch
{
    [HarmonyPriority(Priority.First)]
    public static bool Prefix(PlayerPhysics __instance)
    {
        if (!DuelTumble.AnyLive) return true;
        var pc = __instance.myPlayer;
        return !(pc && DuelTumble.Busy(pc.PlayerId));
    }
}
