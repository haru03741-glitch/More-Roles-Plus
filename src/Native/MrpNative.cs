using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MoreRolesPlus.Native;

// C++ の部品 (native/src) の読み込み口。plugins の隣の MrpNative.dll / libmrpnative.so を最初に使う時に 1 回だけ読み、
// 関数の番地を引いておく (DllImport の名前解決は端末で失敗すると呼ぶたびに例外になるので使わない)。
// 無い・版が違う時は水を止める (Loaded が偽)。On は実行中に切り替えられる (C# 版との答え合わせ用)
internal static unsafe class MrpNative
{
    internal const int Abi = 12;
#if ANDROID
    private const string FileName = "libmrpnative.so";
#else
    private const string FileName = "MrpNative.dll";
#endif

    private static bool _tried;
    private static bool _loaded;
    internal static string Status { get; private set; } = "not loaded";
    internal static bool Enabled = true;
    internal static bool Verify;

    internal static delegate* unmanaged[Cdecl]<byte*, int, int, float, float, float, float, float, float, int, byte*, byte*, int, int, float, float, float, int, void> FloorRaw;
    internal static delegate* unmanaged[Cdecl]<byte*, int, ushort*, byte*, void> FloorBlur;
    internal static delegate* unmanaged[Cdecl]<byte*, byte*, int, int, float, float, float, float, float, float, int, byte*, void> FloorLips;
    internal static delegate* unmanaged[Cdecl]<int*, int*, int, int, int, int, float, float, float, float, byte*, int, int, float, float, float, int, byte*, byte*, int, float, float, byte*, void> WaterPixels;
    internal static delegate* unmanaged[Cdecl]<byte*, byte*, int, int, byte*, void> WaterSpreadFace;
    internal static delegate* unmanaged[Cdecl]<void*, int> WaterSubstep;
    internal static delegate* unmanaged[Cdecl]<void*, void> DamageStamp;
    internal static delegate* unmanaged[Cdecl]<void*, void> SolidCarve;
    internal static delegate* unmanaged[Cdecl]<void*, void> WaterFace;
    internal static delegate* unmanaged[Cdecl]<void*, void> PieceCells;
    internal static delegate* unmanaged[Cdecl]<void*, void> FallRay;
    internal static bool HasFallRay => FallRay != null;

    // 部品が読めたか (On と違い答え合わせの切り替えを含めない)。偽なら水を止め、部屋の版チェックでも弾く
    internal static bool Loaded
    {
        get
        {
            if (!_tried) Load();
            return _loaded;
        }
    }

    internal static bool On
    {
        get
        {
            if (!_tried) Load();
            return _loaded && Enabled;
        }
    }

    private static void Load()
    {
        _tried = true;
        string path = Path.Combine(BepInEx.Paths.PluginPath, FileName);
        try
        {
            if (!File.Exists(path)) { Status = $"missing {path}"; return; }
            if (!NativeLibrary.TryLoad(path, out var lib)) { Status = $"load failed {path}"; return; }
            var abi = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(lib, "mrp_abi");
            int got = abi();
            if (got != Abi) { Status = $"abi {got} != {Abi} {path}"; return; }
            FloorRaw = (delegate* unmanaged[Cdecl]<byte*, int, int, float, float, float, float, float, float, int, byte*, byte*, int, int, float, float, float, int, void>)NativeLibrary.GetExport(lib, "mrp_floor_raw");
            FloorBlur = (delegate* unmanaged[Cdecl]<byte*, int, ushort*, byte*, void>)NativeLibrary.GetExport(lib, "mrp_floor_blur");
            FloorLips = (delegate* unmanaged[Cdecl]<byte*, byte*, int, int, float, float, float, float, float, float, int, byte*, void>)NativeLibrary.GetExport(lib, "mrp_floor_lips");
            WaterPixels = (delegate* unmanaged[Cdecl]<int*, int*, int, int, int, int, float, float, float, float, byte*, int, int, float, float, float, int, byte*, byte*, int, float, float, byte*, void>)NativeLibrary.GetExport(lib, "mrp_water_pixels");
            WaterSpreadFace = (delegate* unmanaged[Cdecl]<byte*, byte*, int, int, byte*, void>)NativeLibrary.GetExport(lib, "mrp_water_spread_face");
            WaterSubstep = (delegate* unmanaged[Cdecl]<void*, int>)NativeLibrary.GetExport(lib, "mrp_water_substep");
            DamageStamp = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_damage_stamp");
            SolidCarve = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_solid_carve");
            WaterFace = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_water_face");
            PieceCells = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_piece_cells");
            FallRay = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_fall_ray");
            _loaded = true;
            Status = $"ok abi={got} {path}";
        }
        catch (Exception e)
        {
            Status = $"error {e.GetType().Name}: {e.Message}";
        }
        finally
        {
            Plugin.Logger.LogInfo($"[native] {Status}");
        }
    }
}
