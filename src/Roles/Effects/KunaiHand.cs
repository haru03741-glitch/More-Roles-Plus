using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// クノイチが構えたクナイと、それを握る手 (全員の端末)。持ち主の端末が構えと狙いを配り、ほかの端末は届いた狙いへなめらかに寄せる。
// 投げると拳が狙いへ突き出て戻り、少しして次のクナイを握り直す。体は狙いの側へ向く。
// 透明の間 (透明化・インビジブルなど) は体と同じ濃さ (本人と幽霊には薄く・ほかの人には見えない)・ベント・会議・死んだ人には出さない
internal static class KunaiHand
{
    private const float HandScale = 0.2f;                               // 握った手の絵 (1.4 単位) の倍率
    private const float Squash = 0.58f;                                 // 狙いの縦の成分が画面で縮む倍率 (床を斜め上から見ている)
    private const float HoldOut = 0.2f, HoldSide = 0.2f, HoldUp = 0.1f; // 体の中心から拳まで (狙いの向きへ・右手の側へ・上へ)
    private const float GripToMid = 0.115f;
    private const float KunaiTip = 0.29f;                               // クナイの絵の真ん中から刃先まで                             // 拳からクナイの絵の真ん中まで (クナイに沿って・持ち手が拳に入る)
    private const float TurnTime = 0.06f;                               // 狙いへ寄せる時定数 (秒)
    private const float PunchOut = 0.05f, PunchBack = 0.17f, Reach = 0.22f; // 投げた時に拳が突き出る秒・戻る秒・突き出る長さ
    private const float Regrip = 0.2f, RegripFade = 0.08f;
    private const float RecoilBack = 0.1f; // 弾き飛ばされた時に拳が体の方へ押し戻される長さ              // 投げてから次のクナイが手に現れるまでの秒・現れるのにかかる秒
    private const float BodyToFeet = 0.3636f;
    private const float FaceDead = 0.25f;                               // 狙いの横向き (cos) がこれより小さい = ほぼ真上・真下は左右を変えない (DuelFacing と同じ)                           // 人の体の中心から足元まで

    private sealed class Held
    {
        public PlayerControl P;
        public GameObject Go;
        public Transform HandTf, KunaiTf, LineTf;
        public SpriteRenderer HandSr, KunaiSr, LineSr;
        public float LineLen, Regrip = 0.2f;
        public float Angle, Want, ThrownAt = -99f, NextColor, Alpha = -1f, KunaiAlpha = -1f;
        public int Color = -1, Flip = -1, SprFlip = -1;
        public bool On, Shown, Blocked, Recoil; // Recoil = 弾き飛ばされた (拳は前へでなく後ろへ揺れる)
    }

    private static readonly List<Held> All = new();
    private static int _shipGen = -1;

    private static Held Find(PlayerControl p)
    {
        for (int i = 0; i < All.Count; i++) if (All[i].P == p) return All[i];
        return null;
    }

    // 構える / しまう。angle = 狙い (度・右が 0)。構え始めは今の狙いへすぐ向ける
    internal static void Show(PlayerControl p, bool on, float angle)
    {
        if (!p) return;
        var h = Find(p);
        if (!on)
        {
            if (h != null) h.On = false;
            return;
        }
        if (h == null)
        {
            h = New(p);
            if (h == null) return;
        }
        if (!h.On) h.Angle = angle;
        h.On = true;
        h.Want = angle;
    }

    // blocked = すぐ先が壁で投げられない (持ち主の画面でだけ渡す・クナイを赤くする)
    internal static void Aim(PlayerControl p, float angle, bool blocked = false)
    {
        var h = Find(p);
        if (h == null) return;
        h.Want = angle;
        if (blocked != h.Blocked) { h.Blocked = blocked; h.KunaiAlpha = -1f; }
    }

    // 投げた: 拳を突き出す。クナイが手を離れる所 (画面の座標) を返す。構えていなければ体の前
    // regrip = 次のクナイを握り直すまでの秒 (投げる待ち時間・手に現れたら次を投げられる)
    internal static Vector2 Throw(PlayerControl p, float angle, float regrip)
    {
        var h = Find(p);
        if (h != null && h.On)
        {
            h.Angle = h.Want = angle;
            h.ThrownAt = Time.time;
            h.Regrip = FxMath.Max(Regrip, regrip - RegripFade);
            h.Recoil = false;
        }
        var c = p ? p.transform.position : FxMath.V3(0f, 0f, 0f);
        float r = angle * (System.MathF.PI / 180f), cs = FxMath.Cos(r), sn = FxMath.Sin(r);
        var f = Fist(c.x, c.y, cs, sn, HoldOut + Reach * 0.5f, h != null ? Face(h, cs) : cs < 0f ? -1f : 1f);
        return FxMath.V2(f.x + cs * GripToMid, f.y + sn * GripToMid);
    }

    // 拳の位置: 体の中心から狙いへ out だけ出し、見ている側 (手前) へ寄せる。左右どちらを向いても手前の手で持つ
    // (体の絵は左を向くと裏返るので、狙いから見た右手の側に置くと左向きの時だけ頭の高さの奥に浮く)
    private static Vector2 Fist(float cx, float cy, float cs, float sn, float out_, float face)
    {
        return FxMath.V2(cx + cs * out_ + sn * face * HoldSide * 0.5f,
            cy + sn * out_ * Squash - FxMath.Abs(cs) * HoldSide * Squash + HoldUp);
    }

    // 持ち主の画面だけ: 狙いの線 (クナイの先から len まで・0 以下で消す)。PC もスマホも同じ線で狙う
    internal static void AimLine(PlayerControl p, float len)
    {
        var h = Find(p);
        if (h == null) return;
        if (len > 0f && !h.LineTf)
        {
            var go = FxRoot.New("AimLine", 0, h.Go.transform);
            h.LineTf = go.transform;
            h.LineSr = go.AddComponent<SpriteRenderer>();
            h.LineSr.sprite = LineSprite();
            h.LineSr.color = FxMath.Rgba(1f, 1f, 1f, LineAlpha);
        }
        if (!h.LineTf) return;
        bool on = len > 0f;
        if (on != h.LineLen > 0f) h.LineSr.enabled = on;
        h.LineLen = len;
    }

    private const float LineAlpha = 0.35f, LineWidth = 0.035f, LineArtLen = 16f; // 線の濃さ・太さ・絵の長さ (128×8 px・高さ 1 単位)
    private static Sprite _line;

    // 手前が濃く先へ行くほど薄くなる細い線 (縦は縁をぼかす)
    private static Sprite LineSprite() => _line ? _line : (_line = FxSprites.Rect(128, 8, (x, y) =>
    {
        float along = 1f - (x + 0.5f);
        float edge = 1f - FxMath.Abs(y) * 2f;
        return along * along * edge;
    }, "MrpKunaiAimLine"));

    // 握ったクナイの絵 (場の物の当たりの位置に使う)。構えていなければ null
    internal static Transform KunaiTransform(PlayerControl p) => Find(p) is { On: true } h ? h.KunaiTf : null;

    // 握ったクナイが手に見えている (投げた直後・弾き飛ばされた後の空の手は false)
    internal static bool Holding(PlayerControl p) =>
        Find(p) is { On: true, Shown: true } h && Time.time - h.ThrownAt >= h.Regrip + RegripFade;

    // 握ったクナイを弾き飛ばされた: 手を空にし、seconds 後に握り直す (拳は弾かれた向きへ少し揺れる)
    internal static void Knock(PlayerControl p, float seconds)
    {
        var h = Find(p);
        if (h == null || !h.On) return;
        h.ThrownAt = Time.time;
        h.Regrip = seconds;
        h.Recoil = true;
    }

    // 足元から、投げたクナイが飛ぶ高さ (手の高さ) まで
    internal static float FlyHeight => BodyToFeet + HoldUp;

    private static Held New(PlayerControl p)
    {
        var mat = FxHands.PlayerMat();
        var grip = FxHands.GripSprite();
        var kunai = FloorPropArt.Air("kunai");
        if (!mat || !grip || !kunai) return null;
        if (GameClock.ShipGen != _shipGen) { Forget(); _shipGen = GameClock.ShipGen; }
        var h = new Held { P = p };
        h.Go = FxRoot.New("MrpKunaiHand"); // 演出の根の下なので逆再生に入る
        var hand = FxRoot.New("Hand", 0, h.Go.transform);
        h.HandTf = hand.transform;
        h.HandTf.localScale = FxMath.V3(HandScale, HandScale, 1f);
        h.HandSr = hand.AddComponent<SpriteRenderer>();
        h.HandSr.sharedMaterial = mat;
        h.HandSr.sprite = grip;
        var k = FxRoot.New("Kunai", 0, h.Go.transform);
        h.KunaiTf = k.transform;
        h.KunaiSr = k.AddComponent<SpriteRenderer>();
        h.KunaiSr.sprite = kunai;
        h.Go.SetActive(false);
        All.Add(h);
        return h;
    }

    private static void Forget()
    {
        foreach (var h in All) if (h.Go) Object.Destroy(h.Go);
        All.Clear();
    }

    // 毎フレーム (Plugin の Update から)
    internal static void Tick()
    {
        if (All.Count == 0) return;
        if (GameClock.ShipGen != _shipGen) { Forget(); return; }
        if (RewindPlayback.Active) return; // 逆再生の間は止める (見えるのは録画の逆再生)
        float now = Time.time, dt = Time.deltaTime;
        bool meeting = MeetingHud.Instance;
        for (int i = All.Count - 1; i >= 0; i--)
        {
            var h = All[i];
            if (!h.P || !h.Go)
            {
                if (h.Go) Object.Destroy(h.Go);
                All.RemoveAt(i);
                continue;
            }
            var p = h.P;
            // 吹っ飛び・吸い出し・はしごの間は体の絵が別に動くので、手が宙に残らないよう隠す
            bool show = h.On && !meeting && PortKit.Alive(p) && !p.inVent && p.Visible && !p.onLadder
                && !DuelTumble.Posing(p.PlayerId) && !CrewGrip.Flying(p.PlayerId);
            if (show != h.Shown)
            {
                h.Shown = show;
                h.Go.SetActive(show);
            }
            if (!show) continue;
            Place(h, p, now, dt);
        }
    }

    // 左右どちらを向いているか (-1 = 左)。真上・真下の近くでは前の向きのまま (体の向きと同じく揺れない)
    private static float Face(Held h, float cs)
    {
        if (cs < -FaceDead) h.Flip = 1;
        else if (cs > FaceDead || h.Flip < 0) h.Flip = 0;
        return h.Flip == 1 ? -1f : 1f;
    }

    private static void Place(Held h, PlayerControl p, float now, float dt)
    {
        if (now >= h.NextColor)
        {
            h.NextColor = now + 0.25f;
            int col = GatlingFx.ShownColor(p); // 変身・カモフラ中はその色 (手の色から正体が分からないように)
            if (col != h.Color)
            {
                h.Color = col;
                PlayerMaterial.SetColors(col < 0 || col >= Palette.PlayerColors.Length ? 0 : col, h.HandSr);
            }
        }
        // 狙いへなめらかに寄せる (回る向きは近い方)
        float d = h.Want - h.Angle;
        d -= 360f * System.MathF.Round(d / 360f);
        h.Angle += d * FxMath.Clamp01(dt / TurnTime);
        float r = h.Angle * (System.MathF.PI / 180f), cs = FxMath.Cos(r), sn = FxMath.Sin(r);
        DuelFacing.Hold(p, cs);

        // 投げた後: 拳が狙いへ突き出て (速く)・ゆっくり戻る
        float t = now - h.ThrownAt, punch = 0f;
        if (t < PunchOut) { float q = t / PunchOut; punch = 1f - (1f - q) * (1f - q); }
        else if (t < PunchOut + PunchBack) { float q = (t - PunchOut) / PunchBack; punch = 1f - q * q * (3f - 2f * q); }
        float kunaiAlpha = t < 0f || t >= h.Regrip + RegripFade ? 1f : t < h.Regrip ? 0f : (t - h.Regrip) / RegripFade;

        var c = p.transform.position;
        float face = Face(h, cs);
        var fist = Fist(c.x, c.y, cs, sn, HoldOut + (h.Recoil ? -RecoilBack : Reach) * punch, face);
        float hx = fist.x, hy = fist.y;
        // 真上寄りを狙う時は体の後ろ・それ以外は前。クナイは拳の奥 (持ち手を握っている)
        float hz = c.z + (sn > 0.7f ? 0.0008f : -0.0008f);
        h.HandTf.position = FxMath.V3(hx, hy, hz);
        float ks = Impostor.Kunoichi.SizeScale; // クナイの大きさの設定 (持ち手は拳に入れたまま刃が伸びる)
        float mid = GripToMid * ks;
        h.KunaiTf.position = FxMath.V3(hx + cs * mid, hy + sn * mid, hz + 0.0002f);
        h.KunaiTf.localScale = FxMath.V3(ks, ks, 1f);
        var rotZ = FxMath.RotZ(h.Angle);
        h.KunaiTf.rotation = rotZ;
        if (h.LineLen > 0f)
        {
            // 線はクナイの先から狙いの向きへ (透明の間は体と同じだけ薄く)
            float from = GripToMid * ks + KunaiTip * ks, len = h.LineLen;
            float mx = hx + cs * (from + len * 0.5f), my = hy + sn * (from + len * 0.5f);
            h.LineTf.position = FxMath.V3(mx, my, hz + 0.0003f);
            h.LineTf.rotation = rotZ;
            h.LineTf.localScale = FxMath.V3(len / LineArtLen, LineWidth, 1f);
        }
        // 拳は左右だけ反転し、傾きは ±50° まで (真上・真下を狙っても裏返らない)
        bool left = face < 0f;
        if (h.SprFlip != h.Flip) { h.SprFlip = h.Flip; h.HandSr.flipX = left; }
        float rot = left ? h.Angle - 180f : h.Angle;
        if (rot > 180f) rot -= 360f;
        if (rot < -180f) rot += 360f;
        h.HandTf.rotation = FxMath.RotZ(FxMath.Clamp(rot, -50f, 50f));

        // 透明の間は体と同じ濃さ
        byte id = p.PlayerId;
        float a = FxMath.Min(Cloak.AlphaOf(id), UchuKit.Hidden.AlphaOf(id));
        if (a != h.Alpha)
        {
            h.Alpha = a;
            h.HandSr.color = FxMath.Rgba(1f, 1f, 1f, a);
        }
        float ka = a * kunaiAlpha;
        if (ka != h.KunaiAlpha)
        {
            h.KunaiAlpha = ka;
            h.KunaiSr.color = h.Blocked ? FxMath.Rgba(1f, 0.3f, 0.3f, ka) : FxMath.Rgba(1f, 1f, 1f, ka);
        }
    }
}
