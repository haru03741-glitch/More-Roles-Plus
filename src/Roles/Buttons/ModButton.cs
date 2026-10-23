// Showing the remaining effect time on the button based on https://github.com/All-Of-Us-Mods/MiraAPI (CustomActionButton, LGPL-2.1)
using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 能力ボタン。本編の能力ボタンを複製して右下の並びに足す (並べるのは本編の GridArrange)。
//   var b = ModButton.Create(Lifespan, ButtonIcons.Get("hammer"), new Text("Demolisher.Hammer"), 3f, () => ...);
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
    private bool _inGrid = true; // 右下の並びに居るか (並びの外のボタンは置いた先の画面と一緒に出入りする)
    private float _left;
    private float _effect, _effectMax; // 効果の残り (本編の技術者のベントと同じく、ボタンを満たす形と残りの秒で出す)
    private int _effectShown = -1;     // 最後に書いた残りの秒
    private bool _usable = true;

    // 長押し (SetHold): 押している間だけ溜め、離した時に _onHold (溜めた値)。押したのがキーならそのキー・画面ならマウス/タッチを見る
    private Func<float> _holdMin, _holdMax;
    private Func<float, bool> _onHold;
    private bool _holding;
    private float _hold;
    private int _holdShown = -1;
    private KeyCode _holdKey, _pressKey;
    private int _holdFinger = -1;
    private long _holdTestUntil; // テスト用: この時刻まで押している扱い (0 = 使わない)
    private PassiveButton _pb;
    // 長押しできることを知らせる札 (押せる間だけ右上の角に出す)。一度溜めて使えば、その起動の間は出さない
    private TMPro.TextMeshPro _hint;
    private bool _hintShown;
    internal static bool HoldLearned;
    // 溜めた秒を 1 秒に 1 つ点す粒 (ボタンの縁の弧・溜めている間だけ)
    private GameObject _pipRoot;
    private SpriteRenderer[] _pips;
    private int _pipsLit;
    private float _pipPop, _pipPulse;
    private static Sprite _pipSprite;

    public float Cooldown { get; set; }

    // 使った後の効果の長さ (秒・0 = 無し)。効果の間はボタンに残りを出し、切れてから待ち時間を始める
    public Func<float> Effect { get; set; }

    // 効果が切れた時 (この端末で)
    public Action OnEffectEnd { get; set; }

    public bool InEffect => _effect > 0f;

    // HUD が出直した時 (試合の始め・会議の後) に待ち時間を最初から数え直す
    public bool ResetOnShow { get; set; }

    public bool CoolingDown => _left > 0f;

    // 待ち時間・効果の残り (秒)
    internal float Left => _left;
    internal float EffectLeft => _effect;

    // 時戻し: 待ち時間を記録の時の値へ戻す。効果の途中へは戻せないので、今の効果は終わらせ、
    // 記録の時に効果中だった時は残りの効果の分も待たせる
    internal void RewindTo(float left, float effect)
    {
        if (_effect > 0f)
        {
            _effect = 0f;
            try { OnEffectEnd?.Invoke(); }
            catch (Exception e) { Plugin.Logger.LogError($"button effect end: {e}"); }
            if (_button)
            {
                _button.SetFillUp(0f, _effectMax);
                if (_button.cooldownTimerText) _button.cooldownTimerText.gameObject.SetActive(false);
            }
            _effectShown = -1;
        }
        _left = Math.Max(0f, left + Math.Max(0f, effect));
        if (!_button) return;
        UpdateHint();
        if (_left > 0f)
        {
            _button.SetCoolDown(_left, Math.Max(Cooldown, _left));
            _button.SetDisabled();
            return;
        }
        _button.SetCoolDown(0f, Cooldown);
        if (_usable) _button.SetEnabled();
        else _button.SetDisabled();
    }

    // 並びの外に置いたボタンを動かす・出し入れする時に (HUD ごと消えた後は null)
    public Transform Transform => _button ? _button.transform : null;

    // アイコンの絵 (本編の能力ボタンの graphic)
    public SpriteRenderer Graphic => _button ? _button.graphic : null;

    private ModButton(AbilityButton button, float cooldown, Func<bool> onClick)
    {
        _button = button;
        _ptr = button.Pointer;
        Cooldown = cooldown;
        _onClick = onClick;
    }

    // onClick の返り値 = 使えたか (false なら待ち時間を始めない)。
    // parent を渡すと右下の並びの外 (その子) に置く (位置は呼び手が決める・HUD と一緒に出入りするのは同じ)
    public static ModButton Create(Lifespan life, Sprite icon, string label, float cooldown, Func<bool> onClick, Transform parent = null)
    {
        var hud = Vanilla.Hud;
        if (!hud || !hud.AbilityButton || life.IsDead) return null;
        var src = hud.AbilityButton;
        bool inGrid = !parent; // 消える時には parent が先に消えていることがあるので、作った時に決める
        var b = UnityEngine.Object.Instantiate(src, inGrid ? src.transform.parent : parent);
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

        var mb = new ModButton(b, cooldown, onClick) { _inGrid = inGrid };
        All.Add(mb);
        life.OnRelease(() =>
        {
            All.Remove(mb);
            // 待ち時間の表示で複製されたマテリアルは GameObject と一緒には消えない
            if (b && b.graphic) UnityEngine.Object.Destroy(b.graphic.material);
            // Destroy は遅れて効くので、先に並びから外す (同じフレームの並べ直しが消えかけのボタンを読んで落ちる)
            if (b)
            {
                b.transform.SetParent(null, false);
                UnityEngine.Object.Destroy(b.gameObject);
            }
            // 並びの外のボタンは並びに居ないので並べ直さない (HUD が隠れている間の並べ直しは本編の中で落ちる)
            if (inGrid) Arrange();
        });
        if (inGrid) Arrange();
        return mb;
    }

    // 押せるか (狙う相手がいない・回数を使い切った時に false)。待ち時間中の表示はそのまま
    public void SetUsable(bool usable)
    {
        if (_usable == usable) return;
        _usable = usable;
        if (_left > 0f || _effect > 0f || !_button) return;
        UpdateHint();
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

    // 使った: 効果があれば効果から、無ければ待ち時間から
    public void StartUsed()
    {
        float d = Effect?.Invoke() ?? 0f;
        if (d <= 0f || !_button || Dev.DevCooldown.Zero)
        {
            StartCooldown();
            return;
        }
        _effect = _effectMax = d;
        _left = 0f;
        _effectShown = -1;
        _button.SetDisabled();
        UpdateHint();
        DrawEffect();
    }

    // 本編の SetFillUp は満たす形だけで残りの数は出さないので、数の欄は秒が変わった時だけ書く
    private void DrawEffect()
    {
        _button.SetFillUp(_effect, _effectMax);
        int sec = (int)Math.Ceiling(_effect);
        var text = _button.cooldownTimerText;
        if (!text || sec == _effectShown) return;
        _effectShown = sec;
        text.text = sec.ToString();
        if (!text.gameObject.activeSelf) text.gameObject.SetActive(true);
    }

    // 待ち時間を始める (押した後でなく、ホストが通した時に始める能力から)
    public void StartCooldown()
    {
        if (Cooldown <= 0f || !_button || Dev.DevCooldown.Zero) return;
        _left = Cooldown;
        _button.SetCoolDown(_left, Cooldown);
        _button.SetDisabled();
        UpdateHint();
    }

    // 長押しで溜めるボタンにする。押した瞬間に OnClick が来るように本編のボタンを押し下げで反応させる
    // (離した時の OnClick では押している間が分からない)。onClick は押せるかだけを返す
    public void SetHold(Func<float> min, Func<float> max, Func<float, bool> onRelease, string hint)
    {
        _holdMin = min;
        _holdMax = max;
        _onHold = onRelease;
        if (!_button) return;
        _pb = _button.GetComponent<PassiveButton>();
        if (_pb) { _pb.OnDown = true; _pb.OnUp = false; }
        MakeHint(hint);
        UpdateHint();
    }

    // 札はボタンの名前の字を複製して作る (書体・色・縁取りを本編の字と同じにする)
    private void MakeHint(string hint)
    {
        var label = _button.buttonLabelText;
        if (!label || string.IsNullOrEmpty(hint)) return;
        var go = UnityEngine.Object.Instantiate(label.gameObject, label.transform.parent);
        go.name = "MrpHoldHint";
        // 名前の字の言語表の部品は Create で外したが、Destroy は遅れて効くので複製にも付いてくる
        var translator = go.GetComponent<TextTranslatorTMP>();
        if (translator) UnityEngine.Object.Destroy(translator);
        _hint = go.GetComponent<TMPro.TextMeshPro>();
        _hint.enableAutoSizing = false;
        _hint.fontSize = HintFontSize;
        _hint.text = hint;
        go.transform.localPosition = FxMath.V3(HintX, HintY, label.transform.localPosition.z);
        _hintShown = true;
    }

    private const float HintFontSize = 2.4f, HintX = 0.3f, HintY = 0.5f;

    // 札を出すのは押せる時だけ (待ち時間・効果・溜めている間・押せない間は隠す)。状態が変わる所から呼ぶ
    private void UpdateHint()
    {
        if (_hint == null) return;
        bool want = !HoldLearned && !_holding && _left <= 0f && _effect <= 0f && _usable;
        if (want == _hintShown || !_hint) return;
        _hintShown = want;
        _hint.gameObject.SetActive(want);
    }

    internal bool Holding => _holding;
    internal float HoldValue => _hold;

    // 押された (待ち時間中・押せない時は何もしない)
    private void Click()
    {
        if (_left > 0f || _effect > 0f || !_usable || _holding) return;
        bool used;
        try { used = _onClick(); }
        catch (Exception e) { Plugin.Logger.LogError($"button {_button.buttonLabelText.text}: {e}"); return; }
        if (!used) return;
        if (_onHold == null) { StartUsed(); return; }
        _hold = _holdMin();
        _holdKey = _pressKey;
        _holdFinger = _holdKey == KeyCode.None ? PressedFinger() : -1;
        _holdShown = -1;
        _holding = true;
        UpdateHint();
        ShowPips();
        DrawHold();
    }

    // 粒の数 = 溜められる秒の段の数 (下限の秒で 1 つ目が点る)。上限の設定が変わっていたら作り直す
    private void ShowPips()
    {
        int n = Math.Clamp((int)_holdMax() - (int)_holdMin() + 1, 1, MaxPips);
        if (_pips == null || _pips.Length != n || !_pipRoot)
        {
            if (_pipRoot) UnityEngine.Object.Destroy(_pipRoot);
            _pipSprite ??= FxSprites.Rect(32, 32, FxSprites.Dot, "MrpHoldPip");
            int layer = _button.gameObject.layer;
            _pipRoot = new GameObject("MrpHoldPips") { layer = layer };
            _pipRoot.transform.SetParent(_button.transform, false);
            _pips = new SpriteRenderer[n];
            for (int i = 0; i < n; i++)
            {
                var go = new GameObject("Pip") { layer = layer };
                go.transform.SetParent(_pipRoot.transform, false);
                // 真上から時計回りに右肩へ (左上の角は回数の丸がある)。1 つだけの時は真上
                float ang = n == 1 ? 90f : PipArcFrom - (PipArcFrom - PipArcTo) * i / (n - 1);
                float rad = ang * FxMath.Deg2Rad;
                go.transform.localPosition = FxMath.V3(PipRadius * FxMath.Cos(rad), PipRadius * FxMath.Sin(rad), PipZ);
                go.transform.localScale = FxMath.V3(PipSize, PipSize, 1f);
                _pips[i] = go.AddComponent<SpriteRenderer>();
                _pips[i].sprite = _pipSprite;
            }
        }
        for (int i = 0; i < _pips.Length; i++) _pips[i].color = PipOff;
        _pipsLit = 0;
        _pipPop = _pipPulse = 0f;
        _pipRoot.SetActive(true);
    }

    private const int MaxPips = 16;
    private const float PipArcFrom = 100f, PipArcTo = -35f, PipRadius = 0.72f, PipSize = 0.15f, PipZ = -0.12f, PipPopTime = 0.25f;
    private static readonly Color PipOff = FxMath.Rgba(1f, 1f, 1f, 0.28f);
    private static readonly Color PipOn = FxMath.Rgba(0.55f, 0.95f, 1f, 1f);

    // 点った粒の数を溜めた秒に合わせる。新しく点った粒は大きく出て戻り、上限まで溜まったら全部が脈打つ
    private void DrawPips()
    {
        if (_pips == null || !_pipRoot) return;
        int lit = Math.Clamp((int)(_hold - _holdMin()) + 1, 0, _pips.Length);
        if (lit != _pipsLit)
        {
            for (int i = _pipsLit; i < lit; i++) _pips[i].color = PipOn;
            for (int i = lit; i < _pipsLit; i++) _pips[i].color = PipOff;
            _pipsLit = lit;
            _pipPop = PipPopTime;
        }
        if (_pipPop > 0f && lit > 0)
        {
            _pipPop = Math.Max(0f, _pipPop - _dt);
            float k = PipSize * (1f + 0.9f * _pipPop / PipPopTime);
            _pips[lit - 1].transform.localScale = FxMath.V3(k, k, 1f);
        }
        if (lit == _pips.Length && _pipPop <= 0f)
        {
            _pipPulse += _dt;
            var c = PipOn;
            c.a = 0.65f + 0.35f * FxMath.Cos(_pipPulse * 12.6f); // 2 回/秒
            for (int i = 0; i < lit; i++) _pips[i].color = c;
        }
    }

    private void HidePips()
    {
        if (_pips == null || !_pipRoot) return;
        _pipRoot.SetActive(false);
        for (int i = 0; i < _pips.Length; i++) _pips[i].transform.localScale = FxMath.V3(PipSize, PipSize, 1f);
    }

    // 画面を触って押した時: このボタンを押した指 (今触れ始めた指・無ければ最後の指)。触れていなければ -1 (マウス)
    private static int PressedFinger()
    {
        int n = Input.touchCount;
        if (n == 0) return -1;
        for (int i = 0; i < n; i++)
        {
            var t = Input.GetTouch(i);
            if (t.phase == TouchPhase.Began) return t.fingerId;
        }
        return Input.GetTouch(n - 1).fingerId;
    }

    // スマホでは押した指だけを見る (もう片方の指で歩いている間も、ボタンの指を離せば離したことになる)
    private bool HeldDown()
    {
        if (_holdTestUntil != 0) return Environment.TickCount64 < _holdTestUntil;
        if (_holdKey != KeyCode.None) return Input.GetKey(_holdKey);
        if (_holdFinger < 0) return Input.GetMouseButton(0);
        int n = Input.touchCount;
        for (int i = 0; i < n; i++)
        {
            var t = Input.GetTouch(i);
            if (t.fingerId == _holdFinger) return t.phase != TouchPhase.Ended && t.phase != TouchPhase.Canceled;
        }
        return false;
    }

    // 溜めた値を本編の効果の残りと同じ形で出す (満たす形 + 数の欄)。数の欄は値が変わった時だけ書く
    private void DrawHold()
    {
        if (!_button) return;
        float max = _holdMax();
        _button.SetFillUp(_hold, max);
        DrawPips();
        int shown = (int)_hold;
        var text = _button.cooldownTimerText;
        if (!text || shown == _holdShown) return;
        _holdShown = shown;
        text.text = shown.ToString();
        if (!text.gameObject.activeSelf) text.gameObject.SetActive(true);
    }

    private void EndHold(bool use)
    {
        _holding = false;
        _holdTestUntil = 0;
        _holdShown = -1;
        if (use && _hold >= _holdMin() + 1f) HoldLearned = true;
        if (_button)
        {
            HidePips();
            _button.SetFillUp(0f, 1f);
            if (_button.cooldownTimerText) _button.cooldownTimerText.gameObject.SetActive(false);
        }
        if (!use) { UpdateHint(); return; }
        bool used;
        try { used = _onHold(_hold); }
        catch (Exception e) { Plugin.Logger.LogError($"button hold: {e}"); UpdateHint(); return; }
        if (used) StartUsed();
        if (HoldLearned) foreach (var b in All) b.UpdateHint();
        else UpdateHint();
    }

    // 溜めている間 (毎 FixedUpdate): 押し続けていれば溜め、離したら使う
    private void TickHold()
    {
        if (!_button) { _holding = false; return; }
        if (!HeldDown()) { EndHold(true); return; }
        _hold = Math.Min(_hold + _dt, _holdMax());
        DrawHold();
    }

    private void EndEffect()
    {
        _effect = 0f;
        try { OnEffectEnd?.Invoke(); }
        catch (Exception e) { Plugin.Logger.LogError($"button effect end: {e}"); }
        if (!_button) return;
        _button.SetFillUp(0f, _effectMax);
        if (_button.cooldownTimerText) _button.cooldownTimerText.gameObject.SetActive(false); // 待ち時間があれば SetCoolDown が出し直す
        _effectShown = -1;
        StartCooldown();
        if (_left <= 0f)
        {
            if (_usable) _button.SetEnabled();
            else _button.SetDisabled();
            UpdateHint();
        }
    }

    // 毎 FixedUpdate。ボタンが無い時・冷めている時は数の比較だけで帰る
    public static void TickAll()
    {
        if (All.Count == 0 || !_hudShown) return;
        _tick++;
        bool redraw = _tick % RedrawTicks == 0;
        if (_dt <= 0f) _dt = Time.fixedDeltaTime;
        for (int i = 0; i < All.Count; i++)
        {
            var b = All[i];
            if (!b._holding) continue;
            b.TickHold();
            if (i < All.Count && All[i] != b) i--; // 使った処理で寿命が切れて一覧から外れた
        }
        // 効果は動けない間も減る (本編の効果と同じ)
        for (int i = 0; i < All.Count; i++)
        {
            var b = All[i];
            if (b._effect <= 0f) continue;
            b._effect -= _dt;
            if (b._effect <= 0f)
            {
                b.EndEffect();
                if (i < All.Count && All[i] != b) i--; // 効果の終わりの処理で寿命が切れて一覧から外れた
            }
            else if (redraw && b._button) b.DrawEffect();
        }
        bool? canMove = null; // 冷めていないボタンがある時だけ 1 回読む
        for (int i = 0; i < All.Count; i++)
        {
            var b = All[i];
            if (b._left <= 0f) continue;
            if (canMove == null)
            {
                var lp = PlayerControl.LocalPlayer;
                canMove = lp && Rules.VanillaTweaks.CooldownTicks(lp);
            }
            if (canMove == false) return;
            if (_dt <= 0f) _dt = Time.fixedDeltaTime;
            b._left = Dev.DevCooldown.Zero ? 0f : b._left - _dt;
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
                b.UpdateHint();
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
        // HUD ごと消えた後 (シーンが変わった) のボタンは寿命が切れるまで残るので飛ばす。
        // 効果の終わりの処理が寿命を切って一覧から外すことがあるので写しを回す
        foreach (var b in All.ToArray())
        {
            if (!shown && b._effect > 0f) b.EndEffect(); // 会議・死亡で効果は切れる (本編の効果と同じ)
            if (!shown && b._holding) b.EndHold(false);  // 溜めている途中の会議・死亡は使わずに捨てる
            if (!b._inGrid || !b._button) continue;
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
    // key = キーで押した時のそのキー (長押しのボタンはキーを離すまで溜める)
    internal void Press(KeyCode key = KeyCode.None)
    {
        if (!_button) return;
        _pressKey = key;
        try { _button.GetComponent<PassiveButton>().OnClick.Invoke(); }
        finally { _pressKey = KeyCode.None; }
    }

    // テスト用: i 番のボタンを ms の間押し続けた扱いで押す (長押しのボタンだけ)
    internal static bool PressHeldForTest(int i, int ms)
    {
        if (i < 0 || i >= All.Count || All[i]._onHold == null || !All[i]._button) return false;
        var b = All[i];
        b._holdTestUntil = Environment.TickCount64 + ms; // 押した瞬間の溜め始めより先に立てる (始めの判定で離した扱いにしない)
        b.Press();
        if (!b._holding) b._holdTestUntil = 0;
        return true;
    }

    internal string Describe() =>
        _button ? $"{_button.buttonLabelText.text} left={_left:0.00} usable={_usable} on={_button.gameObject.activeInHierarchy}"
            + (_onHold != null ? $" hold={(_holding ? _hold.ToString("0.00") : "-")}" + (_pb ? $" down={_pb.OnDown} up={_pb.OnUp} held={_pb.beingHeldDown}" : "") : "")
            : "dead";

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
