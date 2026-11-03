using System;
using System.Collections.Generic;
using MoreRolesPlus.Roles;
using MoreRolesPlus.Rules;
using UnityEngine;

namespace MoreRolesPlus.Dev;

// ロビーのボットを、試合の区切り (始まりの場面の後・会議の後) で本物の人と同じ条件に置く (ホストだけ)。
// - 湧く: Airship は人が湧き場所を選ぶ間をおいてから選べる場所の 1 つへ。他のマップも湧き場所を選ぶ・ばらばらに飛ばす設定なら同じ
// - 湧くまでは何もしない (人は選んでいる間動けない)
// - キルの待ち時間: 湧いた時から、試合の始めは 10 秒 (本編のインポスターと同じ)・会議の後は設定の秒
// - 能力の待ち時間: 始めから待つ能力は湧いた時から数え直す (本人のボタンが HUD が戻るたびに数え直すのと同じ)
internal static class BotRound
{
    private const float FirstKill = 10f;

    private static readonly Dictionary<byte, float> SpawnAt = new();   // まだ湧いていないボット → 湧く時刻 (Time.time)
    private static readonly Dictionary<byte, float> KillAt = new();    // キルできるようになる時刻
    private static readonly List<byte> Due = new();
    private static readonly System.Random Rng = new();
    private static int _gen = -1;
    private static bool _begun, _first, _blocked;
    private static long _nextMs;
    private const int CheckMs = 100; // 本編の場面の切り替わりを見る間隔

    // このボットが動いてよいか (湧いた後で、会議・場面の切り替わりの間でない)
    internal static bool Ready(byte id) => _begun && !_blocked && !SpawnAt.ContainsKey(id);

    internal static bool KillReady(byte id) => Ready(id) && KillAt.TryGetValue(id, out float t) && Time.time >= t;

    // キルが通った (本人のキルボタンと同じく設定の秒から数え直す)
    internal static void Killed(byte id) => KillAt[id] = Time.time + KillCooldown();

    private static float KillCooldown() => GameManager.Instance ? GameManager.Instance.LogicOptions.GetKillCooldown() : FirstKill;

    // BotPlay.Tick から (ホストで試合中だけ)
    internal static void Tick(IEnumerable<byte> bots)
    {
        long ms = Environment.TickCount64;
        if (ms < _nextMs) return;
        _nextMs = ms + CheckMs;
        if (_gen != Terrain.GameClock.ShipGen)
        {
            _gen = Terrain.GameClock.ShipGen;
            _begun = false;
            _first = true;
            _blocked = true; // 新しい船は区切りの外から始める (始まりの場面が消えた時に Begin を通す)
            SpawnAt.Clear();
            KillAt.Clear();
        }
        // Airship の追放の場面はホスト自身が湧き場所を選び終えるまで残るので、選ぶ画面が出たら終わった扱い
        bool blocked = !Spectate.IntroStarted || IntroCutscene.Instance || MeetingHud.Instance || (ExileController.Instance && !Picking());
        if (blocked)
        {
            _blocked = true;
            return;
        }
        if (_blocked)
        {
            _blocked = false;
            Begin(bots);
        }
        if (SpawnAt.Count == 0) return;
        float now = Time.time;
        Due.Clear();
        foreach (var (id, at) in SpawnAt)
            if (now >= at) Due.Add(id);
        foreach (byte id in Due)
        {
            SpawnAt.Remove(id);
            var pc = PortKit.ById(id);
            if (!PortKit.Alive(pc)) continue;
            if (RandomSpawn.BotSpot(out var to)) pc.NetTransform.RpcSnapTo(to);
            StartTimers(pc);
        }
    }

    private static bool Picking()
    {
        var m = Minigame.Instance;
        return m && m.TryCast<SpawnInMinigame>() != null;
    }

    // 区切りの始まり: 湧く時刻を決める (湧き直さないマップはその場で湧く)
    private static void Begin(IEnumerable<byte> bots)
    {
        bool first = !_begun;
        _begun = true;
        _first = first;
        SpawnAt.Clear();
        float now = Time.time;
        // Airship と湧き場所を選ぶ設定は人が選ぶ間 (数秒) をおく。ばらばらに飛ばすだけなら動けるようになってすぐ
        bool picks = MapRules.MapId == 4 || SelectSpawn.Active;
        bool moves = RandomSpawn.BotSpot(out _);
        foreach (byte id in bots)
        {
            var pc = PortKit.ById(id);
            if (!PortKit.Alive(pc)) continue;
            if (!moves) { StartTimers(pc); continue; }
            float wait = picks ? 1.5f + (float)Rng.NextDouble() * 2.5f : 0.3f + (float)Rng.NextDouble() * 0.5f;
            SpawnAt[id] = now + wait;
        }
        Plugin.Logger.LogInfo($"bots: round {(first ? "start" : "after meeting")} spawn={(moves ? (picks ? "pick" : "random") : "here")} waiting={SpawnAt.Count}");
    }

    private static void StartTimers(PlayerControl pc)
    {
        float cd = KillCooldown();
        KillAt[pc.PlayerId] = Time.time + (_first ? Math.Min(FirstKill, cd) : cd);
        var role = RoleState.Of(pc);
        if (role == null) return;
        long now = Environment.TickCount64;
        foreach (var a in role.Abilities)
            if (a.CooldownAtStart) a.HostLastMs = now;
    }

    internal static string Describe()
    {
        float now = Time.time;
        string why = !Spectate.IntroStarted ? "nointro" : IntroCutscene.Instance ? "intro" : MeetingHud.Instance ? "meeting" : ExileController.Instance ? (Picking() ? "picking" : "exile") : "-";
        var sb = new System.Text.StringBuilder($"begun={_begun} first={_first} blocked={_blocked}({why}) waiting={SpawnAt.Count}");
        foreach (var (id, t) in KillAt) sb.Append($" {id}:kill+{Math.Max(0f, t - now):0.0}");
        return sb.ToString();
    }
}
