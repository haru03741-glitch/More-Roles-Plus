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
        long t = Bridge.Perf.Begin();
        Terrain.GameClock.Tick();
        Terrain.TerrainFx.Tick();
        Terrain.BreakNoise.Tick();
        Terrain.HammerSwing.Tick();
        Terrain.BombFuse.Tick();
        Terrain.DustCloud.Tick();
        Terrain.WaterSim.Tick();
        Terrain.Decompression.Tick();
        Terrain.CrewGrip.Tick();
        Terrain.FoamArt.Tick();
        Terrain.DecompFx.Tick();
        Terrain.DecompSound.Tick();
        Terrain.PropSim.Tick();
        Terrain.WaterLeak.Tick();
        Terrain.WaterArt.Tick();
        Terrain.WaterSpray.Tick();
        Fx.FxHands.Tick();
        Terrain.RubbleBake.Tick();
        Terrain.ShadowPatch.Tick();
        Bridge.Burst.Tick();
        Dev.DevConsole.Tick();
        Commands.Shortcuts.Tick();
        Zoom.Tick();
        Commands.ChatInput.Tick();
        Bridge.Perf.End(t);
    }

    private void LateUpdate()
    {
        Terrain.DecompFx.LateTick();
        Bridge.Perf.FrameLate();
    }

    private void FixedUpdate()
    {
        long t = Bridge.Perf.Begin();
        Fx.MrpBundle.Tick();
        Terrain.TerrainWarm.Tick();
        Terrain.TerrainSync.Tick();
        Net.VersionCheck.Tick();
        Roles.KillAbility.Tick();
        Roles.Abilities.Tick();
        Roles.ModButton.TickAll();
        Roles.MeetingEndWatch.Tick();
        Bridge.TestBridge.Tick();
        Net.Remote.Tick(); // 上の Tick が積んだ電文を同じ tick で出す
        Bridge.Perf.EndFixed(t);
    }
}
