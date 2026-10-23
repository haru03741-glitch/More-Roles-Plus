using System;
using HarmonyLib;
using InnerNet;
using PowerTools;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 抜けた人の残像。抜けた人の体は本編が消すので、抜ける直前に着せ替えの見た目だけを複製して取っておき、
// 逆再生が抜けた時刻より前をたどる間だけ、控えた姿のとおりに半透明で歩かせる (見た目だけ・戻し終われば消える)。
// 複製は動かない親の下で作り、絵のアニメ (SpriteAnim) とスキンの層 (SkinLayer・自分では動かない) 以外の部品を
// 目を覚ます前に外す (本編の部品が抜けた人の物を触りに行かないように)。名前などの文字は描かない
internal static partial class RewindPlayback
{
    private const float GhostAlpha = 0.45f;
    private const long GhostKeepMs = Slots * StepMs; // 控えの輪より古くなった残像は使わない

    private sealed partial class Track
    {
        public GameObject Ghost;
        public Transform GhostT;
        public SpriteAnim GhostAnim, GhostSkin;
        public SkinLayer GhostSkinLayer;
        public PlayerAnimationGroup Group;
        public float GScaleX, GScaleY, GRootY, GRootZ;
        public bool GhostFlip0, GhostOn, GhostShown;
        public long LeftMs;
        public IntPtr GhostClip;
        public Kind GhostKind;
    }

    private static GameObject _ghostHolder;
    internal static int Ghosts; // 確かめ用: 作った残像の数

    // 抜ける直前 (体がまだある時) に見た目を複製しておく
    internal static void Leave(PlayerControl pc)
    {
        if (!pc) return;
        var t = Tracks[pc.PlayerId];
        if (t == null || t.PcPtr != pc.Pointer) return;
        DropGhost(t);
        if (!Resolve(t) || !t.Cos || t.Anims == null || t.Anims.group == null) return;
        if (!_ghostHolder)
        {
            _ghostHolder = new GameObject("MRP_RewindGhosts");
            _ghostHolder.SetActive(false);
        }
        // 体の絵 (BodyForms) と着せ替え (Cosmetics) は人の根の別々の子なので、それぞれを複製して新しい根の下に並べる
        var root = pc.transform;
        var go = new GameObject("RewindGhost");
        var gt = go.transform;
        gt.SetParent(_ghostHolder.transform, false);
        var bodyPart = ChildOfRoot(root, t.Anim.transform);
        var cosPart = ChildOfRoot(root, t.Cos.transform);
        if (!bodyPart || !cosPart) { UnityEngine.Object.Destroy(go); return; }
        UnityEngine.Object.Instantiate(bodyPart.gameObject, gt).name = bodyPart.name;
        if (cosPart != bodyPart) UnityEngine.Object.Instantiate(cosPart.gameObject, gt).name = cosPart.name;
        // 目を覚ます前に、絵のアニメとスキンの層以外の部品を外す (起きていない部品は OnDestroy も呼ばれない)
        foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (!mb || mb.TryCast<SpriteAnim>() != null || mb.TryCast<SkinLayer>() != null) continue;
            UnityEngine.Object.DestroyImmediate(mb);
        }
        foreach (var col in go.GetComponentsInChildren<Collider2D>(true))
            if (col) UnityEngine.Object.DestroyImmediate(col);
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            if (!r) continue;
            var sr = r.TryCast<SpriteRenderer>();
            if (sr == null) { r.enabled = false; continue; }
            var c = sr.color;
            c.a *= GhostAlpha;
            sr.color = c;
        }
        // ペットは体から離れて付いてくるので残像には入れない
        var pet = t.Cos.CurrentPet;
        if (pet) Find(gt, root, pet.transform)?.gameObject.SetActive(false);
        t.Ghost = go;
        t.GhostT = gt;
        t.GhostAnim = Find(gt, root, t.Anim.transform)?.GetComponent<SpriteAnim>();
        var skin = t.Cos.skin;
        t.GhostSkinLayer = skin ? Find(gt, root, skin.transform)?.GetComponent<SkinLayer>() : null;
        t.GhostSkin = t.GhostSkinLayer ? t.GhostSkinLayer.animator : null;
        t.Group = t.Anims.group;
        var ls = root.lossyScale;
        var rp = root.position;
        t.GScaleX = MathF.Abs(ls.x);
        t.GScaleY = ls.y;
        t.GRootY = rp.y;
        t.GRootZ = rp.z;
        t.GhostFlip0 = t.Cos.FlipX;
        t.LeftMs = Environment.TickCount64;
        Ghosts++;
        if (!t.GhostAnim) DropGhost(t);
    }

    // part の祖先のうち root の直下の物
    private static Transform ChildOfRoot(Transform root, Transform part)
    {
        for (var p = part; p; p = p.parent)
            if (p.parent == root) return p;
        return null;
    }

    // 複製の中で、元の部品 part に当たる物 (元の root からの道のりをたどる)
    private static Transform Find(Transform copy, Transform root, Transform part)
    {
        if (!part) return null;
        if (part == root) return copy;
        var path = new System.Text.StringBuilder();
        for (var p = part; p && p != root; p = p.parent)
        {
            if (!p.parent) return null; // root の下ではない
            path.Insert(0, p.name).Insert(0, '/');
        }
        return path.Length == 0 ? copy : copy.Find(path.ToString(1, path.Length - 1));
    }

    private static void DropGhost(Track t)
    {
        if (t.Ghost) UnityEngine.Object.Destroy(t.Ghost);
        t.Ghost = null;
        t.GhostT = null;
        t.GhostAnim = t.GhostSkin = null;
        t.GhostSkinLayer = null;
        t.Group = null;
        t.GhostOn = t.GhostShown = false;
        t.LeftMs = 0;
    }

    private static void ClearGhosts()
    {
        foreach (var t in Tracks)
            if (t != null) DropGhost(t);
    }

    // 控えの輪より古い残像は捨てる (控えの合間に 1 回)
    private static void ExpireGhosts(long now)
    {
        foreach (var t in Tracks)
            if (t != null && t.LeftMs != 0 && now - t.LeftMs > GhostKeepMs) DropGhost(t);
    }

    private static void BeginGhosts()
    {
        foreach (var t in Tracks)
        {
            if (t == null || !t.Ghost || t.Pc) continue;
            t.GhostOn = true;
            t.GhostShown = false;
            t.GhostClip = IntPtr.Zero;
            t.GhostKind = Kind.None;
        }
    }

    private static void ShowGhost(Track t, bool on)
    {
        if (t.GhostShown == on) return;
        t.GhostShown = on;
        if (on)
        {
            t.GhostT.SetParent(null, false);
            t.Ghost.SetActive(true);
            if (t.GhostAnim) t.GhostAnim.Paused = true;
            if (t.GhostSkin) t.GhostSkin.Paused = true;
        }
        else t.GhostT.SetParent(_ghostHolder ? _ghostHolder.transform : null, false);
    }

    private static void HideGhosts()
    {
        foreach (var t in Tracks)
        {
            if (t == null || !t.GhostOn) continue;
            t.GhostOn = false;
            try { if (t.Ghost) ShowGhost(t, false); }
            catch (Exception e) { Plugin.Logger.LogWarning($"RewindPlayback ghost hide {t.Id}: {e.Message}"); }
        }
    }

    // 抜けた人の控えのある所だけ残像を置く (抜けた後・ベントなどの途中・幽霊だった所 (こちらが生きている時) は出さない)
    private static void DriveGhost(Track t, int a, int b, float u)
    {
        if (!t.Ghost || !t.GhostAnim) { t.GhostOn = false; return; }
        byte fa = t.F[a], fb = t.F[b];
        bool seen = (fa & FSeen) != 0 && (fb & FSeen) != 0 && ((fa | fb) & FHold) == 0;
        int n = u < 0.5f ? a : b;
        if (seen && (t.F[n] & FDead) != 0)
        {
            var lp = PlayerControl.LocalPlayer;
            seen = lp && lp.Data != null && lp.Data.IsDead;
        }
        ShowGhost(t, seen);
        if (!seen) return;
        float x = t.X[a] + (t.X[b] - t.X[a]) * u, y = t.Y[a] + (t.Y[b] - t.Y[a]) * u;
        bool flip = (t.F[n] & FFlip) != 0;
        float sx = flip == t.GhostFlip0 ? t.GScaleX : -t.GScaleX;
        t.GhostT.position = Fx.FxMath.V3(x, y, t.GRootZ + (y - t.GRootY) / 1000f);
        t.GhostT.localScale = Fx.FxMath.V3(sx, t.GScaleY, 1f);
        IntPtr clip = t.Clip[n];
        if (clip != t.GhostClip) SwitchGhost(t, clip);
        if (t.GhostKind == Kind.None) return;
        float time = AnimTime(t.Clip, t.T, a, b, u);
        t.GhostAnim.SetNormalizedTime(time);
        if (t.GhostSkin) t.GhostSkin.SetNormalizedTime(time);
    }

    // 残像は走りと立ちだけ流す (向きは左右の反転で付けるので、スキンは抜けた時の向きのまま)
    private static void SwitchGhost(Track t, IntPtr clip)
    {
        t.GhostClip = clip;
        t.GhostKind = Kind.None;
        var g = t.Group;
        if (clip == IntPtr.Zero || g == null) return;
        AnimationClip hit = null;
        Kind kind = Kind.None;
        Match(g.RunAnim, Kind.Run, clip, ref hit, ref kind);
        Match(g.IdleAnim, Kind.Idle, clip, ref hit, ref kind);
        if (hit == null) return;
        t.GhostAnim.Play(hit, 1f);
        t.GhostAnim.Paused = true;
        t.GhostKind = kind;
        var skin = t.GhostSkinLayer;
        if (!skin) return;
        if (kind == Kind.Run) skin.SetRun(t.GhostFlip0);
        else skin.SetIdle(t.GhostFlip0);
        if (t.GhostSkin) t.GhostSkin.Paused = true;
    }
}

// 抜ける人の見た目を、本編が体を消す前に控える
[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerLeft))]
internal static class RewindPlaybackLeave
{
    public static void Prefix([HarmonyArgument(0)] ClientData data)
    {
        if (!Rewind.Recording || data == null) return;
        try { RewindPlayback.Leave(data.Character); }
        catch (Exception e) { Plugin.Logger.LogWarning($"RewindPlayback leave: {e.Message}"); }
    }
}
