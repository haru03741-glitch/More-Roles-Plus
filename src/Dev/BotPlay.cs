using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Options;
using MoreRolesPlus.Roles;
using MoreRolesPlus.Roles.Impostor;
using UnityEngine;

namespace MoreRolesPlus.Dev;

// ロビーのボット (ホストが動かすダミー)。ロビーでホストが出し、役職を決めておくと次の試合でその役職が付く。
// 試合中の動き (全員で同じ):
//   立っているだけ = 何もしない (撃つ・操る側を自分で試す的)
//   狙う          = 役職の能力を狙う人 (既定はホスト自身) に使う。撃たれる側・操られる側を一人で試す
//   自動          = 本物の人のように歩いてタスク・キル・通報・投票をする (DummyBrain)
// 能力は本人がボタンを押した時と同じ確かめ (ホストの判定・電文) を通す。考えるのはホストの端末だけ。
internal static class BotPlay
{
    internal enum Mode : byte { Stand, Attack, Auto }

    internal static Mode Current = Mode.Attack;
    internal static byte TargetId = 255; // 狙う人 (255 = ホスト自身)

    // ロビーで出したボット → 付ける役職 (役職の番号・-1 = おまかせ)
    private static readonly Dictionary<byte, int> Ours = new();
    // 部屋を出た時 (「もう一度プレイ」でロビーが作り直された時も) のボットの役職。次にホストでロビーに着いたら同じ構成で出し直す
    private static readonly List<int> Pending = new();
    private static float _lobbyAt = -1f, _scanAt;

    private sealed class Mind
    {
        public float NextThink, NextUse, NextPath, NextWander, HeldAt = -1f, VoteAt = -1f;
        public bool Voted;
        public string Last = "-"; // 確かめ用: 最後に考えた事
        public float ToX, ToY;     // 今の道の行き先 (相手が離れたら引き直す)
    }
    private static readonly Dictionary<byte, Mind> Minds = new();
    private static readonly List<float> Xs = new(), Ys = new();
    private static readonly System.Random Rng = new();
    private static readonly List<byte> Gone = new();
    private static bool _wasMeeting;

    private const float ThinkGap = 0.2f;
    private const float RetryGap = 1.5f;  // 断られた/届かない時に使い直すまで
    private const float PathGap = 2.5f;   // 歩く道を引き直す間 (詰まりの見張り 1.5 秒より長く)

    internal static bool InLobby => LobbyBehaviour.Instance && !ShipStatus.Instance;

    // 出せる所: 公式サーバーでない部屋のホスト (ロビー)
    internal static bool CanUse
    {
        get
        {
            var c = AmongUsClient.Instance;
            return c && c.AmHost && GameData.Instance && PlayerControl.LocalPlayer && !Net.ServerKind.IsOfficial()
                && c.NetworkMode != NetworkModes.FreePlay;
        }
    }

    // DummyBrain に任せず、ここで動かすボットか
    internal static bool Handles(byte id) => Current != Mode.Auto && Ours.ContainsKey(id) && Dummy.OwnsId(id);

    internal static bool IsOurs(byte id) => Ours.ContainsKey(id);

    // 今いるボット (番号と付ける役職)。抜けた物はここで外す
    internal static List<(PlayerControl pc, RoleBase role)> List()
    {
        var list = new List<(PlayerControl, RoleBase)>();
        Gone.Clear();
        foreach (var (id, idx) in Ours)
        {
            var pc = PortKit.ById(id);
            if (!pc || !pc.isDummy) { Gone.Add(id); continue; }
            list.Add((pc, idx >= 0 && idx < Registry.Roles.Count ? Registry.Roles[idx] : null));
        }
        foreach (var id in Gone) Ours.Remove(id);
        list.Sort((a, b) => a.Item1.PlayerId.CompareTo(b.Item1.PlayerId));
        return list;
    }

    // 1 体出す (role = null でおまかせ)。断った理由を返す
    internal static string Add(RoleBase role)
    {
        if (!CanUse) return new Text("BotPlay.NotHere");
        if (!InLobby) return new Text("BotPlay.LobbyOnly");
        if (GameData.Instance.GetAvailableId() < 0 || GameData.Instance.PlayerCount >= Options.Crowded.MaxPlayers) return new Text("HostActions.BotFull");
        // 名前は出す時の情報に入れる (後から名前の RPC を中の人のいない体へ送らない)
        var pc = Dummy.Spawn(Ours.Count, $"BOT {GameData.Instance.GetAvailableId()}");
        if (!pc) return new Text("HostActions.BotFull");
        Ours[pc.PlayerId] = role != null ? Registry.Roles.IndexOf(role) : -1;
        return null;
    }

    internal static void SetRole(byte id, RoleBase role)
    {
        if (Ours.ContainsKey(id)) Ours[id] = role != null ? Registry.Roles.IndexOf(role) : -1;
    }

    internal static void Remove(byte id)
    {
        if (!InLobby) return;
        Ours.Remove(id);
        Minds.Remove(id);
        var pc = PortKit.ById(id);
        if (pc && pc.isDummy) Dummy.Remove(pc);
    }

    internal static int RemoveAll()
    {
        if (!InLobby) return 0;
        Pending.Clear();
        var ids = new List<byte>(Ours.Keys);
        foreach (var id in ids) Remove(id);
        return ids.Count;
    }

    // 試合の配役の直前 (ホスト): 決めておいた役職を固定指定に入れる (試合ごとに使い切られるので毎回)
    internal static void Reserve()
    {
        foreach (var (id, idx) in Ours)
        {
            var pc = PortKit.ById(id);
            // 部屋を移ると同じ番号が本物の人になることがあるので、ダミーの時だけ
            if (idx < 0 || idx >= Registry.Roles.Count || !pc || !pc.isDummy) continue;
            RoleAssigner.Reserved[id] = idx;
        }
    }

    // ---- 試合中 (ホスト・Plugin の毎フレーム。考えるのはボットごとに 0.2 秒に 1 回) ----

    public static void Tick()
    {
        if (Ours.Count == 0 && Pending.Count == 0) return;
        var client = AmongUsClient.Instance;
        // 部屋を出たら番号は忘れる (次の部屋では同じ番号が本物の人になりうる)。役職だけ控えて出し直す
        if (!client || client.GameState == InnerNet.InnerNetClient.GameStates.NotJoined)
        {
            if (Ours.Count > 0) { Pending.Clear(); Pending.AddRange(Ours.Values); Ours.Clear(); Minds.Clear(); }
            _lobbyAt = -1f;
            return;
        }
        // 「もう一度プレイ」ではロビーが作り直され、ダミーは消えて番号だけが残る → 役職を控えに移して出し直す
        bool lobby = InLobby;
        if (!lobby) _lobbyAt = -1f;
        if (lobby && Ours.Count > 0 && Time.unscaledTime >= _scanAt)
        {
            _scanAt = Time.unscaledTime + 0.5f;
            Gone.Clear();
            foreach (var (id, idx) in Ours)
            {
                var pc = PortKit.ById(id);
                if (pc && pc.isDummy) continue;
                Gone.Add(id);
                Pending.Add(idx);
            }
            foreach (var id in Gone) { Ours.Remove(id); Minds.Remove(id); }
        }
        if (Pending.Count > 0) { Respawn(); return; }
        // 湧く場所と待ち時間は動き方によらず本物の人と同じに
        if (client && client.AmHost && !client.IsGameOver && ShipStatus.Instance) BotRound.Tick(Ours.Keys);
        if (!client || !client.AmHost || client.IsGameOver || !ShipStatus.Instance || Current == Mode.Auto)
        {
            if (Minds.Count > 0) Minds.Clear();
            return;
        }
        float now = Time.time;
        var hud = MeetingHud.Instance;
        bool meeting = hud || ExileController.Instance;
        if (meeting != _wasMeeting)
        {
            _wasMeeting = meeting;
            foreach (var m in Minds.Values) { m.Voted = false; m.VoteAt = -1f; m.HeldAt = -1f; }
            foreach (var id in Ours.Keys) { var pc = PortKit.ById(id); if (pc) Dummy.StopWalk(pc); }
        }
        if (hud) { SkipVotes(hud, now); return; }
        if (meeting || Current != Mode.Attack) return;
        var target = Target();
        foreach (var id in Ours.Keys)
        {
            if (Minds.TryGetValue(id, out var m) && now < m.NextThink) continue;
            var pc = PortKit.ById(id);
            if (!PortKit.Alive(pc) || !pc.isDummy || pc == DummyPilot.Possessed || !BotRound.Ready(id)) continue;
            if (m == null) Minds[id] = m = new Mind();
            m.NextThink = now + ThinkGap;
            try { Think(pc, m, target, now); }
            catch (Exception e) { Plugin.Logger.LogError($"bot {id}: {e}"); m.NextThink = now + 5f; }
        }
    }

    // ロビーに着いて少し待ってから (自分の体と部屋の情報が揃ってから) 控えた構成で出す
    private static void Respawn()
    {
        if (!InLobby || !CanUse) { _lobbyAt = -1f; return; }
        float now = Time.unscaledTime;
        if (_lobbyAt < 0f) { _lobbyAt = now; return; }
        if (now - _lobbyAt < 2f) return;
        var roles = new List<int>(Pending);
        Pending.Clear();
        foreach (int idx in roles)
            if (Add(idx >= 0 && idx < Registry.Roles.Count ? Registry.Roles[idx] : null) != null) break;
    }

    private static PlayerControl Target()
    {
        var t = TargetId != 255 ? PortKit.ById(TargetId) : null;
        return t ? t : PlayerControl.LocalPlayer;
    }

    // 会議では話し合いが終わってから少しして投票を飛ばす (勝手に人を追い出さない)
    private static void SkipVotes(MeetingHud hud, float now)
    {
        var st = hud.state;
        if (st != MeetingHud.MeetingStates.NotVoted && st != MeetingHud.MeetingStates.Voted) return;
        foreach (var id in Ours.Keys)
        {
            var pc = PortKit.ById(id);
            if (!PortKit.Alive(pc) || !pc.isDummy || pc == DummyPilot.Voter) continue;
            if (!Minds.TryGetValue(id, out var m)) Minds[id] = m = new Mind();
            if (m.Voted) continue;
            if (m.VoteAt < 0f) { m.VoteAt = now + 2f + (float)Rng.NextDouble() * 2f; continue; }
            if (now < m.VoteAt) continue;
            m.Voted = true;
            if (!hud.DidVote(pc.PlayerId)) Dummy.Vote(pc, 253);
        }
    }

    private static void Think(PlayerControl pc, Mind m, PlayerControl target, float now)
    {
        var role = RoleState.Of(pc);
        if (!PortKit.Alive(target) || target == pc)
        {
            Halt(pc, role);
            return;
        }
        switch (role)
        {
            case TriggerHappy th: Gunner(pc, m, th, target, now); return;
            case RemoteController rc: Remote(pc, m, rc, target, now); return;
            case RocketLauncher rl: Rocket(pc, m, rl, target, now); return;
            case Nova n: Beam(pc, m, n, target, now); return;
        }
        Generic(pc, m, role, target, now);
    }

    // 狙う相手がいない時: 撃つのをやめて止まる
    private static void Halt(PlayerControl pc, RoleBase role)
    {
        Dummy.StopWalk(pc);
        if (role is TriggerHappy th) th.BotAim = null;
        if (role is RemoteController rc) rc.BotDir = Vector2.zero;
    }

    // トリガーハッピー: 射程の内側まで寄って構え、見えている間は相手へ撃ち続ける
    private static void Gunner(PlayerControl pc, Mind m, TriggerHappy th, PlayerControl target, float now)
    {
        float range = TriggerHappy.SprayRange;
        bool seen = Near(pc, target, range * 0.75f, out var dir);
        if (!th.Active)
        {
            th.BotAim = null;
            if (!seen) { Approach(pc, m, target, now); return; }
            Dummy.StopWalk(pc);
            if (now >= m.NextUse && th.Spray != null) Use(pc, m, th.Spray.Index, 255, now);
            return;
        }
        // 構えている間は歩かない (本人も構えている間は歩けない)。見えなくなったら寄り直す
        if (seen) { Dummy.StopWalk(pc); th.BotAim = dir; }
        else { th.BotAim = null; Approach(pc, m, target, now); }
    }

    // リモコン: 相手に印を付け、操作して勝手な向きへ歩かせる (遠隔キルはしない)
    private static void Remote(PlayerControl pc, Mind m, RemoteController rc, PlayerControl target, float now)
    {
        if (rc.IsOperating)
        {
            Dummy.StopWalk(pc);
            if (now >= m.NextWander)
            {
                m.NextWander = now + 1.2f + (float)Rng.NextDouble();
                double a = Rng.Next(8) * Math.PI / 4.0;
                rc.BotDir = FxMath.V2((float)Math.Cos(a), (float)Math.Sin(a));
            }
            return;
        }
        rc.BotDir = Vector2.zero;
        if (now < m.NextUse) return;
        if (rc.MarkedId == target.PlayerId)
        {
            Dummy.StopWalk(pc);
            Use(pc, m, 1, 255, now);
            return;
        }
        if (!Near(pc, target, KillReach() * 0.9f, out _)) { Approach(pc, m, target, now); return; }
        Dummy.StopWalk(pc);
        Use(pc, m, 0, target.PlayerId, now);
    }

    // ロケットランチャー: 寄って掴み、少し待ってから一番開けた向きへ撃ち出す
    private static void Rocket(PlayerControl pc, Mind m, RocketLauncher rl, PlayerControl target, float now)
    {
        if (rl.Flying) { Dummy.StopWalk(pc); return; }
        if (rl.HeldId != 255)
        {
            Dummy.StopWalk(pc);
            if (m.HeldAt < 0f) m.HeldAt = now;
            if (now - m.HeldAt < 1.5f) return;
            m.HeldAt = -1f;
            rl.BotShoot(OpenDirection(pc.GetTruePosition()));
            m.NextUse = now + 2f;
            return;
        }
        m.HeldAt = -1f;
        if (now < m.NextUse) return;
        if (!Near(pc, target, KillReach() * 0.9f, out _)) { Approach(pc, m, target, now); return; }
        Dummy.StopWalk(pc);
        Use(pc, m, 0, target.PlayerId, now);
    }

    // ノヴァ: 見通しの利く所まで寄って、相手の方へ溜めて撃つ
    private static void Beam(PlayerControl pc, Mind m, Nova n, PlayerControl target, float now)
    {
        if (n.BotCharging) { Dummy.StopWalk(pc); return; }
        if (!Near(pc, target, 6f, out var dir)) { Approach(pc, m, target, now); return; }
        Dummy.StopWalk(pc);
        if (now < m.NextUse) return;
        float angle = FxMath.Atan2(dir.y, dir.x) * (180f / MathF.PI);
        if (n.BotFire(angle, Nova.Charge * 0.6f)) m.NextUse = now + Nova.Charge + 1f; // 待ち時間は BotFire が本人と同じに見る
        else m.NextUse = now + RetryGap;
    }

    // その他の役職: 人を狙う能力があれば寄って使う。無くてキルができればキルする
    private static void Generic(PlayerControl pc, Mind m, RoleBase role, PlayerControl target, float now)
    {
        int use = -1;
        float reach = KillReach();
        if (role != null)
            for (int i = 0; i < role.Abilities.Count; i++)
            {
                var a = role.Abilities[i];
                if (a.Ghost || a.TargetDead || a.UsesLeft == 0) continue;
                if (a.Target == AbilityTarget.Pick || a.Target == AbilityTarget.Player)
                {
                    if (a.CanTarget != null && !a.CanTarget(pc, target)) continue;
                    use = i;
                    if (a.Target == AbilityTarget.Pick) reach = 1000f;
                    else if (a.Range > 0f) reach = a.Range;
                    break;
                }
            }
        bool kill = use < 0 && PortKit.CanKillAnyone(pc);
        if (use < 0 && !kill) { Dummy.StopWalk(pc); return; }
        if (reach < 1000f && !Near(pc, target, reach * 0.9f, out _)) { Approach(pc, m, target, now); return; }
        Dummy.StopWalk(pc);
        if (now < m.NextUse) return;
        if (use >= 0) { Use(pc, m, use, target.PlayerId, now); return; }
        if (!BotRound.KillReady(pc.PlayerId)) return;
        string why = Dummy.Kill(pc, target);
        if (why == null) BotRound.Killed(pc.PlayerId);
        else m.NextUse = now + RetryGap;
    }

    private static void Use(PlayerControl pc, Mind m, int index, byte target, float now)
    {
        string why = Abilities.UseAs(pc, index, target);
        m.NextUse = now + RetryGap;
        m.Last = $"use#{index}->{target}:{why ?? "ok"}";
        if (why != null && why != "cooldown") Plugin.Logger.LogDebug($"bot {pc.PlayerId} ability #{index}: {why}");
    }

    // 相手が reach の内側で、間に壁が無いか (dir = 相手の向き)
    private static bool Near(PlayerControl pc, PlayerControl target, float reach, out Vector2 dir)
    {
        Vector2 a = pc.GetTruePosition(), b = target.GetTruePosition();
        float dx = b.x - a.x, dy = b.y - a.y, d = FxMath.Sqrt(dx * dx + dy * dy);
        dir = d > 0.001f ? FxMath.V2(dx / d, dy / d) : FxMath.V2(1f, 0f);
        if (d > reach) return false;
        return !PhysicsHelpers.AnythingBetween(a, b, Constants.ShipAndObjectsMask, false);
    }

    // 相手の所へ歩く (道は時々引き直す)
    private static void Approach(PlayerControl pc, Mind m, PlayerControl target, float now)
    {
        Vector2 a = pc.GetTruePosition(), b = target.GetTruePosition();
        // 道案内の地図は壁だけで家具 (机など) を知らないので、詰まったら横へよけてから引き直す
        if (Dummy.TakeStuck(pc.PlayerId))
        {
            float dx = b.x - a.x, dy = b.y - a.y, d = FxMath.Sqrt(dx * dx + dy * dy);
            if (d > 0.01f)
            {
                float side = Rng.Next(2) == 0 ? 1f : -1f;
                Dummy.WalkTo(pc, a.x - dy / d * 1.2f * side, a.y + dx / d * 1.2f * side);
                m.NextPath = now + 1.2f;
                m.Last = "sidestep";
                return;
            }
        }
        // 歩いている間は引き直さない (引き直すと詰まりの見張りが始めからになり、机に押し付けたままになる)。相手が離れた時だけ
        if (Dummy.IsWalking(pc.PlayerId))
        {
            float mx = b.x - m.ToX, my = b.y - m.ToY;
            if (now < m.NextPath || mx * mx + my * my < 1.5f * 1.5f) return;
        }
        m.NextPath = now + PathGap;
        m.ToX = b.x; m.ToY = b.y;
        m.Last = "walk";
        Xs.Clear(); Ys.Clear();
        if (DummyNav.Path(a.x, a.y, b.x, b.y, Xs, Ys) && Xs.Count > 0) Dummy.WalkPath(pc, Xs.ToArray(), Ys.ToArray());
        else Dummy.WalkTo(pc, b.x, b.y);
    }

    // 8 方向のうち、壁まで一番遠い向き
    private static Vector2 OpenDirection(Vector2 from)
    {
        Vector2 best = FxMath.V2(1f, 0f);
        float far = -1f;
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4.0;
            var d = FxMath.V2((float)Math.Cos(a), (float)Math.Sin(a));
            // ロケットは家具 (層 12) にも当たるので、机も壁と同じに見る
            var hit = Physics2D.Raycast(from, d, 30f, Constants.ShipAndObjectsMask | (1 << 12));
            float dist = hit.collider ? hit.distance : 30f;
            if (dist > far) { far = dist; best = d; }
        }
        return best;
    }

    private static float KillReach() => GameManager.Instance.LogicOptions.GetKillDistance();

    internal static string Describe()
    {
        int pcs = 0, nodata = 0;
        foreach (var p in PlayerControl.AllPlayerControls) { pcs++; if (!p || p.Data == null) nodata++; }
        var sb = new System.Text.StringBuilder($"mode={Current} target={TargetId} bots={Ours.Count} pcs={pcs} nodata={nodata} infos={(GameData.Instance ? GameData.Instance.AllPlayers.Count : -1)} round=[{BotRound.Describe()}]");
        foreach (var r in RoleState.All)
            if (r is RemoteController rc) sb.Append($" rc{rc.PlayerId}:mark={rc.MarkedId},op={rc.IsOperating}");
        foreach (var (pc, role) in List())
            sb.Append($" {pc.PlayerId}:{role?.Id ?? "-"}/{RoleState.Of(pc)?.Id ?? "-"}/{(Minds.TryGetValue(pc.PlayerId, out var m) ? m.Last : "-")}/move={pc.CanMove}");
        return sb.ToString();
    }
}
