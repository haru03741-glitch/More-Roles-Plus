using System;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 視界の外 (影の中) でも火と水を見せる。
// 影の中に見えている船は影のカメラが置き換えシェーダで描き直した絵で、火と水のシェーダはそこでは描けない。
// そこで火と水の絵の写しを影の板のすぐ手前に置き、影のカメラの描き先 (視界の所はアルファ 0・影の所は 1) を
// 画面の位置で引いて、影の所にだけ出す。写しは元と同じ絵とテクスチャを使い、材質だけ「影の所だけ」の印を付けた複製。
internal static class ShadowView
{
    private const float FrontOfShadow = 0.01f; // 影の板からどれだけ手前に置くか
    private static readonly int ShadowTexId = Shader.PropertyToID("_MrpShadowTex");
    private static int _shipGen = -1;
    private static bool _ok;
    private static float _z;
    private static Material _floor, _flame, _water;
    private static RenderTexture _rt;
    private static Camera _cam;
    private static long _checkAt;
    private static readonly int ShadowOnId = Shader.PropertyToID("_MrpShadowOn");

    // 写しを置く z (影の板のすぐ手前)。影のカメラが無い時は false
    internal static bool Ready(out float z)
    {
        if (GameClock.ShipGen != _shipGen)
        {
            _shipGen = GameClock.ShipGen;
            _ok = false;
            try { _ok = Bind(); }
            catch (Exception e) { Plugin.Logger.LogError($"[ShadowView] {e}"); }
        }
        z = _z;
        return _ok;
    }

    private static bool Bind()
    {
        var quad = GameObject.Find("Main Camera/ShadowQuad");
        var camGo = GameObject.Find("Main Camera/ShadowCamera");
        var cam = camGo ? camGo.GetComponent<Camera>() : null;
        var rt = cam ? cam.targetTexture : null;
        if (!quad || !rt) return false;
        Shader.SetGlobalTexture(ShadowTexId, rt);
        Shader.SetGlobalFloat(ShadowOnId, 1f);
        _rt = rt;
        _cam = cam;
        _z = quad.transform.position.z - FrontOfShadow;
        return true;
    }

    // 火か水の絵がある間、1 秒ごとに呼ぶ: 影のカメラが描き先を差し替えたら結び直す (最初に結べなかった時も)
    internal static void Check(long nowMs)
    {
        if (nowMs < _checkAt) return;
        _checkAt = nowMs + 1000;
        if (!_ok || !_cam || _cam.targetTexture != _rt) _shipGen = -1;
        Ready(out _);
    }

    // ズームを引いて影を外している間は写しを出さない (影のカメラの描き先は残るので、出すと絵が二重になる)
    internal static void SetShadowShown(bool on) => Shader.SetGlobalFloat(ShadowOnId, on ? 1f : 0f);

    internal static Material FireFloor => _floor ??= Shadowed(MrpBundle.FireFloorMaterial, 1f);
    internal static Material Flame => _flame ??= Shadowed(MrpBundle.FlameMaterial, 1f);
    internal static Material Water => _water ??= Shadowed(MrpBundle.WaterMaterial, 0.7f); // 水は少し透かす (影の中の家具の上にも重なるので)

    private static Material Shadowed(Material src, float gain)
    {
        if (!src) return null;
        var m = new Material(src) { name = src.name + "Shadow", hideFlags = HideFlags.DontUnloadUnusedAsset };
        m.SetFloat("_ShadowOnly", 1f);
        m.SetFloat("_ShadowGain", gain);
        return m;
    }

    // 元の絵の写しを影の板の手前に作る (元の子にするので、元を消すと一緒に消える)。dz = 写しどうしの前後
    internal static SpriteRenderer Copy(GameObject parent, Sprite sprite, Material mat, Color color, float dz)
    {
        if (!mat || !Ready(out float z)) return null;
        var go = new GameObject("MrpShadowCopy") { layer = parent.layer };
        var tr = go.transform;
        tr.SetParent(parent.transform, false);
        var p = parent.transform.position;
        tr.position = FxMath.V3(p.x, p.y, z - dz);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.enabled = false;
        sr.sprite = sprite;
        sr.sharedMaterial = mat;
        sr.color = color;
        return sr;
    }
}
