using System.Collections.Generic;
using System;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// 練習の相手 (ホストが動かすダミーのサムライ・DuelBot) の口。
// 一振りは本物の客から届いた一振りと同じ確かめ (HostStroke) を通すので、当たり・弾き合い・鍔迫り合いは人と同じに起きる。
// 気は本人の端末が無いので、ホストの見積もり (振りで減り、時刻で溜まる) をそのまま気として使う
public sealed partial class Samurai
{
    internal float BotKi => HostKiNow();
    internal bool BotDrawn => Drawn;
    // 納めてから経った秒 (居合の溜めに使える分)
    internal float BotSheathedFor => Drawn ? 0f : Time.time - _sheathedAt;
    // ホストが確かめた (通した) 一振りの数。ボットが相手の振りの勢いを数えるのに使う
    internal int BotSeenStrokes => _hostStrokes;

    // 向き a0 (度) から sweep (度・符号付き) を secs 秒で振る。納めていれば居合 (charge = 0〜1 の溜め)
    internal void BotStroke(float a0, float sweep, float secs, float charge)
    {
        if (BotSpent) return;
        byte iai = Drawn ? (byte)0 : (byte)(1 + (int)MathF.Round(FxMath.Clamp01(charge) * 254f));
        HostStroke(new Stroke((short)Math.Clamp((int)a0, -180, 180), (short)sweep, (ushort)(secs * 1000f), iai, (byte)IaiTier(HostKiNow(), Drawn ? 0f : charge)));
        BotUsedKi();
    }

    // 振り終わりを待っている一振り (ホスト)
    private sealed class BotPending
    {
        public float At, A0, Sweep, Secs;
        public int Dmg;
        public bool Iai, Full, Smash;
        public readonly List<byte> Cuts = new(); // 刀が触れた瞬間に当てた相手 (振り終わりにもう斬らない)
        public readonly Dictionary<byte, float> Tried = new(); // 触れて頼んだが外れた相手と、その時の掃引角 (度・絶対値)
    }

    private readonly List<BotPending> _botPending = new();

    private void HostBotPendingTick()
    {
        var p = Player;
        // 会議・倒れた・試合の外では捨てる (人の振りもその時は断られる)
        if (!p || p.Data == null || p.Data.IsDead || MeetingHud.Instance || !ShipStatus.Instance) { _botPending.Clear(); return; }
        float now = Time.time;
        for (int i = 0; i < _botPending.Count; i++)
        {
            var s = _botPending[i];
            if (now < s.At) continue;
            _botPending.RemoveAt(i--);
            _hostStrokeIai = s.Iai;
            _hostStrokeFull = s.Full;
            _hostStrokeSmash = s.Smash;
            _hostStrokeSecs = s.Secs;
            HostHit(s.A0, s.Sweep, s.Dmg, skip: s.Cuts);
        }
    }

    // 気切れ (人と同じく、尽きたら SpentTime の間は振れず、明けると SpentRefill まで戻る)。読むたびに明けたかを確かめる
    internal bool BotSpent
    {
        get
        {
            if (_spentUntil > 0f && Time.time >= _spentUntil)
            {
                _spentUntil = -10f;
                HostKiNow();
                _hostKi = FxMath.Max(_hostKi, SpentRefill);
                _blade.SetSpent(false);
                BotSpentCall.Send((PlayerId, false));
            }
            return Spent;
        }
    }

    private void BotUsedKi()
    {
        if (HostKiNow() > 0f) return;
        _hostKi = 0f;
        _spentUntil = Time.time + SpentTime;
        _blade.SetSpent(true);
        BotSpentCall.Send((PlayerId, true));
    }

    private static readonly RemoteCall<(byte id, bool spent)> BotSpentCall = new("Samurai.BotSpent", Route.HostToAll,
        (w, v) => { w.Write(v.id); w.Write(v.spent); }, r => (r.ReadByte(), r.ReadBoolean()),
        (_, v) => (RoleState.Of(v.id) as Samurai)?._blade.SetSpent(v.spent));

    // 刀を納める (全員に見せる)。人の納刀と同じく、すぐには抜けない
    internal void BotSheathe()
    {
        if (!Drawn) return;
        HostKiNow();
        SetDrawn(false);
        BotDrawCall.Send((PlayerId, false));
    }

    internal const float BotDashSpeed = DashSpeed, BotDashTime = DashTime;

    // 踏み込む (ホスト・動かすのは呼んだ側)。抜いていて気が足りる時だけ。気は人の踏み込みと同じだけ減らす
    internal bool BotDash()
    {
        if (!Drawn || BotSpent || HostKiNow() < DashCost + StrokeCost) return false;
        _hostKi -= DashCost;
        _blade.Dash(DashTime + 0.06f);
        BotDashCall.Send(PlayerId);
        return true;
    }

    private static readonly RemoteCall<byte> BotDashCall = new("Samurai.BotDash", Route.HostToAll,
        (w, v) => w.Write(v), r => r.ReadByte(),
        (_, id) => (RoleState.Of(id) as Samurai)?._blade.Dash(DashTime + 0.06f));

    private static readonly RemoteCall<(byte id, bool drawn)> BotDrawCall = new("Samurai.BotDraw", Route.HostToAll,
        (w, v) => { w.Write(v.id); w.Write(v.drawn); }, r => (r.ReadByte(), r.ReadBoolean()),
        (_, v) => (RoleState.Of(v.id) as Samurai)?.SetDrawn(v.drawn));

    // 鍔迫り合いの連打を足す (ホスト)。人の連打と同じ数え方
    internal static void BotMash(byte id, int n)
    {
        if (_hlA == 255 || n <= 0) return;
        if (id == _hlA) _hlCountA += n;
        else if (id == _hlB) _hlCountB += n;
    }

    // 鍔迫り合いの最中か (ホスト)
    internal static bool BotLocked(byte id) => _hlA != 255 && (id == _hlA || id == _hlB);

    // 鍔迫り合いの間のボットの速さ (ホスト): 人と同じく相手との間合いを詰める
    internal static Vector2 BotLockPull(byte id, PlayerControl self)
    {
        byte other = id == _hlA ? _hlB : _hlA;
        var o = GameData.Instance ? GameData.Instance.GetPlayerById(other)?.Object : null;
        return o ? LockPull(self.transform.position, o.transform.position) : default;
    }
}
