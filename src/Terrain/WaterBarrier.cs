using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace MoreRolesPlus.Terrain;

// 水を止める絵の画素の地図 (柵・家具など手前の物の絵の不透明な所)。地図は tools/make-water-barrier.py がゲームの絵から作る。
// 斜め上から見た絵では、縁に立つ物の高さ z の画素は縁から画面で z だけ上にある。縁の真上の画素の列を見れば、
// どこに柱があってどこが抜けているか・どこまで高いかがそのまま分かる (網の目や柱の間は透明なので水が抜ける)
internal static class WaterBarrier
{
    private static readonly byte[] Magic = { (byte)'M', (byte)'R', (byte)'P', (byte)'B' };
    private const int Version = 1;
    private const float OriginTolerance = 0.01f;
    // 縁の線の上下この距離に画素があれば、その列の物は縁に立っている (絵の根元と升の縁は少しずれる)
    private const float FootBand = 0.1f;
    private const float MaxHeight = 1f;   // これより高い物は壁と同じ (高さはここで打ち切る)

    private static int _gen = -1;
    private static byte[] _bits;          // 1 画素 1 ビット (左下原点・行ごと・下位ビットから)
    private static int _w, _h;
    private static float _ox, _oy, _ppu;
    internal static string Stats { get; private set; } = "none";

    internal static bool Active
    {
        get
        {
            Ensure();
            return _bits != null;
        }
    }

    internal static void Ensure()
    {
        int gen = GameClock.ShipGen;
        if (gen == _gen) return;
        _gen = gen;
        _bits = null;
        if (!GameClock.ShipAlive) { Stats = "no ship"; return; }
        var ship = ShipStatus.Instance;
        if (!ship) { Stats = "no ship"; return; }
        string name = ship.name.Replace("(Clone)", "");
        try
        {
            using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MoreRolesPlus.Resources.WaterBarrier." + name + ".bin");
            if (s == null) { Stats = $"{name}: none"; return; }
            using var br = new BinaryReader(s);
            var magic = br.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2] || magic[3] != Magic[3]) { Stats = $"{name}: bad magic"; return; }
            int ver = br.ReadInt32();
            int w = br.ReadInt32(), h = br.ReadInt32();
            if (ver != Version) { Stats = $"{name}: version {ver}"; return; }
            float ox = br.ReadSingle(), oy = br.ReadSingle(), ppu = br.ReadSingle();
            // 水の計算に使うので、全員が同じ DLL の同じ地図を使う。ゲームの絵が作った時と違っても読み、確認用に印だけ残す
            // (絵の指紋は部屋の絵の読み込みの早さや端末の版で変わり、それで読む読まないが割れると結果が割れる)
            uint sig = br.ReadUInt32();
            bool stale = DamageMap.Ready() && sig != FloorMask.Signature(name);
            if (!SolidMap.Ensure() || MathF.Abs(SolidMap.Origin.x - ox) > OriginTolerance || MathF.Abs(SolidMap.Origin.y - oy) > OriginTolerance)
            {
                Stats = $"{name}: origin mismatch";
                return;
            }
            int len = br.ReadInt32();
            var bits = new byte[(w * h + 7) / 8];
            using (var z = new ZLibStream(new MemoryStream(br.ReadBytes(len)), CompressionMode.Decompress))
            {
                int read = 0;
                while (read < bits.Length)
                {
                    int n = z.Read(bits, read, bits.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != bits.Length) { Stats = $"{name}: short"; return; }
            }
            _bits = bits;
            _w = w; _h = h; _ox = ox; _oy = oy; _ppu = ppu;
            Stats = $"{name}: {w}x{h} ppu={ppu:0.##}{(stale ? " art changed" : "")}";
        }
        catch (Exception e)
        {
            Stats = $"{name}: {e.GetType().Name}";
            Plugin.Logger.LogError($"water barrier load failed: {e}");
        }
    }

    internal static float Ppu => _ppu;

    private static bool Px(int mx, int my)
    {
        if (mx < 0 || my < 0 || mx >= _w || my >= _h) return false;
        int k = my * _w + mx;
        return (_bits[k >> 3] & (1 << (k & 7))) != 0;
    }

    // 世界の点に止める絵があるか
    internal static bool At(float x, float y)
    {
        Ensure();
        return _bits != null && Px((int)MathF.Floor((x - _ox) * _ppu), (int)MathF.Floor((y - _oy) * _ppu));
    }

    // 縁の区間を越える水を止める物。横の縁 (d = 2 奥・3 手前) は縁の線 y の上を、x0..x1 の画素の列ごとに見る。
    // 縦の縁 (d = 0 右・1 左) は縁の線 x の上の画素を、y0..y1 の行ごとに見る (横を向いた柵は柱が横棒の裏に隠れて見えないので、絵のある行は止まる)。
    // blocked = 止まる列の割合 (/256)・height = 止まる列の物の高さの最大 (単位・MaxHeight で打ち切り)。地図が無ければ false
    internal static bool Edge(float x0, float y0, float x1, float y1, int d, out int blocked, out float height)
    {
        blocked = 0; height = 0f;
        Ensure();
        if (_bits == null) return false;
        // 縁は画素の境目に乗る (升 0.25 × 32 画素)。端末ごとの丸めで隣の画素へずれないよう、切り捨てでなく四捨五入で画素の境目へ寄せる
        int band = Math.Max(1, (int)(FootBand * _ppu)), maxRun = (int)(MaxHeight * _ppu);
        int cols = 0, hit = 0, best = 0;
        if (d >= 2)
        {
            int my = (int)MathF.Round((y0 - _oy) * _ppu);
            int a = (int)MathF.Round((x0 - _ox) * _ppu), b = (int)MathF.Round((x1 - _ox) * _ppu);
            for (int mx = a; mx < b; mx++)
            {
                cols++;
                int foot = int.MinValue;
                for (int t = -band; t <= band; t++) if (Px(mx, my + t)) { foot = my + t; break; }
                // 縁の線の下まで続く物 (縦の柱・管) は縁に立っていない
                if (foot == int.MinValue || foot == my - band && Px(mx, my - band - 1)) continue;
                hit++;
                int top = foot;
                while (top + 1 - my < maxRun && Px(mx, top + 1)) top++;
                best = Math.Max(best, top + 1 - my);
            }
        }
        else
        {
            int mx = (int)MathF.Round((x0 - _ox) * _ppu);
            int a = (int)MathF.Round((y0 - _oy) * _ppu), b = (int)MathF.Round((y1 - _oy) * _ppu);
            for (int my = a; my < b; my++)
            {
                cols++;
                int at = int.MinValue;
                for (int t = -band; t <= band; t++) if (Px(mx + t, my)) { at = mx + t; break; }
                // 縁の線の外まで横に続く物は縁に立っていない
                if (at == int.MinValue || Px(mx - band - 1, my) && Px(mx + band + 1, my)) continue;
                hit++;
                int top = my;
                while (top + 1 - my < maxRun && (Px(mx, top + 1) || Px(mx - 1, top + 1) || Px(mx + 1, top + 1))) top++;
                best = Math.Max(best, top + 1 - my);
            }
        }
        if (cols == 0) return true;
        blocked = hit * 256 / cols;
        height = best / _ppu;
        return true;
    }
}
