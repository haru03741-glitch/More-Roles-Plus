using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// サムライ同士: 刀同士がぶつかると弾き合う。
// ホストはサムライへの斬りをすぐには当てず ClashWindow 秒だけ待ち、その間に相手からも斬り (納めていれば居合) が返ってきたら
// 二人ともに当てず「弾き合い」にする (火花・二人とも後ろへ弾かれて気を少し失う)。
// 弾き合うのは二振りが時間で重なり (後の振りの始めが先の振りの終わりから ClashSlack 以内)、どちらも刀を相手へ向けていた時だけ。
// 一振りは振り終えた時に届くので、届いた時刻 - かかった秒 を振りの始めとみなす。外れたら二人とも普通に当たる
// 同じ二人の弾き合いが続いた時・二人とも居合だった時は「鍔迫り合い」: 刀を押し付け合ったまま動けず、当事者の画面はカメラが寄り、
// LockTime 秒の連打の多かった方が勝つ。負けた方は刀が下がり (気が尽きた姿)、勝った方の一太刀を受ける
public sealed partial class Samurai
{
    private const float ClashWindowBase = 0.35f;  // 先の斬りを当てずに待つ秒
    internal static float TestWindow;              // 確かめ用: 待つ秒を伸ばす (0 = 既定)
    internal static byte TestSpared = 255;         // 確かめ用: この人には斬りを当てない (外から見る人を撮る時)
    private static float ClashWindow => TestWindow > 0f ? TestWindow : ClashWindowBase;
    private const float ClashKi = 10f;
    private const float ClashSlack = 0.1f;    // 振りが重なったとみなす余裕 (届く時刻の揺れ)
    private const float ChainTime = 4f;       // この間に同じ二人が弾き合ったら鍔迫り合い
    internal const float LockTime = 3f;
    private const int LockDamage = 40;
    private const float KnockSpeed = 11f, KnockTime = 0.14f;

    // ---- ホスト ----

    private struct Held
    {
        public byte A, T;
        public int Dmg;
        public float At;
        public bool Iai, Aimed, Guard, Smash; // Guard = 立ち合いの相手のインポスターへの斬り (口で受けられるのを待つ)
    }

    private static readonly List<Held> HostHeld = new();
    private static byte _chainA = 255, _chainB = 255;
    private static float _chainAt = -10f;
    private static int _chain;
    private static float _lastClashTick = -1f;
    // 鍔迫り合い (ホスト): 二人・終わる時刻・連打の数
    private static byte _hlA = 255, _hlB = 255;
    private static float _hlEnd;
    private static int _hlCountA, _hlCountB;
    private static float _hlNextMeter;
    private bool _hostStrokeIai;
    private bool _hostStrokeFull;
    private bool _hostStrokeSmash;   // 溜めた居合 (段 SmashTier 以上) = 当たった人を吹き飛ばす
    private const int SmashTier = 2;
    private float _hostStrokeSecs;
    internal static int ClashMisses; // 待っていた相手から返ったが、重なっていない/向いていないので弾き合いにしなかった数
    internal static int Clashes, Locks;

    // ホスト: サムライ ts への斬り。待っている ts からの斬りとぶつかれば弾き合い、でなければ当てるのを少し待つ
    private bool HostClashOrHold(Samurai ts, int damage, bool aimed)
    {
        float now = Time.time;
        if (_hlA != 255 && (PlayerId == _hlA || PlayerId == _hlB)) return true; // 鍔迫り合いの間の斬りは通らない
        for (int i = 0; i < HostHeld.Count; i++)
        {
            var h = HostHeld[i];
            if (h.A != ts.PlayerId || h.T != PlayerId || now - h.At > ClashWindow) continue;
            if (!h.Aimed || !aimed || now - _hostStrokeSecs > h.At + ClashSlack) { ClashMisses++; break; }
            HostHeld.RemoveAt(i);
            HostClash(h.A, PlayerId, h.Iai && _hostStrokeIai);
            return true;
        }
        HostHeld.Add(new Held { A = PlayerId, T = ts.PlayerId, Dmg = damage, At = now, Iai = _hostStrokeIai, Aimed = aimed, Smash = _hostStrokeSmash });
        DuelBot.OnHeld(ts.PlayerId, PlayerId, ClashWindow);
        return true;
    }

    // ホスト: 待っていた斬りのうち時間が過ぎた物を当てる・鍔迫り合いの連打の写しと終わり (FixedUpdate ごと・何人いても 1 回)
    private static void HostClashTick()
    {
        float now = Time.time;
        if (_lastClashTick == Time.fixedTime) return;
        _lastClashTick = Time.fixedTime;
        // 会議に入ったら待っていた斬りは捨てる (会議中に斬らない)
        if (MeetingHud.Instance) { HostHeld.Clear(); return; }
        for (int i = HostHeld.Count - 1; i >= 0; i--)
        {
            var h = HostHeld[i];
            if (now - h.At <= (h.Guard ? GuardHold : ClashWindow)) continue;
            HostHeld.RemoveAt(i);
            if (RoleState.Of(h.A) is not Samurai s) continue;
            var target = GameData.Instance ? GameData.Instance.GetPlayerById(h.T)?.Object : null;
            if (s.HostApply(target, h.Dmg, h.Smash)) s.HostUpdatePath();
        }
        if (_hlA == 255) return;
        if (now >= _hlNextMeter)
        {
            _hlNextMeter = now + 0.2f;
            ClashCall.Send(Pack(Kind.Meter, (byte)Math.Min(_hlCountA, 255), (byte)Math.Min(_hlCountB, 255)));
            ApplyClash(Pack(Kind.Meter, (byte)Math.Min(_hlCountA, 255), (byte)Math.Min(_hlCountB, 255)));
        }
        // 乱戦で片方が横から倒されたら、残り時間を待たずに解く
        if (now >= _hlEnd || Gone(_hlA) || Gone(_hlB)) HostEndLock();
    }

    private static void HostClash(byte a, byte b, bool bothIai)
    {
        float now = Time.time;
        bool samePair = (_chainA == a && _chainB == b || _chainA == b && _chainB == a) && now - _chainAt < ChainTime;
        _chain = samePair ? _chain + 1 : 1;
        _chainA = a;
        _chainB = b;
        _chainAt = now;
        // 二人を同じ組にする (体力は減らさない)
        Duel.HostJoin(a, b);
        Clashes++;
        if (_chain >= 2 || bothIai)
        {
            _chain = 0;
            _hlA = a;
            _hlB = b;
            _hlEnd = now + LockTime;
            _hlCountA = _hlCountB = 0;
            _hlNextMeter = now + 0.2f;
            Locks++;
            Broadcast(Pack(Kind.LockStart, a, b));
            return;
        }
        Broadcast(Pack(Kind.Clash, a, b));
    }

    private static bool Gone(byte id) => GameData.Instance && GameData.Instance.GetPlayerById(id) is not { IsDead: false, Disconnected: false };

    private static void HostEndLock()
    {
        byte a = _hlA, b = _hlB;
        int ca = _hlCountA, cb = _hlCountB;
        _hlA = _hlB = 255;
        _chain = 0;
        // 連打の多かった方が勝つ (同じなら二人とも弾かれるだけ)
        byte win = ca > cb ? a : cb > ca ? b : (byte)255;
        byte lose = win == a ? b : win == b ? a : (byte)255;
        Broadcast(Pack(Kind.LockEnd, win == 255 ? a : win, lose == 255 ? b : lose, win == 255));
        if (win == 255 || MeetingHud.Instance || RoleState.Of(win) is not Samurai ws) return;
        var target = GameData.Instance ? GameData.Instance.GetPlayerById(lose)?.Object : null;
        if (ws.HostApply(target, LockDamage, smash: true)) ws.HostUpdatePath();
    }

    // 鍔迫り合いの連打の数 (本人 → ホスト・増えた分だけ)
    private static readonly RemoteCall<byte> MashCall = new("Samurai.Mash", Route.ToHost,
        (w, v) => w.Write(v), r => r.ReadByte(),
        (sender, v) =>
        {
            if (!sender || _hlA == 255) return;
            int add = Math.Min((int)v, 8); // 0.2 秒ごとに送る。人の手で出せる分まで
            if (sender.PlayerId == _hlA) _hlCountA += add;
            else if (sender.PlayerId == _hlB) _hlCountB += add;
        }, perSecond: 10);

    // ---- 立ち合いの相手のインポスターのキル ----

    // ホスト: 立ち合っているインポスターの本編のキル (ボット・遅れて届いた押し) は止めて舌の一撃にする (DuelTongue)
    [OnlyMine]
    private void OnKillAttempt(BeforeMurderEvent e)
    {
        var k = e.Killer;
        if (DuelTongue.Finishing || !k || !DuelTongue.Engages(k, PlayerId)) return;
        e.Cancel("samurai duel");
        DuelTongue.HostPress(k);
    }

    // 試合が替わったら持ち越さない (待っていた斬り・鍔迫り合い・寄せたカメラと隠したボタン)
    private static Lifespan _clashMatch;

    private static void ResetClash()
    {
        var match = RoleState.Match;
        if (_clashMatch == match && !match.IsDead) return;
        _clashMatch = match;
        HostHeld.Clear();
        _hlA = _hlB = _lockA = _lockB = _chainA = _chainB = 255;
        _chain = 0;
        _knockUntil = -10f;
        RestoreCam();
    }

    // ---- 全員の端末 ----

    private enum Kind : byte { Clash = 1, LockStart = 2, Meter = 3, LockEnd = 4 }

    // 値 = 種類 | 一人目 << 8 | 二人目 << 16 | 引き分け << 24 (連打の写しは一人目と二人目の数)
    private static int Pack(Kind k, byte a, byte b, bool tie = false) => (byte)k | a << 8 | b << 16 | (tie ? 1 : 0) << 24;

    private static readonly RemoteCall<int> ClashCall = new("Samurai.Clash", Route.HostToAll,
        (w, v) => w.Write(v), r => r.ReadInt32(), (_, v) => ApplyClash(v));

    private static void Broadcast(int v)
    {
        ClashCall.Send(v);
        ApplyClash(v); // 配る電文はホストの手元では動かない
    }

    // 鍔迫り合いの二人 (全員の端末で)
    private static byte _lockA = 255, _lockB = 255;

    private static void ApplyClash(int v)
    {
        var k = (Kind)(v & 0xFF);
        byte a = (byte)(v >> 8), b = (byte)(v >> 16);
        bool tie = (v >> 24 & 1) != 0;
        switch (k)
        {
            case Kind.Clash:
                Spark(a, b, 1f);
                SamuraiSound.Clash(Pc(a), Pc(b));
                (RoleState.Of(a) as Samurai)?.OnClash(b, true);
                (RoleState.Of(b) as Samurai)?.OnClash(a, true);
                break;
            case Kind.LockStart:
                _lockA = a;
                _lockB = b;
                Spark(a, b, 1.2f);
                SamuraiSound.LockOn(Pc(a), Pc(b));
                (RoleState.Of(a) as Samurai)?.BeginLock(b);
                (RoleState.Of(b) as Samurai)?.BeginLock(a);
                break;
            case Kind.Meter:
                // a / b = 一人目 / 二人目の連打の数。押し合っている間は刀の当たる所に小さな火花が出続ける
                UI.DuelHud.LockMeter(_lockA, _lockB, a, b);
                if (_lockA != 255) Spark(_lockA, _lockB, 0.45f, quiet: true);
                break;
            case Kind.LockEnd:
                _lockA = _lockB = 255;
                Spark(a, b, 1.4f);
                SamuraiSound.LockEnd(Pc(a), Pc(b));
                (RoleState.Of(a) as Samurai)?.EndLock(lost: false, other: b, tie: tie);
                (RoleState.Of(b) as Samurai)?.EndLock(lost: !tie, other: a, tie: tie);
                break;
        }
    }

    // 二人の間 (刀がぶつかった所 = 鍔迫り合いで刀が交わる点) に火花。quiet = 当事者の画面の光と揺れを付けない
    private static PlayerControl Pc(byte id) => GameData.Instance ? GameData.Instance.GetPlayerById(id)?.Object : null;

    private static void Spark(byte a, byte b, float size, bool quiet = false)
    {
        var gd = GameData.Instance;
        var pa = gd ? gd.GetPlayerById(a)?.Object : null;
        var pb = gd ? gd.GetPlayerById(b)?.Object : null;
        if (!pa || !pb) return;
        Vector3 x = pa.transform.position, y = pb.transform.position;
        DuelHitFx.Clash(FxMath.V3((x.x + y.x) * 0.5f, (x.y + y.y) * 0.5f + SamuraiBlade.LockLift, FxMath.Min(x.z, y.z) - 0.02f), pa.gameObject.layer, size);
        if (quiet) return;
        byte me = PlayerControl.LocalPlayer ? PlayerControl.LocalPlayer.PlayerId : (byte)255;
        if (me == a || me == b)
        {
            SamuraiScreen.Flash(0.4f, 0.05f);
            var hud = Vanilla.Hud;
            if (hud && hud.PlayerCam) hud.PlayerCam.ShakeScreen(0.15f, 1.2f * size);
        }
    }

    // ---- 自分の端末 (本人の動き) ----

    private static float _knockUntil = -10f;
    private static Vector2 _knockDir;
    private float _lockUntil = -10f;
    private byte _lockWith = 255;
    private PlayerControl _lockOpp;
    private int _mash, _mashSent;
    private float _mashNext;
    private static float _camBase = -1f;

    internal bool Locked => _lockWith != 255;

    // 弾かれた (全員の端末で)。本人なら後ろへ飛ばされて気を失う
    private void OnClash(byte other, bool knock)
    {
        _blade.Recoil(AngleTo(other));
        if (knock) DuelTumble.Recoil(PlayerId, other);
        if (!IsLocal) return;
        if (knock) Knock(other);
        UseKi(ClashKi);
    }

    private float AngleTo(byte other)
    {
        var o = GameData.Instance ? GameData.Instance.GetPlayerById(other)?.Object : null;
        var p = Player;
        if (!o || !p) return 0f;
        Vector3 a = p.transform.position, b = o.transform.position;
        return FxMath.Atan2(b.y - a.y, b.x - a.x) * FxMath.Rad2Deg;
    }

    private void Knock(byte other)
    {
        float ang = (AngleTo(other) + 180f) * FxMath.Deg2Rad;
        _knockDir = FxMath.V2(FxMath.Cos(ang), FxMath.Sin(ang));
        _knockUntil = Time.time + KnockTime;
    }

    private void BeginLock(byte other)
    {
        if (!Drawn) SetDrawn(true, iai: true);
        _lockWith = other;
        _lockOpp = GameData.Instance ? GameData.Instance.GetPlayerById(other)?.Object : null;
        _lockUntil = Time.time + LockTime + 1f;
        _blade.SetLock(true, other);
        if (!IsLocal) return;
        _mash = _mashSent = 0;
        _mashNext = Time.time + 0.2f;
        SwipeInput.Arm(null);
        UI.DuelHud.ShowLock(true);
        ZoomIn();
    }

    // 鍔迫り合いが終わった (会議でも)。負けた本人は刀が下がり、二人とも弾かれる
    private void EndLock(bool lost = false, byte other = 255, bool tie = false)
    {
        if (_lockWith == 255) return;
        SamuraiSound.StopLock();
        byte with = _lockWith;
        _lockWith = 255;
        _lockOpp = null;
        _blade.SetLock(false, 255);
        if (!IsLocal) return;
        UI.DuelHud.EndLock(other == 255 ? -1 : tie ? 0 : lost ? 2 : 1);
        RestoreCam();
        if (other == 255) return;
        Knock(with);
        if (lost && !DevFree(Player))
        {
            _ki = 0f;
            _spentUntil = Time.time + SpentTime;
            _blade.SetSpent(true);
            SpentCall.Send(true);
        }
    }

    // 毎フレーム (自分の端末・鍔迫り合いの間だけ): 連打を数えてホストへ・カメラを寄せる
    private void LockTick()
    {
        float now = Time.time;
        if (now > _lockUntil) { EndLock(); return; }
        bool press;
        if (OperatingSystem.IsAndroid())
        {
            press = false;
            int n = Input.touchCount;
            for (int i = 0; i < n; i++)
                if (Input.GetTouch(i).phase == TouchPhase.Began) press = true;
        }
        else press = Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.Space);
        if (press) { _mash++; UI.DuelHud.MashPressed(); }
        if (now >= _mashNext && _mash > _mashSent)
        {
            _mashNext = now + 0.2f;
            MashCall.Send((byte)Math.Min(_mash - _mashSent, 255));
            _mashSent = _mash;
        }
    }

    // 当事者の画面: 二人へ一気に寄る (カットで切り替える)。寄せるのは世界のカメラだけ。
    // 本編の端に寄せたボタン (右上・右下・チャット) は世界のカメラの大きさで置き直されてずれるので、その間だけ隠す (動けないので要らない)
    private const float LockZoom = 0.62f;
    private static GameObject _hidTop, _hidBottom, _hidChat;

    private static void ZoomIn()
    {
        var cam = Camera.main;
        if (!cam || _camBase >= 0f || Zoom.Zoomed) return;
        _camBase = cam.orthographicSize;
        cam.orthographicSize = _camBase * LockZoom;
        var hud = Vanilla.Hud;
        if (!hud) return;
        _hidTop = Group(hud, hud.SettingsButton ? hud.SettingsButton.transform : null);
        _hidBottom = Group(hud, hud.UseButton ? hud.UseButton.transform : null);
        var chat = hud.Chat ? hud.Chat.chatButton : null;
        _hidChat = chat ? chat.gameObject : null;
        if (_hidTop && !_hidTop.activeSelf) _hidTop = null;
        if (_hidBottom && !_hidBottom.activeSelf) _hidBottom = null;
        if (_hidChat && !_hidChat.activeSelf) _hidChat = null;
        if (_hidTop) _hidTop.SetActive(false);
        if (_hidBottom) _hidBottom.SetActive(false);
        if (_hidChat) _hidChat.SetActive(false);
    }

    // ボタンを束ねた親 (HUD そのものなら隠さない)
    private static GameObject Group(HudManager hud, Transform button)
    {
        var parent = button ? button.parent : null;
        return parent && parent != hud.transform ? parent.gameObject : null;
    }

    private static void RestoreCam()
    {
        if (_camBase < 0f) return;
        var cam = Camera.main;
        if (cam) cam.orthographicSize = _camBase;
        _camBase = -1f;
        if (_hidTop) _hidTop.SetActive(true);
        if (_hidBottom) _hidBottom.SetActive(true);
        if (_hidChat) _hidChat.SetActive(true);
        _hidTop = _hidBottom = _hidChat = null;
    }

    // 本編の FixedUpdate の後置きから: 弾かれている間の速さ・鍔迫り合いの間は相手との間合いを詰めて止まる
    internal static bool Pushed(out Vector2 velocity)
    {
        velocity = default;
        var s = _local;
        if (s != null && s._lockWith != 255)
        {
            var p = s.Player;
            if (p && s._lockOpp) velocity = LockPull(p.transform.position, s._lockOpp.transform.position);
            return true;
        }
        if (Time.time >= _knockUntil) return false;
        velocity = FxMath.V2(_knockDir.x * KnockSpeed, _knockDir.y * KnockSpeed);
        return true;
    }

    // 鍔迫り合いの間合い: 離れていれば相手へ寄り (二人とも寄るので半分ずつ)、近すぎれば少し下がる。刀は間合いに関わらず交わる (SamuraiBlade.LockPose)
    private const float LockGap = 1f;

    internal static Vector2 LockPull(Vector3 me, Vector3 opp)
    {
        float dx = opp.x - me.x, dy = opp.y - me.y, d = FxMath.Sqrt(dx * dx + dy * dy);
        if (d < 0.001f) return default;
        float gap = d - LockGap, sp;
        if (gap > 0.05f) sp = FxMath.Min(gap * 8f, 7f);
        else if (d < 0.7f) sp = -2f;
        else return default;
        return FxMath.V2(dx / d * sp, dy / d * sp);
    }

    // 確かめ用: ホストで二人を弾き合わせる (lockNow = すぐ鍔迫り合い)
    internal static bool ClashForTest(byte a, byte b, bool lockNow)
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost || a == b) return false;
        if (RoleState.Of(a) is not Samurai || RoleState.Of(b) is not Samurai) return false;
        HostClash(a, b, lockNow);
        return true;
    }

    // 確かめ用: 連打を n 回足す (自分の端末)
    internal void MashForTest(int n) => _mash += n;

    internal static string DescribeClash() =>
        $"clashes={Clashes} clashMiss={ClashMisses} locks={Locks} held={HostHeld.Count} chain={_chain} lock={(_lockA == 255 ? "-" : $"{_lockA}vs{_lockB}")} hostLock={(_hlA == 255 ? "-" : $"{_hlA}:{_hlCountA} vs {_hlB}:{_hlCountB}")}";
}
