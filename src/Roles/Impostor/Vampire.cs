// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Vampire.cs, CustomObject/BloodStain.cs,
// Roles/Ability/HideInAdminAbility.cs, Patches/DevicesPatch.cs (GPL-3.0)
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// ヴァンパイア (インポスター): キルすると相手は決まった秒数たってから死ぬ (会議が呼ばれたらその時に死ぬ)。
// 噛まれた人は歩いた跡に血を落とし、その血は次の会議の後から決まった回数の会議の間だけ見える。
// 設定で 1 度だけ眷属を作れる。ヴァンパイアと眷属は停電を直せず、端末を使えず、アドミンに映らず、バイタルでは死んでも生きて見える
public sealed class Vampire : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Vampire.Name");
    public override string Reading => "う゛ぁんぱいあ";
    public override Text Blurb => new("Vampire.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Vampire.Bite"), new("Vampire.NightFall", "snr_sidekick") };

    public static readonly FloatOpt AbsorptionCooldown = new("Vampire.AbsorptionCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt KillDelay = new("Vampire.Delay", 10f, 1f, 30f, 1f, "s");
    public static readonly IntOpt BloodstainTurns = new("Vampire.BloodstainDuration", 1, 1, 5);
    public static readonly BoolOpt BlackBloodstains = new("Vampire.BlackBloodstains", false);
    public static readonly BoolOpt CreateDependents = new("Vampire.CreateDependents", false);
    public static readonly FloatOpt NightFallCooldown = new("Vampire.NightFallCooldown", 30f, 2.5f, 60f, 2.5f, "s") { When = () => CreateDependents };
    public static readonly FloatOpt DependentKillCooldown = new("Vampire.DependentKillCooldown", 30f, 2.5f, 60f, 2.5f, "s") { When = () => CreateDependents };
    public static readonly BoolOpt DependentCanVent = new("Vampire.DependentCanVent", true) { When = () => CreateDependents };
    public static readonly BoolOpt DependentReverseVision = new("Vampire.DependentReverseVision", true) { When = () => CreateDependents };
    public static readonly BoolOpt DependentImpostorVisionInLightsOff = new("Vampire.DependentImpostorVisionInLightsOff", true) { When = () => CreateDependents && DependentReverseVision };
    public static readonly BoolOpt CannotFixSabotage = new("Vampire.CannotFixSabotage", true);
    public static readonly BoolOpt InvisibleOnAdmin = new("Vampire.InvisibleOnAdmin", true);
    public static readonly BoolOpt NoDeathOnVitals = new("Vampire.NoDeathOnVitals", true);
    public static readonly BoolOpt CannotUseDevice = new("Vampire.CannotUseDevice", true);

    // 噛んでいる相手 (全員の端末で同じように持つ)
    private int _target = -1;
    private float _delay, _stainTimer;
    private int _dependent = -1;

    // まだ見えない血 (噛まれた人ごと) と、見えている血 (残りの会議の数)
    private readonly Dictionary<byte, GameObject> _hidden = new();
    private readonly List<(GameObject root, int turns)> _shown = new();

    public override void ModifyKillCooldown(ref float seconds) => seconds = AbsorptionCooldown;

    public override void OnAssigned()
    {
        if (CreateDependents)
            AddAbility(new Ability(new Text("Vampire.NightFall"), "snr_sidekick")
            {
                Target = AbilityTarget.Player,
                Cooldown = () => NightFallCooldown,
                MaxUses = () => 1,
                CanTarget = (_, t) => !PortKit.IsImpostor(t),
                OnUsed = (_, target) =>
                {
                    _dependent = target.PlayerId;
                    if (AmongUsClient.Instance.AmHost) RoleState.Change(target, RoleState.Proto<VampireDependent>());
                },
            });
        Lifespan.OnRelease(() =>
        {
            foreach (var kv in _hidden) if (kv.Value) Object.Destroy(kv.Value);
            foreach (var s in _shown) if (s.root) Object.Destroy(s.root);
            _hidden.Clear();
            _shown.Clear();
        });
    }

    internal PlayerControl Dependent => _dependent >= 0 ? PortKit.ById((byte)_dependent) : null;

    // 自分の眷属を作ったヴァンパイア
    internal static Vampire MasterOf(byte dependent)
    {
        foreach (var r in RoleState.All)
            if (r is Vampire v && v._dependent == dependent) return v;
        return null;
    }

    // キルの代わりに噛む
    [HostOnly]
    private void OnKill(BeforeMurderEvent e)
    {
        if (e.Canceled || !e.Killer || e.Killer.PlayerId != PlayerId || e.Target.PlayerId == PlayerId) return;
        e.Cancel("vampire bite");
        if (_target >= 0 || e.Target.PlayerId == _dependent) return;
        BiteCall.Send((PlayerId, e.Target.PlayerId));
        Bite(e.Target.PlayerId);
    }

    private static readonly RemoteCall<(byte vampire, byte target)> BiteCall = new("Vampire.Bite", Route.HostToAll,
        (w, m) => { w.Write(m.vampire); w.Write(m.target); }, r => (r.ReadByte(), r.ReadByte()),
        (_, m) => { if (RoleState.Of(m.vampire) is Vampire v) v.Bite(m.target); });

    // 全員の端末で: 噛んだ
    private void Bite(byte target)
    {
        _target = target;
        _delay = KillDelay;
        _stainTimer = 0f;
        if (!_hidden.ContainsKey(target))
        {
            var root = new GameObject($"MrpBloodStains_{target}");
            root.SetActive(false);
            _hidden[target] = root;
        }
        // 眷属のキルの待ち時間を戻す
        var dep = Dependent;
        if (dep && dep.AmOwner) dep.SetKillTimer(DependentKillCooldown);
    }

    public override void OnFixedUpdate()
    {
        if (_target < 0) return;
        var t = PortKit.ById((byte)_target);
        if (!PortKit.Alive(t))
        {
            _target = -1;
            return;
        }
        // 噛んでいる間はキルの待ち時間を進めない
        if (IsLocal) Player.SetKillTimer(AbsorptionCooldown);
        if (MeetingHud.Instance) return;
        if ((_delay -= Time.fixedDeltaTime) <= 0f)
        {
            Execute(t);
            return;
        }
        if ((_stainTimer -= Time.fixedDeltaTime) <= 0f)
        {
            Stain(t, 1f);
            _stainTimer = 0.1f;
        }
    }

    // 全員の端末で: 噛まれた人が死ぬ (ホストが倒す)
    private void Execute(PlayerControl t)
    {
        Stain(t, 3f);
        _target = -1;
        if (AmongUsClient.Instance.AmHost) PortKit.HostKill(t, t);
        if (IsLocal) Player.SetKillTimer(AbsorptionCooldown);
    }

    // 会議が呼ばれたら、噛まれた人はその時に死ぬ
    private void OnMeetingCalled(MeetingCalledEvent e)
    {
        var t = _target >= 0 ? PortKit.ById((byte)_target) : null;
        if (PortKit.Alive(t)) Execute(t);
    }

    // 会議の後: 見える血の残りを減らし、新しい血を見せる
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        for (int i = _shown.Count - 1; i >= 0; i--)
        {
            var (root, turns) = _shown[i];
            if (turns <= 0)
            {
                if (root) Object.Destroy(root);
                _shown.RemoveAt(i);
            }
            else _shown[i] = (root, turns - 1);
        }
        foreach (var kv in _hidden)
        {
            if (!kv.Value) continue;
            kv.Value.SetActive(true);
            _shown.Add((kv.Value, BloodstainTurns - 1));
        }
        _hidden.Clear();
    }

    private void Stain(PlayerControl t, float scale)
    {
        if (!_hidden.TryGetValue(t.PlayerId, out var root) || !root) return;
        var go = new GameObject("MrpBloodStain");
        go.transform.SetParent(root.transform, false);
        Vector2 at = t.GetTruePosition() + Random.insideUnitCircle * 0.15f;
        go.transform.position = new Vector3(at.x, at.y, at.y / 1000f + 0.01f);
        var lr = go.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.useWorldSpace = false;
        float size = Random.Range(0.08f, 0.16f) * scale;
        lr.SetPosition(0, new Vector3(-size * 0.5f, 0f, 0f));
        lr.SetPosition(1, new Vector3(size * 0.5f, 0f, 0f));
        lr.startWidth = lr.endWidth = size;
        lr.numCapVertices = 4;
        lr.material = new Material(Shader.Find("Sprites/Default"));
        var c = BlackBloodstains ? new Color(0.2f, 0.2f, 0.2f, 0.85f) : new Color(179f / 255f, 0f, 0f, 0.85f);
        lr.startColor = lr.endColor = c;
    }

    // 眷属がキルしたら自分のキルの待ち時間を戻す
    [LocalOnly]
    private void OnMurdered(PlayerMurderedEvent e)
    {
        if (e.Killer && e.Killer.PlayerId == _dependent && e.Killer.PlayerId != PlayerId) Player.SetKillTimer(AbsorptionCooldown);
    }

    public override string NameMark(PlayerControl seen) =>
        seen.PlayerId == _dependent ? "<color=#FF1919>◆</color>" : null;

    // ヴァンパイアか眷属か
    internal static bool IsKin(PlayerControl p) => RoleState.Of(p) is Vampire or VampireDependent;

    internal static bool LocalIsKin => PlayerControl.LocalPlayer && IsKin(PlayerControl.LocalPlayer);
}

// 停電を直せない
[HarmonyPatch(typeof(Console), nameof(Console.CanUse))]
internal static class VampireConsolePatch
{
    public static void Postfix(Console __instance, ref float __result, [HarmonyArgument(1)] ref bool canUse, [HarmonyArgument(2)] ref bool couldUse)
    {
        if (!Vampire.CannotFixSabotage || !Vampire.LocalIsKin || __instance.TaskTypes == null) return;
        foreach (var t in __instance.TaskTypes)
        {
            if (t != TaskTypes.FixLights) continue;
            canUse = couldUse = false;
            __result = float.MaxValue;
            return;
        }
    }
}

// アドミン・カメラ・バイタル・ドアログを使えない
[HarmonyPatch(typeof(MapConsole), nameof(MapConsole.CanUse))]
internal static class VampireMapConsolePatch
{
    public static void Postfix(ref float __result, [HarmonyArgument(1)] ref bool canUse, [HarmonyArgument(2)] ref bool couldUse)
    {
        if (!Vampire.CannotUseDevice || !Vampire.LocalIsKin) return;
        canUse = couldUse = false;
        __result = float.MaxValue;
    }
}

[HarmonyPatch(typeof(SystemConsole), nameof(SystemConsole.CanUse))]
internal static class VampireSystemConsolePatch
{
    public static void Postfix(SystemConsole __instance, ref float __result, [HarmonyArgument(1)] ref bool canUse, [HarmonyArgument(2)] ref bool couldUse)
    {
        if (!Vampire.CannotUseDevice || !Vampire.LocalIsKin) return;
        var mg = __instance.MinigamePrefab;
        if (!mg) return;
        string type = mg.GetIl2CppType().Name;
        if (!type.Contains("Vitals") && !type.Contains("SecurityLog") && !type.Contains("Surveillance")) return;
        canUse = couldUse = false;
        __result = float.MaxValue;
    }
}

// バイタルでは死んでも生きて見える
[HarmonyPatch(typeof(VitalsPanel), nameof(VitalsPanel.SetDead))]
internal static class VampireVitalsDeadPatch
{
    public static bool Prefix(VitalsPanel __instance) =>
        !Vampire.NoDeathOnVitals || __instance.PlayerInfo == null || !Vampire.IsKin(__instance.PlayerInfo.Object);
}

[HarmonyPatch(typeof(VitalsPanel), nameof(VitalsPanel.SetDisconnected))]
internal static class VampireVitalsDisconnectPatch
{
    public static bool Prefix(VitalsPanel __instance) =>
        !Vampire.NoDeathOnVitals || __instance.PlayerInfo == null || !Vampire.IsKin(__instance.PlayerInfo.Object);
}

// アドミンに映らない: 本編が数え直した直後に、部屋にいるヴァンパイアと眷属の分を引く
[HarmonyPatch(typeof(MapCountOverlay), nameof(MapCountOverlay.Update))]
internal static class VampireAdminPatch
{
    public static void Postfix(MapCountOverlay __instance)
    {
        if (!Vampire.InvisibleOnAdmin || __instance.isSab || __instance.timer != 0f) return;
        var ship = ShipStatus.Instance;
        if (!ship) return;
        foreach (var area in __instance.CountAreas)
        {
            if (!area || !ship.FastRooms.TryGetValue(area.RoomType, out var room) || !room || !room.roomArea) continue;
            int hidden = 0;
            foreach (var p in PlayerControl.AllPlayerControls)
                if (PortKit.Alive(p) && Vampire.IsKin(p) && room.roomArea.OverlapPoint(p.GetTruePosition())) hidden++;
            if (hidden == 0) continue;
            int shown = 0;
            foreach (var icon in area.myIcons) if (icon && icon.gameObject.activeSelf) shown++;
            area.UpdateCount(Mathf.Max(0, shown - hidden));
        }
    }
}
