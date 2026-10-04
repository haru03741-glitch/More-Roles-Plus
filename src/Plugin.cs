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

    public override void Load()
    {
        Logger = Log;
        EnableTestBridge = Config.Bind("Debug", "EnableTestBridge", false, "外部ツールからの遠隔テスト口を有効にする");

        Harmony = new Harmony(Guid);
        Harmony.PatchAll();

        ClassInjector.RegisterTypeInIl2Cpp<Ticker>();
        AddComponent<Ticker>();

        Log.LogInfo($"More Roles Plus {Version} loaded");
    }
}

// 毎フレームの処理をまとめて回す唯一の MonoBehaviour
public class Ticker : MonoBehaviour
{
    public Ticker(System.IntPtr ptr) : base(ptr) { }

    private void Update()
    {
        Bridge.Perf.FrameStart();
        long t = Bridge.Perf.Begin();
        Terrain.TerrainFx.Tick();
        Terrain.RubbleBake.Tick();
        Bridge.Burst.Tick();
        Bridge.Perf.End(t);
    }

    private void LateUpdate() => Bridge.Perf.FrameLate();

    private void FixedUpdate()
    {
        long t = Bridge.Perf.Begin();
        Fx.MrpBundle.Tick();
        Terrain.TerrainSync.Tick();
        Bridge.TestBridge.Tick();
        Bridge.Perf.EndFixed(t);
    }
}
