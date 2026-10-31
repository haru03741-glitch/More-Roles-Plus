// Ported from https://github.com/ykundesu/SuperNewRoles (GPL-3.0):
// SuperNewRoles/Roles/Ability/CustomTaskAbility.cs, SuperNewRoles/ModHelpers.cs (FixingSabotage), SuperNewRoles/Modules/Arrow.cs
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Net;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// SNR から移した役職の共通部品
public static class SnrKit
{
    // 自分の視界をこの広さに決める (負 = 決めない)。取り憑かれた時などに。役職の視界の計算の後に掛ける
    public static float FixedVision = -1f;

    // 自分の視界を最後に直す処理 (フクロウの特殊停電など)。引数は今の広さ、返り値が新しい広さ
    public static readonly List<System.Func<float, float>> VisionFilters = new();

    internal static float ApplyVision(NetworkedPlayerInfo player, float result)
    {
        if (FixedVision < 0f && VisionFilters.Count == 0) return result; // 何も掛かっていない時は Unity の物に触らない
        if (player.IsDead || !player.Object || !player.Object.AmOwner) return result;
        if (FixedVision >= 0f) result = FixedVision;
        for (int i = 0; i < VisionFilters.Count; i++) result = VisionFilters[i](result);
        return result;
    }

    // ---- タスク ----

    // 自分の端末で: 自分のタスクを配り直す (共通・短い・長いの数)。全部 0 ならタスク無し
    public static void AssignOwnTasks(int common, int shortTasks, int longTasks)
    {
        var lp = PlayerControl.LocalPlayer;
        var ship = ShipStatus.Instance;
        if (!lp || !ship || lp.Data == null) return;
        var list = new Il2CppSystem.Collections.Generic.List<byte>();
        if (common + shortTasks + longTasks > 0)
        {
            var types = new Il2CppSystem.Collections.Generic.HashSet<TaskTypes>();
            Add(ship, ship.CommonTasks, common, list, types);
            Add(ship, ship.ShortTasks, shortTasks, list, types);
            Add(ship, ship.LongTasks, longTasks, list, types);
        }
        var ids = new byte[list.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = list[i];
        TasksCall.Send(ids);
        SetTasks(lp, ids);
    }

    // 本編の設定のタスクの数で配り直す
    public static void AssignOwnVanillaTasks()
    {
        var o = GameOptionsManager.Instance.CurrentGameOptions;
        AssignOwnTasks(o.GetInt(AmongUs.GameOptions.Int32OptionNames.NumCommonTasks),
            o.GetInt(AmongUs.GameOptions.Int32OptionNames.NumShortTasks), o.GetInt(AmongUs.GameOptions.Int32OptionNames.NumLongTasks));
    }

    private static void Add(ShipStatus ship, Il2CppReferenceArray<NormalPlayerTask> all, int count,
        Il2CppSystem.Collections.Generic.List<byte> into, Il2CppSystem.Collections.Generic.HashSet<TaskTypes> types)
    {
        if (all == null || all.Length == 0 || count <= 0) return;
        var shuffled = new Il2CppSystem.Collections.Generic.List<NormalPlayerTask>();
        foreach (var t in all.ToArray().OrderBy(_ => Random.value)) shuffled.Add(t);
        int start = 0;
        ship.AddTasksFromList(ref start, count, into, types, shuffled);
    }

    private static readonly RemoteCall<byte[]> TasksCall = new("SnrKit.Tasks", Route.Anyone,
        (w, ids) => w.WriteBytesAndSize(ids), r => r.ReadBytesAndSize(), (sender, ids) => SetTasks(sender, ids));

    private static void SetTasks(PlayerControl p, byte[] ids)
    {
        if (!p || p.Data == null) return;
        p.Data.SetTasks(ids);
    }

    // ---- サボタージュの修理 ----

    public static bool IsSabotage(TaskTypes t) => t is TaskTypes.FixLights or TaskTypes.RestoreOxy or TaskTypes.ResetReactor
        or TaskTypes.StopCharles or TaskTypes.ResetSeismic or TaskTypes.FixComms or TaskTypes.MushroomMixupSabotage;

    // 何かのサボタージュが起きているか (includeMixup = キノコの混乱も数える)
    public static bool SabotageActive(bool includeMixup)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.myTasks == null) return false;
        foreach (var t in lp.myTasks)
            if (t && IsSabotage(t.TaskType) && (includeMixup || t.TaskType != TaskTypes.MushroomMixupSabotage)) return true;
        return false;
    }

    // 自分の端末から: 起きているサボタージュを全部直す (ignoreMixup = キノコの混乱は直さない)
    public static void FixSabotage(bool ignoreMixup)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || lp.myTasks == null) return;
        var kinds = new List<TaskTypes>();
        foreach (var t in lp.myTasks) if (t && IsSabotage(t.TaskType)) kinds.Add(t.TaskType);
        if (kinds.Contains(TaskTypes.MushroomMixupSabotage))
        {
            if (ignoreMixup) return;
            kinds.Clear();
            kinds.Add(TaskTypes.MushroomMixupSabotage);
        }
        foreach (var k in kinds)
        {
            if (AmongUsClient.Instance.AmHost) HostFix(k);
            else FixCall.Send((byte)k);
        }
    }

    private static readonly RemoteCall<byte> FixCall = new("SnrKit.Fix", Route.ToHost,
        (w, k) => w.Write(k), r => r.ReadByte(), (_, k) => HostFix((TaskTypes)k));

    private static readonly RemoteCall<byte> LightCall = new("SnrKit.FixLight", Route.HostToAll,
        (w, _) => w.Write((byte)0), r => r.ReadByte(), (_, _) => FixLight());

    private static void FixLight()
    {
        if (!ShipStatus.Instance || !ShipStatus.Instance.Systems.TryGetValue(SystemTypes.Electrical, out var s)) return;
        var sw = s.TryCast<SwitchSystem>();
        if (sw != null) sw.ActualSwitches = sw.ExpectedSwitches;
    }

    private static void HostFix(TaskTypes k)
    {
        var ship = ShipStatus.Instance;
        if (!ship || !AmongUsClient.Instance.AmHost) return;
        switch (k)
        {
            case TaskTypes.FixLights:
                LightCall.Send(0);
                FixLight();
                break;
            case TaskTypes.RestoreOxy:
                ship.RpcUpdateSystem(SystemTypes.LifeSupp, 0 | 64);
                ship.RpcUpdateSystem(SystemTypes.LifeSupp, 1 | 64);
                break;
            case TaskTypes.ResetReactor:
                ship.RpcUpdateSystem(SystemTypes.Reactor, 16);
                break;
            case TaskTypes.StopCharles:
                ship.RpcUpdateSystem(SystemTypes.HeliSabotage, 0 | 16);
                ship.RpcUpdateSystem(SystemTypes.HeliSabotage, 1 | 16);
                break;
            case TaskTypes.ResetSeismic:
                ship.RpcUpdateSystem(SystemTypes.Laboratory, 16);
                break;
            case TaskTypes.FixComms:
                ship.RpcUpdateSystem(SystemTypes.Comms, 16 | 0);
                ship.RpcUpdateSystem(SystemTypes.Comms, 16 | 1);
                break;
            case TaskTypes.MushroomMixupSabotage:
                ship.RpcUpdateSystem(SystemTypes.MushroomMixupSabotage, 16 | 1);
                break;
        }
    }

    // ---- 死体 ----

    // その人の死体を作って置く (全員の端末で手元に。通報もできる)
    public static DeadBody SpawnBody(PlayerControl owner, Vector3 at)
    {
        var body = Object.Instantiate(GameManager.Instance.deadBodyPrefab[0]);
        body.enabled = true;
        body.gameObject.SetActive(true);
        body.ParentId = owner.PlayerId;
        foreach (var r in body.bodyRenderers) owner.SetPlayerMaterialColors(r);
        owner.SetPlayerMaterialColors(body.bloodSplatter);
        var lp = PlayerControl.LocalPlayer;
        Vector3 pos = at + (lp && lp.KillAnimations != null && lp.KillAnimations.Length > 0 ? lp.KillAnimations[0].BodyOffset : Vector3.zero);
        pos.z = pos.y / 1000f;
        body.transform.position = pos;
        return body;
    }
}

// 役職がこれを持つと、本編の役職 (インポスター・インポスターの幽霊) が許さなくてもタスクの道具を使える
public interface ITaskDoer { }

// SNR の ConsoleCanUsePatch: タスクをする役職なら、本編の役職の判定を飛ばして距離と壁だけで決める
[HarmonyLib.HarmonyPatch(typeof(Console), nameof(Console.CanUse))]
internal static class TaskDoerConsolePatch
{
    public static bool Prefix(Console __instance, ref float __result, [HarmonyLib.HarmonyArgument(0)] NetworkedPlayerInfo pc,
        [HarmonyLib.HarmonyArgument(1)] out bool canUse, [HarmonyLib.HarmonyArgument(2)] out bool couldUse)
    {
        canUse = couldUse = false;
        if (__instance.AllowImpostor || __instance.TaskTypes == null || __instance.TaskTypes.Length == 0) return true;
        if (pc == null || !pc.Object || RoleState.Of(pc.PlayerId) is not ITaskDoer) return true;
        if (pc.Role && pc.Role.CanUse(__instance.Cast<IUsable>())) return true;
        __result = float.MaxValue;
        var player = pc.Object;
        Vector2 at = player.GetTruePosition();
        Vector3 cp = __instance.transform.position;
        couldUse = __instance.FindTask(player) != null && player.CanMove
            && (!__instance.onlySameRoom || __instance.InRoom(at)) && (!__instance.onlyFromBelow || at.y < cp.y);
        if (!couldUse) return false;
        float d = Vector2.Distance(at, cp);
        canUse = d <= __instance.UsableDistance;
        if (canUse && __instance.checkWalls) canUse = !PhysicsHelpers.AnythingBetween(at, cp, Constants.ShadowMask, false);
        __result = d;
        return false;
    }
}

// 画面の端に出る、ある場所への矢印 (SNR の Arrow)。自分の端末だけで描く
public sealed class WorldArrow
{
    private readonly GameObject _go;
    private readonly SpriteRenderer _rend;

    public WorldArrow(Lifespan life, Color color)
    {
        _go = new GameObject("MrpArrow");
        _go.layer = 5;
        _rend = _go.AddComponent<SpriteRenderer>();
        _rend.sprite = ButtonIcons.Get("snr_arrowhead");
        _rend.color = color;
        life?.OnRelease(Destroy);
    }

    public bool Alive => _go;

    public void Destroy()
    {
        if (_go) Object.Destroy(_go);
    }

    public void SetActive(bool on)
    {
        if (_go && _go.activeSelf != on) _go.SetActive(on);
    }

    // 目標の場所へ向ける。画面に入っていればその上に、外なら画面の端に出す
    public void Update(Vector3 target)
    {
        var cam = Camera.main;
        if (!_go || !cam) return;
        Vector3 vp = cam.WorldToViewportPoint(target);
        bool inside = vp.x is > 0.05f and < 0.95f && vp.y is > 0.05f and < 0.95f && vp.z > 0f;
        Vector3 center = cam.transform.position;
        Vector2 dir = ((Vector2)(target - center)).normalized;
        Vector3 at;
        if (inside) at = target + new Vector3(0f, 0.6f, 0f);
        else
        {
            vp.x = Mathf.Clamp(vp.x, 0.06f, 0.94f);
            vp.y = Mathf.Clamp(vp.y, 0.06f, 0.94f);
            at = cam.ViewportToWorldPoint(vp);
        }
        _go.transform.position = new Vector3(at.x, at.y, -500f);
        float angle = inside ? -90f : Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        _go.transform.rotation = Quaternion.Euler(0f, 0f, angle);
        _go.transform.localScale = Vector3.one * 0.4f;
    }
}

// 死体への矢印 (SNR の DeadBodyArrowsAbility)。自分の端末だけで描く。毎フレーム Update を呼ぶ
public sealed class BodyArrows
{
    private readonly Lifespan _life;
    private readonly Color _color;
    private readonly Dictionary<byte, WorldArrow> _arrows = new();

    // 矢印の色: 0 = 決めた色・1 = 死体の明暗・2 = 死体の色
    public int Mode;

    public BodyArrows(Lifespan life, Color color)
    {
        _life = life;
        _color = color;
    }

    // 死体の一覧は 0.25 秒ごとにだけ探し直す (場面の全部を探すので毎フレームはしない)
    private readonly List<DeadBody> _bodies = new();
    private readonly HashSet<byte> _seen = new();
    private float _scanLeft;

    public void Update(bool show)
    {
        show &= !MeetingHud.Instance && !ExileController.Instance;
        _seen.Clear();
        if (show)
        {
            if ((_scanLeft -= Time.fixedDeltaTime) <= 0f)
            {
                _scanLeft = 0.25f;
                _bodies.Clear();
                foreach (var b in Object.FindObjectsOfType<DeadBody>()) _bodies.Add(b);
            }
            foreach (var b in _bodies)
            {
                if (!b || !b.gameObject.activeInHierarchy) continue;
                _seen.Add(b.ParentId);
                if (!_arrows.TryGetValue(b.ParentId, out var a) || !a.Alive)
                    _arrows[b.ParentId] = a = new WorldArrow(_life, SeerKit.BodyColor(PortKit.ById(b.ParentId), Mode) ?? _color);
                a.SetActive(true);
                a.Update(b.transform.position);
            }
        }
        else _scanLeft = 0f;
        foreach (var kv in _arrows) if (!_seen.Contains(kv.Key)) kv.Value.SetActive(false);
    }
}

// シーア / イビルシーアの共通 (SNR の SeerAbility)。死んだ瞬間に画面が光り、死んだ場所に魂が出る (追放では出ない)
public static class SeerKit
{
    // 明るい色の番号 (SNR の CustomColors.IsLighter)
    private static readonly HashSet<int> Lighter = new() { 3, 4, 5, 7, 10, 11, 13, 14, 17 };
    public static bool IsLighter(int colorId) => Lighter.Contains(colorId);

    private static readonly Color LightColor = new Color32(240, 240, 240, 255);
    private static readonly Color DarkColor = new Color32(26, 42, 108, 255);

    // 色の出し方: 0 = 出さない・1 = 明暗だけ・2 = 死体の色
    public static Color? BodyColor(PlayerControl p, int mode)
    {
        if (mode == 0 || !p || p.Data == null) return null;
        int id = p.Data.DefaultOutfit.ColorId;
        bool valid = id >= 0 && id < Palette.PlayerColors.Length;
        if (mode == 2 && valid) return Palette.PlayerColors[id];
        return IsLighter(id) ? LightColor : DarkColor;
    }

    public static string ColorText(PlayerControl p, int mode)
    {
        if (mode == 0 || !p || p.Data == null) return null;
        int id = p.Data.DefaultOutfit.ColorId;
        bool valid = id >= 0 && id < Palette.PlayerColors.Length;
        string name = mode == 2 && valid ? Palette.GetColorName(id) : new Text(IsLighter(id) ? "EvilSeer.LightColor" : "EvilSeer.DarkColor").ToString();
        return new Text("EvilSeer.FlashColorText").With("color", name).ToString();
    }

    // 魂を置く。limit なら seconds かけて薄くなって消える
    public static void Soul(Lifespan life, Vector2 at, Color? tint, bool limit, float seconds)
    {
        var sr = PortKit.Marker(life, at, "snr_soul", 0.6f, tint);
        if (!limit || !sr) return;
        float t = 0f;
        System.Action step = null;
        step = () =>
        {
            if (!sr) return;
            t += 0.05f;
            float p = Mathf.Clamp01(t / Mathf.Max(0.01f, seconds));
            var c = sr.color;
            c.a = 1f - p;
            sr.color = c;
            if (p >= 1f) { Object.Destroy(sr.gameObject); return; }
            PortKit.After(0.05f, step);
        };
        step();
    }
}
