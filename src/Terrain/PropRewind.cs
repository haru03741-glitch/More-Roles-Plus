using System;
using System.Collections.Generic;

namespace MoreRolesPlus.Terrain;

// 時戻しの記録 (物)。確定の状態 (A) と、確定で動いている物の一覧を写す。待っている出来事は今のまま。
// 天板の刻印 (WaterSim の家具の升) は次の水の刻みの頭で StampTops が A に合わせ直す
internal static partial class PropSim
{
    internal sealed class RewindRec
    {
        public int PropN, ActN;
        public int[] St = Array.Empty<int>();
        public int[] Act = Array.Empty<int>();
        public uint Digest;
        public bool Lifted;   // 読み戻しで切り抜いたままにした家具がある (指紋が記録と違って正しい)
    }

    private const int StInts = 12;
    private static readonly RewindRing<RewindRec> Rewound = new();
    private static readonly List<Prop> RewindMoved = new();
    internal static int RewindCount => Rewound.Count;

    internal static void RewindClear() => Rewound.Clear();

    private static void RewindHead()
    {
        TerrainRewind.CheckShip();
        int step = Auth.Step;
        Rewound.Gap(step);
        if (TerrainRewind.Due(TerrainRewind.Sim.Prop, step, out int target, out _))
        {
            var s = Rewound.Find(target, out int at);
            if (s == null) TerrainRewind.NoteMissing(TerrainRewind.Sim.Prop, target);
            RewindLoad(s, step - at);
            if (s != null && !s.Lifted && TerrainRewind.SelfCheck) TerrainRewind.Check(TerrainRewind.Sim.Prop, s.Digest, Digest());
            Rewound.DropAfter(at);
        }
        if (step % TerrainRewind.Every != 0) return;
        long t0 = TerrainRewind.SaveBegin();
        RewindSave(Rewound.Take(step));
        TerrainRewind.SaveEnd(TerrainRewind.Sim.Prop, t0);
    }

    private static void Pack(St a, int[] d, int o)
    {
        d[o] = a.Px; d[o + 1] = a.Py; d[o + 2] = a.Vx; d[o + 3] = a.Vy; d[o + 4] = a.Spin;
        d[o + 5] = a.Ang; d[o + 6] = a.Tip; d[o + 7] = a.WobStart; d[o + 8] = a.WobAmp;
        d[o + 9] = (a.Active ? 1 : 0) | (a.Moved ? 2 : 0) | (a.Pulled ? 4 : 0) | (a.Rattle ? 8 : 0) | (a.Gone ? 16 : 0) | (a.Stuck ? 32 : 0);
        d[o + 10] = a.PortalIn;
        d[o + 11] = unchecked((int)a.Rng);
    }

    private static bool Same(St a, int[] d, int o)
    {
        int f = (a.Active ? 1 : 0) | (a.Moved ? 2 : 0) | (a.Pulled ? 4 : 0) | (a.Rattle ? 8 : 0) | (a.Gone ? 16 : 0) | (a.Stuck ? 32 : 0);
        return d[o] == a.Px && d[o + 1] == a.Py && d[o + 2] == a.Vx && d[o + 3] == a.Vy && d[o + 4] == a.Spin && d[o + 5] == a.Ang
            && d[o + 6] == a.Tip && d[o + 7] == a.WobStart && d[o + 8] == a.WobAmp && d[o + 9] == f && d[o + 10] == a.PortalIn && d[o + 11] == unchecked((int)a.Rng);
    }

    private static void Unpack(St a, int[] d, int o, int shift)
    {
        a.Px = d[o]; a.Py = d[o + 1]; a.Vx = d[o + 2]; a.Vy = d[o + 3]; a.Spin = d[o + 4];
        a.Ang = d[o + 5]; a.Tip = d[o + 6]; a.WobStart = d[o + 7] == int.MinValue ? int.MinValue : d[o + 7] + shift; a.WobAmp = d[o + 8];
        int f = d[o + 9];
        a.Active = (f & 1) != 0; a.Moved = (f & 2) != 0; a.Pulled = (f & 4) != 0; a.Rattle = (f & 8) != 0; a.Gone = (f & 16) != 0; a.Stuck = (f & 32) != 0;
        a.PortalIn = d[o + 10];
        a.Rng = unchecked((uint)d[o + 11]);
    }

    private static void RewindSave(RewindRec s)
    {
        int n = Props.Count;
        if (s.St.Length < n * StInts) s.St = new int[n * StInts];
        for (int i = 0; i < n; i++) Pack(Props[i].A, s.St, i * StInts);
        s.PropN = n;
        if (s.Act.Length < Auth.Act.Count) s.Act = new int[Math.Max(Auth.Act.Count, 8)];
        for (int i = 0; i < Auth.Act.Count; i++) s.Act[i] = Auth.Act[i].Index;
        s.ActN = Auth.Act.Count;
        if (TerrainRewind.SelfCheck) s.Digest = Digest();
    }

    // s = null は記録が無い時 (戻す所では物はまだ動いていなかった = 何もしない)
    private static void RewindLoad(RewindRec s, int shift)
    {
        if (s == null) return;
        if (s.PropN != Props.Count) { TerrainRewind.NoteMissing(TerrainRewind.Sim.Prop, s.PropN); return; }
        RewindMoved.Clear();
        s.Lifted = false;
        for (int i = 0; i < s.PropN; i++)
        {
            var p = Props[i];
            int o = i * StInts;
            if (Same(p.A, s.St, o)) continue;
            // 部屋の絵から切り抜いた家具は切り抜いたまま元の所へ置く (DrawHeavy は動いた物しか描かない)
            bool lifted = p.Kind == Kind.Heavy && p.A.Moved;
            Unpack(p.A, s.St, o, shift);
            if (lifted && !p.A.Moved) { p.A.Moved = true; s.Lifted = true; }
            RewindMoved.Add(p);
        }
        Auth.Act.Clear();
        for (int i = 0; i < s.ActN; i++) Auth.Act.Add(Props[s.Act[i]]);
        // 見せる用は確定から計算し直す。止まっていた物は Resync が描き直さないので、ここで止まった所へ描く
        foreach (var p in RewindMoved)
        {
            p.D.CopyFrom(p.A);
            if (!p.A.Active) ShowStopped(p, p.D);
        }
        _resync = true;
    }
}
