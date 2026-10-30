using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// 必殺技: 道を決めた (守の道を果たした・修羅に堕ちた) 後、1 試合に 1 回だけ、溜め切った段 4 の居合がそのまま必殺技になる。
// 範囲 (半径 UltRadius・壁越しは無し) の生きている人を全員まとめて斬る。範囲に誰もいなければ普通の居合のまま (回数も減らない)。
// 斬る人と斬られる人だけが全画面の演出を見てその場で止まり (KatanaCinematic)、周りの人は普段どおり。
// 画面が閉じたコマで、ホストが本編のキルで全員を倒し、演出の連撃と同じ角度とコマで空間の裂け目を刻む
public sealed partial class Samurai
{
    private const float UltRadius = 4f;
    private const int UltMaxVictims = 64;      // 電文に載せる上限 (200 人を超える部屋でも 1 通に収まる)
    // コマ番号は演出の絵の番号 (止めを差し込む前・30 コマ/秒)。最後のコマより後は終わりから数える
    private const int UltCloseFrame = 106;      // 画面が閉じきったコマ (ここで倒れる)
    private const int UltVictimLineFrame = 111; // 被害者を通る線を刻むコマ

    // 演出の連撃の跡 (コマ番号・角度・線までの距離)。距離は攻撃者を映した画面の中心からの、線の法線 (-sin, cos) の向きの長さ (演出の場の単位)。
    // 事前に描いたコマには描いておらず、この表で試合の床に刻む
    private static readonly (int f, float deg, float off)[] UltFlurry =
    {
        (108, -32f, 4.8f), (109, 40f, -3.6f), (110, 82f, -5.0f), (112, -58f, 5.4f), (113, 2f, 2.8f),
        (114, 43f, 3.2f), (115, 4f, -4.0f), (115, -29f, -5.0f), (116, 82f, 6.5f), (117, -58f, -5.0f),
    };

    private readonly Tracked<bool> _ultUsed = new(false);
    internal bool UltUsed => _ultUsed;
    internal bool UltReady => (_pathDone || _fallen) && !_ultUsed;

    // ホストだけ: 進行中の必殺技 (始めた時刻からの秒で出来事を起こす)
    private float _ultStart = -1f;
    private int _ultNext;
    private Vector2 _ultOrigin;
    private readonly List<byte> _ultVictims = new();
    private readonly List<(float t, int kind, float x, float y, float deg)> _ultEvents = new(); // kind 0 = 倒す・1 = 裂け目
    private int _ultCount, _ultKilled;

    // ホスト: 範囲の人を近い順に選ぶ (斬りと同じく通気口の中・動く床の上は除く)。回避や弾き合いは見ない
    private void HostUltPick(List<byte> into)
    {
        into.Clear();
        var all = GameData.Instance.AllPlayers;
        Vector2 me = Player.GetTruePosition();
        var near = new List<(float d, byte id)>();
        for (int i = 0; i < all.Count; i++)
        {
            var info = all[i];
            if (info == null || info.Disconnected || info.IsDead || info.PlayerId == PlayerId) continue;
            var pc = info.Object;
            if (!pc || pc.inVent || pc.inMovingPlat) continue;
            Vector2 at = pc.GetTruePosition();
            float dx = at.x - me.x, dy = at.y - me.y, sq = dx * dx + dy * dy;
            if (sq > UltRadius * UltRadius) continue;
            if (PhysicsHelpers.AnythingBetween(me, at, Constants.ShipAndObjectsMask, false)) continue;
            near.Add((sq, info.PlayerId));
        }
        near.Sort((a, b) => a.d.CompareTo(b.d));
        for (int i = 0; i < near.Count && i < UltMaxVictims; i++) into.Add(near[i].id);
    }

    // ホスト: 溜め切った段 4 の居合を必殺技にするか決める。始めたら true (普通の当たりは出さない)
    private bool HostTryUlt()
    {
        if (!UltReady || _ultStart >= 0f) return false;
        HostUltPick(_ultVictims);
        if (_ultVictims.Count == 0) return false;
        float close = KatanaCinematic.TimeOfFrame(UltCloseFrame);
        if (close < 0f) { Plugin.Logger.LogWarning("Samurai ult: no cinematic data"); return false; }
        _ultUsed.Value = true;
        _ultStart = Time.time;
        _ultNext = 0;
        _ultCount++;
        _ultOrigin = Player.transform.position;
        BuildUltEvents(close);
        var msg = new UltMsg(PlayerId, _ultVictims.ToArray());
        UltCall.Send(msg);
        OnUlt(msg);
        Plugin.Logger.LogInfo($"Samurai ult {PlayerId}: victims={_ultVictims.Count} close={close:0.00}s events={_ultEvents.Count}");
        return true;
    }

    private void BuildUltEvents(float close)
    {
        _ultEvents.Clear();
        _ultEvents.Add((close, 0, 0f, 0f, 0f));
        foreach (var (f, deg, off) in UltFlurry)
        {
            float t = KatanaCinematic.TimeOfFrame(f);
            if (t < 0f) continue;
            float r = deg * FxMath.Deg2Rad;
            float x = _ultOrigin.x - FxMath.Sin(r) * off * KatanaCinematic.FieldToWorld, y = _ultOrigin.y + FxMath.Cos(r) * off * KatanaCinematic.FieldToWorld;
            _ultEvents.Add((t, 1, x, y, deg));
        }
        // 被害者全員を通る線 (1 人なら水平・2 人以上は並びの向き = 位置の主軸)
        float tv = KatanaCinematic.TimeOfFrame(UltVictimLineFrame);
        if (tv >= 0f && UltVictimLine(out float vx, out float vy, out float vdeg)) _ultEvents.Add((tv, 1, vx, vy, vdeg));
        _ultEvents.Sort((a, b) => a.t.CompareTo(b.t));
    }

    private bool UltVictimLine(out float cx, out float cy, out float deg)
    {
        cx = cy = deg = 0f;
        int n = 0;
        foreach (byte id in _ultVictims)
        {
            var pc = GameData.Instance.GetPlayerById(id)?.Object;
            if (!pc) continue;
            Vector2 at = pc.GetTruePosition();
            cx += at.x;
            cy += at.y;
            n++;
        }
        if (n == 0) return false;
        cx /= n;
        cy /= n;
        float sxx = 0f, syy = 0f, sxy = 0f;
        foreach (byte id in _ultVictims)
        {
            var pc = GameData.Instance.GetPlayerById(id)?.Object;
            if (!pc) continue;
            Vector2 at = pc.GetTruePosition();
            float dx = at.x - cx, dy = at.y - cy;
            sxx += dx * dx;
            syy += dy * dy;
            sxy += dx * dy;
        }
        deg = n < 2 ? 0f : 0.5f * FxMath.Atan2(2f * sxy, sxx - syy) * FxMath.Rad2Deg;
        return true;
    }

    // ホスト: 出来事を時刻どおりに (OnFixedUpdate)。会議・追放・試合の終わり・斬った人が倒れたら打ち切る
    private void HostUltTick()
    {
        if (_ultStart < 0f) return;
        var p = Player;
        if (AmongUsClient.Instance.IsGameOver || !ShipStatus.Instance || MeetingHud.Instance || ExileController.Instance || !p || p.Data == null || p.Data.IsDead)
        {
            Plugin.Logger.LogInfo($"Samurai ult {PlayerId}: cut short at {_ultNext}/{_ultEvents.Count}");
            _ultStart = -1f;
            return;
        }
        float t = Time.time - _ultStart;
        while (_ultNext < _ultEvents.Count && _ultEvents[_ultNext].t <= t)
        {
            var e = _ultEvents[_ultNext++];
            if (e.kind == 0) HostUltKill();
            else SpaceRifts.HostCut(FxMath.V2(e.x, e.y), e.deg, 4, PlayerId);
        }
        if (_ultNext >= _ultEvents.Count) _ultStart = -1f;
    }

    private void HostUltKill()
    {
        bool counted = false;
        foreach (byte id in _ultVictims)
        {
            var target = GameData.Instance.GetPlayerById(id)?.Object;
            if (!target || target.Data == null || target.Data.IsDead || target.Data.Disconnected) continue;
            if (HostApply(target, Duel.MaxHp)) { counted = true; _ultKilled++; }
        }
        if (counted) HostUpdatePath();
    }

    // 必殺技の始まり (全員の端末)。斬る人と斬られる人だけが演出を流す
    private readonly struct UltMsg
    {
        public readonly byte By;
        public readonly byte[] Victims;

        public UltMsg(byte by, byte[] victims)
        {
            By = by;
            Victims = victims;
        }
    }

    private static readonly RemoteCall<UltMsg> UltCall = new("Samurai.Ult", Route.HostToAll,
        (w, m) =>
        {
            w.Write(m.By);
            w.Write((byte)m.Victims.Length);
            foreach (byte v in m.Victims) w.Write(v);
        },
        r =>
        {
            byte by = r.ReadByte();
            var v = new byte[r.ReadByte()];
            for (int i = 0; i < v.Length; i++) v[i] = r.ReadByte();
            return new UltMsg(by, v);
        },
        (_, m) => (RoleState.Of(m.By) as Samurai)?.OnUlt(m));

    private void OnUlt(in UltMsg m)
    {
        if (!AmongUsClient.Instance.AmHost) _ultUsed.Value = true;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || !Player) return;
        bool mine = lp.PlayerId == PlayerId;
        var vic = new List<PlayerControl>(m.Victims.Length);
        foreach (byte id in m.Victims)
        {
            var pc = GameData.Instance ? GameData.Instance.GetPlayerById(id)?.Object : null;
            if (!pc) continue;
            vic.Add(pc);
            if (pc.PlayerId == lp.PlayerId) mine = true;
        }
        if (!mine) return;
        if (!KatanaCinematic.Play(Player, vic.ToArray())) Plugin.Logger.LogWarning($"Samurai ult {PlayerId}: cinematic not played ({KatanaCinematic.Why})");
    }

    // 確かめ用 (ホスト): 道を決めた扱いにする (守の道を果たした = インポスターを斬った数を足す)
    internal bool UnlockForTest()
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return false;
        if (_impostorKills.Value < PathKills) _impostorKills.Value = PathKills;
        HostUpdatePath();
        return true;
    }

    // 確かめ用 (ホスト): もう一度撃てるように戻す
    internal bool ResetUltForTest()
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return false;
        _ultUsed.Value = false;
        _ultStart = -1f;
        return true;
    }

    private string DescribeUlt() => $"ready={UltReady} used={(bool)_ultUsed} count={_ultCount} killed={_ultKilled} running={(_ultStart >= 0f ? $"{_ultNext}/{_ultEvents.Count}" : "-")} victims={_ultVictims.Count}";
}
