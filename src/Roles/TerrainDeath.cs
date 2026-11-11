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
    // 下敷きは塊が当たって崩れ始めてから数える。客の画面は崩れの知らせを待つ分 (BuryWait) だけ遅れて崩れ始めるので、
    // その分も待ってから倒す (崩れきる前に死ぬと半端な姿の死体になる)
    private static float DoomTime(Cause c) => c switch { Cause.Burn => 2.4f, Cause.Drown => 2.8f, _ => 1.0f + BuryWait };
    private const float StrikeLimit = 2.5f; // 下敷き: 塊が当たらないまま (連番を始められない) これだけ過ぎたら倒す
    private const float Linger = 1.5f;
    private const float ToughRepeat = 1.5f;

    private static string Clip(Cause c) => c switch { Cause.Burn => "burn", Cause.Drown => "drown", _ => "crush" };

    // ── 全員の端末で持つ: 死ぬと決まった人 ─────────────────────────────────
    private struct Doom
    {
        public Cause Cause;
        public byte Actor;
        public float At;      // 崩れ始めた時 (下敷きは塊が当たるまで死ぬと決まった時)
        public bool Started;  // 崩れ始めた (下敷きは塊が当たるまで立ったまま)
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
        Doomed[target] = new Doom { Cause = cause, Actor = actor, At = Time.time, Started = cause != Cause.Crush };
        Plugin.Logger.LogInfo($"[TerrainDeath] doom {target} {cause} by={actor}");
        // 本人は動けなくなる (下敷きは塊が当たるまで立ったまま止まる)
        if (p.AmOwner)
        {
            LocalDoom = target;
            if (cause == Cause.Drown) AirGauge.Empty();
        }
        // 死ぬと決まる前へ時間を戻すと、崩れかけの人は助かる (積もった山も消える)
        Rewind.Undo(_ =>
        {
            Doomed.Remove(target);
            if (LocalDoom == target) LocalDoom = 255;
            Unbury(target);
        });
        // 下敷き: 崩れた壁から塊が飛んで体に当たり、当たった時に崩れ始める (TickBury)。
        // 客には壊れ方が束で後から届くので、崩れの知らせが届くのを少し待ってから塊を出す
        if (cause == Cause.Crush) { Burying.Add(new Burial { Pc = p, Id = target, At = Time.time, HitAt = -1f }); return; }
        Collapse(p, cause);
    }

    // 倒れる連番を流す。死に方の見た目は流す前に渡す (下敷きは着せ替えを山の中へ落とす)。流せなければ取り消す
    private static void Collapse(PlayerControl p, Cause cause)
    {
        FallenCorpse.SetFinish(p, cause switch { Cause.Burn => FallenCorpse.Finish.Burnt, Cause.Drown => FallenCorpse.Finish.Drowned, _ => FallenCorpse.Finish.Buried });
        if (CrewFlip.Play(p, Clip(cause), DoomTime(cause) + Linger, fallen: true))
        {
            if (cause == Cause.Drown) DrownFx.Start(p);
            else if (cause == Cause.Burn) BodyChar.Start(p, DoomTime(cause));
        }
        else FallenCorpse.Ended(p.PlayerId);
    }

    private static readonly Net.RemoteCall<byte> GuardCall = new("TerrainDeath.Guard", Net.Route.HostToAll,
        (w, m) => w.Write(m), r => r.ReadByte(), (_, m) => OnGuard(m));

    private static void OnGuard(byte target)
    {
        var p = PortKit.ById(target);
        if (!p || p.Data == null || p.Data.IsDead) return;
        Plugin.Logger.LogInfo($"[TerrainDeath] guarded {target}");
        if (p.AmOwner) AirGauge.Guarded(ToughRepeat);
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
        if (Burying.Count > 0) TickBury();
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
            if (!away && water && WaterSim.CurrentAt(pos, out _, out _) >= DrownDepth && !NoDrown(id)) SwimFor[id] += dt * Impostor.RemoteController.BreathRate(id);
            else if (SwimFor[id] > 0f) SwimFor[id] = FxMath.Max(0f, SwimFor[id] - dt);
            float hardy = HardyMul(id);
            float burnAt = burnLimit * hardy, drownAt = drownLimit * hardy;
            // 体力で受けた頑丈な役職は、居続けると ToughRepeat 秒ごとに次の一撃を受ける (戦いの外の体力は数秒で戻るため)
            if (BurnFor[id] >= burnAt) BurnFor[id] = HostHarm(p, Cause.Burn, Culprit(pos, Cause.Burn)) ? 0f : burnAt - ToughRepeat;
            else if (SwimFor[id] >= drownAt) SwimFor[id] = HostHarm(p, Cause.Drown, Culprit(pos, Cause.Drown)) ? 0f : drownAt - ToughRepeat;
            if (BurnFor[id] > 0f || SwimFor[id] > 0f) exposed = true;
        }
        _exposed = exposed;
    }

    // 深い水でも息が減らない (水面を走る・泳ぎが得意)
    internal static bool NoDrown(byte id) => CrewTraits.Has(id, CrewTraits.Trait.Skim) || CrewTraits.Has(id, CrewTraits.Trait.Fin);

    // 災害に強い人は溺れる・焼けるまでの秒が倍
    internal static float HardyMul(byte id) => CrewTraits.Has(id, CrewTraits.Trait.Hardy) ? 2f : 1f;

    // その人が溺れるまでの秒 (息のゲージと同じ値)
    internal static float DrownLimit(byte id) => DrownTime * HardyMul(id);

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
            if (gone || d.Fired || !host) continue;
            if (!d.Started ? Time.time - d.At < StrikeLimit : Time.time - d.At < DoomTime(d.Cause)) continue;
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
        Burying.Clear();
        Mounds.Clear();
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
        var sb = new System.Text.StringBuilder($"{AirGauge.Describe()} enabled={(bool)Enabled} burn={(float)BurnTime} drown={(float)DrownTime} crush={(bool)CrushOn} doomed={Doomed.Count} sources={Sources.Count}");
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
    internal static string ForceDoom(byte id, Cause cause, byte by = ResolvedDamage.NoActor)
    {
        var p = PortKit.ById(id);
        if (!p || p.Data == null || p.Data.IsDead) return "no such living player";
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return "not host";
        return HostHarm(p, cause, by) ? "doomed" : $"tough hp={Duel.HpOf(id)}";
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
        NoteFall(r);
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
        AirGauge.Reset();
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

    // ── 下敷きの山 (全員の端末で) ──────────────────────────────────────────
    // 直近に抜けた壁 (全員の端末で破壊が当たった順に控える)。下敷きの知らせはその破壊の後に届く
    private static Vector2 _fallAt, _fallDir;
    private static float _fallSize;
    private static bool _fallSided, _fallWhole;
    private static float _fallTime = -10f;
    private const float FallMemory = 3f, FallNear = 3f;

    private static void NoteFall(in ResolvedDamage r)
    {
        bool wall = r.Kind switch
        {
            DamageKind.Blunt or DamageKind.Burn or DamageKind.Gunfire => TerrainDamage.LastCut > 0,
            DamageKind.Crumble => true,
            _ => false,
        };
        if (!wall) return;
        _fallAt = r.Position;
        _fallDir = r.Direction;
        _fallSize = r.Size;
        _fallSided = r.Kind is DamageKind.Blunt or DamageKind.Gunfire && r.Direction.x * r.Direction.x + r.Direction.y * r.Direction.y > 1e-4f;
        _fallWhole = r.Kind == DamageKind.Crumble; // 仕掛け壁が丸ごと崩れた = 大きな板が倒れ込む
        _fallTime = Time.time;
    }

    private struct Burial
    {
        public PlayerControl Pc;
        public byte Id;
        public float At;
        public Vector2 Feet;
        public float Face;
        public bool Seen;
        public float HitAt; // 先頭の塊が体に当たる時 (< 0 = まだ塊を出していない)
    }

    private static readonly List<Burial> Burying = new();
    private static readonly Dictionary<byte, List<GameObject>> Mounds = new();
    private const float BuryWait = 0.6f; // 崩れの知らせを待つ上限

    private static void TickBury()
    {
        float now = Time.time;
        bool meeting = MeetingHud.Instance || ExileController.Instance;
        for (int i = Burying.Count - 1; i >= 0; i--)
        {
            var b = Burying[i];
            if (!b.Pc) { Burying.RemoveAt(i); continue; }
            // 塊が当たる前に会議になった: 助かる (飛んでいた塊も消す)
            if (meeting) { Unbury(b.Id); continue; }
            if (!b.Seen)
            {
                // 止まった時の足元と向き
                b.Seen = true;
                b.Feet = b.Pc.GetTruePosition();
                var body = BodyOf(b.Pc);
                b.Face = body && body.flipX ? -1f : 1f;
                Burying[i] = b;
            }
            if (b.HitAt < 0f)
            {
                bool fell = _fallTime >= b.At - FallMemory && Near(b.Feet);
                if (!fell && now - b.At < BuryWait) continue;
                if (Mounds.Remove(b.Id, out var old)) foreach (var go in old) if (go) UnityEngine.Object.Destroy(go);
                Mounds[b.Id] = BuryUnder(b, fell, out float hit);
                b.HitAt = now + hit;
                Plugin.Logger.LogInfo($"[TerrainDeath] bury {b.Id} fell={fell} waited={now - b.At:0.00} feet=({b.Feet.x:0.00},{b.Feet.y:0.00}) wall=({_fallAt.x:0.00},{_fallAt.y:0.00}) hit={hit:0.00}");
                Burying[i] = b;
                continue;
            }
            if (now < b.HitAt) continue;
            // 先頭の塊が当たった: ここから崩れる (倒すまでの秒もここから数える)
            Burying.RemoveAt(i);
            if (Doomed.TryGetValue(b.Id, out var d) && !d.Started)
            {
                d.Started = true;
                d.At = now;
                Doomed[b.Id] = d;
            }
            if (b.Pc.Data == null || b.Pc.Data.IsDead) continue;
            // 止まっている間に他人の画面で向きが変わっていたら、山を積んだ向きへ戻してから崩す
            var bs = BodyOf(b.Pc);
            if (bs && bs.flipX != b.Face < 0f) bs.flipX = b.Face < 0f;
            Collapse(b.Pc, Cause.Crush);
        }
    }

    private static SpriteRenderer BodyOf(PlayerControl p) =>
        p.cosmetics && p.cosmetics.currentBodySprite != null ? p.cosmetics.currentBodySprite.BodySprite : null;

    private static bool Near(Vector2 feet)
    {
        float dx = feet.x - _fallAt.x, dy = feet.y - _fallAt.y;
        return dx * dx + dy * dy < FallNear * FallNear;
    }

    private static List<GameObject> BuryUnder(in Burial b, bool fell, out float hit)
    {
        Vector2 axis = FxMath.V2(0f, -1f), from = b.Feet;
        bool slab = false;
        if (fell)
        {
            // 叩いた向きの先へ倒れる。向きの無い崩れ方は壁から人への向き
            float fx = _fallAt.x, fy = _fallAt.y;
            if (_fallSided)
            {
                axis = _fallDir;
                // 塊は壁の向こうの面 (叩いた面から壁の厚みの先) から飛び出す
                float len = FxMath.Sqrt(_fallDir.x * _fallDir.x + _fallDir.y * _fallDir.y);
                if (len > 1e-4f) { fx += _fallDir.x / len * _fallSize; fy += _fallDir.y / len * _fallSize; }
            }
            else axis = FxMath.V2(b.Feet.x - fx, b.Feet.y - fy);
            // 叩いた点は手の高さなので、床の点はその体の真ん中の高さだけ下
            from = FxMath.V2(fx, fy - BodyMid);
            slab = _fallWhole;
        }
        return TerrainFx.Bury(b.Feet, from, fell, axis, b.Face, (ushort)(b.Id * 97 + 13), slab, out hit);
    }

    private static void Unbury(byte id)
    {
        for (int i = Burying.Count - 1; i >= 0; i--) if (Burying[i].Id == id) Burying.RemoveAt(i);
        if (!Mounds.Remove(id, out var mound)) return;
        foreach (var go in mound) if (go) UnityEngine.Object.Destroy(go);
    }

    // ── 下敷き (ホスト) ──────────────────────────────────────────────────
    // 下敷きを避けて転んだ (ホスト → 全員)。向きは -127〜127 に縮めて送る
    private static readonly Net.RemoteCall<(byte id, sbyte dx, sbyte dy)> DodgeCall = new("TerrainDeath.Dodge", Net.Route.HostToAll,
        (w, m) => { w.Write(m.id); w.Write((byte)m.dx); w.Write((byte)m.dy); }, r => (r.ReadByte(), (sbyte)r.ReadByte(), (sbyte)r.ReadByte()),
        (_, m) => DuelTumble.Trip(m.id, m.dx / 127f, m.dy / 127f));

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
            // 災害に強い人は瓦礫を避けて転ぶだけ (守りが付いていれば今までどおり弾く)
            if (CrewTraits.Has(p.PlayerId, CrewTraits.Trait.Hardy) && !Impostor.TriggerHappy.Guarded(p))
            {
                float tx = sided ? dx : ox, ty = sided ? dy : oy, tl = FxMath.Sqrt(tx * tx + ty * ty);
                if (tl < 1e-3f) { tx = 1f; ty = 0f; } else { tx /= tl; ty /= tl; }
                var dir = ((sbyte)MathF.Round(tx * 127f), (sbyte)MathF.Round(ty * 127f));
                Plugin.Logger.LogInfo($"[TerrainDeath] dodge {p.PlayerId}");
                DodgeCall.Send((p.PlayerId, dir.Item1, dir.Item2));
                DuelTumble.Trip(p.PlayerId, tx, ty);
                continue;
            }
            HostHarm(p, Cause.Crush, r.Actor);
        }
    }
}
