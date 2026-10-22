using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace MoreRolesPlus.Terrain;

// 時戻しの記録 (水)。写すのは水の量と流れ・動いている升の一覧・粒・落ちている水・浸水の口。
// 漏れの口 (Sources)・扉の噴き出し (Jets) は壊れた壁と扉に付いた物なので今のまま (壁は戻さない)
internal static partial class WaterSim
{
    internal sealed class RewindRec
    {
        public int ActN, SortedN, WetN, PartN;
        public int[] Act = Array.Empty<int>();
        public int[] Cell = Array.Empty<int>();     // 動いている升ごとに 高さ・流れ出し 4・運ぶ量 4
        public byte[] Stir = Array.Empty<byte>();
        public int[] Wet = Array.Empty<int>();      // 眠っている濡れた升 (升・高さ)
        public int[] Part = Array.Empty<int>();     // 粒ごとに Px Py Ph Ox Oy Oh Vx Vy Vh Stuck Pm
        public readonly List<(int Due, int To, int Amount)> Flying = new();
        public readonly List<(int K, int End, int Rate)> Inflows = new();
        public bool Blowing;
        public uint Digest;
    }

    private const int CellInts = 9, PartInts = 11;
    private static readonly RewindRing<RewindRec> Rewound = new();
    internal static int RewindCount => Rewound.Count;

    internal static void RewindClear() => Rewound.Clear();

    // 中身は捨てる (書く前に伸ばす所だけで使う)
    private static void Grow<T>(ref T[] a, int n) { if (a.Length < n) a = new T[Math.Max(n, a.Length * 2)]; }

    // 刻みの頭 (_step を進める前・前の刻みは終わっている)
    private static void RewindHead()
    {
        TerrainRewind.CheckShip();
        Rewound.Gap(_step);
        if (TerrainRewind.Due(TerrainRewind.Sim.Water, _step, out int target, out _))
        {
            EnsureIdle(); // 裏の絵の仕事が同じ配列を読んでいれば終わるまで待つ
            var s = Rewound.Find(target, out int at);
            if (s == null) TerrainRewind.NoteMissing(TerrainRewind.Sim.Water, target);
            RewindLoad(s, _step - at, at);
            Rewound.DropAfter(at);
        }
        if (_step % TerrainRewind.Every != 0) return;
        long t0 = TerrainRewind.SaveBegin();
        EnsureIdle();
        RewindSave(Rewound.Take(_step));
        TerrainRewind.SaveEnd(TerrainRewind.Sim.Water, t0);
    }

    private static void RewindSave(RewindRec s)
    {
        int n = _actN;
        s.ActN = n;
        s.SortedN = _sortedN;
        Grow(ref s.Act, n);
        Grow(ref s.Cell, n * CellInts);
        Grow(ref s.Stir, n);
        Array.Copy(_act, s.Act, n);
        for (int a = 0; a < n; a++)
        {
            int k = _act[a], o = a * CellInts;
            s.Cell[o] = _hgt[k];
            for (int d = 0; d < 4; d++) { s.Cell[o + 1 + d] = _flux[k * 4 + d]; s.Cell[o + 5 + d] = _move[k * 4 + d]; }
            s.Stir[a] = _stir[k];
        }
        // 眠っている濡れた升 (動いている升は上で写した)。升の大半は乾いているので、まとめて 0 の所は飛ばす
        int w = 0;
        var hgt = _hgt;
        var blocks = MemoryMarshal.Cast<int, Vector<int>>(hgt.AsSpan());
        int vn = Vector<int>.Count;
        for (int b = 0; b <= blocks.Length; b++)
        {
            int k0 = b * vn, k1 = b == blocks.Length ? hgt.Length : k0 + vn;
            if (b < blocks.Length && blocks[b] == Vector<int>.Zero) continue;
            for (int k = k0; k < k1; k++)
            {
                if (hgt[k] == 0 || _inList[k] != 0) continue;
                if (s.Wet.Length < w + 2) Array.Resize(ref s.Wet, Math.Max(w + 2, s.Wet.Length * 2));
                s.Wet[w++] = k;
                s.Wet[w++] = hgt[k];
            }
        }
        s.WetN = w / 2;
        int p = Particles;
        s.PartN = p;
        Grow(ref s.Part, p * PartInts);
        for (int i = 0; i < p; i++)
        {
            int o = i * PartInts;
            s.Part[o] = Px[i]; s.Part[o + 1] = Py[i]; s.Part[o + 2] = Ph[i];
            s.Part[o + 3] = Ox[i]; s.Part[o + 4] = Oy[i]; s.Part[o + 5] = Oh[i];
            s.Part[o + 6] = Vx[i]; s.Part[o + 7] = Vy[i]; s.Part[o + 8] = Vh[i];
            s.Part[o + 9] = Stuck[i] ? 1 : 0; s.Part[o + 10] = Pm[i];
        }
        s.Flying.Clear();
        s.Flying.AddRange(Flying);
        s.Inflows.Clear();
        foreach (var f in Inflows) s.Inflows.Add((f.K, f.End, f.Rate));
        s.Blowing = _blowing;
        if (TerrainRewind.SelfCheck) s.Digest = Digest(_step);
    }

    // s = null は記録が無い時 (戻す所では水が無かった)。shift = 記録の中の刻みの値に足す量
    private static void RewindLoad(RewindRec s, int shift, int at)
    {
        // 今の水を全部消す (描き直しの印を付けて)
        for (int a = 0; a < _actN; a++)
        {
            int k = _act[a];
            for (int d = 0; d < 4; d++) _flux[k * 4 + d] = _move[k * 4 + d] = 0;
            _stir[k] = 0;
            _inList[k] = 0;
        }
        _actN = _sortedN = 0;
        var hgt = _hgt;
        var blocks = MemoryMarshal.Cast<int, Vector<int>>(hgt.AsSpan());
        int vn = Vector<int>.Count;
        for (int b = 0; b <= blocks.Length; b++)
        {
            int k0 = b * vn, k1 = b == blocks.Length ? hgt.Length : k0 + vn;
            if (b < blocks.Length && blocks[b] == Vector<int>.Zero) continue;
            for (int k = k0; k < k1; k++)
            {
                if (hgt[k] == 0) continue;
                hgt[k] = 0;
                MarkTiles(k);
            }
        }
        Particles = 0;
        Flying.Clear();
        Inflows.Clear();
        _blowing = false;
        if (s == null) return;

        // 記録の後に閉じた升 (仕掛け壁など) の水は捨てる
        int kept = 0;
        for (int a = 0; a < s.ActN; a++)
        {
            int k = s.Act[a], o = a * CellInts;
            if (_open[k] == 0) continue;
            if (a < s.SortedN) kept++;
            _act[_actN++] = k;
            _inList[k] = 1;
            _stir[k] = s.Stir[a];
            hgt[k] = s.Cell[o];
            for (int d = 0; d < 4; d++) { _flux[k * 4 + d] = s.Cell[o + 1 + d]; _move[k * 4 + d] = s.Cell[o + 5 + d]; }
            if (hgt[k] != 0) MarkTiles(k);
        }
        _sortedN = kept;
        for (int i = 0; i < s.WetN; i++)
        {
            int k = s.Wet[i * 2];
            if (_open[k] == 0) continue;
            hgt[k] = s.Wet[i * 2 + 1];
            MarkTiles(k);
        }
        int p = Math.Min(s.PartN, MaxParticles);
        for (int i = 0; i < p; i++)
        {
            int o = i * PartInts;
            Px[i] = s.Part[o]; Py[i] = s.Part[o + 1]; Ph[i] = s.Part[o + 2];
            Ox[i] = s.Part[o + 3]; Oy[i] = s.Part[o + 4]; Oh[i] = s.Part[o + 5];
            Vx[i] = s.Part[o + 6]; Vy[i] = s.Part[o + 7]; Vh[i] = s.Part[o + 8];
            Stuck[i] = s.Part[o + 9] != 0; Pm[i] = s.Part[o + 10];
        }
        Particles = p;
        Flying.AddRange(s.Flying);
        if (TerrainRewind.SelfCheck) TerrainRewind.Check(TerrainRewind.Sim.Water, s.Digest, Digest(at));
        for (int i = 0; i < Flying.Count; i++) Flying[i] = (Flying[i].Due + shift, Flying[i].To, Flying[i].Amount);
        foreach (var f in s.Inflows) Inflows.Add(new Inflow { K = f.K, End = f.End == 0 ? 0 : f.End + shift, Rate = f.Rate });
        _blowing = s.Blowing;
    }
}
