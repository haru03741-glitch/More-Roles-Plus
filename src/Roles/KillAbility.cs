using System;
using HarmonyLib;
using Hazel;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// インポスター以外の役職 (RoleBase.CanKill) に本編のキルボタンを使わせる。
// ボタンの表示・狙う相手の選び方・待ち時間の減り方は本編のまま (本編の役職に CanUseKillButton を立てる)。
// 押した時だけ本編の道 (インポスターでないと断られる) を通さず、ホストへ依頼を送る。ホストが確かめてから
// 本編のキル (RpcMurderPlayer) を全員に配るので、結果はホストが決めた 1 つになる。
internal static class KillAbility
{
    private const float DistanceSlack = 1f;     // 通信の遅れで相手が動いた分の余裕
    private const float CooldownSlack = 1.5f;   // 待ち時間の確かめの余裕 (秒)
    private const float FirstCooldown = 10f;    // 試合の始めの待ち時間 (本編のインポスターと同じ)
    private const int ScanEvery = 5;            // 狙う相手を探し直す間隔 (FixedUpdate の回数・0.1 秒)
    private static readonly long[] LastKillMs = new long[256];
    private static readonly bool[] _killed = new bool[256];

    // 自分がキル役の時だけ入る。本編はインポスター以外のキルボタンの待ち時間と狙いを動かさないので、ここで動かす
    private static RoleBase _local;
    private static float _dt;
    private static float _max = FirstCooldown;
    private static float _shownT = -1f, _shownMax;   // 最後にボタンへ書いた値 (同じなら書かない)
    private static bool _shown, _started;
    private static int _scan;
    private static PlayerControl _target;

    // 本編の役職の上に乗せるだけなので、インポスターの土台の役職は本編のまま
    private static bool Uses(RoleBase role) =>
        role is { CanKill: true } && role.Player && role.Player.Data != null && role.Player.Data.Role && !role.Player.Data.Role.IsImpostor;

    internal static void OnAssigned(RoleBase role)
    {
        // 最初のキルも試合の始めの待ち時間を確かめる (割り当ては始めの演出の前なので、そこから数えれば緩い側にずれる)
        LastKillMs[role.PlayerId] = Environment.TickCount64;
        _killed[role.PlayerId] = false;
        if (!Uses(role)) return;
        role.Player.Data.Role.CanUseKillButton = true;
        if (!role.IsLocal) return;
        _local = role;
        _dt = Time.fixedDeltaTime;
        _shown = _started = false;
        // 試合の途中で付いた時は本編がボタンの表示を決め直すのを待たない (使うボタンが出ている = 歩ける画面の時だけ)
        if (HudManager.InstanceExists)
        {
            var hud = HudManager.Instance;
            if (hud.UseButton && hud.UseButton.isActiveAndEnabled) hud.KillButton.ToggleVisible(true);
        }
    }

    internal static void Clear()
    {
        _local = null;
        _target = null;
        _shownT = -1f;
    }

    // 毎 FixedUpdate。ボタンが出ている間だけ待ち時間を減らし、0.1 秒ごとに狙う相手を選び直す。
    // ボタンが出直した時 (試合の始め・会議の後) に待ち時間を戻す
    internal static void Tick()
    {
        var role = _local;
        if (role == null) return;
        var lp = role.Player;
        if (!lp || lp.Data == null || lp.Data.IsDead || !HudManager.InstanceExists) return;
        var btn = HudManager.Instance.KillButton;
        if (!btn.isActiveAndEnabled)
        {
            _shown = false;
            return;
        }
        if (!_shown)
        {
            _shown = true;
            // 客は本編の役職が役職の電文より後に届くことがあるので、ボタンが出るたびに印を付け直す
            var rb = lp.Data.Role;
            if (rb && !rb.CanUseKillButton) rb.CanUseKillButton = true;
            ResetTimer(lp, _started ? Cooldown() : FirstCooldown);
            _started = true;
        }
        float t = lp.killTimer;
        if (t > 0f && lp.CanMove)
        {
            t = Math.Max(0f, t - _dt);
            lp.killTimer = t;
        }
        if (t == _shownT && _max == _shownMax) return;
        _shownT = t;
        _shownMax = _max;
        btn.SetCoolDown(t, _max);
    }

    // 本編はキルボタンを使える人の狙いを毎 FixedUpdate RoleBehaviour.FindClosestTarget で決め直すが、
    // クルーの役職では誰も返さない。キル役の時だけ自前で探した相手に差し替える (探すのは 0.1 秒ごと)
    internal static void OverrideTarget(RoleBehaviour rb, ref PlayerControl result)
    {
        var role = _local;
        if (role == null || rb.Player is not { } lp || lp.Pointer != role.Player.Pointer) return;
        if (_target is not null && (!_target || _target.Data == null || _target.Data.IsDead || _target.Data.Disconnected)) _target = null;
        if (++_scan >= ScanEvery)
        {
            _scan = 0;
            _target = ClosestTarget(lp);
        }
        result = _target;
    }

    // キル距離以内で一番近い、生きていてベントや昇降機の中にいない、間に壁の無い人
    private static PlayerControl ClosestTarget(PlayerControl lp)
    {
        if (!lp.CanMove) return null;
        float reach = GameManager.Instance.LogicOptions.GetKillDistance();
        float best = reach * reach;
        Vector2 me = lp.GetTruePosition();
        float mx = me.x, my = me.y;
        PlayerControl pick = null;
        var all = GameData.Instance.AllPlayers;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p == null || p.Disconnected || p.IsDead || p.PlayerId == lp.PlayerId) continue;
            var pc = p.Object;
            if (!pc || pc.inVent || pc.inMovingPlat || !pc.Visible) continue;
            Vector2 at = pc.GetTruePosition();
            float dx = at.x - mx, dy = at.y - my;
            float sq = dx * dx + dy * dy;
            if (sq >= best) continue;
            if (PhysicsHelpers.AnythingBetween(me, at, Constants.ShipAndObjectsMask, false)) continue;
            best = sq;
            pick = pc;
        }
        return pick;
    }

    private static float Cooldown() => GameManager.Instance.LogicOptions.GetKillCooldown();

    private static void ResetTimer(PlayerControl lp, float seconds)
    {
        _max = Math.Max(seconds, 0.01f);
        _shownT = -1f;
        lp.killTimer = seconds;
    }

    // ホストが通したキルが届いた時 (キルした本人の端末)
    internal static void OnMurder(PlayerControl killer)
    {
        var role = _local;
        if (role == null || killer.Pointer != role.Player.Pointer) return;
        ResetTimer(killer, Cooldown());
    }

    // ---- 押した人 ----

    internal static bool OnClick(KillButton button)
    {
        var role = RoleState.Local;
        if (!Uses(role)) return true;
        var lp = PlayerControl.LocalPlayer;
        var target = button.currentTarget;
        if (!button.isActiveAndEnabled || !target || button.isCoolingDown || lp.Data.IsDead || !lp.CanMove) return false;
        Request.Send(target.PlayerId); // ホストなら手元で判定する
        button.SetTarget(null);
        return false;
    }

    // ---- ホスト ----

    private static readonly RemoteCall<byte> Request = new("Kill.Request", Route.ToHost,
        (w, targetId) => w.Write(targetId), r => r.ReadByte(),
        (killer, targetId) =>
        {
            if (GameData.Instance) TryKill(killer, GameData.Instance.GetPlayerById(targetId)?.Object);
        });

    private static void TryKill(PlayerControl killer, PlayerControl target)
    {
        string why = Refuse(killer, target);
        if (why != null)
        {
            Plugin.Logger.LogWarning($"kill refused {(killer ? killer.PlayerId : -1)}->{(target ? target.PlayerId : -1)}: {why}");
            return;
        }
        LastKillMs[killer.PlayerId] = Environment.TickCount64;
        _killed[killer.PlayerId] = true;
        killer.RpcMurderPlayer(target, true);
    }

    private static string Refuse(PlayerControl killer, PlayerControl target)
    {
        if (AmongUsClient.Instance.IsGameOver || !ShipStatus.Instance || MeetingHud.Instance || ExileController.Instance) return "not now";
        if (!killer || killer.Data == null || killer.Data.IsDead || killer.Data.Disconnected) return "killer";
        if (killer.inVent || killer.inMovingPlat) return "killer busy";
        var role = RoleState.Of(killer);
        if (!Uses(role)) return "role";
        if (!target || target == killer || target.Data == null || target.Data.IsDead || target.Data.Disconnected) return "target";
        if (target.inVent || target.inMovingPlat) return "target busy";
        float reach = GameManager.Instance.LogicOptions.GetKillDistance() + DistanceSlack;
        Vector2 from = killer.GetTruePosition(), to = target.GetTruePosition();
        float dx = to.x - from.x, dy = to.y - from.y;
        if (dx * dx + dy * dy > reach * reach) return "far";
        if (PhysicsHelpers.AnythingBetween(from, to, Constants.ShipAndObjectsMask, false)) return "wall";
        // 前のキル (無ければ割り当て) からの経過。最初は試合の始めの待ち時間
        float cd = FirstCooldown;
        if (_killed[killer.PlayerId])
        {
            cd = GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.KillCooldown);
            role.ModifyKillCooldown(ref cd);
        }
        if ((Environment.TickCount64 - LastKillMs[killer.PlayerId]) / 1000f < cd - CooldownSlack) return "cooldown";
        return null;
    }
}

[HarmonyPatch(typeof(RoleBehaviour), nameof(RoleBehaviour.FindClosestTarget))]
internal static class KillTargetPatch
{
    public static void Postfix(RoleBehaviour __instance, ref PlayerControl __result) => KillAbility.OverrideTarget(__instance, ref __result);
}

[HarmonyPatch(typeof(KillButton), nameof(KillButton.DoClick))]
internal static class KillButtonClickPatch
{
    public static bool Prefix(KillButton __instance) => KillAbility.OnClick(__instance);
}

// 本編はインポスターにだけキルボタンを出すので、キルできる役職にも出す (試合の始め・会議の後・死んだ時に来る)
[HarmonyPatch(typeof(HudManager), nameof(HudManager.SetHudActive), typeof(PlayerControl), typeof(RoleBehaviour), typeof(bool))]
internal static class KillButtonShowPatch
{
    public static void Postfix(HudManager __instance, PlayerControl localPlayer, RoleBehaviour role, bool isActive)
    {
        var mine = RoleState.Local;
        if (mine is not { CanKill: true } || !role || role.IsImpostor || !role.CanUseKillButton) return;
        __instance.KillButton.ToggleVisible(isActive && localPlayer && localPlayer.Data != null && !localPlayer.Data.IsDead);
    }
}
