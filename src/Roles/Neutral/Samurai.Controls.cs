using System;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// サムライの操作と「気」。本編の能力ボタンも待ち時間も使わない。
//   納刀中に押して溜め、なぞる = 居合 (抜きながら斬る・溜めた分だけ強い)
//   抜刀中のなぞり = 斬る (気を使う)・右クリック (スマホは鞘のボタン) = 納刀・Shift (スマホは踏み込みのボタン) = 踏み込み (気を使う)
// 気は納めている間に速く、抜いて止まっている間はゆっくり溜まり、歩いている間はさらにゆっくり溜まる。尽きると刀が下がってしばらく何もできない
public sealed partial class Samurai
{
    internal const float KiMax = 100f;
    internal const float StrokeCost = 18f;
    private const float DashCost = 26f;
    private const float RegenSheathed = 34f, RegenStill = 16f, RegenMoving = 8f;
    internal const float SpentTime = 1.6f;
    private const float KiSlack = 25f;             // ホストの気の見積もりの余裕 (踏み込みはホストへ届かないので、ホストの方が多めに見る)
    internal const float ChargeTime = 1.0f;        // 居合を溜め切るまで (押してから)
    private const float ChargeDelay = 0.12f;       // 押してすぐのなぞりは溜め無し
    internal const float DashSpeed = 14f;
    private const float DashTime = 0.16f;

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
        SwipeInput.TouchBlocked = UI.SamuraiPad.Hit;
        _ring = new SamuraiRing(this);
        Lifespan.OnRelease(() =>
        {
            SwipeInput.OnStroke -= OnLocalStroke;
            SwipeInput.OnStrokeStart -= OnLocalStrokeStart;
            SwipeInput.TouchBlocked = null;
            SwipeInput.Arm(null);
            UI.SamuraiPad.Hide();
            EndLock();
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
        if (DevFree(p)) { _ki = KiMax; return; }
        if (now < _spentUntil) return;
        float rate = !Drawn ? RegenSheathed : _moving ? RegenMoving : RegenStill;
        _ki = FxMath.Min(_ki + rate * dt, KiMax);
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
            if (v) { s.HostKiNow(); s._hostKi = 0f; }
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

    internal static void LocalTick()
    {
        var s = _local;
        if (s == null) return;
        if (s._lockWith != 255) { s.LockTick(); return; }
        if (!s._padOn && !s._canAct) return;
        if (s._padOn) UI.SamuraiPad.Poll(s);
        if (!s._canAct || OperatingSystem.IsAndroid()) return;
        if (s.Drawn && Input.GetMouseButtonDown(1) && !SwipeInput.Dragging) s.Sheathe();
        if (Input.GetKeyDown(KeyCode.LeftShift) || Input.GetKeyDown(KeyCode.RightShift)) s.Dash(MouseDir(s.Player));
    }

    // FixedUpdate ごと (自分の端末)
    private void TickControls()
    {
        var p = Player;
        _canAct = false;
        if (!p || p.Data == null) return;
        bool alive = !p.Data.IsDead;
        bool free = alive && p.CanMove && !MeetingHud.Instance && !p.inVent;
        bool ready = Time.time - _drawnAt >= DrawTime;
        if (_spentUntil > 0f && Time.time >= _spentUntil)
        {
            _spentUntil = -10f;
            EndSpent();
        }
        // 納めていても抜いていても、なぞりを構える (納刀中のなぞり = 居合)。マウスでは歩かない。鍔迫り合いの間は振れない
        SwipeInput.Arm(free && ready && !Spent && !Locked ? p : null);
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

    private int _starts;

    private void OnLocalStrokeStart()
    {
        _starts++;
        if (Drawn) return;
        _iaiStroke = true;
        _iaiCharge = ChargeOf(Time.time - SwipeInput.PressedAt);
        SetDrawn(true, iai: true);
        _blade.ArmIai(_iaiCharge);
    }

    // ---- 踏み込み ----

    private static float _dashUntil = -10f;
    private static bool _dashOn; // 踏み込みの間だけ立つ (本編の FixedUpdate の後置きは、まずこれだけを見る)
    private static Vector2 _dashDir;

    private void Dash(Vector2 dir)
    {
        if (!Drawn || dir.sqrMagnitude < 1e-4f) return;
        UseKi(DashCost);
        _dashDir = dir.normalized;
        _dashUntil = Time.time + DashTime;
        _dashOn = true;
        _blade.Dash(DashTime + 0.06f);
        DashCall.Send((byte)Math.Clamp((int)(DashTime * 100f), 1, 255));
    }

    private static readonly RemoteCall<byte> DashCall = new("Samurai.Dash", Route.Anyone,
        (w, v) => w.Write(v), r => r.ReadByte(),
        (sender, v) => (RoleState.Of(sender) as Samurai)?._blade.Dash(v / 100f + 0.06f), perSecond: 6);

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
        Vector2 vel = default;
        bool dash = Samurai.Dashing(out var dir);
        bool pushed = !dash && Samurai.Pushed(out vel);
        if (!dash && !pushed || !__instance.AmOwner) return;
        var pc = __instance.myPlayer;
        if (!pc || !pc.CanMove || pc.inVent) return;
        __instance.body.velocity = dash ? FxMath.V2(dir.x * Samurai.DashSpeed, dir.y * Samurai.DashSpeed) : vel;
    }
}
