using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace MoreRolesPlus.Flight;

// 3D の艦隊戦の船の形 (.mrpm・tools/make-flight-models.py が書く) を読み、Mesh と当たりの球・印にする。
// 頂点は配列に詰め替えずに、ファイルの並びのまま頂点バッファへ 1 回で書く (Mesh.vertices などの配列の受け渡しを通らない)。
// 頂点 = 位置 f32x3・面の法線 f32x3・色 u8x4・縁取り用のなめらかな法線 f32x3 + 動く部品の番号 f32 (TEXCOORD1・0 = 船体・k = 部品 k-1) の 44 バイト
internal static unsafe class ModelBank
{
    internal const int Stride = 44;

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

    internal static Model Get(int id)
    {
        if (id < 0 || id >= Names.Length) return null;
        if (Loaded[id] != null && Loaded[id].Mesh) return Loaded[id];
        Loaded[id] = Load(Names[id]);
        return Loaded[id];
    }

    private static Model Load(string name)
    {
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
        if (Magic() != 0x4D50524D || I() != 2) { Plugin.Logger.LogError($"flight model bad header: {name}"); return null; }
        int vn = I(), inN = I();
        int vOff = o;
        o += vn * Stride;
        int iOff = o;
        o += inN * 4;
        var m = new Model();
        // 範囲 (Mesh の bounds と当たりの粗い判定の確かめ用)
        Vector3 mn = new(float.MaxValue, float.MaxValue, float.MaxValue), mx = -mn;
        fixed (byte* p = data)
        {
            for (int i = 0; i < vn; i++)
            {
                float* f = (float*)(p + vOff + i * Stride);
                mn = Vector3.Min(mn, new Vector3(f[0], f[1], f[2]));
                mx = Vector3.Max(mx, new Vector3(f[0], f[1], f[2]));
            }
            var mesh = new Mesh { name = "MrpFlight_" + name };
            mesh.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            var attrs = new Il2CppStructArray<VertexAttributeDescriptor>((long)4);
            attrs[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0);
            attrs[1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, 0);
            attrs[2] = new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4, 0);
            attrs[3] = new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4, 0);
            mesh.SetVertexBufferParamsFromArray(vn, attrs);
            mesh.InternalSetVertexBufferData(0, (IntPtr)(p + vOff), 0, 0, vn, Stride, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            // 添字は一度だけなので Mesh.triangles で渡す (添字バッファの直書きと SetSubMesh は削られた本編に無いことがある)
            var tri = new int[inN];
            Buffer.MemoryCopy(p + iOff, Unsafe.AsPointer(ref tri[0]), inN * 4L, inN * 4L);
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
        for (int i = 0; i < kn; i++) m.MarkerPart[i] = I();
        m.Min = mn; m.Max = mx;
        Plugin.Logger.LogInfo($"flight model {name}: verts={vn} tris={inN / 3} spheres={sn} markers={kn} parts={pn} size={mx - mn}");
        return m;
    }
}
