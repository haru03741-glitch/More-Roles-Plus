using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 能力ボタン。本編の能力ボタンを複製して右下の並びに足す (並べるのは本編の GridArrange)。
//   var b = ModButton.Create(Lifespan, ButtonIcons.Get("hammer"), new Text("ハンマー", "Hammer"), 3f, () => ...);
// 寿命が切れると消える。押した時の処理は AbilityButton.DoClick の前置で受ける (UnityEvent に管理側の
// ラムダを登録すると、ゲーム側と mod 側をまたいで参照が回り回収されないため登録しない)。
// 待ち時間は FixedUpdate で数え (HUD が隠れている間・動けない間は止める)、表示は冷めている間だけ 0.1 秒ごとに書き直す
internal sealed class ModButton
{
    private const int RedrawTicks = 5; // 待ち時間の表示を書き直す間隔 (FixedUpdate の回数)

    private static readonly List<ModButton> All = new();
    private static bool _hudShown = true;
    private static int _tick;
    private static float _dt; // 物理の刻み (本編は 1/30 秒。0.02 秒の決め打ちだと実時間の 6 割で減る)。試合中は変わらないので 1 回だけ読む

    private readonly AbilityButton _button;
    private readonly Func<bool> _onClick;
    private readonly IntPtr _ptr;
    private float _left;
    private bool _usable = true;

    public float Cooldown { get; set; }

    // HUD が出直した時 (試合の始め・会議の後) に待ち時間を最初から数え直す
    public bool ResetOnShow { get; set; }

    public bool CoolingDown => _left > 0f;

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
        var hud = Vanilla.Hud;
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

    // 押せるか (狙う相手がいない・回数を使い切った時に false)。待ち時間中の表示はそのまま
    public void SetUsable(bool usable)
    {
        if (_usable == usable) return;
        _usable = usable;
        if (_left > 0f || !_button) return;
        if (usable) _button.SetEnabled();
        else _button.SetDisabled();
    }

    // 残りの回数を出す (負なら回数の表示を隠す)
    public void SetUses(int left)
    {
        if (!_button) return;
        var uses = _button.transform.Find("Uses");
        if (left < 0)
        {
            if (uses) uses.gameObject.SetActive(false);
            return;
        }
        if (uses) uses.gameObject.SetActive(true);
        _button.SetUsesRemaining(left);
    }

    // 待ち時間を始める (押した後でなく、ホストが通した時に始める能力から)
    public void StartCooldown()
    {
        if (Cooldown <= 0f || !_button) return;
        _left = Cooldown;
        _button.SetCoolDown(_left, Cooldown);
        _button.SetDisabled();
    }

    // 押された (待ち時間中・押せない時は何もしない)
    private void Click()
    {
        if (_left > 0f || !_usable) return;
        bool used;
        try { used = _onClick(); }
        catch (Exception e) { Plugin.Logger.LogError($"button {_button.buttonLabelText.text}: {e}"); return; }
        if (used) StartCooldown();
    }

    // 毎 FixedUpdate。ボタンが無い時・冷めている時は数の比較だけで帰る
    public static void TickAll()
    {
        if (All.Count == 0 || !_hudShown) return;
        _tick++;
        bool redraw = _tick % RedrawTicks == 0;
        bool? canMove = null; // 冷めていないボタンがある時だけ 1 回読む
        for (int i = 0; i < All.Count; i++)
        {
            var b = All[i];
            if (b._left <= 0f) continue;
            if (canMove == null)
            {
                var lp = PlayerControl.LocalPlayer;
                canMove = lp && lp.CanMove;
            }
            if (canMove == false) return;
            if (_dt <= 0f) _dt = Time.fixedDeltaTime;
            b._left -= _dt;
            bool done = b._left <= 0f;
            if (!done && !redraw) continue;
            // 表示を書く時だけ生きているかを見る (HUD ごと消えたボタンは寿命が切れるまで一覧に残る)
            if (!b._button) { b._left = 0f; continue; }
            if (done)
            {
                b._left = 0f;
                b._button.SetCoolDown(0f, b.Cooldown);
                if (b._usable) b._button.SetEnabled();
                else b._button.SetDisabled();
            }
            else b._button.SetCoolDown(b._left, b.Cooldown);
        }
    }

    // 本編が HUD を出し入れする時 (会議・死亡・イントロ) に合わせる
    public static void SetHudShown(bool shown)
    {
        bool reshown = shown && !_hudShown;
        _hudShown = shown;
        if (All.Count == 0) return;
        // HUD ごと消えた後 (シーンが変わった) のボタンは寿命が切れるまで残るので飛ばす
        foreach (var b in All)
        {
            if (!b._button) continue;
            b._button.gameObject.SetActive(shown);
            if (reshown && b.ResetOnShow) b.StartCooldown();
        }
        Arrange();
    }

    private static void Arrange()
    {
        var hud = Vanilla.Hud;
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

    internal static bool HudShown => _hudShown;

    // 本編の PassiveButton から押す (人が押したのと同じく DoClick の前置を通る)
    internal void Press()
    {
        if (_button) _button.GetComponent<PassiveButton>().OnClick.Invoke();
    }

    internal string Describe() =>
        _button ? $"{_button.buttonLabelText.text} left={_left:0.00} usable={_usable} on={_button.gameObject.activeInHierarchy}" : "dead";

    // テスト用: 一覧 (press = 押すボタンの番号・-1 で押さない)。押すのは本編の PassiveButton から (DoClick の前置を通る)
    internal static string Probe(int press)
    {
        var sb = new System.Text.StringBuilder($"n={All.Count}");
        for (int i = 0; i < All.Count; i++)
        {
            var b = All[i];
            if (!b._button) { sb.Append($" #{i}=dead"); continue; }
            var p = b._button.transform.localPosition;
            sb.Append($" #{i}={b.Describe()} at={p.x:0.##},{p.y:0.##}");
        }
        if (press >= 0 && press < All.Count && All[press]._button)
        {
            All[press].Press();
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
