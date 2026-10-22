using System;

namespace MoreRolesPlus.Terrain;

// 時戻しの記録 (減圧)。範囲ごとの気圧だけ戻す。口 (Breaches) は抜けた外壁に付いた物なので今のまま (壁は戻さない・泡の絵も口の物に付いている)。
// 範囲の番号は塗り直すたびに変わるので、塗り直していれば記録の時の範囲から升の数で混ぜて移す (塗り直しの時と同じ)
internal static partial class Decompression
{
    internal sealed class RewindRec
    {
        public int Gen;
        public int[] Comp;                  // 記録の時の範囲の番号 (同じ塗りの間の記録で共有する)
        public int[] Press = Array.Empty<int>();
        public int PressN;
        public uint Digest;
    }

    private static int _compGen;
    private static readonly RewindRing<RewindRec> Rewound = new();
    private static RewindRec _lastSaved;
    internal static int RewindCount => Rewound.Count;

    internal static void RewindClear()
    {
        Rewound.Clear();
        _lastSaved = null;
    }

    private static void RewindHead()
    {
        TerrainRewind.CheckShip();
        Rewound.Gap(_step);
        if (TerrainRewind.Due(TerrainRewind.Sim.Decomp, _step, out int target, out _))
        {
            var s = Rewound.Find(target, out int at);
            if (s == null) TerrainRewind.NoteMissing(TerrainRewind.Sim.Decomp, target);
            RewindLoad(s);
            if (s != null && TerrainRewind.SelfCheck) { int now = _step; _step = at; uint d = Digest(); _step = now; TerrainRewind.Check(TerrainRewind.Sim.Decomp, s.Digest, d); }
            Rewound.DropAfter(at);
        }
        if (_step % TerrainRewind.Every != 0) return;
        long t0 = TerrainRewind.SaveBegin();
        RewindSave(Rewound.Take(_step));
        TerrainRewind.SaveEnd(TerrainRewind.Sim.Decomp, t0);
    }

    private static void RewindSave(RewindRec s)
    {
        // 同じ塗りなら前の記録の範囲の写しを使い回す (枠を使い回すので、写しは枠でなく塗りに付ける)
        var last = _lastSaved;
        s.Gen = _compGen;
        s.Comp = last != null && last.Gen == _compGen && last.Comp != null ? last.Comp : (int[])_comp.Clone();
        if (s.Press.Length < _press.Length) s.Press = new int[Math.Max(_press.Length, 8)];
        Array.Copy(_press, s.Press, _press.Length);
        s.PressN = _press.Length;
        _lastSaved = s;
        if (TerrainRewind.SelfCheck) s.Digest = Digest();
    }

    // s = null は記録が無い時 (戻す所では外壁が抜けていなかった = 全部満タン)
    private static void RewindLoad(RewindRec s)
    {
        if (s == null) { Array.Fill(_press, Full); return; }
        if (s.Gen == _compGen && s.PressN == _press.Length) { Array.Copy(s.Press, _press, s.PressN); return; }
        var press = new int[s.PressN];
        Array.Copy(s.Press, press, s.PressN);
        Regions(s.Comp, press);
    }
}
