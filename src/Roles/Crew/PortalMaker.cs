// Based on https://github.com/TheOtherRolesAU/TheOtherRoles@b782da4 TheOtherRoles/Objects/Portal.cs (ポータルメーカーの役職・GPL-3.0)
// Based on https://github.com/Gurge44/EndlessHostRoles@ce23f0d Roles/Standard/Crewmate/Miscellaneous/PortalMaker.cs (2 つ置くと近づいた人が行き来する動き・GPL-3.0)
using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// クルー: ボタンでポータルを 2 か所に置くと、生きている人なら誰でも装置の上で使用ボタンを押してその間を行き来できる。
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
    private const float UseReach = 0.85f; // 足元が装置の真ん中からこの距離に入ると使用ボタンで通れる (装置の幅の半分 + 少し)
    private const float FixtureClear = 0.7f; // ベント・端末・扉・はしごからこれだけ離して置く (装置の幅の半分 + 少し)
    private const float Body = 0.2f;     // 着く所に要る体の半径
    private const float ArriveMax = 1.5f; // 行き先がふさがれていたら、ここまで離れた床を探す
    private const int ShipLayer = 9, FurnitureLayer = 12;
    private const int ArrivePhysics = 24; // 着く所を探す 1 回で物理に問い合わせる上限 (1 フレームに固めない)
    private const long PassGapMs = 400;  // 通った直後に押しが重なっても (クリックとキーが同じ時など) 2 回飛ばない

    // 置いた (持ち主 → 全員)。受け手は送り主がポータルメーカーか・数と間隔の決まりを自分で確かめる
    private static readonly RemoteCall<Vector2> MarkCall = new("PortalMaker.Mark", Route.Anyone,
        (w, v) => { w.Write(v.x); w.Write(v.y); }, r => FxMath.V2(r.ReadSingle(), r.ReadSingle()),
        (sender, v) => Of(sender)?.TryAdd(v.x, v.y));

    private static readonly RemoteCall<byte> ClearCall = new("PortalMaker.Clear", Route.Anyone,
        (w, _) => w.Write((byte)0), r => r.ReadByte(), (sender, _) => Of(sender)?.ClearMarks());

    // 誰かが通った (通った人 → 全員)。値 = どのポータルメーカーのポータルか。光と音だけ (位置は本編の同期で届く)
    private static readonly RemoteCall<byte> PassCall = new("PortalMaker.Pass", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(), (_, id) => (RoleState.Of(id) as PortalMaker)?.ShowPass());

    // 装置をハンマーで叩いた (叩いた人 → ホスト)。値 = どのポータルメーカーの装置か。回数はホストだけが数える
    private static readonly RemoteCall<byte> HitCall = new("PortalMaker.Hit", Route.ToHost,
        (w, id) => w.Write(id), r => r.ReadByte(), (sender, id) => (RoleState.Of(id) as PortalMaker)?.HostHit(sender));

    // 叩かれた回数 (ホスト → 全員)。値 = ポータルメーカー | 叩いた人 << 8 | 回数 << 16。3 回目で壊れる
    private static readonly RemoteCall<int> HitsCall = new("PortalMaker.Hits", Route.HostToAll,
        (w, v) => w.Write(v), r => r.ReadInt32(), (_, v) => (RoleState.Of((byte)v) as PortalMaker)?.ShowHit((byte)(v >> 8), v >> 16));

    private const int HammerHits = 3;
    private const float MachineR = 0.5f;       // ハンマーが当たる装置の大きさ (円)
    private const float HammerSlack = 1f;      // ホストが叩いた人の位置を確かめる時の遅れの分
    private static readonly List<PortalMaker> Active = new();
    private int _hits;                         // ホストだけ: 今の組が叩かれた回数

    private readonly float[] _x = new float[2], _y = new float[2];
    private readonly PortalArt.Handle[] _art = new PortalArt.Handle[2];
    private int _count;
    private static bool _useLit;    // この端末の使用ボタンをポータルのために点けているか
    private static bool _lightAfter, _unlightAfter;
    private static float _percentBefore = 1f; // 点ける前のボタンの待ち時間の暗幕 (消す時に戻す)
    private static readonly int PercentId = Shader.PropertyToID("_Percent");
    private static long _passedAt;  // この端末の自分が最後に通った時刻
    private static string _lastUse = "-"; // 最後に押した結果 (確かめ用)
    private static int _relit;            // 使用ボタンを点けた回数 (確かめ用)

    internal int MarkCount => _count;

    public override void OnAssigned()
    {
        Active.Add(this);
        AddAbility(new Ability(new Text("ポータル", "Portal"), "portal")
        {
            Cooldown = () => PlaceCooldown,
            OnUse = Use,
        });
        Lifespan.OnRelease(() =>
        {
            Active.Remove(this);
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
            _hits = 0;
            PortalArt.Link(_art[0], _art[1]);
            PortalLinks.HostSet(PlayerId, true, _x[0], _y[0], _x[1], _y[1]);
        }
        return true;
    }

    private void ClearMarks()
    {
        _hits = 0;
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

    // 本編が使用ボタンの狙いを決める時 (自分の端末の FixedUpdate ごと)。本編の狙いが無く自分が装置の上にいればボタンを点ける。
    // 本編は狙いが無いと毎回ボタンを消すので、点いている間は本編の処理を飛ばす (毎フレーム点け直さない)
    internal static bool BeforeUseTarget(IUsable target)
    {
        _lightAfter = _unlightAfter = false;
        if (Active.Count == 0 && !_useLit) return true;
        bool on = target == null && FindUse(out _, out _);
        if (!on)
        {
            _unlightAfter = _useLit;
            _useLit = false;
            return true;
        }
        if (_useLit) return false;
        _lightAfter = true; // 本編に一度「狙いなし」の絵へ戻させてから点ける
        return true;
    }

    internal static void AfterUseTarget(UseButton b)
    {
        if (_unlightAfter)
        {
            _unlightAfter = false;
            // 本編の狙いに替わった時は本編が自分で暗幕を決めている
            if (b.currentTarget == null && b.graphic) b.graphic.material.SetFloat(PercentId, _percentBefore);
            return;
        }
        if (!_lightAfter) return;
        _lightAfter = false;
        b.SetEnabled();
        // 本編は端末を狙う時に暗幕を端末の待ち時間 (ふつう 0) にする。狙いなしのままだと 1 で、色が戻っても暗く見える
        if (b.graphic)
        {
            var mat = b.graphic.material;
            _percentBefore = mat.GetFloat(PercentId);
            mat.SetFloat(PercentId, 0f);
        }
        _useLit = true;
        _relit++;
    }

    // 使用ボタンが押された (クリックでもキーでも)。装置の上なら反対側へ飛んで本編の処理は止める
    internal static bool HandleUse(UseButton b)
    {
        if (!_useLit || !b || !b.isActiveAndEnabled || b.currentTarget != null) return false;
        if (!FindUse(out var pm, out int near)) return false;
        long now = Environment.TickCount64;
        if (now - _passedAt < PassGapMs) return true;
        pm.Pass(near, now);
        return true;
    }

    // 自分が乗っている装置 (いちばん近い 1 つ)。死んでいる・動けない・ベントやはしごの中なら無し
    private static bool FindUse(out PortalMaker pm, out int near)
    {
        pm = null; near = -1;
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return false;
        Vector2 me = lp.GetTruePosition();
        float best = UseReach * UseReach;
        foreach (var p in Active)
        {
            if (p._count < 2) continue;
            for (int i = 0; i < 2; i++)
            {
                float dx = me.x - p._x[i], dy = me.y - (p._y[i] - PortalArt.MachineDrop), d = dx * dx + dy * dy;
                if (d > best) continue;
                best = d; pm = p; near = i;
            }
        }
        if (pm == null) return false;
        return lp.Data != null && !lp.Data.IsDead && lp.CanMove && !lp.inVent && !lp.inMovingPlat && !lp.onLadder;
    }

    private void Pass(int near, long now)
    {
        var lp = PlayerControl.LocalPlayer;
        Vector2 me = lp.GetTruePosition();
        int to = 1 - near;
        // 壁 (後から立った仕掛け壁・閉じた扉も) の向こうからは入れない。行き先が埋まっていれば飛ばない
        if (!CanEnter(me.x, me.y, _x[near], _y[near])) { Refuse("wall", near); return; }
        if (!FindArrival(_x[to], _y[to], out float tx, out float ty)) { Refuse("blocked", near); return; }
        Warp(lp, tx, ty);
        _passedAt = now;
        _lastUse = $"pass {near}->{to} at=({tx:0.00},{ty:0.00})";
        ShowPass();
        PassCall.Send(PlayerId);
    }

    private void Refuse(string why, int near)
    {
        _lastUse = $"refused {why} from={near}";
        Plugin.Logger.LogInfo($"[PortalMaker] use refused: {why} from p{near}");
    }

    // 自分の足元を (tx, ty) へ移す (自分の位置は自分で動かす)
    private static void Warp(PlayerControl lp, float tx, float ty)
    {
        Vector2 me = lp.GetTruePosition();
        var root = lp.transform.position;
        var dest = FxMath.V2(tx + (root.x - me.x), ty + (root.y - me.y));
        var nt = lp.NetTransform;
        if (AmongUsClient.Instance.NetworkMode == NetworkModes.FreePlay)
            nt.SnapTo(dest, (ushort)(nt.lastSequenceId + 328)); // 同じ sequence id だと古い位置として捨てられる
        else
            nt.RpcSnapTo(dest);
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

    // 足元 at から aim の左右 60° 以内で、maxDist までに届く装置のいちばん近い当たり (face = 当たる点・toward = 打つ面の向き)
    internal static bool HammerTarget(Vector2 at, Vector2 aim, float maxDist, out byte owner, out Vector2 face, out Vector2 toward)
    {
        owner = 0; face = toward = default;
        float best = float.MaxValue;
        foreach (var pm in Active)
        {
            for (int i = 0; i < pm._count; i++)
            {
                float dx = pm._x[i] - at.x, dy = pm._y[i] - PortalArt.MachineDrop - at.y;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 1e-3f) continue;
                float gap = len - MachineR;
                if (gap > maxDist || gap >= best || (dx * aim.x + dy * aim.y) < 0.5f * len) continue;
                // 斜めの壁の向こうの装置は叩けない
                if (!LineOpen(at.x, at.y, at.x + dx / len * MathF.Max(0f, gap), at.y + dy / len * MathF.Max(0f, gap), false)) continue;
                best = gap;
                owner = pm.PlayerId;
                float nx = dx / len, ny = dy / len, edge = MathF.Max(0f, gap);
                face = FxMath.V2(at.x + nx * edge, at.y + ny * edge);
                toward = FxMath.V2(nx, ny);
            }
        }
        return best < float.MaxValue;
    }

    internal static void SendHammer(byte owner) => HitCall.Send(owner);

    // ホスト: 叩いた人が本当に装置のそばにいれば 1 回と数えて全員へ配る
    private void HostHit(PlayerControl sender)
    {
        if (_count == 0 || !sender || sender.Data == null || sender.Data.IsDead || !GameClock.ShipAlive || MeetingHud.Instance) return;
        Vector2 p = sender.GetTruePosition();
        float reach = DamageProfile.Of(DamageKind.Blunt).Reach + MachineR + HammerSlack;
        float d = NearestMachine(p.x, p.y, out int k);
        if (d > reach) return;
        float dx = _x[k] - p.x, dy = _y[k] - PortalArt.MachineDrop - p.y, edge = MathF.Max(0f, d - MachineR) / MathF.Max(1e-3f, d);
        if (!LineOpen(p.x, p.y, p.x + dx * edge, p.y + dy * edge, false)) return;
        // ハンマーを持っている人だけ・ハンマーの待ち時間ごとに 1 回 (続けて送られても数えない)
        if (TerrainPermits.TryUse(sender.PlayerId, TerrainUse.Hammer, 0f, host: true) != null) return;
        _hits++;
        int v = PlayerId | sender.PlayerId << 8 | _hits << 16;
        HitsCall.Send(v);
        ShowHit(sender.PlayerId, _hits);
    }

    // 全員: 叩かれた (他の人の振りはここで見せる)。3 回目で壊れる
    private void ShowHit(byte hitter, int hits)
    {
        if (_count == 0) return;
        var pc = GameData.Instance ? GameData.Instance.GetPlayerById(hitter) : null;
        var obj = pc != null ? pc.Object : null;
        int k = 0;
        if (obj)
        {
            Vector2 p = obj.GetTruePosition();
            float d = NearestMachine(p.x, p.y, out k);
            var lp = PlayerControl.LocalPlayer;
            if (!lp || lp.PlayerId != hitter)
            {
                float dx = _x[k] - p.x, dy = _y[k] - PortalArt.MachineDrop - p.y, len = MathF.Max(1e-3f, MathF.Sqrt(dx * dx + dy * dy));
                float edge = MathF.Max(0f, len - MachineR);
                HammerSwing.PlayOther(FxMath.V2(p.x + dx / len * edge, p.y + dy / len * edge), FxMath.V2(dx / len, dy / len), pc.DefaultOutfit.ColorId);
            }
        }
        if (hits >= HammerHits) { Break(); return; }
        PortalArt.Crack(_art[k]);
    }

    private float NearestMachine(float px, float py, out int which)
    {
        which = 0;
        float best = float.MaxValue;
        for (int i = 0; i < _count; i++)
        {
            float dx = _x[i] - px, dy = _y[i] - PortalArt.MachineDrop - py, d = MathF.Sqrt(dx * dx + dy * dy);
            if (d < best) { best = d; which = i; }
        }
        return best;
    }

    private void Break()
    {
        _hits = 0;
        for (int i = 0; i < 2; i++) { PortalArt.Break(_art[i]); _art[i] = null; }
        if (_count == 2) PortalLinks.HostSet(PlayerId, false, 0f, 0f, 0f, 0f);
        _count = 0;
    }

    // 誰かが倒された: 窓に倒れた直後の死体を映さないよう、絵の側で死体を拾い直す
    private void OnMurdered(PlayerMurderedEvent e) => PortalArt.WatchBodies();

    internal string Describe()
    {
        var sb = new System.Text.StringBuilder($"count={_count} lit={_useLit} relit={_relit} last={_lastUse}");
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

    // テスト用: 自分の足元を装置 i の上 (置いた所) へ移す
    internal bool GoForTest(int i)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || i < 0 || i >= _count) return false;
        Warp(lp, _x[i], _y[i]);
        return true;
    }
}

[HarmonyPatch(typeof(UseButton), nameof(UseButton.SetTarget))]
internal static class PortalUseTargetPatch
{
    public static bool Prefix(IUsable target) => PortalMaker.BeforeUseTarget(target);
    public static void Postfix(UseButton __instance) => PortalMaker.AfterUseTarget(__instance);
}

[HarmonyPatch(typeof(UseButton), nameof(UseButton.DoClick))]
internal static class PortalUseClickPatch
{
    public static bool Prefix(UseButton __instance) => !PortalMaker.HandleUse(__instance);
}
