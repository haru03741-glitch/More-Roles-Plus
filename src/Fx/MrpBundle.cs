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
    private const string WadeMatPath = "assets/generated/water_wade.mat";
    private const string FireFloorMatPath = "assets/generated/firefloor.mat";
    private const string FlameMatPath = "assets/generated/flame.mat";
    private const string PlayerGradMatPath = "assets/generated/playergrad.mat";
    private const string RiftWarpMatPath = "assets/generated/riftwarp.mat";
    private const string UiFireMatPath = "assets/generated/uifire.mat";
    private const string UiBarMatPath = "assets/generated/uibar.mat";
    private static readonly string[] KatanaMatPaths = { "assets/generated/katanaplate.mat", "assets/generated/katanaframe.mat", "assets/generated/katanaflash.mat" };

    private static AssetBundle _bundle;
    private static AssetBundleRequest _terrainReq, _waterReq, _wadeReq, _fireFloorReq, _flameReq, _gradReq, _riftReq;
    private static bool _failed, _waterFailed, _fireFailed, _gradFailed, _riftFailed;

    // 壁を壊した音・導火線・水漏れ・外壁の穴・家具がぶつかる音・エモートの音 (BreakNoise と DecompSound が鳴らす)・サムライ・機関銃と大砲の音。マテリアルの後に読む。<名前>_m = 遠い・壁越しのこもった音
    private static readonly string[] ClipNames = MakeClipNames();
    private static readonly AssetBundleRequest[] ClipReqs = new AssetBundleRequest[ClipNames.Length];
    private static readonly bool[] ClipDone = new bool[ClipNames.Length];
    private static readonly Dictionary<string, AudioClip> Clips = new();
    private static int _clipsLeft = ClipNames.Length;

    // ここに無い名前の音はバンドルに入っていても読まれず、鳴らしても無音になる (音を足したらここにも足す)
    private static string[] MakeClipNames()
    {
        var names = new List<string>
        {
            "noise_boom", "noise_fuse", "noise_leak", "noise_splash_1", "noise_splash_2", "noise_splash_3",
            "noise_decomp_breach", "noise_decomp_loop", "noise_decomp_whistle", "noise_decomp_seal",
            "noise_bump_small_1", "noise_bump_small_2", "noise_bump_small_3",
            "noise_bump_heavy_1", "noise_bump_heavy_2", "noise_bump_clash_1", "noise_bump_clash_2",
            "noise_fire_loop", "noise_fire_ignite", "noise_steam", "noise_flare", "noise_arc",
            "noise_portal_open", "noise_portal_pass", "noise_portal_close",
            "noise_drone_loop",
            "noise_emote_clap_1", "noise_emote_clap_2", "noise_emote_clap_3", "noise_emote_land_1", "noise_emote_land_2",
            "noise_emote_push", "noise_emote_swish_1", "noise_emote_swish_2", "noise_emote_rustle",
            "noise_samurai_swing_1", "noise_samurai_swing_2", "noise_samurai_swing_3", "noise_samurai_iai",
            "noise_samurai_rend", "noise_samurai_rend_max", "noise_samurai_delayed_3", "noise_samurai_delayed_6", "noise_samurai_delayed_9",
            "noise_samurai_hit", "noise_samurai_hit_ko", "noise_samurai_clash", "noise_samurai_lock_on", "noise_samurai_lock_break",
            "noise_samurai_lock_loop", "noise_samurai_charge_loop", "noise_samurai_charge_full",
            "noise_samurai_draw", "noise_samurai_slide", "noise_samurai_chin", "noise_samurai_dodge", "noise_samurai_dash", "noise_samurai_spent",
            "noise_gat_fire_loop", "noise_gat_spin_loop", "noise_gat_tail", "noise_gat_raise", "noise_gat_lower",
            "noise_gat_ric_1", "noise_gat_ric_2", "noise_gat_ric_3", "noise_gat_ric_4", "noise_gat_ric_5",
            "noise_gat_hit_1", "noise_gat_hit_2", "noise_gat_hit_3", "noise_gat_case_1", "noise_gat_case_2", "noise_gat_case_3",
            "noise_rl_load", "noise_rl_bang_1", "noise_rl_bang_2", "noise_rl_bang_3", "noise_rl_escape", "noise_rl_shatter", "noise_rl_fire", "noise_rl_fly_loop", "noise_rl_blast",
        };
        foreach (string mat in new[] { "metal", "stone", "wood" })
        {
            names.Add("noise_hit_" + mat);
            names.Add("noise_crumble_" + mat);
            names.Add("noise_rubble_" + mat);
        }
        int n = names.Count;
        for (int i = 0; i < n; i++) names.Add(names[i] + "_m");
        names.Add("noise_end_spectate");
        return names.ToArray();
    }

    public static AudioClip Clip(string name) => Clips.TryGetValue(name, out var c) && c ? c : null;
    public static int ClipCount => Clips.Count;

    public static Material TerrainMaterial { get; private set; }
    public static Material WaterMaterial { get; private set; }
    public static Material WadeMaterial { get; private set; }
    public static Material FireFloorMaterial { get; private set; }
    public static Material FlameMaterial { get; private set; }
    // クルーの 2 色のグラデーション (MRP/PlayerGradient)。人形ごとのマテリアルのシェーダだけを差し替えて使う
    public static Shader PlayerGradientShader { get; private set; }
    // 空間の裂け目のせん断 (MRP/RiftWarp)
    public static Material RiftWarpMaterial { get; private set; }
    // 必殺技の演出: 0 = 生の画面を板に貼る・1 = 事前に描いたコマ・2 = 被害者の白の閃き
    public static readonly Material[] KatanaMaterials = new Material[3];
    public static Material UiFireMaterial { get; private set; }
    public static Material UiBarMaterial { get; private set; }

    public static bool Ready => TerrainMaterial;

    // 毎 tick 呼ぶ。読み込みは最初の 1 回だけ始め、終わるまで完了を見張る
    public static void Tick()
    {
        if (_failed) return;
        if (TerrainMaterial)
        {
            if (!WaterMaterial && !_waterFailed) TickWater();
            else if (!FlameMaterial && !_fireFailed) TickFire();
            else if (!PlayerGradientShader && !_gradFailed) TickGradient();
            else if (!RiftWarpMaterial && !_riftFailed) TickRift();
            else if (_katanaNext < KatanaMatPaths.Length && !_katanaFailed) TickKatana();
            else if (!UiFireMaterial && !_uiFireFailed) TickUiFire();
            else if (!UiBarMaterial && !_uiBarFailed) TickUiBar();
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
            _wadeReq ??= _bundle.LoadAssetAsync(WadeMatPath, Il2CppInterop.Runtime.Il2CppType.Of<Material>());
            if (!_waterReq.isDone || !_wadeReq.isDone) return;
            var mat = _waterReq.asset ? _waterReq.asset.TryCast<Material>() : null;
            if (!mat) { Plugin.Logger.LogError($"bundle asset not found: {WaterMatPath}"); _waterFailed = true; return; }
            mat.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            // 体の水面は無くても水たまりは出す
            var wade = _wadeReq.asset ? _wadeReq.asset.TryCast<Material>() : null;
            if (wade) { wade.hideFlags |= HideFlags.DontUnloadUnusedAsset; WadeMaterial = wade; }
            else Plugin.Logger.LogError($"bundle asset not found: {WadeMatPath}");
            WaterMaterial = mat;
            Plugin.Logger.LogInfo($"bundle water: shader={mat.shader.name} supported={mat.shader.isSupported}");
        }
        catch (Exception e) { Plugin.Logger.LogError($"bundle water: {e}"); _waterFailed = true; }
    }

    private static void TickFire()
    {
        try
        {
            _fireFloorReq ??= _bundle.LoadAssetAsync(FireFloorMatPath, Il2CppInterop.Runtime.Il2CppType.Of<Material>());
            _flameReq ??= _bundle.LoadAssetAsync(FlameMatPath, Il2CppInterop.Runtime.Il2CppType.Of<Material>());
            if (!_fireFloorReq.isDone || !_flameReq.isDone) return;
            var floor = _fireFloorReq.asset ? _fireFloorReq.asset.TryCast<Material>() : null;
            var flame = _flameReq.asset ? _flameReq.asset.TryCast<Material>() : null;
            if (!floor || !flame) { Plugin.Logger.LogError("bundle asset not found: fire materials"); _fireFailed = true; return; }
            floor.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            flame.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            FireFloorMaterial = floor;
            FlameMaterial = flame;
            Plugin.Logger.LogInfo($"bundle fire: shader={flame.shader.name} supported={flame.shader.isSupported}");
        }
        catch (Exception e) { Plugin.Logger.LogError($"bundle fire: {e}"); _fireFailed = true; }
    }

    private static void TickGradient()
    {
        try
        {
            _gradReq ??= _bundle.LoadAssetAsync(PlayerGradMatPath, Il2CppInterop.Runtime.Il2CppType.Of<Material>());
            if (!_gradReq.isDone) return;
            var mat = _gradReq.asset ? _gradReq.asset.TryCast<Material>() : null;
            if (!mat || !mat.shader) { Plugin.Logger.LogError($"bundle asset not found: {PlayerGradMatPath}"); _gradFailed = true; return; }
            mat.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            var sh = mat.shader;
            sh.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            PlayerGradientShader = sh;
            Plugin.Logger.LogInfo($"bundle gradient: shader={sh.name} supported={sh.isSupported}");
            Menu.CustomColors.MarkDirty(); // 先に塗った人をグラデーションで塗り直す
        }
        catch (Exception e) { Plugin.Logger.LogError($"bundle gradient: {e}"); _gradFailed = true; }
    }

    private static void TickRift()
    {
        try
        {
            _riftReq ??= _bundle.LoadAssetAsync(RiftWarpMatPath, Il2CppInterop.Runtime.Il2CppType.Of<Material>());
            if (!_riftReq.isDone) return;
            var mat = _riftReq.asset ? _riftReq.asset.TryCast<Material>() : null;
            if (!mat) { Plugin.Logger.LogError($"bundle asset not found: {RiftWarpMatPath}"); _riftFailed = true; return; }
            mat.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            RiftWarpMaterial = mat;
            Plugin.Logger.LogInfo($"bundle rift: shader={mat.shader.name} supported={mat.shader.isSupported}");
        }
        catch (Exception e) { Plugin.Logger.LogError($"bundle rift: {e}"); _riftFailed = true; }
    }

    private static AssetBundleRequest _uiFireReq;
    private static bool _uiFireFailed;

    private static void TickUiFire()
    {
        try
        {
            _uiFireReq ??= _bundle.LoadAssetAsync(UiFireMatPath, Il2CppInterop.Runtime.Il2CppType.Of<Material>());
            if (!_uiFireReq.isDone) return;
            var mat = _uiFireReq.asset ? _uiFireReq.asset.TryCast<Material>() : null;
            if (!mat) { Plugin.Logger.LogError($"bundle asset not found: {UiFireMatPath}"); _uiFireFailed = true; return; }
            mat.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            UiFireMaterial = mat;
            Plugin.Logger.LogInfo($"bundle uifire: shader={mat.shader.name} supported={mat.shader.isSupported}");
        }
        catch (Exception e) { Plugin.Logger.LogError($"bundle uifire: {e}"); _uiFireFailed = true; }
    }

    private static AssetBundleRequest _uiBarReq;
    private static bool _uiBarFailed;

    private static void TickUiBar()
    {
        try
        {
            _uiBarReq ??= _bundle.LoadAssetAsync(UiBarMatPath, Il2CppInterop.Runtime.Il2CppType.Of<Material>());
            if (!_uiBarReq.isDone) return;
            var mat = _uiBarReq.asset ? _uiBarReq.asset.TryCast<Material>() : null;
            if (!mat) { Plugin.Logger.LogError($"bundle asset not found: {UiBarMatPath}"); _uiBarFailed = true; return; }
            mat.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            UiBarMaterial = mat;
            Plugin.Logger.LogInfo($"bundle uibar: shader={mat.shader.name} supported={mat.shader.isSupported}");
        }
        catch (Exception e) { Plugin.Logger.LogError($"bundle uibar: {e}"); _uiBarFailed = true; }
    }

    private static AssetBundleRequest _katanaReq;
    private static int _katanaNext;
    private static bool _katanaFailed;

    private static void TickKatana()
    {
        try
        {
            _katanaReq ??= _bundle.LoadAssetAsync(KatanaMatPaths[_katanaNext], Il2CppInterop.Runtime.Il2CppType.Of<Material>());
            if (!_katanaReq.isDone) return;
            var mat = _katanaReq.asset ? _katanaReq.asset.TryCast<Material>() : null;
            _katanaReq = null;
            if (!mat) { Plugin.Logger.LogError($"bundle asset not found: {KatanaMatPaths[_katanaNext]}"); _katanaFailed = true; return; }
            mat.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            KatanaMaterials[_katanaNext++] = mat;
            Plugin.Logger.LogInfo($"bundle katana: shader={mat.shader.name} supported={mat.shader.isSupported}");
        }
        catch (Exception e) { Plugin.Logger.LogError($"bundle katana: {e}"); _katanaFailed = true; }
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
