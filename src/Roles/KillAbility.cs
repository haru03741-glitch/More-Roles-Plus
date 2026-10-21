using System;
using HarmonyLib;
using Hazel;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 第三陣営の数 (最少〜最多・そのうちキル役の最多) と、キル役の本編のインポスターと同じ並びの設定 (キルのクールダウン・視界・キルの距離)。
// 行は本編の「ゲーム設定」タブのインポスターの項目の下に並べる (GameTabRows)。MRP のタブには並べない。
// 数は人数の帯 (TeamSizes) を使っている時、帯の値が写る
[Settings(Tab.General, "NeutralKillSettings.Title", order: -98)]
public static class NeutralKillSettings
{
    public static readonly IntOpt MinNeutrals = new("NeutralKillSettings.MinNeutrals", 0, 0, Crowded.MaxPlayers) { OffTab = true };
    public static readonly IntOpt MaxNeutrals = new("NeutralKillSettings.MaxNeutrals", 2, 0, Crowded.MaxPlayers) { OffTab = true };
    public static readonly IntOpt MaxKillers = new("NeutralKillSettings.MaxKillers", 1, 0, Crowded.MaxPlayers) { OffTab = true };
    // 本編のインポスターの行と並ぶので、値は本編の書き方 (「25.0秒」)
    public static readonly FloatOpt KillCooldown = new("NeutralKillSettings.KillCooldown", 25f, 2.5f, 60f, 2.5f, "s")
        { OffTab = true, Format = v => VanillaGame.Format(StringNames.GameKillCooldown, v) };
    public static readonly FloatOpt Vision = new("NeutralKillSettings.Vision", 1.5f, 0.25f, 5f, 0.25f, "x")
        { OffTab = true, Format = v => VanillaGame.Format(StringNames.GameImpostorLight, v) };
    public static readonly ChoiceOpt KillDistance = new("NeutralKillSettings.KillDistance", 1,
        new Text("NeutralKillSettings.Short"), new Text("NeutralKillSettings.Medium"), new Text("NeutralKillSettings.Long")) { OffTab = true };

    public static bool Applies(RoleBase role) => role is { Team: Team.Neutral, CanKill: true };

    // 本編のキルの距離の表 (短い / 普通 / 長い)。毎回 il2cpp の配列を読まないように最初の 1 回で写す
    private static float[] _distances;

    public static float Distance()
    {
        if (_distances == null)
        {
            var t = AmongUs.GameOptions.NormalGameOptionsV12.KillDistances;
            if (t == null || t.Length == 0) _distances = new[] { 1f, 1.8f, 2.5f };
            else
            {
                _distances = new float[t.Length];
                for (int i = 0; i < t.Length; i++) _distances[i] = t[i];
            }
        }
        return _distances[Math.Clamp(KillDistance.Value, 0, _distances.Length - 1)];
    }
}

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
        var hud = Vanilla.Hud;
        if (hud)
        {
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
        var btn = Vanilla.Hud.KillButton;
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
        if (_target is not null && (!_target || _target.Data == null || _target.Data.IsDead || _target.Data.Disconnected)) _target = null;
        if (++_scan >= ScanEvery)
        {
            _scan = 0;
            _target = lp.CanMove ? Targeting.ClosestPlayer(lp, Reach(role)) : null;
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

    // 本編はキルボタンを使える人の狙いを毎 FixedUpdate KillButton.SetTarget で決め直すが、クルーの役職では
    // 誰も渡さない。キル役の時だけ Tick で探した相手に差し替える (壊れた・死んだ相手は渡さない)
    internal static void OverrideTarget(ref PlayerControl target)
    {
        if (_local == null) return;
        var t = _target;
        target = t is not null && t && t.Data != null && !t.Data.IsDead ? t : null;
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

    // キルの届く距離。第三陣営のキル役は第三陣営の設定、それ以外は本編の設定
    internal static float Reach(RoleBase role) =>
        NeutralKillSettings.Applies(role) ? NeutralKillSettings.Distance() : GameManager.Instance.LogicOptions.GetKillDistance();

    internal static float Reach(PlayerControl p) => Reach(RoleState.Of(p));

    // インポスター以外のキル役か (ホストがダミーを動かす時の振り分け用)
    internal static bool UsesFor(PlayerControl p) => Uses(RoleState.Of(p));

    // 断った理由を返す (通したら null)
    internal static string TryKill(PlayerControl killer, PlayerControl target)
    {
        string why = Refuse(killer, target);
        if (why != null)
        {
            Plugin.Logger.LogWarning($"kill refused {(killer ? killer.PlayerId : -1)}->{(target ? target.PlayerId : -1)}: {why}");
            return why;
        }
        LastKillMs[killer.PlayerId] = Environment.TickCount64;
        _killed[killer.PlayerId] = true;
        killer.RpcMurderPlayer(target, true);
        return null;
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
        string far = Targeting.Unreachable(killer, target, Reach(role) + DistanceSlack);
        if (far != null) return far;
        // 前のキル (無ければ割り当て) からの経過。最初は試合の始めの待ち時間
        float cd = FirstCooldown;
        if (_killed[killer.PlayerId])
        {
            cd = NeutralKillSettings.Applies(role) ? NeutralKillSettings.KillCooldown : GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.KillCooldown);
            role.ModifyKillCooldown(ref cd);
            var addons = RoleState.AddonsOf(killer.PlayerId);
            for (int i = 0; i < addons.Count; i++) addons[i].ModifyKillCooldown(ref cd);
        }
        if ((Environment.TickCount64 - LastKillMs[killer.PlayerId]) / 1000f < cd - CooldownSlack && !Dev.DevCooldown.Skips(killer)) return "cooldown";
        return null;
    }
}

// RoleBehaviour.FindClosestTarget には当てない: 本体が小さく、ゲームの中で同じ機械語の別の関数とまとめられていて、
// 当てると関係ない呼び出し (引数がオブジェクトでない) まで来て落ちる
[HarmonyPatch(typeof(KillButton), nameof(KillButton.SetTarget))]
internal static class KillTargetPatch
{
    // 本編は起動中 (キルボタンがまだ無い時) にも this が null のまま呼ぶ。そのまま通すとパッチの中継が null を
    // 本編へ渡す所で例外になるので、本編を呼ばずに返す
    public static bool Prefix(KillButton __instance, [HarmonyArgument(0)] ref PlayerControl target)
    {
        if (__instance is null) return false;
        KillAbility.OverrideTarget(ref target);
        return true;
    }
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
