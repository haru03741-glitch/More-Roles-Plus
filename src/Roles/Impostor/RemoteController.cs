// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/RemoteController.cs, Roles/Ability/RemoteControllerAbility.cs (GPL-3.0)
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// リモコン (インポスター): 「マーク」でクルーを 1 人選び、「操作」の間はその人を自分の操作で歩かせる (その人は自分では動けない)。
// 操作中の「遠隔キル」は、操作している人の近くの人を、操作している人の手で倒す (倒すと操作は終わる)
public sealed class RemoteController : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("RemoteController.Name");
    public override string Reading => "りもこん";
    public override Text Blurb => new("RemoteController.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[]
        { new("RemoteController.Mark", "snr_marker"), new("RemoteController.Operate", "snr_remote"), new("RemoteController.Kill", "snr_kill2") };

    public static readonly FloatOpt MarkCooldown = new("RemoteController.MarkCooldown", 20f, 0f, 60f, 2.5f, "s");
    public static readonly BoolOpt OperationInfinite = new("RemoteController.OperationInfinite", false);
    public static readonly FloatOpt OperationDuration = new("RemoteController.OperationDuration", 10f, 2.5f, 120f, 2.5f, "s") { When = () => !OperationInfinite };

    // 操作されている人 (全員の端末で同じ)
    internal static readonly HashSet<byte> Controlled = new();

    private byte _target = 255;
    private bool _operating;
    private float _syncTimer;
    private Vector2 _sentDir;

    // 操作されている人を動かす端末 (本人・ダミーならホスト): 人ごとの最後に届いた向きと、それが切れる時刻 (届かなくなったら止まる)
    private static readonly Dictionary<byte, (Vector2 dir, float until)> Puppets = new();
    private Ability _operate;

    // ボット (ホストが動かすダミー) が操作している時に歩かせる向き (ボットの頭が決める)
    internal Vector2 BotDir;

    // その人の体をこの端末が動かすか (本人の端末・ダミーはホスト)
    private static bool Drives(PlayerControl p) => p && (p.AmOwner || Dev.Dummy.Owns(p));

    private PlayerControl Target => _target == 255 ? null : PortKit.ById(_target);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("RemoteController.Mark"), "snr_marker")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => MarkCooldown,
            CanTarget = (_, t) => (!IsLocal || _target == 255) && !PortKit.IsImpostor(t) && !IsMarkedByAnyone(t.PlayerId),
            OnUsed = (user, target) =>
            {
                if (RoleState.Of(user) is RemoteController rc) rc._target = target.PlayerId;
            },
        });
        _operate = AddAbility(new Ability(new Text("RemoteController.Operate"), "snr_remote")
        {
            CooldownAtStart = false,
            Duration = () => OperationInfinite ? 3600f : OperationDuration,
            Cancellable = true,
            OnUse = () =>
            {
                var t = Target;
                if (!PortKit.Alive(Player) || _operating || !PortKit.Alive(t)) return false;
                if (Player.MyPhysics && Player.MyPhysics.body) Player.MyPhysics.body.velocity = Vector2.zero;
                if (IsLocal)
                {
                    Follow(t);
                    ShowEye(t);
                }
                OperateCall.SendAs(Player, (true, _target));
                SetOperating(Player, true, _target);
                return true;
            },
            OnEnd = StopOperation,
            OnCancel = StopOperation,
        });
        AddAbility(new Ability(new Text("RemoteController.Kill"), "snr_kill2")
        {
            CooldownAtStart = false,
            WhileStill = true, // 操作中は本人の体が止まっている
            OnUse = () =>
            {
                var t = Target;
                if (!_operating || !PortKit.Alive(t)) return false;
                var victim = NearestVictim(t);
                if (!victim) return false;
                if (AmongUsClient.Instance.AmHost) HostRemoteKill(Player, victim.PlayerId);
                else KillCall.SendAs(Player, victim.PlayerId);
                CancelAndClear();
                return true;
            },
        });
        Lifespan.OnRelease(() =>
        {
            if (_target != 255)
            {
                Controlled.Remove(_target);
                Puppets.Remove(_target);
                if (_operating && Target && Target.AmOwner) ShowPuppet(false);
            }
            if (IsLocal) Follow(PlayerControl.LocalPlayer);
            HideEye();
        });
    }

    private static bool IsMarkedByAnyone(byte id)
    {
        foreach (var r in RoleState.All) if (r is RemoteController rc && rc._target == id) return true;
        return false;
    }

    // 操作している人のキルの届く所で、一番近いインポスター以外の生きている人
    private PlayerControl NearestVictim(PlayerControl from)
    {
        float reach = GameManager.Instance.LogicOptions.GetKillDistance();
        PlayerControl best = null;
        float bestD = reach;
        Vector2 at = from.GetTruePosition();
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!PortKit.Alive(p) || p == from || p.PlayerId == PlayerId || PortKit.IsImpostor(p)) continue;
            if (p.inVent || p.inMovingPlat || !p.Visible) continue; // 本編のキルの狙いと同じく、ベントの中・昇降機・透明な人は狙わない
            Vector2 pos = p.GetTruePosition();
            float d = Vector2.Distance(at, pos);
            if (d > bestD || PhysicsHelpers.AnythingBetween(at, pos, Constants.ShipAndObjectsMask, false)) continue;
            best = p;
            bestD = d;
        }
        return best;
    }

    private static void Follow(PlayerControl p)
    {
        var cam = Camera.main ? Camera.main.GetComponent<FollowerCamera>() : null;
        if (cam && p) cam.SetTarget(p);
    }

    private void StopOperation()
    {
        if (!_operating) return;
        OperateCall.SendAs(Player, (false, _target));
        SetOperating(Player, false, _target);
        if (IsLocal) Follow(PlayerControl.LocalPlayer);
        HideEye();
        _target = 255;
        TargetCall.SendAs(Player, 255);
        Player.SetKillTimer(GameOptionsManager.Instance.CurrentGameOptions.GetFloat(AmongUs.GameOptions.FloatOptionNames.KillCooldown));
    }

    // 操作している端末だけ: 操っている人のまわりも見える (本編の視界の切り抜きをその人に付ける) のと、
    // 誰を操っているかが分かる赤い縁取り
    private GameObject _eye;
    private PlayerControl _outlined;
    private float _outlineTimer;
    private static Il2CppSystem.Nullable<UnityEngine.Color> _outlineOn, _outlineOff;
    private static Material _eyeMat;

    private void ShowEye(PlayerControl t)
    {
        HideEye();
        var src = PlayerControl.LocalPlayer ? PlayerControl.LocalPlayer.LightPrefab : null;
        var ship = ShipStatus.Instance;
        if (src && ship)
        {
            _eye = new GameObject("MrpRemoteEye") { layer = LayerMask.NameToLayer("Shadow") };
            _eye.transform.SetParent(t.transform, false);
            float size = ship.MaxLightRadius * 5.25f;
            _eye.transform.localScale = new Vector3(size, size, 1f);
            _eye.AddComponent<MeshFilter>().mesh = src.lightChildMesh;
            // 材質は 1 つを使い回す (renderer.material は複製を作り、GameObject を消しても残る)
            _eyeMat ??= new Material(src.LightCutawayMaterial.shader) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            _eye.AddComponent<MeshRenderer>().sharedMaterial = _eyeMat;
        }
        if (t.cosmetics)
        {
            t.cosmetics.SetOutline(true, _outlineOn ??= new Il2CppSystem.Nullable<UnityEngine.Color>(new UnityEngine.Color(1f, 0.1f, 0.1f, 1f)));
            _outlined = t;
        }
    }

    private void HideEye()
    {
        if (_eye) Object.Destroy(_eye);
        _eye = null;
        if (_outlined && _outlined.cosmetics)
            _outlined.cosmetics.SetOutline(false, _outlineOff ??= new Il2CppSystem.Nullable<UnityEngine.Color>(new UnityEngine.Color(0f, 0f, 0f, 0f)));
        _outlined = null;
    }

    private void CancelAndClear()
    {
        if (_operate != null && _operate.InEffect) _operate.EndEffect();
        else StopOperation();
        _target = 255;
        TargetCall.SendAs(Player, 255);
    }

    // 持ち主の端末 (ボットならホスト) で
    private void OnMeeting(MeetingStartEvent e)
    {
        if (Driven) CancelAndClear();
    }

    private void OnDeath(PlayerMurderedEvent e)
    {
        if (Driven && e.Target && e.Target.PlayerId == PlayerId) CancelAndClear();
    }

    // 操作している端末: スティックの向きを 8 方向+停止に丸め、変わった時と 0.2 秒ごとにだけ、操作されている人へ送る。
    // 操作されている端末: 届いた向きで毎 tick 歩かせ、0.6 秒届かなければ止める
    public override void OnFixedUpdate()
    {
        if (!_operating) return;
        var t = Target;
        // 操作している人が抜けた・死んだら、どの端末でも操作を解く (止める電文が来ないので)
        if (!PortKit.Alive(Player))
        {
            _operating = false;
            Controlled.Remove(_target);
            Puppets.Remove(_target);
            if (t && t.AmOwner) ShowPuppet(false);
            return;
        }
        // 操作されている体をこの端末が動かすなら動かす (操作しているのもこの端末のボット/本人なら、下で向きも決める)
        if (Drives(t)) DrivePuppet(t);
        if (!Driven) return;
        var hud = Vanilla.Hud;
        if ((hud && hud.IsIntroDisplayed) || MeetingHud.Instance || ExileController.Instance) return;
        if (!PortKit.Alive(t) || !PortKit.Alive(Player))
        {
            CancelAndClear();
            return;
        }
        // 能力の狙いの縁取りは狙いが替わる時に前の人の縁取りを消すので、操っている間は少しずつ付け直す
        if (IsLocal) _outlineTimer -= Time.fixedDeltaTime;
        if (_outlineTimer <= 0f && _outlined && _outlined.cosmetics)
        {
            _outlineTimer = 0.5f;
            _outlined.cosmetics.SetOutline(true, _outlineOn);
        }
        Vector2 dir = !IsLocal ? Quantize(BotDir) : Minigame.Instance ? Vector2.zero : Quantize(hud && hud.joystick != null ? hud.joystick.DeltaL : Vector2.zero);
        _syncTimer -= Time.fixedDeltaTime;
        if (dir == _sentDir && _syncTimer > 0f) return;
        _sentDir = dir;
        _syncTimer = 0.2f;
        // 操作されている体をこの端末が動かすなら電文は要らない (ダミーの体はホストが動かすので、ダミー宛ての向きはホストへ)
        if (Drives(t)) Puppets[_target] = (dir, Time.time + 0.6f);
        else VelocityCall.SendAs(Player, (_target, dir), t.isDummy ? AmongUsClient.Instance.HostId : t.OwnerId);
    }

    private static void DrivePuppet(PlayerControl t)
    {
        if (!t.MyPhysics || MeetingHud.Instance || ExileController.Instance) return;
        var dir = Puppets.TryGetValue(t.PlayerId, out var m) && Time.time < m.until ? m.dir : Vector2.zero;
        t.MyPhysics.SetNormalizedVelocity(dir);
    }

    // 8 方向+停止 (ゆれで電文が増えないように)
    private static Vector2 Quantize(Vector2 d)
    {
        float x = d.x, y = d.y;
        if (x * x + y * y < 0.04f) return Vector2.zero;
        int sx = x > 0.38f ? 1 : x < -0.38f ? -1 : 0;
        int sy = y > 0.38f ? 1 : y < -0.38f ? -1 : 0;
        if (sx == 0 && sy == 0) { if (System.Math.Abs(x) > System.Math.Abs(y)) sx = x > 0 ? 1 : -1; else sy = y > 0 ? 1 : -1; }
        float k = sx != 0 && sy != 0 ? 0.70710677f : 1f;
        return new Vector2(sx * k, sy * k);
    }

    private static readonly RemoteCall<(bool on, byte target)> OperateCall = new("RemoteController.Operate", Route.Anyone,
        (w, m) => { w.Write(m.on); w.Write(m.target); }, r => (r.ReadBoolean(), r.ReadByte()), (sender, m) => SetOperating(sender, m.on, m.target));
    private static readonly RemoteCall<byte> TargetCall = new("RemoteController.Target", Route.Anyone,
        (w, id) => w.Write(id), r => r.ReadByte(), (sender, id) => { if (RoleState.Of(sender) is RemoteController rc) rc._target = id; });
    private static readonly RemoteCall<(byte target, Vector2 v)> VelocityCall = new("RemoteController.Velocity", Route.Anyone,
        (w, m) => { w.Write(m.target); w.Write(m.v.x); w.Write(m.v.y); }, r => (r.ReadByte(), new Vector2(r.ReadSingle(), r.ReadSingle())),
        (sender, m) =>
        {
            var t = PortKit.ById(m.target);
            if (!Drives(t) || !Controlled.Contains(m.target) || RoleState.Of(sender) is not RemoteController { _operating: true }) return;
            Puppets[m.target] = (m.v, Time.time + 0.6f);
        }, Delivery.Queued);
    private static readonly RemoteCall<byte> KillCall = new("RemoteController.Kill", Route.ToHost,
        (w, id) => w.Write(id), r => r.ReadByte(), (sender, id) => HostRemoteKill(sender, id));

    private static void SetOperating(PlayerControl p, bool on, byte target)
    {
        if (RoleState.Of(p) is not RemoteController rc) return;
        rc._operating = on;
        if (on) { rc._target = target; Controlled.Add(target); }
        else Controlled.Remove(target);
        var t = PortKit.ById(target);
        if (t && t.AmOwner) ShowPuppet(on);
        if (!on && Drives(t))
        {
            Puppets.Remove(target);
            if (t.MyPhysics) t.MyPhysics.SetNormalizedVelocity(Vector2.zero);
        }
    }

    // 操られている自分の端末: 自分で動けない理由をタスク欄の効果の行に出す (誰に操られているかは出さない)
    private static void ShowPuppet(bool on)
    {
        if (on) StatusEffects.ShowUntil("RemoteControlled", new Text("RemoteController.Controlled"), "#FF1919", new Text("RemoteController.ControlledUntil"));
        else StatusEffects.Hide("RemoteControlled");
    }

    // ホスト: 操作している人の手で倒す
    private static void HostRemoteKill(PlayerControl rcPlayer, byte victimId)
    {
        if (RoleState.Of(rcPlayer) is not RemoteController rc || !rc._operating) return;
        var puppet = rc.Target;
        var victim = PortKit.ById(victimId);
        if (!PortKit.Alive(puppet) || !PortKit.Alive(victim)) return;
        PortKit.HostKill(puppet, victim, PortKit.Harm.Slash);
    }

    internal static bool Operating(PlayerControl p) => RoleState.Of(p) is RemoteController { _operating: true };
    internal bool IsOperating => _operating;
    internal byte MarkedId => _target;
}
