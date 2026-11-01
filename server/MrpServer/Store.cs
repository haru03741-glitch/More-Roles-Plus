using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MrpServer;

// サーバーにつないだ人 (フレンドコード → PUID・名前・IP・最後に見た時刻)。
// バグ報告は「最近ここへつないだフレンドコード」からだけ受ける (他人のコードを名乗った報告を弾く)
public sealed class SeenPlayer
{
    public string FriendCode { get; set; } = "";
    public string Puid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public DateTime LastSeen { get; set; }
}

public sealed class Report
{
    public string Id { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public string FirstMessageId { get; set; } = "";
    public string FriendCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Ip { get; set; } = "";
    public DateTime Created { get; set; }
    public DateTime LastActivity { get; set; }
    public bool Closed { get; set; }
    public bool Acked { get; set; }                  // スタッフが「既読」を押した (送れる数に数えない)
    public string PanelChannelId { get; set; } = ""; // 管理のボタンの書き込み
    public string PanelMessageId { get; set; } = "";
}

public sealed class Ban
{
    public string FriendCode { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime At { get; set; }
}

public sealed class Store
{
    private sealed class Data
    {
        public List<SeenPlayer> Seen { get; set; } = new();
        public List<Report> Reports { get; set; } = new();
        public List<Ban> Bans { get; set; } = new();
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, SeenPlayer> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Report> _reports = new();
    private readonly List<Ban> _bans = new();
    private readonly string _path = Path.Combine(MrpConfig.DataDir, "store.json");
    private int _dirty;

    public Store()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(_path), MrpConfig.Json) ?? new Data();
            foreach (var s in d.Seen) _seen[s.FriendCode] = s;
            foreach (var r in d.Reports) _reports[r.Id] = r;
            _bans.AddRange(d.Bans);
        }
        catch { }
    }

    public void Touch(string friendCode, string puid, string name, string ip)
    {
        if (string.IsNullOrWhiteSpace(friendCode)) return;
        lock (_lock)
        {
            if (!_seen.TryGetValue(friendCode, out var s)) _seen[friendCode] = s = new SeenPlayer { FriendCode = friendCode };
            if (!string.IsNullOrEmpty(puid)) s.Puid = puid;
            if (!string.IsNullOrEmpty(name)) s.Name = name;
            if (!string.IsNullOrEmpty(ip)) s.Ip = ip;
            s.LastSeen = DateTime.UtcNow;
        }
        MarkDirty();
    }

    public SeenPlayer? Seen(string friendCode)
    {
        lock (_lock) return _seen.TryGetValue(friendCode, out var s) ? s : null;
    }

    public Report? Get(string id)
    {
        lock (_lock) return _reports.TryGetValue(id, out var r) ? r : null;
    }

    // 1 時間の枠を見て、空いていれば報告の記録を先に作って置く (同時に送られても枠を越えない)。
    // 枠に数えるのは、1 時間以内の、スタッフがまだ「既読」を押していない報告
    public (Report report, string token)? TryReserve(string friendCode, string name, string title, string ip, int maxPlayer, int maxIp)
    {
        var since = DateTime.UtcNow.AddHours(-1);
        lock (_lock)
        {
            var recent = _reports.Values.Where(r => !r.Acked && r.Created > since).ToList();
            if (maxPlayer > 0 && recent.Count(r => string.Equals(r.FriendCode, friendCode, StringComparison.OrdinalIgnoreCase)) >= maxPlayer) return null;
            if (maxIp > 0 && recent.Count(r => r.Ip == ip) >= maxIp) return null;
            var made = NewReport(friendCode, name, title, ip);
            _reports[made.report.Id] = made.report;
            return made;
        }
    }

    public void Remove(string id)
    {
        lock (_lock) _reports.Remove(id);
        MarkDirty();
    }

    public (Report report, string token) NewReport(string friendCode, string name, string title, string ip)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var r = new Report
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant(),
            TokenHash = Hash(token),
            FriendCode = friendCode,
            Name = name,
            Title = title,
            Ip = ip,
            Created = DateTime.UtcNow,
            LastActivity = DateTime.UtcNow,
        };
        return (r, token);
    }

    public void Save(Report r)
    {
        lock (_lock) _reports[r.Id] = r;
        MarkDirty();
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool TokenOk(Report r, string? token) =>
        !string.IsNullOrEmpty(token) && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(r.TokenHash));

    public Ban? BanFor(string friendCode, string ip)
    {
        lock (_lock)
            return _bans.FirstOrDefault(b =>
                (!string.IsNullOrEmpty(b.FriendCode) && string.Equals(b.FriendCode, friendCode, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(b.Ip) && b.Ip == ip));
    }

    public bool IsBanned(string friendCode)
    {
        lock (_lock) return _bans.Any(b => string.Equals(b.FriendCode, friendCode, StringComparison.OrdinalIgnoreCase));
    }

    public void AddBan(Ban b)
    {
        lock (_lock) _bans.Add(b);
        MarkDirty();
    }

    public bool RemoveBan(string friendCode)
    {
        int n;
        lock (_lock) n = _bans.RemoveAll(b => string.Equals(b.FriendCode, friendCode, StringComparison.OrdinalIgnoreCase));
        if (n > 0) MarkDirty();
        return n > 0;
    }

    private void MarkDirty()
    {
        if (Interlocked.Exchange(ref _dirty, 1) == 1) return;
        // 少し待ってまとめて書く
        _ = Task.Delay(2000).ContinueWith(_ => Flush());
    }

    public void Flush()
    {
        Interlocked.Exchange(ref _dirty, 0);
        try
        {
            string json;
            lock (_lock)
            {
                // 古い記録は捨てる (つないだ記録 30 日・閉じた報告 180 日)
                var oldSeen = DateTime.UtcNow.AddDays(-30);
                foreach (var k in _seen.Where(p => p.Value.LastSeen < oldSeen).Select(p => p.Key).ToList()) _seen.Remove(k);
                var oldReport = DateTime.UtcNow.AddDays(-180);
                foreach (var k in _reports.Where(p => p.Value.LastActivity < oldReport).Select(p => p.Key).ToList()) _reports.Remove(k);
                json = JsonSerializer.Serialize(new Data { Seen = _seen.Values.ToList(), Reports = _reports.Values.ToList(), Bans = _bans.ToList() }, MrpConfig.Json);
            }
            Directory.CreateDirectory(MrpConfig.DataDir);
            File.WriteAllText(_path + ".tmp", json);
            File.Move(_path + ".tmp", _path, true);
        }
        catch { }
    }
}

// 決めた時間の中で何回まで (鍵ごと)
public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _hits = new();

    public bool Allow(string key, int max, TimeSpan window, bool record = true)
    {
        if (max <= 0) return true;
        var q = _hits.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (q)
        {
            var now = DateTime.UtcNow;
            while (q.Count > 0 && now - q.Peek() > window) q.Dequeue();
            if (q.Count >= max) return false;
            if (record) q.Enqueue(now);
            return true;
        }
    }
}
