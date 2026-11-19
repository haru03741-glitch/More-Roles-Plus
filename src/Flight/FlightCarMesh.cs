using System;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MoreRolesPlus.Fx;
using UnityEngine;
using UnityEngine.Rendering;

namespace MoreRolesPlus.Flight;

// 夜の街の車の立体。カメラの周りの決まった数の「枠」に同じ車の形を並べた網目を、入る時に 1 回だけ作る。
// どの枠にどの車が来るか・どこを向いて走っているかは頂点シェーダ (FlightCarCommon) が地面の絵と同じ式で毎フレーム決めるので、
// 毎フレームの C# の仕事は無い (高く飛んで車が見分けられない間は描くのをやめる・渋滞の枠の割り当ては出来事の時だけ)。
// 枠: 近く = 道 2 本 × (細い道 4 + 広い通り 12 車線) × 升 8・遠く = 道 3 本 × (6 + 12) × 升 16・渋滞 = 8 × 19 (軸ごと)
internal static unsafe class FlightCarMesh
{
    private const int NearLanes = 16, NearCells = 8, FarLanes = 18, FarCells = 16, Jams = 8, JamCells = 19, JamFine = 3;
    private const int Stride = 36, GlowStride = 32;
    // 高い所からは車が画素より小さいので描かない (行き来でちらつかないよう幅を持たせる)
    private const float HideAbove = 200f, ShowBelow = 180f;

    private struct Lod { public int Vn, In; public byte[] V; public ushort[] I; }

    private static Lod[] _lods;
    private static float[] _lamps = Array.Empty<float>(); // 灯ごとに 7 個 (位置・ミニバンへの差・種類)
    private static bool _loadFailed;
    private static readonly System.Collections.Generic.List<Mesh> Meshes = new();
    private static readonly System.Collections.Generic.List<GameObject> Objects = new();
    private static bool _shown;

    internal static bool Active { get; private set; }

    private static bool Load()
    {
        if (_lods != null) return true;
        if (_loadFailed) return false;
        try
        {
            using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MoreRolesPlus.Resources.Flight.citycar.mrpc");
            if (s == null) { Plugin.Logger.LogError("flight car model not found"); _loadFailed = true; return false; }
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            byte[] d = ms.ToArray();
            int o = 0;
            int I() { int v = BitConverter.ToInt32(d, o); o += 4; return v; }
            float F() { float v = BitConverter.ToSingle(d, o); o += 4; return v; }
            if (I() != 0x4350524D || I() != 1) { Plugin.Logger.LogError("flight car model bad header"); _loadFailed = true; return false; }
            int n = I();
            if (n != 2) { Plugin.Logger.LogError("flight car model bad lods"); _loadFailed = true; return false; }
            var lods = new Lod[n];
            for (int k = 0; k < n; k++)
            {
                int vn = I(), inN = I();
                if (vn <= 0 || vn > 4096 || inN <= 0 || inN % 3 != 0) { Plugin.Logger.LogError("flight car model bad counts"); _loadFailed = true; return false; }
                var v = new byte[vn * 40];
                Buffer.BlockCopy(d, o, v, 0, v.Length);
                o += v.Length;
                var idx = new ushort[inN];
                for (int i = 0; i < inN; i++)
                {
                    idx[i] = BitConverter.ToUInt16(d, o + i * 2);
                    if (idx[i] >= vn) { Plugin.Logger.LogError("flight car model bad index"); _loadFailed = true; return false; }
                }
                o += (inN * 2 + 3) & ~3;
                lods[k] = new Lod { Vn = vn, In = inN, V = v, I = idx };
            }
            int ln = I();
            var lamps = new float[ln * 7];
            for (int i = 0; i < ln; i++)
            {
                for (int c = 0; c < 6; c++) lamps[i * 7 + c] = F();
                lamps[i * 7 + 6] = I();
            }
            _lods = lods;
            _lamps = lamps;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"flight car model: {e}");
            _loadFailed = true;
            return false;
        }
    }

    // 枠の番号 (シェーダの PlaceCar と同じ並び)
    private static float Entry(int kind, int axis, int lane, int cell) => kind * 4096 + axis * 2048 + lane * 64 + cell;

    internal static void Build(Transform root, int layer)
    {
        Clear();
        if (!MrpBundle.FlightMaterials[7] || !MrpBundle.FlightMaterials[8] || !Load()) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var list = new System.Collections.Generic.List<(float E, int Lod)>();
        // 近く (細かい形) は軸ごとに 1 枚 (16 ビットの添字に収める)
        for (int axis = 0; axis < 2; axis++)
        {
            list.Clear();
            for (int l = 0; l < NearLanes; l++)
                for (int c = 0; c < NearCells; c++) list.Add((Entry(0, axis, l, c), 0));
            Make(root, layer, "MrpFlightCarsNear" + axis, list);
        }
        list.Clear();
        for (int axis = 0; axis < 2; axis++)
            for (int l = 0; l < FarLanes; l++)
                for (int c = 0; c < FarCells; c++) list.Add((Entry(1, axis, l, c), 1));
        Make(root, layer, "MrpFlightCarsFar", list);
        list.Clear();
        for (int j = 0; j < Jams; j++)
            for (int c = 0; c < JamCells; c++)
            {
                if (j < JamFine) list.Add((Entry(2, 0, j, c), 0));
                list.Add((Entry(2, 0, j, c), 1));
            }
        Make(root, layer, "MrpFlightCarsJam", list);
        MakeGlow(root, layer);
        _shown = true;
        Active = true;
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Plugin.Logger.LogInfo($"flight cars: meshes={Meshes.Count} {ms:0.0}ms");
    }

    // 枠ごとに形を 1 つ写した網目。頂点 = 位置 f32x3・法線 + 部品 sn8x4・ミニバンへの差 + 枠の番号 f32x4・ミニバンの法線 + 細かさ sn8x4
    private static void Make(Transform root, int layer, string name, System.Collections.Generic.List<(float E, int Lod)> entries)
    {
        int vn = 0, inN = 0;
        foreach (var (_, lod) in entries) { vn += _lods[lod].Vn; inN += _lods[lod].In; }
        if (vn > 65535) { Plugin.Logger.LogError($"flight cars {name} too big: {vn}"); return; }
        var vb = new byte[vn * Stride];
        var ib = new int[inN];
        int vo = 0, io = 0;
        fixed (byte* dst0 = vb)
        {
            foreach (var (e, lod) in entries)
            {
                var src = _lods[lod];
                fixed (byte* s0 = src.V)
                {
                    for (int i = 0; i < src.Vn; i++)
                    {
                        byte* s = s0 + i * 40;
                        byte* d = dst0 + (vo + i) * Stride;
                        // 位置
                        *(float*)d = *(float*)s;
                        *(float*)(d + 4) = *(float*)(s + 4);
                        *(float*)(d + 8) = *(float*)(s + 8);
                        // 法線 + 部品
                        *(uint*)(d + 12) = *(uint*)(s + 24);
                        // ミニバンへの差 + 枠の番号
                        *(float*)(d + 16) = *(float*)(s + 12);
                        *(float*)(d + 20) = *(float*)(s + 16);
                        *(float*)(d + 24) = *(float*)(s + 20);
                        *(float*)(d + 28) = e;
                        // ミニバンの法線 + 細かさ (sn8 の 127 = 1)
                        *(uint*)(d + 32) = *(uint*)(s + 28);
                        d[35] = (byte)(lod == 0 ? 0 : 127);
                    }
                }
                for (int i = 0; i < src.In; i++) ib[io + i] = src.I[i] + vo;
                vo += src.Vn;
                io += src.In;
            }
        }
        var attrs = new Il2CppStructArray<VertexAttributeDescriptor>((long)4);
        attrs[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0);
        attrs[1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4, 0);
        attrs[2] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4, 0);
        attrs[3] = new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.SNorm8, 4, 0);
        Finish(root, layer, name, vn, attrs, vb, Stride, ib, MrpBundle.FlightMaterials[7]);
    }

    // 灯の光の板: 遠くの枠 + 渋滞の枠ごとに灯 4 つ × 4 頂点。頂点 = 灯の位置 f32x3・角 xy + 種類 sn8x4・ミニバンへの差 + 枠の番号 f32x4
    private static void MakeGlow(Transform root, int layer)
    {
        var es = new System.Collections.Generic.List<float>();
        for (int axis = 0; axis < 2; axis++)
            for (int l = 0; l < FarLanes; l++)
                for (int c = 0; c < FarCells; c++) es.Add(Entry(1, axis, l, c));
        for (int j = 0; j < Jams; j++)
            for (int c = 0; c < JamCells; c++) es.Add(Entry(2, 0, j, c));
        int lamps = _lamps.Length / 7;
        int vn = es.Count * lamps * 4;
        if (vn > 65535) { Plugin.Logger.LogError($"flight car glow too big: {vn}"); return; }
        var vb = new byte[vn * GlowStride];
        var ib = new int[es.Count * lamps * 6];
        int vo = 0, io = 0;
        fixed (byte* d0 = vb)
        {
            foreach (float e in es)
                for (int l = 0; l < lamps; l++)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        byte* d = d0 + (vo + k) * GlowStride;
                        *(float*)d = _lamps[l * 7];
                        *(float*)(d + 4) = _lamps[l * 7 + 1];
                        *(float*)(d + 8) = _lamps[l * 7 + 2];
                        d[12] = (byte)(sbyte)(k == 0 || k == 3 ? -127 : 127);
                        d[13] = (byte)(sbyte)(k < 2 ? -127 : 127);
                        d[14] = (byte)(_lamps[l * 7 + 6] > 0.5f ? 127 : 0);
                        d[15] = 0;
                        *(float*)(d + 16) = _lamps[l * 7 + 3];
                        *(float*)(d + 20) = _lamps[l * 7 + 4];
                        *(float*)(d + 24) = _lamps[l * 7 + 5];
                        *(float*)(d + 28) = e;
                    }
                    ib[io] = vo; ib[io + 1] = vo + 1; ib[io + 2] = vo + 2;
                    ib[io + 3] = vo; ib[io + 4] = vo + 2; ib[io + 5] = vo + 3;
                    vo += 4;
                    io += 6;
                }
        }
        var attrs = new Il2CppStructArray<VertexAttributeDescriptor>((long)3);
        attrs[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0);
        attrs[1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4, 0);
        attrs[2] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4, 0);
        Finish(root, layer, "MrpFlightCarGlow", vn, attrs, vb, GlowStride, ib, MrpBundle.FlightMaterials[8]);
    }

    private static void Finish(Transform root, int layer, string name, int vn, Il2CppStructArray<VertexAttributeDescriptor> attrs, byte[] vb, int stride, int[] ib, Material mat)
    {
        var mesh = new Mesh { name = name };
        mesh.SetVertexBufferParamsFromArray(vn, attrs);
        fixed (byte* p = vb)
            mesh.InternalSetVertexBufferData(0, (IntPtr)p, 0, 0, vn, stride, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        mesh.triangles = ib;
        // 頂点はシェーダがカメラの周りへ置くので、切り捨てられないよう街全体を覆う範囲にする
        mesh.bounds = new Bounds(new Vector3(0f, FlightCity.Ground, 0f), new Vector3(20000f, 400f, 20000f));
        Meshes.Add(mesh);
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(root, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        Objects.Add(go);
    }

    // 毎フレーム (街の場面の間だけ): 高さが閾を越えた時だけ描くのを切り替える
    internal static void Tick(float shipHeight)
    {
        if (!Active) return;
        bool show = _shown ? shipHeight < HideAbove : shipHeight < ShowBelow;
        if (show == _shown) return;
        _shown = show;
        foreach (var go in Objects) if (go) go.SetActive(show);
    }

    internal static void Clear()
    {
        foreach (var m in Meshes) if (m) UnityEngine.Object.Destroy(m);
        Meshes.Clear();
        Objects.Clear();
        Active = false;
    }
}
