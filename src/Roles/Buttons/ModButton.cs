using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 能力ボタン。本編の能力ボタンを複製して右下の並びに足す (並べるのは本編の GridArrange)。
//   var b = ModButton.Create(Lifespan, ButtonIcons.Get("hammer"), new Text("ハンマー", "Hammer"), 3f, () => ...);
// 寿命が切れると消える。押した時の処理は AbilityButton.DoClick の前置で受ける (UnityEvent に管理側の
// ラムダを登録すると、ゲーム側と mod 側をまたいで参照が回り回収されないため登録しない)。
// 待ち時間は FixedUpdate で数え、表示は冷めている間だけ 0.1 秒ごとに書き直す
internal sealed class ModButton
{
    private const int RedrawTicks = 5; // 待ち時間の表示を書き直す間隔 (FixedUpdate の回数)
    private const float Tick = 0.02f;

    private static readonly List<ModButton> All = new();
    private static bool _hudShown = true;
    private static int _tick;

    private readonly AbilityButton _button;
    private readonly Func<bool> _onClick;
    private readonly IntPtr _ptr;
    private float _left;

    public float Cooldown { get; set; }

    private ModButton(AbilityButton button, float cooldown, Func<bool> onClick)
    {
        _button = button;
        _ptr = button.Pointer;
        Cooldown = cooldown;
        _onClick = onClick;
    }

    // onClick の返り値 = 使えたか (false なら待ち時間を始めない)
    public static ModButton Create(Lifespan life, Sprite icon, string label, float cooldown, Func<bool> onClick)
    {
        var hud = HudManager.InstanceExists ? HudManager.Instance : null;
        if (!hud || !hud.AbilityButton || life.IsDead) return null;
        var src = hud.AbilityButton;
        var b = UnityEngine.Object.Instantiate(src, src.transform.parent);
        b.name = "MrpButton";
        // ラベルを本編の言語表で書き戻す部品を外す
        var translator = b.buttonLabelText.GetComponent<TextTranslatorTMP>();
        if (translator) UnityEngine.Object.Destroy(translator);
        b.buttonLabelText.text = label;
        if (icon)
        {
            b.graphic.sprite = icon;
            b.graphic.SetCooldownNormalizedUvs();
        }
        var uses = b.transform.Find("Uses");
        if (uses) uses.gameObject.SetActive(false);
        b.SetCoolDown(0f, 1f);
        b.SetEnabled();
        b.gameObject.SetActive(_hudShown);

        var mb = new ModButton(b, cooldown, onClick);
        All.Add(mb);
        life.OnRelease(() =>
        {
            All.Remove(mb);
            // 待ち時間の表示で複製されたマテリアルは GameObject と一緒には消えない
            if (b && b.graphic) UnityEngine.Object.Destroy(b.graphic.material);
            if (b) UnityEngine.Object.Destroy(b.gameObject);
            Arrange();
        });
        Arrange();
        return mb;
    }

    // 押された (待ち時間中は何もしない)
    private void Click()
    {
        if (_left > 0f) return;
        bool used;
        try { used = _onClick(); }
        catch (Exception e) { Plugin.Logger.LogError($"button {_button.buttonLabelText.text}: {e}"); return; }
        if (!used || Cooldown <= 0f) return;
        _left = Cooldown;
        _button.SetCoolDown(_left, Cooldown);
        _button.SetDisabled();
    }

    // 毎 FixedUpdate。ボタンが無い時・冷めている時は数の比較だけで帰る
    public static void TickAll()
    {
        if (All.Count == 0) return;
        _tick++;
        bool redraw = _tick % RedrawTicks == 0;
        for (int i = 0; i < All.Count; i++)
        {
            var b = All[i];
            if (b._left <= 0f) continue;
            b._left -= Tick;
            bool done = b._left <= 0f;
            if (!done && !redraw) continue;
            // 表示を書く時だけ生きているかを見る (HUD ごと消えたボタンは寿命が切れるまで一覧に残る)
            if (!b._button) { b._left = 0f; continue; }
            if (done)
            {
                b._left = 0f;
                b._button.SetCoolDown(0f, b.Cooldown);
                b._button.SetEnabled();
            }
            else b._button.SetCoolDown(b._left, b.Cooldown);
        }
    }

    // 本編が HUD を出し入れする時 (会議・死亡・イントロ) に合わせる
    public static void SetHudShown(bool shown)
    {
        _hudShown = shown;
        if (All.Count == 0) return;
        // HUD ごと消えた後 (シーンが変わった) のボタンは寿命が切れるまで残るので飛ばす
        foreach (var b in All) if (b._button) b._button.gameObject.SetActive(shown);
        Arrange();
    }

    private static void Arrange()
    {
        var hud = HudManager.InstanceExists ? HudManager.Instance : null;
        if (!hud || !hud.AbilityButton) return;
        var grid = hud.AbilityButton.transform.parent.GetComponent<GridArrange>();
        if (grid) grid.ArrangeChilds();
    }

    internal static bool Owns(AbilityButton b, out ModButton mine)
    {
        IntPtr p = b.Pointer;
        // 消えたボタンの番地は別の物に使い回されうるので、生きている物だけ比べる
        foreach (var x in All)
            if (x._ptr == p && x._button) { mine = x; return true; }
        mine = null;
        return false;
    }

    internal static bool HandleClick(AbilityButton b)
    {
        if (!Owns(b, out var mine)) return false;
        mine.Click();
        return true;
    }

    internal static int Count => All.Count;

    // テスト用: 一覧 (press = 押すボタンの番号・-1 で押さない)。押すのは本編の PassiveButton から (DoClick の前置を通る)
    internal static string Probe(int press)
    {
        var sb = new System.Text.StringBuilder($"n={All.Count}");
        for (int i = 0; i < All.Count; i++)
        {
            var b = All[i];
            if (!b._button) { sb.Append($" #{i}=dead"); continue; }
            var p = b._button.transform.localPosition;
            sb.Append($" #{i}={b._button.buttonLabelText.text} left={b._left:0.00} on={b._button.gameObject.activeInHierarchy} at={p.x:0.##},{p.y:0.##}");
        }
        if (press >= 0 && press < All.Count && All[press]._button)
        {
            All[press]._button.GetComponent<PassiveButton>().OnClick.Invoke();
            sb.Append($" pressed=#{press} left={All[press]._left:0.00}");
        }
        return sb.ToString();
    }
}

[HarmonyPatch(typeof(AbilityButton), nameof(AbilityButton.DoClick))]
internal static class ModButtonClickPatch
{
    public static bool Prefix(AbilityButton __instance) => !ModButton.HandleClick(__instance);
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.SetHudActive), typeof(PlayerControl), typeof(RoleBehaviour), typeof(bool))]
internal static class ModButtonHudPatch
{
    public static void Postfix(PlayerControl localPlayer, bool isActive) =>
        ModButton.SetHudShown(isActive && localPlayer && localPlayer.Data != null && !localPlayer.Data.IsDead);
}
