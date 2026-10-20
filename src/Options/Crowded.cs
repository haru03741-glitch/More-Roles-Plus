// Based on https://github.com/CrowdedMods/CrowdedMod src/CrowdedMod/Patches (MIT, Copyright (c) 2020-2022 andry08 & CrowdedMods; licenses/CrowdedMod-MIT.txt),
// by way of https://github.com/Gurge44/EndlessHostRoles Patches/Crowded.cs (GPL-3.0)
using System;
using AmongUs.GameOptions;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Net;

namespace MoreRolesPlus.Options;

// 15 人を超える部屋 (公式サーバー以外) の対応。
[HarmonyPatch]
internal static class Crowded
{
    public const int VanillaMaxPlayers = 15;
    // 公式サーバー以外で選べる定員。人の番号 (PlayerId) は 0 から振られ、本編は投票の印に 252〜255 を使うので、番号が 251 までに収まる数
    public const int MaxPlayers = 252;

    // 今の接続先 (部屋を立てる前は選んでいる地域) で選べる定員
    public static int MaxFor() => ServerKind.IsOfficial() ? VanillaMaxPlayers : MaxPlayers;

    // 公式サーバーへ持ち込まないように定員を 15 人・インポスターを 3 人へ戻す
    public static void ClampToVanilla(IGameOptions opts)
    {
        if (opts == null) return;
        if (opts.MaxPlayers > VanillaMaxPlayers) opts.SetInt(Int32OptionNames.MaxPlayers, VanillaMaxPlayers);
        ImpostorCap.ClampToVanilla(opts);
    }

    // 本編は人数を添字にして引く表 (おすすめのキルの間隔・おすすめのインポスター数・人数ごとのインポスターの上限) を 15 人分しか持たない。
    // 16 人以上の部屋を立てる・設定を読むと表の外を引いて例外になるので、最後の値を繰り返して番号の取りうる分まで伸ばす (15 人以下は本編と同じ値)
    private const int TableSize = 256;

    public static void PadTables()
    {
        if (NormalGameOptionsV12.RecommendedKillCooldown.Length >= TableSize) return;
        NormalGameOptionsV12.RecommendedKillCooldown = Pad(NormalGameOptionsV12.RecommendedKillCooldown);
        NormalGameOptionsV12.RecommendedImpostors = Pad(NormalGameOptionsV12.RecommendedImpostors);
        NormalGameOptionsV12.MaxImpostors = Pad(NormalGameOptionsV12.MaxImpostors);
        HideNSeekGameOptionsV12.MaxImpostors = Pad(HideNSeekGameOptionsV12.MaxImpostors);
    }

    private static int[] Pad(int[] t)
    {
        if (t == null || t.Length == 0 || t.Length >= TableSize) return t;
        var r = new int[TableSize];
        Array.Copy(t, r, t.Length);
        for (int i = t.Length; i < TableSize; i++) r[i] = t[^1];
        return r;
    }

    // ── 部屋の作成画面 ──
    // 「最大人数」の ± の幅を接続先に合わせる。地域を切り替えた時も合わせ直す

    [HarmonyPatch(typeof(CreateGameOptions), nameof(CreateGameOptions.Show)), HarmonyPostfix]
    public static void CreateShow(CreateGameOptions __instance) => FitCapacity(__instance);

    [HarmonyPatch(typeof(CreateGameOptions), nameof(CreateGameOptions.SetCurrentServer)), HarmonyPostfix]
    public static void CreateServerChanged(CreateGameOptions __instance) => FitCapacity(__instance);

    // 地域が変わった時 (作成画面の外で変えた時も)。作成画面が開いていれば幅を合わせ直す
    [HarmonyPatch(typeof(ServerManager), nameof(ServerManager.SetRegion)), HarmonyPostfix]
    public static void RegionChanged()
    {
        var menu = UnityEngine.Object.FindObjectOfType<CreateGameOptions>();
        if (menu && menu.isActiveAndEnabled) FitCapacity(menu);
        else if (MaxFor() == VanillaMaxPlayers) ClampToVanilla(GameOptionsManager.Instance?.GameHostOptions);
    }

    private static void FitCapacity(CreateGameOptions menu)
    {
        var opt = menu ? menu.capacityOption : null;
        if (!opt) return;
        ImpostorCap.Refresh();
        int max = MaxFor();
        opt.ValidRange = new FloatRange(opt.ValidRange.min, max);
        if (menu.capacitySetting != null) menu.capacitySetting.ValidRange = new IntRange(menu.capacitySetting.ValidRange.min, max);
        if (opt.Value > max)
        {
            opt.Value = max;
            opt.UpdateValue();
        }
        opt.AdjustButtonsActiveState();
        if (max == VanillaMaxPlayers) ClampToVanilla(GameOptionsManager.Instance?.GameHostOptions);
    }

    // 「最大人数」の刻み。15 人までは 1 ずつ、そこから 50 人までは 5 ずつ、その先は 10 ずつ (端数はそろえる)。
    // 本編は足す数 (Increment) を足して幅に収めるので、押す前に足す数だけ変える
    [HarmonyPatch(typeof(NumberOption), nameof(NumberOption.Increase)), HarmonyPrefix]
    public static void CapacityUp(NumberOption __instance)
    {
        if (__instance.intOptionName != Int32OptionNames.MaxPlayers) return;
        int v = (int)__instance.Value;
        int step = v < VanillaMaxPlayers ? 1 : v < 50 ? 5 : 10;
        __instance.Increment = step == 1 ? 1 : (v / step + 1) * step - v;
    }

    [HarmonyPatch(typeof(NumberOption), nameof(NumberOption.Decrease)), HarmonyPrefix]
    public static void CapacityDown(NumberOption __instance)
    {
        if (__instance.intOptionName != Int32OptionNames.MaxPlayers) return;
        int v = (int)__instance.Value;
        int step = v <= VanillaMaxPlayers ? 1 : v <= 50 ? 5 : 10;
        __instance.Increment = step == 1 ? 1 : v - Math.Max(VanillaMaxPlayers, (v - 1) / step * step);
    }

    // 定員の数だけ並べるクルーの絵は 15 人分しかない
    [HarmonyPatch(typeof(CreateGameOptions), nameof(CreateGameOptions.SetCrewmateGraphic)), HarmonyPrefix]
    public static void CrewGraphic(ref float capacity) => capacity = Math.Min(capacity, VanillaMaxPlayers);

    // 部屋を立てる直前。公式サーバーなら本編の上限へ戻す
    [HarmonyPatch(typeof(CreateGameOptions), nameof(CreateGameOptions.Confirm)), HarmonyPrefix]
    public static void CreateConfirm()
    {
        if (!ServerKind.IsOfficial()) return;
        var gom = GameOptionsManager.Instance;
        if (gom == null) return;
        ClampToVanilla(gom.GameHostOptions);
        ClampToVanilla(gom.CurrentGameOptions);
    }

    // 本編は保存した設定を読む時などに、定員が想定 (15 人) を超えた設定を壊れているとみなして既定へ戻す。
    // 想定の人数を広げて、ほかの検査は本編のまま通す
    [HarmonyPatch(typeof(NormalGameOptionsV12), nameof(NormalGameOptionsV12.AreInvalid)), HarmonyPrefix]
    public static void OptionsInvalid(NormalGameOptionsV12 __instance, ref int maxExpectedPlayers)
    {
        if (__instance.MaxPlayers > VanillaMaxPlayers) PadTables();
        if (maxExpectedPlayers < __instance.MaxPlayers && __instance.MaxPlayers <= MaxPlayers) maxExpectedPlayers = __instance.MaxPlayers;
    }

    // Mira HQ のドアのログは人の番号 (PlayerId) をそのまま添字にして書くが、本編の配列は 15 人分しかない。
    // 15 番以上の人 (16 人目・ダミー) が扉を通ると配列の外へ書き、IL2CPP のヒープを壊して後で落ちる。番号の取りうる 0〜255 の分を持たせる
    [HarmonyPatch(typeof(SecurityLogger), nameof(SecurityLogger.Awake)), HarmonyPostfix]
    public static void SecurityLoggerAwake(SecurityLogger __instance)
    {
        __instance.Timers = new Il2CppStructArray<float>((long)256); // int を渡すと Android で壊れた配列になる
    }
}
