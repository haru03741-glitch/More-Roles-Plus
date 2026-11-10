using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Options;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 地形で人が死ぬ: 焼死 (燃えている床に居続ける)・溺死 (足の着かない深さで泳ぎ続けて力尽きる)・下敷き (崩れた壁の倒れる側の壁際にいた)。
// 決めるのはホストだけ。ホストは自分の手元の確定の場 (火・水) と、本編の同期で届いた皆の位置を読む。
// 死ぬと決まった人は全員の端末で同じ崩れ方を流し (本人は動けない)、流し終えた所でホストが自決として倒す。
// 死因と「その地形を起こした人」は倒れた記録 (MatchLog) に入り、オートプシーなどが読む。
// 焼死・溺死・下敷きは体で受ける物理の死なので、頑丈な役職 (サムライ) は体力で受ける (0 になる一撃で倒れる)
[Settings(Tab.General, "TerrainDeath.Title", order: -59)]
public static class TerrainDeath
{
    public enum Cause : byte { None = 0, Burn = 1, Drown = 2, Crush = 3 }

    public static readonly BoolOpt Enabled = new("TerrainDeath.Enabled", true) { OffTab = true };
    public static readonly FloatOpt BurnTime = new("TerrainDeath.BurnTime", 3f, 1f, 15f, 0.5f, "s") { OffTab = true, Parent = Enabled };
    public static readonly FloatOpt DrownTime = new("TerrainDeath.DrownTime", 20f, 5f, 60f, 5f, "s") { OffTab = true, Parent = Enabled };
    public static readonly BoolOpt CrushOn = new("TerrainDeath.Crush", true) { OffTab = true, Parent = Enabled };

    private const float DrownDepth = CrewSwim.BraceMaxDepth; // 足が着かない深さ
    private const float PollEvery = 0.2f;                    // ホストが皆の足元を見る間隔
    private const float CrushReach = 0.6f;                   // 抜けた壁の向こうの面からこの距離の内の人が下敷きになる
    private const float CrushSide = 0.05f;                   // 叩いた向きの先 (倒れる側) にいる印 (向きとの内積)
    private const float CrushAlong = 0.8f;                   // 壁に沿った向きにこの距離の内 (崩れた幅)
    private const float BodyMid = 0.4f;                      // 足元から体の真ん中まで
    private const float CulpritReach = 8f;                   // 地形を起こした人を探す距離
    private const int MaxSources = 64;

    // 死ぬと決まってから倒れるまで (連番が崩れ落ちきる所)・倒れた後も崩れた絵で待つ長さ
    private static float DoomTime(Cause c) => c switch { Cause.Burn => 2.4f, Cause.Drown => 2.8f, _ => 0.7f };
    private const float Linger = 1.5f;
    private const float ToughRepeat = 1.5f;

    private static string Clip(Cause c) => c switch { Cause.Burn => "burn", Cause.Drown => "drown", _ => "crush" };

    // ── 全員の端末で持つ: 死ぬと決まった人 ─────────────────────────────────
    private struct Doom
    {
        public Cause Cause;
        public byte Actor;
        public float At;
        public bool Fired; // ホストが倒す依頼を出した (本編に断られても撃ち直さない)
    }

    private static readonly Dictionary<byte, Doom> Doomed = new();
    internal static byte LocalDoom = 255; // 自分が崩れている間 (動けない・PortFreezePatch が読む)

    internal static bool IsDoomed(byte id) => Doomed.ContainsKey(id);

    // 崩れている間に別の死に方で死んだ (崩れの死因は使わない)
    internal static void Forget(byte id)
    {
        if (!Doomed.Remove(id)) return;
        if (LocalDoom == id) LocalDoom = 255;
    }

    // 倒れた記録が死因を受け取る (受け取ったら消す)
    internal static bool TakeDeath(byte id, out Cause cause, out byte actor)
    {
        if (!Doomed.TryGetValue(id, out var d)) { cause = Cause.None; actor = ResolvedDamage.NoActor; return false; }
        Doomed.Remove(id);
        if (LocalDoom == id) LocalDoom = 255;
        cause = d.Cause;
        actor = d.Actor;
        return true;
    }

    private static readonly Net.RemoteCall<(byte target, byte cause, byte actor)> DoomCall = new("TerrainDeath.Doom", Net.Route.HostToAll,
        (w, m) => { w.Write(m.target); w.Write(m.cause); w.Write(m.actor); }, r => (r.ReadByte(), r.ReadByte(), r.ReadByte()),
        (_, m) => OnDoom(m.target, (Cause)m.cause, m.actor));

    private static void OnDoom(byte target, Cause cause, byte actor)
    {
        var p = PortKit.ById(target);
        if (!p || p.Data == null || p.Data.IsDead || MeetingHud.Instance || ExileController.Instance || cause == Cause.None) return;
        Doomed[target] = new Doom { Cause = cause, Actor = actor, At = Time.time };
        Plugin.Logger.LogInfo($"[TerrainDeath] doom {target} {cause} by={actor}");
        if (p.AmOwner) LocalDoom = target;
        // 死ぬと決まる前へ時間を戻すと、崩れかけの人は助かる
        Rewind.Undo(_ => { Doomed.Remove(target); if (LocalDoom == target) LocalDoom = 255; });
        CrewFlip.Play(p, Clip(cause), DoomTime(cause) + Linger, fallen: true);
    }

    private static readonly Net.RemoteCall<byte> GuardCall = new("TerrainDeath.Guard", Net.Route.HostToAll,
        (w, m) => w.Write(m), r => r.ReadByte(), (_, m) => OnGuard(m));

    private static void OnGuard(byte target)
    {
        var p = PortKit.ById(target);
        if (!p || p.Data == null || p.Data.IsDead) return;
        Plugin.Logger.LogInfo($"[TerrainDeath] guarded {target}");
        Impostor.TriggerHappy.PlayGuard(p);
    }

    // ── ホスト: 足元を見て数える ──────────────────────────────────────────
    private static readonly float[] BurnFor = new float[256];
    private static readonly float[] SwimFor = new float[256];
    private static float _poll;
    private static int _shipGen = -1;

    private static float _step = 1f / 30f; // 1 刻みの秒 (船ができた時に測る)
    private static bool _exposed;           // 誰かの数えた秒が 0 より大きい (火と水が消えても戻し続ける)

    internal static void Tick()
    {
        // 逆再生の間は数えも倒しもしない (崩れかけの人は、戻す区間の取り消しで助かる前に倒されない)
        if (RewindPlayback.Active) return;
        if (Doomed.Count > 0)
        {
            if (_shipGen != GameClock.ShipGen) Reset();
            else Finish();
        }
        _poll += _step;
        if (_poll < PollEvery) return;
        float dt = _poll;
        _poll = 0f;
        if (!ShipStatus.Instance) return;
        if (_shipGen != GameClock.ShipGen) Reset();
        if (!Enabled || !AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;
        if (MeetingHud.Instance || ExileController.Instance)
        {
            // 会議の間は数えない。会議の前に浴びた分は持ち越さない
            if (_exposed) { Array.Clear(BurnFor); Array.Clear(SwimFor); _exposed = false; }
            return;
        }
        bool fire = FireSim.Ready && FireSim.BurningCount > 0;
        bool water = WaterSim.Running && WaterSim.Ready;
        if (!fire && !water && !_exposed) return;
        float burnLimit = BurnTime, drownLimit = DrownTime;
        bool exposed = false;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.Data == null || p.Data.IsDead || p.Data.Disconnected) continue;
            byte id = p.PlayerId;
            if (Doomed.ContainsKey(id)) continue;
            // ベント・はしご・動く床の上では浴びない (戻すだけ)
            bool away = p.inVent || p.onLadder || p.inMovingPlat;
            var pos = p.GetTruePosition();
            if (!away && fire && OnFire(pos)) BurnFor[id] += dt;
            else if (BurnFor[id] > 0f) BurnFor[id] = FxMath.Max(0f, BurnFor[id] - dt);
            if (!away && water && WaterSim.CurrentAt(pos, out _, out _) >= DrownDepth) SwimFor[id] += dt;
            else if (SwimFor[id] > 0f) SwimFor[id] = FxMath.Max(0f, SwimFor[id] - dt);
            // 体力で受けた頑丈な役職は、居続けると ToughRepeat 秒ごとに次の一撃を受ける (戦いの外の体力は数秒で戻るため)
            if (BurnFor[id] >= burnLimit) BurnFor[id] = HostHarm(p, Cause.Burn, Culprit(pos, Cause.Burn)) ? 0f : burnLimit - ToughRepeat;
            else if (SwimFor[id] >= drownLimit) SwimFor[id] = HostHarm(p, Cause.Drown, Culprit(pos, Cause.Drown)) ? 0f : drownLimit - ToughRepeat;
            if (BurnFor[id] > 0f || SwimFor[id] > 0f) exposed = true;
        }
        _exposed = exposed;
    }

    private static bool OnFire(Vector2 pos)
    {
        var org = FireSim.Origin;
        float cell = FireSim.Cell;
        int cx = (int)((pos.x - org.x) / cell), cy = (int)((pos.y - org.y) / cell);
        if (cx < 0 || cy < 0 || cx >= FireSim.W || cy >= FireSim.H) return false;
        return FireSim.Burning(cy * FireSim.W + cx);
    }

    // ホスト: 地形の一撃。頑丈な役職は体力で受け、残りがある間は倒れない。倒れると決まったら true
    private static bool HostHarm(PlayerControl p, Cause cause, byte actor)
    {
        byte id = p.PlayerId;
        // 守り (盾・ベスト・守護天使) の付いた人は地形でも死なない。守りは割れず、弾かれた絵だけが出る
        if (Impostor.TriggerHappy.Guarded(p))
        {
            GuardCall.Send(id);
            OnGuard(id);
            return false;
        }
        if (Duel.Tough(p) && Duel.HpOf(id) > PortKit.ToughDamage)
        {
            Duel.HostDamage(actor != ResolvedDamage.NoActor && actor != id ? actor : id, id, PortKit.ToughDamage, start: false);
            return false;
        }
        DoomCall.Send((id, (byte)cause, actor));
        OnDoom(id, cause, actor);
        return true;
    }

    // 崩れ終えた人を倒す (ホスト)。会議になったら崩れかけの人は助かる (本編どおり会議の前の出来事は止まる)
    private static void Finish()
    {
        bool host = AmongUsClient.Instance && AmongUsClient.Instance.AmHost;
        List<byte> drop = null;
        List<PlayerControl> kill = null;
        foreach (var (id, d) in Doomed)
        {
            var p = PortKit.ById(id);
            bool gone = !p || p.Data == null || p.Data.IsDead || p.Data.Disconnected;
            if (gone && Time.time - d.At > DoomTime(d.Cause) + Linger) { (drop ??= new()).Add(id); continue; }
            if (MeetingHud.Instance) { (drop ??= new()).Add(id); continue; }
            if (!gone && d.Fired && Time.time - d.At > DoomTime(d.Cause) + Linger) { (drop ??= new()).Add(id); continue; } // 本編が倒すのを断った
            if (gone || d.Fired || !host || Time.time - d.At < DoomTime(d.Cause)) continue;
            (kill ??= new()).Add(p);
        }
        // 倒すと倒れた記録がこの表から死因を取り出すので、表を回し終えてから倒す
        if (kill != null)
            foreach (var p in kill)
            {
                if (p.Data == null || p.Data.IsDead || !Doomed.TryGetValue(p.PlayerId, out var d)) continue;
                d.Fired = true;
                Doomed[p.PlayerId] = d;
                Plugin.Logger.LogInfo($"[TerrainDeath] kill {p.PlayerId}");
                p.RpcMurderPlayer(p, true);
            }
        if (drop == null) return;
        foreach (var id in drop)
        {
            Doomed.Remove(id);
            if (LocalDoom == id) LocalDoom = 255;
        }
    }

    private static void Reset()
    {
        _shipGen = GameClock.ShipGen;
        Doomed.Clear();
        LocalDoom = 255;
        Array.Clear(BurnFor);
        Array.Clear(SwimFor);
        Sources.Clear();
        _poll = 0f;
        _exposed = false;
        _step = Time.fixedDeltaTime;
    }

    // 確かめ用: 皆の足元と数えた秒
    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder($"enabled={(bool)Enabled} burn={(float)BurnTime} drown={(float)DrownTime} crush={(bool)CrushOn} doomed={Doomed.Count} sources={Sources.Count}");
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.Data == null) continue;
            var pos = p.GetTruePosition();
            float depth = WaterSim.Running && WaterSim.Ready ? WaterSim.CurrentAt(pos, out _, out _) : 0f;
            bool fire = FireSim.Ready && OnFire(pos);
            sb.Append($" | {p.PlayerId} dead={p.Data.IsDead} dc={p.Data.Disconnected} vent={p.inVent} ladder={p.onLadder} plat={p.inMovingPlat} depth={depth:0.00} fire={fire} burnFor={BurnFor[p.PlayerId]:0.0} swimFor={SwimFor[p.PlayerId]:0.0}");
        }
        return sb.ToString();
    }

    // 確かめ用 (ホスト): その人をその死に方で死なせる
    internal static string ForceDoom(byte id, Cause cause)
    {
        var p = PortKit.ById(id);
        if (!p || p.Data == null || p.Data.IsDead) return "no such living player";
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return "not host";
        return HostHarm(p, cause, ResolvedDamage.NoActor) ? "doomed" : $"tough hp={Duel.HpOf(id)}";
    }

    // ── 地形を起こした人 (火を点けた・油をまいた・水を入れた・壁を壊した) ──────────────
    private struct Source
    {
        public Vector2 At;
        public byte Actor;
        public bool Fire;   // 火を点けた・油をまいた・爆発
        public bool Flood;  // 水を入れた
        public bool Break;  // 壁を壊した (水の漏れの元になりうる)
        public int Tick;
    }

    private static readonly List<Source> Sources = new();

    // 破壊の件が当たった後 (全員の端末で同じ順に呼ばれる)
    internal static void OnApplied(in ResolvedDamage r)
    {
        try { Record(r); }
        catch (Exception e) { Plugin.Logger.LogError($"[TerrainDeath] apply: {e}"); }
    }

    private static void Record(in ResolvedDamage r)
    {
        if (r.Kind == DamageKind.Rewind) return;
        if (r.Actor != ResolvedDamage.NoActor)
        {
            bool fire = r.Kind is DamageKind.Ignite or DamageKind.Spill or DamageKind.Explosion;
            bool flood = r.Kind is DamageKind.Water or DamageKind.Flood;
            bool brk = r.Kind.Shapes();
            if (fire || flood || brk)
            {
                if (Sources.Count >= MaxSources) Sources.RemoveAt(0);
                Sources.Add(new Source { At = r.Position, Actor = r.Actor, Fire = fire, Flood = flood, Break = brk, Tick = GameClock.Expand(r.Tick) });
            }
        }
        if (Enabled && CrushOn && AmongUsClient.Instance && AmongUsClient.Instance.AmHost && !MeetingHud.Instance && !ExileController.Instance) Crush(r);
    }

    // 時戻しで取り消した刻みより後の出来事は忘れる
    // 浴びた秒も戻した先から数え直す (戻した先の居場所はもう火や水の外のことが多い)
    internal static void RewindPurge(int target)
    {
        Sources.RemoveAll(s => s.Tick >= target);
        Array.Clear(BurnFor);
        Array.Clear(SwimFor);
        _exposed = false;
    }

    // 火なら点けた人、水なら水を入れた人のうち近い人。水を入れた人がいなければ近くで壁を壊した人 (漏れの元)
    private static byte Culprit(Vector2 pos, Cause cause)
    {
        float best = CulpritReach * CulpritReach, weak = best;
        byte who = ResolvedDamage.NoActor, fallback = ResolvedDamage.NoActor;
        foreach (var s in Sources)
        {
            float dx = s.At.x - pos.x, dy = s.At.y - pos.y, d = dx * dx + dy * dy;
            bool strong = cause == Cause.Burn ? s.Fire : s.Flood;
            bool weakHit = cause == Cause.Drown && s.Break;
            if (strong && d < best) { best = d; who = s.Actor; }
            else if (weakHit && d < weak) { weak = d; fallback = s.Actor; }
        }
        return who != ResolvedDamage.NoActor ? who : fallback;
    }

    // ── 下敷き (ホスト) ──────────────────────────────────────────────────
    private static void Crush(in ResolvedDamage r)
    {
        bool wall = r.Kind switch
        {
            DamageKind.Blunt or DamageKind.Burn or DamageKind.Gunfire => TerrainDamage.LastCut > 0,
            DamageKind.Crumble => true,
            _ => false,
        };
        if (!wall) return;
        Plugin.Logger.LogInfo($"[TerrainDeath] wall fell {r.Kind} at=({r.Position.x:0.00},{r.Position.y:0.00}) dir=({r.Direction.x:0.00},{r.Direction.y:0.00}) size={r.Size:0.00} by={r.Actor}");
        float dx = r.Direction.x, dy = r.Direction.y;
        bool sided = r.Kind is DamageKind.Blunt or DamageKind.Gunfire && dx * dx + dy * dy > 1e-4f;
        if (sided)
        {
            float len = FxMath.Sqrt(dx * dx + dy * dy);
            dx /= len; dy /= len;
        }
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.Data == null || p.Data.IsDead || p.Data.Disconnected || p.inVent || p.PlayerId == r.Actor) continue;
            if (Doomed.ContainsKey(p.PlayerId)) continue;
            // 壁に当たる点は手の高さで決まるので、比べるのは足元でなく体の真ん中
            var pos = p.GetTruePosition();
            float ox = pos.x - r.Position.x, oy = pos.y + BodyMid - r.Position.y;
            if (sided)
            {
                // 叩いた向きの先 (倒れる側): 抜けた穴の中と、向こうの面 (叩いた面から壁の厚み r.Size 先) から CrushReach の内。
                // 壁に沿っては CrushAlong の内
                float ahead = ox * dx + oy * dy, side = ox * dy - oy * dx;
                if (ahead < CrushSide || ahead > r.Size + CrushReach || side > CrushAlong || side < -CrushAlong) continue;
            }
            else if (ox * ox + oy * oy > CrushReach * CrushReach) continue;
            HostHarm(p, Cause.Crush, r.Actor);
        }
    }
}
