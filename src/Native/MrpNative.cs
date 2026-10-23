using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MoreRolesPlus.Native;

// C++ の部品 (native/src) の読み込み口。部品は本体の DLL に埋め込んであり (別ファイルで配ると入れ忘れや片方だけの更新が起きる)、
// 最初に使う時に 1 回だけ cache へ書き出して読み、関数の番地を引いておく (DllImport の名前解決は端末で失敗すると呼ぶたびに例外になるので使わない)。
// 書き出す名前に中身のハッシュを入れるので、別の版の AU が同じ場所の部品を掴んでいても上書きで揉めない。
// 読めない時は mod ごと起動しない (Plugin.Load が最初に Loaded を見る)。C# 版は Verify の答え合わせ役だけで、部品の代わりには動かない
internal static unsafe class MrpNative
{
    internal const int Abi = 39;
#if ANDROID
    private const string FileName = "libmrpnative.so";
#else
    private const string FileName = "MrpNative.dll";
#endif

    private static bool _tried;
    private static bool _loaded;
    internal static string Status { get; private set; } = "not loaded";
    internal static bool Verify;
    internal static bool Async = true; // 水の小刻みを作業スレッドで回す (切り替えは答え合わせ用)

    internal static delegate* unmanaged[Cdecl]<byte*, int, int, float, float, float, float, float, float, int, byte*, byte*, int, int, float, float, float, int, void> FloorRaw;
    internal static delegate* unmanaged[Cdecl]<byte*, int, ushort*, byte*, void> FloorBlur;
    internal static delegate* unmanaged[Cdecl]<byte*, byte*, int, int, float, float, float, float, float, float, int, byte*, byte*, int, int, float, float, float, void> FloorLips;
    internal static delegate* unmanaged[Cdecl]<int*, int*, int, int, int, int, float, float, float, float, byte*, int, int, float, float, float, int, byte*, byte*, int, float, float, byte*, void> WaterPixels;
    internal static delegate* unmanaged[Cdecl]<byte*, byte*, int, int, byte*, void> WaterSpreadFace;
    internal static delegate* unmanaged[Cdecl]<void*, int> WaterStep;
    internal static delegate* unmanaged[Cdecl]<void*, void> DamageStamp;
    internal static delegate* unmanaged[Cdecl]<void*, void> SolidCarve;
    internal static delegate* unmanaged[Cdecl]<void*, void> WaterFace;
    internal static delegate* unmanaged[Cdecl]<void*, void> PieceCells;
    internal static delegate* unmanaged[Cdecl]<void*, void> FallRay;
    internal static delegate* unmanaged[Cdecl]<void*, void> WaterCells;
    internal static delegate* unmanaged[Cdecl]<void*, void> LinkScan;
    internal static delegate* unmanaged[Cdecl]<void*, void> WaterRaw;
    internal static delegate* unmanaged[Cdecl]<void*, void> HullClip;
    internal static delegate* unmanaged[Cdecl]<void*, void> FoamDraw;
    internal static delegate* unmanaged[Cdecl]<void*, void> FoamCull;
    internal static delegate* unmanaged[Cdecl]<void*, void> DecompRegions;
    internal static delegate* unmanaged[Cdecl]<void*, void> DecompField;
    internal static delegate* unmanaged[Cdecl]<void*, void> FloorFlood;
    internal static delegate* unmanaged[Cdecl]<void*, void> FloorPatch;
    internal static delegate* unmanaged[Cdecl]<void*, void> FloorCutMoved;
    internal static delegate* unmanaged[Cdecl]<void*, void> LiftShape;
    internal static delegate* unmanaged[Cdecl]<void*, void> WaterFill;
    internal static delegate* unmanaged[Cdecl]<void*, void> FaceHeights;
    internal static delegate* unmanaged[Cdecl]<void*, void> FaceCutHoles;
    internal static delegate* unmanaged[Cdecl]<void*, void> FrontFill;
    internal static delegate* unmanaged[Cdecl]<void*, void> FallDraw;
    internal static delegate* unmanaged[Cdecl]<void*, void> FireStep;
    internal static delegate* unmanaged[Cdecl]<void*, void> SolidFlood;
    internal static delegate* unmanaged[Cdecl]<void*, void> SolidOutside;
    internal static delegate* unmanaged[Cdecl]<void*, void> WetFilter;
    internal static delegate* unmanaged[Cdecl]<void*, void> IslandCells;
    internal static delegate* unmanaged[Cdecl]<void*, void> WaterSpray;
    internal static delegate* unmanaged[Cdecl]<void*, void> CineFrame;
    internal static delegate* unmanaged[Cdecl]<void*, void> CineLight;
    internal static delegate* unmanaged[Cdecl]<void*, void> CineWarp;

    // 部品が読めたか。偽なら Plugin.Load が mod ごと起動しない
    internal static bool Loaded
    {
        get
        {
            if (!_tried) Load();
            return _loaded;
        }
    }


    private static void Load()
    {
        _tried = true;
        try
        {
            using var stream = typeof(MrpNative).Assembly.GetManifestResourceStream("MoreRolesPlus.Native." + FileName);
            if (stream == null) { Status = $"not embedded {FileName}"; return; }
            var bytes = new byte[stream.Length];
            for (int n = 0; n < bytes.Length;)
            {
                int r = stream.Read(bytes, n, bytes.Length - n);
                if (r <= 0) { Status = $"short read {FileName}"; return; }
                n += r;
            }
            string name = Path.GetFileNameWithoutExtension(FileName) + "-" + Convert.ToHexString(SHA256.HashData(bytes), 0, 8).ToLowerInvariant() + Path.GetExtension(FileName);

            // 置き場は BepInEx の cache。書けない・読めない時は plugins、最後に OS の一時フォルダ
            string path = null;
            IntPtr lib = IntPtr.Zero;
            foreach (string dir in new[] { BepInEx.Paths.CachePath, BepInEx.Paths.PluginPath, Path.GetTempPath() })
            {
                path = Export(bytes, dir, name);
                if (path != null && NativeLibrary.TryLoad(path, out lib)) break;
                lib = IntPtr.Zero;
            }
            if (lib == IntPtr.Zero) { Status = $"load failed {name}"; return; }
            var abi = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(lib, "mrp_abi");
            int got = abi();
            if (got != Abi) { Status = $"abi {got} != {Abi} {path}"; return; }
            FloorRaw = (delegate* unmanaged[Cdecl]<byte*, int, int, float, float, float, float, float, float, int, byte*, byte*, int, int, float, float, float, int, void>)NativeLibrary.GetExport(lib, "mrp_floor_raw");
            FloorBlur = (delegate* unmanaged[Cdecl]<byte*, int, ushort*, byte*, void>)NativeLibrary.GetExport(lib, "mrp_floor_blur");
            FloorLips = (delegate* unmanaged[Cdecl]<byte*, byte*, int, int, float, float, float, float, float, float, int, byte*, byte*, int, int, float, float, float, void>)NativeLibrary.GetExport(lib, "mrp_floor_lips");
            WaterPixels = (delegate* unmanaged[Cdecl]<int*, int*, int, int, int, int, float, float, float, float, byte*, int, int, float, float, float, int, byte*, byte*, int, float, float, byte*, void>)NativeLibrary.GetExport(lib, "mrp_water_pixels");
            WaterSpreadFace = (delegate* unmanaged[Cdecl]<byte*, byte*, int, int, byte*, void>)NativeLibrary.GetExport(lib, "mrp_water_spread_face");
            WaterStep = (delegate* unmanaged[Cdecl]<void*, int>)NativeLibrary.GetExport(lib, "mrp_water_step");
            DamageStamp = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_damage_stamp");
            SolidCarve = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_solid_carve");
            WaterFace = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_water_face");
            PieceCells = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_piece_cells");
            FallRay = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_fall_ray");
            WaterCells = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_water_cells");
            LinkScan = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_link_scan");
            WaterRaw = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_water_raw");
            HullClip = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_hull_clip");
            FoamDraw = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_foam_draw");
            FoamCull = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_foam_cull");
            DecompRegions = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_decomp_regions");
            DecompField = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_decomp_field");
            FloorFlood = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_floor_flood");
            FloorPatch = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_floor_patch");
            FloorCutMoved = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_floor_cut_moved");
            LiftShape = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_lift_shape");
            WaterFill = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_water_fill");
            FaceHeights = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_face_heights");
            FaceCutHoles = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_face_cut_holes");
            FrontFill = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_front_fill");
            FallDraw = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_fall_draw");
            FireStep = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_fire_step");
            SolidFlood = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_solid_flood");
            SolidOutside = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_solid_outside");
            WetFilter = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_wet_filter");
            IslandCells = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_island_cells");
            WaterSpray = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_water_spray");
            CineFrame = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_cine_frame");
            CineLight = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_cine_light");
            CineWarp = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(lib, "mrp_cine_warp");
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

#if !ANDROID
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
#endif

    // mod を起動しなかったことを本人に知らせる (PC は Windows の通知窓・Android はログだけ)
    internal static void ShowMissing()
    {
#if !ANDROID
        try
        {
            MessageBoxW(IntPtr.Zero,
                "C++ の部品を読み込めなかったので、More Roles Plus を起動しませんでした (本編はそのまま遊べます)。\n" +
                "ウイルス対策ソフトが BepInEx-MRP\\cache の MrpNative-*.dll を隔離していないか確認してください。\n\n" +
                "More Roles Plus was not started because its native component could not be loaded.\n\n" + Status,
                "More Roles Plus", 0x10 | 0x40000); // MB_ICONERROR | MB_TOPMOST
        }
        catch { }
#endif
    }

    // 中身が同じ物が既にあればそれを使い、無ければ一時名で書いてから置き換える (書きかけを読ませない)。
    // 同じ場所の古い版は消す (別の AU が掴んでいる物は消えないので残す)
    private static string Export(byte[] bytes, string dir, string name)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            {
                string tmp = path + "." + Environment.ProcessId + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, path, true);
            }
            string prefix = Path.GetFileNameWithoutExtension(FileName) + "-";
            foreach (string old in Directory.GetFiles(dir, prefix + "*" + Path.GetExtension(FileName)))
            {
                if (Path.GetFileName(old) == name) continue;
                try { File.Delete(old); } catch { }
            }
            return path;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[native] cannot write under {dir}: {e.Message}");
            return null;
        }
    }
}
