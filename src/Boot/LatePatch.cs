using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace MoreRolesPlus.Boot;

// パッチ当て (起動の 0.3 秒ほど) のうち、試合・ロビーの中でしか動かない本編の型へのパッチを後回しにする。
// メニューが出てから (ログインを待っている間に) 1 フレーム数 ms ずつ当て、メニューを早く出す。
// 当て終わる前にロビーや船へ入りそうな時 (部屋に入った・船や自分の駒ができた) は残りを全部その場で当てる。
// 後回しにするのは、パッチ先の型が全部下の一覧に入っているクラスだけ (それ以外・TargetMethod で選ぶ物は今まで通り起動時)
internal static class LatePatch
{
    private static readonly HashSet<Type> InGameOnly = new()
    {
        typeof(PlayerControl), typeof(PlayerPhysics), typeof(MeetingHud), typeof(HudManager), typeof(ShipStatus), typeof(Vent),
        typeof(LobbyBehaviour), typeof(MapBehaviour), typeof(EndGameManager), typeof(Console), typeof(VitalsPanel), typeof(UseButton),
        typeof(SystemConsole), typeof(Mushroom), typeof(MapConsole), typeof(LogicGameFlowNormal), typeof(KillButton), typeof(IntroCutscene),
        typeof(EmergencyMinigame), typeof(ZiplineConsole), typeof(SwitchSystem), typeof(SpawnInMinigame), typeof(ShapeshifterRole),
        typeof(ShapeshifterMinigame), typeof(SabotageSystemType), typeof(PlainDoor), typeof(NormalPlayerTask), typeof(MushroomWallDoor),
        typeof(MovingPlatformBehaviour), typeof(Minigame), typeof(MedScanMinigame), typeof(MapRoom), typeof(MapCountOverlay),
        typeof(InfectedOverlay), typeof(HeliSabotageSystem), typeof(DoorConsole), typeof(CrewmateGhostRole), typeof(AirshipStatus),
        typeof(AbilityButton), typeof(GameStartManager), typeof(ExileController), typeof(PlayerVoteArea), typeof(ChatController),
        typeof(PolusShipStatus), typeof(FungleShipStatus), typeof(DeconControl), typeof(ActionButton), typeof(VentButton),
        typeof(ReportButton), typeof(SabotageButton), typeof(ImpostorRole), typeof(EngineerRole), typeof(ScientistRole),
        typeof(PhantomRole), typeof(TrackerRole), typeof(NoisemakerRole), typeof(GuardianAngelRole), typeof(DeadBody),
    };

    private static readonly Queue<Type> _pending = new();
    private static Harmony _harmony;
    private static int _early, _late;
    private static long _lateMs;

    public static void PatchEarly(Harmony harmony)
    {
        _harmony = harmony;
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(LatePatch).Assembly))
        {
            if (Deferrable(type)) { _pending.Enqueue(type); continue; }
            harmony.CreateClassProcessor(type).Patch();
            _early++;
        }
    }

    // パッチ先の型が全部わかっていて、全部が試合・ロビーの中だけの型か
    private static bool Deferrable(Type type)
    {
        try
        {
            var attrs = HarmonyMethodExtensions.GetFromType(type);
            bool any = false;
            Type classTarget = null;
            foreach (var a in attrs)
            {
                if (a.declaringType != null) classTarget = a.declaringType;
            }
            foreach (var m in AccessTools.GetDeclaredMethods(type))
            {
                if (m.Name is "TargetMethod" or "TargetMethods" || m.GetCustomAttributes(typeof(HarmonyTargetMethod), false).Length > 0
                    || m.GetCustomAttributes(typeof(HarmonyTargetMethods), false).Length > 0) return false;
                foreach (var a in HarmonyMethodExtensions.GetFromMethod(m))
                {
                    var t = a.declaringType ?? classTarget;
                    if (t == null) continue;
                    if (!InGameOnly.Contains(t)) return false;
                    any = true;
                }
            }
            if (classTarget != null)
            {
                if (!InGameOnly.Contains(classTarget)) return false;
                any = true;
            }
            return any;
        }
        catch { return false; }
    }

    // Ticker.Update から。メニューが出た後に 1 フレーム budgetMs まで当てる。試合・ロビーに入りかけたら全部
    public static void Tick()
    {
        if (_pending.Count == 0 || !BootClock.MenuShown) return;
        bool now = (AmongUsClient.Instance && AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.NotJoined)
                   || LobbyBehaviour.Instance || ShipStatus.Instance || PlayerControl.LocalPlayer;
        var sw = Stopwatch.StartNew();
        while (_pending.Count > 0 && (now || sw.ElapsedMilliseconds < 6))
        {
            var type = _pending.Dequeue();
            try { _harmony.CreateClassProcessor(type).Patch(); _late++; }
            catch (Exception e) { Plugin.Logger.LogError($"late patch {type.FullName}: {e}"); }
        }
        _lateMs += sw.ElapsedMilliseconds;
        if (_pending.Count == 0) Plugin.Logger.LogInfo($"late patch: done early={_early} late={_late} in {_lateMs}ms{(now ? " (flushed)" : "")}");
    }

    public static bool Done => _pending.Count == 0;
}
