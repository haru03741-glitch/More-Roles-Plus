using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 「いま歩ける所」の地図。損傷マスクと同じ升 (1/16 単位) で、歩ける升 = 1・それ以外 (壁の中・船体の塊・船の外) = 0。
// 壁の中かどうか (蓋を作る所・見た目の穴を抜く所) は、この地図で決める。叩いた側や爆心から壁の線を数える判定は、
// 基準点が前の穴の中 (元は壁の中) にあると反転し、一度蓋が欠けると以後ずっと開いたままになるので使わない。
// 作り方: 試合の始めに、元の壁の線 (動きの層の当たり判定) を升に描き、2 升太らせて継ぎ目を塞いでから、
// 床の上と分かっている点 (通気口・扉・ダミーの出現位置・出現の中心) から塗り広げ、太らせた分を戻す。
// 塗りが地図の端に届いた点 (壁の外へ漏れた) は捨てる。壊すたびに、切った後の壁の線で形の中を塗り足す (Carve)。
// 入力はマップの形と、全員が同じ順に適用する破壊だけなので、どの端末でも同じ地図になる。
// エアシップは部屋と部屋の間が船体の塊 (HullBlock の赤い板) で、その外は空。厚い壁は掘り進めてよいが、
// 空へは抜けないように、塊の外周 (歩ける所に接していない所) に壊れない壁 (MrpHullEdge) を置く
internal static class SolidMap
{
    private const int ShipLayer = 9;
    private const int Dilate = 2;         // 継ぎ目を塞ぐために壁の線を太らせる升の数 (0.125 単位までの隙間が塞がる)
    private const float EdgeProbe = 0.1f; // 船体の外周の壁を置くか調べる、外周の両側の点までの距離
    private const float MaxGrow = 24f;    // 地図を部屋の絵の範囲からどこまで広げてよいか
    public const string HullEdgeName = "MrpHullEdge";

    private static ShipStatus _ship;
    private static byte[] _open;
    private static byte[] _leaked; // テスト用: 地図の端まで漏れて捨てた塗り
    // 試合の始めの歩ける所を、歩いて行き来できるまとまり (島) ごとに番号で塗り分けたもの (0 = 歩けない・255 = 数えきれない)。
    // はしご・ジップライン・動く足場はつながりに入れない (壁の線しか見ない) ので、番号が違う = 高さが違う床。
    // 元の番号は壊しても書き換えない (穴でつながった後も元の高さで比べる)。掘って開けた升だけ、掘り始めた側の番号を足す
    private static byte[] _island;
    // 船の外 (宇宙・空・スケルドとミラとエアシップだけ) = 1。部屋の絵 (DamageMap の部屋の絵のスプライトの三角形) と船体の塊・船体のメッシュのどれにも掛からず、歩ける所でも壁でもない所。
    // ミラは部屋と部屋の間に空が入り込み、厚い壁の帯 (両側の線の間) と細い空の隙間は線の形・幅・つながりでは区別できない
    // (帯にも隙間にも端が空に開いた所・閉じた所がある)。絵のあるなしで分ける。三角形は画像の圧縮によらないので端末で同じ
    private static byte[] _outside;
    private static int _w, _h;
    private static Vector2 _origin;
    internal static Vector2 Origin => _origin;
    internal static int W => _w;
    internal static int H => _h;
    internal static float Ppu => _ppu;
    internal static bool OpenCell(int k) => _open[k] != 0;
    internal static int IslandCell(int k) => _island == null ? 0 : _island[k];
    internal static bool LeakedCell(int k) => _leaked != null && _leaked[k] != 0;
    private static float _ppu;
    private static readonly List<Rect> HullRects = new();
    internal static readonly List<Vector2> ExtraSeeds = new(); // テスト用: 扉・出現の中心だけが新しく塗った塊の種
    internal static readonly List<Vector2> LeakedSeeds = new(); // テスト用: 塗りが地図の端に届いて捨てた種

    public static bool Valid { get; private set; }
    public static int IslandCount { get; private set; }
    public static bool HasHull => Valid && HullRects.Count > 0;
    // 爆発で外壁を宇宙まで掘り抜けるマップか (スケルド)
    public static bool BreachableHull { get; private set; }
    // エアシップ: 外に面した厚い壁の裏は船体の塊。塊へ抉れたら厚みに関係なく空へ開通したと見なす
    public static bool SkyHull { get; private set; }
    public static bool Breachable => BreachableHull || SkyHull;
    public static string Stats { get; private set; } = "not built";

    // 試合の始め (TerrainWarm) と、最初の破壊の前に呼ぶ。壁を切る前に作ること
    public static bool Ensure()
    {
        var ship = ShipStatus.Instance;
        if (!ship) return false;
        if (_ship == ship) return Valid;
        _ship = ship;
        Valid = false;
        _open = null;
        _leaked = null;
        _island = null;
        _outside = null;
        BreachableHull = false;
        SkyHull = false;
        IslandCount = 0;
        HullRects.Clear();
        ExtraSeeds.Clear();
        LeakedSeeds.Clear();
        if (!DamageMap.Ready()) { _ship = null; return false; }
        try { Build(ship); }
        catch (Exception e)
        {
            Valid = false;
            Stats = $"build failed: {e.Message}";
            Plugin.Logger.LogWarning($"solid map: {Stats}");
        }
        return Valid;
    }

    // 歩けない所 (壁の中・塊・船の外・地図の外) か
    public static bool Solid(Vector2 p)
    {
        int x = (int)MathF.Floor((p.x - _origin.x) * _ppu), y = (int)MathF.Floor((p.y - _origin.y) * _ppu);
        if (x < 0 || y < 0 || x >= _w || y >= _h) return true;
        return _open[y * _w + x] == 0;
    }

    // 試合の始めにその点がいた島の番号 (0 = 歩けない所)
    public static int IslandAt(Vector2 p)
    {
        if (_island == null) return 0;
        int x = (int)MathF.Floor((p.x - _origin.x) * _ppu), y = (int)MathF.Floor((p.y - _origin.y) * _ppu);
        if (x < 0 || y < 0 || x >= _w || y >= _h) return 0;
        return _island[y * _w + x];
    }

    // from から dir へ max まで進んで最初に出会う島の番号 (無ければ 0)。壁の線の上から、その面の片側の床の高さを知る用
    public static int IslandAlong(Vector2 from, Vector2 dir, float max)
    {
        if (_island == null) return 0;
        float step = 0.5f / _ppu;
        for (float d = step; d <= max; d += step)
        {
            int k = IslandAt(from + dir * d);
            if (k != 0) return k;
        }
        return 0;
    }

    // from から dir へ max まで進んで、歩ける床 (島) より先に船の外を通るなら -1、床に着けば 1、どちらも無ければ 0。
    // 壁の面の向こうが宇宙・空か (部屋と部屋の間の隙間が空に開いている所も含む) を知る用
    public static int AlongFloorOrOutside(Vector2 from, Vector2 dir, float max)
    {
        if (_island == null) return 0;
        float step = 0.5f / _ppu;
        for (float d = step; d <= max; d += step)
        {
            Vector2 p = from + dir * d;
            int x = (int)MathF.Floor((p.x - _origin.x) * _ppu), y = (int)MathF.Floor((p.y - _origin.y) * _ppu);
            if (x < 0 || y < 0 || x >= _w || y >= _h) return -1;
            int k = y * _w + x;
            if (_island[k] != 0) return 1;
            if (_outside != null && _outside[k] == 1) return -1;
        }
        return 0;
    }

    // 点の近く (縦横 OutsideReach 升以内) が船の外か。壁の線のすぐ外 (太らせた分) は外に数えないので少し広く見る。
    // 焦げを空に書かない用 (影の中では焦げの所が黒い板になって空の上に出る)
    public static bool NearOutside(float px, float py)
    {
        if (_outside == null) return false;
        int x = (int)MathF.Floor((px - _origin.x) * _ppu), y = (int)MathF.Floor((py - _origin.y) * _ppu);
        return OutsideCell(x, y) || OutsideCell(x - OutsideReach, y) || OutsideCell(x + OutsideReach, y) ||
               OutsideCell(x, y - OutsideReach) || OutsideCell(x, y + OutsideReach);
    }

    private const int OutsideReach = Dilate + 1;

    // 点が絵の無い空・宇宙の上か (船の外と、その際の太らせた分の絵の無い縁)。
    // 穴を空に書かない用 (穴の所には船体の中の板が描かれるので、空の上に黒い塊が出る)
    public static bool BareSky(float px, float py)
    {
        if (_outside == null) return false;
        return BareSkyCell((int)MathF.Floor((px - _origin.x) * _ppu), (int)MathF.Floor((py - _origin.y) * _ppu));
    }

    private static bool BareSkyCell(int x, int y)
    {
        if (x < 0 || y < 0 || x >= _w || y >= _h) return true;
        byte v = _outside[y * _w + x];
        return v == 1 || (v == 2 && (OutsideCell(x - OutsideReach, y) || OutsideCell(x + OutsideReach, y) ||
                                     OutsideCell(x, y - OutsideReach) || OutsideCell(x, y + OutsideReach)));
    }

    // 壁の線の上の点 m の両側 (法線 n の向きと逆向き) のどちらかが船の外か。面から離れながら見て、
    // 歩ける床に先に着いた側は外でない (ポーラスの壁の外の細い通路の先にある船の外を拾わないように)。
    // 船の外の際には太らせた分の外でない縁が残るので、点ごとに縦横も少し広げて見る
    public static bool FacesOutside(Vector2 m, Vector2 n) => SideOutside(m, n) || SideOutside(m, -n);

    private static bool SideOutside(Vector2 m, Vector2 dir)
    {
        if (_outside == null) return false;
        for (int i = 1; i <= FaceProbes; i++)
        {
            Vector2 p = m + dir * (FaceProbeStep * i);
            int x = (int)MathF.Floor((p.x - _origin.x) * _ppu), y = (int)MathF.Floor((p.y - _origin.y) * _ppu);
            if (x >= 0 && y >= 0 && x < _w && y < _h && _open[y * _w + x] != 0) return false;
            if (NearOutside(p.x, p.y)) return true;
        }
        return false;
    }

    // 面 m から dir へ、船体の中 (歩けない所) だけを通って船の外に着くまでの距離。途中で歩ける所に着く・max を越える・
    // 外を持たないマップは -1。外の際の太らせた分 (絵の無い縁) は宇宙に数える (穴の口を空の上に作らないのと同じ線)
    public static float HullDepth(Vector2 m, Vector2 dir, float max)
    {
        if (_outside == null) return -1f;
        for (float d = FaceProbeStep; d <= max + 1e-4f; d += FaceProbeStep)
        {
            Vector2 p = m + dir * d;
            int x = (int)MathF.Floor((p.x - _origin.x) * _ppu), y = (int)MathF.Floor((p.y - _origin.y) * _ppu);
            if (BareSkyCell(x, y)) return d;
            if (_open[y * _w + x] != 0) return -1f;
        }
        return -1f;
    }

    private const float SkyFaceSlack = 0.3f;

    // p から dir へ max まで、歩ける所に当たらずに船の外 (空) か地図の外へ出るか (外に面した厚い壁か)
    public static bool SkyAhead(Vector2 p, Vector2 dir, float max)
    {
        if (!Valid || _outside == null) return false;
        for (float d = FaceProbeStep; d <= max + 1e-4f; d += FaceProbeStep)
        {
            Vector2 q = p + dir * d;
            int x = (int)MathF.Floor((q.x - _origin.x) * _ppu), y = (int)MathF.Floor((q.y - _origin.y) * _ppu);
            if (x < 0 || y < 0 || x >= _w || y >= _h) return true;
            if (BareSkyCell(x, y)) return true;
            if (_open[y * _w + x] != 0 && d > SkyFaceSlack) return false; // 面の際の升は床側に掛かることがある
        }
        return false;
    }

    // 面から 0.15 ずつ 0.6 まで (部屋の範囲が壁の線より外へ張り出している所がある)
    private const float FaceProbeStep = 0.15f;
    private const int FaceProbes = 4;

    private static bool OutsideCell(int x, int y) => x < 0 || y < 0 || x >= _w || y >= _h || _outside[y * _w + x] == 1;

    // エアシップの船体の塊 (赤い板) の中か
    public static bool InHull(Vector2 p)
    {
        foreach (var r in HullRects)
            if (p.x > r.xMin && p.x < r.xMax && p.y > r.yMin && p.y < r.yMax) return true;
        return false;
    }

    // ---- 作る ----

    private static void Build(ShipStatus ship)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ppu = 1f / DamageMap.TexelSize;

        // 1. 元の壁の線 (扉は開け閉めするので入れない = 部屋どうしがつながる)。
        // 地図の範囲は損傷マスク (部屋の絵) と壁の線の両方を囲む範囲 (ファングルは歩ける浜が部屋の絵の外まで続く)
        // 押されて動く家具 (FurnitureKinds) は入れない (当たり判定は家具と一緒に動くが、この地図は作り直さない)
        var all = new List<Vector2>();
        int cols = 0, movable = 0;
        string shipName = FurnitureKinds.ShipName(ship);
        foreach (var c in ship.GetComponentsInChildren<Collider2D>(false))
        {
            if (!IsWall(c) || c.GetComponentInParent<OpenableDoor>()) continue;
            if (FurnitureKinds.TryGet(c.transform, shipName, out _)) { movable++; continue; }
            Segments(c, all);
            cols++;
        }
        float minX = DamageMap.Origin.x, minY = DamageMap.Origin.y;
        float maxX = minX + DamageMap.MapW * DamageMap.TexelSize, maxY = minY + DamageMap.MapH * DamageMap.TexelSize;
        foreach (var p in all)
        {
            if (p.x < minX) minX = p.x;
            if (p.y < minY) minY = p.y;
            if (p.x > maxX) maxX = p.x;
            if (p.y > maxY) maxY = p.y;
        }
        // 遠く離れた当たり判定 (隠した物など) で地図が大きくなりすぎないように、部屋の絵の範囲から MaxGrow までに抑える
        float dx0 = DamageMap.Origin.x, dy0 = DamageMap.Origin.y;
        float dx1 = dx0 + DamageMap.MapW * DamageMap.TexelSize, dy1 = dy0 + DamageMap.MapH * DamageMap.TexelSize;
        minX = Math.Max(minX, dx0 - MaxGrow); minY = Math.Max(minY, dy0 - MaxGrow);
        maxX = Math.Min(maxX, dx1 + MaxGrow); maxY = Math.Min(maxY, dy1 + MaxGrow);
        const float margin = 2f;
        _origin = new Vector2(MathF.Floor(minX - margin), MathF.Floor(minY - margin));
        _w = (int)MathF.Ceiling((maxX + margin - _origin.x) * _ppu);
        _h = (int)MathF.Ceiling((maxY + margin - _origin.y) * _ppu);
        int n = _w * _h;
        var barrier = new byte[n];
        for (int i = 0; i + 1 < all.Count; i += 2) Raster(barrier, _w, _h, 0, 0, all[i], all[i + 1]);

        // 2. 太らせる (縦横それぞれ Dilate 升)
        var blocked = DilateGrid(barrier, _w, _h, Dilate);

        // 3. 床の上と分かっている点から塗る (地図の端に届いた塗りは壁の外へ漏れたので捨てる)
        _open = new byte[n];
        var queue = new int[n];
        var seeds = Seeds(ship);
        int accepted = 0, leaked = 0, extra = 0;
        foreach (var (sp, sure) in seeds)
        {
            int start = NearestFree(blocked, sp);
            if (start < 0 || _open[start] != 0) continue;
            int head = 0, tail = 0;
            bool border = false;
            _open[start] = 1;
            queue[tail++] = start;
            while (head < tail)
            {
                int k = queue[head++];
                int x = k % _w, y = k / _w;
                if (x == 0 || y == 0 || x == _w - 1 || y == _h - 1) border = true;
                if (x > 0) Push(k - 1);
                if (x < _w - 1) Push(k + 1);
                if (y > 0) Push(k - _w);
                if (y < _h - 1) Push(k + _w);
            }
            if (border)
            {
                for (int i = 0; i < tail; i++) _open[queue[i]] = 2; // 漏れた塗り (もう塗らない印)
                leaked++;
                LeakedSeeds.Add(sp);
            }
            else
            {
                accepted++;
                if (!sure) { extra++; ExtraSeeds.Add(sp); }
            }

            void Push(int k2)
            {
                if (_open[k2] != 0 || blocked[k2] != 0) return;
                _open[k2] = 1;
                queue[tail++] = k2;
            }
        }
        _leaked = leaked > 0 ? new byte[n] : null;
        for (int k = 0; k < n; k++) if (_open[k] == 2) { _open[k] = 0; _leaked[k] = 1; }

        // 4. 太らせた分を戻す (壁の線そのものの升は閉じたまま)。角の升は斜めにしか届かないので 1 段多く
        for (int step = 0; step <= Dilate; step++)
        {
            int grown = 0;
            for (int k = 0; k < n; k++)
            {
                if (_open[k] != 0 || barrier[k] != 0 || blocked[k] == 0) continue;
                int x = k % _w, y = k / _w;
                if ((x > 0 && _open[k - 1] == 1) || (x < _w - 1 && _open[k + 1] == 1) ||
                    (y > 0 && _open[k - _w] == 1) || (y < _h - 1 && _open[k + _w] == 1))
                { queue[grown++] = k; }
            }
            for (int i = 0; i < grown; i++) _open[queue[i]] = 1; // 段ごとにまとめて開ける (同じ段で連鎖させない)
        }

        int openCells = 0;
        for (int k = 0; k < n; k++) if (_open[k] != 0) openCells++;
        Valid = accepted > 0 && openCells > n / 50;
        if (Valid) LabelIslands(queue);

        // 5. エアシップの船体の塊と、その外周の壊れない壁
        int edges = 0;
        if (Valid) edges = BuildHull(ship);

        // 6. 船の外。ミラ・エアシップは部屋と部屋の間に空が見える。スケルドの部屋の間は船体のメッシュ (Hull*) が描いていて、
        // その外が星空。ポーラス・ファングルの屋外は地面の絵が描いていて外と分からないので作らない (奥の面と床で決める)
        bool sky = ship.TryCast<MiraShipStatus>() != null || ship.TryCast<AirshipStatus>() != null;
        var hullMeshes = !sky && ship.Type == ShipStatus.MapType.Ship ? HullMeshes(ship) : null;
        _outside = sky || hullMeshes is { Count: > 0 } ? Outside(blocked, hullMeshes) : null;
        BreachableHull = Valid && hullMeshes is { Count: > 0 };
        SkyHull = Valid && HullRects.Count > 0 && ship.TryCast<AirshipStatus>() != null;

        Stats = $"valid={Valid} {_w}x{_h} walls={cols} movable={movable} seeds={seeds.Count} accepted={accepted} extra={extra} leaked={leaked} open={openCells * 100L / n}% islands={IslandCount} hull={HullRects.Count} hullEdges={edges} ms={sw.Elapsed.TotalMilliseconds:F1}";
        Plugin.Logger.LogInfo($"solid map: {Stats}");
    }

    // 船の外 (_outside の説明)。絵と船体の塊は Dilate 升太らせて、際の 1 画素ずれで外が食い込まないようにする
    private static byte[] Outside(byte[] blocked, List<MeshFilter> hullMeshes)
    {
        int n = _w * _h;
        var art = new byte[n];
        var tri = new List<Vector2>(6);
        if (hullMeshes != null)
            foreach (var mf in hullMeshes)
            {
                var t = mf.transform;
                var mesh = mf.sharedMesh;
                var vs = mesh.vertices;
                var ts = mesh.triangles;
                for (int i = 0; i + 2 < ts.Length; i += 3)
                {
                    Vector2 a = t.TransformPoint(vs[ts[i]]), b = t.TransformPoint(vs[ts[i + 1]]), c = t.TransformPoint(vs[ts[i + 2]]);
                    tri.Clear();
                    tri.Add(a); tri.Add(b); tri.Add(b); tri.Add(c); tri.Add(c); tri.Add(a);
                    FillPolygon(art, tri);
                }
            }
        foreach (var sr in DamageMap.RoomArts)
        {
            if (!sr || !sr.sprite) continue;
            var t = sr.transform;
            var vs = sr.sprite.vertices;
            var ts = sr.sprite.triangles;
            for (int i = 0; i + 2 < ts.Length; i += 3)
            {
                Vector2 a = t.TransformPoint(vs[ts[i]]), b = t.TransformPoint(vs[ts[i + 1]]), c = t.TransformPoint(vs[ts[i + 2]]);
                tri.Clear();
                tri.Add(a); tri.Add(b); tri.Add(b); tri.Add(c); tri.Add(c); tri.Add(a);
                FillPolygon(art, tri);
            }
        }
        // エアシップの船体の塊 (部屋と部屋の間の赤い板) も船の中
        foreach (var hr in HullRects)
        {
            int hx0 = Math.Max(0, (int)MathF.Floor((hr.xMin - _origin.x) * _ppu)), hx1 = Math.Min(_w - 1, (int)MathF.Floor((hr.xMax - _origin.x) * _ppu));
            int hy0 = Math.Max(0, (int)MathF.Floor((hr.yMin - _origin.y) * _ppu)), hy1 = Math.Min(_h - 1, (int)MathF.Floor((hr.yMax - _origin.y) * _ppu));
            for (int y = hy0; y <= hy1; y++)
            for (int x = hx0; x <= hx1; x++) art[y * _w + x] = 1;
        }
        var grown = DilateGrid(art, _w, _h, Dilate);
        var outside = new byte[n];
        for (int k = 0; k < n; k++)
            if (grown[k] == 0 && blocked[k] == 0 && _open[k] == 0) outside[k] = 1;
            else if (art[k] == 0) outside[k] = 2; // 絵は無いが外にも数えない升 (太らせた分の縁・壁の線の上など)
        return outside;
    }

    // 閉じた輪郭 (線分の組) の中の升を塗る (升の中心で偶奇)
    private static void FillPolygon(byte[] grid, List<Vector2> segs)
    {
        if (segs.Count < 6) return;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var p in segs) { if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y; }
        int y0 = Math.Max(0, (int)MathF.Floor((minY - _origin.y) * _ppu)), y1 = Math.Min(_h - 1, (int)MathF.Floor((maxY - _origin.y) * _ppu));
        var xs = new List<float>();
        for (int y = y0; y <= y1; y++)
        {
            float wy = _origin.y + (y + 0.5f) / _ppu;
            xs.Clear();
            for (int i = 0; i + 1 < segs.Count; i += 2)
            {
                Vector2 a = segs[i], b = segs[i + 1];
                if ((a.y > wy) == (b.y > wy)) continue;
                xs.Add(a.x + (wy - a.y) / (b.y - a.y) * (b.x - a.x));
            }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int x0 = Math.Max(0, (int)MathF.Ceiling((xs[k] - _origin.x) * _ppu - 0.5f));
                int x1 = Math.Min(_w - 1, (int)MathF.Floor((xs[k + 1] - _origin.x) * _ppu - 0.5f));
                for (int x = x0; x <= x1; x++) grid[y * _w + x] = 1;
            }
        }
    }

    // 歩ける所を 4 近傍のつながりで塗り分ける。番号は升の並び順で最初に出会った順 (どの端末でも同じ)
    private static void LabelIslands(int[] queue)
    {
        int n = _w * _h;
        _island = new byte[n];
        int count = 0;
        for (int s = 0; s < n; s++)
        {
            if (_open[s] == 0 || _island[s] != 0) continue;
            byte id = count < 254 ? (byte)(count + 1) : (byte)255;
            count++;
            int head = 0, tail = 0;
            _island[s] = id;
            queue[tail++] = s;
            while (head < tail)
            {
                int k = queue[head++];
                int x = k % _w;
                if (x > 0) Visit(k - 1);
                if (x < _w - 1) Visit(k + 1);
                if (k >= _w) Visit(k - _w);
                if (k + _w < n) Visit(k + _w);
            }

            void Visit(int k2)
            {
                if (_open[k2] == 0 || _island[k2] != 0) return;
                _island[k2] = id;
                queue[tail++] = k2;
            }
        }
        IslandCount = count;
    }

    private static bool IsWall(Collider2D c) =>
        c && c.enabled && !c.isTrigger && c.gameObject.layer == ShipLayer &&
        c.gameObject.name != WallBody.CapName && c.gameObject.name != WallBody.MouthName && c.gameObject.name != HullEdgeName && c.gameObject.name != HeightLevels.LedgeName && c.gameObject.name != "MrpRubbleBlock";

    // 床の上の点。確か = 通気口とダミーの出現位置 (床に置かれる物)。扉は扉の板の両脇 (通り道の両側) の点を後から足し、
    // それだけが新しく塗った塊の数を数えておく (壁の中に落ちていないかを地図の書き出しで確かめる用)。
    // 出現の中心は使わない (マップによっては壁の多角形の中にあり、閉じた壁の塊を歩ける所として塗ってしまう)
    private static List<(Vector2 P, bool Sure)> Seeds(ShipStatus ship)
    {
        var list = new List<(Vector2, bool)>();
        foreach (var v in ship.AllVents) if (v) list.Add((v.transform.position, true));
        if (ship.DummyLocations != null)
            foreach (var t in ship.DummyLocations) if (t) list.Add((t.position, true));
        // はしごの両端 (上の床と下の床)。通気口の無い高い所 (ファングルの見張り台など) もこれで塗る
        foreach (var l in ship.GetComponentsInChildren<Ladder>(true))
            if (l) list.Add((l.transform.position, false));
        foreach (var d in ship.AllDoors)
        {
            if (!d) continue;
            var col = d.GetComponent<Collider2D>();
            if (!col) continue;
            var b = col.bounds;
            Vector2 c = b.center;
            // 板の短い向きが通り道の向き
            Vector2 across = b.size.x < b.size.y ? new Vector2(b.extents.x + DoorSide, 0f) : new Vector2(0f, b.extents.y + DoorSide);
            list.Add((c + across, false));
            list.Add((c - across, false));
        }
        return list;
    }

    private const float DoorSide = 0.45f; // 扉の板の面から通り道の両側へ出す距離

    // 点のいる升 (塞がっていれば 3 升以内のいちばん近い空いた升)。地図の外は -1
    private static int NearestFree(byte[] blocked, Vector2 p)
    {
        int cx = (int)MathF.Floor((p.x - _origin.x) * _ppu), cy = (int)MathF.Floor((p.y - _origin.y) * _ppu);
        for (int r = 0; r <= 3; r++)
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            int x = cx + dx, y = cy + dy;
            if (x <= 0 || y <= 0 || x >= _w - 1 || y >= _h - 1) continue;
            if (blocked[y * _w + x] == 0) return y * _w + x;
        }
        return -1;
    }

    private static byte[] DilateGrid(byte[] src, int w, int h, int r)
    {
        var tmp = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w, last = -1000000;
            for (int x = 0; x < w; x++) { if (src[row + x] != 0) last = x; if (x - last <= r) tmp[row + x] = 1; }
            last = 1000000;
            for (int x = w - 1; x >= 0; x--) { if (src[row + x] != 0) last = x; if (last - x <= r) tmp[row + x] = 1; }
        }
        var dst = new byte[w * h];
        for (int x = 0; x < w; x++)
        {
            int last = -1000000;
            for (int y = 0; y < h; y++) { if (tmp[y * w + x] != 0) last = y; if (y - last <= r) dst[y * w + x] = 1; }
            last = 1000000;
            for (int y = h - 1; y >= 0; y--) { if (tmp[y * w + x] != 0) last = y; if (last - y <= r) dst[y * w + x] = 1; }
        }
        return dst;
    }

    // 線分が通る升を全部塗る (升の縁を通る所も両側を塗る)。grid の原点は (gx0, gy0) 升
    internal static void Raster(byte[] grid, int w, int h, int gx0, int gy0, Vector2 a, Vector2 b)
    {
        float ax = (a.x - _origin.x) * _ppu - gx0, ay = (a.y - _origin.y) * _ppu - gy0;
        float bx = (b.x - _origin.x) * _ppu - gx0, by = (b.y - _origin.y) * _ppu - gy0;
        int x = (int)MathF.Floor(ax), y = (int)MathF.Floor(ay);
        int ex = (int)MathF.Floor(bx), ey = (int)MathF.Floor(by);
        float dx = bx - ax, dy = by - ay;
        int sx = dx > 0 ? 1 : -1, sy = dy > 0 ? 1 : -1;
        float tdx = dx != 0 ? MathF.Abs(1f / dx) : float.MaxValue, tdy = dy != 0 ? MathF.Abs(1f / dy) : float.MaxValue;
        float tmx = dx != 0 ? ((sx > 0 ? x + 1 - ax : ax - x) * tdx) : float.MaxValue;
        float tmy = dy != 0 ? ((sy > 0 ? y + 1 - ay : ay - y) * tdy) : float.MaxValue;
        int guard = Math.Abs(ex - x) + Math.Abs(ey - y) + 4;
        for (int i = 0; i < guard; i++)
        {
            if (x >= 0 && y >= 0 && x < w && y < h) grid[y * w + x] = 1;
            if (x == ex && y == ey) break;
            if (MathF.Abs(tmx - tmy) < 1e-6f)
            {
                // 升の角を通る: 両隣も塗って斜めの抜け道を作らない
                if (x + sx >= 0 && x + sx < w && y >= 0 && y < h) grid[y * w + x + sx] = 1;
                if (y + sy >= 0 && y + sy < h && x >= 0 && x < w) grid[(y + sy) * w + x] = 1;
                x += sx; y += sy; tmx += tdx; tmy += tdy;
            }
            else if (tmx < tmy) { x += sx; tmx += tdx; }
            else { y += sy; tmy += tdy; }
        }
    }

    // 当たり判定の輪郭を世界座標の線分 (両端を 2 つずつ) で足す。形は変えない
    internal static void Segments(Collider2D c, List<Vector2> outPairs)
    {
        var t = c.transform;
        var edge = c.TryCast<EdgeCollider2D>();
        if (edge)
        {
            var pts = edge.points;
            Vector2 off = edge.offset;
            if (pts.Length < 2) return;
            Vector2 prev = t.TransformPoint(pts[0] + off);
            for (int i = 1; i < pts.Length; i++)
            {
                Vector2 cur = t.TransformPoint(pts[i] + off);
                outPairs.Add(prev); outPairs.Add(cur);
                prev = cur;
            }
            return;
        }
        var box = c.TryCast<BoxCollider2D>();
        if (box)
        {
            Vector2 hs = box.size * 0.5f, o = box.offset;
            Loop(t, new[] { o + new Vector2(-hs.x, -hs.y), o + new Vector2(hs.x, -hs.y), o + new Vector2(hs.x, hs.y), o + new Vector2(-hs.x, hs.y) }, outPairs);
            return;
        }
        var poly = c.TryCast<PolygonCollider2D>();
        if (poly)
        {
            for (int p = 0; p < poly.pathCount; p++)
            {
                var path = poly.GetPath(p);
                var pts = new Vector2[path.Length];
                for (int i = 0; i < pts.Length; i++) pts[i] = path[i] + poly.offset;
                Loop(t, pts, outPairs);
            }
            return;
        }
        var circle = c.TryCast<CircleCollider2D>();
        if (circle)
        {
            var pts = new Vector2[16];
            for (int i = 0; i < 16; i++)
            {
                float ang = i * MathF.PI / 8f;
                pts[i] = circle.offset + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * circle.radius;
            }
            Loop(t, pts, outPairs);
        }
    }

    private static void Loop(Transform t, Vector2[] local, List<Vector2> outPairs)
    {
        if (local.Length < 2) return;
        for (int i = 0; i < local.Length; i++)
        {
            outPairs.Add(t.TransformPoint(local[i]));
            outPairs.Add(t.TransformPoint(local[(i + 1) % local.Length]));
        }
    }

    // 範囲に掛かる動きの層の壁 (守る物・蓋・船体の外周を含む) の線分。穴から壁の中へ見通せるかの判定用
    internal static List<Vector2> WallSegmentsNear(Vector2 c, float r)
    {
        var list = new List<Vector2>();
        foreach (var col in Physics2D.OverlapCircleAll(c, r, 1 << ShipLayer))
        {
            if (!col || !col.enabled || col.isTrigger || col.gameObject.name == "MrpRubbleBlock") continue;
            Segments(col, list);
        }
        return list;
    }

    // ---- 壊した所を塗り足す ----

    // 切り取りと蓋を作り終えた後に呼ぶ。形の中で、もう歩ける升 (叩いた側・前の穴) から、残った壁 (蓋・守った壁を含む) を
    // 越えずに届く升を歩ける所にする。守った壁の向こう (外壁の外) は塗らない
    public static void Carve(CutShape shape, List<Rect> keep, Vector2 walkableRef)
    {
        if (!Valid) return;
        Vector2 c = shape.Center;
        float r = shape.BoundRadius + 0.1f;
        int x0 = Math.Max(0, (int)MathF.Floor((c.x - r - _origin.x) * _ppu)), x1 = Math.Min(_w - 1, (int)MathF.Floor((c.x + r - _origin.x) * _ppu));
        int y0 = Math.Max(0, (int)MathF.Floor((c.y - r - _origin.y) * _ppu)), y1 = Math.Min(_h - 1, (int)MathF.Floor((c.y + r - _origin.y) * _ppu));
        if (x1 < x0 || y1 < y0) return;
        int bw = x1 - x0 + 1, bh = y1 - y0 + 1, bn = bw * bh;

        // 切った後の壁を描いて 1 升太らせる
        var barrier = new byte[bn];
        var segs = WallSegmentsNear(c, r + 0.2f);
        for (int i = 0; i + 1 < segs.Count; i += 2) Raster(barrier, bw, bh, x0, y0, segs[i], segs[i + 1]);
        var blocked = DilateGrid(barrier, bw, bh, 1);

        // 形の中 (家具の範囲を除く)
        var inside = new byte[bn];
        for (int y = 0; y < bh; y++)
        for (int x = 0; x < bw; x++)
        {
            float wx = _origin.x + (x0 + x + 0.5f) / _ppu, wy = _origin.y + (y0 + y + 0.5f) / _ppu;
            if (shape.SignedDistance(wx, wy) >= 0f) continue;
            if (keep != null && InsideAny(keep, wx, wy)) continue;
            if (_outside != null && BareSkyCell(x0 + x, y0 + y)) continue; // 船の外 (空・宇宙) と際の絵の無い縁は歩ける所にしない (空の上に水が広がる)
            inside[y * bw + x] = 1;
        }

        // もう歩ける升と基準点から塗る
        var queue = new int[bn];
        var seen = new byte[bn];
        var lab = new byte[bn]; // 塗り広げた元の島の番号 (掘って開けた升に引き継ぐ)
        int tail = 0;
        for (int k = 0; k < bn; k++)
        {
            if (inside[k] == 0 || blocked[k] != 0) continue;
            int gx = x0 + k % bw, gy = y0 + k / bw;
            if (_open[gy * _w + gx] == 0) continue;
            seen[k] = 1; queue[tail++] = k;
            lab[k] = _island != null ? _island[gy * _w + gx] : (byte)0;
        }
        {
            int rx = (int)MathF.Floor((walkableRef.x - _origin.x) * _ppu) - x0, ry = (int)MathF.Floor((walkableRef.y - _origin.y) * _ppu) - y0;
            if (rx >= 0 && ry >= 0 && rx < bw && ry < bh)
            {
                int k = ry * bw + rx;
                if (seen[k] == 0 && blocked[k] == 0) { seen[k] = 1; queue[tail++] = k; lab[k] = (byte)IslandAt(walkableRef); }
            }
        }
        int head = 0;
        while (head < tail)
        {
            int k = queue[head++];
            int x = k % bw, y = k / bw;
            if (x > 0) Visit(k - 1, k);
            if (x < bw - 1) Visit(k + 1, k);
            if (y > 0) Visit(k - bw, k);
            if (y < bh - 1) Visit(k + bw, k);
        }
        // 太らせた分を戻す (形の中の、壁の線そのものでない升)。角の升は斜めにしか届かないので 2 段
        int grown = tail;
        for (int step = 0; step < 2; step++)
        {
            int from = grown;
            for (int k = 0; k < bn; k++)
            {
                if (seen[k] != 0 || inside[k] == 0 || barrier[k] != 0 || blocked[k] == 0) continue;
                int x = k % bw, y = k / bw;
                int from2 = x > 0 && seen[k - 1] == 1 ? k - 1 : x < bw - 1 && seen[k + 1] == 1 ? k + 1 :
                            y > 0 && seen[k - bw] == 1 ? k - bw : y < bh - 1 && seen[k + bw] == 1 ? k + bw : -1;
                if (from2 < 0) continue;
                lab[k] = lab[from2];
                queue[grown++] = k;
            }
            for (int i = from; i < grown; i++) seen[queue[i]] = 1;
        }
        for (int i = 0; i < grown; i++)
        {
            int k = queue[i];
            int g = (y0 + k / bw) * _w + x0 + k % bw;
            _open[g] = 1;
            // 掘って開けた升は、つながった側の島の番号を持つ (穴の奥から掘り進んでも元の床の高さで比べられるように)
            if (_island != null && _island[g] == 0) _island[g] = lab[k];
        }

        void Visit(int k2, int parent)
        {
            if (seen[k2] != 0 || inside[k2] == 0 || blocked[k2] != 0) return;
            seen[k2] = 1;
            lab[k2] = lab[parent];
            queue[tail++] = k2;
        }
    }

    // 範囲の中で、今の壁の線を越えずに船の外 (空・宇宙) へつながっている歩けない所 (壁の中)。
    // 外壁から内側へ出っ張った塊 (Mira の倉庫の左下の箱) は空との間に壁の線が無く、塊の面はどれも外に面して見えないが、
    // 抜くと穴が空へつながって蓋が空の上に張られる (空へ歩き出せ、蓋が視界を遮って空が黒くなる)。
    // 船の外を持たないマップ (Polus・Fungle) は null
    internal sealed class SkyReach
    {
        internal int X0, Y0, W, H;
        internal byte[] Sky;

        public bool At(Vector2 p)
        {
            int x = (int)MathF.Floor((p.x - _origin.x) * _ppu) - X0, y = (int)MathF.Floor((p.y - _origin.y) * _ppu) - Y0;
            return x >= 0 && y >= 0 && x < W && y < H && Sky[y * W + x] != 0;
        }
    }

    // within = 抜く形。形の中 (面の両側を見る分だけ広げる) だけを塗る: 穴が開くのは形の中だけなので、形の中で空とつながる所だけが
    // 空へ抜ける (端が空に開いた厚い壁の帯でも、空の端から離れた所は内壁として抜ける)。null = 範囲全体 (判定図用)
    public static SkyReach SkyNear(Vector2 c, float r, CutShape within = null)
    {
        if (!Valid || _outside == null) return null;
        int x0 = Math.Max(0, (int)MathF.Floor((c.x - r - _origin.x) * _ppu)), x1 = Math.Min(_w - 1, (int)MathF.Floor((c.x + r - _origin.x) * _ppu));
        int y0 = Math.Max(0, (int)MathF.Floor((c.y - r - _origin.y) * _ppu)), y1 = Math.Min(_h - 1, (int)MathF.Floor((c.y + r - _origin.y) * _ppu));
        if (x1 < x0 || y1 < y0) return null;
        int bw = x1 - x0 + 1, bh = y1 - y0 + 1, bn = bw * bh;

        // 今の壁の線を描き、継ぎ目が塞がるだけ太らせる
        var barrier = new byte[bn];
        var segs = WallSegmentsNear(c, r + 0.2f);
        for (int i = 0; i + 1 < segs.Count; i += 2) Raster(barrier, bw, bh, x0, y0, segs[i], segs[i + 1]);
        var blocked = DilateGrid(barrier, bw, bh, Dilate);

        // 形の中
        byte[] inside = null;
        if (within != null)
        {
            inside = new byte[bn];
            for (int k = 0; k < bn; k++)
            {
                float wx = _origin.x + (x0 + k % bw + 0.5f) / _ppu, wy = _origin.y + (y0 + k / bw + 0.5f) / _ppu;
                if (within.SignedDistance(wx, wy) < SkyProbe + 0.05f) inside[k] = 1;
            }
        }

        // 船の外から、線を越えずに歩けない升だけを塗る
        var sky = new byte[bn];
        var queue = new int[bn];
        int tail = 0;
        for (int k = 0; k < bn; k++)
        {
            if (blocked[k] != 0 || (inside != null && inside[k] == 0)) continue;
            int g = (y0 + k / bw) * _w + x0 + k % bw;
            if (_outside[g] != 1) continue;
            sky[k] = 1; queue[tail++] = k;
        }
        int head = 0;
        while (head < tail)
        {
            int k = queue[head++];
            int x = k % bw, y = k / bw;
            if (x > 0) Visit(k - 1);
            if (x < bw - 1) Visit(k + 1);
            if (y > 0) Visit(k - bw);
            if (y < bh - 1) Visit(k + bw);
        }
        return new SkyReach { X0 = x0, Y0 = y0, W = bw, H = bh, Sky = sky };

        void Visit(int k2)
        {
            if (sky[k2] != 0 || blocked[k2] != 0 || (inside != null && inside[k2] == 0)) return;
            if (_open[(y0 + k2 / bw) * _w + x0 + k2 % bw] != 0) return;
            sky[k2] = 1;
            queue[tail++] = k2;
        }
    }

    // 面 (中点 m・法線 n) のどちらかの側が、壁の線を越えずに船の外へつながる壁の中か (線の太らせた分より奥を見る)
    public static bool FacesSky(SkyReach sky, Vector2 m, Vector2 n) =>
        sky != null && (sky.At(m + n * SkyProbe) || sky.At(m - n * SkyProbe));

    private const float SkyProbe = 0.2f;

    private static bool InsideAny(List<Rect> rects, float x, float y)
    {
        foreach (var rc in rects)
            if (x >= rc.xMin && x <= rc.xMax && y >= rc.yMin && y <= rc.yMax) return true;
        return false;
    }

    // スケルドの船体のメッシュ。形を読めない (読み取り不可の) メッシュは外を作らない (外の判定が全部外れるより無い方がよい)
    private static List<MeshFilter> HullMeshes(ShipStatus ship)
    {
        var list = new List<MeshFilter>();
        foreach (var mf in ship.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!mf || !mf.gameObject.name.StartsWith("Hull", StringComparison.Ordinal)) continue;
            var mesh = mf.sharedMesh;
            if (!mesh) continue;
            if (!mesh.isReadable) { Plugin.Logger.LogWarning($"solid map: hull mesh {mf.gameObject.name} not readable"); return new List<MeshFilter>(); }
            list.Add(mf);
        }
        Plugin.Logger.LogInfo($"solid map: hull meshes={list.Count}");
        return list;
    }

    // ---- エアシップの船体の塊 ----

    private static int BuildHull(ShipStatus ship)
    {
        foreach (var sr in ship.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (!sr || !sr.gameObject.name.StartsWith("HullBlock", StringComparison.Ordinal)) continue;
            var b = sr.bounds;
            HullRects.Add(new Rect(b.min.x, b.min.y, b.size.x, b.size.y));
        }
        if (HullRects.Count == 0) return 0;

        // 塊の和の外周 (他の板に覆われていない辺) のうち、両側とも歩けない所にだけ壊れない壁を置く。
        // 外周が部屋を横切る所 (部屋の絵が塊の外へはみ出している) には置かない
        var go = new GameObject(HullEdgeName) { layer = ShipLayer };
        go.transform.SetParent(ship.transform, true);
        var t = go.transform;
        int made = 0;
        float step = 1f / _ppu;
        foreach (var r in HullRects)
        {
            made += Side(new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), new Vector2(0, -1));
            made += Side(new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax), new Vector2(1, 0));
            made += Side(new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax), new Vector2(0, 1));
            made += Side(new Vector2(r.xMin, r.yMax), new Vector2(r.xMin, r.yMin), new Vector2(-1, 0));
        }
        return made;

        int Side(Vector2 a, Vector2 b, Vector2 outward)
        {
            float len = (b - a).magnitude;
            Vector2 dir = (b - a) / len;
            int count = (int)MathF.Ceiling(len / step);
            var run = new List<Vector2>();
            int k = 0;
            for (int i = 0; i <= count; i++)
            {
                Vector2 p = a + dir * Math.Min(i * step, len);
                bool outer = !InHull(p + outward * 0.02f);
                bool keepPt = outer && Solid(p + outward * EdgeProbe) && Solid(p - outward * EdgeProbe);
                if (keepPt) run.Add(p);
                if ((!keepPt || i == count) && run.Count > 0)
                {
                    if (run.Count >= 2 && (run[run.Count - 1] - run[0]).magnitude >= 0.2f)
                    {
                        var col = go.AddComponent<EdgeCollider2D>();
                        col.points = new[] { (Vector2)t.InverseTransformPoint(run[0]), (Vector2)t.InverseTransformPoint(run[run.Count - 1]) };
                        k++;
                    }
                    run.Clear();
                }
            }
            return k;
        }
    }

    // ---- テスト用 ----

    // 範囲の地図を PPM に書く (白 = 歩ける・灰 = 船体の塊の中・黒 = それ以外)
    internal static string Dump(Vector2 c, float r, string path)
    {
        if (!Valid) return "invalid";
        int x0 = Math.Max(0, (int)MathF.Floor((c.x - r - _origin.x) * _ppu)), x1 = Math.Min(_w - 1, (int)MathF.Floor((c.x + r - _origin.x) * _ppu));
        int y0 = Math.Max(0, (int)MathF.Floor((c.y - r - _origin.y) * _ppu)), y1 = Math.Min(_h - 1, (int)MathF.Floor((c.y + r - _origin.y) * _ppu));
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        var body = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int gx = x0 + x, gy = y1 - y;
            byte v = _open[gy * _w + gx] != 0 ? (byte)255 : InHull(new Vector2(_origin.x + (gx + 0.5f) / _ppu, _origin.y + (gy + 0.5f) / _ppu)) ? (byte)90 : (byte)0;
            int o = (y * w + x) * 3;
            body[o] = body[o + 1] = body[o + 2] = v;
            if (_leaked != null && _leaked[gy * _w + gx] != 0) { body[o] = 0; body[o + 1] = 60; body[o + 2] = 200; }
            else if (_outside != null && _outside[gy * _w + gx] == 1) { body[o] = 40; body[o + 1] = 40; body[o + 2] = 90; }
        }
        // 壁の線 (動きの層) を赤で重ねる
        var segs = WallSegmentsNear(c, r * 1.42f);
        var line = new byte[w * h];
        for (int i = 0; i + 1 < segs.Count; i += 2) Raster(line, w, h, x0, y0, segs[i], segs[i + 1]);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (line[y * w + x] == 0) continue;
            int o = ((h - 1 - y) * w + x) * 3;
            body[o] = 255; body[o + 1] = 0; body[o + 2] = 0;
        }
        using (var fs = System.IO.File.Create(path))
        {
            var head = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
            fs.Write(head, 0, head.Length);
            fs.Write(body, 0, body.Length);
        }
        return $"{w}x{h}";
    }
}
