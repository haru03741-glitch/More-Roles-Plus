// Parallel medbay scans, skipping the walk to the medbay pad, hiding vent animations out of sight and
// cooldowns during minigames and pet visibility based on https://github.com/AU-Avengers/TOU-Mira (VanillaTweakOptions, GPL-3.0)
// Dead impostors cannot sabotage and the shield on the first victim of the previous match based on
// https://github.com/TheOtherRolesAU/TheOtherRoles (GPL-3.0)
using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Rules;

// 本編の細かい調整。行は本編の「ゲーム設定」タブの終わりに並べる (GameTabRows)。どれも既定は本編のまま
[Settings(Tab.General, "VanillaTweaks.Title", order: -50)]
public static class VanillaTweaks
{
    public static readonly BoolOpt ParallelScan = new("VanillaTweaks.ParallelScan", false) { OffTab = true };
    public static readonly BoolOpt ScanNoWalk = new("VanillaTweaks.ScanNoWalk", false) { OffTab = true };
    public static readonly BoolOpt TickInTasks = new("VanillaTweaks.TickInTasks", true) { OffTab = true };
    public static readonly BoolOpt HideVentOutOfSight = new("VanillaTweaks.HideVentOutOfSight", false) { OffTab = true };
    public static readonly BoolOpt DeadImpNoSabotage = new("VanillaTweaks.DeadImpNoSabotage", false) { OffTab = true };
    public static readonly BoolOpt FirstKillShield = new("VanillaTweaks.FirstKillShield", false) { OffTab = true };
    public static readonly BoolOpt NightCams = new("VanillaTweaks.NightCams", false) { OffTab = true };
    public static readonly ChoiceOpt PetsShown = new("VanillaTweaks.PetsShown", 0,
        new Text("VanillaTweaks.PetsAlways"), new Text("VanillaTweaks.PetsAlive"), new Text("VanillaTweaks.PetsOwn")) { OffTab = true };
    public static readonly BoolOpt PetGoesWithBody = new("VanillaTweaks.PetGoesWithBody", false) { OffTab = true };

    private static Opt[] _rows;
    public static Opt[] Rows => _rows ??= new Opt[] { ParallelScan, ScanNoWalk, TickInTasks, HideVentOutOfSight, DeadImpNoSabotage, FirstKillShield, NightCams, PetsShown, PetGoesWithBody };

    // 待ち時間を進めてよいか。本編は作業画面 (Minigame) を開いている間も動けない扱い (CanMove = false) になる
    internal static bool CooldownTicks(PlayerControl lp) => lp.CanMove || (TickInTasks && Minigame.Instance);

    // 本編のキルの待ち時間は作業画面を開いている間も進む (実測)。止める設定の時だけ本編が減らした分を足し戻す
    internal static void Tick()
    {
        Guard("first kill shield", TickShield);
        Guard("night vision cams", NightVisionCams.Tick);
        Guard("pets", TickPets);
        if (TickInTasks || !ShipStatus.Instance) return;
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.killTimer <= 0f || lp.Data == null || lp.Data.IsDead || !Minigame.Instance) return;
        var role = lp.Data.Role;
        if (!role || !role.CanUseKillButton || Roles.KillAbility.Owns) return;
        lp.killTimer += Time.fixedDeltaTime;
    }

    private static readonly System.Collections.Generic.HashSet<string> Failed = new();

    // 場面の切り替わりで本編の物が壊れていても、後ろの Tick (役職・電文) を止めない。同じ失敗は 1 回だけ記録する
    private static void Guard(string what, Action a)
    {
        try { a(); }
        catch (Exception e)
        {
            if (Failed.Add(what)) Plugin.Logger.LogError($"vanilla tweaks {what}: {e}");
        }
    }

    // 自分の視界に入っているか (視界の届く距離の中で、間に壁や物が無い)
    internal static bool InSight(PlayerControl p)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || p == null || p.AmOwner) return true;
        if (lp.Data != null && lp.Data.IsDead) return true;
        Vector2 from = lp.GetTruePosition(), d = p.GetTruePosition() - from;
        float mag = d.magnitude;
        if (!lp.lightSource || mag >= lp.lightSource.viewDistance) return false;
        return mag < 0.01f || !PhysicsHelpers.AnyNonTriggersBetween(from, d / mag, mag, Constants.ShipAndObjectsMask);
    }

    internal static bool BlocksSabotage(PlayerControl p) => DeadImpNoSabotage && p && p.Data != null && p.Data.IsDead;

    // ---- 前の試合の最初の犠牲者に盾 (最初の会議が始まるまで倒されない) ----
    // ホストが試合ごとに最初に倒された人を接続の番号で覚え、次の試合の船ができた時に全員へ配る
    private static int _nextShieldClient = -1; // ホスト: 次の試合で盾を付ける人の接続の番号
    private static bool _recorded;             // ホスト: この試合の最初の犠牲者を覚えたか
    private static byte _shield = 255;         // 全員: 今の試合で盾が付いている人
    private static int _outlineTick;
    private static Il2CppSystem.Nullable<Color> _outlineOn, _outlineOff;

    internal static readonly RemoteCall<byte> ShieldSet = new("VanillaTweaks.Shield", Route.HostToAll,
        (w, id) => w.Write(id), r => r.ReadByte(), (_, id) => SetShield(id));

    internal static bool Shielded(PlayerControl target) => _shield != 255 && target && target.PlayerId == _shield;

    // ホスト: 船ができた (試合の始め)
    internal static void OnShipStart()
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;
        byte id = 255;
        if (FirstKillShield && _nextShieldClient >= 0 && Options.GameModeSwitch.IsNormal)
            foreach (var p in PlayerControl.AllPlayerControls)
                if (p && !p.isDummy && p.OwnerId == _nextShieldClient && p.Data != null && !p.Data.Disconnected) { id = p.PlayerId; break; }
        _nextShieldClient = -1;
        _recorded = false;
        SetShield(id);
        ShieldSet.Send(id);
    }

    // ホスト: キルが通った
    internal static void OnMurder(PlayerControl killer, PlayerControl target)
    {
        if (_recorded || !FirstKillShield || !AmongUsClient.Instance || !AmongUsClient.Instance.AmHost || !ShipStatus.Instance) return;
        if (!target || killer == target) return;
        _recorded = true;
        if (!target.isDummy) _nextShieldClient = target.OwnerId;
    }

    // 最初の会議が始まったら外す (全員がそれぞれ)
    internal static void OnMeeting() => SetShield(255);

    // ---- ペットの見え方 (自分の端末の見た目だけ) ----
    private const int PetEvery = 30; // 0.6 秒ごとに見直す (本編は着せ替えのたびにペットを出し直す)
    private static int _petTick;
    private static readonly bool[] HasBody = new bool[256];
    private static readonly bool[] PetHidden = new bool[256]; // ここで隠したペット (出し直すのはこれだけ。本編が隠した物には触らない)
    private static int _petsHidden;

    private static void TickPets()
    {
        if (++_petTick < PetEvery) return;
        _petTick = 0;
        if (NightVisionCams.Active) return;
        int mode = ShipStatus.Instance && Options.GameModeSwitch.IsNormal ? PetsShown.Value : 0;
        bool withBody = mode == 0 && ShipStatus.Instance && PetGoesWithBody;
        if (mode == 0 && !withBody && _petsHidden == 0) return;
        if (withBody)
        {
            Array.Clear(HasBody, 0, HasBody.Length);
            foreach (var body in UnityEngine.Object.FindObjectsOfType<DeadBody>())
                if (body) HasBody[body.ParentId] = true;
        }
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.AmOwner || p.cosmetics == null || p.Data == null) continue;
            byte id = p.PlayerId;
            bool hide = mode switch
            {
                1 => p.Data.IsDead,
                2 => true,
                _ => withBody && p.Data.IsDead && !HasBody[id], // 会議で死体が片付いた後も出さない
            };
            if (hide == PetHidden[id]) continue;
            var pet = p.cosmetics.currentPet;
            if (hide)
            {
                if (!pet || !pet.Visible) continue; // 本編が隠している物は隠した扱いにしない
                p.cosmetics.TogglePet(false);
            }
            else p.cosmetics.TogglePet(true);
            PetHidden[id] = hide;
            _petsHidden += hide ? 1 : -1;
        }
    }

    internal static string DevShield(string arg)
    {
        if (byte.TryParse(arg, out byte id)) { SetShield(id); ShieldSet.Send(id); }
        return $"OK shield now={_shield} next={_nextShieldClient} recorded={_recorded}";
    }

    private static void SetShield(byte id)
    {
        if (_shield == id) return;
        Outline(_shield, false);
        _shield = id;
        _outlineTick = 0;
    }

    // 本編のキルの狙いの輪郭が上書きするので、盾の輪郭は 0.2 秒ごとに付け直す
    internal static void TickShield()
    {
        if (_shield == 255 || ++_outlineTick < 10) return;
        _outlineTick = 0;
        // 最初の会議の前に試合が終わった (ロビーへ戻った)。客はホストの電文が自分の船より先に届くことがあるので、試合が始まっている間は消さない
        if (AmongUsClient.Instance && AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.Started)
        {
            SetShield(255);
            return;
        }
        Outline(_shield, !NightVisionCams.Active); // 暗視のカメラでは誰か分かる輪郭を出さない
    }

    private static void Outline(byte id, bool on)
    {
        if (id == 255) return;
        var p = GameData.Instance ? GameData.Instance.GetPlayerById(id)?.Object : null;
        if (!p || p.cosmetics == null || p.Data == null || p.Data.IsDead) return;
        p.cosmetics.SetOutline(on, on ? _outlineOn ??= new Il2CppSystem.Nullable<Color>(new Color(0.2f, 0.55f, 1f, 1f))
                                      : _outlineOff ??= new Il2CppSystem.Nullable<Color>(new Color(0f, 0f, 0f, 0f)));
    }
}

// 医務室のスキャンを何人でも同時に: 自分の画面では順番待ちの列を空にして自分の番にする
[HarmonyPatch(typeof(MedScanMinigame), nameof(MedScanMinigame.FixedUpdate))]
internal static class ParallelScanPatch
{
    public static void Prefix(MedScanMinigame __instance)
    {
        if (!VanillaTweaks.ParallelScan) return;
        var lp = PlayerControl.LocalPlayer;
        var scan = __instance.medscan;
        if (!lp || scan == null) return;
        scan.CurrentUser = lp.PlayerId;
        scan.UsersList.Clear();
    }
}

// スキャンの前に台へ歩いて行かない: 歩く手順 (台へ・台の上の位置へ) を歩かずに終わった扱いにする
[HarmonyPatch]
internal static class ScanNoWalkPatch
{
    public static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var t in typeof(MedScanMinigame).GetNestedTypes())
            if (t.Name.Contains("WalkToPad") || t.Name.Contains("WalkToOffset"))
                yield return AccessTools.Method(t, "MoveNext");
    }

    public static bool Prefix(Il2CppObjectBase __instance, ref bool __result)
    {
        if (!VanillaTweaks.ScanNoWalk) return true;
        var mg = __instance.GetType().GetProperty("__4__this")?.GetValue(__instance) as MedScanMinigame;
        if (mg == null) return true;
        mg.state = mg.state switch
        {
            MedScanMinigame.PositionState.None => MedScanMinigame.PositionState.WalkingToPad,
            MedScanMinigame.PositionState.WalkingToPad => MedScanMinigame.PositionState.WalkingToOffset,
            _ => (MedScanMinigame.PositionState)(-1),
        };
        if (mg.state == (MedScanMinigame.PositionState)(-1)) mg.walking = null;
        __result = false;
        return false;
    }
}

// 見えていない所のベントの出入りでは蓋の動き (と音) を出さない
[HarmonyPatch(typeof(Vent))]
internal static class HideVentPatch
{
    [HarmonyPatch(nameof(Vent.EnterVent)), HarmonyPrefix]
    public static bool Enter(Vent __instance, PlayerControl pc) =>
        !VanillaTweaks.HideVentOutOfSight || !__instance.EnterVentAnim || VanillaTweaks.InSight(pc);

    [HarmonyPatch(nameof(Vent.ExitVent)), HarmonyPrefix]
    public static bool Exit(Vent __instance, PlayerControl pc, ref Il2CppSystem.Collections.IEnumerator __result)
    {
        if (!VanillaTweaks.HideVentOutOfSight || !__instance.ExitVentAnim || VanillaTweaks.InSight(pc)) return true;
        __result = Effects.Wait(0f); // 呼び出し元はこの戻りを待つので空の待ちを返す
        return false;
    }
}

// 死んだインポスターのサボタージュ: ボタンを出さず、地図はふつうの地図で開く
[HarmonyPatch(typeof(HudManager), nameof(HudManager.SetHudActive), typeof(PlayerControl), typeof(RoleBehaviour), typeof(bool))]
internal static class DeadSabotageButtonPatch
{
    public static void Postfix(HudManager __instance, PlayerControl localPlayer)
    {
        if (VanillaTweaks.BlocksSabotage(localPlayer) && __instance.SabotageButton) __instance.SabotageButton.ToggleVisible(false);
    }
}

[HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.ShowSabotageMap))]
internal static class DeadSabotageMapPatch
{
    public static bool Prefix(MapBehaviour __instance)
    {
        if (!VanillaTweaks.BlocksSabotage(PlayerControl.LocalPlayer)) return true;
        __instance.ShowNormalMap();
        return false;
    }
}

// 盾の付いた人へのキル: 押す側でも止め (本編のキルボタン)、ホストも本編のキルの確かめで断る
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckMurder))]
internal static class ShieldCheckMurderPatch
{
    public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
    {
        if (!VanillaTweaks.Shielded(target) || !AmongUsClient.Instance.AmHost) return true;
        Plugin.Logger.LogInfo($"first kill shield: {__instance.PlayerId}->{target.PlayerId} refused");
        return false;
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
internal static class ShieldMurderPatch
{
    public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target, [HarmonyArgument(1)] MurderResultFlags resultFlags)
    {
        if ((resultFlags & MurderResultFlags.Succeeded) == 0) return;
        try { VanillaTweaks.OnMurder(__instance, target); }
        catch (Exception e) { Plugin.Logger.LogError($"first kill shield: {e}"); }
    }
}
