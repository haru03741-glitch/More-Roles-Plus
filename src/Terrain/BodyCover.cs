using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 水に浸かった横たわる体 (死体・溺れて倒れた体) を水で覆う: 体と同じ絵を型にした水の板を体のすぐ手前に重ね、
// 足元の水が体の厚み (Thick) を越えた分だけ周りの水と同じ色で覆う (家具の水面と同じシェーダの「低い机」の作り)。
// 立った体の水面 (WaterWade) は縦長の体の形で覆うので、横たわった体には合わない。
// 死体のそばに落ちて死体の子になった帽子・服も、薄い物として同じように覆う。
// 死体は水がある間だけ 0.25 秒ごとに足元の深さを見直し、死んだ人の数が変わった時と数秒おきに死体を探し直す。
// 死体が倒れる動き (本編の連番) の間は、板の絵を毎フレーム体の絵にそろえる
internal static class BodyCover
{
    internal const float Thick = 0.2f;          // 横たわった体の厚み (水の深さの単位)。これより浅い水では覆わない
    private const float PieceThick = 0.08f;     // 死体のそばに落ちた帽子・服の厚み
    private const float ScanEvery = 0.25f, FindEvery = 3f, SyncFor = 2.5f;
    private const float LvlStep = 0.01f;        // 水位がこれだけ変わったら板へ書き直す
    private const float FrontZ = -0.0006f;      // 体の絵とその上の着せ替えより手前

    private static readonly int MainTexId = Shader.PropertyToID("_MainTex"), LvlId = Shader.PropertyToID("_Lvl"),
        CoverId = Shader.PropertyToID("_Cover"), FaceHId = Shader.PropertyToID("_FaceH"), FlatTopId = Shader.PropertyToID("_FlatTop"),
        SilRId = Shader.PropertyToID("_SilR"), BaseOnId = Shader.PropertyToID("_BaseOn");
    private static Material _mat;

    internal sealed class Cover
    {
        public SpriteRenderer Src, Sr;
        public MaterialPropertyBlock Block;
        public Sprite Last;
        public Texture Tex;
        public float Lvl = -1f, Thick;
        public bool Shown;
    }

    // src の手前に覆いの板を作る (隠したまま)。材質が無ければ null
    internal static Cover Make(SpriteRenderer src, float thick = Thick)
    {
        var mat = Mat();
        if (!mat || !src) return null;
        var go = new GameObject("MrpBodyCover") { layer = src.gameObject.layer };
        go.SetActive(false);
        var t = go.transform;
        t.SetParent(src.transform, false);
        t.localPosition = FxMath.V3(0f, 0f, FrontZ);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sharedMaterial = mat;
        sr.sortingOrder = src.sortingOrder;
        var c = new Cover { Src = src, Sr = sr, Block = new MaterialPropertyBlock(), Thick = thick };
        // SpriteRenderer にブロックを付けると絵が外れるので、絵もブロックで渡す
        c.Block.SetFloat(FlatTopId, 1f);
        c.Block.SetFloat(FaceHId, thick);
        c.Block.SetFloat(CoverId, 1f);
        c.Block.SetFloat(SilRId, 0f);
        c.Block.SetFloat(BaseOnId, 0f);
        Sync(c);
        return c;
    }

    // 体の絵が変わっていたら板の絵 (と型の絵) をそろえる
    internal static void Sync(Cover c)
    {
        if (c == null || !c.Src || !c.Sr) return;
        var sp = c.Src.sprite;
        bool flip = c.Src.flipX;
        if (c.Sr.flipX != flip) c.Sr.flipX = flip;
        if (sp == c.Last) return;
        c.Last = sp;
        c.Sr.sprite = sp;
        var tex = sp ? sp.texture : null;
        if (tex == c.Tex) return;
        c.Tex = tex;
        if (tex) c.Block.SetTexture(MainTexId, tex);
        c.Sr.SetPropertyBlock(c.Block);
    }

    // 足元から測った水位 (単位)。体の厚みより浅ければ隠す
    internal static void SetLevel(Cover c, float lvl)
    {
        if (c == null || !c.Sr) return;
        bool show = lvl > c.Thick + 0.005f && c.Tex;
        if (show != c.Shown) { c.Sr.gameObject.SetActive(show); c.Shown = show; }
        if (!show || MathF.Abs(lvl - c.Lvl) < LvlStep) return;
        c.Lvl = lvl;
        c.Block.SetFloat(LvlId, lvl);
        c.Sr.SetPropertyBlock(c.Block);
    }

    internal static void Destroy(Cover c)
    {
        if (c != null && c.Sr) UnityEngine.Object.Destroy(c.Sr.gameObject);
    }

    private static Material Mat()
    {
        if (_mat) return _mat;
        var src = MrpBundle.WadeMaterial;
        if (!src) return null;
        _mat = new Material(src) { name = "MrpBodyCover", hideFlags = HideFlags.DontUnloadUnusedAsset };
        _mat.SetFloat("_Mode", 3f);
        return _mat;
    }

    // ── 死体 (全員の端末で) ─────────────────────────────────────────────
    private sealed class Corpse
    {
        public DeadBody Body;
        public Cover C;
        public float Born;
        public int Kids = -1;
        public readonly List<Cover> Pieces = new(); // 死体の子になった落ちた帽子・服
    }

    private static readonly List<Corpse> Corpses = new();
    private static float _nextScan, _nextFind;
    private static int _dead = -1, _shipGen = -1;

    public static void Tick()
    {
        try { TickCore(); }
        catch (Exception e) { Plugin.Logger.LogError($"[BodyCover] {e}"); Clear(); }
    }

    private static void TickCore()
    {
        bool water = WaterSim.Running && WaterSim.Ready;
        if (!water && Corpses.Count == 0) return;
        if (GameClock.ShipGen != _shipGen) { Clear(); _shipGen = GameClock.ShipGen; }
        float now = Time.time;
        // 倒れる動きの間は絵を毎フレームそろえる
        for (int i = 0; i < Corpses.Count; i++)
        {
            var k = Corpses[i];
            if (k.C != null && k.C.Shown && now - k.Born < SyncFor) Sync(k.C);
        }
        if (now < _nextScan) return;
        _nextScan = now + ScanEvery;
        if (!water) { foreach (var k in Corpses) { SetLevel(k.C, 0f); foreach (var c in k.Pieces) SetLevel(c, 0f); } return; }
        int dead = DeadCount();
        if (dead != _dead || now >= _nextFind) { _dead = dead; _nextFind = now + FindEvery; Find(now); }
        for (int i = Corpses.Count - 1; i >= 0; i--)
        {
            var k = Corpses[i];
            if (!k.Body || k.C == null || !k.C.Src) { Drop(k); Corpses.RemoveAt(i); continue; }
            Sync(k.C);
            // 落ちた帽子・服は地面に着くと死体の子になる (子の数が変わった時だけ探す)
            int kids = k.Body.transform.childCount;
            if (kids != k.Kids) { k.Kids = kids; AddPieces(k); }
            float lvl = k.Body.isActiveAndEnabled ? WaterSim.CurrentAt(k.Body.TruePosition, out _, out _) : 0f;
            // 体の絵を隠している間 (決闘で殴り飛ばされた人形が飛んでいる間など) は板も出さない
            SetLevel(k.C, k.C.Src.enabled ? lvl : 0f);
            for (int j = 0; j < k.Pieces.Count; j++) { var c = k.Pieces[j]; SetLevel(c, c.Src && c.Src.enabled ? lvl : 0f); }
        }
    }

    private static int DeadCount()
    {
        int n = 0;
        var all = PlayerControl.AllPlayerControls;
        for (int i = 0; i < all.Count; i++) { var p = all[i]; if (p && p.Data != null && p.Data.IsDead) n++; }
        return n;
    }

    // 新しい死体に板を付ける (死体の描画物は本編の最初の絵 = bodyRenderers の先頭)
    private static void Find(float now)
    {
        foreach (var b in UnityEngine.Object.FindObjectsOfType<DeadBody>())
        {
            if (!b || Has(b)) continue;
            var rs = b.bodyRenderers;
            var main = rs != null && rs.Length > 0 ? rs[0] : null;
            if (!main) continue;
            var c = Make(main);
            if (c != null) Corpses.Add(new Corpse { Body = b, C = c, Born = now });
        }
    }

    private static void AddPieces(Corpse k)
    {
        var bt = k.Body.transform;
        for (int i = 0; i < bt.childCount; i++)
        {
            var ch = bt.GetChild(i);
            if (ch.name != "MrpFallenCosmetic") continue;
            foreach (var sr in ch.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (!sr || sr.gameObject.name == "MrpBodyCover" || Covered(k, sr)) continue;
                var c = Make(sr, PieceThick);
                if (c != null) k.Pieces.Add(c);
            }
        }
    }

    private static bool Covered(Corpse k, SpriteRenderer sr)
    {
        for (int i = 0; i < k.Pieces.Count; i++) if (k.Pieces[i].Src == sr) return true;
        return false;
    }

    private static void Drop(Corpse k)
    {
        Destroy(k.C);
        foreach (var c in k.Pieces) Destroy(c);
        k.Pieces.Clear();
    }

    private static bool Has(DeadBody b)
    {
        for (int i = 0; i < Corpses.Count; i++) if (Corpses[i].Body == b) return true;
        return false;
    }

    internal static void Clear()
    {
        foreach (var k in Corpses) Drop(k);
        Corpses.Clear();
        _dead = -1;
        _nextFind = 0f;
    }
}
