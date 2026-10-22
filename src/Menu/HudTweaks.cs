// Precise cooldowns based on https://github.com/AU-Avengers/TOU-Mira (KillButtonCooldownPatch, GPL-3.0)
// Button scale based on https://github.com/All-Of-Us-Mods/MiraAPI (HudManagerPatches.ResizeUI, LGPL-2.1)
// Screenshot key based on https://github.com/Dolly1016/Nebula-Public (CommandHandler, GPL-3.0)
using System;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// 自分の端末だけの HUD の調整: 残り 10 秒からの小数のクールダウン・右下のボタンの大きさ・スクリーンショットのキー
internal static class HudTweaks
{
    // ---- 小数のクールダウン ----
    private const int Tenths = 100; // 10.0 秒まで
    private static PinnedText.Str[] _tenths;

    internal static void OnSetCoolDown(ActionButton b, float timer)
    {
        // 準備できたボタンも毎フレーム timer = 0 で呼ばれるので、本編の値を読む前に数だけで帰す
        if (timer <= 0f || timer > 10f || !ClientOptions.PreciseCooldown.Value || !b.isCoolingDown) return;
        var text = b.cooldownTimerText;
        if (!text) return;
        if (_tenths == null)
        {
            _tenths = new PinnedText.Str[Tenths + 1];
            for (int i = 0; i <= Tenths; i++)
                _tenths[i] = new PinnedText.Str((i / 10f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        }
        int k = (int)Math.Ceiling(timer * 10f);
        PinnedText.Set(text, _tenths[k < 1 ? 1 : k > Tenths ? Tenths : k]);
    }

    // ---- ボタンの大きさ ----
    internal static readonly int[] Scales = { 80, 90, 100, 115, 130 };
    private const float HalfButton = 0.4f; // 本編のボタンの半分の幅 (当たり 1.15 × 0.7 倍)
    private static IntPtr _hud;
    private static Vector3 _edge; // 右下の並びの本編の DistanceFromEdge (HUD ごとに 1 回控える)
    private static Vector2 _cell; // 本編の升の大きさ。本編の並べ直しは升を親の倍率で割って置くので、倍率と同じだけ升も掛ける

    internal static string ScaleLabel() => $"{ClientOptions.ButtonScale.Value}%";

    internal static void NextScale()
    {
        int cur = Array.IndexOf(Scales, ClientOptions.ButtonScale.Value);
        ClientOptions.ButtonScale.Value = Scales[(cur + 1) % Scales.Length];
        ApplyScale(true);
    }

    // 右下の並び (本編の GridArrange) を角のボタンの中心を軸に拡縮し、角のボタンの外側の縁が画面の端から同じ距離に残るよう寄せる
    // rearrange = 今の並びを置き直す (HudManager.Start の時点では本編の並びの一覧がまだ無く、本編の Start が升を見て並べる)
    internal static void ApplyScale(bool rearrange)
    {
        var hud = Vanilla.Hud;
        if (!hud || !hud.UseButton) return;
        var grid = hud.UseButton.transform.parent;
        var aspect = grid ? grid.GetComponent<AspectPosition>() : null;
        var arrange = grid ? grid.GetComponent<GridArrange>() : null;
        if (!aspect || !arrange) return;
        if (_hud != hud.Pointer)
        {
            _hud = hud.Pointer;
            _edge = aspect.DistanceFromEdge;
            _cell = arrange.CellSize;
        }
        int pct = ClientOptions.ButtonScale.Value;
        if (Array.IndexOf(Scales, pct) < 0) pct = 100;
        float s = pct / 100f;
        float shift = HalfButton * (1f - s);
        grid.localScale = new Vector3(s, s, 1f);
        aspect.DistanceFromEdge = new Vector3(_edge.x - shift, _edge.y - shift, _edge.z);
        aspect.AdjustPosition();
        arrange.CellSize = new Vector2(_cell.x * s, _cell.y * s);
        if (rearrange && grid.gameObject.activeInHierarchy && arrange.cells != null) arrange.ArrangeChilds();
    }

    internal static string Describe() =>
        $"OK hudtweaks scale={ClientOptions.ButtonScale.Value} precise={(ClientOptions.PreciseCooldown.Value ? "on" : "off")} edge0={_edge} cell0={_cell}"
        + (Vanilla.Hud is { } h && h.UseButton ? $" grid={h.UseButton.transform.parent.localScale.x:0.00} edge={h.UseButton.transform.parent.GetComponent<AspectPosition>().DistanceFromEdge} pos={h.UseButton.transform.parent.localPosition} cell={h.UseButton.transform.parent.GetComponent<GridArrange>().CellSize} report={h.ReportButton.transform.localPosition}" : "");

    // ---- スクリーンショット ----
    internal static string Screenshot()
    {
        // Android の CaptureScreenshot は絶対パスを受け付けない (キーも無い)
        if (OperatingSystem.IsAndroid()) return "ERR PC only";
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Among Us MRP");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
        ScreenCapture.CaptureScreenshot(path);
        Plugin.Logger.LogInfo($"screenshot: {path}");
        var hud = Vanilla.Hud;
        if (hud && hud.Chat && hud.Chat.gameObject.activeInHierarchy) ChatNotes.Post(new Text("HudTweaks.Screenshot").With("path", path).ToString());
        return path;
    }
}

[HarmonyPatch(typeof(ActionButton), nameof(ActionButton.SetCoolDown))]
internal static class PreciseCooldownPatch
{
    public static void Postfix(ActionButton __instance, float timer)
    {
        try { HudTweaks.OnSetCoolDown(__instance, timer); }
        catch (Exception e) { Plugin.Logger.LogError($"precise cooldown: {e}"); }
    }
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
internal static class HudScalePatch
{
    public static void Postfix()
    {
        try { HudTweaks.ApplyScale(false); }
        catch (Exception e) { Plugin.Logger.LogError($"hud scale: {e}"); }
    }
}
