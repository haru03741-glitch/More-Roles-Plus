// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Orpheus.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// オルフェウス (インポスター): 仲間のインポスターが死ぬと、会議の後に船のどこかへその「儀式の死体」が現れる (設定の会議の数まで)。
// 儀式の死体のそばで「蘇生」すると、次の会議の後にその仲間が元の役職で生き返る。
// 儀式の死体は近く (通報できる距離の何割か) でないと通報できず、通報されるとその仲間はもう生き返らない
public sealed class Orpheus : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Orpheus.Name");
    public override string Reading => "おるふぇうす";
    public override Text Blurb => new("Orpheus.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Orpheus.Revive", "snr_lyre") };

    public static readonly IntOpt CorpseTurns = new("Orpheus.CorpseTurns", 2, 1, 5);
    public static readonly FloatOpt ReviveCooldown = new("Orpheus.Cooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly FloatOpt ReportPercent = new("Orpheus.ReportPercent", 50f, 0f, 100f, 5f, "%");

    private sealed class Entry
    {
        public int Rounds;
        public bool Pending;
        public bool Targetable;
        public RoleBase Role;
    }

    // ホストだけが持つ: 生き返れる仲間
    private static readonly Dictionary<byte, Entry> Entries = new();
    // 全員の端末: 今出ている儀式の死体 / 次の会議で出す知らせ
    private static readonly Dictionary<byte, DeadBody> Ritual = new();
    private static readonly HashSet<byte> RangeHidden = new();
    private static bool _banner;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Orpheus.Revive"), "snr_lyre")
        {
            Cooldown = () => ReviveCooldown,
            OnUse = () =>
            {
                if (!PortKit.Alive(Player) || MeetingHud.Instance) return false;
                byte id = NearestRitual();
                if (id == 255) return false;
                if (AmongUsClient.Instance.AmHost) HostConsume(Player, id);
                else ConsumeCall.Send(id);
                return true;
            },
        });
        Lifespan.OnRelease(() =>
        {
            foreach (var b in Ritual.Values) if (b) Object.Destroy(b.gameObject);
            Ritual.Clear();
            Entries.Clear();
            RangeHidden.Clear();
            _banner = false;
        });
    }

    private static bool AnyAlive()
    {
        foreach (var r in RoleState.All) if (r is Orpheus && PortKit.Alive(r.Player)) return true;
        return false;
    }

    private byte NearestRitual()
    {
        var lp = PlayerControl.LocalPlayer;
        Vector2 me = lp.GetTruePosition();
        float best = lp.MaxReportDistance;
        byte id = 255;
        foreach (var kv in Ritual)
        {
            var b = kv.Value;
            if (!b || !b.gameObject.activeInHierarchy) continue;
            float d = Vector2.Distance(me, b.TruePosition);
            if (d > best || PhysicsHelpers.AnythingBetween(me, b.TruePosition, Constants.ShipAndObjectsMask, false)) continue;
            best = d;
            id = kv.Key;
        }
        return id;
    }

    // ホスト: インポスターが死んだら生き返れる仲間に入れる
    [HostOnly]
    private void OnDeath(PlayerMurderedEvent e) => Register(e.Target);

    [HostOnly]
    private void OnExiled(PlayerExiledEvent e) => Register(e.Player);

    private static void Register(PlayerControl victim)
    {
        if (!victim || !PortKit.IsImpostor(victim) || Entries.ContainsKey(victim.PlayerId) || !AnyAlive()) return;
        Entries[victim.PlayerId] = new Entry { Rounds = CorpseTurns, Role = RoleState.Of(victim) };
    }

    // 全員の端末で: 会議が始まったら、通報されたもの以外の儀式の死体を消す。蘇生があれば知らせる
    private void OnMeeting(MeetingCalledEvent e)
    {
        byte reported = e.Body != null ? e.Body.PlayerId : (byte)255;
        foreach (var kv in new List<KeyValuePair<byte, DeadBody>>(Ritual))
        {
            if (kv.Key == reported) continue;
            if (kv.Value) Object.Destroy(kv.Value.gameObject);
            Ritual.Remove(kv.Key);
        }
        if (AmongUsClient.Instance.AmHost && e.Body != null && Entries.TryGetValue(reported, out var entry))
        {
            if (!entry.Targetable || !entry.Pending) Entries.Remove(reported);
        }
        if (_banner)
        {
            _banner = false;
            PortKit.After(1f, () => PortKit.Notice(new Text("Orpheus.Banner").ToString()));
        }
    }

    // ホスト: 会議の後に蘇生を済ませ、残りの仲間の儀式の死体を出す
    [HostOnly]
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (FirstOrpheus() != this) return; // 何人いても 1 回だけ
        foreach (var kv in new List<KeyValuePair<byte, Entry>>(Entries))
        {
            if (!kv.Value.Pending) continue;
            Entries.Remove(kv.Key);
            var p = PortKit.ById(kv.Key);
            if (!p || p.Data == null || !p.Data.IsDead) continue;
            PortKit.HostRevive(p);
            if (kv.Value.Role != null) RoleState.Change(p, Options.Registry.Roles.Find(r => r.GetType() == kv.Value.Role.GetType()));
            if (p.Data.Role && !p.Data.Role.IsImpostor) p.RpcSetRole(AmongUs.GameOptions.RoleTypes.Impostor, true);
        }
        if (!AnyAlive())
        {
            foreach (var id in new List<byte>(Entries.Keys)) ExpireCall.Send(id);
            Entries.Clear();
            return;
        }
        foreach (var kv in new List<KeyValuePair<byte, Entry>>(Entries))
        {
            if (kv.Value.Rounds <= 0)
            {
                Entries.Remove(kv.Key);
                continue;
            }
            Vector2 at = RandomSpot(kv.Key);
            SpawnCall.Send((kv.Key, at));
            Spawn(kv.Key, at);
            kv.Value.Rounds--;
            kv.Value.Targetable = true;
        }
    }

    private static Orpheus FirstOrpheus()
    {
        foreach (var r in RoleState.All) if (r is Orpheus o) return o;
        return null;
    }

    private static Vector2 RandomSpot(byte victim)
    {
        if (MapArea.MapDatabase.TryGetRandomDeadBodySpawnPositionForCurrentMap(out var pooled)) return pooled;
        var map = MapArea.MapDatabase.GetCurrentMapData();
        if (map != null && MapArea.MapDatabase.TryGetSpawnScanBoundsForCurrentMap(out var min, out var max))
            for (int i = 0; i < 48; i++)
            {
                var p = new Vector2(Random.Range(min.x, max.x), Random.Range(min.y, max.y));
                if (map.CheckMapArea(p) && !map.IsNearPlayerSpawnPosition(p)) return p;
            }
        var v = PortKit.ById(victim);
        return v ? (Vector2)v.transform.position : Vector2.zero;
    }

    // 全員の端末で: 儀式の死体は近くでないと通報できない
    [LocalOnly]
    public override void OnFixedUpdate() => UpdateReportability();

    private static void UpdateReportability()
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp || Ritual.Count == 0) return;
        float max = lp.MaxReportDistance * (ReportPercent / 100f);
        Vector2 me = lp.GetTruePosition();
        foreach (var kv in Ritual)
        {
            var b = kv.Value;
            if (!b) continue;
            bool ok = Vector2.Distance(me, b.TruePosition) <= max && !PhysicsHelpers.AnythingBetween(me, b.TruePosition, Constants.ShipAndObjectsMask, false);
            if (!ok)
            {
                if (!b.Reported) RangeHidden.Add(kv.Key);
                b.Reported = true;
            }
            else if (RangeHidden.Remove(kv.Key)) b.Reported = false;
        }
    }

    private static readonly RemoteCall<(byte id, Vector2 at)> SpawnCall = new("Orpheus.Spawn", Route.HostToAll,
        (w, m) => { w.Write(m.id); w.Write(m.at.x); w.Write(m.at.y); }, r => (r.ReadByte(), new Vector2(r.ReadSingle(), r.ReadSingle())),
        (_, m) => Spawn(m.id, m.at));
    private static readonly RemoteCall<byte> ConsumeCall = new("Orpheus.Consume", Route.ToHost,
        (w, id) => w.Write(id), r => r.ReadByte(), (sender, id) => HostConsume(sender, id));
    private static readonly RemoteCall<byte> ConsumedCall = new("Orpheus.Consumed", Route.HostToAll,
        (w, id) => w.Write(id), r => r.ReadByte(), (_, id) => Consumed(id));
    private static readonly RemoteCall<byte> ExpireCall = new("Orpheus.Expire", Route.HostToAll,
        (w, id) => w.Write(id), r => r.ReadByte(), (_, id) => Remove(id));

    private static void Spawn(byte id, Vector2 at)
    {
        var p = PortKit.ById(id);
        if (!p) return;
        Remove(id);
        var body = SnrKit.SpawnBody(p, at);
        body.transform.position = new Vector3(at.x, at.y, at.y / 1000f);
        var mark = PortKit.Marker(RoleState.Match, at + new Vector2(-0.3f, 0.1f), "snr_lyre", 0.3f, new UnityEngine.Color(1f, 1f, 1f, 0.7f));
        if (mark) mark.transform.SetParent(body.transform, true);
        Ritual[id] = body;
        UpdateReportability();
    }

    private static void Remove(byte id)
    {
        if (Ritual.TryGetValue(id, out var b) && b) Object.Destroy(b.gameObject);
        Ritual.Remove(id);
        RangeHidden.Remove(id);
    }

    private static void HostConsume(PlayerControl orpheus, byte id)
    {
        if (RoleState.Of(orpheus) is not Orpheus || !PortKit.Alive(orpheus)) return;
        if (!Entries.TryGetValue(id, out var e) || e.Pending || !Ritual.ContainsKey(id)) return;
        e.Pending = true;
        ConsumedCall.Send(id);
        Consumed(id);
    }

    private static void Consumed(byte id)
    {
        foreach (var b in Object.FindObjectsOfType<DeadBody>())
            if (b && b.ParentId == id) Object.Destroy(b.gameObject);
        Ritual.Remove(id);
        RangeHidden.Remove(id);
        _banner = true;
    }
}
