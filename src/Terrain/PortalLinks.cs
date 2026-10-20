using System;
using System.Collections.Generic;

namespace MoreRolesPlus.Terrain;

// ポータルの組の台帳 (刻み付き)。物 (PropSim) を全員同じ刻みで通すため、置いた・消えたを扉と同じく刻みを押して配る。
// 書くのはホスト (と一人の時) だけ。座標はホストが 1/1024 単位の整数にしてから配る (端末ごとの小数の丸めで確定がずれない)
internal static class PortalLinks
{
    internal const int Unit = 1024;

    internal struct Link
    {
        public int Tick;
        public byte Owner;
        public bool On;
        public int Ax, Ay, Bx, By; // 2 つの足元 (世界座標 × 1024)
    }

    private static readonly List<Link> Log = new();
    private static readonly int[] Last = new int[256];
    private static readonly List<byte> Owners = new();
    private static int _gen = -1;

    internal static int Count => Log.Count;

    internal static int Q(float v) => (int)MathF.Round(v * Unit);

    // ホストと一人の時: 組が開いた / 閉じた。刻みを押して自分の台帳へ入れ、全員へ配る
    internal static void HostSet(byte owner, bool on, float ax, float ay, float bx, float by)
    {
        if (TerrainSync.IsGuest()) return;
        ResetIfNewShip();
        ushort stamp = GameClock.Stamp;
        var l = new Link { Tick = GameClock.Expand(stamp), Owner = owner, On = on };
        if (on) { l.Ax = Q(ax); l.Ay = Q(ay); l.Bx = Q(bx); l.By = Q(by); }
        Add(l);
        TerrainSync.BroadcastPortal(stamp, l);
    }

    // 客: ホストから届いた
    internal static void OnPortal(in Link l)
    {
        ResetIfNewShip();
        Add(l);
    }

    private static void ResetIfNewShip()
    {
        if (_gen == GameClock.ShipGen) return;
        _gen = GameClock.ShipGen;
        Log.Clear();
    }

    private static void Add(in Link l)
    {
        int i = Log.Count;
        while (i > 0 && Log[i - 1].Tick > l.Tick) i--;
        Log.Insert(i, l);
    }

    // step の時点で開いている組を buf へ (持ち主ごとに step 以前の最後の記録)。返り値 = 組の数
    internal static int OpenAt(int step, Link[] buf)
    {
        if (_gen != GameClock.ShipGen || Log.Count == 0) return 0;
        Owners.Clear();
        for (int i = 0; i < Log.Count && Log[i].Tick <= step; i++)
        {
            byte o = Log[i].Owner;
            if (!Owners.Contains(o)) Owners.Add(o);
            Last[o] = i;
        }
        int n = 0;
        for (int k = 0; k < Owners.Count && n < buf.Length; k++)
        {
            var l = Log[Last[Owners[k]]];
            if (l.On) buf[n++] = l;
        }
        return n;
    }
}
