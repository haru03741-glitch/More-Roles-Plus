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
    internal static ConfigEntry<bool> ForceDisableIncrementalGc;
    internal static ConfigEntry<bool> PreemptiveGc;
    internal static ConfigEntry<bool> ReuseDelegateTypes;
    internal static ConfigEntry<bool> SleepAccountManager;

    public override void Load()
    {
        Logger = Log;
        Log.LogInfo($"console: {ConsoleGuard.Install()}");
        Boot.BootClock.Mark("load");
        // C++ の部品が読めない時は mod ごと起動しない (地形を部品抜きで動かさない)。本編はそのまま遊べる
        if (!Native.MrpNative.Loaded)
        {
            Log.LogError($"More Roles Plus {Version} not started: {Native.MrpNative.Status}");
            Native.MrpNative.ShowMissing();
            return;
        }
        Boot.BootClock.Mark("native");
        EnableTestBridge = Config.Bind("Debug", "EnableTestBridge", false, "外部ツールからの遠隔テスト口を有効にする");
        ForceDisableIncrementalGc = Config.Bind("Performance", "ForceDisableIncrementalGc", false, "incremental GC を常に切る (既定は書き込みバリア付きの Il2CppInterop.Runtime が載っている 64bit の時だけ残す・Windows のみ)");
        ReuseDelegateTypes = Config.Bind("Performance", "ReuseDelegateTypes", true, "パッチを当てる時の型を形ごとに使い回して起動を速くする (パッチが当たらない時は切る)");
        // 止めるとログイン前のメニューが作りかけ (上の欄が無い・絵が出ない) のまま見えるので、既定は本編のまま (前の true を引き継がないよう名前を変えた)
        SleepAccountManager = Config.Bind("Performance", "SleepAccountMenu", false, "メニューのアカウント表示を止めておき、ログイン待ちの間もメニューを使えるようにする (ログインが終わるまでメニューが作りかけに見える)");
        PreemptiveGc = Config.Bind("Performance", "PreemptiveGc", true, "試合開始と終了の演出中に GC を先に回して、遊んでいる最中の引っかかりを減らす");
        Boot.BootClock.Mark("cfg0"); Net.MrpRegion.Bind(Config);
        Menu.ClientOptions.Bind(Config);
        Menu.CustomColors.Bind(Config);
        Menu.ModUpdate.Bind(Config);
        try { Menu.CustomColors.ExtendPalette(); }
        catch (System.Exception e) { Log.LogError($"colors: palette not extended: {e.Message}"); }
        Options.PresetLabel.Bind(Config);
        Boot.BootClock.Mark("cfg");

        Boot.IncrementalGcInvalidator.ApplyIfConfigured();
        Boot.UnityAllocatorSetup.Run(); // Unity のメモリの区切り方 (次の起動から効く)
        Boot.BootClock.Mark("igc");
        Net.Remote.Init(); // 電文の名前は設定の指紋に入るので先に
        Boot.BootClock.Mark("remote");
        Options.Registry.Init();
        Boot.BootClock.Mark("registry");
        Rules.VanillaTweaks.Init();
        Dev.DevCommands.Register();
        Dev.DevCooldown.Register();
        Boot.BootClock.Mark("init");

        Harmony = new Harmony(Guid);
        if (ReuseDelegateTypes.Value) Boot.DelegateTypeCache.Install(Harmony);
        Boot.LatePatch.PatchEarly(Harmony); // 試合の中だけのパッチはメニューが出てから
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
        Boot.LatePatch.Tick();
        Menu.AccountSleep.Tick();
        Bridge.Perf.FrameStart();
        long t0 = Bridge.Perf.Begin(), t = t0;
        Terrain.GameClock.Tick();
        Terrain.TerrainRewind.RunDue(); t = Bridge.Perf.Lane(0, t);
        Terrain.TerrainFx.Tick(); t = Bridge.Perf.Lane(1, t);
        Terrain.TrickWall.Tick();
        Roles.ArchitectPlan.Frame();
        Roles.ArchitectDrone.FrameAll();
        Roles.StatusEffects.Tick();
        Menu.Keybinds.Tick();
        Dev.Dummy.Tick();
        Dev.DummyBrain.Tick();
        Roles.DuelBot.Tick();
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
        Terrain.WaterSpray.Tick(); t = Bridge.Perf.Lane(15, t);
        Terrain.WaterFall.Tick(); t = Bridge.Perf.Lane(35, t);
        Terrain.WaterWade.Tick(); t = Bridge.Perf.Lane(38, t);
        Terrain.CrewBrace.Tick(); t = Bridge.Perf.Lane(40, t);
        Terrain.PropSim.SunkTick(); t = Bridge.Perf.Lane(39, t);
        Terrain.FireArt.Tick(); t = Bridge.Perf.Lane(36, t);
        Terrain.FireFx.Tick(); t = Bridge.Perf.Lane(37, t);
        Fx.FxHands.Tick(); t = Bridge.Perf.Lane(16, t);
        Fx.EmoteArt.Tick();
        Roles.RocketCannon.Tick();
        Roles.Impostor.RocketLauncher.LocalTick();
        Roles.SwipeInput.Tick();
        Roles.Neutral.Samurai.LocalTick();
        Roles.Watch.Tick();
        Roles.SamuraiBlade.TickAll();
        Roles.DuelHitFx.Tick();
        Roles.DoorCutFx.Tick();
        Roles.CutDoors.HostTick();
        Roles.SpaceRifts.Tick();
        Roles.RiftArt.Tick();
        Roles.DuelDodge.Tick();
        Roles.DuelTongue.Tick();
        Roles.DuelShade.Tick();
        Roles.DuelTumble.Tick();
        Roles.CutCorpse.Tick();
        Roles.DuelGloom.Tick();
        UI.DuelHud.Tick();
        Menu.Emotes.Tick();
        Menu.MeetingStamps.Tick();
        Roles.PortalArt.Tick();
        Roles.RewindWorldFx.Tick();
        Roles.RewindChargeFx.Tick();
        Roles.RewindCinematic.Tick();
        Roles.KatanaCinematic.Tick();
        Terrain.RubbleBake.Tick(); t = Bridge.Perf.Lane(17, t);
        Terrain.ShadowPatch.Tick(); t = Bridge.Perf.Lane(18, t);
        Bridge.Burst.Tick(); t = Bridge.Perf.Lane(19, t);
        Dev.DevConsole.Tick(); t = Bridge.Perf.Lane(20, t);
        Commands.Shortcuts.Tick(); t = Bridge.Perf.Lane(21, t);
        Zoom.Tick(); t = Bridge.Perf.Lane(22, t);
        NoClip.Tick();
        Dev.DevCooldown.Tick();
        Commands.ChatInput.Tick();
        Menu.ColorPicker.Tick();
        Menu.MeetingMemo.Tick();
        Options.RolePage.Tick(); UI.UiWindow.Tick(); UI.Backdrop.Tick(); UI.Aura.Tick(); t = Bridge.Perf.Lane(23, t);
        Options.CrowdPages.Tick();
        Options.GameModeSwitch.Tick();
        Menu.ChatNotes.Tick();
        Bridge.Perf.End(t0);
    }

    private void LateUpdate()
    {
        long t = Bridge.Perf.Begin();
        Terrain.TerrainWarm.LateTick(); // 船が出たフレームだけ準備をまとめて (読み込みの止まりに重ねる)
        t = Bridge.Perf.Lane(25, t);
        Terrain.DamageMap.Flush(); // 溜めた損傷マスクの変更を描画の前に送る
        Bridge.Perf.Lane(34, t);
        Terrain.DecompFx.LateTick();
        Roles.PortalArt.LateTick(); // 窓は人とカメラが動き終わった後に描く
        Roles.RiftView.LateTick();
        Terrain.WaterSim.Kick(); // 預けた水の小刻みを描画の裏で回す (読み手は次に水の配列に触る時に終わりを待つ)
        Bridge.Perf.FrameLate();
    }

    private void FixedUpdate()
    {
        long t0 = Bridge.Perf.Begin(), t = t0;
        Fx.MrpBundle.Tick(); t = Bridge.Perf.Lane(24, t);
        Terrain.TerrainWarm.Tick(); t = Bridge.Perf.Lane(25, t);
        Terrain.TerrainSync.Tick(); t = Bridge.Perf.Lane(26, t);
        Menu.CustomColors.Tick();
        Commands.AfkGuard.Tick();
        Menu.AutoRejoin.Tick();
        Menu.ModUpdate.Tick();
        Roles.Spectate.Tick();
        Net.VersionCheck.Tick(); t = Bridge.Perf.Lane(27, t);
        Net.MrpRegion.Tick();
        Net.HostChange.Tick();
        Roles.TeamSizes.Tick();
        Menu.MatchInfoExtension.Tick();
        Menu.SabotageAlert.Tick();
        Roles.KillAbility.Tick(); t = Bridge.Perf.Lane(28, t);
        Rules.VanillaTweaks.Tick();
        Roles.RoleFrame.Tick();
        Roles.TimeRewind.Tick();
        Roles.Abilities.Tick(); t = Bridge.Perf.Lane(29, t);
        Roles.ModButton.TickAll(); t = Bridge.Perf.Lane(30, t);
        Rules.RulesHooks.Tick();
        Roles.MeetingEndWatch.Tick(); t = Bridge.Perf.Lane(31, t);
        Bridge.TestBridge.Tick(); t = Bridge.Perf.Lane(32, t);
        Dev.TpBurst.Tick();
        Dev.PosTrace.Tick();
        Net.Teleport.Tick();
        Net.Remote.Tick(); t = Bridge.Perf.Lane(33, t); // 上の Tick が積んだ電文を同じ tick で出す
        Bridge.Perf.EndFixed(t0);
    }
}
