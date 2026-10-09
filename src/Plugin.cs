using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace MoreRolesPlus;

[BepInPlugin(Guid, "More Roles Plus", Version)]
[BepInProcess("Among Us.exe")]
public class Plugin : BasePlugin
{
    public const string Guid = "86aa5c38-7092-4d6f-ae72-7e9301070806";
    public const string Version = "0.0.1";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;
    internal static ConfigEntry<bool> EnableTestBridge;
    internal static ConfigEntry<bool> DisableIncrementalGc;
    internal static ConfigEntry<bool> PreemptiveGc;
    internal static ConfigEntry<bool> ReuseDelegateTypes;
    internal static ConfigEntry<bool> SleepAccountManager;

    public override void Load()
    {
        Logger = Log;
        Boot.BootClock.Mark("load");
        EnableTestBridge = Config.Bind("Debug", "EnableTestBridge", false, "外部ツールからの遠隔テスト口を有効にする");
        DisableIncrementalGc = Config.Bind("Performance", "DisableIncrementalGc", true, "incremental GC を切る (GC 中の interop の書き込みで落ちるのを防ぐ・Windows のみ)");
        ReuseDelegateTypes = Config.Bind("Performance", "ReuseDelegateTypes", true, "パッチを当てる時の型を形ごとに使い回して起動を速くする (パッチが当たらない時は切る)");
        SleepAccountManager = Config.Bind("Performance", "SleepAccountManager", true, "メニューのアカウント表示を止めておき、ログイン待ちの間もメニューを使えるようにする");
        PreemptiveGc = Config.Bind("Performance", "PreemptiveGc", true, "試合開始と終了の演出中に GC を先に回して、遊んでいる最中の引っかかりを減らす");

        Boot.IncrementalGcInvalidator.ApplyIfConfigured();
        Net.Remote.Init(); // 電文の名前は設定の指紋に入るので先に
        Options.Registry.Init();
        Dev.DevCommands.Register();
        Boot.BootClock.Mark("init");

        Harmony = new Harmony(Guid);
        if (ReuseDelegateTypes.Value) Boot.DelegateTypeCache.Install(Harmony);
        Harmony.PatchAll();
        Boot.BootClock.Mark("patch");
        Log.LogInfo(Boot.DelegateTypeCache.Summary());

        ClassInjector.RegisterTypeInIl2Cpp<Ticker>();
        AddComponent<Ticker>();
        Health.Install();
        Boot.BootClock.Mark("loaded");

        Log.LogInfo($"More Roles Plus {Version} loaded");
    }
}

// 毎フレームの処理をまとめて回す唯一の MonoBehaviour
public class Ticker : MonoBehaviour
{
    public Ticker(System.IntPtr ptr) : base(ptr) { }

    private static bool _ran;

    private void Update()
    {
        if (!_ran) { _ran = true; Boot.BootClock.Mark("frame1"); }
        Health.Frame();
        Boot.BootClock.Tick();
        Menu.AccountSleep.Tick();
        Bridge.Perf.FrameStart();
        long t0 = Bridge.Perf.Begin(), t = t0;
        Terrain.GameClock.Tick(); t = Bridge.Perf.Lane(0, t);
        Terrain.TerrainFx.Tick(); t = Bridge.Perf.Lane(1, t);
        Terrain.BreakNoise.Tick(); t = Bridge.Perf.Lane(2, t);
        Terrain.HammerSwing.Tick(); t = Bridge.Perf.Lane(3, t);
        Terrain.BombFuse.Tick(); t = Bridge.Perf.Lane(4, t);
        Terrain.DustCloud.Tick(); t = Bridge.Perf.Lane(5, t);
        Terrain.TerrainStep.Tick(); t = Bridge.Perf.Lane(6, t);
        Terrain.Decompression.Tick(); t = Bridge.Perf.Lane(7, t);
        Terrain.CrewGrip.Tick(); t = Bridge.Perf.Lane(8, t);
        Terrain.FoamArt.Tick(); t = Bridge.Perf.Lane(9, t);
        Terrain.DecompFx.Tick(); t = Bridge.Perf.Lane(10, t);
        Terrain.DecompSound.Tick(); t = Bridge.Perf.Lane(11, t);
        Terrain.PropSim.Tick(); t = Bridge.Perf.Lane(12, t);
        Terrain.WaterLeak.Tick(); t = Bridge.Perf.Lane(13, t);
        Terrain.WaterArt.Tick(); t = Bridge.Perf.Lane(14, t);
        Terrain.WaterSpray.Tick(); Terrain.WaterFall.Tick(); t = Bridge.Perf.Lane(15, t);
        Fx.FxHands.Tick(); t = Bridge.Perf.Lane(16, t);
        Terrain.RubbleBake.Tick(); t = Bridge.Perf.Lane(17, t);
        Terrain.ShadowPatch.Tick(); t = Bridge.Perf.Lane(18, t);
        Bridge.Burst.Tick(); t = Bridge.Perf.Lane(19, t);
        Dev.DevConsole.Tick(); t = Bridge.Perf.Lane(20, t);
        Commands.Shortcuts.Tick(); t = Bridge.Perf.Lane(21, t);
        Zoom.Tick(); t = Bridge.Perf.Lane(22, t);
        Commands.ChatInput.Tick(); t = Bridge.Perf.Lane(23, t);
        Bridge.Perf.End(t0);
    }

    private void LateUpdate()
    {
        long t = Bridge.Perf.Begin();
        Terrain.DamageMap.Flush(); // 溜めた損傷マスクの変更を描画の前に送る
        Bridge.Perf.Lane(34, t);
        Terrain.DecompFx.LateTick();
        Bridge.Perf.FrameLate();
    }

    private void FixedUpdate()
    {
        long t0 = Bridge.Perf.Begin(), t = t0;
        Fx.MrpBundle.Tick(); t = Bridge.Perf.Lane(24, t);
        Terrain.TerrainWarm.Tick(); t = Bridge.Perf.Lane(25, t);
        Terrain.TerrainSync.Tick(); t = Bridge.Perf.Lane(26, t);
        Net.VersionCheck.Tick(); t = Bridge.Perf.Lane(27, t);
        Roles.KillAbility.Tick(); t = Bridge.Perf.Lane(28, t);
        Roles.Abilities.Tick(); t = Bridge.Perf.Lane(29, t);
        Roles.ModButton.TickAll(); t = Bridge.Perf.Lane(30, t);
        Roles.MeetingEndWatch.Tick(); t = Bridge.Perf.Lane(31, t);
        Bridge.TestBridge.Tick(); t = Bridge.Perf.Lane(32, t);
        Net.Remote.Tick(); t = Bridge.Perf.Lane(33, t); // 上の Tick が積んだ電文を同じ tick で出す
        Bridge.Perf.EndFixed(t0);
    }
}
