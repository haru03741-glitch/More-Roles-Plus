using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Fx;

// エモートの見た目: 本編のペットをなでる手 (pet_hand・赤=本体色/青=影色のひな形) を本人の色で塗り、人の子に置いて一緒に動かす。
// 手を振る・跳ねる・拍手・手招きの 4 種。跳ねるは体の絵 (BodyForms) と帽子・服 (cosmetics) の根を同じだけ上げ下げし、終わりに元へ戻す。
// 描くのは手元の画面だけ。毎フレーム触るのは出している人だけで、出ていない時は件数の比較だけで帰る。算術は FxMath
internal static class EmoteArt
{
    internal const byte Wave = 0, Jump = 1, Clap = 2, Beckon = 3, Count = 4;

    private static readonly float[] Durations = { 1.6f, 1.2f, 1.6f, 1.6f };
    private static readonly string[] SpriteNames = { "pet_hand0012", "pet_hand0020", "pet_hand0041", "pet_hand0001" };
    private const int Open = 0, Curl = 1, Side = 2, Fist = 3;

    private const float HandSize = 0.3f;    // 手の絵 (約 1.28 単位幅) の倍率
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
        public Transform At, Bt, Ac, Bc; // 持ち手 (位置・回転・鏡) と、中心を持ち手に合わせた絵
        public SpriteRenderer Ar, Br;
        public int Asp = -1, Bsp = -1;   // 今の絵の番号
        public bool Hidden, Flip;
    }

    private static readonly List<Show> Live = new();
    private static readonly Sprite[] Sprites = new Sprite[SpriteNames.Length];
    private static readonly Vector2[] Centers = new Vector2[SpriteNames.Length]; // 絵の中心を持ち手に合わせるずらし (本編の手の絵は原点が絵の外にある)
    private static int _frame, _shipGen = -1, _spriteGen = -1;

    // 確認用の数
    internal static int Played, Refused;

    internal static float Duration(byte kind) => kind < Count ? Durations[kind] : 0f;

    internal static bool IsPlaying(byte id)
    {
        foreach (var s in Live) if (s.Id == id) return true;
        return false;
    }

    private static int IconSprite(byte kind) => kind switch { Wave => Open, Jump => Fist, Clap => Side, _ => Curl };

    internal static Sprite Icon(byte kind) => LoadSprites() ? Sprites[IconSprite(kind)] : null;

    // アイコンの絵の中心を置き場所に合わせるずらし
    internal static Vector2 IconCenter(byte kind) => LoadSprites() ? Centers[IconSprite(kind)] : default;

    // ボタンのアイコンの手の傾き (度・各エモートの姿に合わせる)
    internal static float IconRotation(byte kind) => kind switch { Wave => 180f, Jump => 160f, Clap => 90f, _ => 120f };

    // pc にエモートを出す (同じ人の前のエモートは打ち切る)
    internal static bool Play(PlayerControl pc, byte kind)
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
            Start = Time.time,
            Dur = Durations[kind],
            Root = pc.transform,
        };
        float scale = s.Root.localScale.x;
        s.Inv = scale > 0.01f ? 1f / scale : 1f;
        int color = pc.Data != null ? pc.Data.DefaultOutfit.ColorId : 0;
        int layer = pc.gameObject.layer;
        s.A = NewHand(s.Root, mat, color, layer, out s.At, out s.Ac, out s.Ar);
        if (kind == Clap || kind == Jump) s.B = NewHand(s.Root, mat, color, layer, out s.Bt, out s.Bc, out s.Br);

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
            float t = now - s.Start;
            if (t >= s.Dur || !s.A || !s.Pc || check && !Alive(s.Pc))
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
                // 頭の横で開いた手を左右に振る (指先が上・手首を軸に ±25°)
                float rot = 180f + 25f * FxMath.Sin(w * 2.5f);
                Hand(s.At, s.Ac, s.Ar, ref s.Asp, s.Inv, Open, sx, 0.33f, 0.22f, rot, alpha);
                break;
            }
            case Jump:
            {
                // 3 回跳ねる。開いた両手を上げて弾みに合わせて揺らす
                float hop = FxMath.Abs(FxMath.Sin(t / s.Dur * FxMath.PI * 3f)) * 0.3f;
                if (s.Hop)
                {
                    float dy = hop * s.Inv;
                    if (s.Body) s.Body.localPosition = FxMath.V3(s.Bp.x, s.Bp.y + dy, s.Bp.z);
                    if (s.Cos) s.Cos.localPosition = FxMath.V3(s.Cp.x, s.Cp.y + dy, s.Cp.z);
                }
                float lift = s.Hop ? hop : 0f;
                float sway = 12f * FxMath.Sin(w * 2.5f);
                Hand(s.At, s.Ac, s.Ar, ref s.Asp, s.Inv, Open, sx, 0.3f, 0.12f + lift, 160f + sway, alpha);
                Hand(s.Bt, s.Bc, s.Br, ref s.Bsp, s.Inv, Open, -sx, 0.3f, 0.12f + lift, 160f - sway, alpha);
                break;
            }
            case Clap:
            {
                // 体の前で両手を打ち合わせる (秒 3 回)
                float gap = 0.03f + 0.08f * (0.5f + 0.5f * FxMath.Cos(w * 3f));
                Hand(s.At, s.Ac, s.Ar, ref s.Asp, s.Inv, Side, sx, 0.18f + gap, -0.05f, 90f, alpha);
                Hand(s.Bt, s.Bc, s.Br, ref s.Bsp, s.Inv, Side, -sx, gap - 0.18f, -0.05f, 90f, alpha);
                break;
            }
            default:
            {
                // 前へ出した手で招く (開く ⇄ 曲げるを 0.2 秒ごと・手首を前後に)
                bool curl = (int)(t / 0.2f) % 2 == 1;
                float rot = 120f + 25f * FxMath.Sin(w * 2.5f);
                Hand(s.At, s.Ac, s.Ar, ref s.Asp, s.Inv, curl ? Curl : Open, sx, 0.3f, -0.02f, rot, alpha);
                break;
            }
        }
    }

    // 手を 1 つ置く。x は mirror の向きが正 (左向きの人・反対の手は -1 で鏡に)。鏡は持ち手の倍率で (絵の中心のずらしごと映す)
    private static void Hand(Transform tf, Transform art, SpriteRenderer sr, ref int cur, float inv, int sprite, float mirror, float x, float y, float rot, float alpha)
    {
        if (!tf) return;
        if (cur != sprite)
        {
            cur = sprite;
            sr.sprite = Sprites[sprite];
            art.localPosition = FxMath.V3(Centers[sprite].x, Centers[sprite].y, 0f);
        }
        tf.localPosition = FxMath.V3(mirror * x * inv, y * inv, HandZ * inv);
        tf.localRotation = FxMath.RotZ(mirror < 0f ? -rot : rot);
        float k = HandSize * inv;
        tf.localScale = FxMath.V3(mirror < 0f ? -k : k, k, 1f);
        sr.color = FxMath.Rgba(1f, 1f, 1f, alpha);
    }

    // 本編の手の絵は試合の船と一緒に読み込まれる。船ごとに 1 回だけ名前で探す (見つからなければ次の船で探し直す)
    private static bool LoadSprites()
    {
        if (Sprites[0] && Sprites[1] && Sprites[2] && Sprites[3]) return true;
        if (_spriteGen == Terrain.GameClock.ShipGen) return false;
        _spriteGen = Terrain.GameClock.ShipGen;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
        {
            var sp = o.TryCast<Sprite>();
            if (!sp) continue;
            string n = sp.name;
            for (int i = 0; i < SpriteNames.Length; i++)
            {
                if (n != SpriteNames[i]) continue;
                Sprites[i] = sp;
                Rect r = sp.rect;
                Vector2 pv = sp.pivot;
                float ppu = sp.pixelsPerUnit > 0f ? sp.pixelsPerUnit : 100f;
                Centers[i] = new Vector2((pv.x - r.width * 0.5f) / ppu, (pv.y - r.height * 0.5f) / ppu);
            }
            if (Sprites[0] && Sprites[1] && Sprites[2] && Sprites[3]) break;
        }
        return Sprites[0] && Sprites[1] && Sprites[2] && Sprites[3];
    }

    internal static string Describe() =>
        $"live={Live.Count} played={Played} refused={Refused} sprites={(Sprites[0] ? 1 : 0) + (Sprites[1] ? 1 : 0) + (Sprites[2] ? 1 : 0) + (Sprites[3] ? 1 : 0)}/4";
}
