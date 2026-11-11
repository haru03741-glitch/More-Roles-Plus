// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/RemoteController.cs, Roles/Ability/RemoteControllerAbility.cs (GPL-3.0)
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MoreRolesPlus.Net;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Options;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// リモコン (インポスター): 「マーク」でクルーを 1 人選び、「操作」の間はその人を自分の操作で歩かせる (その人は自分では動けない)。
// 操作中の「遠隔キル」は、操作している人の近くの人 (縁取りで見える一番近いクルー) を、操作している人の手で倒す (倒すと操作は終わる)。
// 操作中は「使う」とベントのボタンが、操作している人の足元の物 (扉・はしご・昇降機・ジップライン・ベント) を狙い、押すとその人の体で使う
public sealed class RemoteController : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("RemoteController.Name");
    public override string Reading => "りもこん";
    public override Text Blurb => new("RemoteController.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[]
        {
            new("RemoteController.Mark", "snr_marker"), new("RemoteController.Operate", "snr_remote"), new("RemoteController.Kill", "snr_kill2"),
            new("RemoteController.Strike", "emote_fist"),
        };

    public static readonly FloatOpt MarkCooldown = new("RemoteController.MarkCooldown", 20f, 0f, 60f, 2.5f, "s");
    public static readonly BoolOpt OperationInfinite = new("RemoteController.OperationInfinite", false);
    public static readonly FloatOpt OperationDuration = new("RemoteController.OperationDuration", 10f, 2.5f, 120f, 2.5f, "s") { When = () => !OperationInfinite };
    public static readonly FloatOpt StrikeCooldown = new("RemoteController.StrikeCooldown", 5f, 0f, 30f, 2.5f, "s");

    // 操られている人は自分で水を掻けないので、足の着かない深さでは息がこの倍の速さで尽きる (ホストの数えと本人の泡の両方)
    internal const float HeldBreathRate = 2f;
    internal static float BreathRate(byte id) => Controlled.Count > 0 && Controlled.Contains(id) ? HeldBreathRate : 1f;

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
                    Hearing.Self("remote_link_on", 0.5f);
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
                var victim = _aim && PortKit.Alive(_aim) ? _aim : NearestVictim(t); // 縁取りで見えている人を倒す
                if (!victim) return false;
                if (AmongUsClient.Instance.AmHost) HostRemoteKill(Player, victim.PlayerId);
                else KillCall.SendAs(Player, victim.PlayerId);
                CancelAndClear();
                return true;
            },
        });
        // 操作している人の拳で、その人の前の壁を叩かせる (操作は続く)。壁を叩いたのはその人として記録される
        AddAbility(new Ability(new Text("RemoteController.Strike"), "emote_fist")
        {
            CooldownAtStart = false,
            WhileStill = true,
            Cooldown = () => StrikeCooldown,
            Usable = () => _operating,
            OnUse = () =>
            {
                var t = Target;
                if (!_operating || !PortKit.Alive(t) || t.inVent || t.onLadder || t.inMovingPlat) return false;
                Vector2 aim = _sentDir.sqrMagnitude > 0.01f ? _sentDir : HammerSwing.Aim(t, out _);
                if (!HammerSwing.FindTarget(t.GetTruePosition(), aim, DamageProfile.Of(DamageKind.Blunt).Reach, out Vector2 dir, out _, out _)) return false;
                if (AmongUsClient.Instance.AmHost) HostStrike(Player, dir);
                else StrikeCall.SendAs(Player, dir);
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

    // ---- 操作中の「使う」・ベント・キルの狙い (操作している端末) ----

    private const byte UseEnterVent = 0, UseExitVent = 1, UseMoveVent = 2, UseLadder = 3, UsePlatform = 4, UseZipline = 5;

    private PlayerControl _aim;          // 縁取りを付けている、遠隔キルで倒す人
    private float _scanTimer;
    private IUsable _useT;               // 操作している人の足元の「使う」の狙い
    private Vent _ventT;                 // 同じくベントの狙い
    private int _ventId = -1;            // 操作している人が入っているベント (-1 = 外)
    private static Il2CppSystem.Nullable<UnityEngine.Color> _aimOn, _aimOff;

    // 0.1 秒ごとに狙いを探し直し、本編が毎 tick 自分の足元の物で書き戻す「使う」・ベントのボタンの狙いを、操作している人の足元の物へ付け直す
    private void TickTargets(PlayerControl t)
    {
        var hud = Vanilla.Hud;
        if (!hud) return;
        _scanTimer -= Time.fixedDeltaTime;
        if (_scanTimer <= 0f)
        {
            _scanTimer = 0.1f;
            SetAim(NearestVictim(t));
            try { ScanUsables(t); }
            catch (System.Exception e) { _useT = null; _ventT = null; Plugin.Logger.LogWarning($"RemoteController scan: {e.Message}"); }
        }
        try
        {
            if (hud.UseButton) hud.UseButton.SetTarget(_useT);
            if (hud.ImpostorVentButton) hud.ImpostorVentButton.SetTarget(_ventT);
        }
        catch (System.Exception e) { _useT = null; _ventT = null; Plugin.Logger.LogWarning($"RemoteController target: {e.Message}"); }
    }

    private void ClearUseTargets()
    {
        _useT = null;
        _ventT = null;
        var hud = Vanilla.Hud;
        if (!hud) return;
        if (hud.UseButton) hud.UseButton.SetTarget((IUsable)null);
        if (hud.ImpostorVentButton) hud.ImpostorVentButton.SetTarget((Vent)null);
    }

    private void SetAim(PlayerControl p)
    {
        if (p == _aim) return;
        if (_aim && _aim.cosmetics) _aim.cosmetics.SetOutline(false, _aimOff ??= new Il2CppSystem.Nullable<UnityEngine.Color>(new UnityEngine.Color(0f, 0f, 0f, 0f)));
        _aim = p;
        if (_aim && _aim.cosmetics) _aim.cosmetics.SetOutline(true, _aimOn ??= new Il2CppSystem.Nullable<UnityEngine.Color>(new UnityEngine.Color(1f, 0.95f, 0.3f, 1f)));
    }

    // 操作している人の足元で使える物の一番近い物 (本編の「使う」の狙いと同じ範囲)。使える物は扉・はしご・昇降機・ジップライン・ベント
    private void ScanUsables(PlayerControl t)
    {
        _useT = null;
        _ventT = null;
        var local = PlayerControl.LocalPlayer;
        if (!local || t.Data == null) return;
        float bestUse = float.MaxValue, bestVent = float.MaxValue;
        foreach (var col in Physics2D.OverlapCircleAll(t.GetTruePosition(), local.MaxReportDistance, Constants.Usables))
        {
            if (!local.cache.TryGetValue(col, out var cached))
            {
                local.cache[col] = col.GetComponents<IUsable>().ToArray();
                cached = local.cache[col];
            }
            if (cached == null) continue;
            foreach (IUsable usable in cached)
            {
                if (usable == null) continue;
                var vent = usable.TryCast<Vent>();
                if (vent != null)
                {
                    float d = VentDistance(t, vent, out bool canUse);
                    if (canUse && d < bestVent) { bestVent = d; _ventT = vent; }
                    continue;
                }
                if (usable.TryCast<DoorConsole>() == null && usable.TryCast<Ladder>() == null
                    && usable.TryCast<PlatformConsole>() == null && usable.TryCast<ZiplineConsole>() == null) continue;
                float dist = usable.CanUse(t.Data, out bool can, out bool _);
                if (can && dist < bestUse) { bestUse = dist; _useT = usable; }
            }
        }
    }

    private static Vent VentById(int id)
    {
        var ship = ShipStatus.Instance;
        if (!ship || ship.AllVents == null) return null;
        foreach (var v in ship.AllVents) if (v && v.Id == id) return v;
        return null;
    }

    // 操作している人からそのベントまでの距離。入れる (近い・間に壁が無い・掃除中でない) なら canUse
    private float VentDistance(PlayerControl t, Vent vent, out bool canUse)
    {
        canUse = false;
        if (!t || !vent) return float.MaxValue;
        Vector2 from = t.GetTruePosition();
        Vector2 at = vent.transform.position;
        float d = Vector2.Distance(from, at);
        canUse = d <= vent.UsableDistance && !VentBlocked(t, vent) && !PhysicsHelpers.AnythingBetween(from, at, Constants.ShipOnlyMask, false);
        return d;
    }

    private bool VentBlocked(PlayerControl t, Vent vent)
    {
        bool current = t.inVent && _ventId == vent.Id;
        if (!current && t.MustCleanVent(vent.Id)) return true;
        var ship = ShipStatus.Instance;
        if (ship && ship.Systems.TryGetValue(SystemTypes.Ventilation, out var sys))
        {
            var vs = sys.TryCast<VentilationSystem>();
            if (vs != null && vs.IsVentCurrentlyBeingCleaned(vent.Id)) return true;
        }
        return false;
    }

    private static bool LocalOperation(out RemoteController rc, out PlayerControl puppet)
    {
        rc = RoleState.Local as RemoteController;
        puppet = null;
        if (rc == null || !rc._operating || !rc.IsLocal || !PortKit.Alive(rc.Player)) return false;
        puppet = rc.Target;
        return PortKit.Alive(puppet);
    }

    // 操作している人の体で「使う」を押した (クリックでもキーでも)。扉は操作している端末で画面を開き、はしご・昇降機・ジップラインは操作されている人の端末でその人が使う
    internal static bool HandleUse(UseButton b)
    {
        if (!LocalOperation(out var rc, out var t)) return false;
        if (!b || !b.isActiveAndEnabled || b.isCoolingDown || b.currentTarget == null) return true;
        if (t.inVent) return true;
        var target = b.currentTarget;
        var door = target.TryCast<DoorConsole>();
        if (door != null)
        {
            try
            {
                Minigame minigame = Object.Instantiate(door.MinigamePrefab, Camera.main.transform);
                minigame.transform.localPosition = new Vector3(0f, 0f, -50f);
                minigame.Cast<IDoorMinigame>().SetDoor(door.MyDoor);
                minigame.Begin(null);
            }
            catch
            {
                if (Minigame.Instance) Object.Destroy(Minigame.Instance.gameObject);
            }
            return true;
        }
        var ladder = target.TryCast<Ladder>();
        if (ladder != null) { rc.SendUse(t, UseLadder, 0, ladder.transform.position.x, ladder.transform.position.y); return true; }
        if (target.TryCast<PlatformConsole>() != null) { rc.SendUse(t, UsePlatform, 0); return true; }
        var zip = target.TryCast<ZiplineConsole>();
        if (zip != null) rc.SendUse(t, UseZipline, 0, zip.transform.position.x, zip.transform.position.y);
        return true;
    }

    // 操作している人の体でベントのボタンを押した: 入っていれば出し、外なら狙いのベントへ入れる
    internal static bool HandleVent(VentButton b)
    {
        if (!LocalOperation(out var rc, out var t)) return false;
        if (!b || !b.isActiveAndEnabled || b.isCoolingDown) return true;
        if (t.inVent)
        {
            int id = rc._ventId >= 0 ? rc._ventId : TimeRewind.VentOf(t);
            if (id < 0) return true;
            rc.SendUse(t, UseExitVent, id);
            VentById(id)?.SetButtons(false);
            rc._ventId = -1;
            return true;
        }
        var vent = b.currentTarget;
        if (!vent) return true;
        rc.VentDistance(t, vent, out bool canUse);
        if (!canUse) return true;
        rc.SendUse(t, UseEnterVent, vent.Id);
        vent.SetButtons(true);
        rc._ventId = vent.Id;
        return true;
    }

    // ベントの中の操作されている人を、別のベントへ動かす (ベントの矢印)
    internal static bool HandleMove(Vent from, Vent other, out bool result, out string error)
    {
        result = false;
        error = string.Empty;
        if (!LocalOperation(out var rc, out var t)) return false;
        if (!other) { error = "Vent does not exist"; return true; }
        if (!t.inVent) { error = "Player is not currently inside a vent"; return true; }
        if (t.walkingToVent || t.Visible) { error = "Player was still in the middle of animating into current vent"; return true; }
        if (rc.VentBlocked(t, other)) { error = "Vent is blocked by vent cleaning"; return true; }
        rc.SendUse(t, UseMoveVent, other.Id);
        if (from) from.SetButtons(false);
        other.SetButtons(true);
        rc._ventId = other.Id;
        result = true;
        return true;
    }

    // 操作されている人の端末 (ダミーならホスト) へ「その人の体で使う」を頼む。この端末がその人の体を動かすなら直接
    private void SendUse(PlayerControl t, byte kind, int id, float x = 0f, float y = 0f)
    {
        var m = (kind, id, x, y);
        if (Drives(t)) { DoUse(t, m); return; }
        UseCall.SendAs(Player, m, t.isDummy ? AmongUsClient.Instance.HostId : t.OwnerId);
    }

    private static readonly RemoteCall<(byte kind, int id, float x, float y)> UseCall = new("RemoteController.Use", Route.Anyone,
        (w, m) => { w.Write(m.kind); w.Write(m.id); w.Write(m.x); w.Write(m.y); },
        r => (r.ReadByte(), r.ReadInt32(), r.ReadSingle(), r.ReadSingle()),
        (sender, m) =>
        {
            if (RoleState.Of(sender) is not RemoteController { _operating: true } rc) return;
            var t = rc.Target;
            if (!t || !Drives(t) || !Controlled.Contains(t.PlayerId)) return;
            DoUse(t, m);
        }, perSecond: 10);

    // 操作されている人の体で使う (その人の端末・ダミーならホストで)。結果は本編の電文で全員に同じに見える
    private static void DoUse(PlayerControl t, (byte kind, int id, float x, float y) m)
    {
        if (!PortKit.Alive(t) || !t.MyPhysics || MeetingHud.Instance || ExileController.Instance) return;
        var ship = ShipStatus.Instance;
        if (!ship) return;
        switch (m.kind)
        {
            case UseEnterVent:
                {
                    var v = VentById(m.id);
                    if (v && !t.inVent && !t.onLadder && !t.inMovingPlat) t.MyPhysics.RpcEnterVent(v.Id);
                    break;
                }
            case UseExitVent:
                if (t.inVent) t.MyPhysics.RpcExitVent(m.id);
                break;
            case UseMoveVent:
                {
                    var v = VentById(m.id);
                    if (!v || !t.inVent) break;
                    if (t.AmOwner)
                    {
                        if (ship.Systems.ContainsKey(SystemTypes.Ventilation)) VentilationSystem.Update(VentilationSystem.Operation.Move, v.Id);
                        Vent.currentVent = v;
                    }
                    Vector3 at = v.transform.position;
                    Vector2 off = t.Collider ? t.Collider.offset : Vector2.zero;
                    t.NetTransform.RpcSnapTo(new Vector2(at.x - off.x, at.y - off.y));
                    break;
                }
            case UseLadder:
                {
                    if (t.inVent || t.onLadder || t.inMovingPlat) break;
                    Ladder best = null;
                    float bestD = 0.5f;
                    foreach (var l in Object.FindObjectsOfType<Ladder>())
                    {
                        float dx = l.transform.position.x - m.x, dy = l.transform.position.y - m.y, d = dx * dx + dy * dy;
                        if (d < bestD) { bestD = d; best = l; }
                    }
                    if (best) t.MyPhysics.RpcClimbLadder(best);
                    break;
                }
            case UsePlatform:
                if (!t.inVent && !t.onLadder && !t.inMovingPlat) t.RpcUsePlatform();
                break;
            case UseZipline:
                {
                    if (t.inVent || t.onLadder || t.inMovingPlat) break;
                    ZiplineConsole best = null;
                    float bestD = 0.5f;
                    foreach (var zc in Object.FindObjectsOfType<ZiplineConsole>())
                    {
                        float dx = zc.transform.position.x - m.x, dy = zc.transform.position.y - m.y, d = dx * dx + dy * dy;
                        if (d < bestD) { bestD = d; best = zc; }
                    }
                    if (best) t.RpcUseZipline(t, best.zipline, best.atTop);
                    break;
                }
        }
    }

    private static void Follow(PlayerControl p)
    {
        var cam = Camera.main ? Camera.main.GetComponent<FollowerCamera>() : null;
        if (cam && p) cam.SetTarget(p);
    }

    private void StopOperation()
    {
        if (!_operating) return;
        if (IsLocal) Hearing.Self("remote_link_off", 0.5f);
        var puppet = Target;
        // ベントの中で止める時はその人をベントから出す (操作を解く電文より先に送る)
        if (Driven && puppet && puppet.inVent)
        {
            int ventId = _ventId >= 0 ? _ventId : TimeRewind.VentOf(puppet);
            if (ventId >= 0) { SendUse(puppet, UseExitVent, ventId); VentById(ventId)?.SetButtons(false); }
        }
        _ventId = -1;
        if (IsLocal)
        {
            if (Minigame.Instance) Minigame.Instance.Close(); // 操作中に開いた扉の画面
            ClearUseTargets();
        }
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
        SetAim(null);
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
        if (_strikeAt > 0f) TickStrike();
        if (!_operating) return;
        var t = Target;
        // 操作している人が抜けた・死んだら、どの端末でも操作を解く (止める電文が来ないので)
        if (!PortKit.Alive(Player))
        {
            _operating = false;
            Controlled.Remove(_target);
            Puppets.Remove(_target);
            _ventId = -1;
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
        if (IsLocal) TickTargets(t);
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

    // ---- 叩かせる (ホストが決める) ----
    // 無傷の壁は弱い一撃 (1 削る) でひびが入るだけ。ひびの入った壁 (残り MaxHp 未満) は強い一撃 (2 削る) で必ず抜ける。
    // 拳を全員に見せてから、拳が壁に届く頃 (RemoteControllerFx.Contact) に操られている人の破壊として依頼する
    private const float WeakForce = 0.5f, StrongForce = 0.8f, StrikeMinGap = 0.8f;
    private float _strikeNext, _strikeAt;
    private DamageEvent _strikeEvent;

    private static readonly RemoteCall<Vector2> StrikeCall = new("RemoteController.Strike", Route.ToHost,
        (w, d) => { w.Write(d.x); w.Write(d.y); }, r => new Vector2(r.ReadSingle(), r.ReadSingle()),
        (sender, d) => HostStrike(sender, d), perSecond: 4);

    private static readonly RemoteCall<(byte puppet, Vector2 hit, Vector2 toward)> PunchCall = new("RemoteController.Punch", Route.HostToAll,
        (w, m) => { w.Write(m.puppet); w.Write(m.hit.x); w.Write(m.hit.y); w.Write(m.toward.x); w.Write(m.toward.y); },
        r => (r.ReadByte(), new Vector2(r.ReadSingle(), r.ReadSingle()), new Vector2(r.ReadSingle(), r.ReadSingle())),
        (_, m) => RemoteControllerFx.Punch(PortKit.ById(m.puppet), m.hit, m.toward));

    private static void HostStrike(PlayerControl rcPlayer, Vector2 dir)
    {
        if (RoleState.Of(rcPlayer) is not RemoteController rc || !rc._operating || !PortKit.Alive(rcPlayer)) return;
        var t = rc.Target;
        if (!PortKit.Alive(t) || !Controlled.Contains(t.PlayerId) || t.inVent || t.onLadder || t.inMovingPlat) return;
        if (MeetingHud.Instance || ExileController.Instance || !GameClock.ShipAlive) return;
        if (!float.IsFinite(dir.x) || !float.IsFinite(dir.y)) return;
        float len = FxMath.Sqrt(dir.x * dir.x + dir.y * dir.y);
        if (len < 1e-3f) return;
        dir = new Vector2(dir.x / len, dir.y / len);
        float now = Time.time;
        if (now < rc._strikeNext || rc._strikeAt > 0f) return;
        rc._strikeNext = now + StrikeMinGap;
        Vector2 at = t.GetTruePosition();
        // 送られた向きはそのまま使わず、ホストが見ている位置から同じ探し方で壁のある向きに直す
        if (HammerSwing.FindTarget(at, dir, DamageProfile.Of(DamageKind.Blunt).Reach, out Vector2 found, out _, out _)) dir = found;
        ushort seed = (ushort)System.Environment.TickCount;
        var weak = new DamageEvent(DamageKind.Blunt, at, dir, 0f, WeakForce, seed);
        // 外壁など断られる所でも拳は当てて見せる (壊れないだけ)
        bool hits = TerrainDamage.TryResolve(weak, out var r, out string why);
        bool cracked = hits && r.Hp < WallDurability.MaxHp - 1;
        Vector2 face = hits ? r.Position : at + dir * 0.5f, toward = hits ? -r.Normal : dir;
        Plugin.Logger.LogInfo($"[RemoteController] strike puppet={t.PlayerId} at={at.x:0.00},{at.y:0.00} dir={dir.x:0.00},{dir.y:0.00} wall={(hits ? $"hp->{r.Hp}" : why)} cracked={cracked}");
        RemoteControllerFx.Punch(t, face, toward);
        PunchCall.Send((t.PlayerId, face, toward));
        if (!hits) return;
        rc._strikeEvent = cracked ? new DamageEvent(DamageKind.Blunt, at, dir, 0f, StrongForce, seed) : weak;
        rc._strikeAt = now + RemoteControllerFx.Contact;
    }

    // ホスト: 拳が届いた所で地形に当てる。会議・死・操作の終わりの後には当てない
    private void TickStrike()
    {
        if (Time.time < _strikeAt) return;
        _strikeAt = 0f;
        var t = Target;
        if (!_operating || !PortKit.Alive(t) || !PortKit.Alive(Player) || MeetingHud.Instance || ExileController.Instance || !GameClock.ShipAlive) return;
        string res = TerrainSync.RequestAs(_strikeEvent, t.PlayerId);
        Plugin.Logger.LogInfo($"[RemoteController] strike applied puppet={t.PlayerId} force={_strikeEvent.Force:0.0} -> {res}");
    }

    internal static bool Operating(PlayerControl p) => RoleState.Of(p) is RemoteController { _operating: true };
    internal bool IsOperating => _operating;
    internal byte MarkedId => _target;
}

// 操作中は「使う」・ベントのボタン・ベントの矢印を、操作している人の体で使う (原作と同じく本編の入口を差し替える)
[HarmonyPatch(typeof(UseButton), nameof(UseButton.DoClick))]
internal static class RemoteControllerUsePatch
{
    public static bool Prefix(UseButton __instance) => !RemoteController.HandleUse(__instance);
}

[HarmonyPatch(typeof(VentButton), nameof(VentButton.DoClick))]
internal static class RemoteControllerVentPatch
{
    public static bool Prefix(VentButton __instance) => !RemoteController.HandleVent(__instance);
}

[HarmonyPatch(typeof(Vent), nameof(Vent.TryMoveToVent))]
internal static class RemoteControllerVentMovePatch
{
    public static bool Prefix(Vent __instance, ref bool __result, [HarmonyArgument(0)] Vent otherVent, ref string error)
    {
        if (!RemoteController.HandleMove(__instance, otherVent, out bool result, out string err)) return true;
        __result = result;
        error = err;
        return false;
    }
}
