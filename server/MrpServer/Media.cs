namespace MrpServer;

// ゲームから付けられた画像・動画。名前の拡張子は信じず、中身の頭で種類を決める (決まらない物は受けない)
public sealed record MediaFile(string Name, byte[] Data, string Kind);

public static class Media
{
    // (拡張子, 種類) / 分からなければ null
    public static (string ext, string kind)? Detect(byte[] d)
    {
        if (d.Length < 12) return null;
        if (d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') return ("png", "image");
        if (d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return ("jpg", "image");
        if (d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8') return ("gif", "image");
        if (d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return ("webp", "image");
        if (d[0] == 0x1A && d[1] == 0x45 && d[2] == 0xDF && d[3] == 0xA3) return ("webm", "video");
        if (d[4] == 'f' && d[5] == 't' && d[6] == 'y' && d[7] == 'p')
            return d[8] == 'q' && d[9] == 't' ? ("mov", "video") : ("mp4", "video");
        return null;
    }

    // 名前は英数字などだけにして、拡張子は中身から付け直す
    public static string SafeName(string? name, string ext, int index)
    {
        string stem = Path.GetFileNameWithoutExtension(name ?? "");
        stem = new string(stem.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(40).ToArray());
        if (stem.Length == 0) stem = $"file{index + 1}";
        return $"{stem}.{ext}";
    }

    // Discord の 1 つの書き込みの上限 (合わせて max バイト・10 個) に収まるように分ける
    public static List<List<MediaFile>> Chunks(IReadOnlyList<MediaFile> files, long max)
    {
        var chunks = new List<List<MediaFile>>();
        List<MediaFile>? cur = null;
        long size = 0;
        foreach (var f in files)
        {
            if (cur == null || cur.Count >= 10 || size + f.Data.Length > max)
            {
                chunks.Add(cur = new List<MediaFile>());
                size = 0;
            }
            cur.Add(f);
            size += f.Data.Length;
        }
        return chunks;
    }
}
