using System;
using System.Globalization;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Dev;

// 確かめ用 (ブリッジ `tpburst`): 電文で配る TP (Net.Teleport) を一定の速さで続け、切断されないかを見る。
// 始めた位置と横へ Step ずらした位置を行き来し、終わったら始めた位置へ戻る。
// 速さは電文の本数の予算 (1 秒 10 通) まで。
internal static class TpBurst
{
    private const int MaxRate = 10;
    private const int TicksPerSecond = 50; // FixedUpdate
    private const float Step = 2f;

    private static bool _on;
    private static int _rate, _ticks, _endTick, _done;
    private static float _acc;
    private static Vector2 _origin;
    private static int _movesAtStart, _deferredAtStart, _packetsAtStart;

    public static void Register()
    {
        TestBridge.Register("tpburst", $"<回数/秒 (1..{MaxRate})> <秒数 (1..120)> | stop  電文で配る TP を続ける (始めた位置と横へ {Step} の所を行き来)。毎秒の進みはログの [tpburst]", (args, reply) =>
        {
            var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a.Length >= 1 && a[0] == "stop")
            {
                Stop("stopped");
                reply("OK tpburst stop");
                return;
            }
            if (a.Length < 2 || !int.TryParse(a[0], out int rate) || !int.TryParse(a[1], out int sec))
            {
                reply("ERR tpburst <rate> <seconds> | stop");
                return;
            }
            var lp = PlayerControl.LocalPlayer;
            if (!lp) { reply("ERR tpburst no local player"); return; }
            _rate = Math.Clamp(rate, 1, MaxRate);
            _endTick = Math.Clamp(sec, 1, 120) * TicksPerSecond;
            _ticks = 0;
            _done = 0;
            _acc = 0f;
            _origin = lp.transform.position;
            _movesAtStart = Teleport.Moves;
            _deferredAtStart = Teleport.Deferred;
            _packetsAtStart = Remote.Packets;
            _on = true;
            Plugin.Logger.LogInfo($"[tpburst] start rate={_rate}/s seconds={_endTick / TicksPerSecond} mode={(AmongUsClient.Instance ? AmongUsClient.Instance.NetworkMode.ToString() : "-")}");
            reply($"OK tpburst rate={_rate} seconds={_endTick / TicksPerSecond}");
        });
    }

    // FixedUpdate (Net.Teleport.Tick より前)
    public static void Tick()
    {
        if (!_on) return;
        var client = AmongUsClient.Instance;
        if (!PlayerControl.LocalPlayer || !client || client.GameState == InnerNet.InnerNetClient.GameStates.NotJoined)
        {
            Stop("lost connection");
            return;
        }
        _acc += _rate / (float)TicksPerSecond;
        while (_acc >= 1f)
        {
            _acc -= 1f;
            _done++;
            Teleport.Self((_done & 1) == 1 ? new Vector2(_origin.x + Step, _origin.y) : _origin);
        }
        _ticks++;
        if (_ticks % TicksPerSecond == 0) Plugin.Logger.LogInfo($"[tpburst] {Summary()}");
        if (_ticks >= _endTick) Stop("done");
    }

    private static void Stop(string why)
    {
        if (!_on) return;
        _on = false;
        if (PlayerControl.LocalPlayer && (_done & 1) == 1) Teleport.Self(_origin);
        Plugin.Logger.LogInfo($"[tpburst] {why} {Summary()}");
    }

    private static string Summary()
    {
        var client = AmongUsClient.Instance;
        string state = client ? client.GameState.ToString() : "-";
        return string.Format(CultureInfo.InvariantCulture, "t={0:0.0}s requested={1} moved={2} deferred={3} packets={4} state={5} lastdc={6}",
            _ticks / (float)TicksPerSecond, _done, Teleport.Moves - _movesAtStart, Teleport.Deferred - _deferredAtStart,
            Remote.Packets - _packetsAtStart, state, client ? client.LastDisconnectReason.ToString() : "-");
    }
}
