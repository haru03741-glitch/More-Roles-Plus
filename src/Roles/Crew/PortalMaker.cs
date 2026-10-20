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
    private const float FixtureClear = 0.7f; // ベント・端末・扉・はしごからこれだけ離して置く (装置の幅の半分 + 少し)
    private const float Body = 0.2f;     // 着く所に要る体の半径
    private const float ArriveMax = 1.5f; // 行き先がふさがれていたら、ここまで離れた床を探す
    private const int ShipLayer = 9, FurnitureLayer = 12;
    private const int ArrivePhysics = 24; // 着く所を探す 1 回で物理に問い合わせる上限 (1 フレームに固めない)
    private const float Rearm = 1.5f;    // 通った後、足元がどちらのポータルからもこれより離れると次に通れる (縁で行き来し続けない)

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
    private bool _armed = true;    // 一度ポータルの外へ出るまで通らない (着いた側ですぐ戻らない)
    private long _recheck;         // 壁越し・行き先なしで断った後、次に調べ直す時刻 (立ち止まっている間に毎回物理を引かない)
    private static int _passGen;   // この端末の自分が通るたびに増える。別のポータルメーカーのポータルが着いた先に重なっていても続けて飛ばない
    private int _seenGen;

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
            if (_count == 2) PortalLinks.HostSet(PlayerId, false, 0f, 0f, 0f, 0f);
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
        if (NearFixture(at)) return false;
        if (!TryAdd(at.x, at.y)) return false;
        MarkCall.Send(at);
        if (_count == 2)
        {
            // 置いた本人は 2 つ目の上に立っているので、そのまま飛ばない
            _armed = false;
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
        if (_count == 2)
        {
            PortalArt.Link(_art[0], _art[1]);
            PortalLinks.HostSet(PlayerId, true, _x[0], _y[0], _x[1], _y[1]);
        }
        return true;
    }

    private void ClearMarks()
    {
        for (int i = 0; i < 2; i++) { PortalArt.Close(_art[i]); _art[i] = null; }
        if (_count == 2) PortalLinks.HostSet(PlayerId, false, 0f, 0f, 0f, 0f);
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
        if (_seenGen != _passGen) { _seenGen = _passGen; _armed = false; }
        Vector2 me = lp.GetTruePosition();
        if (!_armed) { if (Near(me.x, me.y, Rearm) < 0) _armed = true; return; }
        int near = Near(me.x, me.y, Reach);
        if (near < 0) return;
        if (lp.Data == null || lp.Data.IsDead || !lp.CanMove || lp.inVent || lp.inMovingPlat || lp.onLadder) return;
        long now = Environment.TickCount64;
        if (now < _recheck) return;

        int to = 1 - near;
        // 壁 (後から立った仕掛け壁・閉じた扉も) の向こうからは入れない。行き先が埋まっていれば飛ばない
        if (!CanEnter(me.x, me.y, _x[near], _y[near]) || !FindArrival(_x[to], _y[to], out float tx, out float ty))
        {
            _recheck = now + 200;
            return;
        }
        var root = lp.transform.position;
        var dest = FxMath.V2(tx + (root.x - me.x), ty + (root.y - me.y));
        var nt = lp.NetTransform;
        if (AmongUsClient.Instance.NetworkMode == NetworkModes.FreePlay)
            nt.SnapTo(dest, (ushort)(nt.lastSequenceId + 328)); // 同じ sequence id だと古い位置として捨てられる
        else
            nt.RpcSnapTo(dest);
        _armed = false;
        _seenGen = ++_passGen;
        ShowPass();
        PassCall.Send(PlayerId);
    }

    // ベント・端末・扉・はしごの上には置かない (着いた人がそれに重なり、ベントは出入りのたびに飛ばされる)
    private static bool NearFixture(Vector2 at)
    {
        if (SolidMap.Valid && SolidMap.Solid(at.x, at.y)) return true;
        foreach (var col in Physics2D.OverlapCircleAll(at, FixtureClear, ~0))
        {
            if (!col) continue;
            if (col.GetComponentInParent<OpenableDoor>() || col.GetComponentInParent<Vent>() || col.GetComponentInParent<global::Console>() || col.GetComponentInParent<Ladder>()) return true;
        }
        return false;
    }

    // 足元からポータルまでの間に、歩けない所 (壁・仕掛け壁) も船の層の当たり判定 (閉じた扉) も無いか
    private static bool CanEnter(float fx, float fy, float px, float py)
    {
        if (!LineOpen(fx, fy, px, py, false)) return false;
        float dx = px - fx, dy = py - fy, len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.05f) return true;
        // 線の判定は短い区間だと壁の継ぎ目を素通りするので、細い円を滑らせる
        foreach (var hit in Physics2D.CircleCastAll(FxMath.V2(fx, fy), 0.1f, FxMath.V2(dx / len, dy / len), len, 1 << ShipLayer))
            if (hit.collider && !hit.collider.isTrigger) return false;
        return true;
    }

    // 行き先のポータルの近くで、体が収まり、ポータルから壁を越えずに届く所 (ポータルに近い順)。
    // 置いた後に仕掛け壁・瓦礫・扉などで埋まっていたら、同じ側の空いた床へずらす
    private static bool FindArrival(float mx, float my, out float ax, out float ay)
    {
        ax = mx; ay = my;
        if (!SolidMap.Valid) return true;
        _physicsLeft = ArrivePhysics;
        if (Arrivable(mx, my, mx, my)) return true;
        for (int ring = 1; ring * ArriveStep <= ArriveMax; ring++)
        {
            float r = ring * ArriveStep;
            for (int k = 0; k < ArriveDirs; k++)
            {
                float cx = mx + r * DirX[k], cy = my + r * DirY[k];
                if (!Arrivable(mx, my, cx, cy)) continue;
                ax = cx; ay = cy;
                return true;
            }
            if (_physicsLeft <= 0) return false;
        }
        return false;
    }

    private static int _physicsLeft;
    private const float ArriveStep = 0.125f;
    private const int ArriveDirs = 16;
    private static readonly float[] DirX = MakeDirs(true), DirY = MakeDirs(false);

    private static float[] MakeDirs(bool cos)
    {
        var a = new float[ArriveDirs];
        for (int k = 0; k < ArriveDirs; k++) a[k] = cos ? MathF.Cos(k * MathF.PI * 2f / ArriveDirs) : MathF.Sin(k * MathF.PI * 2f / ArriveDirs);
        return a;
    }

    private static bool Arrivable(float mx, float my, float cx, float cy)
    {
        if (SolidMap.Solid(cx, cy) || SolidMap.SealedAt(cx, cy)) return false;
        if (SolidMap.Solid(cx - Body, cy) || SolidMap.Solid(cx + Body, cy) || SolidMap.Solid(cx, cy - Body) || SolidMap.Solid(cx, cy + Body)) return false;
        // ポータルが壁に埋まっていても、その壁を抜けた先の床なら着いてよい (出た後にまた壁を越えるのはだめ)
        if (!LineOpen(mx, my, cx, cy, true)) return false;
        // 地図に載らない物 (大きな瓦礫・閉じた扉・家具) にめり込まないか・ポータルからそこまでの間に閉じた扉が無いか
        if (_physicsLeft-- <= 0) return false;
        foreach (var col in Physics2D.OverlapCircleAll(FxMath.V2(cx, cy), Body, (1 << ShipLayer) | (1 << FurnitureLayer)))
            if (col && !col.isTrigger) return false;
        float dx = cx - mx, dy = cy - my, len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.05f) return true;
        foreach (var hit in Physics2D.CircleCastAll(FxMath.V2(mx, my), 0.1f, FxMath.V2(dx / len, dy / len), len, 1 << ShipLayer))
            if (hit.collider && !hit.collider.isTrigger && !hit.collider.OverlapPoint(FxMath.V2(mx, my))) return false;
        return true;
    }

    // a から b まで歩ける所が続くか (1/32 刻み)。skipLead = 始めの歩けない所は数えない (壁に埋まったポータルから出る時)
    private static bool LineOpen(float ax, float ay, float bx, float by, bool skipLead)
    {
        if (!SolidMap.Valid) return true;
        float dx = bx - ax, dy = by - ay;
        int n = (int)(MathF.Sqrt(dx * dx + dy * dy) * 32f) + 1;
        bool lead = skipLead;
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            if (SolidMap.Solid(ax + dx * t, ay + dy * t))
            {
                if (!lead) return false;
            }
            else lead = false;
        }
        return !lead;
    }

    // 爆発が装置に掛かると壊れて 2 つとも閉じる (置き直せる)。地形の結果と同じ順に全員の端末へ届くので、全員が同じ所で壊す
    private void OnTerrainBroken(TerrainBrokenEvent e)
    {
        if (_count == 0 || e.Kind != DamageKind.Explosion || e.Radius <= 0f) return;
        Vector2 c = e.Position;
        float r = e.Radius + PortalArt.MachineW * 0.5f;
        for (int i = 0; i < _count; i++)
        {
            float dx = _x[i] - c.x, dy = _y[i] - PortalArt.MachineDrop - c.y;
            if (dx * dx + dy * dy > r * r) continue;
            Break();
            return;
        }
    }

    private void Break()
    {
        for (int i = 0; i < 2; i++) { PortalArt.Break(_art[i]); _art[i] = null; }
        if (_count == 2) PortalLinks.HostSet(PlayerId, false, 0f, 0f, 0f, 0f);
        _count = 0;
    }

    // 誰かが倒された: 窓に倒れた直後の死体を映さないよう、絵の側で死体を拾い直す
    private void OnMurdered(PlayerMurderedEvent e) => PortalArt.WatchBodies();

    private int Near(float x, float y, float r)
    {
        for (int i = 0; i < 2; i++)
        {
            float dx = x - _x[i], dy = y - _y[i];
            if (dx * dx + dy * dy <= r * r) return i;
        }
        return -1;
    }

    internal string Describe()
    {
        var sb = new System.Text.StringBuilder($"count={_count} armed={_armed}");
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
