using System.Text.Json;

namespace MrpServer;

// Impostor を動かしているフォルダーの config_mrp.json (無ければ例を書き出して、Discord の機能は止めたまま動く)
public sealed class MrpConfig
{
    public string PublicUrl { get; set; } = "https://amongus.nkzk.win";
    public string ModGuid { get; set; } = "169375b5-e08c-4ca8-bf96-0c8cde15ddf7";
    // Cloudflare など前に置いた物が付ける本当の送り元 (CF-Connecting-IP / X-Forwarded-For) を信じる
    public bool TrustProxyHeaders { get; set; } = true;
    public DiscordConfig Discord { get; set; } = new();
    public ReportConfig Reports { get; set; } = new();
    public MatchmakingConfig Matchmaking { get; set; } = new();

    public sealed class DiscordConfig
    {
        public string BotToken { get; set; } = "";
        public string GuildId { get; set; } = "";
        public string ReportChannelId { get; set; } = "";     // フォーラムでも普通のテキストチャンネルでもよい
        public string MatchmakingChannelId { get; set; } = "";
        public string StaffRoleId { get; set; } = "";          // 管理のボタンを押せる役職。空ならサーバーの管理者だけ
        public string StaffChannelId { get; set; } = "";       // 管理のボタンを置くチャンネル (スタッフだけが見られる所)。空なら報告のスレッドの中
    }

    public sealed class ReportConfig
    {
        public bool RequireRecentConnection { get; set; } = true;
        public int RecentConnectionHours { get; set; } = 72;
        // 1 時間に送れる数。スタッフが「既読」を押した報告は数えない (読まれたらまた送れる)
        public int MaxPerHourPerPlayer { get; set; } = 3;
        public int MaxPerHourPerIp { get; set; } = 6;
        public int MaxLogBytes { get; set; } = 6 * 1024 * 1024;
        // 画像・動画 (1 回に付けられる数と 1 つの大きさ。Discord の無料の上限が 10MB)
        public int MaxMediaFiles { get; set; } = 4;
        public int MaxMediaBytes { get; set; } = 9 * 1024 * 1024 + 512 * 1024;
        public int ChatCooldownSeconds { get; set; } = 3;
        public int MaxChatPerHour { get; set; } = 60;
    }

    public sealed class MatchmakingConfig
    {
        public bool Enabled { get; set; } = true;
        public bool OnlyMrpGames { get; set; } = true;
    }

    public bool DiscordReady => !string.IsNullOrWhiteSpace(Discord.BotToken);

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public const string FileName = "config_mrp.json";
    public static string DataDir => Path.Combine(Directory.GetCurrentDirectory(), "mrp-data");

    public static MrpConfig Load()
    {
        string path = Path.Combine(Directory.GetCurrentDirectory(), FileName);
        if (!File.Exists(path))
        {
            var def = new MrpConfig();
            try { File.WriteAllText(path, JsonSerializer.Serialize(def, Json)); } catch { }
            return def;
        }
        return JsonSerializer.Deserialize<MrpConfig>(File.ReadAllText(path), Json) ?? new MrpConfig();
    }
}
