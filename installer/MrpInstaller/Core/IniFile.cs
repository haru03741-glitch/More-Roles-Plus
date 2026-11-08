using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MrpInstaller.Core;

// BepInEx の .cfg / doorstop の .ini を、コメントや並びを崩さずに 1 か所だけ読み書きする
public static class IniFile
{
    public static string Get(string path, string section, string key)
    {
        if (!File.Exists(path)) return null;
        string cur = null;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith("[") && line.EndsWith("]")) { cur = line.Substring(1, line.Length - 2); continue; }
            if (!string.Equals(cur, section, StringComparison.OrdinalIgnoreCase) || line.StartsWith("#") || line.StartsWith(";")) continue;
            int eq = line.IndexOf('=');
            if (eq > 0 && string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) return line.Substring(eq + 1).Trim();
        }
        return null;
    }

    public static void Set(string path, string section, string key, string value)
    {
        var lines = File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>();
        string cur = null;
        int sectionEnd = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                cur = line.Substring(1, line.Length - 2);
                if (string.Equals(cur, section, StringComparison.OrdinalIgnoreCase)) sectionEnd = i + 1;
                continue;
            }
            if (!string.Equals(cur, section, StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Length > 0) sectionEnd = i + 1;
            if (line.StartsWith("#") || line.StartsWith(";")) continue;
            int eq = line.IndexOf('=');
            if (eq > 0 && string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key} = {value}";
                Write(path, lines);
                return;
            }
        }
        if (sectionEnd < 0)
        {
            if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            sectionEnd = lines.Count;
        }
        lines.Insert(sectionEnd, $"{key} = {value}");
        Write(path, lines);
    }

    private static void Write(string path, List<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }
}
