using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Native;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 確かめ用: 水の床の地図 (床・壁の面) を、配る地図を使わずに今の場面から作る。
// 入力 = 床の層の絵 (部屋の絵と床に置かれた平たい物) の取り込み・水より手前の絵の取り込み・歩ける所と水の届く升・家具の当たり判定。
// 決める手順は C++ (mrp_floor_decide)。取り込みは板ごとにその場でビットへ落とす (マップ全体の色を持たない)
internal static unsafe class FloorLive
{
    private const int Layer = 31;       // 本編が使っていない層 (描く間だけ)
    private const int ChunkMax = 2048;  // 1 回に描く板の辺の上限
    private const float SkyZ = 20f;
    private const float BandZ = 0.1f;   // これより手前の絵はどの床の水より手前
    private const float Far = 7000f;    // 写しを置く場所 (層 31 の他の物が写らないよう船から遠くへ)

    internal static void Register()
    {
        TestBridge.Register("floorlive", "[ppu] [save] [apply] 水の床の地図を今の場面から作る (かかった時間を返す)・save = 入力と答えを Screens/floorlive_<船>.bin へ・apply = 作った地図を床マスクに使う (floormask load none で配る地図へ戻す)", (args, reply) =>
        {
            var ship = ShipStatus.Instance;
            if (!ship || !DamageMap.Ready() || !SolidMap.Ensure()) { reply("ERR floorlive not ready"); return; }
            if (MrpNative.FloorDecide == null) { reply("ERR floorlive native " + MrpNative.Status); return; }
            string shipName = ship.name.Replace("(Clone)", "");
            if (shipName == "FungleShip") { reply("ERR floorlive FungleShip (網目の地面と崖はこの決め方の外)"); return; }
            double ppu = FloorMask.Ppu;
            bool save = false, apply = false;
            foreach (var a in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (a == "save") save = true;
                else if (a == "apply") apply = true;
                else if (double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double p)) ppu = p;
            }
            if (!(ppu > 1.0)) { reply("ERR floorlive ppu (床マスクが無い船は ppu を渡す)"); return; }
            try { Run(ship, shipName, ppu, save, apply, reply); }
            catch (Exception e) { reply($"ERR floorlive {e.GetType().Name} {e.Message}"); }
        });
    }

    private static unsafe void Run(ShipStatus ship, string shipName, double ppu, bool save, bool apply, Action<string> reply)
    {
        long t0 = Stopwatch.GetTimestamp();
        float ox = SolidMap.Origin.x, oy = SolidMap.Origin.y;
        int w = (int)Math.Ceiling(SolidMap.W / (double)SolidMap.Ppu * ppu), h = (int)Math.Ceiling(SolidMap.H / (double)SolidMap.Ppu * ppu);
        int nb = (int)(((long)w * h + 7) / 8);
        var drawn = new byte[nb];
        var dark = new byte[nb];
        var walk = new byte[nb];
        var furn = new byte[nb];
        var stand = new byte[nb];
        var front = new byte[nb];
        var floor = new byte[nb];
        var face = new byte[nb];

        // 床の層: 部屋の絵のうち床の奥行きより奥の物と、名前で床に入れる物 (床に置かれた平たい物を含む)
        FloorCovers.Ensure();
        float back = FloorMask.BackZ(shipName);
        var rooms = new List<SpriteRenderer>();
        var fronts = new List<SpriteRenderer>();
        var roomIds = new HashSet<int>();
        var added = new HashSet<int>();
        var arts = DamageMap.RoomArts;
        for (int i = 0; i < arts.Count; i++)
        {
            var sr = arts[i];
            if (!sr) continue;
            roomIds.Add(sr.GetInstanceID());
            if (!sr.sprite) continue;
            float z = sr.transform.position.z;
            if (z >= SkyZ || (z < back && !FloorMask.AlwaysFloor(shipName, sr.sprite.name))) continue;
            rooms.Add(sr);
            added.Add(sr.GetInstanceID());
        }
        int nRooms = rooms.Count;
        // 床の上に置かれた絵: 床に入れる物は床の層へ・残りのうち水より手前に描かれる物は床から抜く
        var skip = PropSim.FrontSkip();
        foreach (var sr in ship.GetComponentsInChildren<SpriteRenderer>(false))
        {
            if (!FloorMask.CoverArt(sr, shipName, roomIds)) continue;
            string name = sr.sprite.name;
            float z = sr.transform.position.z;
            if (FloorCovers.Is(shipName, name)) { if (z < SkyZ && added.Add(sr.GetInstanceID())) rooms.Add(sr); continue; }
            if (name is "room_weapon" or "room_broadcast") continue;
            if (z < BandZ || PropSim.FrontCover(sr, shipName, roomIds, skip)) fronts.Add(sr);
        }
        double msPick = Ms(t0);

        long t1 = Stopwatch.GetTimestamp();
        fixed (byte* pd = drawn, pk = dark, pf = front)
        {
            Capture(rooms, ox, oy, ppu, w, h, pd, pk);
            Capture(fronts, ox, oy, ppu, w, h, pf, null);
        }
        double msCapture = Ms(t1);

        // 歩ける所: 歩ける升と水の届く升を画素へ引く
        t1 = Stopwatch.GetTimestamp();
        int cells = SolidMap.W * SolidMap.H;
        var cellBits = new byte[(cells + 7) / 8];
        var open = SolidMap.OpenCells;
        var wet = WaterSim.WetCells;
        if (wet != null && wet.Length != cells) wet = null;
        for (int k = 0; k < cells; k++)
            if (open[k] != 0 || (wet != null && wet[k] != 0)) cellBits[k >> 3] |= (byte)(1 << (k & 7));
        fixed (byte* pc = cellBits, pw = walk)
            MrpNative.FloorGridBits(pc, SolidMap.W, SolidMap.H, SolidMap.Ppu, w, h, ppu, pw);

        // 家具: 当たり判定の線分の組 (a, b, a, b, ...) を順につないだ輪郭を塗る
        var cols = new List<Collider2D>();
        var segs = new List<Vector2>();
        DamageMap.FurnitureColliders(new Vector2(ox + SolidMap.W / SolidMap.Ppu * 0.5f, oy + SolidMap.H / SolidMap.Ppu * 0.5f),
            Math.Max(SolidMap.W, SolidMap.H) / SolidMap.Ppu, cols);
        var pts = new float[64];
        fixed (byte* pfu = furn)
            for (int i = 0; i < cols.Count; i++)
            {
                segs.Clear();
                SolidMap.Segments(cols[i], segs);
                int n = (segs.Count + 1) / 2;
                if (n < 3) continue;
                if (pts.Length < n * 2) pts = new float[n * 4];
                for (int k = 0; k < n; k++)
                {
                    var v = segs[k * 2];
                    pts[k * 2] = (float)((v.x - ox) * ppu);
                    pts[k * 2 + 1] = (float)((v.y - oy) * ppu);
                }
                fixed (float* pp = pts) MrpNative.FloorFillPoly(pfu, w, h, pp, n);
            }
        double msGrid = Ms(t1);

        t1 = Stopwatch.GetTimestamp();
        int r;
        fixed (byte* pd = drawn, pk = dark, pw = walk, pfu = furn, ps = stand, pf = front, po = floor, pe = face)
            r = MrpNative.FloorDecide(w, h, ppu, pd, pk, pw, pfu, ps, pf, po, pe);
        double msDecide = Ms(t1);
        if (r != 0) { reply($"ERR floorlive decide {r}"); return; }

        string saved = "";
        if (save)
        {
            string path = Path.Combine(TestBridge.ScreensDir, $"floorlive_{shipName}.bin");
            using (var fs = new FileStream(path, FileMode.Create))
            {
                fs.Write(new[] { (byte)'M', (byte)'R', (byte)'P', (byte)'L' });
                fs.Write(BitConverter.GetBytes(w)); fs.Write(BitConverter.GetBytes(h));
                fs.Write(BitConverter.GetBytes(ox)); fs.Write(BitConverter.GetBytes(oy)); fs.Write(BitConverter.GetBytes(ppu));
                foreach (var b in new[] { drawn, dark, walk, furn, stand, front, floor, face }) fs.Write(b);
            }
            saved = " -> " + path;
        }
        if (apply)
        {
            FloorMask.Install(floor, face, w, h, ox, oy, (float)ppu);
            WaterArt.FloorMaskToggled();
        }
        reply($"OK floorlive {shipName} {w}x{h} ppu={ppu:0.####} rooms={nRooms}+{rooms.Count - nRooms} fronts={fronts.Count} furniture={cols.Count} wet={(wet != null ? "on" : "none")} pick={msPick:F1}ms capture={msCapture:F1}ms grid+furn={msGrid:F1}ms decide={msDecide:F1}ms total={Ms(t0):F1}ms{(apply ? " applied" : "")}{saved}");
    }

    // 絵を層 31 の写しにして使い捨てのカメラで板ごとに描き、読み戻した画素をその場でビットへ落とす
    private static unsafe void Capture(List<SpriteRenderer> srcs, float ox, float oy, double ppu, int w, int h, byte* drawn, byte* dark)
    {
        if (srcs.Count == 0) return;
        var gos = new List<GameObject>(srcs.Count);
        GameObject camGo = null;
        Texture2D tex = null;
        var prev = RenderTexture.active;
        try
        {
            foreach (var sr in srcs)
            {
                var st = sr.transform;
                var go = new GameObject("MrpFloorLive") { layer = Layer };
                var pos = st.position;
                go.transform.position = FxMath.V3(pos.x + Far, pos.y + Far, pos.z);
                go.transform.rotation = st.rotation;
                go.transform.localScale = st.lossyScale;
                var r = go.AddComponent<SpriteRenderer>();
                // 材質は既定のまま (本編の材質は型抜きの条件で絵の一部しか描かない物がある)
                r.sprite = sr.sprite;
                r.color = sr.color;
                r.flipX = sr.flipX;
                r.flipY = sr.flipY;
                r.sortingLayerID = sr.sortingLayerID;
                r.sortingOrder = sr.sortingOrder;
                if (sr.drawMode != SpriteDrawMode.Simple) { r.drawMode = sr.drawMode; r.size = sr.size; }
                gos.Add(go);
            }
            camGo = new GameObject("MrpFloorLiveCam");
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.cullingMask = 1 << Layer;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 60f;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            bool fast = true;
            byte[] buf = null;
            for (int cy = 0; cy < h; cy += ChunkMax)
                for (int cx = 0; cx < w; cx += ChunkMax)
                {
                    int cw = Math.Min(ChunkMax, w - cx), chh = Math.Min(ChunkMax, h - cy);
                    var rt = RenderTexture.GetTemporary(cw, chh, 0, RenderTextureFormat.ARGB32);
                    cam.orthographicSize = (float)(chh / ppu * 0.5);
                    cam.aspect = cw / (float)chh;
                    camGo.transform.position = FxMath.V3((float)(ox + Far + (cx + cw * 0.5) / ppu), (float)(oy + Far + (cy + chh * 0.5) / ppu), -30f);
                    cam.targetTexture = rt;
                    cam.Render();
                    cam.targetTexture = null;
                    RenderTexture.active = rt;
                    if (!tex || tex.width != cw || tex.height != chh)
                    {
                        if (tex) UnityEngine.Object.Destroy(tex);
                        tex = new Texture2D(cw, chh, TextureFormat.RGBA32, false, true);
                    }
                    tex.ReadPixels(new Rect(0, 0, cw, chh), 0, 0, false);
                    var raw = tex.GetRawTextureData();
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                    int n = cw * chh * 4;
                    // 配列の中身を直接渡す。配列の頭の大きさが思った通りかを先に数点で確かめ、違えば写してから渡す
                    IntPtr data = raw.Pointer + 4 * IntPtr.Size;
                    for (int k = 0; fast && k < 64; k++)
                    {
                        int at = (int)((long)k * (n - 1) / 63);
                        if (Marshal.ReadByte(data, at) != raw[at]) fast = false;
                    }
                    if (fast) MrpNative.FloorChunkBits((byte*)data, cw, chh, cx, cy, w, h, drawn, dark);
                    else
                    {
                        if (buf == null || buf.Length < n) buf = new byte[n];
                        for (int k = 0; k < n; k++) buf[k] = raw[k];
                        fixed (byte* pb = buf) MrpNative.FloorChunkBits(pb, cw, chh, cx, cy, w, h, drawn, dark);
                    }
                }
        }
        finally
        {
            RenderTexture.active = prev;
            if (tex) UnityEngine.Object.Destroy(tex);
            if (camGo) UnityEngine.Object.Destroy(camGo);
            // Destroy はフレームの終わりまで残るので、続けて描く次の取り込みに写らないよう先に隠す
            foreach (var go in gos) if (go) { go.SetActive(false); UnityEngine.Object.Destroy(go); }
        }
    }

    private static double Ms(long from) => (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;
}
