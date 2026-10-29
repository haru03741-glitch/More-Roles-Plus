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

    // 向き a0 (度) から sweep (度・符号付き) を secs 秒で振る。納めていれば居合 (charge = 0〜1 の溜め)
    internal void BotStroke(float a0, float sweep, float secs, float charge)
    {
        byte iai = Drawn ? (byte)0 : (byte)(1 + (int)MathF.Round(FxMath.Clamp01(charge) * 254f));
        HostStroke(new Stroke((short)Math.Clamp((int)a0, -180, 180), (short)sweep, (ushort)(secs * 1000f), iai));
    }

    // 刀を納める (全員に見せる)。人の納刀と同じく、すぐには抜けない
    internal void BotSheathe()
    {
        if (!Drawn) return;
        HostKiNow();
        SetDrawn(false);
        BotDrawCall.Send((PlayerId, false));
    }

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
