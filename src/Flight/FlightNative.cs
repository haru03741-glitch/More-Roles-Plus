using System;
using System.Runtime.InteropServices;
using MoreRolesPlus.Native;

namespace MoreRolesPlus.Flight;

// C++ のシム (native/src/flight_sim.cpp) の口。形は flight.h と揃える
internal static unsafe class FlightNative
{
    internal const int BoltStride = 24;
    internal const int MaxShips = 8;
    internal const int MaxBolts = 1024;
    internal const int MaxFx = 2048 + 320;

    internal enum Ev { Fire = 1, Hit = 2, BoltEnd = 3, Dodge = 4, UTurn = 5 }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Sphere { public float X, Y, Z, R; public int Sector; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Marker { public int Kind, Id; public float Px, Py, Pz, Dx, Dy, Dz; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Event { public int Kind, A, B, C; public float X, Y, Z, F; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public float Pitch, Yaw, Roll;
        public int Fire, Boost, Brake, Dodge, UTurn;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct FrameArgs
    {
        public IntPtr World;
        public float Dt;
        public int Local;
        public Input In;
        public double Now;
        public float* ShipOut;
        public float* CamOut;
        public byte* BoltVerts;
        public int BoltCap, BoltPrev, BoltN;
        public Event* EvOut;
        public int EvCap, EvN;
        public float* Hud;
        public int Steps;
        public byte* FxVerts;
        public int FxCap, FxPrev, FxN;
    }

    internal static delegate* unmanaged[Cdecl]<uint, IntPtr> Create;
    internal static delegate* unmanaged[Cdecl]<IntPtr, void> Destroy;
    internal static delegate* unmanaged[Cdecl]<int> Size;
    internal static delegate* unmanaged[Cdecl]<IntPtr, float*, int, void> Params;
    internal static delegate* unmanaged[Cdecl]<IntPtr, int, Sphere*, int, Marker*, int, int> Model;
    internal static delegate* unmanaged[Cdecl]<IntPtr, int, int, int, int, float*, float*, float, void> Spawn;
    internal static delegate* unmanaged[Cdecl]<IntPtr, int, void> Despawn;
    internal static delegate* unmanaged[Cdecl]<IntPtr, int, double, float*, float*, float*, void> Snap;
    internal static delegate* unmanaged[Cdecl]<IntPtr, int, float*, float*, float, float, void> Bolt;
    internal static delegate* unmanaged[Cdecl]<IntPtr, int, float, void> Hp;
    internal static delegate* unmanaged[Cdecl]<IntPtr, int, float*, void> Get;
    internal static delegate* unmanaged[Cdecl]<FrameArgs*, void> Frame;
    internal static delegate* unmanaged[Cdecl]<IntPtr, float*, float, void> Explode;

    private static int _state; // 0 = 未 / 1 = 使える / 2 = 使えない

    internal static bool Ready()
    {
        if (_state != 0) return _state == 1;
        try
        {
            Create = (delegate* unmanaged[Cdecl]<uint, IntPtr>)Need("mrp_flt_create");
            Destroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)Need("mrp_flt_destroy");
            Size = (delegate* unmanaged[Cdecl]<int>)Need("mrp_flt_size");
            Params = (delegate* unmanaged[Cdecl]<IntPtr, float*, int, void>)Need("mrp_flt_params");
            Model = (delegate* unmanaged[Cdecl]<IntPtr, int, Sphere*, int, Marker*, int, int>)Need("mrp_flt_model");
            Spawn = (delegate* unmanaged[Cdecl]<IntPtr, int, int, int, int, float*, float*, float, void>)Need("mrp_flt_spawn");
            Despawn = (delegate* unmanaged[Cdecl]<IntPtr, int, void>)Need("mrp_flt_despawn");
            Snap = (delegate* unmanaged[Cdecl]<IntPtr, int, double, float*, float*, float*, void>)Need("mrp_flt_snap");
            Bolt = (delegate* unmanaged[Cdecl]<IntPtr, int, float*, float*, float, float, void>)Need("mrp_flt_bolt");
            Hp = (delegate* unmanaged[Cdecl]<IntPtr, int, float, void>)Need("mrp_flt_hp");
            Get = (delegate* unmanaged[Cdecl]<IntPtr, int, float*, void>)Need("mrp_flt_get");
            Frame = (delegate* unmanaged[Cdecl]<FrameArgs*, void>)Need("mrp_flt_frame");
            Explode = (delegate* unmanaged[Cdecl]<IntPtr, float*, float, void>)Need("mrp_flt_explode");
            // 構造体の大きさが C++ と違えば口を開けない (片方だけ欄を足した時に壊れた値で動かさない)
            int cs = Size(), ms = sizeof(FrameArgs);
            if (cs != ms) throw new InvalidOperationException($"FrameArgs size C++ {cs} != C# {ms}");
            _state = 1;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"flight native: {e.Message}");
            _state = 2;
        }
        return _state == 1;
    }

    private static IntPtr Need(string name)
    {
        IntPtr p = MrpNative.Export(name);
        if (p == IntPtr.Zero) throw new InvalidOperationException($"export not found: {name}");
        return p;
    }
}
