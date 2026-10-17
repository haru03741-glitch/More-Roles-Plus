using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// マップごとに人の目で付けた注釈 (マップの絵に塗った範囲)。当たり判定の形からは分からないことを上書きする。
//   Keep = 壊れてほしくない物 (家具など)。家具の保護範囲に足す (見た目も当たり判定も残る)
//   Free = 壊れてよい所。名前や形で自動に守っている物 (小物・家具) でも壊す (扉・外壁は除く)
//   Low / High / Higher = 周りより低い / 高い / さらに高い床。壁の両側で違えば高さの違う床の境として扱う (差の大きさは見ない)
//   Void = 壊せないエリア (奈落など)。範囲の中は壁も船体の塊も削らない (どの向きから掘っても範囲の手前で止まる)。
//          床の高さとしては一番低い
//   Low / High / Higher / Void を壁の上に線で引いた所は、壊しても越えられない壁 (Marked)
// 範囲は 1/8 単位の長方形の一覧 (MapNotesData.cs・画像から生成)
internal static partial class MapNotes
{
    private static ShipStatus _ship;
    private static Rect[] _keep = Array.Empty<Rect>();
    private static Rect[] _low = Array.Empty<Rect>();
    private static Rect[] _high = Array.Empty<Rect>();
    private static Rect[] _higher = Array.Empty<Rect>();
    private static Rect[] _free = Array.Empty<Rect>();
    private static Rect[] _void = Array.Empty<Rect>();
    private static Rect[] _air = Array.Empty<Rect>();
    // Level 用の範囲 (xMin, yMin, xMax, yMax の 4 つ組)。Rect.Contains は interop なので、壁の線に沿って何度も引く所は小数で比べる
    private static float[] _voidB = Array.Empty<float>(), _lowB = Array.Empty<float>(), _higherB = Array.Empty<float>(), _highB = Array.Empty<float>();

    public static string Stats => $"notes keep={_keep.Length} free={_free.Length} low={_low.Length} high={_high.Length} higher={_higher.Length} void={_void.Length}";

    private static void Ensure()
    {
        var ship = ShipStatus.Instance;
        if (_ship == ship) return;
        _ship = ship;
        _keep = _free = _low = _high = _higher = _void = _air = Array.Empty<Rect>();
        _voidB = _lowB = _higherB = _highB = Array.Empty<float>();
        if (!ship) return;
        // 派生の型から先に調べる (どのマップも ShipStatus を継ぐ)
        if (ship.TryCast<AirshipStatus>() != null) { Load(AirshipKeep, AirshipFree, AirshipLow, AirshipHigh, AirshipHigher, AirshipVoid); _air = ToRects(Side(AirshipAir)); }
        else if (ship.TryCast<FungleShipStatus>() != null) { Load(FungleKeep, FungleFree, FungleLow, FungleHigh, FungleHigher, FungleVoid); _air = ToRects(Side(FungleAir)); }
        else if (ship.TryCast<PolusShipStatus>() != null) { Load(PolusKeep, PolusFree, PolusLow, PolusHigh, PolusHigher, PolusVoid); _air = ToRects(Side(PolusAir)); }
        else if (ship.TryCast<MiraShipStatus>() != null) { Load(MiraKeep, MiraFree, MiraLow, MiraHigh, MiraHigher, MiraVoid); _air = ToRects(Side(MiraAir)); }
        else if (ship.Type == ShipStatus.MapType.Ship && (MapFlip.Flipped || ship.transform.lossyScale.x > 0f)) { Load(SkeldKeep, SkeldFree, SkeldLow, SkeldHigh, SkeldHigher, SkeldVoid); _air = ToRects(Side(SkeldAir)); } // 本編の左右反転のスケルド (dlekS) は座標が合わないので使わない
    }

    // 表は反転前の船の座標。左右反転の船では鏡に映して使う
    private static float[] Side(float[] v) => MapFlip.Flipped ? MapFlip.MirrorBoxes(v) : v;

    private static void Load(float[] keep, float[] free, float[] low, float[] high, float[] higher, float[] voids)
    {
        keep = Side(keep); free = Side(free); low = Side(low); high = Side(high); higher = Side(higher); voids = Side(voids);
        _void = ToRects(voids);
        _keep = ToRects(keep);
        _free = ToRects(free);
        _low = ToRects(low);
        _high = ToRects(high);
        _higher = ToRects(higher);
        _voidB = ToBoxes(voids);
        _lowB = ToBoxes(low);
        _higherB = ToBoxes(higher);
        _highB = ToBoxes(high);
    }

    // Rect.MinMaxRect と同じ値 (幅 = max - min・xMax = min + 幅) を前もって出しておく
    private static float[] ToBoxes(float[] v)
    {
        var b = new float[v.Length / 4 * 4];
        for (int i = 0; i + 3 < v.Length; i += 4)
        {
            float x0 = v[i], y0 = v[i + 1];
            b[i] = x0; b[i + 1] = y0; b[i + 2] = x0 + (v[i + 2] - x0); b[i + 3] = y0 + (v[i + 3] - y0);
        }
        return b;
    }

    // Rect.Contains と同じ判定 (下と左の辺は含み、上と右の辺は含まない)
    private static bool InAny(float[] b, float x, float y)
    {
        for (int i = 0; i < b.Length; i += 4)
            if (x >= b[i] && x < b[i + 2] && y >= b[i + 1] && y < b[i + 3]) return true;
        return false;
    }

    private static Rect[] ToRects(float[] v)
    {
        var r = new Rect[v.Length / 4];
        for (int i = 0; i < r.Length; i++) r[i] = Rect.MinMaxRect(v[i * 4], v[i * 4 + 1], v[i * 4 + 2], v[i * 4 + 3]);
        return r;
    }

    // 円 (中心 c・半径 r) に掛かる「壊れてほしくない物」と「壊せないエリア」の範囲を足す
    public static void AddKeep(Vector2 c, float r, List<Rect> list)
    {
        Ensure();
        Add(_keep);
        Add(_void);

        void Add(Rect[] rects)
        {
            foreach (var k in rects)
            {
                float dx = Math.Max(Math.Max(k.xMin - c.x, 0f), c.x - k.xMax);
                float dy = Math.Max(Math.Max(k.yMin - c.y, 0f), c.y - k.yMax);
                if (dx * dx + dy * dy <= r * r) list.Add(k);
            }
        }
    }

    public static bool InKeep(Vector2 p)
    {
        Ensure();
        foreach (var k in _keep) if (k.Contains(p)) return true;
        return false;
    }

    public static bool InVoid(Vector2 p)
    {
        Ensure();
        foreach (var k in _void) if (k.Contains(p)) return true;
        return false;
    }

    public static bool InFree(Vector2 p)
    {
        Ensure();
        foreach (var k in _free) if (k.Contains(p)) return true;
        return false;
    }

    // 高さの注釈が塗ってあるマップか
    public static bool HasLevels
    {
        get { Ensure(); return _void.Length + _low.Length + _high.Length + _higher.Length > 0; }
    }

    // 升 (原点 org・1 辺 cell・w×h) の真ん中の高さを lvl に塗る (Level と同じ優先: 奈落 > 低い > さらに高い > 高い)。
    // 塗っていない升はそのまま (先に塗った区域の高さを残す)。升ごとに範囲を全部見ると重いので、範囲ごとに升を塗る
    public static void RasterLevels(sbyte[] lvl, int w, int h, Vector2 org, float cell)
    {
        Ensure();
        Fill(_high, 1);
        Fill(_higher, 2);
        Fill(_low, -1);
        Fill(_void, -2);

        void Fill(Rect[] rects, int level)
        {
            foreach (var r in rects)
            {
                // 真ん中 (x + 0.5) * cell が範囲に入る升
                int x0 = Math.Max(0, (int)MathF.Ceiling((r.xMin - org.x) / cell - 0.5f)), x1 = Math.Min(w - 1, (int)MathF.Floor((r.xMax - org.x) / cell - 0.5f));
                int y0 = Math.Max(0, (int)MathF.Ceiling((r.yMin - org.y) / cell - 0.5f)), y1 = Math.Min(h - 1, (int)MathF.Floor((r.yMax - org.y) / cell - 0.5f));
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++) lvl[y * w + x] = (sbyte)level;
            }
        }
    }

    // 空 (縁を越えた水が落ちて消える所) を塗ったマップか
    public static bool HasAir
    {
        get { Ensure(); return _air.Length > 0; }
    }

    // 升 (原点 org・1 辺 cell・w×h) の真ん中が空の範囲に入る升を 1 にする
    public static void RasterAir(byte[] air, int w, int h, Vector2 org, float cell)
    {
        Ensure();
        foreach (var r in _air)
        {
            int x0 = Math.Max(0, (int)MathF.Ceiling((r.xMin - org.x) / cell - 0.5f)), x1 = Math.Min(w - 1, (int)MathF.Floor((r.xMax - org.x) / cell - 0.5f));
            int y0 = Math.Max(0, (int)MathF.Ceiling((r.yMin - org.y) / cell - 0.5f)), y1 = Math.Min(h - 1, (int)MathF.Floor((r.yMax - org.y) / cell - 0.5f));
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++) air[y * w + x] = 1;
        }
    }

    // その点の床の高さ (-2 = 奈落・-1 = 低い・1 = 高い・2 = さらに高い・0 = 塗っていない)
    public static int Level(Vector2 p)
    {
        Ensure();
        return LevelNoEnsure(p.x, p.y);
    }

    // 船の確認 (Ensure) を呼び出し側で済ませた後に、同じ船で何度も引く用
    internal static void EnsureShip() => Ensure();

    internal static int LevelNoEnsure(float x, float y)
    {
        if (InAny(_voidB, x, y)) return -2;
        if (InAny(_lowB, x, y)) return -1;
        if (InAny(_higherB, x, y)) return 2;
        if (InAny(_highB, x, y)) return 1;
        return 0;
    }
}
