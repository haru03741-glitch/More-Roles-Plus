using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 手前の絵 (機関・端末・壁の端末・柵など、クルーと同じ奥行きの帯にあって床の水より手前に描かれる動かない絵) の前面にも、壁の面と同じく水が這い上がる。
// 床の水はこの絵の下に描かれ (床マスクも絵の画素を床から抜いてある) ので、そのままでは水が絵の縁で止まる。扉と同じく水面の板 (水のシェーダの _Mode 3) を
// 絵ごとに子として重ね (クルーとの前後は絵と同じ)、列ごとの床の線から測った画面の高さが、その列の手前の水位より低い所を覆う。
// 床の線 = 絵の下で床マスクが床に変わる所 (床に立つ物は絵の下の縁・壁に掛かった物は壁の下の縁)。水位も列ごとに板の床の線のテクスチャの G に持つ
// (幅の広い絵は片側だけ水に浸かる)。影の中は家具と同じく写しを影の板の手前に置く
internal static partial class PropSim
{
    private const float FrontBandZ = 0.1f;   // |z| がこれ未満の絵 (クルーと同じ奥行きの帯) だけ。頭上の重ね絵 (壁の上面・吊り物) は下の縁が床でない
    private const float FrontLvlMax = 4f;     // 列ごとの水位の G = 255 (壁の面の高さの上限と同じくらい)
    private const float FrontFaceMax = 4f;    // 絵の下から床の線を探す距離 (壁に掛かった物は壁の面の高さぶん下に床がある)
    private const float FrontProbeNear = 0.15f, FrontProbeFar = 0.4f; // 水位を見る点の床の線からの距離 (絵の足元の升は当たり判定で水が入らないことがある)
    private const int FrontGatePts = 5;       // 板の無い絵が浸かったかを見る点の数 (絵の幅に並べる)
    private const int FrontMakePerTick = 2;   // 1 回に列を測る・板を作る絵の数 (一斉に水が来ても 1 回で作り切らない)
    private const long FrontCheckMs = 1000;   // 出している板の絵がまだ描かれているかを見る間隔
    private const float FrontDrawn = 0.25f;   // 板が描く絵の不透明度 (床マスクを作る道具が絵の画素を床から抜く閾値と同じ)

    private sealed class FrontArt
    {
        public SpriteRenderer Sr;
        public Vector4 Box; // 絵の範囲 (x0, y0, x1, y1)。動かない絵なので集めた時に 1 回
        public float[] GateX = new float[FrontGatePts], GateY = new float[FrontGatePts]; // 浸かったかを見る床の線 (NaN = 無し)
        public float[] ColX, ColY; // 列ごとの床の線 (世界)。ColY が NaN の列は水を描かない
        public byte[] Px;          // 板の床の線のテクスチャ (RGBA32: R = 床の線の v・G = 水位)
        public Vector4 Range;      // 列の範囲 (u0, 1/du) と床の線の v の範囲 (v0, dv)
        public float VPerUnit;
        public Sunk S;
        public bool Failed, Off; // Off = 絵が今は描かれていない
        public long Checked;
    }

    private static readonly List<FrontArt> FrontArts = new();
    private static readonly List<(SpriteRenderer Sr, Vector4 Box)> FrontOver = new(); // 帯より手前の重ね絵 (地図のアニメ・壁の上面)。影の写しで抜くだけ
    private static int _frontGen = -1;
    private static readonly int LvlColsId = Shader.PropertyToID("_LvlCols"), LvlMaxId = Shader.PropertyToID("_LvlMax"), SilCutId = Shader.PropertyToID("_SilCut");

    private static bool _frontOff; // 確認用 (bridge `props front off`): 板を出さない (前の見え方と見比べる)

    private static void FrontSunk(long now)
    {
        if (_frontOff) { FrontSunkHideAll(); return; }
        if (_frontGen != GameClock.ShipGen && !FrontCollect()) return;
        int made = 0;
        float full = WaterSim.Full;
        foreach (var a in FrontArts)
        {
            if (a.Failed) continue;
            var s = a.S;
            if (s != null && now - a.Checked >= FrontCheckMs)
            {
                // 絵が消えた・隠れた (壊れた壁と一緒に外れた等) 間は板も出さない
                a.Checked = now;
                var sr = a.Sr;
                if (!sr) { SunkHide(s); a.Failed = true; continue; }
                a.Off = !sr.enabled || !sr.gameObject.activeInHierarchy;
            }
            if (a.Off) { SunkHide(s); continue; }
            if (s == null || !s.Shown)
            {
                float gate = 0f;
                for (int i = 0; i < FrontGatePts; i++)
                    if (!float.IsNaN(a.GateY[i])) gate = Math.Max(gate, FrontDepth(a.GateX[i], a.GateY[i], full));
                if (gate < SunkShow) continue;
                if (s == null || !s.Go)
                {
                    if (made >= FrontMakePerTick) continue;
                    made++;
                    if (!FrontMake(a)) { a.Failed = true; continue; }
                    s = a.S;
                    a.Checked = now;
                }
            }
            // 列ごとの水位を G へ。前と 2 段以上違う列があれば書き直す (1 段の揺れでは書き直さない)
            var px = a.Px;
            bool dirty = false;
            float top = 0f;
            for (int c = 0; c < BaseCols; c++)
            {
                float y = a.ColY[c];
                float d = float.IsNaN(y) ? 0f : FrontDepth(a.ColX[c], y, full);
                top = Math.Max(top, d);
                int g = Math.Clamp((int)MathF.Round(d / FrontLvlMax * 255f), 0, 255);
                int old = px[c * 4 + 1];
                if (Math.Abs(g - old) >= 2 || (g == 0) != (old == 0)) { px[c * 4 + 1] = (byte)g; dirty = true; }
            }
            bool move = !(MathF.Abs(top - s.Lvl) < SunkStep); // 最初 (Lvl = NaN) も書く
            if (dirty || move || !s.Shown && top >= SunkShow)
            {
                // 板は絵の子なので、絵が壊れて消えると次の生存確認より先に板も消えている
                if (!s.Go) { s.Shown = false; a.Failed = true; continue; }
                // 隠す時も書いておく (次に出す時に列の差分が 0 で古い水位のまま出ない)
                if (dirty) FrontUpload(s.Base, px);
            }
            if (top < SunkShow) { SunkHide(s); continue; }
            if (!s.Shown)
            {
                s.Go.SetActive(true);
                s.Shown = true;
                _sunkAny = true;
                SunkShadow(s);
                if (s.OccDirty) FrontOcc(a);
            }
            if (!move) continue;
            s.Lvl = top;
            s.Block.SetFloat(LvlId, top);
            s.Sr.SetPropertyBlock(s.Block);
            if (s.Shadow) s.Shadow.SetPropertyBlock(s.Block);
        }
    }

    // 床の線 (x, y) の手前の水の深さ (単位): 線のすぐ下と少し下の深い方
    private static float FrontDepth(float x, float y, float full) =>
        Math.Max(WaterSim.DepthAt(FxMath.V2(x, y - FrontProbeNear)), WaterSim.DepthAt(FxMath.V2(x, y - FrontProbeFar))) / full;

    // 船の手前の絵を集め、浸かったかを見る点を測る (板と列は浸かってから)。船ごとに 1 回
    private static bool FrontCollect()
    {
        FrontSunkClear();
        var ship = ShipStatus.Instance;
        if (!ship || !DamageMap.Ready() || !FloorMask.Active) return false;
        _frontGen = GameClock.ShipGen;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        string shipName = ship.name.Replace("(Clone)", "");
        var roomIds = new HashSet<int>();
        foreach (var r in DamageMap.RoomArts) if (r) roomIds.Add(r.GetInstanceID());
        // 高さの表に載った動かない家具 (Stills) は家具の水面の板が覆う
        var furnished = new HashSet<int>();
        foreach (var p in Furnished) if (p.Tr) furnished.Add(p.Tr.GetInstanceID());
        // 扉の絵 (除染室の扉など本編の扉でない物も) は扉の板が閉じている間だけ覆う
        for (int i = 0, n = Decompression.DoorCount; i < n; i++)
            foreach (var d in WaterArt.DoorRenderers(i)) if (d) furnished.Add(d.transform.GetInstanceID());
        int tiled = 0, turned = 0;
        foreach (var sr in ship.GetComponentsInChildren<SpriteRenderer>(false))
        {
            if (!FloorMask.CoverArt(sr, shipName, roomIds)) continue;
            var tr = sr.transform;
            if (furnished.Contains(tr.GetInstanceID())) continue;
            float z = tr.position.z;
            if (z >= FrontBandZ || FloorMask.AlwaysFloor(sr.sprite.name)) continue;
            if (z <= -FrontBandZ)
            {
                var ob = sr.bounds;
                Vector3 omn = ob.min, omx = ob.max;
                FrontOver.Add((sr, V4(omn.x, omn.y, omx.x, omx.y)));
                continue;
            }
            // 並べて描く絵は網目と描く形が違い、回した絵は列が縦にならない (どちらも本編の手前の絵にはほぼ無い)
            if (sr.drawMode != SpriteDrawMode.Simple) { tiled++; continue; }
            var m = tr.localToWorldMatrix;
            if (MathF.Abs(m.m01) > 1e-4f || MathF.Abs(m.m10) > 1e-4f) { turned++; continue; }
            var b = sr.bounds;
            Vector3 mn = b.min, mx = b.max;
            var a = new FrontArt { Sr = sr, Box = V4(mn.x, mn.y, mx.x, mx.y) };
            for (int i = 0; i < FrontGatePts; i++)
            {
                float x = mn.x + (i + 0.5f) / FrontGatePts * (mx.x - mn.x);
                a.GateX[i] = x;
                a.GateY[i] = FloorMask.FloorLine(x, mn.y, mx.y - mn.y, FrontFaceMax);
            }
            FrontArts.Add(a);
        }
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Plugin.Logger.LogInfo($"[PropSim] front arts={FrontArts.Count} over={FrontOver.Count} tiled={tiled} turned={turned} ({ms:F1}ms)");
        return true;
    }

    // 列ごとの床の線を測って板を作る (隠したまま)。型の uv の列 (MeshBase と同じ BaseCols 列) ごとに、絵の一番下から床マスクで床の線を探す
    private static bool FrontMake(FrontArt a)
    {
        var sr = a.Sr;
        if (a.S != null) { PlateDestroy(a.S); a.S = null; }
        if (!sr || !sr.sprite) return false;
        var sp = sr.sprite;
        if (!UvMap(sp, out float au, out float bu, out float av, out float bv)) return false;
        var m = sr.transform.localToWorldMatrix;
        float fx = (sr.flipX ? -1f : 1f) * m.m00, fy = (sr.flipY ? -1f : 1f) * m.m11;
        if (MathF.Abs(fx) < 1e-6f || MathF.Abs(fy) < 1e-6f) return false;
        bool down = fy < 0f;
        var bottom = MeshBase(sp.uv, sp.triangles, down, out var mr);
        if (bottom == null) return false;
        var b = sr.bounds;
        float yTop = b.max.y;
        a.ColX = new float[BaseCols];
        a.ColY = new float[BaseCols];
        var vs = new float[BaseCols];
        float vLo = float.MaxValue, vHi = float.MinValue;
        float du = 1f / mr.y;
        for (int c = 0; c < BaseCols; c++)
        {
            float u = mr.x + (c + 0.5f) / BaseCols * du;
            float v = mr.z + bottom[c] / 255f * mr.w;
            // 型の uv → 絵の中の位置 → 世界 (回していない絵だけなので軸ごと)
            float wx = m.m03 + fx * ((u - bu) / au);
            float wy = m.m13 + fy * ((v - bv) / av);
            float fl = FloorMask.FloorLine(wx, wy, Math.Max(yTop - wy, 0f), FrontFaceMax);
            a.ColX[c] = wx;
            a.ColY[c] = fl;
            vs[c] = float.NaN;
            if (float.IsNaN(fl)) continue;
            float fv = bv + av * ((fl - m.m13) / fy);
            vs[c] = fv;
            vLo = Math.Min(vLo, fv);
            vHi = Math.Max(vHi, fv);
        }
        if (vLo > vHi) return false;
        // 床の線の v の範囲は絵の v の範囲も含める (影の写しで抜く手前の絵の型も同じ範囲で uv を引く)。範囲が広がる分、線は 16 ビットで持つ
        vLo = Math.Min(vLo, mr.z);
        vHi = Math.Max(vHi, mr.z + mr.w);
        float dv = Math.Max(vHi - vLo, 1e-6f);
        a.Px = new byte[BaseCols * 4];
        float last = float.NaN;
        for (int c = 0; c < BaseCols; c++) if (!float.IsNaN(vs[c])) { last = vs[c]; break; }
        for (int c = 0; c < BaseCols; c++)
        {
            if (!float.IsNaN(vs[c])) last = vs[c];
            int q = Math.Clamp((int)MathF.Round((last - vLo) / dv * 65535f), 0, 65535);
            a.Px[c * 4] = (byte)(q >> 8);
            a.Px[c * 4 + 2] = (byte)q;
            a.Px[c * 4 + 3] = 255;
        }
        a.Range = V4(mr.x, mr.y, vLo, dv);
        a.VPerUnit = av / fy;

        var s = a.S = new Sunk();
        // 床の線は自前の 2 色のテクスチャで渡すので SunkPlate には渡さない
        SunkPlate(s, sr.transform, sp, FxMath.V3(0f, 0f, SunkFrontZ), FxMath.V3(1f, 1f, 1f), sr.flipX, sr.flipY, SpriteDrawMode.Simple, default, a.VPerUnit, vLo, null, default);
        // 端末の絵などは描く順番が上げてあり、z に関わらず順番 0 の板より手前に描かれて水を隠す
        int order = sr.sortingOrder, layer = sr.sortingLayerID;
        if (order != 0 || layer != 0) { s.Sr.sortingLayerID = layer; s.Sr.sortingOrder = order; }
        var tex = s.Base = new Texture2D(BaseCols, 1, TextureFormat.RGBA32, false) { name = "MrpSunkFront", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        FrontUpload(tex, a.Px);
        s.Block.SetTexture(BaseTexId, tex);
        // Android 版の MaterialPropertyBlock には SetVector が無いので 4 つの Float で渡す
        s.Block.SetFloat(BaseU0Id, a.Range.x);
        s.Block.SetFloat(BaseUsId, a.Range.y);
        s.Block.SetFloat(BaseV0Id, a.Range.z);
        s.Block.SetFloat(BaseVsId, a.Range.w);
        s.Block.SetFloat(BaseOnId, 1f);
        s.Block.SetFloat(LvlColsId, 1f);
        s.Block.SetFloat(LvlMaxId, FrontLvlMax);
        s.Block.SetFloat(SilCutId, FrontDrawn);
        s.Block.SetFloat(SilRId, 0f);
        s.Block.SetFloat(RiseId, 1f);
        s.Block.SetFloat(FaceHId, DoorFaceH);
        s.Block.SetFloat(CoverId, 0f);
        s.Range = a.Range;
        return true;
    }

    // 影の中の写しは全部の絵より手前に出るので、重なった板の写しは水が二重になり、視界で手前の絵に隠れる所 (頭上の重ね絵・手前の家具) にも水が出る。
    // 頭上の重ね絵と手前の家具はその形を抜く。手前の絵同士は、手前の板の写しを奥の絵の形で抜いて奥の板に描かせる: 重なった列では
    // どちらも同じ床の線 (奥の絵の下から手前の絵を抜けた床) から測るので水の線は同じで、網目の形は絵より粗いので、手前 (柵) の形で抜くと
    // 柵の隙間から見える奥 (機関) の水まで消える。動かない絵なので、作るのは最初に出した時だけ
    private static void FrontOcc(FrontArt a)
    {
        var s = a.S;
        var r = a.Box;
        float z = a.Sr.transform.position.z;
        OccSrs.Clear();
        foreach (var f in FrontArts)
        {
            if (f == a || f.Failed || f.Off) continue;
            var sr = f.Sr;
            var q = f.Box;
            if (!sr || q.z <= r.x || q.x >= r.z || q.w <= r.y || q.y >= r.w) continue;
            if (sr.transform.position.z <= z) continue;
            OccSrs.Add(sr);
        }
        foreach (var (sr, q) in FrontOver)
            if (sr && sr.enabled && q.z > r.x && q.x < r.z && q.w > r.y && q.y < r.w && sr.gameObject.activeInHierarchy) OccSrs.Add(sr);
        FrontSprites(Rect.MinMaxRect(r.x, r.y, r.z, r.w), z, OccSrs);
        SunkOccFill(s);
    }

    // 確認用 (bridge `props front [x y r]`): 範囲に掛かる手前の絵の板の状態
    private static void FrontReport(string args, Action<string> reply)
    {
        var w = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length == 1 && (w[0] == "on" || w[0] == "off")) { _frontOff = w[0] == "off"; reply($"OK props front off={_frontOff}"); return; }
        bool near = w.Length >= 3;
        float x = near ? float.Parse(w[0], System.Globalization.CultureInfo.InvariantCulture) : 0f, y = near ? float.Parse(w[1], System.Globalization.CultureInfo.InvariantCulture) : 0f;
        float r = near ? float.Parse(w[2], System.Globalization.CultureInfo.InvariantCulture) : 0f;
        int n = 0, shown = 0, failed = 0;
        float full = WaterSim.Full;
        foreach (var a in FrontArts)
        {
            n++;
            if (a.S != null && a.S.Shown) shown++;
            if (a.Failed) failed++;
            var q = a.Box;
            if (!near || q.z < x - r || q.x > x + r || q.w < y - r || q.y > y + r) continue;
            float gate = 0f;
            for (int i = 0; i < FrontGatePts; i++)
                if (!float.IsNaN(a.GateY[i])) gate = Math.Max(gate, FrontDepth(a.GateX[i], a.GateY[i], full));
            int cols = 0;
            if (a.ColY != null) foreach (var c in a.ColY) if (!float.IsNaN(c)) cols++;
            var sr = a.Sr;
            reply($"FRONT {(sr && sr.sprite ? sr.sprite.name : "?")} z={(sr ? sr.transform.position.z : 0f):0.####} box={q.x:0.##},{q.y:0.##}..{q.z:0.##},{q.w:0.##} gate={gate:0.###} cols={cols} plate={a.S != null} shown={a.S?.Shown} lvl={a.S?.Lvl:0.###} failed={a.Failed} off={a.Off} order={(sr ? sr.sortingOrder : 0)}");
        }
        reply($"OK props front arts={n} over={FrontOver.Count} shown={shown} failed={failed}");
    }

    private static unsafe void FrontUpload(Texture2D tex, byte[] px)
    {
        if (!tex) return;
        fixed (byte* p = px) tex.LoadRawTextureData((IntPtr)p, px.Length);
        tex.Apply(false, false);
    }

    private static void FrontSunkHideAll()
    {
        foreach (var a in FrontArts) SunkHide(a.S);
    }

    private static void FrontSunkClear()
    {
        foreach (var a in FrontArts)
            if (a.S != null) PlateDestroy(a.S);
        FrontArts.Clear();
        FrontOver.Clear();
        _frontGen = -1;
    }
}
