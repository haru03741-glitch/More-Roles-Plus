using System;
using System.Globalization;
using MoreRolesPlus.Bridge;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 確認用ブリッジコマンド「water」。小コマンドごとに 1 つの関数。
// どの小コマンドにも当たらなければ (引数なし・reset) 水の状態の 1 行を返す
internal static partial class WaterSim
{
    private const NumberStyles Num = NumberStyles.Float;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal static void Register()
    {
        TestBridge.Register("water", "[reset | prebase on|off | sweep start|check | spikes [ms|off] | flood [depth] | pour x y r [depth] | inflow x y [秒 (0=止まらない)] [速さ 0..1] | cell x0 x1 y | tile x y [up|dump] | tilez | fallsat x y [r] | show [r] | hide] 水の計算 (show = 自分の周りに判定を色で重ねる: 赤 = 水の升が閉じている・橙 = 歩けない所・黄 = 家具で水を見せない所): 刻み・遅れ・指紋・量 (reset = 水を全部消す)", (args, reply) =>
        {
            string a = args.Trim();
            // 預けた刻みを終わらせてから (確認用の操作は水の配列を直に読み書きする)
            TerrainStep.Settle();
            if (a == "reset") Reset();
            if (RunSubcommand(a, reply)) return;
            if (!_ready) { reply($"OK water off {GameClock.Describe()}"); return; }
            reply($"OK water native={Native.MrpNative.Loaded} step={_step} target={GameClock.Now - Delay} late={Late} latePlatform={LatePlatform} platform={(_plat ? _pfOff : -1)}/{PlatLog.Count} shocks={Shocks} splashes={Splashes} queue={Queue.Count} sources={Sources.Count} inflows={Inflows.Count} inflowed={InflowTotal} gushes={Gushes} particles={Particles} active={_actN} volume={Volume()} spilled={SpilledTotal} fell={FellTotal} void={VoidTotal} flying={Flying.Count} links={FallTo.Count} doors={System.Numerics.BitOperations.PopCount(_doorBits)}/{_doorUnder?.Length ?? 0} digest={Digest():x8} stepMs={LastStepMs:0.000} maxStepMs={MaxStepMs:0.000} grid={_w}x{_h} {GameClock.Describe()}");
        });
    }

    // 小コマンドを 1 つ実行したら true。名前は前方一致なので、長い名前を先に見る (floorcheck → flood → flow)
    private static bool RunSubcommand(string a, Action<string> reply)
    {
        if (a.StartsWith("sleep")) { Sleep(a, reply); return true; }
        if (a.StartsWith("prebase")) { Prebase(a, reply); return true; }
        if (a.StartsWith("zflow")) { ZFlowToggle(a, reply); return true; }
        if (a.StartsWith("floorcheck")) { reply($"OK water floorcheck {WaterArt.FloorCheck()}"); return true; }
        if (a.StartsWith("tilez")) { reply($"OK water tilez {WaterArt.TileZReport(reply)}"); return true; }
        if (a.StartsWith("tile")) { TileAt(a, reply); return true; }
        if (a.StartsWith("spikes")) { Spikes(a, reply); return true; }
        if (a.StartsWith("prof")) { Profile(a, reply); return true; }
        if (a.StartsWith("flood")) { FloodAll(a, reply); return true; }
        if (a.StartsWith("flow")) { FlowRow(a, reply); return true; }
        if (a.StartsWith("cell")) { CellRow(a, reply); return true; }
        if (a.StartsWith("pour")) { PourAt(a, reply); return true; }
        if (a.StartsWith("bench")) { Bench(a, reply); return true; }
        if (a.StartsWith("inflow")) { InflowAt(a, reply); return true; }
        if (a.StartsWith("fallsat")) { FallsAt(a, reply); return true; }
        if (a.StartsWith("show")) { reply(WaterDebug.Show(a.Length > 4 ? a.Substring(4).Trim() : "")); return true; }
        if (a == "hide") { WaterDebug.Hide(); reply("OK water hide"); return true; }
        if (a.StartsWith("barrier")) { Barrier(a, reply); return true; }
        if (a.StartsWith("line")) { DepthLine(a, reply); return true; }
        if (a == "doors") { WaterDebug.ListDoors(reply); return true; }
        if (a.StartsWith("sea")) { Sea(a, reply); return true; }
        if (a == "links") { LinksDump(reply); return true; }
        if (a.StartsWith("sweep")) { Sweep(a.Substring(5).Trim(), reply); return true; }
        if (a == "platuse" || a.StartsWith("platuse ")) { PlatformUse(a, reply); return true; }
        if (a.StartsWith("why")) { WhyNoFallAt(a, reply); return true; }
        if (a.StartsWith("furn")) { WaterDebug.ListFurniture(a.Length > 4 ? a.Substring(4).Trim() : "", reply); return true; }
        return false;
    }

    // 小コマンドの名前の後ろを空白で分けたもの
    private static string[] Rest(string a, int nameLength)
        => a.Substring(nameLength).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static void Sleep(string a, Action<string> reply)
    {
        if (a.EndsWith("off")) _noSleep = true;
        else if (a.EndsWith("on")) _noSleep = false;
        reply($"OK water sleep={!_noSleep} (this device only・水を消すと on に戻る)");
    }

    private static void Prebase(string a, Action<string> reply)
    {
        if (a.EndsWith("off")) _noWarmBase = true;
        else if (a.EndsWith("on")) _noWarmBase = false;
        reply($"OK water prebase={!_noWarmBase} based={_based} ready={_ready} (this device only・off = 準備段で升の土台を作らず、最初の破壊の前に作る)");
    }

    private static void ZFlowToggle(string a, Action<string> reply)
    {
        if (a.EndsWith("off")) ZFlow = false;
        else if (a.EndsWith("on")) ZFlow = true;
        reply($"OK water zflow={ZFlow} native={Native.MrpNative.Loaded} (this device only・水を消すと on に戻る)");
    }

    // 刻みの計算が閾値 (ms・既定 1) を超えたフレームの区間の内訳を記録に出す。off で止める
    private static void Spikes(string a, Action<string> reply)
    {
        var q = a.Substring(6).Trim();
        TerrainStep.SpikeMs = q == "off" ? 0 : double.TryParse(q, Num, Inv, out double sm) ? sm : 1.0;
        reply($"OK water spikes {(TerrainStep.SpikeMs > 0 ? $"{TerrainStep.SpikeMs:0.##}ms" : "off")}");
    }

    // 刻みごとの区間の平均時間 (ms)。reset で数え直す
    private static void Profile(string a, Action<string> reply)
    {
        double k = ProfSteps > 0 ? 1000.0 / System.Diagnostics.Stopwatch.Frequency / ProfSteps : 0;
        double perPass = LinkPasses > 0 ? 1000.0 / System.Diagnostics.Stopwatch.Frequency / LinkPasses : 0;
        double msPerTick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var t = ProfTicks;
        int sleeping = 0;
        for (int c = 0; _inList != null && c < _inList.Length; c++) if (_inList[c] == 0 && Counted(c)) sleeping++;
        reply($"OK water prof steps={ProfSteps} active={_actN} sleeping={sleeping} ms/step events={t[0] * k:0.000} doors={t[1] * k:0.000} jets={t[2] * k:0.000} blow={t[3] * k:0.000} sub={t[4] * k:0.000} subfalls={t[5] * k:0.000} falls={t[6] * k:0.000} tidy={t[7] * k:0.000}"
            + $" | per step pre={TerrainStep.ProfTicks[0] * k:0.000} decomp={TerrainStep.ProfTicks[1] * k:0.000} prop={TerrainStep.ProfTicks[2] * k:0.000} water={TerrainStep.ProfTicks[3] * k:0.000} fire={TerrainStep.ProfTicks[4] * k:0.000} burn={TerrainStep.ProfTicks[5] * k:0.000}"
            + $" | art tiles/step={(ProfSteps > 0 ? (double)WaterArt.ProfTiles / ProfSteps : 0):0.00} ms/tile {ArtProf()}"
            + $" | thread async={AsyncSteps} sync={SyncSteps} wait={JoinWaitTicks * k:0.000} early={EarlyJoins}"
            + $" | links n={LinkPasses} edges={LinkEdges} ms/pass scan={LinkProf[0] * perPass:0.000} ray={LinkProf[1] * perPass:0.000} post={LinkProf[2] * perPass:0.000}"
            + $" | floor queue={WaterArt.FloorFromQueue} dirty={WaterArt.FloorFromDirty} ms={WaterArt.FloorTicks * msPerTick:0.0} thread={WaterArt.FloorFromThread}/{WaterArt.FloorThreadTicks * msPerTick:0.0}ms");
        if (!a.Contains("reset")) return;
        Array.Clear(ProfTicks);
        Array.Clear(TerrainStep.ProfTicks);
        ProfSteps = 0;
        Array.Clear(WaterArt.ProfTicks);
        WaterArt.ProfTiles = 0;
        WaterArt.ProfThreadTiles = 0;
        ArtThreadTicks = FoamThreadTicks = FallThreadTicks = 0;
        AsyncSteps = SyncSteps = JoinWaitTicks = EarlyJoins = 0;
        Array.Clear(LinkProf);
        LinkPasses = LinkEdges = 0;
        WaterArt.FloorFromQueue = WaterArt.FloorFromDirty = WaterArt.FloorTicks = WaterArt.FloorFromThread = WaterArt.FloorThreadTicks = 0;
    }

    private static void FloodAll(string a, Action<string> reply)
    {
        int depth = a.Length > 5 && int.TryParse(a.Substring(5).Trim(), out int given) ? given : Full / 4;
        int n = Flood(depth);
        reply(n < 0 ? $"ERR water flood: {NoGrid()}" : $"OK water flood cells={n} depth={depth}");
    }

    // 確認用: その点を含む水の絵のタイルの状態
    private static void TileAt(string a, Action<string> reply)
    {
        var q = Rest(a, 4);
        if (q.Length < 2 || !float.TryParse(q[0], Num, Inv, out float x) || !float.TryParse(q[1], Num, Inv, out float y)) { reply("ERR water tile x y"); return; }
        if (q.Length > 2 && q[2] == "dump") { reply($"OK water {WaterArt.TileDump(x, y)}"); return; }
        reply($"OK water {WaterArt.TileInfo(x, y, q.Length > 2 && q[2] == "up")}");
    }

    // 確認用: 横一列 (step 升おき) の深さと流れの速さ (単位/秒・縁を越える分を含む・絵と同じ値)
    private static void FlowRow(string a, Action<string> reply)
    {
        var q = Rest(a, 4);
        if (_open == null || q.Length < 3 || !float.TryParse(q[0], Num, Inv, out float x0) || !float.TryParse(q[1], Num, Inv, out float x1) || !float.TryParse(q[2], Num, Inv, out float y))
        { reply("ERR water flow x0 x1 y [step]"); return; }
        int step = q.Length > 3 && int.TryParse(q[3], out int given) ? Math.Max(1, given) : 2;
        var sb = new System.Text.StringBuilder("OK water flow");
        int yy = (int)MathF.Floor((y - _org.y) / _cell);
        for (int xx = (int)MathF.Floor((x0 - _org.x) / _cell); xx <= (int)MathF.Floor((x1 - _org.x) / _cell); xx += step)
        {
            if (xx < 1 || yy < 1 || xx >= _w - 1 || yy >= _h - 1) continue;
            int k = yy * _w + xx;
            FlowThrough(k, out int qx, out int qy);
            float fs = 0.5f * _cell * GameClock.Hz / Math.Max(_hgt[k], Full / 8);
            sb.Append($" | {_org.x + (xx + 0.5f) * _cell:0.00}:h{_hgt[k]} v({qx * fs:0.00},{qy * fs:0.00})");
        }
        reply(sb.ToString());
    }

    // 横一列の升の中身: 開いているか・段・床の高さ・水・通れる縁 (右左上下の順・1 = 通れる)
    private static void CellRow(string a, Action<string> reply)
    {
        var q = Rest(a, 4);
        if (_open == null || q.Length < 3 || !float.TryParse(q[0], Num, Inv, out float x0) || !float.TryParse(q[1], Num, Inv, out float x1) || !float.TryParse(q[2], Num, Inv, out float y))
        { reply("ERR water cell x0 x1 y (no grid?)"); return; }
        var sb = new System.Text.StringBuilder("OK water cell");
        int yy = (int)MathF.Floor((y - _org.y) / _cell);
        for (int xx = (int)MathF.Floor((x0 - _org.x) / _cell); xx <= (int)MathF.Floor((x1 - _org.x) / _cell); xx++)
        {
            if (xx < 1 || yy < 1 || xx >= _w - 1 || yy >= _h - 1) continue;
            int k = yy * _w + xx;
            sb.Append($" | {_org.x + (xx + 0.5f) * _cell:0.##}:o{_open[k]}{(SkyIn(xx, yy) ? "s" : "")} l{_lvl[k]} b{_bed[k]}{(_furn[k] != 0 ? $" f{_furn[k]}" : "")} h{_hgt[k]} e");
            for (int d = 0; d < 4; d++) sb.Append(Passable(k, d) ? '1' : '0');
            if (_open[k] == 0) sb.Append(" F").Append(WaterArt.FillOf(k));
            if (_open[k] != 0)
                for (int d = 0; d < 4; d++)
                    if (!Passable(k, d)) sb.Append(' ').Append(d).Append('=').Append(Target(xx, yy, d, _lvl[k], out int to, out _, out _, out _)).Append(to == -1 ? "!" : "");
        }
        reply(sb.ToString());
    }

    private static void PourAt(string a, Action<string> reply)
    {
        var q = Rest(a, 4);
        if (q.Length < 3 || !float.TryParse(q[0], Num, Inv, out float px) || !float.TryParse(q[1], Num, Inv, out float py) || !float.TryParse(q[2], Num, Inv, out float pr))
        { reply("ERR water pour x y r [depth]"); return; }
        int depth = q.Length > 3 && int.TryParse(q[3], out int given) ? given : Full / 4;
        int n = Pour(px, py, pr, depth);
        reply(n < 0 ? $"ERR water pour: {NoGrid()}" : $"OK water pour cells={n} depth={depth}");
    }

    // 確認用: 水を消して丸く注ぎ、刻みを n 回その場で回して指紋と時間を返す (刻みの番号は 0 から・終わったら水を消す)。
    // 時刻に左右されないので、同じ地形なら端末や版をまたいで同じ指紋になる
    private static void Bench(string a, Action<string> reply)
    {
        var q = Rest(a, 5);
        if (q.Length < 4 || !int.TryParse(q[0], out int steps) || !float.TryParse(q[1], Num, Inv, out float bx) || !float.TryParse(q[2], Num, Inv, out float by) || !float.TryParse(q[3], Num, Inv, out float br))
        { reply("ERR water bench n x y r [depth]"); return; }
        int depth = q.Length > 4 && int.TryParse(q[4], out int given) ? given : Full;
        bool zflow = ZFlow;
        Reset();
        ZFlow = zflow;
        if (Pour(bx, by, br, depth) < 0) { reply($"ERR water bench: {NoGrid()}"); return; }
        _step = 0;
        Array.Clear(ProfTicks);
        ProfSteps = 0;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        long activeSum = 0;
        for (int i = 0; i < steps; i++)
        {
            activeSum += _actN;
            // 作業スレッドを使う時はゲーム中と同じく預けて投げて受け取る
            bool deferred = StepOnce(Native.MrpNative.Async);
            _step++;
            if (deferred) { Kick(); Settle(); }
        }
        double totalMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        double perStep = 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, steps);
        reply($"OK water bench steps={steps} digest={Digest():x8} volume={Volume()} active={_actN} avgActive={activeSum / Math.Max(1, steps)} fell={FellTotal} void={VoidTotal} native={Native.MrpNative.Loaded} zflow={ZFlow} ms/step={totalMs / Math.Max(1, steps):0.000} sub={ProfTicks[4] * perStep:0.000} tidy={ProfTicks[7] * perStep:0.000}");
        Reset();
        ZFlow = zflow;
    }

    // 浸水の口 (同期する・ホストか一人の時だけ): 秒数 0 = 止まらない
    private static void InflowAt(string a, Action<string> reply)
    {
        var q = Rest(a, 6);
        if (q.Length < 2 || !float.TryParse(q[0], Num, Inv, out float x) || !float.TryParse(q[1], Num, Inv, out float y)) { reply("ERR water inflow x y [seconds] [rate 0..1]"); return; }
        float seconds = q.Length > 2 && float.TryParse(q[2], Num, Inv, out float s) ? s : 30f;
        float rate = q.Length > 3 && float.TryParse(q[3], Num, Inv, out float r) ? r : 0.5f;
        var res = TerrainApi.WorldFlood(new Vector2(x, y), seconds, rate);
        reply(res.Ok ? $"OK water inflow {res.Why}" : $"ERR water inflow {res.Why}");
    }

    // 確認用: 点の近くの滝の縁 (x y [r])
    private static void FallsAt(string a, Action<string> reply)
    {
        var q = Rest(a, 7);
        if (q.Length < 2 || !float.TryParse(q[0], Num, Inv, out float x) || !float.TryParse(q[1], Num, Inv, out float y)) { reply("ERR water fallsat x y [r]"); return; }
        float r = q.Length > 2 && float.TryParse(q[2], Num, Inv, out float given) ? given : 1f;
        reply("OK water fallsat" + WaterFall.DescribeNear(x, y, r));
    }

    // 確認用: barrier [x y] [r] = 止める絵と落ちる縁の止まる割合を Screens/barrier.ppm へ (既定は自分の周り 4)
    private static void Barrier(string a, Action<string> reply)
    {
        var q = Rest(a, 7);
        var lp = PlayerControl.LocalPlayer;
        Vector2 at = lp ? (Vector2)lp.transform.position : Vector2.zero;
        float r = 4f;
        if (q.Length >= 2) at = new Vector2(float.Parse(q[0], Inv), float.Parse(q[1], Inv));
        if (q.Length == 1 || q.Length >= 3) r = float.Parse(q[q.Length == 1 ? 0 : 2], Inv);
        reply(BarrierView(at.x, at.y, r));
    }

    // 確認用: その点を通る横一列 (左右 n 升) の深さ
    private static void DepthLine(string a, Action<string> reply)
    {
        var q = Rest(a, 4);
        if (!_ready || q.Length < 2 || !float.TryParse(q[0], out float x) || !float.TryParse(q[1], out float y)) { reply("ERR water line x y [n]"); return; }
        int n = q.Length > 2 && int.TryParse(q[2], out int given) ? given : 12;
        int cx = (int)MathF.Floor((x - _org.x) / _cell), cy = (int)MathF.Floor((y - _org.y) / _cell);
        var sb = new System.Text.StringBuilder();
        for (int xx = cx - n; xx <= cx + n; xx++)
            sb.Append(xx < 0 || xx >= _w || cy < 0 || cy >= _h ? "-" : _hgt[cy * _w + xx].ToString()).Append(xx == cx ? "|" : " ");
        reply($"OK water line step={_step} {sb}");
    }

    // 確認用 (自分の手元だけ・同期しない): 海面を変える (水の深さの物差し・Full = 1 単位)
    private static void Sea(string a, Action<string> reply)
    {
        if (a.Length > 3 && int.TryParse(a.Substring(3).Trim(), out int level)) SeaLevel = level;
        reply($"OK water sea level={SeaLevel} out={SeaOutTotal} in={SeaInTotal} edges={(_seaMask == null ? 0 : CountBits(_seaMask))} bed={BedStats}");
    }

    // 確認用: 落ちる縁 (縁の升の真ん中・向き・落ちる先 / void) と、升ごとの段・島・開き・縁の書き出し (端末の突き合わせ用)
    private static void LinksDump(Action<string> reply)
    {
        if (!_ready) { reply("ERR water links: no grid"); return; }
        const string Dirs = "+x-x+y-y";
        var sb = new System.Text.StringBuilder();
        int c = 0;
        foreach (var kv in FallTo)
        {
            int k = kv.Key / 4, d = kv.Key % 4, to = kv.Value.To;
            sb.Append($"({_org.x + (k % _w + 0.5f) * _cell:0.0},{_org.y + (k / _w + 0.5f) * _cell:0.0}){Dirs.Substring(d * 2, 2)}");
            sb.Append(to < 0 ? ">void " : $">({_org.x + (to % _w + 0.5f) * _cell:0.0},{_org.y + (to / _w + 0.5f) * _cell:0.0}) ");
            if (++c % 8 == 0) { reply("LINKS " + sb); sb.Clear(); }
        }
        if (sb.Length > 0) reply("LINKS " + sb);
        sb.Clear();
        foreach (var kv in StepTo)
        {
            int k = kv.Key / 4, d = kv.Key % 4;
            sb.Append($"({_org.x + (k % _w + 0.5f) * _cell:0.00},{_org.y + (k / _w + 0.5f) * _cell:0.00}){Dirs.Substring(d * 2, 2)} ");
        }
        reply("STEPS " + sb);

        uint lvlHash = 2166136261, islHash = 2166136261;
        for (int k = 0; k < _w * _h; k++)
        {
            lvlHash = (lvlHash ^ (byte)_lvl[k]) * 16777619;
            islHash = (islHash ^ (_cellIsl == null ? 0u : _cellIsl[k])) * 16777619;
        }
        var dump = new byte[_w * _h * 4];
        for (int k = 0; k < _w * _h; k++)
        {
            dump[k * 4] = (byte)_lvl[k];
            dump[k * 4 + 1] = _cellIsl == null ? (byte)0 : _cellIsl[k];
            dump[k * 4 + 2] = _open[k];
            dump[k * 4 + 3] = _edge[k];
        }
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(TestBridge.ScreensDir, "waterlinks.bin"), dump);
        reply($"OK water links n={FallTo.Count} steps={StepTo.Count} lvl={lvlHash:x8} isl={islHash:x8} grid={GridHash():x8} w={_w} h={_h} org={_org.x:0.0000},{_org.y:0.0000} cell={_cell:0.000000}");
    }

    // 確認用: 昇降機を動かす (自分が乗る)
    // 昇降台を動かす。left / right を付けると、その側にいない時だけ動かす (試合ごとに始まる側が変わるので確認の前に揃える)
    private static void PlatformUse(string a, Action<string> reply)
    {
        var pf = FindPlatform();
        if (!pf) { reply("ERR no platform"); return; }
        string side = a.Length > 8 ? a.Substring(8).Trim() : "";
        bool moved = side switch { "left" => !pf.IsLeft, "right" => pf.IsLeft, _ => true };
        if (moved) pf.Use();
        reply($"OK water platuse moved={moved} left={pf.IsLeft}");
    }

    // 確認用: 範囲の縁の升が落ちない理由を向きごとに数える (4 つ目に "-y:scan" などを渡すとその升の位置を並べる。
    // scan = 先に開いた升が無い・same = 先が低くない・door・room = 谷の外で外壁の向こうの奈落・shadow = 影の線。
    // prop = すぐ隣が床に置かれた物・落ちる側は fall/side/void/sky = 手すりの外の空/cliff = 何も無い崖の下)
    private static void WhyNoFallAt(string a, Action<string> reply)
    {
        var q = Rest(a, 3);
        if (!_ready || q.Length < 3 || !float.TryParse(q[0], Num, Inv, out float x) || !float.TryParse(q[1], Num, Inv, out float y) || !float.TryParse(q[2], Num, Inv, out float r))
        { reply("ERR water why x y r"); return; }
        reply(WhyNoFall(x, y, r, q.Length > 3 ? q[3] : null));
    }
}
