using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Terrain;
using UnityEngine;

namespace MoreRolesPlus.Dev;

// ダミーの道案内。歩ける所の地図 (SolidMap) を 0.25 刻みの格子に間引き、壁から体の幅だけ離れた升をつないで探す。
// 壊れて変わった壁は数秒ごとに作り直して拾う。はしごや動く足場はつながりに入らない (別の島へは行かない)
internal static class DummyNav
{
    private const float Cell = 0.25f;
    private const float Clear = 0.2f;     // 壁から離す距離 (体の半径くらい)
    private const long RebuildMs = 5000;
    private static readonly int[] Di = { 1, -1, 0, 0, 1, 1, -1, -1 };
    private static readonly int[] Dj = { 0, 0, 1, -1, 1, -1, 1, -1 };

    private static bool[] _free;
    // 人が止まる物 (机などの家具・押せる家具・扉・瓦礫と設計台) も避けた升。地図 (SolidMap) は壁の線だけなので別に描く。
    // 当たり判定の無い物を歩かせる時 (ドッペルゲンガーの分身) に使う。作り直しは _free と同じ間隔で、頼まれた時だけ
    private static bool[] _freeSolid;
    private static int _solidGen = -1;
    private static long _solidMs;
    private static readonly int ObjectMask = Constants.ShipAndObjectsMask | 1 << 12; // 本編の船と物の層に家具 (机) の層を足す
    private static int[] _prev, _queue;
    private static int _w, _h, _gen = -1;
    private static float _ox, _oy;
    private static long _builtMs;
    private static readonly List<int> Cells = new();

    private static bool Ensure()
    {
        if (!SolidMap.Ensure()) return false;
        long now = Environment.TickCount64;
        if (_free != null && _gen == GameClock.ShipGen && now - _builtMs < RebuildMs) return true;
        var o = SolidMap.Origin;
        _ox = o.x; _oy = o.y;
        _w = (int)(SolidMap.W / SolidMap.Ppu / Cell) + 1;
        _h = (int)(SolidMap.H / SolidMap.Ppu / Cell) + 1;
        int n = _w * _h;
        if (_free == null || _free.Length != n) { _free = new bool[n]; _prev = new int[n]; _queue = new int[n]; }
        for (int j = 0; j < _h; j++)
        {
            float y = _oy + (j + 0.5f) * Cell;
            for (int i = 0; i < _w; i++)
            {
                float x = _ox + (i + 0.5f) * Cell;
                _free[j * _w + i] = !SolidMap.Solid(x, y) && !SolidMap.Solid(x + Clear, y) && !SolidMap.Solid(x - Clear, y)
                                    && !SolidMap.Solid(x, y + Clear) && !SolidMap.Solid(x, y - Clear);
            }
        }
        _gen = GameClock.ShipGen;
        _builtMs = now;
        return true;
    }

    // 人が止まる物を _free に重ねた升を作る (家具・扉・瓦礫の当たり判定の周り Clear まで)
    private static bool EnsureSolid()
    {
        if (!Ensure()) return false;
        if (_freeSolid != null && _freeSolid.Length == _free.Length && _solidGen == _gen && _solidMs == _builtMs) return true;
        var ship = ShipStatus.Instance;
        if (!ship) return false;
        if (_freeSolid == null || _freeSolid.Length != _free.Length) _freeSolid = new bool[_free.Length];
        Array.Copy(_free, _freeSolid, _free.Length);
        string shipName = FurnitureKinds.ShipName(ship);
        float cx = _ox + _w * Cell * 0.5f, cy = _oy + _h * Cell * 0.5f;
        float reach = MathF.Sqrt(_w * _w + _h * _h) * Cell * 0.5f + 1f;
        foreach (var c in Physics2D.OverlapCircleAll(FxMath.V2(cx, cy), reach, ObjectMask))
        {
            if (!c || c.isTrigger || !c.enabled) continue;
            var go = c.gameObject;
            // 壁の線は地図に入っている。地図が外す物 (押せる家具・扉・瓦礫と設計台) だけを足す
            if (go.layer == 9 && !SolidMap.BlocksOnly(go.name) && !c.GetComponentInParent<OpenableDoor>()
                && !FurnitureKinds.TryGet(c.transform, shipName, out _)) continue;
            var b = c.bounds;
            Vector3 mn = b.min, mx = b.max;
            int i0 = Math.Max(0, (int)MathF.Floor((mn.x - Clear - _ox) / Cell)), i1 = Math.Min(_w - 1, (int)MathF.Floor((mx.x + Clear - _ox) / Cell));
            int j0 = Math.Max(0, (int)MathF.Floor((mn.y - Clear - _oy) / Cell)), j1 = Math.Min(_h - 1, (int)MathF.Floor((mx.y + Clear - _oy) / Cell));
            for (int j = j0; j <= j1; j++)
            {
                float y = _oy + (j + 0.5f) * Cell;
                for (int i = i0; i <= i1; i++)
                {
                    int k = j * _w + i;
                    if (!_freeSolid[k]) continue;
                    float x = _ox + (i + 0.5f) * Cell;
                    if (c.OverlapPoint(FxMath.V2(x, y)) || c.OverlapPoint(FxMath.V2(x + Clear, y)) || c.OverlapPoint(FxMath.V2(x - Clear, y))
                        || c.OverlapPoint(FxMath.V2(x, y + Clear)) || c.OverlapPoint(FxMath.V2(x, y - Clear)))
                        _freeSolid[k] = false;
                }
            }
        }
        _solidGen = _gen;
        _solidMs = _builtMs;
        return true;
    }

    // 点 (足元) が人の止まる物の中か。ドッペルゲンガーの入れ替わりの行き先を確かめる
    public static bool InsideObject(float x, float y)
    {
        foreach (var c in Physics2D.OverlapCircleAll(FxMath.V2(x, y), 0.05f, ObjectMask))
            if (c && !c.isTrigger && c.enabled) return true;
        return false;
    }

    // from から to への経路を世界座標の点の列で xs/ys へ (出発点は入れない・見通せる点は飛ばす)。届かなければ false。
    // solid = 人が止まる物 (家具・扉・瓦礫) も避ける (当たり判定の無い物を歩かせる時)
    public static bool Path(float fx, float fy, float tx, float ty, List<float> xs, List<float> ys, bool solid = false)
    {
        xs.Clear();
        ys.Clear();
        if (solid ? !EnsureSolid() : !Ensure()) return false;
        var free = solid ? _freeSolid : _free;
        int s = Nearest(free, fx, fy, 3), g = Nearest(free, tx, ty, 8);
        if (s < 0 || g < 0) return false;
        Array.Fill(_prev, -1);
        int head = 0, tail = 0;
        _queue[tail++] = s;
        _prev[s] = s;
        while (head < tail)
        {
            int c = _queue[head++];
            if (c == g) break;
            int ci = c % _w, cj = c / _w;
            for (int d = 0; d < 8; d++)
            {
                int ni = ci + Di[d], nj = cj + Dj[d];
                if (ni < 0 || nj < 0 || ni >= _w || nj >= _h) continue;
                int k = nj * _w + ni;
                if (!free[k] || _prev[k] >= 0) continue;
                // 斜めは角をすり抜けない
                if (d >= 4 && (!free[cj * _w + ni] || !free[nj * _w + ci])) continue;
                _prev[k] = c;
                _queue[tail++] = k;
            }
        }
        if (_prev[g] < 0) return false;
        Cells.Clear();
        for (int c = g; c != s; c = _prev[c]) Cells.Add(c);
        Cells.Reverse();
        int from = s;
        for (int i = 0; i < Cells.Count; i++)
        {
            int far = i;
            while (far + 1 < Cells.Count && Visible(free, from, Cells[far + 1])) far++;
            from = Cells[far];
            xs.Add(_ox + (from % _w + 0.5f) * Cell);
            ys.Add(_oy + (from / _w + 0.5f) * Cell);
            i = far;
        }
        if (xs.Count == 0) { xs.Add(_ox + (g % _w + 0.5f) * Cell); ys.Add(_oy + (g / _w + 0.5f) * Cell); }
        return true;
    }

    // 点にいちばん近い通れる升 (r 升まで)
    private static int Nearest(bool[] free, float x, float y, int r)
    {
        int ci = (int)MathF.Floor((x - _ox) / Cell), cj = (int)MathF.Floor((y - _oy) / Cell);
        int best = -1, bestD = int.MaxValue;
        for (int dj = -r; dj <= r; dj++)
        for (int di = -r; di <= r; di++)
        {
            int i = ci + di, j = cj + dj;
            if (i < 0 || j < 0 || i >= _w || j >= _h || !free[j * _w + i]) continue;
            int d = di * di + dj * dj;
            if (d < bestD) { bestD = d; best = j * _w + i; }
        }
        return best;
    }

    // 2 升の間をまっすぐ歩けるか (半升ごとに確かめる)
    private static bool Visible(bool[] free, int a, int b)
    {
        float ax = a % _w + 0.5f, ay = a / _w + 0.5f, bx = b % _w + 0.5f, by = b / _w + 0.5f;
        float dx = bx - ax, dy = by - ay;
        int steps = (int)(MathF.Sqrt(dx * dx + dy * dy) * 2f) + 1;
        for (int t = 1; t < steps; t++)
        {
            float f = (float)t / steps;
            int i = (int)(ax + dx * f), j = (int)(ay + dy * f);
            if (!free[j * _w + i]) return false;
        }
        return true;
    }
}
