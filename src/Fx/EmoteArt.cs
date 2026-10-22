using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Fx;

// エモートの見た目: 本編の手と同じ様式 (3 本指 + 親指・赤=本体色/青=影色のひな形) で描いたエモート用の手の絵
// (Resources/Icons/emote_*.png・tools/make-emote-hands.py) を本人の色で塗り、人の子に置いて一緒に動かす。
// 手を振る (開いた手)・跳ねる (握りこぶし)・拍手 (横から見た手)・手招き (手のひらを上へ・指を曲げる 3 段) の 4 種。跳ねるは体の絵 (BodyForms) と帽子・服 (cosmetics) の根を同じだけ上げ下げし、終わりに元へ戻す。
// 描くのは手元の画面だけ。毎フレーム触るのは出している人だけで、出ていない時は件数の比較だけで帰る。算術は FxMath
internal static class EmoteArt
{
    internal const byte Wave = 0, Jump = 1, Clap = 2, Beckon = 3, Count = 4;

    private static readonly float[] Durations = { 1.6f, 1.2f, 1.6f, 1.6f };
    private static readonly string[] SpriteNames = { "emote_open", "emote_fist", "emote_side", "emote_beckon0", "emote_beckon1", "emote_beckon2" };
    private const int Open = 0, Fist = 1, Side = 2, Beckon0 = 3;
    // 絵の中心から手首までのずらし (持ち手 = 手首に置いて手首を軸に回す)。絵は 160 px・1 単位 = 100 px
    private static readonly Vector2[] Wrists = { new(0f, 0.6f), new(0f, 0.6f), new(0.02f, 0.6f), new(0.46f, 0.2f), new(0.46f, 0.2f), new(0.46f, 0.2f) };
    private static readonly int[] BeckonCycle = { 0, 1, 2, 1 }; // 手招きの指の曲げ (伸ばす → 曲げきる → 戻す)

    private const float HandSize = 0.3f;    // 手の絵 (指先から手首まで約 1.2 単位) の倍率
    private const float FistSize = 0.26f;   // 握りこぶしは開いた手より幅がある
    private const float ClapSize = 0.24f;   // 体の前で打つ手は体を隠しすぎない大きさに
    private const float HandZ = -0.002f;    // 体の手前
    private const float Fade = 0.15f;       // 出入りの秒
    private const int CheckEvery = 6;       // 見えるか・生きているかを見直す間隔 (フレーム)

    private sealed class Show
    {
        public PlayerControl Pc;
        public byte Id, Kind;
        public float Start, Dur, Inv;
        public Transform Root, Body, Cos;
        public Vector3 Bp, Cp;
        public bool Hop;
        public GameObject A, B;
        public Transform At, Bt, Ac, Bc; // 持ち手 (手首の位置・回転・鏡) と、手首を持ち手に合わせた絵
        public SpriteRenderer Ar, Br;
        public int Asp = -1, Bsp = -1;   // 今の絵の番号
        public bool Hidden, Flip;
        public float Hold = -1f;         // 確認用: 0 以上ならこの時点の姿で止める
        public int Cue = -1;             // 最後に音を鳴らした拍の番号 (替わった時だけ鳴らす)
        public float Pitch;              // 人ごとに少し違う高さ (何人かが同時に拍手しても 1 つの音に重ならない)
        // 拍手の打った瞬間の効果 (閃きと飛び散る線)。打った回の番号が替わった時だけ角度を振り直す
        public GameObject Fx;
        public Transform Glow, Core;
        public Transform[] Lines;
        public SpriteRenderer GlowR, CoreR;
        public SpriteRenderer[] LineR;
        public float[] Dx, Dy;           // 線の飛ぶ向き
        public int Hit = -1;
        public bool FxOn, FlashOn;       // FlashOn = 閃きがまだ見えている (消えきったら書かない)
    }

    // 拍手: 秒 3 回打つ。打つ瞬間に手が一番速く当たり (|sin| の谷)、跳ね返るように離れる
    private const float ClapRate = 3f, ClapFirst = 1f / 6f, ClapGap = 0.12f;
    private const float ClapX = 0f, ClapY = -0.03f;   // 両手の指が合わさる所 (体の中心から)
    private const float FlashTime = 0.14f, LineTime = 0.24f;
    private static readonly float[] LineAngles = { 28f, 68f, 112f, 152f }; // 上へ開く 4 本 (度)
    private const float LineFrom = 0.17f, LineTo = 0.38f;  // 線の中心が合わさる所から離れる長さ
    private const float LineLen = 0.2f, LineThick = 0.06f;
    private static Sprite _glowSprite, _lineSprite;

    private static readonly List<Show> Live = new();
    private static readonly Sprite[] Sprites = new Sprite[SpriteNames.Length];
    private static int _frame, _shipGen = -1;
    private static bool _loaded;

    // 確認用の数
    internal static int Played, Refused, Sounds;
    private static string _lastSound = "-";

    internal static float Duration(byte kind) => kind < Count ? Durations[kind] : 0f;

    internal static bool IsPlaying(byte id)
    {
        foreach (var s in Live) if (s.Id == id) return true;
        return false;
    }

    private static int IconSprite(byte kind) => kind switch { Wave => Open, Jump => Fist, Clap => Side, _ => Beckon0 + 2 };

    // 各絵の手が描かれている所の中心 (絵の中心から・単位)。絵ごとに手の寄り方が違うので、ボタンでは手の中心を揃える
    private static readonly Vector2[] IconCenters = { new(-0.09f, -0.01f), new(-0.035f, -0.215f), new(0.105f, -0.02f), new(-0.10f, 0f) };

    // ボタンのアイコン (原点は絵の中心)
    internal static Sprite Icon(byte kind) => LoadSprites() ? Sprites[IconSprite(kind)] : null;

    internal static Vector2 IconCenter(byte kind) => kind < Count ? IconCenters[kind] : default;

    // pc にエモートを出す (同じ人の前のエモートは打ち切る)
    internal static bool Play(PlayerControl pc, byte kind) => Play(pc, kind, -1f);

    private static bool Play(PlayerControl pc, byte kind, float hold)
    {
        if (!pc || kind >= Count || !LoadSprites()) { Refused++; return false; }
        Material mat = FxHands.PlayerMat();
        if (!mat) { Refused++; return false; }
        Stop(pc.PlayerId);
        // 出している物が無い間は船の番号を見ていないので、ここで今の船に合わせる (合わせないと最初の Tick で消える)
        if (Live.Count == 0) _shipGen = Terrain.GameClock.ShipGen;

        var s = new Show
        {
            Pc = pc,
            Id = pc.PlayerId,
            Kind = kind,
            Hold = hold,
            Start = Time.time,
            Dur = Durations[kind],
            Root = pc.transform,
            Pitch = 0.94f + pc.PlayerId % 5 * 0.03f,
        };
        float scale = s.Root.localScale.x;
        s.Inv = scale > 0.01f ? 1f / scale : 1f;
        int color = pc.Data != null ? pc.Data.DefaultOutfit.ColorId : 0;
        int layer = pc.gameObject.layer;
        s.A = NewHand(s.Root, mat, color, layer, out s.At, out s.Ac, out s.Ar);
        if (kind == Clap || kind == Jump) s.B = NewHand(s.Root, mat, color, layer, out s.Bt, out s.Bc, out s.Br);
        if (kind == Clap) NewClapFx(s, layer);

        // 体の根を他の演出 (流れに踏ん張る・吸い出し) が動かしている間は跳ねない (位置の取り合いになる)
        if (kind == Jump && !Terrain.CrewBrace.Posed(s.Id) && !Terrain.CrewGrip.Active)
        {
            s.Body = s.Root.Find("BodyForms");
            s.Cos = pc.cosmetics ? pc.cosmetics.transform : null;
            if (s.Body) s.Bp = s.Body.localPosition;
            if (s.Cos) s.Cp = s.Cos.localPosition;
            s.Hop = s.Body || s.Cos;
        }

        s.Flip = pc.cosmetics && pc.cosmetics.FlipX;
        Live.Add(s);
        Played++;
        Apply(s, 0f);
        return true;
    }

    private static GameObject NewHand(Transform root, Material mat, int color, int layer, out Transform tf, out Transform art, out SpriteRenderer sr)
    {
        var go = new GameObject("MrpEmoteHand") { layer = layer };
        tf = go.transform;
        tf.SetParent(root, false);
        var ago = new GameObject("Art") { layer = layer };
        art = ago.transform;
        art.SetParent(tf, false);
        sr = ago.AddComponent<SpriteRenderer>();
        sr.sharedMaterial = mat;
        PlayerMaterial.SetColors(color, sr);
        sr.color = FxMath.Rgba(1f, 1f, 1f, 0f);
        return go;
    }

    internal static void Stop(byte id)
    {
        for (int i = Live.Count - 1; i >= 0; i--)
            if (Live[i].Id == id)
            {
                End(Live[i]);
                Live.RemoveAt(i);
            }
    }

    internal static void ClearAll()
    {
        foreach (var s in Live) End(s);
        Live.Clear();
    }

    private static void Unhop(Show s)
    {
        if (!s.Hop) return;
        s.Hop = false;
        if (s.Body) s.Body.localPosition = s.Bp;
        if (s.Cos) s.Cos.localPosition = s.Cp;
    }

    private static void End(Show s)
    {
        Unhop(s);
        if (s.A) Object.Destroy(s.A);
        if (s.B) Object.Destroy(s.B);
        if (s.Fx) Object.Destroy(s.Fx);
    }

    // 確認用: 自分のエモートを t 秒の姿で止めて出す (歩いても向きを変えても止まったまま・Stop/会議で消える)
    internal static bool Hold(PlayerControl pc, byte kind, float t)
    {
        // 止める時刻を先に入れてから出す (最初の姿を書く時に拍の音を鳴らさない)
        if (!Play(pc, kind, FxMath.Clamp(t, Fade, Duration(kind) - Fade))) return false;
        var s = Live[^1];
        Apply(s, s.Hold);
        return true;
    }

    // 毎フレーム
    internal static void Tick()
    {
        if (Live.Count == 0) return;
        if (Terrain.GameClock.ShipGen != _shipGen)
        {
            _shipGen = Terrain.GameClock.ShipGen;
            ClearAll();
            return;
        }
        bool check = ++_frame % CheckEvery == 0;
        float now = Time.time;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var s = Live[i];
            float t = s.Hold >= 0f ? s.Hold : now - s.Start;
            if (s.Hold < 0f && t >= s.Dur || !s.A || !s.Pc || check && !Alive(s.Pc))
            {
                End(s);
                Live.RemoveAt(i);
                continue;
            }
            if (check)
            {
                SetHidden(s, !s.Pc.Visible);
                s.Flip = s.Pc.cosmetics && s.Pc.cosmetics.FlipX;
                // 跳ねている途中で踏ん張り・吸い出しが体の根を動かし始めたら、こちらの上げ下げをやめて譲る
                if (s.Hop && (Terrain.CrewBrace.Posed(s.Id) || Terrain.CrewGrip.Active)) Unhop(s);
            }
            if (!s.Hidden) Apply(s, t);
        }
    }

    private static bool Alive(PlayerControl pc) => pc.Data != null && !pc.Data.IsDead && !pc.Data.Disconnected;

    private static void SetHidden(Show s, bool hidden)
    {
        if (s.Hidden == hidden) return;
        s.Hidden = hidden;
        s.A.SetActive(!hidden);
        if (s.B) s.B.SetActive(!hidden);
        if (s.Fx && hidden && s.FxOn)
        {
            s.FxOn = false;
            s.Fx.SetActive(false);
        }
    }

    // t 秒の時点の姿を書く。位置は体の中心からのワールドの長さ (右向き)・左向きの時は鏡に
    private static void Apply(Show s, float t)
    {
        float sx = s.Flip ? -1f : 1f;
        float alpha = FxMath.Clamp01(FxMath.Min(t, s.Dur - t) / Fade);
        float w = t * 2f * FxMath.PI;

        switch (s.Kind)
        {
            case Wave:
            {
                // 頭の横で開いた手を左右に振る (指先が上・手首を軸に外へ 10° 傾けて ±25°)
                float rot = -10f + 25f * FxMath.Sin(w * 2.5f);
                Hand(s.At, s.Ac, s.Ar, ref s.Asp, s.Inv, Open, sx, 1f, 0.46f, 0f, rot, alpha);
                // 外へ振るたびに袖が擦れる (音の山が振りの一番速い所に来るよう少し先に鳴らす・消えかけの最後の振りは鳴らさない)
                int beat = (int)((t + 0.06f) * 2.5f);
                if (NewBeat(s, beat) && t < s.Dur - 0.2f) Sound(s, beat % 2 == 0 ? "noise_emote_swish_1" : "noise_emote_swish_2", 0.3f, 6f, 3.5f);
                break;
            }
            case Jump:
            {
                // 3 回跳ねる。両手の握りこぶしを肩の上へ突き上げ、弾みに合わせて揺らす
                float hop = FxMath.Abs(FxMath.Sin(t / s.Dur * FxMath.PI * 3f)) * 0.3f;
                if (s.Hop)
                {
                    float dy = hop * s.Inv;
                    if (s.Body) s.Body.localPosition = FxMath.V3(s.Bp.x, s.Bp.y + dy, s.Bp.z);
                    if (s.Cos) s.Cos.localPosition = FxMath.V3(s.Cp.x, s.Cp.y + dy, s.Cp.z);
                }
                float lift = (s.Hop ? hop : 0f) + 0.06f * hop / 0.3f;
                float sway = 10f * FxMath.Sin(w * 2.5f);
                Hand(s.At, s.Ac, s.Ar, ref s.Asp, s.Inv, Fist, sx, 1f, 0.42f, lift - 0.12f, -20f + sway, alpha, FistSize);
                Hand(s.Bt, s.Bc, s.Br, ref s.Bsp, s.Inv, Fist, -sx, 1f, 0.42f, lift - 0.12f, -20f + sway, alpha, FistSize);
                // 踏み切りと着地 (体が本当に跳ねている時だけ)。最後の着地は終わりの間際なので少し早めに拍を替える
                int beat = (int)((t + 0.04f) / s.Dur * 3f);
                if (NewBeat(s, beat) && s.Hop) JumpSound(s, beat);
                break;
            }
            case Clap:
            {
                // 胸の前 (バイザーより下) で両手の手のひらを打ち合わせる。横から見た手の手のひらを内へ向ける。
                // 打った直後は手首が少し沈み指先が外へ開く (当たった反動)
                float ph = (t - ClapFirst) * ClapRate;
                float gap = ClapGap * FxMath.Abs(FxMath.Sin(ph * FxMath.PI));
                int hit = ph >= 0f ? (int)ph : -1;
                float since = hit >= 0 ? (ph - hit) / ClapRate : 1f;
                float kick = since < 0.1f ? 1f - since / 0.1f : 0f;
                float y = -0.2f - 0.02f * kick, rot = -6f + 10f * kick;
                Hand(s.At, s.Ac, s.Ar, ref s.Asp, s.Inv, Side, sx, -1f, 0.09f + gap, y, rot, alpha, ClapSize);
                Hand(s.Bt, s.Bc, s.Br, ref s.Bsp, s.Inv, Side, -sx, -1f, 0.09f + gap, y, rot, alpha, ClapSize);
                ClapFx(s, hit, since, alpha);
                if (NewBeat(s, hit)) Sound(s, ClapClips[hit % ClapClips.Length], 0.75f * (0.6f + 0.4f * alpha), 10f, 5f);
                break;
            }
            default:
            {
                // 手のひらを上へ向けて前へ出し、指を曲げて招く (伸ばす → 曲げきる → 戻すを 0.4 秒で)
                int curl = BeckonCycle[(int)(t / 0.1f) % BeckonCycle.Length];
                float rot = 8f * FxMath.Sin(w * 2.5f);
                Hand(s.At, s.Ac, s.Ar, ref s.Asp, s.Inv, Beckon0 + curl, sx, 1f, 0.38f, -0.12f, rot, alpha);
                // 指を曲げ始めるたびに手袋が小さく擦れる
                if (NewBeat(s, t >= 0.05f ? (int)((t - 0.05f) / 0.4f) : -1)) Sound(s, "noise_emote_rustle", 0.25f, 4f, 2.5f);
                break;
            }
        }
    }

    private static readonly string[] ClapClips = { "noise_emote_clap_1", "noise_emote_clap_2", "noise_emote_clap_3" };

    // 拍の番号が替わった時だけ true (確認用に姿を止めている間は鳴らさない)
    private static bool NewBeat(Show s, int beat)
    {
        if (beat < 0 || beat == s.Cue) return false;
        s.Cue = beat;
        return s.Hold < 0f;
    }

    // 出している人の足元から鳴らす (遠いと小さく・こもる)。拍の替わり目だけ呼ぶので位置はその時に読む
    // 見えない人 (透明・ベントの中) の音は鳴らさない (位置が分かってしまう)。隠れの見直しは数フレームおきなので、ここでも見る
    private static void Sound(Show s, string clip, float volume, float range, float muffle)
    {
        if (s.Hidden || !s.Pc.Visible) return;
        Vector2 p = s.Pc.GetTruePosition();
        Sounds++;
        _lastSound = clip;
        Terrain.BreakNoise.PlayAt(clip, p.x, p.y, range, muffle, volume, s.Pitch * FxMath.Range(0.97f, 1.03f), true);
    }

    private static void JumpSound(Show s, int beat)
    {
        if (beat == 0) Sound(s, "noise_emote_push", 0.4f, 7f, 4f);
        else Sound(s, beat % 2 == 1 ? "noise_emote_land_1" : "noise_emote_land_2", 0.6f, 8f, 4.5f);
    }

    // 手を 1 つ置く。x は mirror の向きが正 (左向きの人・反対の手は -1 で鏡に)。鏡は持ち手の倍率で (手首のずらしごと映す)。
    // face = -1 で絵だけを左右に返す (横から見た手の手のひらを内へ向ける時)
    private static void Hand(Transform tf, Transform art, SpriteRenderer sr, ref int cur, float inv, int sprite, float mirror, float face, float x, float y, float rot, float alpha, float size = HandSize)
    {
        if (!tf) return;
        if (cur != sprite)
        {
            cur = sprite;
            sr.sprite = Sprites[sprite];
            art.localPosition = FxMath.V3(Wrists[sprite].x, Wrists[sprite].y, 0f);
        }
        float m = mirror * face;
        tf.localPosition = FxMath.V3(mirror * x * inv, y * inv, HandZ * inv);
        tf.localRotation = FxMath.RotZ(m < 0f ? -rot : rot);
        float k = size * inv;
        tf.localScale = FxMath.V3(m < 0f ? -k : k, k, 1f);
        sr.color = FxMath.Rgba(1f, 1f, 1f, alpha);
    }

    // 拍手の効果の部品 (閃きのにじみ・白い核・線 4 本) を作る。角度・位置は打つたびに ClapFx が書く
    private static void NewClapFx(Show s, int layer)
    {
        var glow = GlowSprite();
        var line = LineSprite();
        if (!glow || !line) return;
        s.Fx = new GameObject("MrpEmoteClapFx") { layer = layer };
        s.Fx.transform.SetParent(s.Root, false);
        s.Fx.SetActive(false);
        s.Glow = NewPart(s.Fx.transform, glow, layer, out s.GlowR);
        s.Core = NewPart(s.Fx.transform, glow, layer, out s.CoreR);
        s.Lines = new Transform[LineAngles.Length];
        s.LineR = new SpriteRenderer[LineAngles.Length];
        s.Dx = new float[LineAngles.Length];
        s.Dy = new float[LineAngles.Length];
        for (int i = 0; i < LineAngles.Length; i++) s.Lines[i] = NewPart(s.Fx.transform, line, layer, out s.LineR[i]);
    }

    private static Transform NewPart(Transform parent, Sprite sprite, int layer, out SpriteRenderer sr)
    {
        var go = new GameObject("Part") { layer = layer };
        go.transform.SetParent(parent, false);
        sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        return go.transform;
    }

    // 打った瞬間から since 秒の効果を書く。打った回が替わったら線の角度を少し振り直す (毎回同じ形に見えないように)
    private static void ClapFx(Show s, int hit, float since, float alpha)
    {
        if (s.Lines == null) return;
        if (hit < 0 || since >= LineTime || alpha <= 0f)
        {
            if (s.FxOn)
            {
                s.FxOn = false;
                s.Fx.SetActive(false);
            }
            return;
        }
        float inv = s.Inv, z = (HandZ - 0.002f) * inv;
        if (hit != s.Hit)
        {
            s.Hit = hit;
            float tilt = (hit % 2 == 0 ? 1f : -1f) * 7f;
            for (int i = 0; i < s.Lines.Length; i++)
            {
                float a = LineAngles[i] + tilt + (i % 2 == 0 ? 4f : -4f) * (hit % 3 - 1);
                float r = a * FxMath.Deg2Rad;
                s.Dx[i] = FxMath.Cos(r);
                s.Dy[i] = FxMath.Sin(r);
                s.Lines[i].localRotation = FxMath.RotZ(a);
            }
            s.Glow.localPosition = FxMath.V3(ClapX * inv, ClapY * inv, z);
            s.Core.localPosition = FxMath.V3(ClapX * inv, ClapY * inv, z - 0.0005f * inv);
            s.FlashOn = true;
        }
        if (!s.FxOn)
        {
            s.FxOn = true;
            s.Fx.SetActive(true);
        }

        // 閃き: にじみ (暖かい白) が広がって消え、白い核はもっと早く消える
        if (s.FlashOn)
        {
            float p = FxMath.Clamp01(since / FlashTime);
            float g = (0.3f + 0.35f * (1f - (1f - p) * (1f - p))) * inv;
            s.Glow.localScale = FxMath.V3(g, g, 1f);
            s.GlowR.color = FxMath.Rgba(1f, 0.93f, 0.68f, (1f - p) * (1f - p) * alpha);
            float pc = FxMath.Clamp01(since / (FlashTime * 0.6f));
            float c = (0.14f + 0.08f * pc) * inv;
            s.Core.localScale = FxMath.V3(c, c, 1f);
            s.CoreR.color = FxMath.Rgba(1f, 1f, 1f, (1f - pc) * alpha);
            s.FlashOn = since < FlashTime;
        }

        // 線: 合わさる所から外へ飛び、縮みながら後半で消える
        float pl = since / LineTime;
        float e = 1f - (1f - pl) * (1f - pl);
        float rr = LineFrom + (LineTo - LineFrom) * e;
        float len = LineLen * (1f - 0.6f * pl) * inv, thick = LineThick / LineSpriteH * inv;
        float la = (pl < 0.5f ? 1f : (1f - pl) * 2f) * alpha;
        for (int i = 0; i < s.Lines.Length; i++)
        {
            s.Lines[i].localPosition = FxMath.V3((ClapX + s.Dx[i] * rr) * inv, (ClapY + s.Dy[i] * rr) * inv, z);
            s.Lines[i].localScale = FxMath.V3(len, thick, 1f);
            s.LineR[i].color = FxMath.Rgba(1f, 1f, 1f, la);
        }
    }

    // 1 単位の丸いにじみ (中心ほど明るい)。起動中は持ち続ける
    private static Sprite GlowSprite()
    {
        if (_glowSprite) return _glowSprite;
        const int n = 64;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
            float t = FxMath.Clamp01(1f - System.MathF.Sqrt(dx * dx + dy * dy));
            int i = (y * n + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = 255;
            px[i + 3] = (byte)(t * t * 255f + 0.5f);
        }
        return _glowSprite = MakeSprite("MrpEmoteGlow", px, n, n, n);
    }

    private const float LineSpriteH = 0.25f; // 線の絵の高さ (長さ 1 単位に対して)

    // 長さ 1 単位の丸い端の線。暗い縁 (白い床でも見える) の中に明るい黄色、芯は白
    private static Sprite LineSprite()
    {
        if (_lineSprite) return _lineSprite;
        const int w = 64, h = 16;
        const float r = 7.5f, edge = 2.2f;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float fx = x + 0.5f, fy = y + 0.5f;
            float cx = FxMath.Clamp(fx, 8f, w - 8f);
            float d = System.MathF.Sqrt((fx - cx) * (fx - cx) + (fy - 8f) * (fy - 8f));
            float a = FxMath.Clamp01(r - d);
            float fill = FxMath.Clamp01(r - edge - d);   // 0 = 縁 / 1 = 中
            float core = FxMath.Clamp01(1f - d / (r - edge)); // 芯ほど白
            int i = (y * w + x) * 4;
            px[i] = (byte)((0.12f + (1f - 0.12f) * fill) * 255f);
            px[i + 1] = (byte)((0.1f + (0.88f + 0.12f * core - 0.1f) * fill) * 255f);
            px[i + 2] = (byte)((0.16f + (0.45f + 0.55f * core - 0.16f) * fill) * 255f);
            px[i + 3] = (byte)(a * 255f);
        }
        return _lineSprite = MakeSprite("MrpEmoteLine", px, w, h, w);
    }

    private static unsafe Sprite MakeSprite(string name, byte[] px, int w, int h, float ppu)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        fixed (byte* p = px) tex.LoadRawTextureData((System.IntPtr)p, px.Length);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.HideAndDontSave;
        var sp = Sprite.Create(tex, new Rect(0, 0, w, h), FxMath.V2(0.5f, 0.5f), ppu, 0, SpriteMeshType.FullRect);
        sp.hideFlags = HideFlags.HideAndDontSave;
        return sp;
    }

    // 絵は DLL に埋め込んだ物 (船の有無によらず読める・1 回読んだら持ち続ける)
    private static bool LoadSprites()
    {
        if (_loaded) return true;
        bool all = true;
        for (int i = 0; i < SpriteNames.Length; i++)
        {
            if (!Sprites[i]) Sprites[i] = Roles.ButtonIcons.Load(SpriteNames[i], 100f, FxMath.V2(0.5f, 0.5f));
            all &= Sprites[i];
        }
        return _loaded = all;
    }

    internal static string Describe() =>
        $"live={Live.Count} played={Played} refused={Refused} sounds={Sounds} lastSound={_lastSound} sprites={(_loaded ? "ok" : "missing")}";
}
