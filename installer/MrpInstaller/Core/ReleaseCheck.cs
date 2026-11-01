using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MrpInstaller.Core;

// GitHub のリリース (タグ vX.Y.Z) を見て、新しければ落として入れ替える。
//   MoreRolesPlus.dll           → 入っている各ゲームの MRP だけ入れ替える
//   MoreRolesPlusInstaller.exe  → インストーラー自身を入れ替えて開き直す (BepInEx 一式もこちらに入っている)
// 版は src/Plugin.cs の Version。DLL とインストーラーの版はビルドの時にそこから付く
public static class ReleaseCheck
{
    public const string Repo = "haru03741-glitch/More-Roles-Plus";
    public const string InstallerAsset = "MoreRolesPlusInstaller.exe";
    public static string RepoUrl => "https://github.com/" + Repo;
    public static string ReleasesUrl => RepoUrl + "/releases";

    public sealed class Release
    {
        public string Tag, Name, Url, Notes;
        public string DllUrl, ExeUrl;
        public long DllSize, ExeSize;
        public DateTime Published;
        public Version Version => ParseVersion(Tag);
    }

    public static Version InstallerVersion => typeof(ReleaseCheck).Assembly.GetName().Version;

    // "v1.2.3" / "1.2.3+abc" / "1.2.3 (abc)" / "1.2" → Version (読めなければ null)
    public static Version ParseVersion(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(new[] { '+', '-', ' ', '(' });
        if (cut >= 0) s = s.Substring(0, cut);
        if (!s.Contains(".")) s += ".0";
        return Version.TryParse(s, out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static Version FileVersionOf(string path)
    {
        try { return File.Exists(path) ? ParseVersion(FileVersionInfo.GetVersionInfo(path).ProductVersion) : null; }
        catch { return null; }
    }

    // リリースの方が新しいか。版が読めない時は日付で見る
    public static bool Newer(Release rel, Version have, DateTime? haveTime)
    {
        if (rel == null) return false;
        if (rel.Version != null && have != null) return rel.Version > Normalize(have);
        return haveTime.HasValue && rel.Published.ToLocalTime() > haveTime.Value.AddMinutes(10);
    }

    public static bool InstallerOutdated(Release rel) => rel?.ExeUrl != null && rel.Version != null && rel.Version > Normalize(InstallerVersion);

    public static bool MrpOutdated(Release rel, GameInstall g) =>
        rel?.DllUrl != null && g.State is InstallState.Mrp or InstallState.MrpDisabled && Newer(rel, FileVersionOf(g.MrpDllPath), g.MrpBuilt);

    private static HttpClient Client(TimeSpan timeout)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var c = new HttpClient { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MoreRolesPlusInstaller/" + InstallerVersion);
        return c;
    }

    // 下書き・プレリリースを除いた一番新しいリリース。無い・つながらない時は null / 例外
    public static async Task<Release> Latest()
    {
        using var c = Client(TimeSpan.FromSeconds(20));
        // MRP_RELEASES_URL でリリース一覧の場所を差し替えられる (手元での試験用)
        string url = Environment.GetEnvironmentVariable("MRP_RELEASES_URL") ?? $"https://api.github.com/repos/{Repo}/releases?per_page=10";
        var res = await c.GetAsync(url).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        var list = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<List<Dictionary<string, object>>>(await res.Content.ReadAsStringAsync().ConfigureAwait(false));
        foreach (var r in list ?? new())
        {
            if (r.TryGetValue("draft", out var d) && d is true) continue;
            if (r.TryGetValue("prerelease", out var pre) && pre is true) continue;
            var rel = new Release
            {
                Tag = r["tag_name"] as string,
                Name = r.TryGetValue("name", out var n) ? n as string : null,
                Url = r["html_url"] as string,
                Notes = r.TryGetValue("body", out var b) ? b as string : null,
                Published = DateTime.TryParse(r["published_at"] as string, out var p) ? p : DateTime.MinValue,
            };
            if (r["assets"] is System.Collections.ArrayList assets)
                foreach (Dictionary<string, object> a in assets)
                {
                    string name = a["name"] as string;
                    if (string.Equals(name, GameInstall.MrpDll, StringComparison.OrdinalIgnoreCase))
                    {
                        rel.DllUrl = a["browser_download_url"] as string;
                        rel.DllSize = Convert.ToInt64(a["size"]);
                    }
                    else if (string.Equals(name, InstallerAsset, StringComparison.OrdinalIgnoreCase))
                    {
                        rel.ExeUrl = a["browser_download_url"] as string;
                        rel.ExeSize = Convert.ToInt64(a["size"]);
                    }
                }
            return rel;
        }
        return null;
    }

    private static async Task Download(string url, long size, string dest, Action<double> progress)
    {
        using var c = Client(TimeSpan.FromMinutes(10));
        using var res = await c.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        long total = res.Content.Headers.ContentLength ?? size, done = 0;
        using (var s = await res.Content.ReadAsStreamAsync().ConfigureAwait(false))
        using (var fs = File.Create(dest))
        {
            var buf = new byte[81920];
            int n;
            while ((n = await s.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buf, 0, n).ConfigureAwait(false);
                done += n;
                if (total > 0) progress?.Invoke((double)done / total);
            }
        }
        // 途中で切れた物・exe でない物 (エラーの HTML など) は使わない
        var fi = new FileInfo(dest);
        bool mz;
        using (var fs = File.OpenRead(dest)) mz = fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
        if ((size > 0 && fi.Length != size) || !mz)
        {
            File.Delete(dest);
            throw new InstallException(Lang.T("err.badDownload"));
        }
    }

    // 入っている DLL は MoreRolesPlus.dll.bak に残す
    public static async Task InstallDll(GameInstall g, Release rel, Action<double> progress)
    {
        if (rel?.DllUrl == null) throw new InstallException(Lang.T("err.noAsset"));
        if (ModInstaller.GameRunning()) throw new InstallException(Lang.T("err.running"));
        Directory.CreateDirectory(g.PluginsDir);
        string tmp = g.MrpDllPath + ".download";
        await Download(rel.DllUrl, rel.DllSize, tmp, progress).ConfigureAwait(false);
        if (File.Exists(g.MrpDllPath))
        {
            string bak = g.MrpDllPath + ".bak";
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(g.MrpDllPath, bak);
        }
        File.Move(tmp, g.MrpDllPath);
        g.Refresh();
    }

    // 動いている exe は消せないが名前は変えられるので、.old へ退けて新しい物を置き、開き直す
    public static async Task<string> UpdateSelf(Release rel, Action<double> progress)
    {
        if (rel?.ExeUrl == null) throw new InstallException(Lang.T("err.noAsset"));
        string exe = Process.GetCurrentProcess().MainModule.FileName;
        string tmp = exe + ".new";
        await Download(rel.ExeUrl, rel.ExeSize, tmp, progress).ConfigureAwait(false);
        string old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        File.Move(tmp, exe);
        return exe;
    }

    public static void CleanupOld()
    {
        try
        {
            string old = Process.GetCurrentProcess().MainModule.FileName + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch { }
    }
}
