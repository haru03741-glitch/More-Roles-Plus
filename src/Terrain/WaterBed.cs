using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 床の高さ (同じ段の中の坂) と海。マップごとの表 (Resources/Heights/<船>.bin) を升の真ん中で引く。表の無いマップは高さ 0・海なし。
// 同じ段の隣どうしは水面 (床の高さ + 水の深さ) の差で流れるので、坂の水は低い方へ下りて溜まる。
// 海は縁の先 (歩けない升だけを真っすぐ SeaScan 升まで) に海の升がある縁から出入りする。海面 (SeaLevel) より水面が高ければ出ていき、
// 海面が上がれば入ってくる。縁の先に部屋の影の線があれば (建物の壁の向こう) 海とつながない
internal static partial class WaterSim
{
    private const int SeaScan = 16;
    private const int SeaInMax = Full / 8;     // 海から 1 升の 1 向きへ 1 刻みに入る量の上限
    private static readonly byte[] BedMagic = { (byte)'M', (byte)'R', (byte)'P', (byte)'H' };
    private static short[] _bed;               // 床の高さ (水の深さと同じ物差し)
    private static byte[] _seaCell;            // 海の升
    private static byte[] _seaMask;            // 海とつながる縁の向き
    private static byte[] _seaCut;             // 作った時に調べた縁 (ビット d) と、海とつながった縁 (ビット 4 + d)
    private static readonly List<int> SeaCells = new();
    private static bool _seaListDirty;
    private static bool _hasSea;
    internal static int SeaLevel;              // 海面の高さ (床の高さ 0 からの水の深さの物差し)
    internal static long SeaOutTotal, SeaInTotal;
    internal static string BedStats = "-";

    internal static int BedAt(int k) => _bed == null ? 0 : _bed[k];

    private static void LoadBed()
    {
        int n = _w * _h;
        _bed = new short[n];
        _seaCell = new byte[n];
        _seaMask = new byte[n];
        _seaCut = new byte[n];
        SeaCells.Clear();
        _seaListDirty = false;
        _hasSea = false;
        SeaLevel = 0;
        var ship = ShipStatus.Instance;
        if (!ship) { BedStats = "no ship"; return; }
        string name = ship.name.Replace("(Clone)", "");
        try
        {
            using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MoreRolesPlus.Resources.Heights." + name + ".bin");
            if (s == null) { BedStats = $"{name}: none"; return; }
            using var br = new BinaryReader(s);
            var magic = br.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != BedMagic[0] || magic[1] != BedMagic[1] || magic[2] != BedMagic[2] || magic[3] != BedMagic[3]) { BedStats = $"{name}: bad magic"; return; }
            int ver = br.ReadByte();
            if (ver != 1) { BedStats = $"{name}: version {ver}"; return; }
            // 原点と升は 1/16 単位の整数。升の真ん中で引くので境目ちょうどの点は出ない
            int ox16 = br.ReadInt16(), oy16 = br.ReadInt16(), c16 = br.ReadByte();
            int bw = br.ReadUInt16(), bh = br.ReadUInt16();
            var v = br.ReadBytes(bw * bh);
            if (v.Length != bw * bh || c16 <= 0) { BedStats = $"{name}: short"; return; }
            const int Scale = Full / 32;
            int seas = 0;
            for (int y = 0; y < _h; y++)
            for (int x = 0; x < _w; x++)
            {
                // 升の真ん中 (1/16 単位の 2 倍で整数にする)
                int px2 = (int)MathF.Round((_org.x + (x + 0.5f) * _cell) * 32f), py2 = (int)MathF.Round((_org.y + (y + 0.5f) * _cell) * 32f);
                int bx = FloorDiv(px2 - ox16 * 2, c16 * 2), by = FloorDiv(py2 - oy16 * 2, c16 * 2);
                if (bx < 0 || by < 0 || bx >= bw || by >= bh) continue;
                sbyte b = (sbyte)v[by * bw + bx];
                int k = y * _w + x;
                if (b == sbyte.MinValue) { if (_open[k] == 0) { _seaCell[k] = 1; seas++; } continue; }
                _bed[k] = (short)(b * Scale);
            }
            _hasSea = seas > 0;
            BedStats = $"{name}: {bw}x{bh} sea={seas}";
        }
        catch (Exception e) { BedStats = $"{name}: {e.Message}"; }
    }

    private static int CountBits(byte[] m)
    {
        int c = 0;
        foreach (byte b in m) c += PopCount(b);
        return c;
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    // 縁の升 (x, y) の向き d の先に海があるか。作った時 (_ledgeScan) だけ影の線を読み、結果を残す
    private static bool SeaEdge(int x, int y, int d)
    {
        if (!_hasSea) return false;
        int k = y * _w + x;
        if ((_seaCut[k] >> d & 1) != 0) return (_seaCut[k] >> (4 + d) & 1) != 0;
        if (!_ledgeScan) return false;
        bool hit = false;
        for (int i = 1; i <= SeaScan; i++)
        {
            int cx = x + Nx[d] * i, cy = y + Ny[d] * i;
            if (cx < 0 || cy < 0 || cx >= _w || cy >= _h) break;
            int c = cy * _w + cx;
            if (_doorCell[c] != 0 || _open[c] != 0) break;
            if (_seaCell[c] != 0) { hit = !ShadowBetween(x, y, cx, cy, true); break; }
        }
        _seaCut[k] |= (byte)(1 << d | (hit ? 1 << (4 + d) : 0));
        return hit;
    }

    // 海面が床より上なら、海とつながる縁から水が入る (流れを決める前・升の番号の順)
    private static void SeaInflow()
    {
        if (!_hasSea || SeaLevel <= 0) return;
        if (_seaListDirty)
        {
            SeaCells.Clear();
            for (int k = 0; k < _seaMask.Length; k++) if (_seaMask[k] != 0) SeaCells.Add(k);
            _seaListDirty = false;
        }
        foreach (int k in SeaCells)
        {
            int gap = SeaLevel - _bed[k] - _furn[k] - _hgt[k];
            if (gap <= WetHold) continue;
            int add = Math.Min(SeaInMax, gap * Gain / 256) * PopCount(_seaMask[k]);
            if (add <= 0) continue;
            _hgt[k] += add;
            SeaInTotal += add;
            Wake(k);
            Mark(k);
        }
    }
}
