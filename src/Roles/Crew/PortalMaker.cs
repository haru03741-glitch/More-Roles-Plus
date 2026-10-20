// Based on https://github.com/TheOtherRolesAU/TheOtherRoles@b782da4 TheOtherRoles/Objects/Portal.cs (ポータルメーカーの役職・GPL-3.0)
// Based on https://github.com/Gurge44/EndlessHostRoles@ce23f0d Roles/Standard/Crewmate/Miscellaneous/PortalMaker.cs (2 つ置くと近づいた人が行き来する動き・GPL-3.0)
using System;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// クルー: ボタンでポータルを 2 か所に置くと、生きている人なら誰でもその間を行き来できる。
// 置き終わった後にもう一度押すと 2 つとも消え、置き直せる (設定で切れる)
public sealed class PortalMaker : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#45D9C9";
    public override Text Name => new("ポータルメーカー", "Portal Maker");
    public override string Reading => "ぽーたるめーかー";
    public override Text Blurb => new("2 つのポータルで船の中をつなげ", "Link two places with portals");

    public static readonly FloatOpt PlaceCooldown = new("ポータルを置く待ち時間", "Portal Cooldown", 5f, 1f, 60f, 1f, "s");
    public static readonly BoolOpt CanRemove = new("置いた後にもう一度押すと消せる", "Press Again to Remove Portals", true);

    private const float MinGap = 4f;     // 2 つ目は 1 つ目からこれだけ離して置く
    private const float Reach = 1f;      // 足元がポータルからこの距離に入ると通る
    private const long ReuseMs = 5000;   // 通った人がもう一度通れるまで

    // 置いた (持ち主 → 全員)。受け手は送り主がポータルメーカーか・数と間隔の決まりを自分で確かめる
    private static readonly RemoteCall<Vector2> MarkCall = new("PortalMaker.Mark", Route.Anyone,
        (w, v) => { w.Write(v.x); w.Write(v.y); }, r => FxMath.V2(r.ReadSingle(), r.ReadSingle()),
        (sender, v) => Of(sender)?.TryAdd(v.x, v.y));

    private static readonly RemoteCall<byte> ClearCall = new("PortalMaker.Clear", Route.Anyone,
        (w, _) => w.Write((byte)0), r => r.ReadByte(), (sender, _) => Of(sender)?.ClearMarks());

    // 誰かが通った (通った人 → 全員)。値 = どのポータルメーカーのポータルか。光と音だけ (位置は本編の同期で届く)
    private static readonly RemoteCall<byte> PassCall = new("PortalMaker.Pass", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(), (_, id) => (RoleState.Of(id) as PortalMaker)?.ShowPass());

    private readonly float[] _x = new float[2], _y = new float[2];
    private readonly PortalArt.Handle[] _art = new PortalArt.Handle[2];
    private int _count;
    private static long _nextPass; // この端末の自分が次に通れる時刻 (ポータルメーカーが何人いても 1 人 5 秒に 1 回)
    private bool _armed = true;    // 一度ポータルの外へ出るまで通らない (着いた側ですぐ戻らない)

    internal int MarkCount => _count;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("ポータル", "Portal"), "portal")
        {
            Cooldown = () => PlaceCooldown,
            OnUse = Use,
        });
        Lifespan.OnRelease(() =>
        {
            for (int i = 0; i < 2; i++) { PortalArt.Remove(_art[i]); _art[i] = null; }
            _count = 0;
        });
    }

    private static PortalMaker Of(PlayerControl sender) => sender == null ? null : RoleState.Of(sender.PlayerId) as PortalMaker;

    private bool Use()
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return false;
        if (_count == 2)
        {
            if (!CanRemove) return false;
            ClearMarks();
            ClearCall.Send(0);
            return true;
        }
        // はしご・昇降床・ベントの中は、着いた人がそこから動けなくなるので置かない
        if (lp.inVent || lp.onLadder || lp.inMovingPlat) return false;
        Vector2 at = lp.GetTruePosition();
        if (!TryAdd(at.x, at.y)) return false;
        MarkCall.Send(at);
        if (_count == 2)
        {
            // 置いた本人は 2 つ目の上に立っているので、そのまま飛ばない
            _armed = false;
            _nextPass = Environment.TickCount64 + ReuseMs;
        }
        return true;
    }

    private bool TryAdd(float x, float y)
    {
        if (_count >= 2 || !float.IsFinite(x) || !float.IsFinite(y)) return false;
        if (_count == 1)
        {
            float dx = x - _x[0], dy = y - _y[0];
            if (dx * dx + dy * dy < MinGap * MinGap) return false;
        }
        _x[_count] = x;
        _y[_count] = y;
        _art[_count] = PortalArt.Open(x, y);
        _count++;
        return true;
    }

    private void ClearMarks()
    {
        for (int i = 0; i < 2; i++) { PortalArt.Close(_art[i]); _art[i] = null; }
        _count = 0;
    }

    private void ShowPass()
    {
        if (_count < 2) return;
        PortalArt.Pass(_x[0], _y[0]);
        PortalArt.Pass(_x[1], _y[1]);
    }

    // 全員の端末で、その端末の自分だけを見る (自分の位置は自分で動かす)
    public override void OnFixedUpdate()
    {
        if (_count < 2) return;
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return;
        Vector2 me = lp.GetTruePosition();
        int near = Near(me.x, me.y);
        if (near < 0) { _armed = true; return; }
        if (!_armed || Environment.TickCount64 < _nextPass) return;
        if (lp.Data == null || lp.Data.IsDead || !lp.CanMove || lp.inVent || lp.inMovingPlat || lp.onLadder) return;

        int to = 1 - near;
        float tx = _x[to], ty = _y[to];
        // 置いた後に仕掛け壁などで行き先がふさがれていたら、近くの床へ (無ければ飛ばない)
        if (SolidMap.Valid && SolidMap.Solid(tx, ty))
        {
            if (!SolidMap.NearestFloor(tx, ty, 1.5f, 0.2f, out var floor)) return;
            tx = floor.x; ty = floor.y;
        }
        var root = lp.transform.position;
        var dest = FxMath.V2(tx + (root.x - me.x), ty + (root.y - me.y));
        var nt = lp.NetTransform;
        if (AmongUsClient.Instance.NetworkMode == NetworkModes.FreePlay)
            nt.SnapTo(dest, (ushort)(nt.lastSequenceId + 328)); // 同じ sequence id だと古い位置として捨てられる
        else
            nt.RpcSnapTo(dest);
        _armed = false;
        _nextPass = Environment.TickCount64 + ReuseMs;
        ShowPass();
        PassCall.Send(PlayerId);
    }

    private int Near(float x, float y)
    {
        for (int i = 0; i < 2; i++)
        {
            float dx = x - _x[i], dy = y - _y[i];
            if (dx * dx + dy * dy <= Reach * Reach) return i;
        }
        return -1;
    }

    internal string Describe()
    {
        var sb = new System.Text.StringBuilder($"count={_count} armed={_armed} wait={Math.Max(0L, _nextPass - Environment.TickCount64)}ms");
        for (int i = 0; i < _count; i++) sb.Append($" p{i}=({_x[i]:0.00},{_y[i]:0.00})");
        return sb.ToString();
    }

    // テスト用: 自分の端末で (x, y) に置いた扱いにして全員へ配る
    internal bool PlaceForTest(float x, float y)
    {
        if (!TryAdd(x, y)) return false;
        MarkCall.Send(FxMath.V2(x, y));
        return true;
    }
}
