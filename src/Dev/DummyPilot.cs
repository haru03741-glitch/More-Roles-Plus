using HarmonyLib;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Dev;

// ホストがダミーに乗り移って動かす。ホストの歩く入力 (キーボード・画面のスティック) をダミーへ回し、
// カメラと視界の光をダミーへ付け替える。ホスト自身の体はその場に止める。
// 行動 (キル・通報・投票) は dact / 乗り移り中のキーで。会議・ダミーの死・試合の終わりで自動で戻る
internal static class DummyPilot
{
    public static PlayerControl Possessed { get; private set; }
    private static Transform _lightParent;

    public static string Possess(PlayerControl pc)
    {
        Release();
        if (!AmongUsClient.Instance.AmHost || !pc || !pc.isDummy || pc.Data == null || pc.Data.IsDead) return "not a living dummy (host only)";
        var lp = PlayerControl.LocalPlayer;
        var hud = Vanilla.Hud;
        if (!lp || !hud) return "not in game";
        Dummy.StopWalk(pc);
        Possessed = pc;
        hud.PlayerCam.Target = pc;
        var light = lp.lightSource;
        if (light)
        {
            _lightParent = light.transform.parent;
            light.transform.SetParent(pc.transform, false);
        }
        return null;
    }

    public static void Release()
    {
        var pc = Possessed;
        if (pc == null) return;
        Possessed = null;
        if (pc && pc.MyPhysics) pc.MyPhysics.body.velocity = FxMath.V2(0f, 0f);
        var lp = PlayerControl.LocalPlayer;
        var hud = Vanilla.Hud;
        if (hud && lp) hud.PlayerCam.Target = lp;
        if (lp && lp.lightSource && _lightParent) lp.lightSource.transform.SetParent(_lightParent, false);
        _lightParent = null;
    }

    // テスト用: 今の状態
    public static string Describe()
    {
        var pc = Possessed;
        if (pc == null) return "none";
        var hud = Vanilla.Hud;
        var d = hud && hud.joystick != null ? hud.joystick.DeltaL : default;
        var v = pc && pc.MyPhysics ? pc.MyPhysics.body.velocity : default;
        return $"id={(pc ? pc.PlayerId : -1)} canMove={(pc && pc.CanMove)} moveable={(pc && pc.moveable)} stick={d.x:0.##},{d.y:0.##} vel={v.x:0.##},{v.y:0.##} phys={_physCalls}";
    }
    internal static int _physCalls;
    // テスト用: 実の入力の代わりに使うスティックの値 (null = 実の入力)
    internal static Vector2? FakeStick;

    // 毎フレーム (ダミーがいる時だけ呼ばれる)
    public static void Tick()
    {
        // 乗り移りの切り替え: F6 = 次のダミー (最後の次は自分に戻る)
        if (Input.GetKeyDown(KeyCode.F6)) Cycle();
        var pc = Possessed;
        if (pc == null) return;
        if (!pc || pc.Data == null || pc.Data.IsDead || !AmongUsClient.Instance.AmHost || AmongUsClient.Instance.IsGameOver
            || MeetingHud.Instance || ExileController.Instance || !ShipStatus.Instance)
        {
            Release();
            return;
        }
        // F7 = 近くの人をキル / F8 = 近くの死体を通報 (無ければ緊急会議)
        if (Input.GetKeyDown(KeyCode.F7)) KillNearest(pc);
        else if (Input.GetKeyDown(KeyCode.F8)) ReportNearest(pc);
    }

    private static void Cycle()
    {
        var all = Dummy.All;
        int at = -1;
        for (int i = 0; i < all.Count; i++) if (all[i] == Possessed) at = i;
        for (int i = at + 1; i < all.Count; i++)
        {
            var c = all[i];
            if (c && c.Data != null && !c.Data.IsDead && Possess(c) == null) return;
        }
        Release();
    }

    private static void KillNearest(PlayerControl pc)
    {
        float reach = GameManager.Instance.LogicOptions.GetKillDistance();
        var me = pc.GetTruePosition();
        PlayerControl best = null;
        float bestD = reach * reach;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p == pc || p.Data == null || p.Data.IsDead || p.Data.Disconnected) continue;
            var q = p.GetTruePosition();
            float dx = q.x - me.x, dy = q.y - me.y, d = dx * dx + dy * dy;
            if (d <= bestD) { bestD = d; best = p; }
        }
        if (best) Dummy.Kill(pc, best);
    }

    private static void ReportNearest(PlayerControl pc)
    {
        var me = pc.GetTruePosition();
        NetworkedPlayerInfo body = null;
        float bestD = 2.5f * 2.5f;
        foreach (var b in Object.FindObjectsOfType<DeadBody>())
        {
            if (!b) continue;
            var q = b.TruePosition;
            float dx = q.x - me.x, dy = q.y - me.y, d = dx * dx + dy * dy;
            if (d > bestD) continue;
            bestD = d;
            body = GameData.Instance.GetPlayerById(b.ParentId);
        }
        Dummy.Report(pc, body);
    }
}

// 乗り移り中: 本編の歩行の後で、入力をダミーの速さへ回し、ホスト自身は止める。乗り移っていなければ何もしない
[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.FixedUpdate))]
internal static class DummyPilotPhysics
{
    public static void Postfix(PlayerPhysics __instance)
    {
        var pc = DummyPilot.Possessed;
        if (pc == null) return;
        if (__instance.AmOwner)
        {
            __instance.body.velocity = FxMath.V2(0f, 0f);
            return;
        }
        if (!pc || __instance.myPlayer != pc) return;
        DummyPilot._physCalls++;
        if (!pc.CanMove) return;
        Vector2 d;
        if (DummyPilot.FakeStick is { } fake) d = fake;
        else
        {
            var hud = Vanilla.Hud;
            if (!hud || hud.joystick == null) return;
            d = hud.joystick.DeltaL;
        }
        float s = __instance.TrueSpeed;
        __instance.body.velocity = FxMath.V2(d.x * s, d.y * s);
    }
}
