// Based on https://github.com/waffle-ful/Aeterna-End-K-not Modules/FxShaderBundle.cs (GPL-3.0)
using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace MoreRolesPlus.Fx;

// DLL に埋め込んだ AssetBundle (mrp_fx) を読み、シェーダ入りのマテリアルを渡す。
// Android の libunity には同期の AssetBundle.LoadAsset と LoadFromMemoryAsync が無いので、
// LoadFromMemory + LoadAssetAsync の組み合わせだけを使う (全環境共通)。
internal static class MrpBundle
{
    private const string ResourceName = "MoreRolesPlus.Resources.Bundles.mrp_fx.bundle";
    private const string TerrainMatPath = "assets/generated/terrain.mat";

    private static AssetBundle _bundle;
    private static AssetBundleRequest _terrainReq;
    private static bool _failed;

    public static Material TerrainMaterial { get; private set; }

    public static bool Ready => TerrainMaterial;

    // 毎 tick 呼ぶ。読み込みは最初の 1 回だけ始め、終わるまで完了を見張る
    public static void Tick()
    {
        if (_failed || TerrainMaterial) return;

        try
        {
            if (!_bundle)
            {
                byte[] bytes = ReadResource();
                if (bytes == null) { Fail($"resource not found: {ResourceName}"); return; }
                _bundle = AssetBundle.LoadFromMemory(bytes);
                if (!_bundle) { Fail("LoadFromMemory returned null"); return; }
            }

            _terrainReq ??= _bundle.LoadAssetAsync(TerrainMatPath, Il2CppInterop.Runtime.Il2CppType.Of<Material>());
            if (!_terrainReq.isDone) return;

            var mat = _terrainReq.asset ? _terrainReq.asset.TryCast<Material>() : null;
            if (!mat) { Fail($"asset not found: {TerrainMatPath}"); return; }

            mat.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            TerrainMaterial = mat;
            Plugin.Logger.LogInfo($"bundle ready: shader={mat.shader.name} supported={mat.shader.isSupported}");
        }
        catch (Exception e) { Fail(e.ToString()); }
    }

    private static void Fail(string why)
    {
        _failed = true;
        Plugin.Logger.LogError($"bundle load failed: {why}");
    }

    private static byte[] ReadResource()
    {
        using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (s == null) return null;
        var buf = new byte[s.Length];
        int read = 0;
        while (read < buf.Length)
        {
            int n = s.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return buf;
    }
}
