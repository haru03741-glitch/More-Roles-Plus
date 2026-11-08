using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace MrpInstaller.Core;

// インストーラー自身の設定 (%AppData%\MoreRolesPlusInstaller\settings.json)
public sealed class Settings
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MoreRolesPlusInstaller");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public string Language { get; set; } = "ja";
    public List<string> ExtraFolders { get; set; } = new();
    public List<string> CopyFolders { get; set; } = new();
    public string LastSelected { get; set; }
    public bool CloseAfterLaunch { get; set; }
    public bool CheckUpdateOnStart { get; set; } = true;
    public bool KeepBackups { get; set; } = true;
    public bool TourDone { get; set; }
    public string SkippedVersion { get; set; }

    private static Settings _current;
    public static Settings Current => _current ??= Load();

    private static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this));
        }
        catch { }
    }

    public void AddFolder(string dir, bool copy = false)
    {
        dir = Path.GetFullPath(dir).TrimEnd('\\');
        if (!ExtraFolders.Exists(d => d.Equals(dir, StringComparison.OrdinalIgnoreCase))) ExtraFolders.Add(dir);
        if (copy && !CopyFolders.Exists(d => d.Equals(dir, StringComparison.OrdinalIgnoreCase))) CopyFolders.Add(dir);
        Save();
    }

    public void RemoveFolder(string dir)
    {
        ExtraFolders.RemoveAll(d => d.Equals(dir, StringComparison.OrdinalIgnoreCase));
        CopyFolders.RemoveAll(d => d.Equals(dir, StringComparison.OrdinalIgnoreCase));
        Save();
    }
}
