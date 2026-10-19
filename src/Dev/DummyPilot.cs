using HarmonyLib;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Dev;

// ホストがダミーに乗り移って動かす。ホストの歩く入力 (キーボード・画面のスティック) をダミーへ回し、
// カメラと視界の光をダミーへ付け替える。ホスト自身の体はその場に止める。
// 行動 (キル・通報・投票) は dact / 乗り移り中のキーで。ダミーの死・試合の終わりで自動で戻る。
// 会議では乗り移ったダミーが「票を入れる人」になり、ホストが会議画面で押した票がそのダミーの票になる
// (入れたらホスト自身の票に戻る)。会議の後はそのダミーへ乗り移り直す
internal static class DummyPilot
{
    public static PlayerControl Possessed { get; private set; }
    private static Transform _lightParent;
    // 会議中に票を入れるダミー (null = ホスト自身)
    public static PlayerControl Voter { get; private set; }
    // 会議の後で乗り移り直すダミー
    private static PlayerControl _resume;
    // 票を入れる間だけ止めた本編のダミーの部品 (フリープレイの本編のダミーは自分で票を入れるので)
    private static DummyBehaviour _pausedBrain;
    private static byte _lit = 255;

    public static string Possess(PlayerControl pc)
    {
        Release();
        if (!AmongUsClient.Instance.AmHost || !pc || !pc.isDummy || pc.Data == null || pc.Data.IsDead) return "not a living dummy (host only)";
        var meeting = MeetingHud.Instance;
        if (meeting) return SetVoter(meeting, pc, true) ? null : "already voted";
        var lp = PlayerControl.LocalPlayer;
        var hud = Vanilla.Hud;
        if (!lp || !hud || !ShipStatus.Instance) return "not in game";
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
        FakeStick = null;
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

    // 乗り移り・会議の票・乗り移り直しを全部やめる
    public static void Reset()
    {
        Release();
        var hud = MeetingHud.Instance;
        if (hud && Voter != null) SetVoter(hud, null, false);
        ClearVoter();
        _resume = null;
    }

    private static void ClearVoter()
    {
        Voter = null;
        _lit = 255;
        if (_pausedBrain) _pausedBrain.enabled = true;
        _pausedBrain = null;
    }

    // 票を入れる人を替える (pc = null でホスト自身)。会議画面の光る枠も移す
    private static bool SetVoter(MeetingHud hud, PlayerControl pc, bool resumeAfter)
    {
        if (pc != null && (!pc || pc.Data == null || pc.Data.IsDead || hud.DidVote(pc.PlayerId))) return false;
        var lp = PlayerControl.LocalPlayer;
        byte me = lp ? lp.PlayerId : (byte)255;
        byte old = _lit == 255 ? me : _lit;
        ClearVoter();
        Voter = pc;
        if (resumeAfter) _resume = pc;
        byte now = pc != null ? pc.PlayerId : me;
        if (pc != null)
        {
            var db = pc.GetComponent<DummyBehaviour>();
            if (db && db.enabled) { db.enabled = false; _pausedBrain = db; }
            _lit = now;
        }
        foreach (var pva in hud.playerStates)
        {
            if (!pva) continue;
            byte id = pva.PlayerId.Value;
            if (id == old || id == now) pva.SetHighlighted(id == now);
        }
        return true;
    }

    // 会議画面で押した票 (MeetingHud.Confirm の前)。ダミーが票を入れる番なら横取りして true
    public static bool TakeVote(MeetingHud hud, byte target)
    {
        var pc = Voter;
        if (pc == null) return false;
        if (pc && pc.Data != null && !pc.Data.IsDead && !hud.DidVote(pc.PlayerId) && AmongUsClient.Instance.AmHost) Dummy.Vote(pc, target);
        // 押した印 (確定ボタン) を消し、ホストの票はまだ入れられるようにしておく
        foreach (var pva in hud.playerStates) if (pva) pva.ClearButtons();
        if (hud.SkipVoteButton) hud.SkipVoteButton.ClearButtons();
        SetVoter(hud, null, false);
        return true;
    }

    // F6 (会議中) = 次のまだ票を入れていないダミー (最後の次はホスト自身)
    private static void CycleVoter(MeetingHud hud)
    {
        var all = Dummy.All;
        int at = -1;
        for (int i = 0; i < all.Count; i++) if (all[i] == Voter) at = i;
        for (int i = at + 1; i < all.Count; i++)
            if (all[i] && SetVoter(hud, all[i], true)) return;
        SetVoter(hud, null, false);
        _resume = null;
    }

    private static void MeetingTick(MeetingHud hud)
    {
        if (!AmongUsClient.Instance.AmHost) { Reset(); return; }
        // 乗り移ったまま会議に入った: そのダミーが票を入れる番
        if (Possessed != null)
        {
            var pc = Possessed;
            Release();
            SetVoter(hud, pc, true);
        }
        if (Input.GetKeyDown(KeyCode.F6)) CycleVoter(hud);
        var v = Voter;
        if (v != null && (!v || v.Data == null || v.Data.IsDead)) SetVoter(hud, null, false);
    }

    // テスト用: 今の状態
    public static string Describe()
    {
        if (Voter != null || _resume != null) return $"voter={(Voter ? Voter.PlayerId : -1)} resume={(_resume ? _resume.PlayerId : -1)}" + (Possessed == null ? "" : " possessed");
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
        var meeting = MeetingHud.Instance;
        if (meeting) { MeetingTick(meeting); return; }
        if (Voter != null) ClearVoter();
        // 乗り移りの切り替え: F6 = 次のダミー (最後の次は自分に戻る)
        if (Input.GetKeyDown(KeyCode.F6)) { _resume = null; Cycle(); }
        // 会議 (と追放の場面) が終わって歩けるようになったら、会議で票を入れたダミーへ乗り移り直す
        if (_resume != null)
        {
            var lp = PlayerControl.LocalPlayer;
            if (AmongUsClient.Instance.IsGameOver || !_resume) _resume = null;
            else if (!ExileController.Instance && lp && lp.CanMove)
            {
                var r = _resume;
                _resume = null;
                Possess(r);
            }
        }
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

// 会議画面で票を確定した時: ダミーが票を入れる番ならダミーの票にして、本編の確定 (ホスト自身の票) は止める
[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Confirm))]
internal static class DummyPilotVote
{
    public static bool Prefix(MeetingHud __instance, byte suspectStateIdx) => !DummyPilot.TakeVote(__instance, suspectStateIdx);
}
