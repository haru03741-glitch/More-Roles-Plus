// Based on https://github.com/waffle-ful/Aeterna-End-K-not Modules/BootTimeline.cs (GPL-3.0)
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
    // メニューの起動処理 (ログイン・持ち物・ショップの準備) が終わったか、終わる前にメニューを離れた
    internal static bool StartupOver => _tickDone;
    // メニューが一度出た (起動の区切りの記録を締めた)
    internal static bool MenuShown => _done;

    // メニューが出てから本編の起動処理 (ログイン・持ち物・ショップの準備) が終わるまでを見る。終われば以後は何もしない
    internal static void Tick()
    {
        if (_tickDone || !_done) return;
        if (!_menu) { _tickDone = true; return; }
        if (!_menu.finishStartup) return;
        _tickDone = true;
        Plugin.Logger.LogInfo($"BOOT startup={_baseMs + _sw.ElapsedMilliseconds}");
        MemTrim.Compact("menu"); // 起動 (パッチ当て) で膨らんだヒープを詰める
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
        _line.Append(name).Append('=').Append(_baseMs + _sw.ElapsedMilliseconds).Append('/').Append(GC.GetTotalMemory(false) >> 20).Append("MB/").Append(Environment.WorkingSet >> 20).Append("MB ");
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
