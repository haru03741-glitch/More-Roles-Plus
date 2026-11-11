using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 水に浸かったクルーの体に水面を描く: 足元の水の深さぶん上までを周りと同じ水の色で覆い、まわりに水面の輪を出す。
// 頭まで沈んだら体は水越しの影として残し、泡を昇らせる (形と色は水のシェーダの _Mode 2)。
// 板はクルーの子なので動きには毎フレーム触らず、深さと見せるかどうかだけを 0.1 秒ごとに見直す。
// 板はクルーと同じ奥行きにあるので、視界の外では本編の影の板に一緒に隠れる
internal static class WaterWade
{
    private const long ScanMs = 100;
    private const float ShowDepth = 0.03f;     // これより浅いと出さない
    private const float DepthStep = 0.01f;     // 深さがこれだけ変わったら板へ書き直す
    // 板の大きさと足元の位置 (シェーダの _Quad と同じ値)
    private const float QuadW = 0.84f, QuadH = 1.75f, QuadBelow = 0.15f;
    private const float FrontZ = -0.00012f;    // 体と帽子より手前 (奥行きは y の 1/1000 なので、大きいと手前の人の上にかかる)

    private sealed class Wade
    {
        public GameObject Go;
        public SpriteRenderer Sr;
        public MaterialPropertyBlock Block;
        public float Depth = -1f, Base;
        public bool Shown;
    }

    // 溺れている人: 水面に顔を出す/沈むのを、足元の深さに足す分 (毎フレーム) で見せる。Hide = 覆いを出さない (横たわった体は別の描き方)
    private static readonly float[] Extra = new float[256];
    private static readonly bool[] Hidden = new bool[256];
    private static readonly List<byte> Bobbing = new();

    internal static void Bob(byte id, float extra, bool hide)
    {
        Extra[id] = extra;
        Hidden[id] = hide;
        if (!Bobbing.Contains(id)) Bobbing.Add(id);
    }

    internal static void EndBob(byte id)
    {
        Extra[id] = 0f;
        Hidden[id] = false;
        Bobbing.Remove(id);
        if (Wades.TryGetValue(id, out var w)) w.Depth = -1f; // 次の見直しで足元の深さを書き直す
    }

    // 溺れている人の覆いは毎フレーム書く (深さの揺れが速いので 0.1 秒ごとでは跳ぶ)
    private static void BobTick()
    {
        for (int i = 0; i < Bobbing.Count; i++)
        {
            byte id = Bobbing[i];
            if (!Wades.TryGetValue(id, out var w) || !w.Go) continue;
            bool show = w.Base >= ShowDepth && !Hidden[id];
            if (show != w.Shown) { w.Go.SetActive(show); w.Shown = show; }
            if (!show) continue;
            float d = FxMath.Max(0f, w.Base + Extra[id]);
            if (MathF.Abs(d - w.Depth) < 0.002f) continue;
            w.Depth = d;
            w.Block.SetFloat(DepthId, d);
            w.Sr.SetPropertyBlock(w.Block);
        }
    }

    private static readonly Dictionary<byte, Wade> Wades = new();
    // 体の大きさ (足元から・実測) はシェーダの _Body の既定値。Android の libunity には MaterialPropertyBlock.SetVector が無い
    private static readonly int DepthId = Shader.PropertyToID("_Depth");
    private static Sprite _sprite;
    private static int _shipGen = -1;
    private static long _nextMs;

    public static void Tick()
    {
        try { TickCore(); }
        catch (Exception e) { Plugin.Logger.LogError($"[WaterWade] {e}"); Clear(); }
    }

    private static void TickCore()
    {
        if (Wades.Count == 0 && !WaterSim.Running) return;
        if (Bobbing.Count > 0 && WaterSim.Running && WaterSim.Ready) BobTick();
        long now = Environment.TickCount64;
        if (now < _nextMs) return;
        _nextMs = now + ScanMs;
        if (GameClock.ShipGen != _shipGen) { Clear(); _shipGen = GameClock.ShipGen; }
        if (!WaterSim.Running || !WaterSim.Ready || !MrpBundle.WadeMaterial) { HideAll(); return; }

        var all = PlayerControl.AllPlayerControls;
        for (int i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (!pc) continue;
            Wades.TryGetValue(pc.PlayerId, out var w);
            var data = pc.Data;
            // 深さを先に見て、水に入っている人だけ見せてよいかを調べる
            float d = WaterSim.CurrentAt(pc.GetTruePosition(), out _, out _);
            if (d >= ShowDepth && (data == null || data.IsDead || data.Disconnected || pc.inVent || !pc.Visible || !BodyShown(pc)
                || Roles.CrewTraits.Has(pc.PlayerId, Roles.CrewTraits.Trait.Skim))) d = 0f; // 水面を走る人は沈まない
            if (w != null) w.Base = d;
            byte id = pc.PlayerId;
            if (d < ShowDepth || Hidden[id])
            {
                if (w != null && w.Shown && w.Go) { w.Go.SetActive(false); w.Shown = false; }
                continue;
            }
            if (w == null || !w.Go) { w = Wades[id] = Make(pc); w.Base = d; }
            if (!w.Shown) { w.Go.SetActive(true); w.Shown = true; }
            d = FxMath.Max(0f, d + Extra[id]);
            if (MathF.Abs(d - w.Depth) < DepthStep) continue;
            w.Depth = d;
            w.Block.SetFloat(DepthId, d);
            w.Sr.SetPropertyBlock(w.Block);
        }
    }

    // 透明になる役職は体の絵を薄くして消える。消えている体に水の形を出すと居場所が分かる
    private static bool BodyShown(PlayerControl pc)
    {
        var body = pc.cosmetics ? pc.cosmetics.currentBodySprite : null;
        var sr = body != null ? body.BodySprite : null;
        return sr && sr.enabled && sr.color.a > 0.5f;
    }

    private static Wade Make(PlayerControl pc)
    {
        var t = pc.transform;
        Vector3 at = t.position, scale = t.lossyScale;
        Vector2 feet = pc.GetTruePosition();
        var go = new GameObject("MrpWade") { layer = pc.gameObject.layer };
        go.transform.SetParent(t, false);
        float sx = scale.x != 0f ? 1f / scale.x : 1f, sy = scale.y != 0f ? 1f / scale.y : 1f;
        go.transform.localPosition = FxMath.V3((feet.x - at.x) * sx, (feet.y - at.y) * sy, FrontZ);
        go.transform.localScale = FxMath.V3(QuadW * sx, QuadH * sy, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = Sprite();
        sr.sharedMaterial = MrpBundle.WadeMaterial;
        var w = new Wade { Go = go, Sr = sr, Block = new MaterialPropertyBlock() };
        go.SetActive(false);
        return w;
    }

    // 1 単位四方の白い絵 (足元が下から QuadBelow のところ)
    private static unsafe Sprite Sprite()
    {
        if (_sprite) return _sprite;
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "MrpWade", hideFlags = HideFlags.DontUnloadUnusedAsset };
        byte* px = stackalloc byte[4] { 255, 255, 255, 255 };
        tex.LoadRawTextureData((IntPtr)px, 4);
        tex.Apply(false, true);
        _sprite = UnityEngine.Sprite.Create(tex, new Rect(0, 0, 1, 1), FxMath.V2(0.5f, QuadBelow / QuadH), 1f, 0, SpriteMeshType.FullRect);
        _sprite.name = "MrpWade";
        _sprite.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        return _sprite;
    }

    private static void HideAll()
    {
        foreach (var w in Wades.Values)
            if (w.Shown) { if (w.Go) w.Go.SetActive(false); w.Shown = false; }
    }

    internal static void Clear()
    {
        Bobbing.Clear();
        Array.Clear(Extra);
        Array.Clear(Hidden);
        foreach (var w in Wades.Values) if (w.Go) UnityEngine.Object.Destroy(w.Go);
        Wades.Clear();
    }
}
