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
    private const float SunkFade = 0.06f;    // 沈み始めてから家具ごと覆うまでの水の厚さ
    private const float SunkProbe = 0.12f;   // 足元の箱の外側の深さを見る距離
    private const float SunkStep = 0.01f;    // 水面の線がこれだけ動いたら書き直す
    private const float SunkFrontZ = -0.0005f; // 家具の絵のすぐ手前

    private sealed class Sunk
    {
        public GameObject Go;
        public SpriteRenderer Sr;
        public MaterialPropertyBlock Block;
        public Sprite Own;          // 切り抜きの形から作った絵 (片付けで消す)
        public float Line = float.NaN, Cover = -1f;
        public float Above;         // 絵の上の縁が足元の箱の下の縁から何単位上か (作った時の姿)
        public bool Shown, Failed;
    }

    private static readonly List<Prop> Furnished = new(); // 高さの表に載った家具 (中の詰まった物と脚のある物)
    private static readonly int LineId = Shader.PropertyToID("_Line"), CoverId = Shader.PropertyToID("_Cover"), SilRId = Shader.PropertyToID("_SilR");
    private static Material _sunkMat;
    private static long _sunkNext;
    private static int _sunkGen = -1;
    private static bool _sunkAny;

    internal static void SunkTick()
    {
        try { SunkCore(); }
        catch (Exception e) { Plugin.Logger.LogError($"[PropSim] sunk: {e}"); SunkClear(); }
    }

    private static void SunkCore()
    {
        if (!_sunkAny && !WaterSim.Running) return;
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
            if (!s.Shown) { s.Go.SetActive(true); s.Shown = true; _sunkAny = true; }
            // 天板の上に水が乗ったら、線を絵の上の縁より上へ (家具ごと水の下)。それまでは足元から周りの深さぶん
            float cover = over > SunkTopWet ? Math.Min(1f, (over - SunkTopWet) / SunkFade) : 0f;
            float line = cover > 0f ? by + s.Above + over : by + (p.Legs ? around : Math.Min(around, p.Top / (float)WaterSim.Full));
            if (MathF.Abs(line - s.Line) < SunkStep && MathF.Abs(cover - s.Cover) < 0.05f) continue;
            s.Line = line;
            s.Cover = cover;
            s.Block.SetFloat(LineId, line);
            s.Block.SetFloat(CoverId, cover);
            s.Block.SetFloat(SilRId, p.Lift != null ? 1f : 0f);
            s.Sr.SetPropertyBlock(s.Block);
        }
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
        s.Above = Math.Max(sr.bounds.max.y - by, p.Top / (float)WaterSim.Full);
        go.SetActive(false);
        return s;
    }

    private static void SunkReport(Action<string> reply)
    {
        int n = 0;
        foreach (var p in Furnished)
        {
            bool wet = SunkDepth(p, out float by, out float around, out float over);
            var s = p.Sk;
            if (!wet && s == null) continue;
            n++;
            reply($"SUNK {p.Name} legs={p.Legs} by={by:0.###} around={around:0.###} over={over:0.###} shown={s?.Shown} failed={s?.Failed} line={s?.Line:0.###} cover={s?.Cover:0.##} above={s?.Above:0.###}");
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
