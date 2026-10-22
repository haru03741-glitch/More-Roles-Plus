using System;

namespace MoreRolesPlus.Terrain;

// 時戻しの記録 (火)。熱・燃料・材質 (油)・炎・焦げは、材質を決めたタイルの中で部屋から決めた姿と違う升だけ写す
// (火と油の出来事は材質を決めてから書くので、違う升は必ず決めたタイルの中にある)。待っている出来事は今のまま。
// 壁に貯めた熱 (Scalds・ホストだけ) も写す: 焼けて抜けた壁は時戻しで元に戻るので、熱と段も戻さないと同じ壁が早く抜けたり二度と焼けなくなったりする
internal static partial class FireSim
{
    internal sealed class RewindRec
    {
        public int CellN, ActN;
        public int[] Cell = Array.Empty<int>(); // 升・熱・熱の差・燃料・(材質 | 炎の印 << 8 | 炎 << 16 | 一覧 << 24)・(噴き上がり | 火花 << 8)・焦げ
        public int[] Act = Array.Empty<int>();
        public int ScaldN;
        public long[] ScaldKey = Array.Empty<long>();
        public int[] Scald = Array.Empty<int>(); // 熱・段・升・向き・終わり (5 個ずつ)
        public uint Digest;
    }

    private const int CellInts = 7;
    private static readonly RewindRing<RewindRec> Rewound = new();
    internal static int RewindCount => Rewound.Count;

    internal static void RewindClear() => Rewound.Clear();

    private static void RewindHead()
    {
        TerrainRewind.CheckShip();
        Rewound.Gap(_step);
        if (TerrainRewind.Due(TerrainRewind.Sim.Fire, _step, out int target, out _))
        {
            var s = Rewound.Find(target, out int at);
            if (s == null) TerrainRewind.NoteMissing(TerrainRewind.Sim.Fire, target);
            RewindLoad(s, _step - at);
            if (s != null && TerrainRewind.SelfCheck) { int now = _step; _step = at; uint d = Digest(); _step = now; TerrainRewind.Check(TerrainRewind.Sim.Fire, s.Digest, d); }
            Rewound.DropAfter(at);
        }
        if (_step % TerrainRewind.Every != 0) return;
        long t0 = TerrainRewind.SaveBegin();
        RewindSave(Rewound.Take(_step));
        TerrainRewind.SaveEnd(TerrainRewind.Sim.Fire, t0);
    }

    private static bool Changed(int c) =>
        _t[c] != 0 || _dt[c] != 0 || _flag[c] != 0 || _burn[c] != 0 || _inList[c] != 0 || _flareAt[c] != 0 || _arcAt[c] != 0 || _char[c] != 0
        || _mat[c] != _base[c] || _fuel[c] != MFuel[_base[c]];

    private static void RewindSave(RewindRec s)
    {
        int n = 0;
        for (int t = 0; t < _tileMat.Length; t++)
        {
            if (_tileMat[t] == 0) continue;
            int x0 = t % _tw * TileCells, y0 = t / _tw * TileCells;
            for (int cy = y0; cy < Math.Min(_h, y0 + TileCells); cy++)
            for (int cx = x0; cx < Math.Min(_w, x0 + TileCells); cx++)
            {
                int c = cy * _w + cx;
                if (!Changed(c)) continue;
                if (s.Cell.Length < (n + 1) * CellInts) Array.Resize(ref s.Cell, Math.Max((n + 1) * CellInts, s.Cell.Length * 2));
                int o = n * CellInts;
                s.Cell[o] = c;
                s.Cell[o + 1] = _t[c];
                s.Cell[o + 2] = _dt[c];
                s.Cell[o + 3] = _fuel[c];
                s.Cell[o + 4] = _mat[c] | _flag[c] << 8 | _burn[c] << 16 | _inList[c] << 24;
                s.Cell[o + 5] = _flareAt[c] | _arcAt[c] << 8;
                s.Cell[o + 6] = _char[c];
                n++;
            }
        }
        s.CellN = n;
        if (s.Act.Length < _actN) s.Act = new int[Math.Max(_actN, s.Act.Length * 2)];
        Array.Copy(_act, s.Act, _actN);
        s.ActN = _actN;
        int sn = Scalds.Count;
        if (s.ScaldKey.Length < sn) { s.ScaldKey = new long[Math.Max(sn, s.ScaldKey.Length * 2)]; s.Scald = new int[s.ScaldKey.Length * 5]; }
        int si = 0;
        foreach (var (key, w) in Scalds)
        {
            s.ScaldKey[si] = key;
            int o = si * 5;
            s.Scald[o] = w.Heat; s.Scald[o + 1] = w.Stage; s.Scald[o + 2] = w.K; s.Scald[o + 3] = w.D; s.Scald[o + 4] = w.Done ? 1 : 0;
            si++;
        }
        s.ScaldN = sn;
        if (TerrainRewind.SelfCheck) s.Digest = Digest();
    }

    // s = null は記録が無い時 (戻す所では火も油も無かった)
    private static void RewindLoad(RewindRec s, int shift)
    {
        // 材質を決めたタイルを部屋から決めた姿へ戻し、全部描き直す
        for (int t = 0; t < _tileMat.Length; t++)
        {
            if (_tileMat[t] == 0) continue;
            int x0 = t % _tw * TileCells, y0 = t / _tw * TileCells;
            for (int cy = y0; cy < Math.Min(_h, y0 + TileCells); cy++)
            for (int cx = x0; cx < Math.Min(_w, x0 + TileCells); cx++)
            {
                int c = cy * _w + cx;
                if (!Changed(c)) continue;
                _t[c] = _dt[c] = 0;
                _flag[c] = _burn[c] = _inList[c] = _flareAt[c] = _arcAt[c] = 0;
                _char[c] = 0;
                _mat[c] = _base[c];
                _fuel[c] = MFuel[_base[c]];
            }
            if (_tileDirty[t] == 0) { _tileDirty[t] = 1; DirtyTiles.Add(t); }
        }
        _actN = 0;
        BurningCount = 0;
        Scalds.Clear();
        BurnQueue.Clear();
        if (s == null) return;
        for (int i = 0; i < s.ScaldN; i++)
        {
            int o = i * 5;
            Scalds[s.ScaldKey[i]] = new Scald { Heat = s.Scald[o], Stage = s.Scald[o + 1], K = s.Scald[o + 2], D = s.Scald[o + 3], Done = s.Scald[o + 4] != 0 };
        }
        for (int i = 0; i < s.CellN; i++)
        {
            int o = i * CellInts, c = s.Cell[o];
            // 記録の後に閉じた升 (仕掛け壁など) は部屋の姿のまま
            if (!WaterSim.Open(c)) continue;
            EnsureTile(c);
            _t[c] = s.Cell[o + 1];
            _dt[c] = s.Cell[o + 2];
            _fuel[c] = s.Cell[o + 3];
            int m = s.Cell[o + 4];
            _mat[c] = (byte)m; _flag[c] = (byte)(m >> 8); _burn[c] = (byte)(m >> 16); _inList[c] = (byte)(m >> 24);
            // 噴き上がりと火花の刻みは下位 8 ビット (噴き上がりの 0 = まだ無い)
            int f = s.Cell[o + 5] & 255, a = s.Cell[o + 5] >> 8 & 255;
            _flareAt[c] = f == 0 ? (byte)0 : (byte)Math.Max(1, (f + shift) & 255);
            _arcAt[c] = (byte)((a + shift) & 255);
            _char[c] = (ushort)s.Cell[o + 6];
            Mark(c);
        }
        for (int i = 0; i < s.ActN; i++)
        {
            int k = s.Act[i];
            if (_inList[k] == 0) continue;
            _act[_actN++] = k;
            if (Burning(k)) BurningCount++;
        }
    }
}
