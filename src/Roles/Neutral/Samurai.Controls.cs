using System;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// サムライの操作と「気」。本編の能力ボタンも待ち時間も使わない。
//   納刀中に押して溜め、なぞる = 居合 (抜きながら斬る・溜めた分だけ強い)
//   抜刀中のなぞり = 斬る・外へ押し出すなぞり = 突く (どちらも気を使う)・右クリック (スマホは鞘のボタン) = 納刀・Shift (スマホは踏み込みのボタン) = 踏み込み (気を使う)
// 気は納めている間に速く、抜いて止まっている間はゆっくり溜まり、歩いている間はさらにゆっくり溜まる。尽きると刀が下がってしばらく何もできない
public sealed partial class Samurai
{
    internal const float KiMax = 100f;
    internal const float StrokeCost = 18f;
    // 押したまま前の振りから ChainGap 秒以内に続けた振りは安い (振り回せるように)。ホストと他人の見積もりは時間だけで同じに数える
    private const float ChainCost = 10f, ChainGap = 0.8f;
    private const float DashCost = 26f;
    private const float RegenSheathed = 34f, RegenStill = 16f, RegenMoving = 12f;
    internal const float SpentTime = 1.6f;
    private const float SpentRefill = 25f;          // 気切れが明けた時に戻る気 (明けてすぐの一振りでまた尽きないよう、一振り分より少し多く)
    private const float KiSlack = 25f;             // ホストの気の見積もりの余裕 (踏み込みはホストへ届かないので、ホストの方が多めに見る)
    internal const float ChargeTime = 1.0f;        // 居合を溜め切るまで (押してから)
    private const float ChargeDelay = 0.12f;       // 押してすぐのなぞりは溜め無し
    internal const float DashSpeed = 14f;
    private const float DashTime = 0.16f;
    // 突いた時に前へ出る (0.5 単位)
    private const float LungeSpeed = 7f, LungeTime = 0.07f;

    // 自分の端末の値
    private float _ki = KiMax, _kiT = -1f;
    private float _spentUntil = -10f;
    private float _moveX, _moveY;
    private bool _moving;
    // ホストと他人の端末の見積もり (振った姿と尽きた知らせから)
    private float _hostKi = KiMax, _hostKiT = -1f;

    // 自分のサムライ (毎フレームの入力はこれだけ見る)
    private static Samurai _local;

    internal float Ki => _ki;
    internal bool Spent => Time.time < _spentUntil;
    // 居合の溜め (0〜1)。納刀中に押している間だけ
    internal float Charge => !Drawn && SwipeInput.Dragging && !SwipeInput.InStroke ? ChargeOf(Time.time - SwipeInput.PressedAt) : 0f;

    private static float ChargeOf(float held) => FxMath.Clamp01((held - ChargeDelay) / ChargeTime);

    // ダミー (練習の相手) はホストが持ち主でも開発用の待ち時間なしを使わない
    private static bool DevFree(PlayerControl p) => p && !p.isDummy && Dev.DevCooldown.Skips(p);

    private void HookControls()
    {
        if (!IsLocal) return;
        _local = this;
        SwipeInput.OnStroke += OnLocalStroke;
        SwipeInput.OnStrokeStart += OnLocalStrokeStart;
        SwipeInput.OnThrust += OnLocalThrust;
        SwipeInput.TouchBlocked = UI.SamuraiPad.Hit;
        _ring = new SamuraiRing(this);
        Lifespan.OnRelease(() =>
        {
            SwipeInput.OnStroke -= OnLocalStroke;
            SwipeInput.OnStrokeStart -= OnLocalStrokeStart;
            SwipeInput.OnThrust -= OnLocalThrust;
            SwipeInput.TouchBlocked = null;
            SwipeInput.NoThrust = false;
            SwipeInput.Arm(null);
            UI.SamuraiPad.Hide();
            EndLock();
            ReleaseLeap();
            _ring?.Dispose();
            if (_local == this) _local = null;
        });
    }

    private SamuraiRing _ring;

    // ---- 気 ----

    private void TickKi(PlayerControl p)
    {
        float now = Time.time;
        float dt = _kiT < 0f ? 0f : FxMath.Min(now - _kiT, 0.5f);
        _kiT = now;
        var at = p.transform.position;
        float mx = at.x - _moveX, my = at.y - _moveY;
        _moveX = at.x;
        _moveY = at.y;
        _moving = mx * mx + my * my > 1e-5f;
        if (_moving)
        {
            float ml = FxMath.Sqrt(mx * mx + my * my);
            _faceX = mx / ml;
            _faceY = my / ml;
        }
        if (DevFree(p)) { _ki = KiMax; return; }
        if (now < _spentUntil) return;
        float rate = !Drawn ? RegenSheathed : _moving ? RegenMoving : RegenStill;
        _ki = FxMath.Min(_ki + rate * dt, KiMax);
    }

    private float _lastStrokeAt = -10f, _othersLastStroke = -10f;
    private int _lastStrokeDrag = -1;

    // 自分の一振りの気: 同じなぞりで前の振りから間もなければ安い (居合と突きは続けても安くしない)
    private float LocalStrokeCost(bool chainable)
    {
        float now = Time.time;
        bool chain = chainable && SwipeInput.DragId == _lastStrokeDrag && now - _lastStrokeAt <= ChainGap;
        _lastStrokeAt = now;
        _lastStrokeDrag = chainable ? SwipeInput.DragId : -1;
        return chain ? ChainCost : StrokeCost;
    }

    // ホストと他人の端末: 届いた一振りの気 (前に届いた振りからの間だけで見る)
    private float SeenStrokeCost(bool chainable)
    {
        float now = Time.time;
        bool chain = chainable && now - _othersLastStroke <= ChainGap;
        _othersLastStroke = now;
        return chain ? ChainCost : StrokeCost;
    }

    // 気を使う。尽きたら刀が下がる (全員に見せる)
    private void UseKi(float cost)
    {
        if (DevFree(Player)) return;
        _ki -= cost;
        if (_ki > 0f) return;
        _ki = 0f;
        _spentUntil = Time.time + SpentTime;
        _blade.SetSpent(true);
        SpentCall.Send(true);
    }

    private void EndSpent()
    {
        _blade.SetSpent(false);
        SpentCall.Send(false);
    }

    private static readonly RemoteCall<bool> SpentCall = new("Samurai.Spent", Route.Anyone,
        (w, v) => w.Write(v), r => r.ReadBoolean(),
        (sender, v) =>
        {
            if (RoleState.Of(sender) is not Samurai s) return;
            s._blade.SetSpent(v);
            s.HostKiNow();
            s._hostKi = v ? 0f : FxMath.Max(s._hostKi, SpentRefill);
        }, perSecond: 8);

    // 立ち合いの HUD に出す気 (本人の端末なら本人の値・他人なら見積もり)
    internal float ShownKi => IsLocal ? _ki : FxMath.Clamp(HostKiNow(), 0f, KiMax);
    internal bool ShownSpent => IsLocal ? Spent : _blade.Spent;

    // ホスト: 時刻から気を見積もる (歩いているかは見ないので、本人の値より多め)
    private float HostKiNow()
    {
        float now = Time.time;
        if (_hostKiT >= 0f) _hostKi = FxMath.Min(_hostKi + (Drawn ? RegenStill : RegenSheathed) * (now - _hostKiT), KiMax);
        _hostKiT = now;
        return _hostKi;
    }

    // ---- 毎フレーム (自分の端末) ----

    // 毎フレームは押した瞬間の読みだけ (動けるかなどは FixedUpdate で決めておく)
    private bool _canAct, _padOn;
    internal string Disarm = "-"; // 確かめ用: 最後に構えを外した理由

    internal static void LocalTick()
    {
        var s = _local;
        if (s == null) return;
        if (s._autoSheatheAt > 0f && Time.time >= s._autoSheatheAt) s.AutoSheathe();
        // 溜めているキーボードの斬りは、動けなくなっても離した時に振り終える (溜めたまま残さない)
        if (s._kbHold && !s._kbTestHold && !OperatingSystem.IsAndroid() && !Input.GetKey(Menu.Keybinds.Key(Menu.Keybinds.Slot.Ability1))) s.KeyRelease();
        if (s._lockWith != 255) { s.LockTick(); return; }
        if (!s._padOn && !s._canAct) return;
        if (s._padOn) UI.SamuraiPad.Poll(s);
        if (!s._canAct || OperatingSystem.IsAndroid()) return;
        if (s.Drawn && Input.GetMouseButtonDown(1) && !SwipeInput.Dragging) s.Sheathe();
        if (Input.GetKeyDown(KeyCode.LeftShift) || Input.GetKeyDown(KeyCode.RightShift)) s.Dash(s.KeyboardMode() ? StickDir(s.Player) : MouseDir(s.Player));
        if (Input.anyKeyDown) s.KeyControls();
    }

    // ---- キーボードだけの操作 (PC) ----
    // 斬る (能力 1 のキー・既定 Z) = 歩いている向き (止まっていれば向いている左右) を横に薙ぐ一振り。続けて押すと左右交互に切り返す。
    // 納刀中は押している間が居合の溜めで、離すと抜きながら斬る。突く (能力 3・既定 C) = 歩いている向きへ突き。納刀 (能力 2・既定 X)。
    // マウスと同じなぞりの道 (SwipeInput) へ一振りを流すので、強さ・気・居合の溜め・必殺技はマウスと同じに決まる
    private const float KeySweep = 75f, KeySwing = 0.15f, KeyChain = 0.9f;
    private bool _kbHold;
    private int _kbSide = 1, _kbPresses = -1;
    private float _kbLast = -9f, _kbA0, _kbFace;

    // 最後に使ったのがキーボードの斬りか (マウスで押すと外れる)。踏み込みの向きを歩いている向きにする
    private bool KeyboardMode()
    {
        if (_kbPresses >= 0 && SwipeInput.Presses != _kbPresses) _kbPresses = -1;
        return _kbPresses >= 0;
    }

    private void KeyControls()
    {
        if (Menu.Keybinds.Down(Menu.Keybinds.Slot.Ability2)) KeyAct(2);
        else if (Menu.Keybinds.Down(Menu.Keybinds.Slot.Ability3)) KeyAct(3);
        else if (Menu.Keybinds.Down(Menu.Keybinds.Slot.Ability1)) KeyAct(1);
    }

    // 1 = 斬る・2 = 納刀・3 = 突く
    private void KeyAct(int key)
    {
        if (SwipeInput.Dragging && !SwipeInput.HeldSynth && !_kbHold) return; // マウスでなぞっている間
        if (key == 2)
        {
            if (Drawn && !SwipeInput.Dragging) Sheathe();
            return;
        }
        var face = StickDir(Player);
        float deg = FxMath.Atan2(face.y, face.x) * (180f / FxMath.PI);
        if (key == 3)
        {
            if (SwipeInput.Dragging) return;
            _kbPresses = SwipeInput.Presses;
            SwipeInput.SynthThrust(deg);
            return;
        }
        if (SwipeInput.Dragging) return;
        _kbPresses = SwipeInput.Presses;
        float now = Time.time;
        _kbSide = now - _kbLast < KeyChain ? -_kbSide : 1;
        _kbLast = now;
        _kbFace = deg;
        _kbA0 = deg + KeySweep * _kbSide;
        if (Drawn) SwipeInput.Synth(_kbA0, deg - KeySweep * _kbSide, KeySwing, 0f, ease: true);
        else
        {
            // 居合: 押している間は溜め
            SwipeInput.HoldSynth(_kbA0);
            _kbHold = true;
        }
    }

    // 確かめ用 (bridge): slash / thrust / sheathe / hold (離すまで溜めたまま) / release
    private bool _kbTestHold;
    internal string KeyForTest(string what)
    {
        _kbTestHold = what == "hold";
        if (what == "release") { if (_kbHold) KeyRelease(); }
        else KeyAct(what == "sheathe" ? 2 : what == "thrust" ? 3 : 1);
        return $"kbHold={_kbHold} heldSynth={SwipeInput.HeldSynth} side={_kbSide} kbMode={KeyboardMode()}";
    }

    private void KeyRelease()
    {
        _kbHold = false;
        _kbTestHold = false;
        _kbLast = Time.time;
        SwipeInput.ReleaseHeld(_kbFace - KeySweep * _kbSide, KeySwing);
    }

    // FixedUpdate ごと (自分の端末)
    private void TickControls()
    {
        var p = Player;
        _canAct = false;
        if (!p || p.Data == null) return;
        bool alive = !p.Data.IsDead;
        bool free = alive && p.CanMove && !MeetingHud.Instance && !p.inVent && !DuelTumble.Busy(PlayerId);
        bool ready = Time.time - _drawnAt >= DrawTime;
        if (_spentUntil > 0f && Time.time >= _spentUntil)
        {
            _spentUntil = -10f;
            _ki = FxMath.Max(_ki, SpentRefill);
            EndSpent();
        }
        // 納めていても抜いていても、なぞりを構える (納刀中のなぞり = 居合)。マウスでは歩かない。鍔迫り合いの間は振れない。
        // 気が尽きている間も構えたまま (押しっぱなしのなぞりを捨てない・振りは役職の側で捨てる) なので、戻ればそのまま振れる
        bool arm = free && ready && !Locked;
        if (!arm) Disarm = !alive ? "dead" : !p.CanMove ? "canmove" : DuelTumble.Busy(PlayerId) ? "tumble" : MeetingHud.Instance ? "meeting" : p.inVent ? "vent" : !ready ? "drawing" : "locked";
        SwipeInput.Arm(arm ? p : null);
        SwipeInput.NoThrust = Spent || _blade.Stunned;
        _ring?.Tick(alive && !MeetingHud.Instance);
        _padOn = UI.SamuraiPad.Tick(this, free && alive && !Locked);
        if (!Drawn && SwipeInput.Dragging) _blade.Charging();
        // 居合で抜いたのに一振りを送らずに離した: 抜いたことだけを全員へ
        if (_iaiStroke && !SwipeInput.Dragging)
        {
            _iaiStroke = false;
            if (Drawn) DrawCall.Send(true);
        }
        // チャット・開発者の窓に打っている間は Shift や右クリックを拾わない
        var hud = Vanilla.Hud;
        bool typing = hud && hud.Chat && hud.Chat.IsOpenOrOpening || Dev.DevConsole.IsOpen;
        _canAct = free && ready && !Spent && !typing && !Locked;
        if (_dashOn && Time.time >= _dashUntil) _dashOn = false;
        TickLeap(p);
    }

    // スマホのボタンから
    internal void PadSheathe()
    {
        if (Drawn && Time.time - _drawnAt >= DrawTime && !Spent && !SwipeInput.Dragging) Sheathe();
    }

    internal void PadDash()
    {
        var p = Player;
        if (!p || Time.time - _drawnAt < DrawTime || Spent) return;
        Dash(StickDir(p));
    }

    private void Sheathe()
    {
        SetDrawn(false);
        DrawCall.Send(false);
    }

    // ---- 居合 ----

    // なぞりが一振りになった瞬間。納めていれば、ここで抜く (見た目は振り付けを飛ばしてそのまま振る)。
    // 短すぎて捨てられた一振りの後に本当の一振りが続くことがあるので、居合の印は送った一振りで使うまで持ち越す
    private bool _iaiStroke;
    private float _iaiCharge;
    private int _iaiTier = 1;

    private int _starts;

    private void OnLocalStrokeStart()
    {
        _starts++;
        NextStrokeSeq();
        if (Drawn || Spent) return;
        _iaiStroke = true;
        _iaiCharge = ChargeOf(Time.time - SwipeInput.PressedAt);
        _iaiTier = IaiTier(_ki, _iaiCharge);
        SetDrawn(true, iai: true);
        _blade.ArmIai(_iaiCharge, _iaiTier);
    }

    // ---- 居合の後の駆け抜けと納刀 ----

    private float _autoSheatheAt = -10f;

    // 段 2 からの居合を振り抜いた (自分の刀の見た目から): 斬った向きへ駆け抜け、hold 秒止めてから自動で刀を納める。
    // 気は使わない (居合の一振りで払っている)
    internal void IaiFollow(float dx, float dy, float dist, float hold)
    {
        if (!IsLocal || !Drawn) return;
        float secs = 0f;
        if (dist > 0.05f)
        {
            secs = dist / DashSpeed;
            _dashSpeed = DashSpeed;
            _dashDir = FxMath.V2(dx, dy);
            _dashUntil = Time.time + secs;
            _dashOn = true;
            _blade.Dash(secs + 0.08f);
            SendDash(dx, dy, secs);
        }
        _autoSheatheAt = Time.time + (secs + hold) * SamuraiBlade.SlowMo;
    }

    // 続けて振り始めた・倒れた・鍔迫り合いの間は納めない
    private void AutoSheathe()
    {
        _autoSheatheAt = -10f;
        if (!Drawn || SwipeInput.Dragging || Locked || Spent) return;
        var p = Player;
        if (!p || p.Data == null || p.Data.IsDead || MeetingHud.Instance) return;
        Sheathe();
    }

    // ---- 踏み込み ----

    private static float _dashUntil = -10f;
    private static bool _dashOn; // 踏み込みの間だけ立つ (本編の FixedUpdate の後置きは、まずこれだけを見る)
    private static Vector2 _dashDir;
    private static float _dashSpeed = DashSpeed;
    internal static float DashSpeedNow => _dashSpeed;

    private void Dash(Vector2 dir)
    {
        if (_leapOn || dir.sqrMagnitude < 1e-4f || TryLeap(dir) || !Drawn) return;
        UseKi(DashCost);
        _dashSpeed = DashSpeed;
        _dashDir = dir.normalized;
        _dashUntil = Time.time + DashTime;
        _dashOn = true;
        _blade.Dash(DashTime + 0.06f);
        SendDash(_dashDir.x, _dashDir.y, DashTime);
    }

    // 値 = 秒 (1/100) | 向き (256 分割) << 8。ホストは練習のボットに踏み込みを知らせる (送った本人の端末では受け手の処理が走らない)
    private void SendDash(float dx, float dy, float secs)
    {
        byte t = (byte)Math.Clamp((int)(secs * 100f), 1, 255);
        byte a = (byte)((int)MathF.Round(FxMath.Atan2(dy, dx) * FxMath.Rad2Deg / 360f * 256f) & 0xFF);
        DashCall.Send((ushort)(t | a << 8));
        if (AmongUsClient.Instance && AmongUsClient.Instance.AmHost) DuelBot.OnDash(PlayerId, a, secs);
    }

    // 突いた向きへ少し前へ (自分の端末だけ・位置は本編の同期で届く)
    private void Lunge(float dx, float dy)
    {
        _dashSpeed = LungeSpeed;
        _dashDir = FxMath.V2(dx, dy);
        _dashUntil = Time.time + LungeTime;
        _dashOn = true;
    }

    private static readonly RemoteCall<ushort> DashCall = new("Samurai.Dash", Route.Anyone,
        (w, v) => w.Write(v), r => r.ReadUInt16(), OnDashCall, perSecond: 6);

    private static void OnDashCall(PlayerControl sender, ushort v)
    {
        if (RoleState.Of(sender) is not Samurai s) return;
        float secs = (v & 0xFF) / 100f;
        s._blade.Dash(secs + 0.06f);
        if (AmongUsClient.Instance && AmongUsClient.Instance.AmHost) DuelBot.OnDash(sender.PlayerId, (byte)(v >> 8), secs);
    }

    // PC: 体からマウスの方向
    private static Vector2 MouseDir(PlayerControl p)
    {
        var cam = Camera.main;
        if (!cam) return default;
        Vector3 m = Input.mousePosition;
        Vector3 w = cam.ScreenToWorldPoint(FxMath.V3(m.x, m.y, 0f));
        Vector3 c = p.transform.position;
        return FxMath.V2(w.x - c.x, w.y - c.y);
    }

    // スマホ: 歩いている向き (止まっていれば向いている左右)
    private static Vector2 StickDir(PlayerControl p)
    {
        var v = p.MyPhysics ? p.MyPhysics.body.velocity : default;
        if (v.sqrMagnitude > 0.01f) return v;
        return FxMath.V2(p.cosmetics && p.cosmetics.FlipX ? -1f : 1f, 0f);
    }

    internal static bool Dashing(out Vector2 dir)
    {
        dir = _dashDir;
        if (!_dashOn) return false;
        if (Time.time < _dashUntil) return true;
        _dashOn = false;
        return false;
    }

    // 確かめ用
    internal void DrawForTest() => SetDrawn(true, iai: true);

    internal bool SheatheForTest()
    {
        if (!Drawn || Time.time - _drawnAt < DrawTime) return false;
        Sheathe();
        return true;
    }

    internal bool DashForTest(float dx, float dy)
    {
        if (!Drawn || Time.time - _drawnAt < DrawTime || Spent) return false;
        Dash(FxMath.V2(dx, dy));
        return true;
    }
}

// 踏み込み・弾かれ・鍔迫り合い: 本編の歩行の後で、短い間だけ体の速さを書き換える (壁は物理が止める)
[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.FixedUpdate))]
internal static class SamuraiDashPatch
{
    public static void Postfix(PlayerPhysics __instance)
    {
        // 吹っ飛ばされている・のけぞっている間 (役職によらない)
        if (__instance.AmOwner && __instance.myPlayer && DuelTumble.Velocity(__instance.myPlayer.PlayerId, out var tumbleVel))
        {
            __instance.body.velocity = tumbleVel;
            return;
        }
        // 跳躍は当たり判定を切って動くので、歩けるか (CanMove) によらず先に書く
        if (__instance.AmOwner && Samurai.Leaping(__instance, out var leapVel))
        {
            __instance.body.velocity = leapVel;
            return;
        }
        Vector2 vel = default;
        bool dash = Samurai.Dashing(out var dir);
        bool pushed = !dash && Samurai.Pushed(out vel);
        if (!dash && !pushed || !__instance.AmOwner) return;
        var pc = __instance.myPlayer;
        if (!pc || !pc.CanMove || pc.inVent) return;
        __instance.body.velocity = dash ? FxMath.V2(dir.x * Samurai.DashSpeedNow, dir.y * Samurai.DashSpeedNow) : vel;
    }
}
