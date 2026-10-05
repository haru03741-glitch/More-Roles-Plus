using System;
using System.Diagnostics;
using System.Text;
using HarmonyLib;

namespace MoreRolesPlus.Boot;

// 起動の各区切りがプロセス開始から何 ms かを 1 行にまとめてログへ出す (起動の速さを見るため)
internal static class BootClock
{
    private static readonly StringBuilder _line = new();
    private static long _baseMs = -1;
    private static readonly Stopwatch _sw = Stopwatch.StartNew();
    private static bool _done;
    private static MainMenuManager _menu;
    private static bool _tickDone;

    // メニューが出てから本編の起動処理 (ログイン・持ち物・ショップの準備) が終わるまでを見る。終われば以後は何もしない
    internal static void Tick()
    {
        if (_tickDone || !_done) return;
        if (!_menu) { _tickDone = true; return; }
        if (!_menu.finishStartup) return;
        _tickDone = true;
        Plugin.Logger.LogInfo($"BOOT startup={_baseMs + _sw.ElapsedMilliseconds}");
    }

    internal static void Mark(string name)
    {
        if (_done) return;
        if (_baseMs < 0)
        {
            try { _baseMs = (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds; }
            catch { _baseMs = 0; }
            _sw.Restart();
        }
        _line.Append(name).Append('=').Append(_baseMs + _sw.ElapsedMilliseconds).Append(' ');
    }

    internal static void Finish(string name)
    {
        if (_done) return;
        Mark(name);
        _done = true;
        Plugin.Logger.LogInfo("BOOT " + _line.ToString().TrimEnd());
    }

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Awake))]
    private static class MenuAwake
    {
        public static void Prefix(MainMenuManager __instance)
        {
            _menu ??= __instance;
            Finish("menu");
        }
    }
}
