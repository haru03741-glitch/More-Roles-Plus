// Based on https://github.com/waffle-ful/Aeterna-End-K-not Modules/FxShaderBundle.cs (GPL-3.0)
using System;
using System.Collections.Generic;
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
    private const string WaterMatPath = "assets/generated/water.mat";

    private static AssetBundle _bundle;
    private static AssetBundleRequest _terrainReq, _waterReq;
    private static bool _failed, _waterFailed;

    // 壁を壊した音・導火線・水漏れ・外壁の穴・家具がぶつかる音 (BreakNoise と DecompSound が鳴らす)。マテリアルの後に読む。<名前>_m = 遠い・壁越しのこもった音
    private static readonly string[] ClipNames = MakeClipNames();
    private static readonly AssetBundleRequest[] ClipReqs = new AssetBundleRequest[ClipNames.Length];
    private static readonly bool[] ClipDone = new bool[ClipNames.Length];
    private static readonly Dictionary<string, AudioClip> Clips = new();
    private static int _clipsLeft = ClipNames.Length;

    private static string[] MakeClipNames()
    {
        var names = new List<string>
        {
            "noise_boom", "noise_fuse", "noise_leak", "noise_splash_1", "noise_splash_2", "noise_splash_3",
            "noise_decomp_breach", "noise_decomp_loop", "noise_decomp_whistle", "noise_decomp_seal",
            "noise_bump_small_1", "noise_bump_small_2", "noise_bump_small_3",
            "noise_bump_heavy_1", "noise_bump_heavy_2", "noise_bump_clash_1", "noise_bump_clash_2",
        };
        foreach (string mat in new[] { "metal", "stone", "wood" })
        {
            names.Add("noise_hit_" + mat);
            names.Add("noise_crumble_" + mat);
            names.Add("noise_rubble_" + mat);
        }
        int n = names.Count;
        for (int i = 0; i < n; i++) names.Add(names[i] + "_m");
        return names.ToArray();
    }

    public static AudioClip Clip(string name) => Clips.TryGetValue(name, out var c) && c ? c : null;
    public static int ClipCount => Clips.Count;

    public static Material TerrainMaterial { get; private set; }
    public static Material WaterMaterial { get; private set; }

    public static bool Ready => TerrainMaterial;

    // 毎 tick 呼ぶ。読み込みは最初の 1 回だけ始め、終わるまで完了を見張る
    public static void Tick()
    {
        if (_failed) return;
        if (TerrainMaterial)
        {
            if (!WaterMaterial && !_waterFailed) TickWater();
            if (_clipsLeft > 0) TickClips();
            return;
        }

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

    private static void TickWater()
    {
        try
        {
            _waterReq ??= _bundle.LoadAssetAsync(WaterMatPath, Il2CppInterop.Runtime.Il2CppType.Of<Material>());
            if (!_waterReq.isDone) return;
            var mat = _waterReq.asset ? _waterReq.asset.TryCast<Material>() : null;
            if (!mat) { Plugin.Logger.LogError($"bundle asset not found: {WaterMatPath}"); _waterFailed = true; return; }
            mat.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            WaterMaterial = mat;
            Plugin.Logger.LogInfo($"bundle water: shader={mat.shader.name} supported={mat.shader.isSupported}");
        }
        catch (Exception e) { Plugin.Logger.LogError($"bundle water: {e}"); _waterFailed = true; }
    }

    private static void TickClips()
    {
        try
        {
            for (int i = 0; i < ClipNames.Length; i++)
            {
                if (ClipDone[i]) continue;
                ClipReqs[i] ??= _bundle.LoadAssetAsync($"assets/generated/{ClipNames[i]}.wav", Il2CppInterop.Runtime.Il2CppType.Of<AudioClip>());
                if (!ClipReqs[i].isDone) continue;
                var clip = ClipReqs[i].asset ? ClipReqs[i].asset.TryCast<AudioClip>() : null;
                ClipReqs[i] = null;
                ClipDone[i] = true;
                _clipsLeft--;
                if (!clip) { Plugin.Logger.LogError($"bundle clip not found: {ClipNames[i]}"); continue; }
                clip.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                Clips[ClipNames[i]] = clip;
            }
        }
        catch (Exception e) { _clipsLeft = 0; Plugin.Logger.LogError($"bundle clip load failed: {e}"); }
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
