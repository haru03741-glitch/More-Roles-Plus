// Ported from https://github.com/TeamUchuaddon/UchuAddon UchuAddon/Scripts/Role/Impostor/Magician.cs (role idea and logic; UchuAddon allows porting them, no assets used)
// The role is credited by UchuAddon to https://github.com/yukieiji/ExtremeRoles (GPL-3.0)
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;

namespace MoreRolesPlus.Roles.Impostor;

// マジシャン (インポスター): 「マジック」で生きている全員の位置を無作為に入れ替える。
// 全員の画面が赤く光り、タスクの画面は閉じ、ベントの中の人は外に出る
public sealed class Magician : RoleBase
{
    public override Team BaseTeam => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Magician.Name");
    public override string Reading => "まじしゃん";
    public override Text Blurb => new("Magician.Blurb");
    public override string Origin => "Uchu, ER";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Magician.Magic", "magician_magic") };

    public static readonly FloatOpt MagicCooldown = new("Magician.MagicCooldown", 20f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt NumOfMagic = new("Magician.NumOfMagic", 4, 1, 15);
    public static readonly IntOpt HazardSpots = new("Magician.HazardSpots", 2, 0, 3);

    public override void OnAssigned()
    {
        AddAbility(new Ability(new Text("Magician.Magic"), "magician_magic")
        {
            Cooldown = () => MagicCooldown,
            MaxUses = () => NumOfMagic,
            OnUse = () =>
            {
                MagicRequest.Send(0); // ホストなら手元で
                return true;
            },
        });
    }

    private static readonly RemoteCall<byte> MagicRequest = new("Magician.Request", Route.ToHost,
        (w, _) => w.Write((byte)0), r => r.ReadByte(),
        (sender, _) => { if (RoleState.Of(sender) is Magician && PortKit.Alive(sender)) HostMagic(); }, perSecond: 2);

    // ホスト: 並べ替えを決めて配る
    private static void HostMagic()
    {
        if (MeetingHud.Instance || ExileController.Instance) return;
        var alive = new List<PlayerControl>();
        foreach (var p in PlayerControl.AllPlayerControls) if (PortKit.Alive(p)) alive.Add(p);
        if (alive.Count < 2) return;
        // 並べ替えた i 番目の人を、i+1 番目の人の元の位置へ (全員が元と違う場所へ行く)
        for (int i = alive.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (alive[i], alive[j]) = (alive[j], alive[i]);
        }
        var orig = new Dictionary<byte, Vector2>();
        foreach (var p in alive) orig[p.PlayerId] = p.transform.position;
        var moves = new List<(byte id, float x, float y)>();
        for (int i = 0; i < alive.Count; i++)
        {
            var to = orig[alive[(i + 1) % alive.Count].PlayerId];
            moves.Add((alive[i].PlayerId, to.x, to.y));
        }
        SendToHazards(alive, moves);
        MagicCall.Send(moves);
        OnMagic(moves);
    }

    // ホスト: 船に深い水・火事・吸い出している穴があれば、インポスター以外の何人かの行き先をその場所に差し替える。
    // どこも即死の所ではなく、すぐ動けば逃げられる所 (足の着かない水の中・燃えている床の縁・吸われるが口の際ではない所)。
    // 種類ごとに 1 か所だけ選ぶので、同じ所に 2 人は落ちない。空いた元の位置には誰も行かない
    private static void SendToHazards(List<PlayerControl> alive, List<(byte id, float x, float y)> moves)
    {
        int want = HazardSpots;
        if (want <= 0) return;
        var spots = new List<Vector2>(3);
        if (WaterSpot(out var w)) spots.Add(w);
        if (FireSpot(out var f)) spots.Add(f);
        if (PullSpot(out var d)) spots.Add(d);
        if (spots.Count == 0) return;
        var victims = new List<int>();
        for (int i = 0; i < alive.Count; i++) if (!PortKit.IsImpostor(alive[i])) victims.Add(i);
        int n = Mathf.Min(want, Mathf.Min(spots.Count, victims.Count));
        for (int s = 0; s < n; s++)
        {
            int vi = Random.Range(s, victims.Count), si = Random.Range(s, spots.Count);
            (victims[s], victims[vi]) = (victims[vi], victims[s]);
            (spots[s], spots[si]) = (spots[si], spots[s]);
            var p = alive[victims[s]];
            // 選んだ点は足元。体の位置は当たり判定のずれの分だけ上
            Vector2 off = p.Collider ? p.Collider.offset : Vector2.zero;
            var at = spots[s];
            moves[victims[s]] = (p.PlayerId, at.x - off.x, at.y - off.y);
            Plugin.Logger.LogInfo($"[Magician] hazard {p.PlayerId} -> {at.x:0.00},{at.y:0.00} (spots={spots.Count})");
        }
    }

    private const float Probe = 0.5f; // 水と穴の周りを探す網の目

    // 足の着かない深さの水 (溺れが数えられる深さ)。網の目の点から無作為に 1 つ
    private static bool WaterSpot(out Vector2 at)
    {
        at = default;
        if (!Terrain.WaterSim.Running || !Terrain.WaterSim.Ready) return false;
        var org = Terrain.WaterSim.Origin;
        float sx = Terrain.WaterSim.W * Terrain.WaterSim.Cell, sy = Terrain.WaterSim.H * Terrain.WaterSim.Cell;
        int seen = 0;
        for (float y = org.y + Probe * 0.5f; y < org.y + sy; y += Probe)
            for (float x = org.x + Probe * 0.5f; x < org.x + sx; x += Probe)
            {
                var p = new Vector2(x, y);
                if (Terrain.WaterSim.CurrentAt(p, out _, out _) < Terrain.CrewSwim.BraceMaxDepth || !Pusher.CanStand(p)) continue;
                if (Random.Range(0, ++seen) == 0) at = p;
            }
        return seen > 0;
    }

    // 燃えている床の縁 (隣に燃えていない升がある燃えている升)。一歩出れば火から出られる
    private static bool FireSpot(out Vector2 at)
    {
        at = default;
        if (!Terrain.FireSim.Ready || Terrain.FireSim.BurningCount <= 0) return false;
        int w = Terrain.FireSim.W, h = Terrain.FireSim.H, seen = 0;
        var org = Terrain.FireSim.Origin;
        float cell = Terrain.FireSim.Cell;
        var act = Terrain.FireSim.ActiveCells;
        for (int i = 0; i < act.Count; i++)
        {
            int k = act[i], cx = k % w, cy = k / w;
            if (!Terrain.FireSim.Burning(k) || cx < 1 || cy < 1 || cx >= w - 1 || cy >= h - 1) continue;
            if (Terrain.FireSim.Burning(k - 1) && Terrain.FireSim.Burning(k + 1) && Terrain.FireSim.Burning(k - w) && Terrain.FireSim.Burning(k + w)) continue;
            var p = new Vector2(org.x + (cx + 0.5f) * cell, org.y + (cy + 0.5f) * cell);
            if (!Pusher.CanStand(p)) continue;
            if (Random.Range(0, ++seen) == 0) at = p;
        }
        return seen > 0;
    }

    private const float PullMin = 0.6f;   // 吸う強さ (歩く速さの倍) がこれ以上
    private const float MouthGap = 2f;    // 口の線からこれ以上離れた所 (口の際は掴む間もなく吸い出される)
    private const float PullSearch = 5f;  // 口の周りを探す半径

    // 吸い出している穴の手前 (流れに引かれるが、角に掴まるか走れば逃げられる所)
    private static bool PullSpot(out Vector2 at)
    {
        at = default;
        if (!Terrain.Decompression.Pulling) return false;
        int seen = 0;
        foreach (var br in Terrain.Decompression.OpenedBreaches)
        {
            if (br.Sealed || br.Lines.Count == 0) continue;
            var (a, b) = br.Lines[0];
            float mx = (a.x + b.x) * 0.5f, my = (a.y + b.y) * 0.5f;
            for (float y = my - PullSearch; y <= my + PullSearch; y += Probe)
                for (float x = mx - PullSearch; x <= mx + PullSearch; x += Probe)
                {
                    var p = new Vector2(x, y);
                    if (!Terrain.Decompression.PullAt(p, out _, out float mul, out _) || mul < PullMin) continue;
                    if (Terrain.Decompression.MouthDist(p, out _) < MouthGap || !Pusher.CanStand(p)) continue;
                    if (Random.Range(0, ++seen) == 0) at = p;
                }
        }
        return seen > 0;
    }

    private static readonly RemoteCall<List<(byte id, float x, float y)>> MagicCall = new("Magician.Magic", Route.HostToAll,
        (w, list) => { w.Write((byte)list.Count); foreach (var m in list) { w.Write(m.id); w.Write(m.x); w.Write(m.y); } },
        r => { int n = r.ReadByte(); var l = new List<(byte, float, float)>(); for (int i = 0; i < n; i++) l.Add((r.ReadByte(), r.ReadSingle(), r.ReadSingle())); return l; },
        (_, list) => OnMagic(list));

    private static readonly Color[] Sparkle = { new(1f, 0.2f, 0.25f, 0.95f), new(1f, 0.85f, 0.3f, 0.95f), new(0.8f, 0.4f, 1f, 0.95f) };

    // 全員の端末で: 光って、自分の体 (と自分の動かすダミー) を動かす
    private static void OnMagic(List<(byte id, float x, float y)> moves)
    {
        var lp = PlayerControl.LocalPlayer;
        PortKit.Flash(new Color(1f, 0.1f, 0.1f, 0.5f));
        Hearing.Self("magician_swap", 0.7f); // 全員が動かされるので、どの端末でも手元の音
        // 全員の画面で: 消える所と現れる所が光る。粒の数には上限があるので、画面の外の分は出さない
        // (全員分を出すと画面の中の分が上限で欠ける)
        Vector2 eye = lp ? lp.GetTruePosition() : default;
        bool InView(float px, float py) => !lp || FxMath.Abs(px - eye.x) < 7f && FxMath.Abs(py - eye.y) < 4.5f;
        foreach (var (id, x, y) in moves)
        {
            var p = PortKit.ById(id);
            if (p && !p.inVent && p.Visible)
            {
                var at = p.GetTruePosition();
                if (InView(at.x, at.y)) RoleSparks.Poof(at, Sparkle[id % Sparkle.Length]);
            }
            if (InView(x, y - 0.36f)) RoleSparks.Poof(new Vector2(x, y - 0.36f), Sparkle[id % Sparkle.Length]);
        }
        foreach (var (id, x, y) in moves)
        {
            var p = PortKit.ById(id);
            if (!p || !(p.AmOwner || Dev.Dummy.Owns(p))) continue;
            if (p.AmOwner && Minigame.Instance) Minigame.Instance.Close();
            if (p.inVent)
            {
                var vent = PortKit.NearestVent(p.GetTruePosition(), 5f);
                if (vent) p.MyPhysics.RpcExitVent(vent.Id);
                p.inVent = false;
            }
            var to = new Vector2(x, y);
            p.NetTransform.RpcSnapTo(to);
        }
    }
}
