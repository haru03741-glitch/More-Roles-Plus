using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MrpInstaller.Core;

public enum GamePlatform { Steam, Epic, Manual }

public enum InstallState { NotInstalled, Mrp, MrpDisabled, OtherMod, Broken }

// 見つけた Among Us 1 つ分。状態はファイルを見て毎回作り直す (Refresh)
public sealed class GameInstall
{
    public const string MrpDll = "MoreRolesPlus.dll";
    public const string PatcherDll = "MoreRolesPlus.BootAccel.dll";
    public const string MrpConfig = "86aa5c38-7092-4d6f-ae72-7e9301070806.cfg";

    public GameInstall(string path, GamePlatform platform, string epicAppName = null, bool isCopy = false)
    {
        Path = System.IO.Path.GetFullPath(path).TrimEnd('\\');
        Platform = platform;
        EpicAppName = epicAppName;
        IsCopy = isCopy;
        Refresh();
    }

    public string Path { get; }
    public GamePlatform Platform { get; }
    public string EpicAppName { get; }
    public bool IsCopy { get; } // インストーラーが作った MRP 専用のコピー (exe を直接起動する)

    public string ExePath => P("Among Us.exe");
    public string BepInExDir => P("BepInEx");
    public string PluginsDir => P("BepInEx", "plugins");
    public string PatchersDir => P("BepInEx", "patchers");
    public string MrpDllPath => P("BepInEx", "plugins", MrpDll);
    public string PatcherPath => P("BepInEx", "patchers", PatcherDll);
    public string PatcherDisabledPath => PatcherPath + ".disabled";
    public string WinhttpPath => P("winhttp.dll");
    public string WinhttpDisabledPath => P("winhttp.dll.disabled");
    public string LogPath => P("BepInEx", "LogOutput.log");
    public string BepInExCfg => P("BepInEx", "config", "BepInEx.cfg");
    public string MrpCfg => P("BepInEx", "config", MrpConfig);
    public string LanguageDir => P("MRP_Language");
    public string BackupRoot => P("MRP_Backups");

    public InstallState State { get; private set; }
    public List<string> OtherPlugins { get; private set; } = new();
    public bool Is64Bit { get; private set; } = true;
    public bool HasPatcher => File.Exists(PatcherPath);
    public DateTime? GameUpdated { get; private set; }
    public DateTime? MrpBuilt { get; private set; }
    public long MrpSize { get; private set; }
    public List<string> Backups { get; private set; } = new();

    public string PlatformLabel => IsCopy ? "MRP Copy" : Platform switch
    {
        GamePlatform.Steam => "Steam",
        GamePlatform.Epic => "Epic Games",
        _ => "Folder",
    };

    public void Refresh()
    {
        Is64Bit = PeMachine.Is64(P("GameAssembly.dll"));
        GameUpdated = File.Exists(P("GameAssembly.dll")) ? File.GetLastWriteTime(P("GameAssembly.dll")) : null;
        OtherPlugins = Directory.Exists(PluginsDir)
            ? Directory.GetFiles(PluginsDir, "*.dll", SearchOption.AllDirectories)
                .Select(System.IO.Path.GetFileName)
                .Where(n => !n.Equals(MrpDll, StringComparison.OrdinalIgnoreCase))
                .ToList()
            : new List<string>();
        bool hasCore = File.Exists(P("BepInEx", "core", "BepInEx.Unity.IL2CPP.dll"));
        bool hasMrp = File.Exists(MrpDllPath);
        if (hasMrp)
        {
            var fi = new FileInfo(MrpDllPath);
            MrpBuilt = fi.LastWriteTime;
            MrpSize = fi.Length;
        }
        else { MrpBuilt = null; MrpSize = 0; }

        if (hasMrp && hasCore) State = File.Exists(WinhttpPath) ? InstallState.Mrp : InstallState.MrpDisabled;
        else if (hasMrp) State = InstallState.Broken;
        else if (hasCore || OtherPlugins.Count > 0 || File.Exists(WinhttpPath)) State = InstallState.OtherMod;
        else State = InstallState.NotInstalled;

        Backups = Directory.Exists(BackupRoot)
            ? Directory.GetDirectories(BackupRoot).OrderByDescending(d => d).ToList()
            : new List<string>();
    }

    // 埋め込みの MRP と同じ物が入っているか (中身で見比べる)
    public bool MrpIsCurrent => File.Exists(MrpDllPath) && Payload.SameAsPayload("BepInEx/plugins/" + MrpDll, MrpDllPath);

    public string P(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public override string ToString() => $"[{PlatformLabel}] {Path}";
}

// GameAssembly.dll が 64bit か (同梱の Doorstop と BepInEx は 64bit 用)
internal static class PeMachine
{
    public static bool Is64(string file)
    {
        try
        {
            if (!File.Exists(file)) return true;
            using var fs = File.OpenRead(file);
            using var br = new BinaryReader(fs);
            fs.Position = 0x3C;
            int pe = br.ReadInt32();
            fs.Position = pe + 4;
            ushort machine = br.ReadUInt16();
            return machine == 0x8664;
        }
        catch { return true; }
    }
}
