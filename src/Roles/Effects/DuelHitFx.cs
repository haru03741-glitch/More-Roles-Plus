using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Roles;

// 立ち合いの当たりの見た目 (全員の画面): 当たった人の上に白い星形の火花、斬った向きへ飛ぶ光の筋と赤いしぶき。強く斬るほど大きい。
// 当事者 (斬った人・斬られた人) の画面だけ、1 コマの暗転と画面の揺れが付く (遠くの斬り合いで他人の画面を点滅させない)
internal static class DuelHitFx
{
    private const int MaxParts = 48;

    private enum Kind : byte { Star, Streak, Drop }

    private sealed class Part
    {
        public GameObject Go;
        public Transform Tf;
        public SpriteRenderer Sr;
        public Kind Kind;
        public float T, Life, X, Y, Ex, Ey, Size, Rot;
        public float R, G, B;
    }

    private static readonly List<Part> Parts = new();
    private static readonly Stack<Part> Pool = new();
    private static Sprite _star, _streak, _dot;
    private static bool _hooked;

    internal static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        Duel.OnHit += OnHit;
    }

    private static void OnHit(byte a, byte t, int hp)
    {
        int dmg = Duel.LastDamage;
        if (a == t || dmg <= 0) return; // 自分から自分 = 立ち合いの外の回復
        var gd = GameData.Instance;
        if (!gd) return;
        var tp = gd.GetPlayerById(t)?.Object;
        var ap = gd.GetPlayerById(a)?.Object;
        if (!tp) return;
        Burst(tp, ap, dmg, hp == 0);
    }

    // 確かめ用にも使う。attacker が無ければ右から斬った扱い
    internal static void Burst(PlayerControl target, PlayerControl attacker, int dmg, bool ko)
    {
        float k = FxMath.Clamp01(dmg / 60f);
        SamuraiSound.Hit(target, ko);
        byte me = PlayerControl.LocalPlayer ? PlayerControl.LocalPlayer.PlayerId : (byte)255;
        if (me == target.PlayerId || attacker && me == attacker.PlayerId)
        {
            SamuraiScreen.Flash(0.45f + 0.25f * k, ko ? 0.09f : 0.05f);
            var hud = Vanilla.Hud;
            if (hud && hud.PlayerCam) hud.PlayerCam.ShakeScreen(0.12f + 0.1f * k, 0.6f + 1.2f * k);
        }
        if (!target.Visible) return;
        if (!Ensure()) return;
        Vector3 at = target.transform.position;
        float x = at.x, y = at.y + 0.1f, z = at.z - 0.01f;
        int layer = target.gameObject.layer;
        // 斬った向き (斬った人 → 斬られた人)
        float dx = 1f, dy = 0f;
        if (attacker)
        {
            Vector3 from = attacker.transform.position;
            dx = x - from.x;
            dy = y - from.y;
            float l = FxMath.Sqrt(dx * dx + dy * dy);
            if (l > 1e-3f) { dx /= l; dy /= l; }
            else { dx = 1f; dy = 0f; }
        }
        float dir = FxMath.Atan2(dy, dx) * FxMath.Rad2Deg;
        float size = 0.55f + 0.6f * k + (ko ? 0.4f : 0f);
        Spawn(Kind.Star, layer, x, y, z, 0f, 0f, size, FxMath.Range(0f, 45f), 0.16f + 0.06f * k, 0.92f, 0.96f, 1f);
        int streaks = 5 + (int)(4f * k) + (ko ? 3 : 0);
        for (int i = 0; i < streaks; i++)
        {
            // 半分は斬った向きへ吹き、残りは周りへ散る
            float ang = i % 2 == 0 ? dir + FxMath.Range(-55f, 55f) : FxMath.Range(0f, 360f);
            float dist = (0.45f + 0.35f * k) * FxMath.Range(0.7f, 1.2f);
            float ar = ang * FxMath.Deg2Rad;
            Spawn(Kind.Streak, layer, x, y, z - 0.001f, FxMath.Cos(ar) * dist, FxMath.Sin(ar) * dist,
                (0.30f + 0.25f * k) * FxMath.Range(0.7f, 1.1f), ang, 0.17f, 0.75f, 0.9f, 1f);
        }
        int drops = (ko ? 8 : 2) + (int)(4f * k);
        for (int i = 0; i < drops; i++)
        {
            float ang = (dir + FxMath.Range(-40f, 40f)) * FxMath.Deg2Rad;
            float dist = FxMath.Range(0.35f, 0.85f) * (1f + 0.4f * k);
            Spawn(Kind.Drop, layer, x, y - 0.05f, z + 0.0005f, FxMath.Cos(ang) * dist, FxMath.Sin(ang) * dist - 0.15f,
                FxMath.Range(0.06f, 0.11f), 0f, FxMath.Range(0.25f, 0.4f), 0.72f, 0.05f, 0.12f);
        }
    }

    // 刀同士がぶつかった所の火花 (全員の画面): 黄みの白い星と、周りへ散る短い筋
    internal static void Clash(Vector3 at, int layer, float size)
    {
        if (!Ensure()) return;
        float x = at.x, y = at.y, z = at.z;
        Spawn(Kind.Star, layer, x, y, z, 0f, 0f, 0.7f * size, FxMath.Range(0f, 45f), 0.14f + 0.04f * size, 1f, 0.9f, 0.55f);
        int n = 3 + (int)(5f * size);
        for (int i = 0; i < n; i++)
        {
            float ang = FxMath.Range(0f, 360f);
            float ar = ang * FxMath.Deg2Rad;
            float dist = 0.5f * size * FxMath.Range(0.6f, 1.2f);
            Spawn(Kind.Streak, layer, x, y, z - 0.001f, FxMath.Cos(ar) * dist, FxMath.Sin(ar) * dist,
                0.28f * size * FxMath.Range(0.7f, 1.1f), ang, 0.16f, 1f, 0.8f, 0.35f);
        }
    }

    private static bool Ensure()
    {
        _star ??= FxSprites.Rect(64, 64, Star, "MrpHitStar");
        _streak ??= FxSprites.Rect(64, 16, Streak, "MrpHitStreak");
        _dot ??= FxSprites.Rect(16, 16, FxSprites.Dot, "MrpHitDot");
        return _star && _streak && _dot;
    }

    // 4 本の鋭い光 (縦横は長く、斜めは短い) と丸い芯
    private static float Star(float x, float y)
    {
        float ax = FxMath.Abs(x), ay = FxMath.Abs(y);
        float r = FxMath.Sqrt(x * x + y * y) * 2f;
        if (r >= 1f) return 0f;
        float fall = (1f - r) * (1f - r);
        float h = FxMath.Clamp01(1f - ay / (0.06f * (1f - r) + 0.004f)) * fall;
        float v = FxMath.Clamp01(1f - ax / (0.06f * (1f - r) + 0.004f)) * fall;
        float d1 = FxMath.Abs(x - y) * 0.7071f, d2 = FxMath.Abs(x + y) * 0.7071f;
        float dg = r < 0.55f ? FxMath.Max(FxMath.Clamp01(1f - d1 / 0.03f), FxMath.Clamp01(1f - d2 / 0.03f)) * (1f - r / 0.55f) : 0f;
        float core = FxMath.Clamp01((0.3f - r) / 0.2f);
        return FxMath.Max(FxMath.Max(FxMath.Max(h, v), dg), core);
    }

    // 横長の細い菱形 (先が尖る)
    private static float Streak(float x, float y)
    {
        float ax = FxMath.Abs(x) * 2f, ay = FxMath.Abs(y) * 2f;
        float w = 1f - ax;
        if (w <= 0f) return 0f;
        return FxMath.Clamp01((w - ay) / 0.25f);
    }

    private static void Spawn(Kind kind, int layer, float x, float y, float z, float ex, float ey, float size, float rot, float life, float r, float g, float b)
    {
        if (Parts.Count >= MaxParts) return;
        var p = Pool.Count > 0 ? Pool.Pop() : null;
        if (p == null || !p.Go)
        {
            var go = new GameObject("MrpDuelHit");
            Object.DontDestroyOnLoad(go);
            p = new Part { Go = go, Tf = go.transform, Sr = go.AddComponent<SpriteRenderer>() };
        }
        p.Go.layer = layer;
        p.Kind = kind;
        p.Sr.sprite = kind == Kind.Star ? _star : kind == Kind.Streak ? _streak : _dot;
        p.T = Time.time;
        p.Life = life;
        p.X = x;
        p.Y = y;
        p.Ex = ex;
        p.Ey = ey;
        p.Size = size;
        p.Rot = rot;
        p.R = r;
        p.G = g;
        p.B = b;
        p.Tf.position = FxMath.V3(x, y, z);
        p.Tf.rotation = FxMath.RotZ(rot);
        p.Tf.localScale = FxMath.V3(0f, 0f, 1f);
        p.Go.SetActive(true);
        Parts.Add(p);
    }

    // 毎フレーム。出ていなければ件数の比較だけで帰る
    internal static void Tick()
    {
        if (Parts.Count == 0) return;
        float now = Time.time;
        for (int i = Parts.Count - 1; i >= 0; i--)
        {
            var p = Parts[i];
            if (!p.Go) { Parts.RemoveAt(i); continue; }
            float u = (now - p.T) / (p.Life * SamuraiBlade.SlowMo);
            if (u >= 1f)
            {
                p.Go.SetActive(false);
                Pool.Push(p);
                Parts.RemoveAt(i);
                continue;
            }
            float e = 1f - (1f - u) * (1f - u);
            var pos = p.Tf.position;
            float a, sx, sy;
            switch (p.Kind)
            {
                case Kind.Star:
                    // 一瞬で開いて少し大きくなりながら消える
                    float pop = u < 0.2f ? u / 0.2f : 1f + 0.15f * (u - 0.2f);
                    sx = sy = p.Size * pop;
                    a = u < 0.2f ? 1f : 1f - (u - 0.2f) / 0.8f;
                    break;
                case Kind.Streak:
                    sx = p.Size * (1f - 0.7f * u);
                    sy = p.Size * 0.22f * (1f - u);
                    a = 1f - u * u;
                    break;
                default:
                    sx = sy = p.Size * (1f - 0.5f * u);
                    a = 1f - u * u;
                    break;
            }
            p.Tf.position = FxMath.V3(p.X + p.Ex * e, p.Y + p.Ey * e, pos.z);
            p.Tf.localScale = FxMath.V3(sx, sy, 1f);
            // 光は始めが白く、色へ移る
            float w = p.Kind == Kind.Drop ? 0f : 1f - u;
            p.Sr.color = FxMath.Rgba(p.R + (1f - p.R) * w, p.G + (1f - p.G) * w, p.B + (1f - p.B) * w, a);
        }
    }
}
