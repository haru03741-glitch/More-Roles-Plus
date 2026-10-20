using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// ポータルの絵: 足元に立つ縦長の渦。暗い下地 → 青緑のにじみ → 逆向きに回る渦 2 枚 → 白い芯 → 吸い込まれる光の粒。
// 開く時は縦の線から横へ広がり、閉じる時は縦の線に縮む。人が通った時は光が膨らんで縦に抜ける。
// 絵は人と同じ奥行きの決め方 (足元の y に体の高さを足して /1000) なので、手前を通る人と上に立った人はポータルの前に出る。
// 毎フレーム触るのは近くにあるポータルだけ (遠い物と視界の外は影に隠れるので動かさない)
internal static class PortalArt
{
    private const float W = 0.9f, H = 1.3f;        // 楕円の幅と高さ
    private const float Lift = H * 0.5f - 0.04f;   // 足元から楕円の中心まで
    private const float BodyLift = 0.3636f;        // 人の奥行きは体の y/1000 で、体は足元よりこれだけ上
    private const float LayerStep = 0.00001f;      // 重ねる絵どうしの奥行きの差 (人との前後は変えない程度)
    private const float SinkBehind = 0.25f;        // ポータルの上に立った人 (着いた人) が絵に隠れないよう、この y の分だけ奥に置く
    private const float OpenTime = 0.35f, CloseTime = 0.3f, PassTime = 0.4f;
    private const float AnimRange = 14f;           // これより遠いポータルは毎フレーム動かさない
    private const int Motes = 4;
    private const int MaxBursts = 12;              // 通った光が同時にこれより多ければ足さない

    private static readonly Color Deep = FxMath.Rgba(0.02f, 0.05f, 0.16f, 0.95f);
    private static readonly Color Teal = FxMath.Rgba(0.2f, 0.95f, 0.85f, 1f);
    private static readonly Color Blue = FxMath.Rgba(0.15f, 0.45f, 1f, 1f);
    private static readonly Color Violet = FxMath.Rgba(0.62f, 0.48f, 1f, 1f);
    private static readonly Color White = FxMath.Rgba(0.9f, 1f, 1f, 1f);

    internal sealed class Handle
    {
        internal GameObject Root;
        internal Transform Body, SwirlA, SwirlB;
        internal SpriteRenderer Halo, Core, Floor;
        internal readonly Transform[] Mote = new Transform[Motes];
        internal readonly SpriteRenderer[] MoteSr = new SpriteRenderer[Motes];
        internal float X, Y, Born, Closing = -1f, Spin;
    }

    private sealed class Burst
    {
        internal GameObject Root;
        internal Transform Glow, Column;
        internal SpriteRenderer GlowSr, ColumnSr;
        internal float Born;
    }

    private static readonly List<Handle> Live = new();
    private static readonly List<Burst> Bursts = new();
    private static GameObject _container;
    private static int _gen = -1;
    private static Sprite _glow, _swirl, _disc;

    internal static int LiveCount => Live.Count;

    // 足元 (x, y) にポータルを開く
    public static Handle Open(float x, float y)
    {
        var parent = Container();
        if (!parent) return null;
        var h = new Handle { X = x, Y = y, Born = Time.time, Spin = FxMath.Range(0f, 360f) };
        h.Root = new GameObject("MrpPortal");
        h.Root.transform.SetParent(parent.transform, false);
        h.Root.transform.position = FxMath.V3(x, y + Lift, (y + SinkBehind + BodyLift) / 1000f);

        // 床の照り返しは人の後ろ (奥) に置く
        h.Floor = Layer(h.Root.transform, "floor", _glow, FxMath.V3(0f, -Lift + 0.02f, 0.001f), FxMath.V3(1.1f, 0.26f, 1f), FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0.3f));

        var body = new GameObject("body");
        body.transform.SetParent(h.Root.transform, false);
        body.transform.localScale = FxMath.V3(0.02f, H, 1f);
        h.Body = body.transform;
        Layer(h.Body, "deep", _disc, FxMath.V3(0f, 0f, 0f), FxMath.V3(0.97f, 0.97f, 1f), Deep);
        h.Halo = Layer(h.Body, "halo", _glow, FxMath.V3(0f, 0f, -LayerStep), FxMath.V3(1.6f, 1.35f, 1f), FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0.4f));
        h.SwirlA = Layer(h.Body, "swirlA", _swirl, FxMath.V3(0f, 0f, -LayerStep * 2f), FxMath.V3(1f, 1f, 1f), FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0.95f)).transform;
        h.SwirlB = Layer(h.Body, "swirlB", _swirl, FxMath.V3(0f, 0f, -LayerStep * 3f), FxMath.V3(0.78f, 0.78f, 1f), FxMath.Rgba(Violet.r, Violet.g, Violet.b, 0.7f)).transform;
        h.Core = Layer(h.Body, "core", _glow, FxMath.V3(0f, 0f, -LayerStep * 4f), FxMath.V3(0.42f, 0.36f, 1f), FxMath.Rgba(White.r, White.g, White.b, 0.85f));
        for (int i = 0; i < Motes; i++)
        {
            h.MoteSr[i] = Layer(h.Body, "mote", _glow, FxMath.V3(0f, 0f, -LayerStep * 5f), FxMath.V3(0.09f, 0.065f, 1f), FxMath.Rgba(White.r, White.g, White.b, 0f));
            h.Mote[i] = h.MoteSr[i].transform;
        }
        Live.Add(h);
        Pulse(x, y, 0.7f);
        BreakNoise.PlayAt("noise_portal_open", x, y, 9f, 5f, 0.8f, FxMath.Range(0.95f, 1.05f));
        return h;
    }

    // 縦の線に縮めて消す
    public static void Close(Handle h)
    {
        if (h == null || h.Closing >= 0f) return;
        h.Closing = Time.time;
        Pulse(h.X, h.Y, 0.6f);
        BreakNoise.PlayAt("noise_portal_close", h.X, h.Y, 9f, 5f, 0.8f, FxMath.Range(0.95f, 1.05f));
    }

    // 演出なしですぐ消す (役職が外れた時)
    public static void Remove(Handle h)
    {
        if (h == null) return;
        Live.Remove(h);
        if (h.Root) UnityEngine.Object.Destroy(h.Root);
    }

    // 人が通った (入った側と出た側の両方で呼ぶ)
    public static void Pass(float x, float y)
    {
        Pulse(x, y, 1f);
        BreakNoise.PlayAt("noise_portal_pass", x, y, 9f, 5f, 0.9f, FxMath.Range(0.95f, 1.08f));
    }

    private static void Pulse(float x, float y, float size)
    {
        if (Bursts.Count >= MaxBursts) return;
        var parent = Container();
        if (!parent) return;
        var b = new Burst { Born = Time.time };
        b.Root = new GameObject("MrpPortalBurst");
        b.Root.transform.SetParent(parent.transform, false);
        // 人より少し手前に出す (光が人にかぶさる)
        b.Root.transform.position = FxMath.V3(x, y + Lift, (y + BodyLift) / 1000f - 0.002f);
        b.Root.transform.localScale = FxMath.V3(size, size, 1f);
        b.GlowSr = Layer(b.Root.transform, "glow", _glow, FxMath.V3(0f, 0f, 0f), FxMath.V3(0.4f, 0.5f, 1f), FxMath.Rgba(White.r, White.g, White.b, 0f));
        b.Glow = b.GlowSr.transform;
        b.ColumnSr = Layer(b.Root.transform, "column", _glow, FxMath.V3(0f, 0.2f, -LayerStep), FxMath.V3(0.3f, 0.6f, 1f), FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0f));
        b.Column = b.ColumnSr.transform;
        Bursts.Add(b);
    }

    public static void Tick()
    {
        if (Live.Count == 0 && Bursts.Count == 0) return;
        if (GameClock.ShipGen != _gen) { Live.Clear(); Bursts.Clear(); _container = null; return; }
        float now = Time.time, dt = Time.deltaTime;
        float mx = 0f, my = 0f;
        bool haveMe = false;
        var lp = PlayerControl.LocalPlayer;
        if (lp)
        {
            var p = lp.transform.position;
            mx = p.x; my = p.y; haveMe = true;
        }

        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var h = Live[i];
            if (!h.Root) { Live.RemoveAt(i); continue; }
            float age = now - h.Born;
            if (h.Closing >= 0f)
            {
                float k = (now - h.Closing) / CloseTime;
                if (k >= 1f) { Live.RemoveAt(i); UnityEngine.Object.Destroy(h.Root); continue; }
                float q = 1f - k;
                h.Body.localScale = FxMath.V3(W * q * q, H * (1f + 0.15f * k), 1f);
                h.Floor.color = FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0.3f * q);
                continue;
            }
            float dx = h.X - mx, dy = h.Y - my;
            if (haveMe && dx * dx + dy * dy > AnimRange * AnimRange && age > OpenTime) continue;

            if (age < OpenTime + 0.05f)
            {
                // 縦の線から横へ広がり、少し行き過ぎて戻る
                float k = FxMath.Clamp01(age / OpenTime);
                float e = 1f - (1f - k) * (1f - k) * (1f - k);
                h.Body.localScale = FxMath.V3(W * FxMath.Max(e * (1f + 0.06f * FxMath.Sin(FxMath.PI * k)), 0.02f), H, 1f);
            }

            h.Spin += dt;
            float s = h.Spin;
            h.SwirlA.localRotation = FxMath.RotZ(-110f * s);
            h.SwirlB.localRotation = FxMath.RotZ(70f * s + 40f);
            // にじみと芯は呼吸する (周期の違う 2 つの波を足して単調にしない)
            float breathe = 0.5f + 0.3f * FxMath.Sin(s * 2.3f) + 0.2f * FxMath.Sin(s * 3.7f + 1f);
            h.Halo.color = FxMath.Rgba(Teal.r, Teal.g, Teal.b, 0.3f + 0.2f * breathe);
            h.Core.color = FxMath.Rgba(White.r, White.g, White.b, 0.65f + 0.3f * breathe);

            // 光の粒: 縁から渦を巻いて芯へ吸い込まれる
            for (int m = 0; m < Motes; m++)
            {
                float t = FxMath.Repeat(s * 0.55f + m / (float)Motes, 1f);
                float r = (1f - t) * 0.46f;
                float a = m * (FxMath.PI * 2f / Motes) - t * FxMath.PI * 3f;
                h.Mote[m].localPosition = FxMath.V3(r * FxMath.Cos(a), r * FxMath.Sin(a), -LayerStep * 5f);
                h.MoteSr[m].color = FxMath.Rgba(White.r, White.g, White.b, FxMath.Sin(FxMath.PI * t) * 0.9f);
            }
        }

        for (int i = Bursts.Count - 1; i >= 0; i--)
        {
            var b = Bursts[i];
            float k = (now - b.Born) / PassTime;
            if (k >= 1f || !b.Root)
            {
                Bursts.RemoveAt(i);
                if (b.Root) UnityEngine.Object.Destroy(b.Root);
                continue;
            }
            // 一瞬で明るくなり、膨らみながら薄れる。縦の光は上へ伸びて抜ける
            float rise = k < 0.12f ? k / 0.12f : 1f - (k - 0.12f) / 0.88f;
            float grow = 1f - (1f - k) * (1f - k);
            b.Glow.localScale = FxMath.V3(0.4f + 1.4f * grow, 0.5f + 1.9f * grow, 1f);
            b.GlowSr.color = FxMath.Rgba(FxMath.Lerp(White.r, Teal.r, k), FxMath.Lerp(White.g, Teal.g, k), FxMath.Lerp(White.b, Teal.b, k), 0.9f * rise);
            b.Column.localScale = FxMath.V3(0.3f * (1f - 0.5f * k), 0.6f + 2.2f * grow, 1f);
            b.Column.localPosition = FxMath.V3(0f, 0.2f + 0.9f * grow, -LayerStep);
            b.ColumnSr.color = FxMath.Rgba(Blue.r * 0.5f + 0.5f * Teal.r, Teal.g, Teal.b, 0.6f * rise);
        }
    }

    // 船ごとに 1 つの入れ物 (船と一緒に消える)。船の倍率と反転は打ち消す
    private static GameObject Container()
    {
        var ship = ShipStatus.Instance;
        if (!ship || !GameClock.ShipAlive) return null;
        if (_gen == GameClock.ShipGen && _container) return _container;
        Live.Clear();
        Bursts.Clear();
        EnsureSprites();
        _container = new GameObject("MrpPortals");
        _container.transform.SetParent(ship.transform, false);
        Vector3 ls = ship.transform.lossyScale;
        _container.transform.localScale = FxMath.V3(1f / ls.x, 1f / ls.y, 1f / ls.z);
        _gen = GameClock.ShipGen;
        return _container;
    }

    private static SpriteRenderer Layer(Transform parent, string name, Sprite sp, Vector3 pos, Vector3 scale, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        sr.color = color;
        return sr;
    }

    // 絵は起動中に 1 回だけ作り、試合をまたいで使い回す (白で作り、色は頂点色で付ける)
    private static void EnsureSprites()
    {
        _glow ??= Make(64, Glow);
        _swirl ??= Make(128, Swirl);
        _disc ??= Make(64, Disc);
    }

    // 真ん中から縁へなめらかに薄れる丸
    private static void Glow(float u, float ang, out float value, out float alpha)
    {
        float q = FxMath.Clamp01(1f - u);
        value = 1f;
        alpha = q * q * (3f - 2f * q);
    }

    // 縁だけぼかした塗りつぶしの丸 (ポータルの奥の暗い穴)
    private static void Disc(float u, float ang, out float value, out float alpha)
    {
        float q = FxMath.Clamp01((1f - u) / 0.2f);
        value = 1f;
        alpha = q * q * (3f - 2f * q);
    }

    // 3 本の腕の渦と明るい縁。真ん中は芯の絵に任せて薄くする
    private static void Swirl(float u, float ang, out float value, out float alpha)
    {
        float arm = 0.5f + 0.5f * FxMath.Cos(3f * ang + 7.5f * u);
        arm = arm * arm * arm;
        float inner = FxMath.Clamp01((u - 0.08f) / 0.3f);
        float outer = 1f - FxMath.Clamp01((u - 0.84f) / 0.16f);
        float rimD = (u - 0.88f) / 0.07f;
        float rim = FxMath.Exp(-rimD * rimD);
        value = FxMath.Clamp01(0.55f + 0.45f * arm + 0.3f * rim);
        alpha = FxMath.Clamp01(arm * inner * outer * 0.95f + rim * 0.9f + 0.12f * inner * outer);
    }

    private delegate void Shade(float u, float ang, out float value, out float alpha);

    private static unsafe Sprite Make(int n, Shade shade)
    {
        var px = new byte[n * n * 4];
        float c = (n - 1) * 0.5f, rad = n * 0.5f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float dx = x - c, dy = y - c;
            float u = FxMath.Sqrt(dx * dx + dy * dy) / rad;
            float value = 0f, alpha = 0f;
            if (u < 1f) shade(u, FxMath.Atan2(dy, dx), out value, out alpha);
            int i = (y * n + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = (byte)(255f * value);
            px[i + 3] = (byte)(255f * alpha);
        }
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "MrpPortal", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        fixed (byte* b = px) tex.LoadRawTextureData((IntPtr)b, px.Length);
        tex.Apply(false, true);
        tex.hideFlags |= HideFlags.HideAndDontSave;
        var sp = UnityEngine.Sprite.Create(tex, new Rect(0, 0, n, n), FxMath.V2(0.5f, 0.5f), n, 0, SpriteMeshType.FullRect);
        sp.hideFlags |= HideFlags.HideAndDontSave;
        return sp;
    }
}
