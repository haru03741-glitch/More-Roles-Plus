using System;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Roles;
using MoreRolesPlus.Roles.Neutral;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.UI;

// 立ち合い (Duel) の当事者の画面に出す格闘ゲーム風の HUD。上の左右に自分 (左) と相手 (右) の体力のバー・その下に気のバー・中央に ∞・
// 始まりに VS の幕 (DuelHud.Intro.cs) →「FIGHT!」の大きな題字・続けて当てると「n HIT COMBO」・相手が倒れると「K.O.」・鍔迫り合いの決着の題字。
// 字は本編のタスク欄の字を複製して使う (書体を合わせる)。作るのは最初に出す時の 1 回で、終わったら隠すだけ。
// 毎フレーム書くのは題字・連撃・削られた分の影が動いている間だけ
internal static partial class DuelHud
{
    private const float Z = -500f;
    private const float BarH = 0.16f, BarGap = 0.32f, BarTop = 1.05f; // バーの高さ・中央からの空き・画面の上からの位置 (本編の上の段のボタンの下)
    private const float FightPop = 0.15f, FightHold = 0.5f, TitleFade = 0.2f;
    private const float ComboKeep = 1.6f, KoHold = 0.9f, GhostDelay = 0.35f, GhostSpeed = 0.9f;

    private static GameObject _root;
    private static Transform _rootTf;
    private static SpriteRenderer _leftFill, _rightFill, _leftGhost, _rightGhost, _leftFace, _rightFace;
    private static TextMeshPro _leftName, _rightName, _center, _title, _hint;
    private static Material[] _mats = Array.Empty<Material>();
    private static float _halfW, _halfH, _barW;

    private static bool _shown;
    private static byte _me = 255, _opp = 255;
    private static float _startT = -10f, _koT = -10f, _comboT = -10f, _comboPop = -10f;
    private static int _combo;
    private static float _leftHp = 1f, _rightHp = 1f, _leftGhostHp = 1f, _rightGhostHp = 1f, _leftHitT, _rightHitT;
    private static bool _hooked;
    private static int _titleState = -1; // 0 = 幕の間 (題字なし)・1 = FIGHT!・2 以上 = 2 + 知らせの番号・-1 = 無し
    // 大きな題字で知らせる出来事
    private const int BannerKo = 0, BannerBreak = 1, BannerBroken = 2, BannerDraw = 3;
    private static readonly string[] BannerText = { "K.O.", "BREAK!", "GUARD BREAK", "DRAW" };
    private static int _banner;
    private static float _bannerT = -10f;

    internal static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        Duel.OnStart += OnStart;
        Duel.OnHit += OnHit;
        Duel.OnEnd += OnEnd;
    }

    private static byte LocalId => PlayerControl.LocalPlayer ? PlayerControl.LocalPlayer.PlayerId : (byte)255;

    private static void OnStart(byte a, byte t)
    {
        byte me = LocalId;
        if (me != a && me != t) return;
        Show(me, me == a ? t : a);
    }

    private static void OnHit(byte a, byte t, int hp)
    {
        if (!_shown || a == t) return; // 自分から自分 = 立ち合いの外の回復
        float k = hp / (float)Duel.MaxHp;
        float now = Time.time;
        if (t == _me) { _leftHp = k; _leftHitT = now; }
        else if (t == _opp) { _rightHp = k; _rightHitT = now; }
        if (a == _me)
        {
            _combo = now - _comboT <= ComboKeep ? _combo + 1 : 1;
            _comboT = _comboPop = now;
        }
        else if (t == _me) _combo = 0;
        Write();
    }

    private static void OnEnd(byte a, byte t)
    {
        if (!_shown || !(a == _me && t == _opp || a == _opp && t == _me)) return;
        var opp = GameData.Instance ? GameData.Instance.GetPlayerById(_opp) : null;
        if (opp != null && opp.IsDead)
        {
            // 相手が倒れた: K.O. を出してから隠す
            _rightHp = 0f;
            _koT = Time.time;
            Banner(BannerKo);
            Write();
            return;
        }
        Hide();
    }

    private static void Show(byte me, byte opp)
    {
        if (!Build()) return;
        _me = me;
        _opp = opp;
        _shown = true;
        _hint.text = "";
        _root.SetActive(true);
        // 左のタスク一覧はバーと重なるので、立ち合いの間だけ隠す
        var hud = Vanilla.Hud;
        _tasks = hud && hud.TaskPanel ? hud.TaskPanel.gameObject : null;
        _tasksWasOn = _tasks && _tasks.activeSelf;
        if (_tasksWasOn) _tasks.SetActive(false);
        _startT = Time.time;
        _koT = _comboT = _comboPop = _bannerT = -10f;
        _combo = 0;
        _titleState = -1;
        IntroBegin();
        _leftHp = _leftGhostHp = Duel.HpOf(me) / (float)Duel.MaxHp;
        _rightHp = _rightGhostHp = Duel.HpOf(opp) / (float)Duel.MaxHp;
        Paint(_leftFace, _leftName, me);
        Paint(_rightFace, _rightName, opp);
        KiShow();
        Write();
    }

    private static void Banner(int which)
    {
        _banner = which;
        _bannerT = Time.time;
    }

    private static GameObject _tasks;
    private static bool _tasksWasOn;

    // ---- 鍔迫り合い ----

    // 押し合いのバーは画面の下。左 = 自分の押し・二人の境目に刀の当たる光・上に残り時間の区切り (1 秒に 1 つ消える)
    private const float LockW = 5.2f, LockH = 0.24f, PushEase = 12f;
    private static SpriteRenderer _lockMine, _lockTheirs, _lockGlow, _lockCore;
    private static readonly SpriteRenderer[] _lockSeg = new SpriteRenderer[3];
    private static GameObject _lockRoot;
    private static TextMeshPro _lockText;
    private static float _lockY, _lockT = -10f, _push = 0.5f, _pushGoal = 0.5f;
    private static int _lockSegLeft;
    private static bool _lockOn;

    internal static void ShowLock(bool on)
    {
        if (!_root) return;
        SetLockOn(on && _shown);
        if (!_lockOn) return;
        _lockT = Time.time;
        _lockText.text = new Text("Duel.LockMash").ToString();
        _push = _pushGoal = 0.5f;
        _lockSegLeft = -1;
        Push(0.5f);
    }

    // 鍔迫り合いが終わった。result = 1 勝ち・2 負け・0 引き分け・-1 打ち切り (会議など)
    internal static void EndLock(int result)
    {
        if (!_root || !_lockOn) return;
        SetLockOn(false);
        if (result >= 0 && _shown) Banner(result == 1 ? BannerBreak : result == 2 ? BannerBroken : BannerDraw);
    }

    private static void SetLockOn(bool on)
    {
        _lockOn = on;
        _lockRoot.SetActive(on);
        LockBands(on);
    }

    // ホストから届いた連打の数 (a / b = 二人・ca / cb = それぞれの数)
    internal static void LockMeter(byte a, byte b, int ca, int cb)
    {
        if (!_lockOn) return;
        int mine, theirs;
        if (_me == a) { mine = ca; theirs = cb; }
        else if (_me == b) { mine = cb; theirs = ca; }
        else return;
        _pushGoal = (mine + 1f) / (mine + theirs + 2f);
    }

    // 押し合いの割合 (0〜1・自分の分) をバーへ。境目の光も一緒に動く
    private static void Push(float k)
    {
        float mine = LockW * FxMath.Clamp(k, 0.05f, 0.95f), theirs = LockW - mine, edge = -LockW * 0.5f + mine;
        _lockMine.transform.localPosition = FxMath.V3(-LockW * 0.5f + mine * 0.5f, _lockY, -0.1f);
        _lockMine.transform.localScale = FxMath.V3(mine, LockH, 1f);
        _lockTheirs.transform.localPosition = FxMath.V3(edge + theirs * 0.5f, _lockY, -0.1f);
        _lockTheirs.transform.localScale = FxMath.V3(theirs, LockH, 1f);
        _lockGlow.transform.localPosition = FxMath.V3(edge, _lockY, -0.2f);
        _lockCore.transform.localPosition = FxMath.V3(edge, _lockY, -0.3f);
    }

    // 押し合いは届いた割合へ滑らかに寄せる・境目の光が震える・「連打!」の字が脈打つ・残り時間の区切りが消える
    private static void LockTick(float now)
    {
        float t = now - _lockT;
        _push += (_pushGoal - _push) * FxMath.Min(1f, PushEase * Time.deltaTime);
        Push(_push);
        float s = 1f + 0.12f * FxMath.Abs(FxMath.Sin(t * 14f));
        _lockText.transform.localScale = FxMath.V3(s, s, 1f);
        float g = 0.85f + 0.35f * FxMath.Abs(FxMath.Sin(t * 37f)), c = 0.8f + 0.4f * FxMath.Abs(FxMath.Sin(t * 53f + 1f));
        _lockGlow.transform.localScale = FxMath.V3(0.62f * g, 0.62f * g, 1f);
        _lockCore.transform.localScale = FxMath.V3(0.2f * c, 0.2f * c, 1f);
        int left = (int)FxMath.Clamp(Samurai.LockTime - t + 0.999f, 0f, 3f);
        if (left == _lockSegLeft) return;
        _lockSegLeft = left;
        for (int i = 0; i < _lockSeg.Length; i++)
            _lockSeg[i].color = i < left ? FxMath.Rgba(1f, 0.9f, 0.55f, 1f) : FxMath.Rgba(0.3f, 0.3f, 0.35f, 0.5f);
    }

    // 自分のバーの下の一行 (空 = 出さない)。出していない時は何もしない (書けたら true)
    internal static bool SetHint(string text)
    {
        if (!_shown || !_hint) return false;
        _hint.text = text;
        return true;
    }

    private static void Hide()
    {
        if (_tasksWasOn && _tasks) _tasks.SetActive(true);
        _tasksWasOn = false;
        _tasks = null;
        _shown = false;
        _me = _opp = 255;
        _bannerT = -10f;
        if (_lockRoot) SetLockOn(false);
        IntroHide();
        if (_root) _root.SetActive(false);
    }

    // 毎フレーム (出している間だけ動く)
    internal static void Tick()
    {
        if (!_shown) return;
        if (!_root || MeetingHud.Instance) { Hide(); return; }
        float now = Time.time;
        // 削られた分の影は少し遅れてから減る
        bool ghost = Ghost(ref _leftGhostHp, _leftHp, _leftHitT, now) | Ghost(ref _rightGhostHp, _rightHp, _rightHitT, now);
        if (ghost) Write();
        TitleTick(now);
        IntroTick(now);
        KiTick(now);
        if (_lockOn) LockTick(now);
        ComboTick(now);
        if (_koT > 0f && now - _koT > KoHold + TitleFade) Hide();
    }

    private static bool Ghost(ref float g, float hp, float hitT, float now)
    {
        if (g <= hp) { g = hp; return false; }
        if (now - hitT < GhostDelay) return false;
        g = FxMath.Max(hp, g - GhostSpeed * Time.deltaTime);
        return true;
    }

    private static void TitleTick(float now)
    {
        float t = now - _startT;
        int state;
        float scale = 1f, alpha = 1f;
        if (now - _bannerT < KoHold + TitleFade)
        {
            state = 2 + _banner;
            float k = now - _bannerT;
            scale = k < FightPop ? FxMath.Lerp(1.6f, 1f, k / FightPop) : 1f;
            alpha = k > KoHold ? 1f - (k - KoHold) / TitleFade : 1f;
        }
        else if (t < IntroTime) state = 0;
        else if (t < IntroTime + FightPop + FightHold + TitleFade)
        {
            state = 1;
            float k = t - IntroTime;
            scale = k < FightPop ? FxMath.Lerp(1.7f, 1f, k / FightPop) : 1f;
            alpha = k > FightPop + FightHold ? 1f - (k - FightPop - FightHold) / TitleFade : 1f;
        }
        else state = -1;

        if (state != _titleState)
        {
            _titleState = state;
            bool on = state >= 1;
            _title.gameObject.SetActive(on);
            if (on)
            {
                _title.text = state == 1 ? "FIGHT!" : BannerText[state - 2];
                // 崩された側だけ赤い題字 (赤い縁では字が滲むので黒い縁)
                bool red = state == 2 + BannerBroken;
                _title.colorGradient = red ? RedGradient : GoldGradient;
                _title.outlineColor = red ? FxMath.Rgba32(18, 6, 8, 255) : FxMath.Rgba32(140, 13, 13, 255);
            }
        }
        if (state < 1) return;
        var tf = _title.transform;
        tf.localPosition = FxMath.V3(0f, 0.55f, -2f);
        tf.localScale = FxMath.V3(scale, scale, 1f);
        _title.alpha = FxMath.Clamp01(alpha);
    }

    private static VertexGradient GoldGradient => new(FxMath.Rgba(1f, 0.95f, 0.35f, 1f), FxMath.Rgba(1f, 0.95f, 0.35f, 1f),
        FxMath.Rgba(1f, 0.42f, 0.08f, 1f), FxMath.Rgba(1f, 0.42f, 0.08f, 1f));
    private static VertexGradient RedGradient => new(FxMath.Rgba(1f, 0.55f, 0.5f, 1f), FxMath.Rgba(1f, 0.55f, 0.5f, 1f),
        FxMath.Rgba(0.75f, 0.06f, 0.08f, 1f), FxMath.Rgba(0.75f, 0.06f, 0.08f, 1f));

    private static void ComboTick(float now)
    {
        bool on = _combo >= 2 && now - _comboT <= ComboKeep;
        if (_comboShown != _combo && on)
        {
            _comboShown = _combo;
            _comboTmp.text = $"<size=150%>{_combo}</size> HIT\nCOMBO";
        }
        if (_comboOn != on) { _comboOn = on; _comboTf.gameObject.SetActive(on); }
        if (!on) return;
        float p = now - _comboPop;
        float s = p < 0.12f ? FxMath.Lerp(1.35f, 1f, p / 0.12f) : 1f;
        _comboTf.localScale = FxMath.V3(s, s, 1f);
        _comboTmp.alpha = FxMath.Clamp01((ComboKeep - (now - _comboT)) / 0.3f);
    }

    private static int _comboShown = -1;
    private static bool _comboOn;
    private static Transform _comboTf;
    private static TextMeshPro _comboTmp;

    // 体力のバーと影を今の値で書く (外側の端を基準に、中央の側から減る)
    private static void Write()
    {
        if (!_root) return;
        Bar(_leftFill, _leftGhost, _leftHp, _leftGhostHp, -1f);
        Bar(_rightFill, _rightGhost, _rightHp, _rightGhostHp, 1f);
    }

    private static void Bar(SpriteRenderer fill, SpriteRenderer ghost, float hp, float gh, float side)
    {
        float outer = side * (BarGap + _barW);
        Set(fill, outer, hp, side, HpColor(hp));
        Set(ghost, outer, gh, side, FxMath.Rgba(0.95f, 0.2f, 0.2f, 1f));
    }

    private static void Set(SpriteRenderer sr, float outer, float k, float side, Color c, float h = BarH * 0.72f)
    {
        k = FxMath.Clamp01(k);
        float w = _barW * k;
        var tf = sr.transform;
        // 外側の端から中央の側へ w だけ伸びる
        tf.localPosition = FxMath.V3(outer - side * w * 0.5f, tf.localPosition.y, tf.localPosition.z);
        tf.localScale = FxMath.V3(FxMath.Max(w, 0.0001f), h, 1f);
        sr.color = c;
    }

    private static Color HpColor(float k) =>
        k > 0.5f ? FxMath.Rgba(0.36f, 0.89f, 0.29f, 1f) : k > 0.25f ? FxMath.Rgba(0.98f, 0.82f, 0.2f, 1f) : FxMath.Rgba(0.95f, 0.3f, 0.2f, 1f);

    private static void Paint(SpriteRenderer face, TextMeshPro name, byte id)
    {
        var p = GameData.Instance ? GameData.Instance.GetPlayerById(id) : null;
        int color = p != null ? p.DefaultOutfit.ColorId : 0;
        var pc = Palette.PlayerColors;
        face.color = color >= 0 && color < pc.Length ? pc[color] : FxMath.Rgba(1f, 1f, 1f, 1f);
        name.text = p != null ? p.PlayerName : "?";
    }

    // ---- 組み立て (最初に出す時の 1 回) ----

    private static bool Build()
    {
        if (_root) return true;
        // HUD ごと消えて作り直す時は、前の字の縁取りの材質を片付ける (字と一緒には消えない)
        foreach (var m in _mats) if (m) Object.Destroy(m);
        _mats = Array.Empty<Material>();
        var hud = Vanilla.Hud;
        var cam = hud ? hud.UICamera : null;
        var src = hud && hud.TaskPanel ? hud.TaskPanel.taskText : null;
        if (!hud || !cam || !src) return false;
        _halfH = cam.orthographicSize;
        _halfW = _halfH * cam.aspect;
        _barW = FxMath.Min(_halfW * 0.62f, 3.4f);
        int layer = hud.gameObject.layer;
        _root = new GameObject("MrpDuelHud") { layer = layer };
        _rootTf = _root.transform;
        _rootTf.SetParent(hud.transform, false);
        _rootTf.localPosition = FxMath.V3(0f, 0f, Z);
        float y = _halfH - BarTop;
        var white = WhiteSprite();

        foreach (float side in new[] { -1f, 1f })
        {
            float outer = side * (BarGap + _barW), mid = side * (BarGap + _barW * 0.5f);
            // 枠 (黒の板) → 削られた分の影 (赤) → 体力 (緑)
            Quad("Frame", white, mid, y, 0f, _barW + 0.06f, BarH, FxMath.Rgba(0.06f, 0.06f, 0.1f, 0.88f), layer);
            Quad("Back", white, mid, y, -0.1f, _barW, BarH * 0.72f, FxMath.Rgba(0.22f, 0.05f, 0.08f, 0.9f), layer);
            var ghost = Quad("Ghost", white, mid, y, -0.2f, _barW, BarH * 0.72f, FxMath.Rgba(0.95f, 0.2f, 0.2f, 1f), layer);
            var fill = Quad("Fill", white, mid, y, -0.3f, _barW, BarH * 0.72f, HpColor(1f), layer);
            // 顔 (陣営色の丸) と名前は外側の端
            var face = Quad("Face", Circle(), outer + side * 0.24f, y, -0.3f, 0.36f, 0.36f, FxMath.Rgba(1f, 1f, 1f, 1f), layer);
            var name = Text(src, "Name", 0f, 0f, 1.25f, side < 0 ? TextAlignmentOptions.Left : TextAlignmentOptions.Right, layer, 3f);
            Outline(name, 0.2f, FxMath.Rgba(0.05f, 0.05f, 0.08f, 1f));
            if (side < 0)
            {
                name.transform.localPosition = FxMath.V3(outer + 0.5f + 1.5f, y - 0.24f, -0.3f);
                _leftFill = fill; _leftGhost = ghost; _leftFace = face; _leftName = name;
            }
            else
            {
                name.transform.localPosition = FxMath.V3(outer - 0.5f - 1.5f, y - 0.24f, -0.3f);
                _rightFill = fill; _rightGhost = ghost; _rightFace = face; _rightName = name;
            }
        }
        _center = Text(src, "Infinity", 0f, y, 3.2f, TextAlignmentOptions.Center, layer, 1f);
        _center.text = "∞";
        Outline(_center, 0.25f, FxMath.Rgba(0.1f, 0.1f, 0.15f, 1f));
        // 自分の手 (回避の残り) は自分のバーの下
        _hint = Text(src, "Hint", -(BarGap + _barW * 0.5f), y - 0.62f, 1.6f, TextAlignmentOptions.Center, layer, 4f);
        _hint.color = FxMath.Rgba(1f, 0.92f, 0.55f, 1f);
        Outline(_hint, 0.25f, FxMath.Rgba(0.05f, 0.05f, 0.08f, 1f));
        _hint.text = "";

        _title = Text(src, "Title", 0f, 0.55f, 9f, TextAlignmentOptions.Center, layer, 12f);
        _title.fontStyle = FontStyles.Bold | FontStyles.Italic;
        _title.enableVertexGradient = true;
        _title.colorGradient = GoldGradient;
        Outline(_title, 0.28f, FxMath.Rgba(0.55f, 0.05f, 0.05f, 1f));
        _title.gameObject.SetActive(false);

        BuildKi(white, layer, y);
        BuildIntro(white, layer, src);

        // 鍔迫り合いの押し合いのバー (画面の下・左 = 自分の押し)。縁の明るい線 → 黒い枠 → 二人の押し → 境目の光
        float ly = -_halfH + 1.35f;
        _lockRoot = new GameObject("Lock") { layer = layer };
        var lockTf = _lockRoot.transform;
        lockTf.SetParent(_rootTf, false);
        Quad("LockRim", white, 0f, ly, 0.1f, LockW + 0.16f, LockH + 0.16f, FxMath.Rgba(1f, 0.85f, 0.5f, 0.55f), layer, lockTf);
        Quad("LockFrame", white, 0f, ly, 0f, LockW + 0.08f, LockH + 0.08f, FxMath.Rgba(0.05f, 0.05f, 0.08f, 0.95f), layer, lockTf);
        _lockMine = Quad("LockMine", white, 0f, ly, -0.1f, LockW * 0.5f, LockH, FxMath.Rgba(0.35f, 0.75f, 1f, 1f), layer, lockTf);
        _lockTheirs = Quad("LockTheirs", white, 0f, ly, -0.1f, LockW * 0.5f, LockH, FxMath.Rgba(1f, 0.3f, 0.35f, 1f), layer, lockTf);
        _lockGlow = Quad("LockGlow", Glow(), 0f, ly, -0.2f, 0.62f, 0.62f, FxMath.Rgba(1f, 0.85f, 0.45f, 0.9f), layer, lockTf);
        _lockCore = Quad("LockCore", Glow(), 0f, ly, -0.3f, 0.2f, 0.2f, FxMath.Rgba(1f, 1f, 1f, 1f), layer, lockTf);
        for (int i = 0; i < _lockSeg.Length; i++)
            _lockSeg[i] = Quad("LockSeg", white, (i - 1) * 0.62f, ly + 0.34f, 0f, 0.52f, 0.09f, FxMath.Rgba(1f, 0.9f, 0.55f, 1f), layer, lockTf);
        _lockText = Text(src, "LockText", 0f, ly + 0.8f, 3.2f, TextAlignmentOptions.Center, layer, 10f);
        _lockText.transform.SetParent(lockTf, true);
        _lockText.fontStyle = FontStyles.Bold | FontStyles.Italic;
        Outline(_lockText, 0.28f, FxMath.Rgba(0.1f, 0.05f, 0.05f, 1f));
        _lockY = ly;
        SetLockOn(false);

        // 左寄せの字は箱の左端から始まるので、箱の幅の半分だけ内へ
        _comboTmp = Text(src, "Combo", -_halfW + 0.35f + 2f, 0.9f, 3.2f, TextAlignmentOptions.Left, layer, 4f);
        _comboTmp.fontStyle = FontStyles.Bold | FontStyles.Italic;
        _comboTmp.enableVertexGradient = true;
        _comboTmp.colorGradient = new VertexGradient(FxMath.Rgba(1f, 0.85f, 0.3f, 1f), FxMath.Rgba(1f, 0.85f, 0.3f, 1f),
            FxMath.Rgba(1f, 0.38f, 0.1f, 1f), FxMath.Rgba(1f, 0.38f, 0.1f, 1f));
        Outline(_comboTmp, 0.25f, FxMath.Rgba(0.35f, 0.04f, 0.04f, 1f));
        _comboTf = _comboTmp.transform;
        _comboTf.gameObject.SetActive(false);
        _comboOn = false;
        _comboShown = -1;
        _root.SetActive(false);
        return true;
    }

    private static SpriteRenderer Quad(string name, Sprite sprite, float x, float y, float z, float w, float h, Color c, int layer, Transform parent = null)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(parent ? parent : _rootTf, false);
        go.transform.localPosition = FxMath.V3(x, y, z);
        go.transform.localScale = FxMath.V3(w, h, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = c;
        return sr;
    }

    private static TextMeshPro Text(TextMeshPro src, string name, float x, float y, float size, TextAlignmentOptions align, int layer, float width)
    {
        var go = Object.Instantiate(src.gameObject, _rootTf);
        go.name = name;
        go.layer = layer;
        for (int i = go.transform.childCount - 1; i >= 0; i--) Object.Destroy(go.transform.GetChild(i).gameObject);
        foreach (var c in go.GetComponents<Component>())
            if (!c.TryCast<Transform>() && !c.TryCast<TextMeshPro>() && !c.TryCast<Renderer>() && !c.TryCast<MeshFilter>()) Object.Destroy(c);
        go.transform.localPosition = FxMath.V3(x, y, -1f);
        go.transform.localScale = FxMath.V3(1f, 1f, 1f);
        var tmp = go.GetComponent<TextMeshPro>();
        tmp.rectTransform.sizeDelta = FxMath.V2(width, 1.2f);
        tmp.enableAutoSizing = false;
        tmp.enableWordWrapping = false;
        tmp.margin = default;
        tmp.fontSize = size;
        tmp.alignment = align;
        tmp.color = FxMath.Rgba(1f, 1f, 1f, 1f);
        tmp.text = "";
        return tmp;
    }

    // 縁取りは字ごとの材質の複製になる (片付けの時に消す)
    private static void Outline(TextMeshPro tmp, float width, Color c)
    {
        var m = tmp.fontMaterial;
        tmp.outlineWidth = width;
        tmp.outlineColor = c;
        var list = new Material[_mats.Length + 1];
        Array.Copy(_mats, list, _mats.Length);
        list[^1] = m;
        _mats = list;
    }

    private static Sprite _white, _circle;

    private static Sprite WhiteSprite()
    {
        if (_white) return _white;
        var tex = Texture2D.whiteTexture;
        _white = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), FxMath.V2(0.5f, 0.5f), tex.width);
        _white.hideFlags = HideFlags.HideAndDontSave;
        return _white;
    }

    // 顔の丸 (64 px・縁は滑らか)。1 回だけ作る
    internal static Sprite Circle()
    {
        if (_circle) return _circle;
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        float r = n * 0.5f - 1f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = x + 0.5f - n * 0.5f, dy = y + 0.5f - n * 0.5f;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                // 外側 3 px は黒い縁
                byte a = (byte)(255f * FxMath.Clamp01(r - d + 0.5f));
                bool rim = d > r - 4f;
                px[y * n + x] = rim ? FxMath.Rgba32(20, 20, 28, a) : FxMath.Rgba32(255, 255, 255, a);
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.HideAndDontSave;
        _circle = Sprite.Create(tex, new Rect(0, 0, n, n), FxMath.V2(0.5f, 0.5f), n);
        _circle.hideFlags = HideFlags.HideAndDontSave;
        return _circle;
    }

    // 確かめ用: 当事者でなくても出す (me = 自分の番号・opp = 相手)
    internal static void TestShow(byte me, byte opp) => Show(me, opp);
    internal static void TestHit(bool mine, int hp) => OnHit(mine ? _me : _opp, mine ? _opp : _me, hp);
    internal static void TestKo() { _rightHp = 0f; _koT = Time.time; Banner(BannerKo); Write(); }
    internal static void TestIntro() { if (_shown) { _startT = Time.time; _titleState = -1; IntroBegin(); } }
    internal static void TestPush(float k) => _pushGoal = k;

    internal static string Describe() =>
        $"shown={_shown} me={_me} opp={_opp} hp={_leftHp:0.00}/{_rightHp:0.00} ghost={_leftGhostHp:0.00}/{_rightGhostHp:0.00} combo={_combo} title={_titleState} built={(bool)_root}";
}
