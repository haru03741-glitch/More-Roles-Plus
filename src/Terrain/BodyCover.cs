using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 水に浸かった体や物のうち、立った体の水面 (WaterWade) の形が合わない物を水で覆う:
// その物と同じ絵 (切った死体の片は同じ網目) を型にした水の板をすぐ手前に重ね、周りの水と同じ色で覆う (家具の水面と同じシェーダ)。
// 寝ている物 (死体・倒れた体・落ちた帽子と服・血の跡) は厚みを越えた水の分だけ一面を覆い、
// 立っている物 (ペット) は絵の下の縁を床として、水位より下だけを覆う。
// 死体は死んだ知らせの直後から探して付け (水がある時だけ)、ほかは持ち主が Track で渡す。
// 水がある間だけ 0.25 秒ごとに足元の深さを見直す。絵が動く間 (倒れる連番・ペット) は板の絵を毎フレームそろえる
internal static class BodyCover
{
    internal const float Thick = 0.2f;          // 横たわった体の厚み (水の深さの単位)。これより浅い水では覆わない
    internal const float PieceThick = 0.08f;    // 落ちた帽子・服・切れた片の厚み
    private const float StainThick = 0.01f;     // 床の血の跡
    private const float UprightShow = 0.02f;    // 立っている物はこれより深ければ覆い始める
    private const float ScanEvery = 0.25f, FindEvery = 3f, SyncFor = 2.5f, Eager = 0.6f;
    private const float LvlStep = 0.01f;        // 水位がこれだけ変わったら板へ書き直す
    private const float FrontZ = -0.0006f;      // 元の絵とその上の着せ替えより手前

    private static readonly int MainTexId = Shader.PropertyToID("_MainTex"), LvlId = Shader.PropertyToID("_Lvl"),
        CoverId = Shader.PropertyToID("_Cover"), FaceHId = Shader.PropertyToID("_FaceH"), FlatTopId = Shader.PropertyToID("_FlatTop"),
        SilRId = Shader.PropertyToID("_SilR"), BaseOnId = Shader.PropertyToID("_BaseOn"), RiseId = Shader.PropertyToID("_Rise"),
        VPerUnitId = Shader.PropertyToID("_VPerUnit"), VBaseId = Shader.PropertyToID("_VBase");
    private static Material _mat;

    internal sealed class Cover
    {
        public Renderer SrcR, R;                // 元の描画物・板の描画物
        public SpriteRenderer Src, Sr;          // 絵の時だけ (切った片は網目)
        public MaterialPropertyBlock Block;
        public Sprite Last;
        public Texture Tex;
        public float Lvl = -1f, Thick, ScaleY = 1f, Until;
        public bool Shown, Upright, Freeze; // Freeze = 絵が変わったら外す (死んだ人の体に残った最後の絵)
    }

    // src の手前に覆いの板を作る (隠したまま)。upright = 立っている物 (水位より下だけ覆う)。材質が無ければ null
    internal static Cover Make(SpriteRenderer src, float thick = Thick, bool upright = false)
    {
        var mat = Mat();
        if (!mat || !src) return null;
        var go = NewPlate(src.transform, src.gameObject.layer);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sharedMaterial = mat;
        sr.sortingOrder = src.sortingOrder;
        Plates.Add(sr.Pointer);
        var c = new Cover { SrcR = src, R = sr, Src = src, Sr = sr, Block = NewBlock(thick, upright), Thick = upright ? UprightShow : thick, Upright = upright };
        if (upright) c.ScaleY = MathF.Abs(src.transform.lossyScale.y);
        Sync(c);
        return c;
    }

    // 網目の描画物 (切った死体の片) の手前に同じ網目の板を作る
    internal static Cover MakeMesh(MeshRenderer src, Mesh mesh, Texture tex, float thick = PieceThick)
    {
        var mat = Mat();
        if (!mat || !src || !mesh || !tex) return null;
        var go = NewPlate(src.transform, src.gameObject.layer);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.sortingOrder = src.sortingOrder;
        Plates.Add(mr.Pointer);
        var c = new Cover { SrcR = src, R = mr, Block = NewBlock(thick, false), Thick = thick, Tex = tex };
        c.Block.SetTexture(MainTexId, tex);
        mr.SetPropertyBlock(c.Block);
        return c;
    }

    // 板の描画物 (時戻しの写しはこれを写さない: 板の値はブロックにあり、写しでは正しく描けない)
    internal static readonly HashSet<IntPtr> Plates = new();

    private static GameObject NewPlate(Transform parent, int layer)
    {
        var go = new GameObject("MrpBodyCover") { layer = layer };
        go.SetActive(false);
        var t = go.transform;
        t.SetParent(parent, false);
        t.localPosition = FxMath.V3(0f, 0f, FrontZ);
        return go;
    }

    private static MaterialPropertyBlock NewBlock(float thick, bool upright)
    {
        var b = new MaterialPropertyBlock();
        b.SetFloat(FlatTopId, upright ? 0f : 1f);
        b.SetFloat(FaceHId, thick);
        b.SetFloat(CoverId, 1f);
        b.SetFloat(SilRId, 0f);
        b.SetFloat(BaseOnId, 0f);
        b.SetFloat(RiseId, 1f); // 立っている物は絵の縦がそのまま高さ
        return b;
    }

    // 元の絵が変わっていたら板の絵 (と型の絵・立っている物の床の線) をそろえる
    internal static void Sync(Cover c)
    {
        if (c == null || !c.Src || !c.Sr) return;
        var sp = c.Src.sprite;
        bool flip = c.Src.flipX;
        if (c.Sr.flipX != flip) c.Sr.flipX = flip;
        if (sp == c.Last) return;
        c.Last = sp;
        c.Sr.sprite = sp;
        bool dirty = false;
        var tex = sp ? sp.texture : null;
        if (tex != c.Tex)
        {
            c.Tex = tex;
            if (tex) { c.Block.SetTexture(MainTexId, tex); dirty = true; }
        }
        if (c.Upright && sp && UprightOf(sp, out float vBase, out float vPerUnit, out float height))
        {
            c.Block.SetFloat(VBaseId, vBase);
            c.Block.SetFloat(VPerUnitId, vPerUnit / FxMath.Max(c.ScaleY, 1e-4f));
            c.Block.SetFloat(FaceHId, height * c.ScaleY);
            dirty = true;
        }
        if (dirty) c.Sr.SetPropertyBlock(c.Block);
    }

    // 立っている絵の下の縁の v・絵の 1 単位あたりの v・絵の高さ (網目の頂点から。絵ごとに覚える)
    private static readonly Dictionary<IntPtr, (float vBase, float vPerUnit, float h)> UprightCache = new();
    private static bool UprightOf(Sprite sp, out float vBase, out float vPerUnit, out float height)
    {
        if (UprightCache.TryGetValue(sp.Pointer, out var u)) { vBase = u.vBase; vPerUnit = u.vPerUnit; height = u.h; return u.h > 0f; }
        vBase = vPerUnit = height = 0f;
        var vs = sp.vertices;
        var uv = sp.uv;
        float lowY = float.MaxValue, highY = float.MinValue, vLow = 0f, vHigh = 0f;
        for (int i = 0; i < vs.Length && i < uv.Length; i++)
        {
            float y = vs[i].y;
            if (y < lowY) { lowY = y; vLow = uv[i].y; }
            if (y > highY) { highY = y; vHigh = uv[i].y; }
        }
        if (highY - lowY > 1e-4f)
        {
            vBase = vLow;
            vPerUnit = (vHigh - vLow) / (highY - lowY);
            height = highY - lowY;
        }
        if (UprightCache.Count > 512) UprightCache.Clear();
        UprightCache[sp.Pointer] = (vBase, vPerUnit, height);
        return height > 0f;
    }

    // 元の物の下の水位 (単位)。厚みより浅ければ隠す
    internal static void SetLevel(Cover c, float lvl)
    {
        if (c == null || !c.R) return;
        bool show = lvl > c.Thick + 0.005f && c.Tex;
        if (show != c.Shown) { c.R.gameObject.SetActive(show); c.Shown = show; }
        if (!show || MathF.Abs(lvl - c.Lvl) < LvlStep) return;
        c.Lvl = lvl;
        c.Block.SetFloat(LvlId, lvl);
        c.R.SetPropertyBlock(c.Block);
    }

    internal static void Destroy(Cover c)
    {
        if (c == null) return;
        if (c.R) { Plates.Remove(c.R.Pointer); UnityEngine.Object.Destroy(c.R.gameObject); }
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

    private static bool Water => WaterSim.Running && WaterSim.Ready;

    // 元の物の下の水の深さ (寝ている物は真ん中・立っている物は下の縁)。元の物が隠れていれば 0
    private static float DepthUnder(Cover c)
    {
        var r = c.SrcR;
        if (!r || !r.enabled || !r.gameObject.activeInHierarchy) return 0f;
        // 薄れて消える物・透明になった人のペットには掛けない (水の形で居場所が分かる)
        if (c.Src && c.Src.color.a < 0.5f) return 0f;
        var b = r.bounds;
        var at = b.center;
        return WaterSim.CurrentAt(FxMath.V2(at.x, c.Upright ? b.min.y + 0.02f : at.y), out _, out _);
    }

    // ── 持ち主が渡す物 (死体になるまでの人形・落ちた着せ替え・切れた片・ペット) ─────────────
    private static readonly List<Cover> Loose = new();

    internal static void Track(SpriteRenderer src, float thick = Thick, bool upright = false)
    {
        if (!src || Tracked(src)) return;
        var c = Make(src, thick, upright);
        if (c == null) return;
        Loose.Add(c);
        if (Water) SetLevel(c, DepthUnder(c));
    }

    internal static void TrackMesh(MeshRenderer src, Mesh mesh, Texture tex, float thick = PieceThick)
    {
        if (!src || Tracked(src)) return;
        var c = MakeMesh(src, mesh, tex, thick);
        if (c == null) return;
        Loose.Add(c);
        if (Water) SetLevel(c, DepthUnder(c));
    }

    // 死んだ人の体の板を、本編が体を幽霊の絵へ替えるまで残す (死んだ直後は倒れた最後の絵のまま数フレーム見えている)
    internal static void Linger(Cover c, float seconds = 1f)
    {
        if (c == null || !c.R) return;
        c.Freeze = true;
        c.Until = Time.time + seconds;
        Loose.Add(c);
    }

    private static bool Tracked(Renderer r)
    {
        for (int i = 0; i < Loose.Count; i++) if (Loose[i].SrcR == r) return true;
        for (int i = 0; i < Corpses.Count; i++) if (Corpses[i].C != null && Corpses[i].C.SrcR == r) return true;
        return false;
    }

    // ── 死体 (全員の端末で) ─────────────────────────────────────────────
    private sealed class Corpse
    {
        public DeadBody Body;
        public Cover C, Blood;
        public float Born, Depth;
    }

    private static readonly List<Corpse> Corpses = new();
    private static float _nextScan, _nextFind, _eagerUntil;
    private static int _dead = -1, _shipGen = -1;

    // 誰かが死んだ (全員の端末で): 本編の死体はこの時に出るので、すぐ探して水を掛ける (少しの間は毎フレーム探す)
    internal static void OnMurder()
    {
        if (!Water) return;
        _eagerUntil = Time.time + Eager;
        Find(Time.time);
    }

    public static void Tick()
    {
        try { TickCore(); }
        catch (Exception e) { Plugin.Logger.LogError($"[BodyCover] {e}"); Clear(); }
    }

    private static void TickCore()
    {
        bool water = Water;
        if (!water && Corpses.Count == 0 && Loose.Count == 0) return;
        if (GameClock.ShipGen != _shipGen) { Clear(); _shipGen = GameClock.ShipGen; }
        float now = Time.time;
        if (water && now < _eagerUntil) Find(now);
        if (now < _nextScan) return;
        _nextScan = now + ScanEvery;
        if (!water)
        {
            foreach (var k in Corpses) { SetLevel(k.C, 0f); SetLevel(k.Blood, 0f); }
            foreach (var c in Loose) SetLevel(c, 0f);
            return;
        }
        int dead = DeadCount();
        if (dead != _dead || now >= _nextFind) { _dead = dead; _nextFind = now + FindEvery; Find(now); }
        for (int i = Corpses.Count - 1; i >= 0; i--)
        {
            var k = Corpses[i];
            if (!k.Body || k.C == null || !k.C.Src) { Destroy(k.C); Destroy(k.Blood); Corpses.RemoveAt(i); continue; }
            Sync(k.C);
            // 本編の死体は倒れる動きが終わるまで DeadBody 自体を止めている (通報できない) ので、見えるかは物の有無と絵で見る
            float lvl = k.Body.gameObject.activeInHierarchy ? WaterSim.CurrentAt(k.Body.TruePosition, out _, out _) : 0f;
            k.Depth = lvl;
            // 体の絵を隠している間 (決闘で殴り飛ばされた人形が飛んでいる間など) は板も出さない
            SetLevel(k.C, k.C.Src.enabled && k.C.Src.color.a >= 0.5f ? lvl : 0f);
            if (k.Blood != null)
            {
                Sync(k.Blood);
                SetLevel(k.Blood, k.Blood.Src && k.Blood.Src.enabled ? lvl : 0f);
            }
        }
        for (int i = Loose.Count - 1; i >= 0; i--)
        {
            var c = Loose[i];
            if (!c.SrcR || !c.R) { Destroy(c); Loose.RemoveAt(i); continue; }
            if (c.Freeze) continue;
            if (c.Src) Sync(c);
            SetLevel(c, DepthUnder(c));
        }
    }

    // 本編のアニメと描き替えが絵を差した後 (LateUpdate) に、板の絵を毎フレームそろえる:
    // 出たばかりの死体 (本編の死体は絵の無いまま出て、倒れる連番や倒れた姿へ差し替わる。絵が付いた最初のフレームから水を掛ける)・ペット。
    // 死んだ人の体に残した板は、体の絵が替わったら外す
    internal static void LateTick()
    {
        if (Corpses.Count == 0 && Loose.Count == 0) return;
        try { LateCore(); }
        catch (Exception e) { Plugin.Logger.LogError($"[BodyCover] {e}"); Clear(); }
    }

    private static void LateCore()
    {
        float now = Time.time;
        for (int i = 0; i < Corpses.Count; i++)
        {
            var k = Corpses[i];
            if (now - k.Born >= SyncFor) continue;
            if (k.C != null) { Sync(k.C); if (!k.C.Shown && k.C.Src && k.C.Src.enabled) SetLevel(k.C, k.Depth); }
            if (k.Blood != null) { Sync(k.Blood); if (!k.Blood.Shown && k.Blood.Src && k.Blood.Src.enabled) SetLevel(k.Blood, k.Depth); }
        }
        for (int i = Loose.Count - 1; i >= 0; i--)
        {
            var c = Loose[i];
            if (c.Freeze)
            {
                if (!c.Src || c.Src.sprite != c.Last || now >= c.Until) { Destroy(c); Loose.RemoveAt(i); }
                continue;
            }
            if (c.Shown && c.Upright) Sync(c);
        }
    }

    private static int DeadCount()
    {
        int n = 0;
        var all = PlayerControl.AllPlayerControls;
        for (int i = 0; i < all.Count; i++) { var p = all[i]; if (p && p.Data != null && p.Data.IsDead) n++; }
        return n;
    }

    // 新しい死体に板を付ける (死体の描画物は本編の最初の絵 = bodyRenderers の先頭・床の血の跡にも)
    private static void Find(float now)
    {
        foreach (var b in UnityEngine.Object.FindObjectsOfType<DeadBody>())
        {
            if (!b || Has(b)) continue;
            var rs = b.bodyRenderers;
            var main = rs != null && rs.Length > 0 ? rs[0] : null;
            if (!main) continue;
            var c = Make(main);
            if (c == null) continue;
            var k = new Corpse { Body = b, C = c, Born = now };
            if (b.bloodSplatter) k.Blood = Make(b.bloodSplatter, StainThick);
            Corpses.Add(k);
            float lvl = k.Depth = WaterSim.CurrentAt(b.TruePosition, out _, out _);
            SetLevel(c, lvl);
            SetLevel(k.Blood, b.bloodSplatter && b.bloodSplatter.enabled ? lvl : 0f);
        }
    }

    private static bool Has(DeadBody b)
    {
        for (int i = 0; i < Corpses.Count; i++) if (Corpses[i].Body == b) return true;
        return false;
    }

    internal static void Clear()
    {
        foreach (var k in Corpses) { Destroy(k.C); Destroy(k.Blood); }
        Corpses.Clear();
        foreach (var c in Loose) Destroy(c);
        Loose.Clear();
        Plates.Clear();
        _dead = -1;
        _nextFind = 0f;
        _eagerUntil = 0f;
    }
}
