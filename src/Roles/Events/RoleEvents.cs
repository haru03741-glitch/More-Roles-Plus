using HarmonyLib;

namespace MoreRolesPlus.Roles;

// ---- 試合で起きた事 (どれも全員の端末で起きる) ----

// 誰かが倒された。PlayerId = 倒された人 ([OnlyMine] なら自分が倒された時)
public sealed class PlayerMurderedEvent : GameEvent, IPlayerEvent
{
    public PlayerControl Killer { get; internal set; }
    public PlayerControl Target { get; internal set; }
    public byte PlayerId => Target.PlayerId;
}

// 会議で追放された
public sealed class PlayerExiledEvent : GameEvent, IPlayerEvent
{
    public PlayerControl Player { get; internal set; }
    public byte PlayerId => Player.PlayerId;
}

// 会議が始まった (投票の画面が出た)
public sealed class MeetingStartEvent : GameEvent { }

// 会議が終わって歩けるようになった。Exiled = 追放された人 (いなければ null)
public sealed class MeetingEndEvent : GameEvent
{
    public NetworkedPlayerInfo Exiled { get; internal set; }
}

// 試合が終わった。Result = 第三陣営がいる試合で配られた結果 (本編の判定のまま終わった試合は null)
public sealed class GameEndEvent : GameEvent
{
    public GameResult Result { get; internal set; }
}

// ---- 本編の入口 ----

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
internal static class MurderEventPatch
{
    public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target, [HarmonyArgument(1)] MurderResultFlags resultFlags)
    {
        KillAbility.OnMurder(__instance);
        if (RoleState.All.Count == 0 || !target || (resultFlags & MurderResultFlags.Succeeded) == 0) return;
        Events<PlayerMurderedEvent>.Run(new PlayerMurderedEvent { Killer = __instance, Target = target });
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Exiled))]
internal static class ExiledEventPatch
{
    public static void Postfix(PlayerControl __instance)
    {
        if (RoleState.All.Count == 0) return;
        Events<PlayerExiledEvent>.Run(new PlayerExiledEvent { Player = __instance });
    }
}

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
internal static class MeetingStartEventPatch
{
    public static void Postfix()
    {
        if (RoleState.All.Count == 0) return;
        Events<MeetingStartEvent>.Run(new MeetingStartEvent());
    }
}

// 追放の演出の後に操作を戻す所。Airship の別の演出からも同じ関数が呼ばれる
[HarmonyPatch(typeof(ExileController), nameof(ExileController.ReEnableGameplay))]
internal static class MeetingEndEventPatch
{
    public static void Postfix(ExileController __instance)
    {
        if (RoleState.All.Count == 0) return;
        Events<MeetingEndEvent>.Run(new MeetingEndEvent { Exiled = __instance.initData?.networkedPlayer });
    }
}
