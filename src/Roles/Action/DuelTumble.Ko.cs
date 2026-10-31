using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 吹き飛ばす一撃で倒れた人: 倒れる直前の姿を写した人形を飛ばし、落ちた所へ本編の死体を移す。
// 倒した知らせ (体力 0) は本編のキルより先に届くので、その時に生きている姿を写しておく。
// 死体は本編のキルの演出の途中で出てくるので、出るまで探し、飛んでいる間は隠す。
// 飛ぶ道は各端末が自分の画面の位置から決める (壁で止まる)。死体は落ちた所へ動かす
internal static partial class DuelTumble
{
    private const float KoAir = 0.5f, KoBounce = 0.22f, KoSlide = 0.4f, KoHold = 0.2f;
    private const float KoSpeed = 1.15f;     // 吹っ飛びより少し遠くへ
    private const float KoBodyWait = 2f;     // 本編の死体が出るのを待つ上限 (秒)
    private const float WallLook = 0.45f;    // 壁を探す先 (短い区間は壁を素通りするので先まで見る)
    private const float BodySearchEvery = 0.1f, BodyEager = 0.6f;
    private const int FurnitureLayer = 12;
    // 人が通れない物 = 壁と机などの家具で止める
    private static readonly int StopMask = Constants.ShipAndObjectsMask | 1 << FurnitureLayer;

    private sealed class Ko
    {
        public byte Id;
        public float T0, Dx, Dy, Spin;
        public GameObject Go;          // 人形 (体の中心が原点)
        public Transform Tf;
        public SpriteRenderer Shadow;
        public float Gx, Gy, Z0, Y0;   // 地面の上の体の中心・始めの z と y
        public float Sx, Sy;           // 始めの体の中心
        public float FootDy;           // 体の中心から足元まで
        public bool Stopped, Landed;
        public DeadBody Body;
        public bool BodyHidden;
        public float NextSearch, NextDust;
    }

    private static readonly List<Ko> _kos = new();
    internal static int KoLaunches, KoBodiesMoved, KoNoBody;

    // 全員の端末: 倒れる直前の id を from から吹き飛ばす
    internal static void KoLaunch(byte id, byte from)
    {
        var pc = Player(id);
        if (!pc || pc.Data == null || pc.inVent || !pc.cosmetics) return;
        foreach (var k in _kos)
            if (k.Id == id) return;
        var bodyRef = pc.cosmetics.currentBodySprite;
        var body = bodyRef != null ? bodyRef.BodySprite : null;
        if (!body || !body.enabled) return;
        // 生きている姿で回していた分は戻してから写す
        var st = States[id];
        if (st != null && st.Mode != Mode.None) End(st);

        var b = body.bounds;
        Vector3 c = b.center;
        var ko = new Ko { Id = id, T0 = Time.time, Gx = c.x, Gy = c.y, Sx = c.x, Sy = c.y, Z0 = pc.transform.position.z, Y0 = c.y, FootDy = b.min.y - c.y };
        var src = Player(from);
        float dx = 0f, dy = 0f;
        if (src)
        {
            Vector2 at = src.GetTruePosition(), me = pc.GetTruePosition();
            dx = me.x - at.x;
            dy = me.y - at.y;
        }
        float d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d < 0.01f) { dx = pc.cosmetics.FlipX ? 1f : -1f; dy = 0f; d = 1f; }
        ko.Dx = dx / d;
        ko.Dy = dy / d;
        ko.Spin = ko.Dx >= 0f ? -1f : 1f;
        if (!MakeDoll(ko, pc, body)) return;
        _kos.Add(ko);
        KoLaunches++;
        SamuraiSound.At("noise_samurai_dodge", pc, 0.6f);
    }

    // 体と着せ替え (帽子・バイザー・スキン) の今見えている絵を、ただの絵として写す
    private static bool MakeDoll(Ko ko, PlayerControl pc, SpriteRenderer body)
    {
        int layer = pc.gameObject.layer;
        var go = new GameObject("MrpTumbleDoll") { layer = layer };
        var tf = go.transform;
        tf.position = FxMath.V3(ko.Sx, ko.Sy, ko.Z0);
        int n = Copy(tf, body, layer, true, pc.cosmetics.ColorId);
        foreach (var sr in pc.cosmetics.GetComponentsInChildren<SpriteRenderer>(false))
            n += Copy(tf, sr, layer, false, pc.cosmetics.ColorId);
        if (n == 0) { Object.Destroy(go); return false; }
        ko.Go = go;
        ko.Tf = tf;
        _shadowSprite ??= FxSprites.Rect(32, 32, FxSprites.Soft, "MrpTumbleShadow");
        var sgo = new GameObject("MrpTumbleDollShadow") { layer = layer };
        ko.Shadow = sgo.AddComponent<SpriteRenderer>();
        ko.Shadow.sprite = _shadowSprite;
        return true;
    }

    private static int Copy(Transform parent, SpriteRenderer src, int layer, bool body, int color)
    {
        if (!src || !src.enabled || !src.sprite || !src.gameObject.activeInHierarchy) return 0;
        var go = new GameObject("Part") { layer = layer };
        var t = go.transform;
        t.SetParent(parent, false);
        var st = src.transform;
        t.position = st.position;
        t.rotation = st.rotation;
        t.localScale = st.lossyScale;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = src.sprite;
        sr.flipX = src.flipX;
        sr.flipY = src.flipY;
        sr.sortingOrder = src.sortingOrder;
        sr.color = src.color;
        // 体は本編の死体と同じ材質に色を塗る (生きている体の材質はこの後に幽霊の物へ替わる)
        var mat = body ? FxHands.PlayerMat() : null;
        sr.sharedMaterial = mat ? mat : src.sharedMaterial;
        // 色は本人の色で塗り直す (本編は体と着せ替えの色を描くたびに塗るので、写した材質には残っていない)
        if (color >= 0 && color < Palette.PlayerColors.Length) PlayerMaterial.SetColors(color, sr);
        return 1;
    }

    private static void KoTick()
    {
        float now = Time.time;
        bool stop = MeetingHud.Instance || RewindPlayback.Active;
        float end = KoAir + KoBounce + KoSlide + KoHold;
        for (int i = _kos.Count - 1; i >= 0; i--)
        {
            var ko = _kos[i];
            float t = now - ko.T0;
            // 死体は本編のキルの直後に出るので、始めは毎フレーム探して出た瞬間から隠す
            if (!ko.Body && (t < BodyEager || now >= ko.NextSearch) && t < end + KoBodyWait)
            {
                ko.NextSearch = now + BodySearchEvery;
                ko.Body = FindBody(ko.Id);
            }
            // 死体は落ちるまで隠す (見つけた時に 1 回)
            if (ko.Body && !ko.BodyHidden)
            {
                ShowBody(ko.Body, false);
                ko.BodyHidden = true;
            }
            if (stop || !ko.Go || t >= end && (ko.Body || t >= end + KoBodyWait))
            {
                Finish(ko);
                _kos.RemoveAt(i);
                continue;
            }
            if (t < end) PoseDoll(ko, t, now);
        }
    }

    private static void PoseDoll(Ko ko, float t, float now)
    {
        float sp, ang, h;
        if (t < KoAir)
        {
            float k = t / KoAir;
            sp = (AirSpeed0 + (AirSpeed1 - AirSpeed0) * k) * KoSpeed;
            ang = AirTurn * (1f - (1f - k) * (1f - k));
            h = AirHeight * FxMath.Sin(FxMath.PI * k);
        }
        else if (t < KoAir + KoBounce)
        {
            float k = (t - KoAir) / KoBounce;
            if (!ko.Landed)
            {
                ko.Landed = true;
                DuelHitFx.Dust(ko.Gx, ko.Gy + ko.FootDy, ko.Z0, ko.Go.layer, ko.Dx, ko.Dy, 7, 1f);
            }
            sp = BounceSpeed * KoSpeed;
            ang = AirTurn + Lying * k;
            h = BounceHeight * FxMath.Sin(FxMath.PI * k);
        }
        else if (t < KoAir + KoBounce + KoSlide)
        {
            float k = (t - KoAir - KoBounce) / KoSlide;
            sp = SlideSpeed * KoSpeed * (1f - k);
            ang = AirTurn + Lying + 5f * FxMath.Sin(k * 9f) * (1f - k);
            h = 0f;
            if (now >= ko.NextDust)
            {
                ko.NextDust = now + DustEvery;
                DuelHitFx.Dust(ko.Gx, ko.Gy + ko.FootDy, ko.Z0, ko.Go.layer, ko.Dx, ko.Dy, 1, 0.55f);
            }
        }
        else
        {
            sp = 0f;
            ang = AirTurn + Lying;
            h = 0f;
        }
        if (!ko.Stopped && sp > 0f)
        {
            float step = sp * Time.deltaTime;
            Vector2 from = FxMath.V2(ko.Gx, ko.Gy + ko.FootDy);
            Vector2 look = FxMath.V2(from.x + ko.Dx * (step + WallLook), from.y + ko.Dy * (step + WallLook));
            if (PhysicsHelpers.AnythingBetween(from, look, StopMask, false)) ko.Stopped = true;
            else
            {
                ko.Gx += ko.Dx * step;
                ko.Gy += ko.Dy * step;
            }
        }
        float z = ko.Z0 + (ko.Gy - ko.Y0) / 1000f;
        ko.Tf.position = FxMath.V3(ko.Gx, ko.Gy + h, z);
        ko.Tf.rotation = FxMath.RotZ(ko.Spin * ang);
        float hk = FxMath.Clamp01(h / AirHeight);
        var stf = ko.Shadow.transform;
        stf.position = FxMath.V3(ko.Gx, ko.Gy + ko.FootDy + FootLift, z + 0.002f);
        stf.localScale = FxMath.V3(ShadowW * (1f - 0.35f * hk), ShadowH * (1f - 0.35f * hk), 1f);
        ko.Shadow.color = FxMath.Rgba(0f, 0f, 0f, ShadowA * (1f - 0.55f * hk));
    }

    // 落ちた: 本編の死体を人形と同じだけ動かして出し、人形を消す
    private static void Finish(Ko ko)
    {
        if (ko.Body)
        {
            var bt = ko.Body.transform;
            Vector3 p = bt.position;
            float mx = ko.Gx - ko.Sx, my = ko.Gy - ko.Sy;
            bt.position = FxMath.V3(p.x + mx, p.y + my, p.z + my / 1000f);
            ShowBody(ko.Body, true);
            KoBodiesMoved++;
        }
        else KoNoBody++;
        if (ko.Go) Object.Destroy(ko.Go);
        if (ko.Shadow) Object.Destroy(ko.Shadow.gameObject);
    }

    private static void ShowBody(DeadBody body, bool on)
    {
        var rs = body.bodyRenderers;
        if (rs != null)
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r && r.enabled != on) r.enabled = on;
            }
        var blood = body.bloodSplatter;
        if (blood && blood.enabled != on) blood.enabled = on;
    }

    private static DeadBody FindBody(byte id)
    {
        foreach (var b in Object.FindObjectsOfType<DeadBody>())
            if (b && b.ParentId == id) return b;
        return null;
    }

    private static void KoReset()
    {
        foreach (var ko in _kos)
        {
            if (ko.Body) ShowBody(ko.Body, true);
            if (ko.Go) Object.Destroy(ko.Go);
            if (ko.Shadow) Object.Destroy(ko.Shadow.gameObject);
        }
        _kos.Clear();
    }

    internal static string DescribeKo() => $"ko live={_kos.Count} launches={KoLaunches} moved={KoBodiesMoved} noBody={KoNoBody}";
}
