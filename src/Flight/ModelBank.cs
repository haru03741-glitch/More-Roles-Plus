using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace MoreRolesPlus.Flight;

// 3D の艦隊戦の船の形 (.mrpm・tools/make-flight-models.py が書く) を読み、Mesh と当たりの球・印にする。
// 頂点は配列に詰め替えずに、ファイルの並びのまま頂点バッファへ 1 回で書く (Mesh.vertices などの配列の受け渡しを通らない)。
// 中身が同じ頂点は書き出しの時に 1 つにまとめてあり、面は 16 ビットの添字で引く。
// 頂点 (28 バイト) = 位置 f32x3・陰の法線 i8x3 + 材質の番号 i8 (模様と照りはシェーダが番号から決める)・色 u8x4・縁取りを押し出す向き i8x3 (長さ = 太さの割合) + 動く部品の番号 i8 (TEXCOORD1・0 = 船体・k = 部品 k-1)
// ・模様の座標 i16x2 (TEXCOORD2・丸い肩のように投影では模様が伸びたりずれたりする面だけが使う)
internal static unsafe class ModelBank
{
    internal const int Stride = 28;

    internal sealed class Model
    {
        public Mesh Mesh;
        public FlightNative.Sphere[] Spheres;
        public FlightNative.Marker[] Markers;
        public FlightNative.Part[] Parts;
        public int[] MarkerPart;
        public Vector3 Min, Max;
    }

    internal static readonly string[] Names = { "skeld", "battleship_dev" };
    private static readonly Model[] Loaded = new Model[Names.Length];
    // 中身が壊れていて読めなかった形は、呼ばれるたびに読み直さない
    private static readonly bool[] Failed = new bool[Names.Length];

    internal static Model Get(int id)
    {
        if (id < 0 || id >= Names.Length || Failed[id]) return null;
        if (Loaded[id] != null && Loaded[id].Mesh) return Loaded[id];
        Loaded[id] = Load(Names[id]);
        if (Loaded[id] == null) Failed[id] = true;
        return Loaded[id];
    }

    private static Model Load(string name)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        byte[] data;
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"MoreRolesPlus.Resources.Flight.{name}.mrpm"))
        {
            if (s == null) { Plugin.Logger.LogError($"flight model not found: {name}"); return null; }
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            data = ms.ToArray();
        }
        int o = 0;
        uint Magic() { uint v = BitConverter.ToUInt32(data, o); o += 4; return v; }
        int I() { int v = BitConverter.ToInt32(data, o); o += 4; return v; }
        if (Magic() != 0x4D50524D || I() != 7) { Plugin.Logger.LogError($"flight model bad header: {name}"); return null; }
        int vn = I(), inN = I();
        float F() { float v = BitConverter.ToSingle(data, o); o += 4; return v; }
        // 範囲 (Mesh の bounds と当たりの粗い判定の確かめ用) は書き出しの時に測ってある
        Vector3 mn = new(F(), F(), F()), mx = new(F(), F(), F());
        int vOff = o;
        o += vn * Stride;
        int iOff = o;
        // 添字は 16 ビット・全体で 4 バイトの倍数に埋めてある
        o += (inN * 2 + 3) & ~3;
        if (vn <= 0 || vn > 65535 || inN <= 0 || inN % 3 != 0 || o + 4 > data.Length) { Plugin.Logger.LogError($"flight model bad counts: {name}"); return null; }
        var m = new Model();
        fixed (byte* p = data)
        {
            // 部品の数は頂点の後ろにあるので先に読む (頂点の部品番号がそれを超えるとシェーダが配列の外を引く)
            int partsAt = o;
            partsAt += 4 + BitConverter.ToInt32(data, partsAt) * 20;
            partsAt += 4 + BitConverter.ToInt32(data, partsAt) * 32;
            int partN = BitConverter.ToInt32(data, partsAt);
            // 中身の検査は Mesh を作る前に全部済ませる (作った後で止めると Mesh が残る)
            if (partN < 0 || partN > FlightNative.MaxParts) { Plugin.Logger.LogError($"flight model {name}: too many parts {partN}"); return null; }
            for (int i = 0; i < vn; i++)
            {
                int part = ((sbyte*)(p + vOff + i * Stride))[23];
                if (part < 0 || part > partN) { Plugin.Logger.LogError($"flight model {name}: vertex part {part} > {partN}"); return null; }
            }
            var tri = new int[inN];
            ushort* ix = (ushort*)(p + iOff);
            for (int i = 0; i < inN; i++)
            {
                if (ix[i] >= vn) { Plugin.Logger.LogError($"flight model {name}: index {ix[i]} >= {vn}"); return null; }
                tri[i] = ix[i];
            }
            var mesh = new Mesh { name = "MrpFlight_" + name };
            mesh.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            var attrs = new Il2CppStructArray<VertexAttributeDescriptor>((long)5);
            attrs[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0);
            attrs[1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4, 0);
            attrs[2] = new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 0);
            attrs[3] = new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.SNorm8, 4, 0);
            attrs[4] = new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.SNorm16, 2, 0);
            mesh.SetVertexBufferParamsFromArray(vn, attrs);
            mesh.InternalSetVertexBufferData(0, (IntPtr)(p + vOff), 0, 0, vn, Stride, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            // 添字は一度だけなので Mesh.triangles で渡す (添字バッファの直書きと SetSubMesh は削られた本編に無いことがある)
            mesh.triangles = tri;
            // 動く部品 (開く翼・回る板) が外へ出ても消えないよう、範囲を広めに取る
            mesh.bounds = new Bounds((mn + mx) * 0.5f, (mx - mn) * 1.4f);
            m.Mesh = mesh;
        }
        int sn = I();
        m.Spheres = new FlightNative.Sphere[sn];
        for (int i = 0; i < sn; i++)
        {
            m.Spheres[i] = MemoryMarshal.Read<FlightNative.Sphere>(data.AsSpan(o));
            o += 20;
        }
        int kn = I();
        m.Markers = new FlightNative.Marker[kn];
        for (int i = 0; i < kn; i++)
        {
            m.Markers[i] = MemoryMarshal.Read<FlightNative.Marker>(data.AsSpan(o));
            o += 32;
        }
        int pn = I();
        m.Parts = new FlightNative.Part[pn];
        for (int i = 0; i < pn; i++)
        {
            m.Parts[i] = MemoryMarshal.Read<FlightNative.Part>(data.AsSpan(o));
            o += sizeof(FlightNative.Part);
        }
        m.MarkerPart = new int[kn];
        for (int i = 0; i < kn; i++)
        {
            int mp = I();
            m.MarkerPart[i] = mp >= 0 && mp < pn ? mp : -1;
        }
        m.Min = mn; m.Max = mx;
        double loadMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Plugin.Logger.LogInfo($"flight model {name}: verts={vn} tris={inN / 3} spheres={sn} markers={kn} parts={pn} size={mx - mn} bytes={data.Length} load={loadMs:F2}ms");
        return m;
    }
}
