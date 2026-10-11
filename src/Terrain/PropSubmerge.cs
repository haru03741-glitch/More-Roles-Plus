using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 水に浸かった家具の絵に水面を描く: 家具の絵 (部屋の絵から持ち上げた家具は切り抜きの形) を型にした板を家具の手前に置き、
// 足元から水の深さぶん上までを周りと同じ水の色で覆う (形と色は水のシェーダの _Mode 3)。
// 天板の上まで水が来たら家具ごと覆い、家具は水越しの影として残す。
// 板は家具の子なので動きには毎フレーム触らず、水面の線と沈み具合だけを 0.1 秒ごとに見直す
internal static partial class PropSim
{
    private const long SunkScanMs = 100;
    private const float SunkShow = 0.03f;    // 周りの水がこれより浅いと出さない
    private const float SunkTopWet = 0.02f;  // 天板の上の水がこれを越えたら沈み始める
    private const float SunkFade = 0.12f;    // 沈み始めてから家具ごと覆うまでの水の厚さ
    private const float SunkProbe = 0.12f;   // 足元の箱の外側の深さを見る距離
    private const float SunkStep = 0.01f;    // 水面の線がこれだけ動いたら書き直す
    private const float SunkFrontZ = -0.0005f; // 家具の絵のすぐ手前
    private const float SunkShadowDz = -0.001f; // 影の中の写しの前後: 水の写し (-0.002) より手前・火の床の写し (0) より奥

    private sealed class Sunk
    {
        public GameObject Go;
        public SpriteRenderer Sr, Shadow; // Shadow = 影の中に見せる写し (ShadowView)
        public MaterialPropertyBlock Block;
        public Sprite Own;          // 切り抜きの形から作った絵 (片付けで消す)
        public float Line = float.NaN, Cover = -1f;
        public float Above;         // 絵の上の縁が足元の箱の下の縁から何単位上か (作った時の姿)
        public bool Shown, Failed;
    }

    private static readonly List<Prop> Furnished = new(); // 高さの表に載った家具 (中の詰まった物と脚のある物)
    private static readonly int MainTexId = Shader.PropertyToID("_MainTex"), LineId = Shader.PropertyToID("_Line"), CoverId = Shader.PropertyToID("_Cover"), TopId = Shader.PropertyToID("_Top"), TopWId = Shader.PropertyToID("_TopW"), SilRId = Shader.PropertyToID("_SilR");
    private static Material _sunkMat;
    private static long _sunkNext;
    private static int _sunkGen = -1;
    private static bool _sunkAny, _sunkForce; // _sunkForce = 確認用に全部の板を沈めたまま止める (bridge `props sunk force`)

    internal static void SunkTick()
    {
        try { SunkCore(); }
        catch (Exception e) { Plugin.Logger.LogError($"[PropSim] sunk: {e}"); SunkClear(); }
    }

    private static void SunkCore()
    {
        if (_sunkForce || !_sunkAny && !WaterSim.Running) return;
        long now = Environment.TickCount64;
        if (now < _sunkNext) return;
        _sunkNext = now + SunkScanMs;
        if (GameClock.ShipGen != _sunkGen) { SunkClear(); _sunkGen = GameClock.ShipGen; }
        if (!_ready || !WaterSim.Running || !WaterSim.Ready || !MrpBundle.WadeMaterial) { SunkHideAll(); return; }
        foreach (var p in Furnished)
        {
            var s = p.Sk;
            if (!SunkDepth(p, out float by, out float around, out float over))
            {
                if (s != null && s.Shown) { if (s.Go) s.Go.SetActive(false); s.Shown = false; }
                continue;
            }
            if (s == null || (!s.Go && !s.Failed)) s = p.Sk = SunkMake(p, by);
            if (s.Failed) continue;
            if (!s.Shown) { s.Go.SetActive(true); s.Shown = true; _sunkAny = true; SunkShadow(s); }
            // 天板の上に水が乗ったら、線を絵の上の縁より上へ (家具ごと水の下)。それまでは足元から周りの深さぶん
            float cover = over > SunkTopWet ? Math.Min(1f, (over - SunkTopWet) / SunkFade) : 0f;
            float line = cover > 0f ? by + s.Above + over : by + (p.Legs ? around : Math.Min(around, p.Top / (float)WaterSim.Full));
            if (MathF.Abs(line - s.Line) < SunkStep && MathF.Abs(cover - s.Cover) < 0.05f) continue;
            s.Line = line;
            s.Cover = cover;
            s.Block.SetFloat(LineId, line);
            s.Block.SetFloat(CoverId, cover);
            s.Block.SetFloat(TopId, by + p.Top / (float)WaterSim.Full);
            s.Block.SetFloat(TopWId, Math.Max(over, 0f));
            s.Block.SetFloat(SilRId, p.Lift != null ? 1f : 0f);
            s.Sr.SetPropertyBlock(s.Block);
            if (s.Shadow) s.Shadow.SetPropertyBlock(s.Block);
        }
    }

    // 影の中: 板は影の板の裏になるので、影の板の手前に写しを置く (水の写しは家具の形を抜いてあるので、その上に沈み具合を描く)。
    // 出すたびに呼ぶ: 影のカメラがまだ無くて作れなかった写しを作り直し、家具の前後 (z) が変わって影の板の裏へ回った写しを手前へ置き直す
    private static void SunkShadow(Sunk s)
    {
        if (!ShadowView.Ready(out float qz)) return;
        if (!s.Shadow)
        {
            var sh = ShadowView.Copy(s.Go, s.Sr.sprite, ShadowView.Sunk(_sunkMat), FxMath.Rgba(1f, 1f, 1f, 1f), SunkShadowDz);
            if (!sh) return;
            sh.flipX = s.Sr.flipX;
            sh.flipY = s.Sr.flipY;
            sh.enabled = true;
            sh.SetPropertyBlock(s.Block);
            s.Shadow = sh;
            return;
        }
        var tr = s.Shadow.transform;
        var q = tr.position;
        if (MathF.Abs(q.z - (qz - SunkShadowDz)) > 1e-4f) tr.position = FxMath.V3(q.x, q.y, qz - SunkShadowDz);
    }

    // 見せる姿 (D) の足元の箱の下の縁 (世界の y)・周りの水の深さ・天板より上の水の厚さ。出さない時は false
    private static bool SunkDepth(Prop p, out float by, out float around, out float over)
    {
        by = around = over = 0f;
        if (!FootBox(p, p.D, out int cx, out int cy, out int hx, out int hy, out int top)) return false;
        float x = _org.x + cx / (float)Unit, y = _org.y + cy / (float)Unit, wx = hx / (float)Unit, wy = hy / (float)Unit;
        by = y - wy;
        around = Math.Max(WaterSim.CurrentAt(FxMath.V2(x, by - SunkProbe), out _, out _),
            Math.Max(WaterSim.CurrentAt(FxMath.V2(x - wx - SunkProbe, y), out _, out _), WaterSim.CurrentAt(FxMath.V2(x + wx + SunkProbe, y), out _, out _)));
        // 中の詰まった家具の上の升の水は天板の上の水。脚のある家具は下を通る水が天板を越えた分
        float mid = WaterSim.CurrentAt(FxMath.V2(x, y), out _, out _);
        over = p.Legs ? Math.Max(around, mid) - top / (float)WaterSim.Full : mid;
        return around >= SunkShow || over > SunkTopWet;
    }

    private static Sunk SunkMake(Prop p, float by)
    {
        var s = new Sunk();
        Transform parent;
        Sprite sp;
        Vector3 local = FxMath.V3(0f, 0f, SunkFrontZ), scale = FxMath.V3(1f, 1f, 1f);
        bool flipX = false, flipY = false;
        if (p.Lift != null)
        {
            var l = p.Lift;
            if (!l.Cut || !l.Shape) { s.Failed = true; return s; }
            parent = l.Cut;
            // 形は四角い画素なので、幅に合わせた大きさで作って縦を伸ばす
            var w = l.ShapeWorld;
            sp = s.Own = Sprite.Create(l.Shape, new Rect(0, 0, l.Shape.width, l.Shape.height), FxMath.V2(0.5f, 0.5f), l.Shape.width / w.width, 0, SpriteMeshType.FullRect);
            sp.name = "MrpSunk";
            local = FxMath.V3(l.ShapeOff.x, l.ShapeOff.y, SunkFrontZ);
            scale = FxMath.V3(l.ShapeScale.x, l.ShapeScale.y * (w.height / w.width) * (l.Shape.width / (float)l.Shape.height), 1f);
        }
        else
        {
            var src = p.Tr ? p.Tr.GetComponent<SpriteRenderer>() : null;
            if (!src || !src.sprite) { s.Failed = true; return s; }
            parent = p.Tr;
            sp = src.sprite;
            flipX = src.flipX;
            flipY = src.flipY;
        }
        if (!_sunkMat)
        {
            _sunkMat = new Material(MrpBundle.WadeMaterial) { name = "MrpSunk", hideFlags = HideFlags.DontUnloadUnusedAsset };
            _sunkMat.SetFloat("_Mode", 3f);
        }
        var go = new GameObject("MrpSunk") { layer = parent.gameObject.layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = local;
        go.transform.localScale = scale;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        sr.flipX = flipX;
        sr.flipY = flipY;
        sr.sharedMaterial = _sunkMat;
        s.Go = go;
        s.Sr = sr;
        s.Block = new MaterialPropertyBlock();
        // SpriteRenderer にブロックを付けるとスプライトの絵が外れる (シェーダの既定の黒になり、型が無くて何も描かれない) ので、絵もブロックで渡す
        s.Block.SetTexture(MainTexId, sp.texture);
        s.Above = Math.Max(sr.bounds.max.y - by, p.Top / (float)WaterSim.Full);
        go.SetActive(false);
        return s;
    }

    // 水の影の写しで抜く絵 (r と重なり、水 z より手前の物)。小物は水に流されて動き続けるので入れない。
    // 部屋の絵から持ち上げた家具は床マスクが抜くので入れない
    internal static void FrontSprites(Rect r, float z, List<SpriteRenderer> into)
    {
        if (!_ready) return;
        foreach (var p in Props)
        {
            if (p.Kind == Kind.Small || p.Lift != null || p.Hidden || !p.Tr) continue;
            if (p.Front == null) FrontOf(p);
            // 足元から絵の端まで (FrontR) で先に間引く (絵の範囲を引くのは本編の呼び出しなので、掛かりそうな物だけ)
            var c = p.FrontAt;
            if (c.x + p.FrontR <= r.xMin || c.x - p.FrontR >= r.xMax || c.y + p.FrontR <= r.yMin || c.y - p.FrontR >= r.yMax) continue;
            foreach (var sr in p.Front)
            {
                if (!sr || !sr.enabled || !sr.sprite || !sr.gameObject.activeInHierarchy) continue;
                var b = sr.bounds;
                if (b.max.x <= r.xMin || b.min.x >= r.xMax || b.max.y <= r.yMin || b.min.y >= r.yMax) continue;
                if (sr.transform.position.z >= z) continue;
                into.Add(sr);
            }
        }
    }

    private static void FrontOf(Prop p)
    {
        var list = new List<SpriteRenderer>();
        var at = p.Tr.position;
        float r = 0f;
        foreach (var sr in p.Tr.GetComponentsInChildren<SpriteRenderer>(false))
        {
            if (sr.gameObject.name.StartsWith("Mrp", StringComparison.Ordinal)) continue;
            list.Add(sr);
            var b = sr.bounds;
            r = Math.Max(r, MathF.Sqrt((b.center.x - at.x) * (b.center.x - at.x) + (b.center.y - at.y) * (b.center.y - at.y)) + b.extents.magnitude);
        }
        p.Front = list.ToArray();
        p.FrontAt = at;
        p.FrontR = r + 0.1f;
    }

    // 動いた家具が止まった: 元いた所と止まった所の抜き型を作り直す
    private static void FrontSettled(Prop p)
    {
        if (p.Kind == Kind.Small || p.Lift != null || !p.Tr || p.Front == null) return;
        var at = p.Tr.position;
        WaterArt.FrontMoved(FxMath.V2(p.FrontAt.x, p.FrontAt.y), FxMath.V2(at.x, at.y), p.FrontR);
        p.FrontAt = at;
    }

    private static void SunkReport(bool force, Action<string> reply)
    {
        int n = 0;
        _sunkForce = force;
        foreach (var p in Furnished)
        {
            bool wet = SunkDepth(p, out float by, out float around, out float over);
            var s = p.Sk;
            if (!wet && s == null) continue;
            n++;
            reply($"SUNK {p.Name} legs={p.Legs} by={by:0.###} around={around:0.###} over={over:0.###} shown={s?.Shown} failed={s?.Failed} line={s?.Line:0.###} cover={s?.Cover:0.##} above={s?.Above:0.###}");
            if (force && s != null && s.Sr) { s.Block.SetFloat(LineId, 100f); s.Block.SetFloat(CoverId, 1f); s.Sr.SetPropertyBlock(s.Block); if (s.Shadow) s.Shadow.SetPropertyBlock(s.Block); s.Line = float.NaN; }
        }
        reply($"OK props sunk n={n} of {Furnished.Count}");
    }

    private static void SunkHideAll()
    {
        foreach (var p in Furnished)
            if (p.Sk != null && p.Sk.Shown) { if (p.Sk.Go) p.Sk.Go.SetActive(false); p.Sk.Shown = false; }
        _sunkAny = false;
    }

    private static void SunkClear()
    {
        foreach (var p in Furnished)
        {
            var s = p.Sk;
            if (s == null) continue;
            if (s.Go) UnityEngine.Object.Destroy(s.Go);
            if (s.Own) UnityEngine.Object.Destroy(s.Own);
            p.Sk = null;
        }
        _sunkAny = false;
    }
}
