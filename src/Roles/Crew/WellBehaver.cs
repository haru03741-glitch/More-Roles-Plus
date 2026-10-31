// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/WellBehaver.cs (GPL-3.0)
// SuperNewRoles/CustomObject/Garbage.cs
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// 品行方正 (クルー): タスクは無い。ほかの誰か 1 人の足元に一定の間隔でゴミが落ち、「ゴミ拾い」で拾う。
// 船のゴミが上限 (品行方正の人数 × 設定の数) に達すると死ぬ
public sealed class WellBehaver : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#FEC458";
    public override Text Name => new("WellBehaver.Name");
    public override string Reading => "ひんこうほうせい";
    public override Text Blurb => new("WellBehaver.Blurb");
    public override string Origin => "SNR";
    public override bool TasksCount => false;
    public override HelpSection[] HelpSections => new HelpSection[] { new("WellBehaver.Pick", "snr_garbage") };

    public static readonly IntOpt LimitTrash = new("WellBehaver.LimitTrash", 15, 15, 60);
    public static readonly FloatOpt Frequency = new("WellBehaver.Frequency", 5f, 1f, 30f, 1f, "s");
    public static readonly BoolOpt AllCanSee = new("WellBehaver.AllCanSee", true);

    private const float Reach = 0.75f;

    // 船に落ちているゴミ (作った人の番号・その人の何個目か → 印)。全員の端末で同じ
    private static readonly Dictionary<(byte, int), SpriteRenderer> Garbage = new();

    private byte _garbager = 255;
    private float _timer;
    private int _index;

    private static int Limit
    {
        get
        {
            int n = 0;
            foreach (var r in RoleState.All) if (r is WellBehaver) n++;
            return LimitTrash * Mathf.Max(n, 1);
        }
    }

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("WellBehaver.Pick"), "snr_garbage")
        {
            OnUse = () =>
            {
                var key = Nearest();
                if (key is not { } k) return false;
                PickCall.Send(k);
                Remove(k);
                return true;
            },
        });
        Lifespan.OnRelease(() =>
        {
            foreach (var g in Garbage.Values) if (g) Object.Destroy(g.gameObject);
            Garbage.Clear();
        });
        if (IsLocal) PortKit.After(1f, () => { if (RoleState.Local == this) SnrKit.AssignOwnTasks(0, 0, 0); });
    }

    // 近く (壁越しは除く) で一番近いゴミ
    private static (byte, int)? Nearest()
    {
        var lp = PlayerControl.LocalPlayer;
        Vector2 me = lp.GetTruePosition();
        (byte, int)? best = null;
        float bestD = Reach;
        foreach (var kv in Garbage)
        {
            if (!kv.Value) continue;
            Vector2 at = kv.Value.transform.position;
            float d = Vector2.Distance(me, at);
            if (d > bestD || PhysicsHelpers.AnythingBetween(me, at, Constants.ShadowMask, false)) continue;
            bestD = d;
            best = kv.Key;
        }
        return best;
    }

    // 会議の後はゴミを落とす人を選び直す
    [LocalOnly]
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (PortKit.Alive(Player)) Reassign();
    }

    private void Reassign()
    {
        var pool = new List<PlayerControl>();
        foreach (var p in PlayerControl.AllPlayerControls)
            if (PortKit.Alive(p) && !p.AmOwner) pool.Add(p);
        _garbager = pool.Count > 0 ? pool[Random.Range(0, pool.Count)].PlayerId : (byte)255;
    }

    [LocalOnly]
    public override void OnFixedUpdate()
    {
        if (!PortKit.Alive(Player) || MeetingHud.Instance || ExileController.Instance) return;
        var hud = Vanilla.Hud;
        if (hud && hud.IsIntroDisplayed) return;
        var g = PortKit.ById(_garbager);
        if (!PortKit.Alive(g))
        {
            Reassign();
            g = PortKit.ById(_garbager);
        }
        if (!g) return;
        _timer += Time.fixedDeltaTime;
        if (_timer >= Frequency)
        {
            _timer = 0f;
            var m = (g.PlayerId, _index++, (Vector2)g.transform.position);
            DropCall.Send(m);
            Drop(m.Item1, m.Item2, m.Item3);
        }
        if (Garbage.Count >= Limit) PortKit.Suicide();
    }

    public override string NameMark(PlayerControl seen) =>
        seen && seen.PlayerId == PlayerId ? $"<color=#FEC458>({Garbage.Count}/{Limit})</color>" : null;

    private static readonly RemoteCall<(byte by, int index, Vector2 at)> DropCall = new("WellBehaver.Drop", Route.Anyone,
        (w, m) => { w.Write(m.by); w.WritePacked(m.index); w.Write(m.at.x); w.Write(m.at.y); },
        r => (r.ReadByte(), r.ReadPackedInt32(), new Vector2(r.ReadSingle(), r.ReadSingle())),
        (_, m) => Drop(m.by, m.index, m.at));

    private static readonly RemoteCall<(byte, int)> PickCall = new("WellBehaver.Pick", Route.Anyone,
        (w, k) => { w.Write(k.Item1); w.WritePacked(k.Item2); }, r => (r.ReadByte(), r.ReadPackedInt32()),
        (_, k) => Remove(k));

    private static void Drop(byte by, int index, Vector2 at)
    {
        var key = (by, index);
        if (Garbage.ContainsKey(key)) return;
        var r = PortKit.Marker(RoleState.Match, at, "snr_garbage", 0.45f);
        if (!r) return;
        var pos = r.transform.position;
        r.transform.position = new Vector3(pos.x, pos.y, at.y / 1000f + 0.0005f);
        r.gameObject.SetActive(AllCanSee || RoleState.Local is WellBehaver);
        Garbage[key] = r;
    }

    private static void Remove((byte, int) key)
    {
        if (!Garbage.TryGetValue(key, out var r)) return;
        if (r) Object.Destroy(r.gameObject);
        Garbage.Remove(key);
    }
}
