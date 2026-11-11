using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using MoreRolesPlus.Bridge;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 確かめ用: クルーより常に奥にある絵 (部屋の絵・床の上の物・壁の飾り) を、今の場面のまま 1 枚に重ねて読み戻す。
// 床の画素を決める材料 (色と不透明度) を、絵のファイルを開かずに試合中に取れるか・どのくらい時間がかかるかを見る。
internal static class FloorCapture
{
    private const int Layer = 31;       // 本編が使っていない層 (描く間だけ)
    private const int ChunkMax = 2048;  // 1 回に描く板の辺の上限
    private const float SkyZ = 20f;
    private const float BandZ = 0.1f;   // |z| がこれ未満の絵はクルーと同じ奥行き (前後が入れ替わる)
    private const float Far = 7000f;    // 写しを置く場所 (層 31 の他の物が写らないよう船から遠くへ)

    internal static void Register()
    {
        TestBridge.Register("floorcapture", "[ppu] [time] [chunk <画素>] 奥の絵 (部屋の絵と、クルーより常に奥の物) を今の場面のまま重ねて読み戻し、色と不透明度を Screens/floorcap_<船>.bin へ書く (かかった時間を返す)・time = 描いて読み戻すだけ (書き出さない)・chunk = 1 回に描く板の辺", (args, reply) =>
        {
            var ship = ShipStatus.Instance;
            if (!ship || !DamageMap.Ready() || !SolidMap.Ensure()) { reply("ERR floorcapture not ready"); return; }
            float ppu = FloorMask.Ppu;
            bool timeOnly = false;
            int chunk = ChunkMax;
            var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == "time") timeOnly = true;
                else if (a[i] == "chunk" && i + 1 < a.Length && int.TryParse(a[i + 1], out int c)) { chunk = Math.Clamp(c, 64, ChunkMax); i++; }
                else if (float.TryParse(a[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float p)) ppu = p;
            }
            if (!(ppu > 1f)) { reply("ERR floorcapture ppu (床マスクが無い船は ppu を渡す)"); return; }
            try { Run(ship, ppu, timeOnly, chunk, reply); }
            catch (Exception e) { reply($"ERR floorcapture {e.GetType().Name} {e.Message}"); }
        });
    }

    private static void Run(ShipStatus ship, float ppu, bool timeOnly, int ChunkPx, Action<string> reply)
    {
        long t0 = Stopwatch.GetTimestamp();
        string shipName = ship.name.Replace("(Clone)", "");
        float ox = SolidMap.Origin.x, oy = SolidMap.Origin.y;
        int w = (int)MathF.Ceiling(SolidMap.W / SolidMap.Ppu * ppu), h = (int)MathF.Ceiling(SolidMap.H / SolidMap.Ppu * ppu);

        // 重ねる絵: 部屋の絵のうちクルーの帯より奥の物と名前で床に入れる物・床の上に置かれた絵のうち帯より奥の物
        var srcs = new List<SpriteRenderer>();
        var roomIds = new HashSet<int>();
        var arts = DamageMap.RoomArts;
        for (int i = 0; i < arts.Count; i++)
        {
            var sr = arts[i];
            if (!sr || !sr.sprite) continue;
            roomIds.Add(sr.GetInstanceID());
            float z = sr.transform.position.z;
            if (z >= SkyZ || (z < BandZ && !FloorMask.AlwaysFloor(shipName, sr.sprite.name))) continue;
            srcs.Add(sr);
        }
        int rooms = srcs.Count;
        foreach (var sr in ship.GetComponentsInChildren<SpriteRenderer>(false))
        {
            if (!FloorMask.CoverArt(sr, shipName, roomIds)) continue;
            if (sr.transform.position.z < BandZ && !FloorMask.AlwaysFloor(shipName, sr.sprite.name)) continue;
            srcs.Add(sr);
        }

        var gos = new List<GameObject>(srcs.Count);
        byte[] buf = null;
        FileStream fs = null;
        string path = Path.Combine(TestBridge.ScreensDir, $"floorcap_{shipName}.bin");
        double readMax = 0;
        bool fast = true;
        GameObject camGo = null;
        Texture2D tex = null;
        var prev = RenderTexture.active;
        double msRender = 0, msRead = 0, msCopy = 0;
        int chunks = 0;
        try
        {
            foreach (var sr in srcs)
            {
                var st = sr.transform;
                var go = new GameObject("MrpFloorCap") { layer = Layer };
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
            double msBuild = Ms(t0);

            camGo = new GameObject("MrpFloorCapCam");
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

            for (int cy = 0; cy < h; cy += ChunkPx)
                for (int cx = 0; cx < w; cx += ChunkPx)
                {
                    int cw = Math.Min(ChunkPx, w - cx), chh = Math.Min(ChunkPx, h - cy);
                    long t1 = Stopwatch.GetTimestamp();
                    var rt = RenderTexture.GetTemporary(cw, chh, 0, RenderTextureFormat.ARGB32);
                    cam.orthographicSize = chh / ppu * 0.5f;
                    cam.aspect = cw / (float)chh;
                    camGo.transform.position = FxMath.V3(ox + Far + (cx + cw * 0.5f) / ppu, oy + Far + (cy + chh * 0.5f) / ppu, -30f);
                    cam.targetTexture = rt;
                    cam.Render();
                    cam.targetTexture = null;
                    msRender += Ms(t1);
                    t1 = Stopwatch.GetTimestamp();
                    RenderTexture.active = rt;
                    if (!tex || tex.width != cw || tex.height != chh)
                    {
                        if (tex) UnityEngine.Object.Destroy(tex);
                        tex = new Texture2D(cw, chh, TextureFormat.RGBA32, false, true);
                    }
                    tex.ReadPixels(new Rect(0, 0, cw, chh), 0, 0, false);
                    var raw = tex.GetRawTextureData();
                    double one = Ms(t1);
                    msRead += one;
                    readMax = Math.Max(readMax, one);
                    t1 = Stopwatch.GetTimestamp();
                    if (!timeOnly)
                    {
                        int n = cw * chh * 4;
                        if (buf == null || buf.Length < n) buf = new byte[n];
                        // 配列の中身をまとめて写す。配列の頭の大きさが思った通りかを先に数点で確かめ、違えば 1 個ずつ読む
                        IntPtr data = raw.Pointer + 4 * IntPtr.Size;
                        for (int k = 0; fast && k < 64; k++)
                        {
                            int at = (int)((long)k * (n - 1) / 63);
                            if (System.Runtime.InteropServices.Marshal.ReadByte(data, at) != raw[at]) fast = false;
                        }
                        if (fast) System.Runtime.InteropServices.Marshal.Copy(data, buf, 0, n);
                        else for (int k = 0; k < n; k++) buf[k] = raw[k];
                        if (fs == null)
                        {
                            fs = new FileStream(path, FileMode.Create);
                            fs.Write(new[] { (byte)'M', (byte)'R', (byte)'P', (byte)'D' });
                            fs.Write(BitConverter.GetBytes(w)); fs.Write(BitConverter.GetBytes(h));
                            fs.Write(BitConverter.GetBytes(ox)); fs.Write(BitConverter.GetBytes(oy)); fs.Write(BitConverter.GetBytes(ppu));
                        }
                        fs.Write(BitConverter.GetBytes(cx)); fs.Write(BitConverter.GetBytes(cy)); fs.Write(BitConverter.GetBytes(cw)); fs.Write(BitConverter.GetBytes(chh));
                        fs.Write(buf, 0, n);
                    }
                    msCopy += Ms(t1);
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                    chunks++;
                }

            reply($"OK floorcapture {shipName} {w}x{h} ppu={ppu:0.###} arts={srcs.Count} (rooms={rooms}) chunk={ChunkPx} chunks={chunks} build={msBuild:F1}ms render={msRender:F1}ms read={msRead:F1}ms (1 枚の最大 {readMax:F1}ms) copy+write={msCopy:F1}ms fast={fast} total={Ms(t0):F1}ms{(timeOnly ? "" : " -> " + path)}");
        }
        finally
        {
            RenderTexture.active = prev;
            fs?.Dispose();
            if (tex) UnityEngine.Object.Destroy(tex);
            if (camGo) UnityEngine.Object.Destroy(camGo);
            foreach (var go in gos) if (go) UnityEngine.Object.Destroy(go);
        }
    }

    private static double Ms(long from) => (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;
}
