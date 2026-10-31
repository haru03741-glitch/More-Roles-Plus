// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Neutral/BlackHatHacker.cs (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Neutral;

// ブラックハットハッカー (第三陣営): 「ハック」で人をウィルスに感染させる。感染した人に近づいた人も少しずつ感染し、
// 生きている自分以外の全員の感染度が 100% になると単独で勝つ
public sealed class BlackHatHacker : RoleBase
{
    public override Team Team => Team.Neutral;
    public override string Color => "#2F4F4F";
    public override Text Name => new("BlackHatHacker.Name");
    public override string Reading => "ぶらっくはっとはっかー";
    public override Text Blurb => new("BlackHatHacker.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("BlackHatHacker.Hack", "snr_virus") };
    public static readonly FloatOpt HackCooldown = new("BlackHatHacker.Cooldown", 30f, 2.5f, 120f, 2.5f, "s");
    public static readonly IntOpt HackUses = new("BlackHatHacker.Uses", 2, 1, 10);
    public static readonly FloatOpt SpreadRange = new("BlackHatHacker.Range", 1.5f, 0.5f, 5f, 0.25f, "m");
    public static readonly FloatOpt SpreadSeconds = new("BlackHatHacker.Seconds", 10f, 1f, 60f, 1f, "s");

    // 感染度 (0〜100)。ホストが数えて全員へ配る
    private readonly Dictionary<byte, int> _virus = new();
    private float _tick;

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("BlackHatHacker.Hack"), "snr_virus")
        {
            Target = AbilityTarget.Player,
            Cooldown = () => HackCooldown,
            MaxUses = () => HackUses,
            OnUsed = (_, target) => _virus[target.PlayerId] = 100,
        });
    }

    // ホストの端末で: 1 秒ごとに、感染しきった人の近くにいる人の感染度を上げる
    [HostOnly]
    public override void OnFixedUpdate()
    {
        if (MeetingHud.Instance || !PortKit.Alive(Player)) return;
        _tick += Time.fixedDeltaTime;
        if (_tick < 1f) return;
        _tick = 0f;
        int step = Mathf.Max(1, Mathf.RoundToInt(100f / SpreadSeconds));
        var all = PlayerControl.AllPlayerControls;
        var add = new List<byte>();
        foreach (var p in all)
        {
            if (!PortKit.Alive(p) || p.PlayerId == PlayerId || Level(p.PlayerId) >= 100) continue;
            foreach (var q in all)
            {
                if (!PortKit.Alive(q) || q.PlayerId == p.PlayerId || Level(q.PlayerId) < 100) continue;
                if (Vector2.Distance(p.GetTruePosition(), q.GetTruePosition()) > SpreadRange) continue;
                add.Add(p.PlayerId);
                break;
            }
        }
        foreach (var id in add)
        {
            int v = Mathf.Min(100, Level(id) + step);
            _virus[id] = v;
            VirusCall.Send((PlayerId, id, (byte)v));
        }
        bool all100 = true;
        foreach (var p in all)
            if (PortKit.Alive(p) && p.PlayerId != PlayerId && Level(p.PlayerId) < 100) { all100 = false; break; }
        if (all100) GameEnd.Win(this);
    }

    private int Level(byte id) => _virus.TryGetValue(id, out var v) ? v : 0;

    private static readonly RemoteCall<(byte hacker, byte who, byte level)> VirusCall = new("BlackHatHacker.Virus", Route.HostToAll,
        (w, m) => { w.Write(m.hacker); w.Write(m.who); w.Write(m.level); }, r => (r.ReadByte(), r.ReadByte(), r.ReadByte()),
        (_, m) => { if (RoleState.Of(PortKit.ById(m.hacker)) is BlackHatHacker h) h._virus[m.who] = m.level; });

    public override string NameMark(PlayerControl seen)
    {
        int v = Level(seen.PlayerId);
        return v <= 0 || seen.PlayerId == PlayerId ? null : $"<size=70%><color=#2F4F4F>{v}%</color></size>";
    }
}
