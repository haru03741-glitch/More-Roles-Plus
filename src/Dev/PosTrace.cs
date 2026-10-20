using System;
using System.Globalization;
using System.Text;
using MoreRolesPlus.Bridge;
using UnityEngine;

namespace MoreRolesPlus.Dev;

// 確かめ用 (ブリッジ `ptrace`): 他人の体の位置を FixedUpdate ごとに記録し、TP が一度に移ったか・間を滑ったかを見る。
// 止まって立っている人を TP させて測る前提 (歩いている人では動いた tick がすべて数に入る)。
// 結果 = 動いた tick の数・一番大きな 1 tick の移動・道のり・始めと終わりの距離。1 回の TP なら moving=1 で道のり ≈ 距離。
internal static class PosTrace
{
    private const int TicksPerSecond = 50; // FixedUpdate
    private const float Still = 0.01f;     // これ以下の 1 tick の移動は止まっている扱い
    private const int MaxSteps = 40;       // 記録に並べる動いた tick の上限

    private static bool _on;
    private static byte _pid;
    private static int _ticks, _endTick, _moving;
    private static Vector2 _start, _last;
    private static float _path, _maxStep;
    private static readonly StringBuilder Steps = new();
    private static Action<string> _reply;

    public static void Register()
    {
        TestBridge.Register("ptrace", "<番号> <秒数 (1..30)>  その人の位置を毎 tick 記録し、動いた tick の数・最大の 1 歩・道のりを返す (TP が一度に移るかの確かめ)", (args, reply) =>
        {
            var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a.Length < 2 || !byte.TryParse(a[0], out byte pid) || !int.TryParse(a[1], out int sec))
            {
                reply("ERR ptrace <番号> <秒数>");
                return;
            }
            var p = Find(pid);
            if (!p) { reply($"ERR ptrace no player {pid}"); return; }
            if (_on) Finish("restarted");
            _pid = pid;
            _endTick = Math.Clamp(sec, 1, 30) * TicksPerSecond;
            _ticks = 0;
            _moving = 0;
            _path = 0f;
            _maxStep = 0f;
            _start = _last = p.transform.position;
            Steps.Clear();
            _reply = reply;
            _on = true;
        });
    }

    // FixedUpdate
    public static void Tick()
    {
        if (!_on) return;
        var p = Find(_pid);
        if (!p) { Finish("lost"); return; }
        Vector2 now = p.transform.position;
        float step = Vector2.Distance(now, _last);
        if (step > Still)
        {
            _moving++;
            _path += step;
            if (step > _maxStep) _maxStep = step;
            if (_moving <= MaxSteps) Steps.Append(string.Format(CultureInfo.InvariantCulture, " {0}:{1:0.00}", _ticks, step));
        }
        _last = now;
        if (++_ticks >= _endTick) Finish("done");
    }

    private static void Finish(string why)
    {
        _on = false;
        string msg = string.Format(CultureInfo.InvariantCulture,
            "OK ptrace {0} p{1} ticks={2} moving={3} maxstep={4:0.00} path={5:0.00} net={6:0.00} from={7:0.00},{8:0.00} to={9:0.00},{10:0.00} steps=[{11} ]",
            why, _pid, _ticks, _moving, _maxStep, _path, Vector2.Distance(_last, _start), _start.x, _start.y, _last.x, _last.y, Steps.ToString());
        Plugin.Logger.LogInfo($"[ptrace] {msg}");
        var reply = _reply;
        _reply = null;
        reply?.Invoke(msg);
    }

    private static PlayerControl Find(byte pid)
    {
        foreach (var p in PlayerControl.AllPlayerControls)
            if (p && p.PlayerId == pid) return p;
        return null;
    }
}
