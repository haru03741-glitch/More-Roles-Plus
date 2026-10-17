using System;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 閉まった扉は壁と同じく、手前の面に水が這い上がる。扉の絵は水より手前に描かれるので、家具の水面の板 (水のシェーダの _Mode 3) を
// 扉の絵ごとに重ね、絵の下の縁から測った画面の高さ (壁の面と同じく 1 単位 = 1 単位) が水位より低い所を覆う。
// 開いている扉と開け閉めの途中は板を隠す (開いた扉は枠だけが残り、床の水がその間を通る)。影の中は家具と同じく写しを影の板の手前に置く
internal static partial class PropSim
{
    private const float DoorFaceH = 8f; // 扉の絵は全部が前面 (天板として頭打ちにしない)
    private const float DoorProbe = 0.35f; // 深さを見る点の扉の絵からの距離 (閉じた扉の升は絵より 1 升ほど広く水を通さない)

    private sealed class DoorPlate
    {
        public Sunk S;
        public IntPtr Sp; // 板を作った時の扉の絵 (閉じた扉の絵が替わったら作り直す)
    }

    private static DoorPlate[][] _doorPlates = Array.Empty<DoorPlate[]>();
    private static long[] _doorClosedAt = Array.Empty<long>();
    private static float[] _doorBox = Array.Empty<float>(); // 扉ごとの絵の範囲 (x0, y0, x1, y1)。閉じて最初に板を出す時に測る
    private static ulong _doorPlateBits;
    private static int _doorPlateGen = -1;

    private static void DoorSunk(long now)
    {
        int n = Decompression.DoorCount;
        if (_doorPlateGen != GameClock.ShipGen || _doorPlates.Length != n)
        {
            DoorSunkClear();
            _doorPlateGen = GameClock.ShipGen;
            _doorPlates = new DoorPlate[n][];
            _doorClosedAt = new long[n];
            _doorBox = new float[n * 4];
            _doorPlateBits = Decompression.LatestDoorBits;
        }
        ulong bits = Decompression.LatestDoorBits;
        if (bits != _doorPlateBits)
        {
            ulong changed = bits ^ _doorPlateBits;
            _doorPlateBits = bits;
            for (int i = 0; i < n; i++)
                if ((changed >> i & 1) != 0) { _doorClosedAt[i] = now; _doorBox[i * 4] = float.NaN; }
        }
        for (int i = 0; i < n; i++)
        {
            bool closed = (bits >> i & 1) == 0 && now - _doorClosedAt[i] >= WaterArt.DoorSettleMs;
            float lvl = closed ? DoorLevel(i) : 0f;
            if (lvl < SunkShow) { DoorHide(i); continue; }
            var srs = WaterArt.DoorRenderers(i);
            var plates = _doorPlates[i] ??= new DoorPlate[srs.Length];
            for (int k = 0; k < srs.Length && k < plates.Length; k++)
            {
                var d = srs[k];
                if (!d || !d.enabled || !d.gameObject.activeInHierarchy) { PlateHide(plates[k]); continue; }
                var p = plates[k] ??= new DoorPlate();
                var s = p.S;
                if (s == null || !s.Go)
                {
                    var sp = d.sprite;
                    if (!sp) continue;
                    if (s != null) PlateDestroy(s);
                    s = p.S = DoorPlateMake(d, sp);
                    p.Sp = sp.Pointer;
                }
                if (!s.Shown)
                {
                    // 閉じた扉の絵が板を作った時と違う (開け閉めの途中で作った): 作り直す
                    var sp = d.sprite;
                    if (sp && sp.Pointer != p.Sp)
                    {
                        PlateDestroy(s);
                        s = p.S = DoorPlateMake(d, sp);
                        p.Sp = sp.Pointer;
                    }
                    s.Go.SetActive(true);
                    s.Shown = true;
                    _sunkAny = true;
                    SunkShadow(s);
                }
                if (MathF.Abs(lvl - s.Lvl) < SunkStep) continue;
                s.Lvl = lvl;
                s.Block.SetFloat(LvlId, lvl);
                s.Sr.SetPropertyBlock(s.Block);
                if (s.Shadow) s.Shadow.SetPropertyBlock(s.Block);
            }
        }
    }

    // 扉の手前の水の深さ: 絵の下の縁の少し下と、左右の少し外 (横向きの扉は通路が左右にある) の深い方
    private static float DoorLevel(int i)
    {
        int b = i * 4;
        if (float.IsNaN(_doorBox[b]) || _doorBox[b] == 0f && _doorBox[b + 2] == 0f)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var d in WaterArt.DoorRenderers(i))
            {
                if (!d || !d.enabled || !d.sprite || !d.gameObject.activeInHierarchy) continue;
                var bb = d.bounds;
                Vector3 mn = bb.min, mx = bb.max;
                x0 = Math.Min(x0, mn.x); y0 = Math.Min(y0, mn.y); x1 = Math.Max(x1, mx.x); y1 = Math.Max(y1, mx.y);
            }
            if (x0 > x1) return 0f;
            _doorBox[b] = x0; _doorBox[b + 1] = y0; _doorBox[b + 2] = x1; _doorBox[b + 3] = y1;
        }
        float cx = (_doorBox[b] + _doorBox[b + 2]) * 0.5f, cy = (_doorBox[b + 1] + _doorBox[b + 3]) * 0.5f;
        float south = WaterSim.CurrentAt(FxMath.V2(cx, _doorBox[b + 1] - DoorProbe), out _, out _);
        float left = WaterSim.CurrentAt(FxMath.V2(_doorBox[b] - DoorProbe, cy), out _, out _);
        float right = WaterSim.CurrentAt(FxMath.V2(_doorBox[b + 2] + DoorProbe, cy), out _, out _);
        return Math.Max(south, Math.Max(left, right));
    }

    private static Sunk DoorPlateMake(SpriteRenderer d, Sprite sp)
    {
        var s = new Sunk();
        var baseCol = SpriteFloorLine(sp, d.flipY, d.transform.lossyScale.y, out float vPerUnit, out float vBase, out var baseUv);
        var size = d.drawMode != SpriteDrawMode.Simple ? d.size : default;
        SunkPlate(s, d.transform, sp, FxMath.V3(0f, 0f, SunkFrontZ), FxMath.V3(1f, 1f, 1f), d.flipX, d.flipY, d.drawMode, size, vPerUnit, vBase, baseCol, baseUv);
        s.Block.SetFloat(SilRId, 0f);
        s.Block.SetFloat(RiseId, 1f);
        s.Block.SetFloat(FaceHId, DoorFaceH);
        s.Block.SetFloat(CoverId, 0f);
        s.Go.SetActive(false);
        return s;
    }

    private static void DoorHide(int i)
    {
        var plates = _doorPlates[i];
        if (plates == null) return;
        foreach (var p in plates) PlateHide(p);
    }

    private static void PlateHide(DoorPlate p)
    {
        var s = p?.S;
        if (s == null || !s.Shown) return;
        if (s.Go) s.Go.SetActive(false);
        s.Shown = false;
    }

    private static void PlateDestroy(Sunk s)
    {
        if (s.Go) UnityEngine.Object.Destroy(s.Go);
        if (s.Base) UnityEngine.Object.Destroy(s.Base);
    }

    private static void DoorSunkHideAll()
    {
        for (int i = 0; i < _doorPlates.Length; i++) DoorHide(i);
    }

    private static void DoorSunkClear()
    {
        foreach (var plates in _doorPlates)
            if (plates != null)
                foreach (var p in plates)
                    if (p?.S != null) PlateDestroy(p.S);
        _doorPlates = Array.Empty<DoorPlate[]>();
    }
}
