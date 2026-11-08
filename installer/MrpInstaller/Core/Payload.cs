using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace MrpInstaller.Core;

// exe に埋め込んだ Payload.zip (BepInEx 一式 + MRP)。中身は 1 回だけメモリへ読む
public static class Payload
{
    private static byte[] _zip;
    private static Dictionary<string, (long Size, string Hash, DateTime Time)> _index;

    private static byte[] Zip
    {
        get
        {
            if (_zip != null) return _zip;
            using var s = typeof(Payload).Assembly.GetManifestResourceStream("MrpInstaller.Payload.zip")
                          ?? throw new InvalidOperationException("Payload.zip が exe に入っていない");
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return _zip = ms.ToArray();
        }
    }

    public static ZipArchive Open() => new(new MemoryStream(Zip, false), ZipArchiveMode.Read);

    // 中身の一覧 (パスは / 区切り) と SHA256
    public static Dictionary<string, (long Size, string Hash, DateTime Time)> Index
    {
        get
        {
            if (_index != null) return _index;
            var d = new Dictionary<string, (long, string, DateTime)>(StringComparer.OrdinalIgnoreCase);
            using var z = Open();
            foreach (var e in z.Entries.Where(e => e.Name.Length > 0))
            {
                using var s = e.Open();
                d[Norm(e.FullName)] = (e.Length, Hash(s), e.LastWriteTime.LocalDateTime);
            }
            return _index = d;
        }
    }

    public static string Norm(string p) => p.Replace('\\', '/').TrimStart('/');

    public static string Hash(Stream s)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "");
    }

    public static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Hash(fs);
    }

    private static readonly Dictionary<string, string> HashCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool SameAsPayload(string entry, string file)
    {
        try
        {
            if (!Index.TryGetValue(Norm(entry), out var info) || !File.Exists(file)) return false;
            var fi = new FileInfo(file);
            if (fi.Length != info.Size) return false;
            // 大きい DLL を画面の更新のたびに読み直さないよう、大きさと日時が同じなら前の結果を使う
            string key = fi.FullName + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            lock (HashCache)
                if (HashCache.TryGetValue(key, out string h)) return h == info.Hash;
            string hash = HashFile(file);
            lock (HashCache) HashCache[key] = hash;
            return hash == info.Hash;
        }
        catch { return false; }
    }

    public static long TotalSize => Index.Values.Sum(v => v.Size);
    public static long CompressedSize => Zip.Length;

    public static DateTime? MrpTime => Index.TryGetValue("BepInEx/plugins/" + GameInstall.MrpDll, out var i) ? i.Time : null;

    // MRP の DLL のバージョン (AssemblyInformationalVersion があれば) を書き出して調べる
    private static string _mrpVersion, _bieVersion;
    public static string MrpVersion => _mrpVersion ??= Short(ReadMrpVersion());
    public static string BepInExVersion => _bieVersion ??= Short(ReadBepInExVersion());

    // 1.0.0+9b20b5066bbd... → 1.0.0 (9b20b50)
    private static string Short(string v)
    {
        int plus = v.IndexOf('+');
        return plus < 0 ? v : $"{v.Substring(0, plus)} ({v.Substring(plus + 1, Math.Min(7, v.Length - plus - 1))})";
    }

    private static string ReadMrpVersion()
    {
        {
            try
            {
                using var z = Open();
                var e = z.GetEntry("BepInEx/plugins/" + GameInstall.MrpDll);
                if (e == null) return "?";
                string tmp = Path.Combine(Path.GetTempPath(), "mrp_ver_" + Guid.NewGuid().ToString("N") + ".dll");
                e.ExtractToFile(tmp, true);
                try { var v = FileVersionInfo.GetVersionInfo(tmp); return v.ProductVersion ?? v.FileVersion ?? "?"; }
                finally { try { File.Delete(tmp); } catch { } }
            }
            catch { return "?"; }
        }
    }

    private static string ReadBepInExVersion()
    {
        {
            try
            {
                using var z = Open();
                var e = z.GetEntry("BepInEx/core/BepInEx.Core.dll");
                if (e == null) return "?";
                string tmp = Path.Combine(Path.GetTempPath(), "mrp_bie_" + Guid.NewGuid().ToString("N") + ".dll");
                e.ExtractToFile(tmp, true);
                try { var v = FileVersionInfo.GetVersionInfo(tmp); return v.ProductVersion ?? "?"; }
                finally { try { File.Delete(tmp); } catch { } }
            }
            catch { return "?"; }
        }
    }
}
