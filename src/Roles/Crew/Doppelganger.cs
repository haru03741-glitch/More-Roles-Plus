// Ported from https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Roles/Crewmate/Doppelganger.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using HarmonyLib;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// ドッペルゲンガー (クルー): 地図を押した所へ、自分そっくりの分身を歩かせる (分身がいなければ足元に出る)。
// 分身の近く (決めた範囲) にいる人は地図に映る。「入れ替わり」で少しの間止まった後、分身と居場所を入れ替える。
// 分身は誰にでも本物に見え、キルできる。分身がキルされると本物が分身の所で死ぬ。会議・死亡で分身は消える
public sealed class Doppelganger : RoleBase
{
    public const string Hex = "#BF67D7";

    public override Team BaseTeam => Team.Crew;
    public override string Color => Hex;
    public override Text Name => new("Doppelganger.Name");
    public override string Reading => "どっぺるげんがー";
    public override Text Blurb => new("Doppelganger.Blurb");
    public override string Origin => "Nebula";
    public override HelpSection[] HelpSections => new HelpSection[]
    {
        new("Doppelganger.Command", "doppelganger_map"), new("Doppelganger.Swap", "doppelganger_swap"), new("Doppelganger.Despawn", "doppelganger_despawn"),
    };

    public static readonly FloatOpt SwapCooldown = new("Doppelganger.SwapCooldown", 10f, 0f, 60f, 2.5f, "s");
    public static readonly FloatOpt SwapDuration = new("Doppelganger.SwapDuration", 2f, 1f, 5f, 0.5f, "s");
    public static readonly FloatOpt DetectionRadius = new("Doppelganger.DetectionRadius", 3f, 0f, 10f, 0.25f, "x");

    private Ability _swap, _despawn;
    private Vector2? _flag;          // 自分の端末: 歩かせている行き先
    private int _walkId;

    private DoppelPuppet Mine => DoppelPuppet.Of(PlayerId);

    public override void OnAssigned()
    {
        System.Array.Clear(_lastStrikeMs); // 前の試合の時刻を持ち越さない
        AddAbility(new Ability(new Text("Doppelganger.Command"), "doppelganger_map")
        {
            CooldownAtStart = false,
            OnUse = () =>
            {
                var hud = Vanilla.Hud;
                if (hud) hud.ToggleMapVisible(new MapOptions { Mode = MapOptions.Modes.Normal, AllowMovementWhileMapOpen = true, ShowLivePlayerPosition = true });
                return false;
            },
        });
        _swap = AddAbility(new Ability(new Text("Doppelganger.Swap"), "doppelganger_swap")
        {
            Cooldown = () => SwapCooldown,
            Duration = () => SwapDuration,
            OnUse = () =>
            {
                var m = Mine;
                if (m == null || PlayerControl.LocalPlayer.inVent) return false;
                MoveSpeed.Add("DoppelSwap", 0f, SwapDuration + 0.25f);
                Stop(); // 歩いている分身はその場で待たせる
                Hearing.Self("doppel_swap_charge", 0.55f, 2f / SwapDuration); // 待つ長さに合わせて速める
                return true;
            },
            OnEnd = Swap,
        });
        _despawn = AddAbility(new Ability(new Text("Doppelganger.Despawn"), "doppelganger_despawn")
        {
            CooldownAtStart = false,
            Usable = () => Mine != null, // 使えない間はボタンを暗くする
            OnUse = () =>
            {
                if (Mine == null) return false;
                Despawn(true);
                return true;
            },
        });
        if (IsLocal) Lifespan.OnRelease(() =>
        {
            DoppelMap.Clear();
            Despawn(true);
            if (MapBehaviour.Instance && MapBehaviour.Instance.IsOpen) MapBehaviour.Instance.Close();
        });
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        bool has = Mine != null;
        _swap?.Button?.SetUsable(has && !_swap.InEffect);
        _despawn?.Button?.SetUsable(has);
        if (!has) _flag = null;
    }

    // ---- 分身 (自分の端末から全員へ) ----

    private void Summon(Vector2 root, bool flip)
    {
        SpawnCall.Send((PlayerId, root.x, root.y, flip));
        DoppelPuppet.Spawn(Player, root, flip);
    }

    private void Despawn(bool effect)
    {
        _flag = null;
        if (Mine == null) return;
        DespawnCall.Send((PlayerId, effect));
        DoppelPuppet.Despawn(PlayerId, effect);
    }

    private void Stop()
    {
        var m = Mine;
        if (m == null) return;
        _walkId++;
        _flag = null;
        StopCall.Send((PlayerId, m.Root.x, m.Root.y));
        m.StopAt(m.Root);
    }

    // 地図を押した所へ歩かせる (分身がいなければ足元に出してから)
    internal void Command(Vector2 to)
    {
        if (!PortKit.Alive(Player) || MeetingHud.Instance) return;
        if (_swap != null && _swap.InEffect) return; // 入れ替わりを待っている間は動かせない
        if (Mine == null) Summon(Player.transform.position, Player.cosmetics.FlipX);
        var m = Mine;
        if (m == null) return;
        var xs = new List<float>();
        var ys = new List<float>();
        Vector2 from = m.True;
        // 分身は当たり判定を持たないので、机・家具・扉・瓦礫も道から外す (本物が通れない所は通らない)
        if (!Dev.DummyNav.Path(from.x, from.y, to.x, to.y, xs, ys, solid: true) || xs.Count == 0)
        {
            PortKit.Notice(new Text("Doppelganger.NoPath").ToString());
            return;
        }
        float speed = Player.MyPhysics ? Player.MyPhysics.TrueSpeed : 2.5f;
        var pts = new float[xs.Count * 2];
        for (int i = 0; i < xs.Count; i++) { pts[i * 2] = xs[i]; pts[i * 2 + 1] = ys[i]; }
        int id = ++_walkId;
        _flag = to;
        WalkCall.Send((PlayerId, speed, pts));
        m.Walk(pts, speed, () =>
        {
            if (id != _walkId || Mine != m) return;
            _flag = null;
            PortKit.Flash(RoleDisplay.ParseColor(Hex), 0.5f);
            Hearing.Self("doppel_arrive", 0.6f);
        });
    }

    // 入れ替わり: 自分は分身の所へ飛び、分身は自分のいた所に出る
    private void Swap()
    {
        var m = Mine;
        var lp = PlayerControl.LocalPlayer;
        if (m == null || !PortKit.Alive(lp) || MeetingHud.Instance || !ShipStatus.Instance) return;
        Vector2 myRoot = lp.transform.position;
        bool myFlip = lp.cosmetics.FlipX;
        Vector2 to = m.Root;
        bool flip = m.Flip;
        // 分身が物の中にいたら入れ替えない (本物が机や扉の中に出る)
        var feet = m.True;
        if (Dev.DummyNav.InsideObject(feet.x, feet.y))
        {
            PortKit.Notice(new Text("Doppelganger.SwapBlocked").ToString());
            return;
        }
        DespawnCall.Send((PlayerId, false));
        DoppelPuppet.Despawn(PlayerId, false);
        Net.Teleport.Self(to);
        lp.cosmetics.SetFlipX(flip);
        Summon(myRoot, myFlip);
    }

    private static readonly RemoteCall<(byte id, float x, float y, bool flip)> SpawnCall = new("Doppel.Spawn", Route.Anyone,
        (w, m) => { w.Write(m.id); w.Write(m.x); w.Write(m.y); w.Write(m.flip); }, r => (r.ReadByte(), r.ReadSingle(), r.ReadSingle(), r.ReadBoolean()),
        (sender, m) => { if (sender) DoppelPuppet.Spawn(sender, FxMath.V2(m.x, m.y), m.flip); });

    private static readonly RemoteCall<(byte id, bool effect)> DespawnCall = new("Doppel.Despawn", Route.Anyone,
        (w, m) => { w.Write(m.id); w.Write(m.effect); }, r => (r.ReadByte(), r.ReadBoolean()),
        (sender, m) => { if (sender) DoppelPuppet.Despawn(sender.PlayerId, m.effect); });

    private static readonly RemoteCall<(byte id, float x, float y)> StopCall = new("Doppel.Stop", Route.Anyone,
        (w, m) => { w.Write(m.id); w.Write(m.x); w.Write(m.y); }, r => (r.ReadByte(), r.ReadSingle(), r.ReadSingle()),
        (sender, m) => { if (sender) DoppelPuppet.Of(sender.PlayerId)?.StopAt(FxMath.V2(m.x, m.y)); });

    private static readonly RemoteCall<(byte id, float speed, float[] pts)> WalkCall = new("Doppel.Walk", Route.Anyone,
        (w, m) => { w.Write(m.id); w.Write(m.speed); w.WritePacked(m.pts.Length); foreach (var f in m.pts) w.Write(f); },
        r =>
        {
            byte id = r.ReadByte();
            float speed = r.ReadSingle();
            int n = r.ReadPackedInt32();
            var pts = new float[n is >= 0 and <= MaxWalkFloats ? n : 0];
            for (int i = 0; i < pts.Length; i++) pts[i] = r.ReadSingle();
            return (id, speed, pts);
        },
        (sender, m) => { if (sender && m.pts.Length > 0) DoppelPuppet.Of(sender.PlayerId)?.Walk(m.pts, m.speed, null); });

    private const int MaxWalkFloats = 512; // 経路の点 (x, y) の上限。超えた電文は歩かせない

    // 会議で分身は消える (どの端末でも)
    private void OnMeetingStart(MeetingStartEvent e)
    {
        _flag = null;
        DoppelPuppet.Despawn(PlayerId, false);
    }

    [OnlyMine]
    private void OnMurdered(PlayerMurderedEvent e) => DoppelPuppet.Despawn(PlayerId, true);

    [OnlyMine]
    private void OnExiled(PlayerExiledEvent e) => DoppelPuppet.Despawn(PlayerId, false);

    // ---- 地図 (自分の端末) ----

    internal void UpdateMap(MapBehaviour map)
    {
        var m = Mine;
        bool on = PortKit.Alive(Player) && !MeetingHud.Instance && !(map.countOverlay && map.countOverlay.gameObject.activeSelf);
        DoppelMap.Draw(map, on ? m : null, on ? _flag : null, Player, DetectionRadius);
    }

    // 地図を開いている間、押した所へ歩かせる (毎フレーム)
    internal static void FrameTick()
    {
        if (RoleState.Local is not Doppelganger d || !Input.GetMouseButtonDown(0)) return;
        var map = MapBehaviour.Instance;
        if (!map || !map.IsOpen || !map.HerePoint || !ShipStatus.Instance || MeetingHud.Instance) return;
        if (map.countOverlay && map.countOverlay.gameObject.activeSelf) return;
        var cam = Camera.main;
        if (!cam) return;
        var parent = map.HerePoint.transform.parent;
        Vector3 local = parent.InverseTransformPoint(cam.ScreenToWorldPoint(Input.mousePosition));
        var ship = ShipStatus.Instance;
        var world = FxMath.V2(local.x * Mathf.Sign(ship.transform.localScale.x), local.y) * ship.MapScale;
        d.Command(world);
    }

    // ---- 分身へのキル ----

    // キルした人の端末から: その人の分身を倒した
    internal static readonly RemoteCall<byte> KillRequest = new("Doppel.KillReq", Route.ToHost,
        (w, id) => w.Write(id), r => r.ReadByte(),
        (killer, owner) => HostStrike(killer, owner));

    internal static void HostStrike(PlayerControl killer, byte ownerId)
    {
        var owner = PortKit.ById(ownerId);
        var m = DoppelPuppet.Of(ownerId);
        if (m == null || !PortKit.Alive(owner) || !PortKit.Alive(killer) || !PortKit.CanKillAnyone(killer)) return;
        if (MeetingHud.Instance || ExileController.Instance || killer.inVent) return;
        if (Vector2.Distance(killer.GetTruePosition(), m.True) > KillAbility.Reach(killer) + 1f) return;
        if (StruckTooSoon(killer)) return;
        if (BeforeMurderEvent.Ask(killer, owner) != null) return;
        _lastStrikeMs[killer.PlayerId] = System.Environment.TickCount64;
        KillAbility.HostCounted(killer);
        StruckCall.Send(ownerId);
        Struck(ownerId);
        // 本物が分身の所へ飛ぶのを待ってから倒す (体はそこに残る)
        PortKit.After(0.45f, () =>
        {
            if (PortKit.Alive(owner) && PortKit.Alive(killer)) killer.RpcMurderPlayer(owner, true);
        });
    }

    // ホスト: 飛び道具 (弾・ロケット・光線・隕石) が分身を倒した。届く距離と待ち時間はそれぞれの武器が確かめている。
    // 本物は分身の所へ引き寄せられて倒れる (近くでキルした時と同じ)
    internal static void HostStrikeRanged(PlayerControl killer, byte ownerId)
    {
        var owner = PortKit.ById(ownerId);
        if (DoppelPuppet.Of(ownerId) == null || !PortKit.Alive(owner) || !PortKit.Alive(killer) || killer.PlayerId == ownerId) return;
        if (MeetingHud.Instance || ExileController.Instance) return;
        if (BeforeMurderEvent.Ask(killer, owner) != null) return;
        StruckCall.Send(ownerId);
        Struck(ownerId);
        PortKit.After(0.45f, () =>
        {
            if (PortKit.Alive(owner) && PortKit.Alive(killer)) killer.RpcMurderPlayer(owner, true);
        });
    }

    // ホスト: 円の中の分身を全部倒す (爆発・隕石)
    internal static void HostStrikeArea(PlayerControl killer, Vector2 at, float radius)
    {
        if (!DoppelPuppet.Any) return;
        _area.Clear();
        foreach (var m in DoppelPuppet.Each)
        {
            var t = m.True;
            float dx = t.x - at.x, dy = t.y - at.y;
            if (dx * dx + dy * dy <= radius * radius) _area.Add(m.Owner);
        }
        foreach (var id in _area) HostStrikeRanged(killer, id);
    }
    private static readonly List<byte> _area = new();

    // ホスト: キルの待ち時間の中なら断る (押した人の画面の待ち時間だけに頼らない)。インポスターは本編のキルの待ち時間を
    // 前に分身を倒した時から数える
    private static readonly long[] _lastStrikeMs = new long[256];

    private static bool StruckTooSoon(PlayerControl killer)
    {
        if (Dev.DevCooldown.Skips(killer)) return false;
        if (KillAbility.HostCoolingDown(killer)) return true;
        long last = _lastStrikeMs[killer.PlayerId];
        if (last == 0 || !PortKit.IsImpostor(killer)) return false;
        float cd = GameManager.Instance ? GameManager.Instance.LogicOptions.GetKillCooldown() : 0f;
        return (System.Environment.TickCount64 - last) / 1000f < cd - 1f;
    }

    private static readonly RemoteCall<byte> StruckCall = new("Doppel.Struck", Route.HostToAll,
        (w, id) => w.Write(id), r => r.ReadByte(), (_, id) => Struck(id));

    private static void Struck(byte ownerId)
    {
        var m = DoppelPuppet.Of(ownerId);
        if (m == null) return;
        // 本物は分身の所へ引き寄せられて消えるので、元いた所に煙を残す
        var owner = PortKit.ById(ownerId);
        if (owner) DoppelPuppet.Poof(owner.GetTruePosition(), 0.8f);
        var lp = PlayerControl.LocalPlayer;
        if (lp && lp.PlayerId == ownerId)
        {
            Net.Teleport.Self(m.Root);
            lp.cosmetics.SetFlipX(m.Flip);
        }
        DoppelPuppet.Despawn(ownerId, false);
    }
}

// 分身の見た目 (全員の端末で同じ動き)。本編の人の型から体の部品だけを残して作る
internal sealed class DoppelPuppet
{
    private static readonly Dictionary<byte, DoppelPuppet> All = new();
    private static bool _bound;

    public readonly byte Owner;
    private readonly GameObject _go;
    private readonly CosmeticsLayer _cos;
    private readonly PlayerAnimations _anims;
    private readonly Vector2 _offset; // 体の根から足元 (GetTruePosition) まで
    private float[] _path;
    private int _next;
    private float _speed;
    private Action _arrived;
    private bool _running;

    public Vector2 Root { get; private set; }
    public Vector2 True => Root + _offset;
    public bool Flip => _cos && _cos.FlipX;
    public bool Moving => _path != null;

    internal static DoppelPuppet Of(byte owner) => All.TryGetValue(owner, out var m) ? m : null;
    internal static IEnumerable<DoppelPuppet> Each => All.Values;
    internal static bool Any => All.Count > 0;

    private DoppelPuppet(PlayerControl owner, Vector2 root, bool flip)
    {
        Owner = owner.PlayerId;
        _offset = owner.Collider ? owner.Collider.offset : FxMath.V2(0f, -0.3636f);
        var prefab = AmongUsClient.Instance.PlayerPrefab;
        prefab.gameObject.SetActive(false);
        _go = UnityEngine.Object.Instantiate(prefab.gameObject);
        prefab.gameObject.SetActive(true);
        _go.name = "MrpDoppelganger";
        UnityEngine.Object.Destroy(_go.GetComponent<PlayerControl>());
        UnityEngine.Object.Destroy(_go.GetComponent<PlayerPhysics>());
        UnityEngine.Object.Destroy(_go.GetComponent<Rigidbody2D>());
        UnityEngine.Object.Destroy(_go.GetComponent<CircleCollider2D>());
        UnityEngine.Object.Destroy(_go.GetComponent<BoxCollider2D>());
        UnityEngine.Object.Destroy(_go.GetComponent<AudioSource>());
        UnityEngine.Object.Destroy(_go.GetComponent<PassiveButton>());
        UnityEngine.Object.Destroy(_go.GetComponent<CustomNetworkTransform>());
        UnityEngine.Object.Destroy(_go.GetComponent<DummyBehaviour>());
        UnityEngine.Object.Destroy(_go.GetComponent<HnSImpostorScreamSfx>());
        _go.SetActive(true);
        _cos = _go.GetComponentInChildren<CosmeticsLayer>();
        _anims = _go.GetComponentInChildren<PlayerAnimations>();
        Root = root;
        _go.transform.position = new Vector3(root.x, root.y, (root.y + _offset.y) / 1000f);
        Dress(owner, flip);
        FxRoot.TrackWorld(_go);
    }

    // ---- 時戻し: 分身は全員の端末が同じ電文で動かすので、各端末が自分の手元の出し入れと歩きを取り消しとして積む ----

    private readonly struct Pose
    {
        public readonly Vector2 Root;
        public readonly float[] Path;
        public readonly int Next;
        public readonly float Speed;
        public readonly Action Arrived;
        public readonly bool Flip;
        public Pose(DoppelPuppet m) { Root = m.Root; Path = m._path; Next = m._next; Speed = m._speed; Arrived = m._arrived; Flip = m.Flip; }
    }

    private void Apply(in Pose p)
    {
        Root = p.Root;
        _path = p.Path;
        _next = p.Next;
        _speed = p.Speed;
        _arrived = p.Arrived;
        if (_cos) _cos.SetFlipX(p.Flip);
        if (_go) _go.transform.position = new Vector3(Root.x, Root.y, (Root.y + _offset.y) / 1000f);
    }

    private const float MarkEvery = 0.25f; // 歩いている間の位置を積む間 (時戻しの値の記録と同じ)
    private float _nextMark;

    // 今の歩きの状態へ戻る取り消しを積む
    private void Mark()
    {
        if (!Rewind.Recording) return;
        var p = new Pose(this);
        Rewind.Undo(_ => Apply(p));
    }

    // 消えた分身を取り消しで出し直す (煙と音は出さない)
    private static void Restore(byte owner, Pose p)
    {
        var pc = PortKit.ById(owner);
        if (!pc || pc.Data == null || !AmongUsClient.Instance || !AmongUsClient.Instance.PlayerPrefab) return;
        if (All.Remove(owner, out var cur)) cur.Destroy();
        try
        {
            var m = new DoppelPuppet(pc, p.Root, p.Flip);
            m.Apply(p);
            All[owner] = m;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"doppelganger restore: {e.Message}"); }
    }

    private static void UndoSpawn(byte owner, DoppelPuppet made, bool hadOld, Pose old)
    {
        if (All.TryGetValue(owner, out var cur) && cur == made)
        {
            All.Remove(owner);
            made.Destroy();
        }
        if (hadOld) Restore(owner, old);
    }

    private void Dress(PlayerControl owner, bool flip)
    {
        if (!_cos) return;
        _cos.EnsureInitialized(PlayerBodyTypes.Normal);
        if (_anims) _anims.SetBodyType(PlayerBodyTypes.Normal, _cos.currentBodySprite.normalCosmeticOffset, _cos.currentBodySprite.flippedCosmeticOffset);
        _cos.normalBodySprite.Visible = true;
        var outfit = owner.Data.Outfits[owner.CurrentOutfitType];
        _cos.SetMaskType(PlayerMaterial.MaskType.None);
        _dressType = (int)owner.CurrentOutfitType;
        _dressColor = outfit.ColorId;
        _cos.SetColor(outfit.ColorId);
        _cos.SetHat(outfit.HatId, outfit.ColorId);
        _cos.SetSkin(outfit.SkinId, outfit.ColorId, null);
        _cos.SetVisor(outfit.VisorId, outfit.ColorId);
        _cos.SetNamePosition(new Vector3(0f, string.IsNullOrEmpty(outfit.HatId) ? 0.8f : 1f, -0.5f));
        if (_cos.nameText) _cos.nameText.text = outfit.PlayerName;
        _cos.SetFlipX(flip);
        _cos.TogglePet(false);
        if (_anims) _anims.PlayIdleAnimation();
        _cos.AnimateSkinIdle();
    }

    internal static void Spawn(PlayerControl owner, Vector2 root, bool flip)
    {
        if (!owner || !AmongUsClient.Instance || !AmongUsClient.Instance.PlayerPrefab) return;
        bool hadOld = All.Remove(owner.PlayerId, out var old);
        var oldPose = hadOld ? new Pose(old) : default;
        if (hadOld) old.Destroy(); // 出し直し (入れ替わりの印は残さない)
        if (!_bound)
        {
            _bound = true;
            RoleState.Match.OnRelease(() =>
            {
                foreach (var m in All.Values) m.Destroy();
                All.Clear();
                _quietOut.Clear();
                _bound = false;
            });
        }
        try
        {
            var made = new DoppelPuppet(owner, root, flip);
            All[owner.PlayerId] = made;
            byte id = owner.PlayerId;
            Rewind.Undo(_ => UndoSpawn(id, made, hadOld, oldPose)); // 出す前へ戻ると分身は消える (前の分身がいたら戻る)
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"doppelganger: {e.Message}"); }
        Poof(root, 0.6f);
        // 入れ替わりは「黙って消す → 本人のいた所に出す」の順で届く。直前に黙って消えていれば両方の地点で空気が抜ける
        if (_quietOut.TryGetValue(owner.PlayerId, out var q) && Time.time - q.T < SwapWindow)
        {
            _quietOut.Remove(owner.PlayerId);
            Poof(q.At, 0.6f);
            Hearing.At("doppel_swap", q.At.x, q.At.y, SoundRange, 0.8f);
            Hearing.At("doppel_swap", root.x, root.y, SoundRange, 0.8f);
        }
        else Hearing.At("doppel_spawn", root.x, root.y, SoundRange, 0.75f);
    }

    private const float SoundRange = 7f;
    private const float SwapWindow = 0.6f;
    private static readonly Dictionary<byte, (float T, Vector2 At)> _quietOut = new();

    internal static void Despawn(byte owner, bool effect)
    {
        if (!All.TryGetValue(owner, out var m)) return;
        All.Remove(owner);
        var pose = new Pose(m);
        Rewind.Undo(_ => Restore(owner, pose)); // 消える前へ戻ると分身が戻る
        if (effect)
        {
            Poof(m.Root, 1f);
            Hearing.At("doppel_vanish", m.Root.x, m.Root.y, SoundRange, 0.75f);
        }
        else _quietOut[owner] = (Time.time, m.Root);
        m.Destroy();
    }

    // 煙のように消える・現れる: その場に床に沿って広がる煙を立てる (壁を壊した時の土煙と同じ部品)
    internal static void Poof(Vector2 root, float size)
    {
        if (!ShipStatus.Instance) return;
        Terrain.DustCloud.Kick(root, 0.25f + 0.35f * size, (ushort)FxMath.Range(0, 65535));
    }

    private void Destroy()
    {
        if (_go) UnityEngine.Object.Destroy(_go);
    }

    // 足元の点の列 (x, y, x, y…) をたどって歩く
    internal void Walk(float[] path, float speed, Action arrived)
    {
        Mark();
        _path = path;
        _next = 0;
        _speed = Mathf.Max(speed, 0.5f);
        _arrived = arrived;
    }

    internal void StopAt(Vector2 root)
    {
        Mark();
        _path = null;
        _arrived = null;
        Root = root;
    }

    internal static void TickAll(float dt)
    {
        if (All.Count == 0 || RewindPlayback.Active) return; // 逆再生の間は止める (見えるのは録画の逆再生)
        foreach (var m in All.Values) m.Tick(dt);
    }

    // 本物の見た目 (変身・カモフラ) が変わったら分身も着替える (分身だけ元の服で正体が分かるのを防ぐ)。毎フレームは読まない
    private int _dressType = -1, _dressColor = -1;
    private float _nextDressCheck;

    private void FollowOutfit()
    {
        if (Time.time < _nextDressCheck) return;
        _nextDressCheck = Time.time + 0.25f;
        var owner = PortKit.ById(Owner);
        if (!owner || owner.Data == null) return;
        int type = (int)owner.CurrentOutfitType;
        var outfit = owner.Data.Outfits[owner.CurrentOutfitType];
        if (outfit == null || (type == _dressType && outfit.ColorId == _dressColor)) return;
        Dress(owner, _cos && _cos.FlipX);
    }

    private void Tick(float dt)
    {
        if (!_go) return;
        FollowOutfit();
        bool moving = false;
        if (_path != null && !MeetingHud.Instance)
        {
            if (Time.time >= _nextMark) { _nextMark = Time.time + MarkEvery; Mark(); }
            float budget = _speed * dt;
            while (budget > 0f && _path != null)
            {
                var target = FxMath.V2(_path[_next * 2], _path[_next * 2 + 1]) - _offset;
                var d = target - Root;
                float len = d.magnitude;
                if (len <= budget)
                {
                    Root = target;
                    budget -= len;
                    if (++_next * 2 >= _path.Length)
                    {
                        _path = null;
                        var a = _arrived;
                        _arrived = null;
                        a?.Invoke();
                    }
                }
                else
                {
                    Root += d / len * budget;
                    budget = 0f;
                }
                if (Mathf.Abs(d.x) > 0.01f && _cos) _cos.SetFlipX(d.x < 0f);
                moving = true;
            }
        }
        _go.transform.position = new Vector3(Root.x, Root.y, (Root.y + _offset.y) / 1000f);
        if (_anims && _cos)
        {
            if (moving && !_running) { _anims.PlayRunAnimation(); _cos.AnimateSkinRun(); }
            else if (!moving && _running) { _anims.PlayIdleAnimation(); _cos.AnimateSkinIdle(); }
        }
        _running = moving;
        UpdateName();
    }

    // 名前は見えている時だけ (影の中・壁の向こうでは本物と同じく出さない)
    private void UpdateName()
    {
        if (!_cos || !_cos.nameText) return;
        var lp = PlayerControl.LocalPlayer;
        bool show = true;
        if (lp && lp.Data != null && !lp.Data.IsDead)
        {
            float r = lp.lightSource ? lp.lightSource.viewDistance : 3f;
            Vector2 me = lp.GetTruePosition();
            show = Vector2.Distance(me, True) <= r && !PhysicsHelpers.AnythingBetween(me, True, Constants.ShadowMask, false);
        }
        var holder = _cos.nameText.transform.parent ? _cos.nameText.transform.parent.gameObject : _cos.nameText.gameObject;
        if (holder.activeSelf != show) holder.SetActive(show);
    }

    internal void SetOutline(bool on, Color color)
    {
        if (_cos) _cos.SetOutline(on, new Il2CppSystem.Nullable<Color>(color));
    }
}

// 地図: 分身の居場所・行き先の旗・分身の近くの人
internal static class DoppelMap
{
    private static SpriteRenderer _me, _flag;
    private static readonly List<SpriteRenderer> Near = new();

    internal static void Clear()
    {
        if (_me) UnityEngine.Object.Destroy(_me.gameObject);
        if (_flag) UnityEngine.Object.Destroy(_flag.gameObject);
        foreach (var r in Near) if (r) UnityEngine.Object.Destroy(r.gameObject);
        Near.Clear();
        _me = _flag = null;
    }

    private static Vector3 ToMap(Vector2 world, float z)
    {
        var ship = ShipStatus.Instance;
        Vector3 pos = world / ship.MapScale;
        pos.x *= Mathf.Sign(ship.transform.localScale.x);
        pos.z = z;
        return pos;
    }

    // 地図の旗: 細い竿と、風で少しなびく三角の布 (高さ 1 単位・竿の根元が原点より下の端)
    private static Sprite _flagSprite;
    private static Sprite FlagSprite => _flagSprite ? _flagSprite : _flagSprite = FxSprites.Rect(48, 64, (x, y) =>
    {
        float pole = Mathf.Clamp01((0.045f - MathF.Abs(x + 0.30f)) / 0.02f) * Mathf.Clamp01((0.48f - MathF.Abs(y)) / 0.02f);
        float u = (x + 0.27f) / 0.62f;                       // 竿から布の先へ 0..1
        float mid = 0.27f - 0.05f * MathF.Sin(u * 5f) * u;   // 布の真ん中の高さ (先ほど揺れる)
        float half = 0.17f * (1f - u);
        float cloth = u is > 0f and < 1f ? Mathf.Clamp01((half - MathF.Abs(y - mid)) / 0.03f) : 0f;
        return MathF.Max(pole, cloth);
    }, "MrpDoppelFlag");

    internal static void Draw(MapBehaviour map, DoppelPuppet m, Vector2? flag, PlayerControl owner, float radius)
    {
        var parent = map.HerePoint.transform.parent;
        if (!_me)
        {
            _me = UnityEngine.Object.Instantiate(map.HerePoint, parent);
            _me.name = "MrpDoppelHere";
            _me.material.SetInt(PlayerMaterial.MaskLayer, 255);
            _flag = new GameObject("MrpDoppelFlag").AddComponent<SpriteRenderer>();
            _flag.transform.SetParent(parent, false);
            _flag.gameObject.layer = map.HerePoint.gameObject.layer;
            _flag.sprite = FlagSprite;
            _flag.color = RoleDisplay.ParseColor(Doppelganger.Hex);
            _flag.transform.localScale = Vector3.one * 0.32f;
        }
        _me.gameObject.SetActive(m != null);
        if (m != null)
        {
            owner.SetPlayerMaterialColors(_me);
            _me.color = new Color(0.88f, 0.88f, 0.88f, 1f);
            _me.transform.localPosition = ToMap(m.True, -1.5f);
        }
        _flag.gameObject.SetActive(m != null && flag != null);
        if (m != null && flag is Vector2 f) _flag.transform.localPosition = ToMap(f, -2f) + new Vector3(0.072f, 0.155f, 0f); // 竿の根元をその点に

        // 分身の近くの人
        var near = new List<PlayerControl>();
        if (m != null && radius > 0f)
            foreach (var p in PlayerControl.AllPlayerControls)
                if (PortKit.Alive(p) && p.PlayerId != owner.PlayerId && Vector2.Distance(p.GetTruePosition(), m.True) < radius) near.Add(p);
        while (Near.Count < near.Count)
        {
            var r = UnityEngine.Object.Instantiate(map.HerePoint, parent);
            r.material.SetInt(PlayerMaterial.MaskLayer, 255);
            Near.Add(r);
        }
        for (int i = 0; i < Near.Count; i++)
        {
            var r = Near[i];
            if (!r) continue;
            if (i >= near.Count) { r.gameObject.SetActive(false); continue; }
            near[i].SetPlayerMaterialColors(r);
            r.transform.localPosition = ToMap(near[i].GetTruePosition(), -1f);
            r.gameObject.SetActive(true);
        }
    }
}

[HarmonyPatch(typeof(MapBehaviour), nameof(MapBehaviour.FixedUpdate))]
internal static class DoppelMapPatch
{
    public static void Postfix(MapBehaviour __instance)
    {
        if (RoleState.Local is not Doppelganger d) return;
        try { d.UpdateMap(__instance); }
        catch (Exception e) { Plugin.Logger.LogWarning($"doppelganger map: {e.Message}"); }
    }
}

// キルボタン: 狙う人がいない時、届く所にいる他人の分身を狙う
[HarmonyPatch(typeof(KillButton), nameof(KillButton.SetTarget))]
internal static class DoppelKillTargetPatch
{
    internal static byte Target = 255;
    private static DoppelPuppet _lit;

    public static void Postfix(KillButton __instance)
    {
        if (__instance is null) return;
        try { Pick(__instance); }
        catch (Exception e) { Plugin.Logger.LogWarning($"doppelganger kill target: {e.Message}"); }
    }

    private static void Pick(KillButton b)
    {
        DoppelPuppet best = null;
        var lp = PlayerControl.LocalPlayer;
        if (b.currentTarget == null && lp && PortKit.Alive(lp) && PortKit.CanKillAnyone(lp) && !lp.inVent && lp.CanMove && !MeetingHud.Instance)
        {
            float reach = KillAbility.Reach(lp);
            Vector2 me = lp.GetTruePosition();
            foreach (var m in DoppelPuppet.Each)
            {
                if (m.Owner == lp.PlayerId || !PortKit.Alive(PortKit.ById(m.Owner))) continue;
                float d = Vector2.Distance(me, m.True);
                if (d > reach || PhysicsHelpers.AnythingBetween(me, m.True, Constants.ShipAndObjectsMask, false)) continue;
                reach = d;
                best = m;
            }
        }
        if (_lit != null && _lit != best) _lit.SetOutline(false, Color.clear);
        _lit = best;
        Target = best?.Owner ?? 255;
        if (best == null) return;
        best.SetOutline(true, Palette.ImpostorRed);
        b.SetEnabled();
    }
}

[HarmonyPatch(typeof(KillButton), nameof(KillButton.DoClick))]
[HarmonyPriority(Priority.First)]
internal static class DoppelKillClickPatch
{
    public static bool Prefix(KillButton __instance)
    {
        if (__instance is null || __instance.currentTarget != null || DoppelKillTargetPatch.Target == 255) return true;
        var lp = PlayerControl.LocalPlayer;
        if (!__instance.isActiveAndEnabled || __instance.isCoolingDown || !PortKit.Alive(lp) || !lp.CanMove) return false;
        byte owner = DoppelKillTargetPatch.Target;
        if (AmongUsClient.Instance.AmHost) Doppelganger.HostStrike(lp, owner);
        else Doppelganger.KillRequest.Send(owner);
        return false;
    }
}
