using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Menu;

// 撮影モード: HUD (タスク一覧・ボタン・チャット・暗転の板) を隠す / 戻す。キー (既定 F3) と bridge の hud から。
// HUD の根 (HudManager の GameObject) は止めない: キーボード/マウスの移動 (KeyboardJoystick) は根に付いていて、
// 根ごと止めると移動の入力が 0 のままになり歩けない。根の直下の子だけを止め、止めた子を覚えて戻す時にそれだけを付け直す
internal static class PhotoMode
{
    private static readonly List<GameObject> Hidden = new();
    private static System.IntPtr _hud;

    internal static bool On => _hud != System.IntPtr.Zero && Vanilla.Hud is { } h && h.Pointer == _hud;

    internal static void Toggle() => Set(!On);

    internal static bool Set(bool on)
    {
        var hud = Vanilla.Hud;
        if (!hud) return false;
        // 前の HUD (場面が替わって作り直された) で隠した物は捨てる
        if (_hud != System.IntPtr.Zero && _hud != hud.Pointer) { Hidden.Clear(); _hud = System.IntPtr.Zero; }
        // 前の作りで根ごと止めていた時の名残を戻す
        if (!hud.gameObject.activeSelf) hud.gameObject.SetActive(true);
        if (on == On) return true;
        var root = hud.transform;
        if (on)
        {
            // 画面のスティック (スマホの移動) は残す
            var stick = hud.joystick?.TryCast<MonoBehaviour>();
            var keep = stick ? stick.transform : null;
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (keep && (child == keep || keep.IsChildOf(child))) continue;
                var go = child.gameObject;
                if (!go.activeSelf) continue;
                go.SetActive(false);
                Hidden.Add(go);
            }
            _hud = hud.Pointer;
        }
        else
        {
            foreach (var go in Hidden)
                if (go) go.SetActive(true);
            Hidden.Clear();
            _hud = System.IntPtr.Zero;
        }
        Plugin.Logger.LogInfo($"photo mode: {(on ? "on" : "off")}");
        return true;
    }
}
