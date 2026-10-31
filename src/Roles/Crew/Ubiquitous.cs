// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Crewmate/Ubiquitous.cs, CustomObject/Drone.cs (GPL-3.0)
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ユビキタス (クルー): 「呼ぶ / 戻す」で足元にドローンを出す (もう一度押すと消す)。「操作」の間は自分は動けず、カメラがドローンへ移って
// ドローンを飛ばせる (ドローンのまわりが見える)。操作中は「ドアを開ける」で近くの閉まったドアを開けられる。
// 会議が始まるとドローンはその場に止まり、設定の会議の数だけ残る。止まったドローンの近くにいる人は地図に映る
public sealed class Ubiquitous : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#389BDF";
    public override Text Name => new("Ubiquitous.Name");
    public override string Reading => "ゆびきたす";
    public override Text Blurb => new("Ubiquitous.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[]
        { new("Ubiquitous.Call", "snr_drone"), new("Ubiquitous.Operate", "snr_remote"), new("Ubiquitous.DoorHack", "snr_door") };

    public static readonly FloatOpt CallCooldown = new("Ubiquitous.CallCooldown", 10f, 0f, 60f, 2.5f, "s");
    public static readonly FloatOpt OperateCooldown = new("Ubiquitous.OperateCooldown", 0f, 0f, 60f, 2.5f, "s");
    public static readonly BoolOpt LimitOperate = new("Ubiquitous.LimitOperate", false);
    public static readonly FloatOpt OperateTime = new("Ubiquitous.OperateTime", 10f, 2.5f, 60f, 2.5f, "s") { When = () => LimitOperate };
    public static readonly IntOpt StayTurns = new("Ubiquitous.StayTurns", 5, 1, 20);
    public static readonly FloatOpt FlyingSpeed = new("Ubiquitous.FlyingSpeed", 1.5f, 0.25f, 3f, 0.25f, "x");
    public static readonly FloatOpt DoorHackCooldown = new("Ubiquitous.DoorHackCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt DoorHackScope = new("Ubiquitous.DoorHackScope", 0.5f, 0.25f, 5f, 0.25f, "x");
    public static readonly FloatOpt VisibilityRange = new("Ubiquitous.VisibilityRange", 1f, 0.25f, 5f, 0.25f, "x");
    public static readonly BoolOpt MapShowColor = new("Ubiquitous.MapShowColor", false);

    private static readonly HashSet<int> LighterColors = new() { 3, 4, 5, 7, 10, 11, 13, 14, 17 };

    // 止まったドローン (全員の端末で持つ)
    private sealed class IdleDrone
    {
        public byte Owner;
        public GameObject Go;
        public int Turns;
    }
    private static readonly List<IdleDrone> Idle = new();

    // 飛んでいるドローン (自分の端末だけ)
    private GameObject _drone;
    private Rigidbody2D _body;
    private SpriteRenderer _rend;
    private GameObject _light;
    private float _hover;
    private Ability _operate;
    private SpriteRenderer[] _herePoints;

    internal static bool Operating(PlayerControl p) =>
        p && p.AmOwner && RoleState.Of(p) is Ubiquitous u && u._drone && u._operate != null && u._operate.InEffect;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Ubiquitous.Call"), "snr_drone")
        {
            Cooldown = () => CallCooldown,
            OnUse = () =>
            {
                if (!PortKit.Alive(Player) || _operate != null && _operate.InEffect) return false;
                if (_drone) DestroyDrone();
                else CreateDrone(Player.transform.position);
                return true;
            },
        });
        _operate = AddAbility(new Ability(new Text("Ubiquitous.Operate"), "snr_remote")
        {
            Cooldown = () => OperateCooldown,
            CooldownAtStart = false,
            Duration = () => LimitOperate ? OperateTime : 3600f,
            Cancellable = true,
            OnUse = () =>
            {
                if (!_drone || Minigame.Instance || !PortKit.Alive(Player)) return false;
                if (Player.MyPhysics && Player.MyPhysics.body) Player.MyPhysics.body.velocity = Vector2.zero;
                Follow(_drone.GetComponent<DroneTarget>());
                return true;
            },
            OnEnd = StopOperate,
            OnCancel = StopOperate,
        });
        AddAbility(new Ability(new Text("Ubiquitous.DoorHack"), "snr_door")
        {
            Cooldown = () => DoorHackCooldown,
            OnUse = () =>
            {
                if (!_drone || !_operate.InEffect) return false;
                var doors = HackTargets();
                if (doors.Count == 0) return false;
                var ids = doors.ToArray();
                DoorCall.Send(ids);
                OpenDoors(ids);
                return true;
            },
        });
        Lifespan.OnRelease(() =>
        {
            if (!IsLocal) return;
            DestroyDrone();
            Follow(null);
            foreach (var h in _herePoints ?? new SpriteRenderer[0]) if (h) Object.Destroy(h.gameObject);
        });
        RoleState.Match.OnRelease(ClearIdle);
    }

    private static void ClearIdle()
    {
        foreach (var d in Idle) if (d.Go) Object.Destroy(d.Go);
        Idle.Clear();
    }

    private void StopOperate()
    {
        if (_body) _body.velocity = Vector2.zero;
        Follow(null);
    }

    private static void Follow(MonoBehaviour target)
    {
        var hud = Vanilla.Hud;
        if (!hud || !hud.PlayerCam) return;
        hud.PlayerCam.SetTarget(target ? target : PlayerControl.LocalPlayer);
    }

    // ---- ドローン ----

    private static bool _registered;

    private static GameObject MakeDrone(Vector2 at, bool active)
    {
        if (!_registered)
        {
            _registered = true;
            ClassInjector.RegisterTypeInIl2Cpp<DroneTarget>();
        }
        var go = new GameObject("UbiquitousDrone") { layer = LayerMask.NameToLayer("Players") };
        go.transform.position = new Vector3(at.x, at.y, active ? -1f : at.y / 1000f);
        go.AddComponent<DroneTarget>();
        var r = new GameObject("Renderer") { layer = LayerMask.NameToLayer("Objects") };
        r.transform.SetParent(go.transform, false);
        var sr = r.AddComponent<SpriteRenderer>();
        sr.sprite = ButtonIcons.Get("snr_drone");
        r.transform.localScale = Vector3.one * 0.45f;
        sr.color = active ? new UnityEngine.Color(1f, 1f, 1f, 0.5f) : UnityEngine.Color.white;
        return go;
    }

    private void CreateDrone(Vector2 at)
    {
        _drone = MakeDrone(at, true);
        _body = _drone.AddComponent<Rigidbody2D>();
        _body.gravityScale = 0f;
        _body.freezeRotation = true;
        _body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        _body.sleepMode = RigidbodySleepMode2D.NeverSleep;
        _body.interpolation = RigidbodyInterpolation2D.Interpolate;
        var col = _drone.AddComponent<CircleCollider2D>();
        col.offset = new Vector2(0f, -0.2f);
        col.radius = 0.2234f;
        _rend = _drone.GetComponentInChildren<SpriteRenderer>();
        // 操作中はドローンのまわりも見える (本編の視界の切り抜きをもう 1 つ置く)
        _light = new GameObject("LightChild") { layer = LayerMask.NameToLayer("Shadow") };
        _light.transform.SetParent(_drone.transform, false);
        _light.transform.localScale = Vector3.zero;
        var src = PlayerControl.LocalPlayer.LightPrefab;
        if (src)
        {
            _light.AddComponent<MeshFilter>().mesh = src.lightChildMesh;
            _light.AddComponent<MeshRenderer>().material.shader = src.LightCutawayMaterial.shader;
        }
    }

    private void DestroyDrone()
    {
        if (_operate != null && _operate.InEffect) _operate.EndEffect();
        if (_drone) Object.Destroy(_drone);
        _drone = null;
        _body = null;
    }

    public override void OnFixedUpdate()
    {
        if (!IsLocal) return;
        if (_drone && !PortKit.Alive(Player)) DestroyDrone();
        if (!_drone) return;
        bool op = _operate.InEffect;
        if (op && !Minigame.Instance)
        {
            var hud = Vanilla.Hud;
            Vector2 delta = hud && hud.joystick != null ? hud.joystick.DeltaL : Vector2.zero;
            _body.velocity = delta * FlyingSpeed * (Player.MyPhysics ? Player.MyPhysics.Speed : 2.5f);
        }
        else _body.velocity = Vector2.zero;
        var p = _drone.transform.position;
        _drone.transform.position = new Vector3(p.x, p.y, -1f);
        _hover += Time.fixedDeltaTime;
        if (_hover > Mathf.PI * 2f) _hover -= Mathf.PI * 2f;
        if (_rend)
        {
            _rend.transform.localPosition = new Vector3(0f, Mathf.Sin(_hover) / 20f, 0f);
            if (_body.velocity.x > 0.1f) _rend.flipX = true;
            else if (_body.velocity.x < -0.1f) _rend.flipX = false;
            _rend.color = op ? UnityEngine.Color.white : new UnityEngine.Color(1f, 1f, 1f, 0.5f);
        }
        float size = op && ShipStatus.Instance ? ShipStatus.Instance.MaxLightRadius * VisibilityRange * 5.25f : 0f;
        if (_light) _light.transform.localScale = new Vector3(size, size, 1f);
    }

    // 会議が始まったら、飛んでいるドローンをその場に止める
    [LocalOnly]
    private void OnMeeting(MeetingStartEvent e)
    {
        if (!_drone) return;
        Vector2 at = _drone.transform.position;
        DestroyDrone();
        Follow(null);
        IdleCall.Send(at);
        AddIdle(Player, at);
    }

    // 会議が終わったら、止まったドローンの残りの会議の数を減らす
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (FirstUbiquitous() != this) return;
        for (int i = Idle.Count - 1; i >= 0; i--)
        {
            if (Idle[i].Turns-- > 0) continue;
            if (Idle[i].Go) Object.Destroy(Idle[i].Go);
            Idle.RemoveAt(i);
        }
    }

    private static Ubiquitous FirstUbiquitous()
    {
        foreach (var r in RoleState.All) if (r is Ubiquitous u) return u;
        return null;
    }

    private static readonly RemoteCall<Vector2> IdleCall = new("Ubiquitous.Idle", Route.Anyone,
        (w, at) => { w.Write(at.x); w.Write(at.y); }, r => new Vector2(r.ReadSingle(), r.ReadSingle()), (sender, at) => AddIdle(sender, at));

    private static void AddIdle(PlayerControl owner, Vector2 at)
    {
        if (!owner || RoleState.Of(owner) is not Ubiquitous) return;
        Idle.Add(new IdleDrone { Owner = owner.PlayerId, Go = MakeDrone(at, false), Turns = StayTurns });
    }

    // ---- ドア ----

    private List<int> HackTargets()
    {
        var list = new List<int>();
        var doors = ShipStatus.Instance ? ShipStatus.Instance.AllDoors : null;
        if (doors == null || !_drone) return list;
        for (int i = 0; i < doors.Length; i++)
        {
            var d = doors[i];
            if (!d || d.IsOpen || d.TryCast<AutoCloseDoor>()) continue;
            if (Vector2.Distance(_drone.transform.position, d.transform.position) > DoorHackScope * 3f) continue;
            list.Add(i);
        }
        return list;
    }

    private static readonly RemoteCall<int[]> DoorCall = new("Ubiquitous.Doors", Route.Anyone,
        (w, ids) => { w.WritePacked(ids.Length); foreach (var i in ids) w.WritePacked(i); },
        r => { var a = new int[r.ReadPackedInt32()]; for (int i = 0; i < a.Length; i++) a[i] = r.ReadPackedInt32(); return a; },
        (_, ids) => OpenDoors(ids));

    private static void OpenDoors(int[] ids)
    {
        var ship = ShipStatus.Instance;
        var doors = ship ? ship.AllDoors : null;
        if (doors == null) return;
        foreach (int i in ids)
        {
            if (i < 0 || i >= doors.Length) continue;
            var d = doors[i];
            if (!d || d.IsOpen || d.TryCast<AutoCloseDoor>()) continue;
            var auto = d.TryCast<AutoOpenDoor>();
            if (auto != null && ship.Systems.TryGetValue(SystemTypes.Doors, out var sys) && sys.TryCast<AutoDoorsSystemType>() is { } autoSys)
            {
                // スケルドは同じ部屋のドアをまとめて開ける
                if (ship.Type == ShipStatus.MapType.Ship)
                {
                    bool any = false;
                    foreach (var other in doors)
                    {
                        var od = other ? other.TryCast<AutoOpenDoor>() : null;
                        if (od == null || od.Room != auto.Room) continue;
                        autoSys.SetDoor(od, true);
                        any = true;
                    }
                    if (!any) autoSys.SetDoor(auto, true);
                }
                else autoSys.SetDoor(auto, true);
                continue;
            }
            d.SetDoorway(true);
        }
    }

    // ---- 地図 ----

    // 止まった自分のドローンの近くにいる人を地図に映す
    internal void UpdateMap(MapBehaviour map)
    {
        if (!map || !map.HerePoint || !ShipStatus.Instance) return;
        if (_herePoints == null || _herePoints.Length == 0 || !_herePoints[0])
        {
            int n = Mathf.Max(1, PlayerControl.AllPlayerControls.Count - 1);
            _herePoints = new SpriteRenderer[n];
            for (int i = 0; i < n; i++)
            {
                _herePoints[i] = Object.Instantiate(map.HerePoint, map.HerePoint.transform.parent);
                _herePoints[i].material.SetInt(PlayerMaterial.MaskLayer, 255);
                _herePoints[i].gameObject.SetActive(false);
            }
        }
        var near = new List<PlayerControl>();
        float range = ShipStatus.Instance.MaxLightRadius * VisibilityRange;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p.PlayerId == PlayerId) continue;
            Vector2 pos = p.GetTruePosition();
            foreach (var d in Idle)
                if (d.Owner == PlayerId && d.Go && Vector2.Distance(d.Go.transform.position, pos) <= range) { near.Add(p); break; }
        }
        for (int i = 0; i < _herePoints.Length; i++)
        {
            var r = _herePoints[i];
            if (!r) continue;
            if (i >= near.Count) { r.gameObject.SetActive(false); continue; }
            var p = near[i];
            if (MapShowColor) p.SetPlayerMaterialColors(r);
            else PlayerMaterial.SetColors(LighterColors.Contains(p.Data.DefaultOutfit.ColorId) ? 7 : 6, r);
            Vector3 pos = p.GetTruePosition();
            pos /= ShipStatus.Instance.MapScale;
            pos.x *= Mathf.Sign(ShipStatus.Instance.transform.localScale.x);
            pos.z = -1f;
            r.transform.localPosition = pos;
            r.gameObject.SetActive(true);
        }
    }
}

// カメラが追いかける的 (本編のカメラは MonoBehaviour しか追えない)
public class DroneTarget : MonoBehaviour
{
    public DroneTarget(System.IntPtr ptr) : base(ptr) { }
}

[HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.FixedUpdate))]
internal static class UbiquitousMapPatch
{
    public static void Postfix(MapBehaviour __instance)
    {
        if (RoleState.Local is Ubiquitous u) u.UpdateMap(__instance);
    }
}
