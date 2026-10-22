using System;
using System.Collections.Generic;
using MoreRolesPlus.Fx;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 時戻し (壁の形): 壁を変える件ごとに「元に戻す記録」を取り、戻す合図で刻み Target 以後の件を新しい順に取り消す。
// 件は連番の順に全員が同じ物を適用するので、同じ件を同じ順で取り消せば全員が同じ壁に戻る。
// 記録するのは時戻しのいる試合だけ (TerrainRewind.On)。
//   - 当たり判定の線: 書き換える前の点・ずらし・有効を件の中で最初の 1 回だけ控える
//   - 作った物 (蓋・口・縁・切って足した線・箱の壁を置き換えた線): 取り消す時に止めてから消す (Destroy は同じフレームでは残り、問い合わせに出る)
//   - 耐久: 件の中で最初に触った前の値
//   - 大きな配列 (歩ける所の地図・損傷マスク): 書く所を全部探す代わりに、前の写しと件の終わりで比べ、変わった所の元の値を持つ。
//     件の頭で写しを今に揃える (件の外の書き込み = 火の焦げなどは件に入れない)
internal static class TerrainUndo
{
    private const int Chunk = 4096;

    private sealed class Run
    {
        public int Track, Start;
        public byte[] Old, New;
    }

    private sealed class Entry
    {
        public bool Live = true; // まだ取り消せる (取り消した・輪の外へ出た件は false)
        public ushort Seq;
        public int Tick;
        public ResolvedDamage R;
        public uint DigestBefore;
        public float Far; // 物ごと壊れた物の端までの距離 (水の升を作り直す範囲)
        public long Scorch; // 件の始まりの焦げの控えの通し番号 (件より後の焦げを先に戻す)
        public readonly List<Action> Undo = new();
        public readonly HashSet<int> Lines = new();
        public readonly Dictionary<long, int> Hp = new();
        public readonly List<Run> Runs = new();
    }

    // 比べる配列 (今の配列を毎回読む: 地図は作り直しや照合で入れ替わる)
    private sealed class Track
    {
        public Func<byte[]> Get;
        public Action<int, int> Dirty; // 書き戻した範囲 (添字の始まりと長さ)
        public byte[] Copy;
    }

    private static readonly Track[] Tracks =
    {
        new() { Get = () => SolidMap.OpenCells },
        new() { Get = () => SolidMap.IslandCells },
        new() { Get = () => SolidMap.OutsideCells },
        new() { Get = () => SolidMap.SealedCells },
        new() { Get = () => DamageMap.PixelBytes, Dirty = (s, n) => DamageMap.MarkDirtyBytes(s, n, 4) },
        new() { Get = () => DamageMap.GenBytes, Dirty = (s, n) => DamageMap.MarkDirtyBytes(s, n, 1) },
    };

    private static readonly List<Entry> Entries = new();
    private static Entry _cur;
    private static Entry _late; // 件の後で出す物 (次のフレームの見た目) を出している間の持ち主
    private static int _shipGen = -1;

    // 確認用
    internal static int Undone { get; private set; }
    internal static double LastBeginMs, LastEndMs, LastUndoMs, MaxEndMs;
    internal static int LastRuns, LastBytes;

    internal static bool Recording => _cur != null;

    // 取り消している合図の刻み (取り消しの中で各計算へ出来事を入れる時の刻み)
    internal static int UndoTick { get; private set; }

    // 今の件 (適用中か、その件の後回しの見た目を出している間)。後で作る物はこれを持ち主として持つ
    internal static object Token => _cur ?? _late;

    // 持ち主の件がまだ取り消せるか (取り消せる間は瓦礫を焼かずに置く)
    internal static bool Live(object owner) => owner is Entry e && e.Live;

    // 件の後回しの見た目 (TerrainFx.Defer) を、その件を持ち主にして出す
    internal static void RunAs(object owner, Action a)
    {
        var was = _late;
        _late = owner as Entry;
        try { a(); }
        finally { _late = was; }
    }

    // 取り消す時にすること (件の中の物を消す・数え直すなど)。件の後回しの中からも足せる
    internal static void OnUndo(Action a)
    {
        var e = _cur ?? _late;
        if (e == null || !e.Live) return;
        e.Undo.Add(a);
    }

    private static void CheckShip()
    {
        if (_shipGen == GameClock.ShipGen) return;
        _shipGen = GameClock.ShipGen;
        Clear();
    }

    internal static void Clear()
    {
        foreach (var e in Entries) e.Live = false;
        Entries.Clear();
        _cur = null;
        foreach (var t in Tracks) t.Copy = null;
        Undone = 0;
    }

    // 壁を変える件を適用する前 (ApplyNow)。時戻しのいない試合では何もしない
    internal static void Begin(ushort seq, in ResolvedDamage r)
    {
        _cur = null;
        // 壁を変える件と、壁に口を開けて水を噴く放水 (口は件ごとに外す)
        if (!TerrainRewind.On || !(r.Kind.Shapes() || r.Kind == DamageKind.Wall || r.Kind == DamageKind.Water)) return;
        CheckShip();
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int tick = GameClock.Expand(r.Tick);
        Prune(tick);
        // 地図は最初の破壊の中で作られることがある → 先に作って壊す前の姿を写す
        SolidMap.Ensure();
        DamageMap.Ready();
        WaterSim.EnsureIdle(); // 作業スレッドが地図を読んでいる間に写さない
        foreach (var t in Tracks) Sync(t);
        _cur = new Entry { Seq = seq, Tick = tick, R = r, DigestBefore = TerrainDigest.Running, Scorch = DamageMap.ScorchMark };
        LastBeginMs = Ms(t0);
    }

    // 適用の後。変わった配列の所を件に入れる
    internal static void End()
    {
        var e = _cur;
        _cur = null;
        if (e == null) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        WaterSim.EnsureIdle();
        int runs = 0, bytes = 0;
        for (int i = 0; i < Tracks.Length; i++) Diff(i, e, ref runs, ref bytes);
        e.Far = MathF.Ceiling(BreakableProps.LastBrokenFar * 64f) / 64f; // 格子に切り上げる (水の升の範囲が端末の末尾の差で割れないように)
        Entries.Add(e);
        LastRuns = runs; LastBytes = bytes;
        LastEndMs = Ms(t0);
        if (LastEndMs > MaxEndMs) MaxEndMs = LastEndMs;
    }

    // 線を書き換える前 (EdgeCutter)。件の中で最初の 1 回だけ控える
    internal static void Line(EdgeCollider2D col)
    {
        var e = _cur;
        if (e == null || !col) return;
        if (!e.Lines.Add(col.GetInstanceID())) return;
        var pts = col.points;
        var off = col.offset;
        bool on = col.enabled;
        e.Undo.Add(() =>
        {
            if (!col) return;
            col.offset = off;
            col.points = pts;
            col.enabled = on;
            DigestLine(col);
        });
    }

    // 件の中で作った物 (取り消す時に止めてから消す)
    internal static void Made(GameObject go)
    {
        var e = _cur ?? _late;
        if (e == null || !go) return;
        if (!e.Live) { go.SetActive(false); UnityEngine.Object.Destroy(go); return; } // 出す前に取り消された件の物
        e.Undo.Add(() =>
        {
            if (!go) return;
            go.SetActive(false);
            UnityEngine.Object.Destroy(go);
        });
    }

    internal static void Made(Collider2D c)
    {
        var e = _cur;
        if (e == null || !c) return;
        e.Lines.Add(c.GetInstanceID()); // 同じ件の中で後から切っても控えない (消すので)
        e.Undo.Add(() =>
        {
            if (!c) return;
            c.enabled = false;
            UnityEngine.Object.Destroy(c);
        });
    }

    // 有効を切り替える前 (置き換えた元の壁など)
    internal static void Enabled(Behaviour b)
    {
        var e = _cur;
        if (e == null || !b) return;
        bool on = b.enabled;
        e.Undo.Add(() => { if (b) b.enabled = on; });
    }

    // 耐久を書く前 (WallDurability)。had = 前に値があったか
    internal static void Hp(long key, bool had, int before)
    {
        var e = _cur;
        if (e == null || e.Hp.ContainsKey(key)) return;
        e.Hp[key] = had ? before : int.MinValue;
    }

    // 戻す合図 (ApplyNow の Rewind・全員の手元・連番の順)。刻み target 以後の件を新しい順に取り消し、取り消した数を返す。
    // 指紋は取り消した一番古い件の前の値へ戻し、戻した後の線と耐久を合図の件に積む (取り消しが端末ごとに割れたら指紋が割れる)
    internal static int Rewind(int target, int tick)
    {
        CheckShip();
        UndoTick = tick;
        int n = 0;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        WaterSim.EnsureIdle();
        uint digest = 0;
        for (int i = Entries.Count - 1; i >= 0 && Entries[i].Tick >= target; i--)
        {
            var e = Entries[i];
            // 火の焦げは件の外で書くので、この件より後に付いた分を先に戻す (同じ画素を件と焦げの両方が書いた時に古い値へ揃える)
            try { DamageMap.Unscorch(e.Scorch); }
            catch (Exception ex) { Plugin.Logger.LogError($"[TerrainUndo] unscorch seq={e.Seq}: {ex}"); }
            // 1 つが失敗しても残りと件の片付けは必ず通す (この端末だけ途中で止まると指紋と壁が割れる)
            for (int k = e.Undo.Count - 1; k >= 0; k--)
            {
                try { e.Undo[k](); }
                catch (Exception ex) { Plugin.Logger.LogError($"[TerrainUndo] seq={e.Seq}: {ex}"); }
            }
            try
            {
                foreach (var (key, hp) in e.Hp) WallDurability.Restore(key, hp);
                for (int k = e.Runs.Count - 1; k >= 0; k--) Put(e.Runs[k]);
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[TerrainUndo] restore seq={e.Seq}: {ex}"); }
            e.Live = false;
            try
            {
                // 持ち物 (動いている塊・焼く前の瓦礫) を消し、各計算に作り直しを入れる
                TerrainFx.DropOwned(e);
                RubbleBake.DropOwned(e);
                ShadowPatch.MarkDirty(e.R.Position, Reach(e.R) + e.Far + 0.5f);
                WaterSim.RewindReshape(e.R, tick, e.Far);
                Decompression.RewindRebuild(tick);
            }
            catch (Exception ex) { Plugin.Logger.LogError($"[TerrainUndo] after seq={e.Seq}: {ex}"); }
            digest = e.DigestBefore;
            Entries.RemoveAt(i);
            n++;
        }
        try { DamageMap.UnscorchFrom(target); }
        catch (Exception ex) { Plugin.Logger.LogError($"[TerrainUndo] unscorch: {ex}"); }
        if (n > 0)
        {
            SolidMap.TrimSealed();
            TerrainDigest.Restore(digest);
            Undone += n;
        }
        LastUndoMs = Ms(t0);
        return n;
    }

    // 時間で輪の外へ出た件の持ち物を手放す (取り消せる間だけ焼かずに置いていた瓦礫を焼けるように)。
    // 件そのものは消さない: 端末の時計で消すと、遅れた客とホストで取り消す件が割れる (件を消すのは件と合図の刻みだけ)
    internal static void Expire()
    {
        int old = GameClock.Now - (TerrainRewind.Slots + 1) * TerrainRewind.Every;
        foreach (var e in Entries)
        {
            if (e.Tick >= old) break;
            e.Live = false;
        }
    }

    // 輪より古い件は戻せないので捨てる
    private static void Prune(int tick)
    {
        int keep = (TerrainRewind.Slots + 1) * TerrainRewind.Every;
        int drop = 0;
        while (drop < Entries.Count && Entries[drop].Tick < tick - keep) drop++;
        for (int i = 0; i < drop; i++) Entries[i].Live = false;
        if (drop > 0) Entries.RemoveRange(0, drop);
    }

    // 件が触った範囲のおおよその半径 (影の中の絵の焼き直し)
    private static float Reach(in ResolvedDamage r) => r.Kind switch
    {
        DamageKind.Explosion => r.Size * 2.5f,
        DamageKind.Wall => r.Size * 0.5f + 0.5f,
        _ => 2f,
    };

    // 写しを今の配列に揃える (件の外で書かれた所を写しへ取り込む)
    private static void Sync(Track t)
    {
        var a = t.Get();
        if (a == null) { t.Copy = null; return; }
        if (t.Copy == null || t.Copy.Length != a.Length) { t.Copy = (byte[])a.Clone(); return; }
        for (int s = 0; s < a.Length; s += Chunk)
        {
            int len = Math.Min(Chunk, a.Length - s);
            var now = new ReadOnlySpan<byte>(a, s, len);
            if (!now.SequenceEqual(new ReadOnlySpan<byte>(t.Copy, s, len))) now.CopyTo(new Span<byte>(t.Copy, s, len));
        }
    }

    private static void Diff(int ti, Entry e, ref int runs, ref int bytes)
    {
        var t = Tracks[ti];
        var a = t.Get();
        // 件の中で初めて作られた配列 (口の向こうの升) は、無かった = 全部 0 として差分を取る
        if (a != null && t.Copy == null) t.Copy = new byte[a.Length];
        if (a == null || t.Copy.Length != a.Length) { Sync(t); return; }
        for (int s = 0; s < a.Length; s += Chunk)
        {
            int len = Math.Min(Chunk, a.Length - s);
            var now = new ReadOnlySpan<byte>(a, s, len);
            var was = new Span<byte>(t.Copy, s, len);
            if (now.SequenceEqual(was)) continue;
            e.Runs.Add(new Run { Track = ti, Start = s, Old = was.ToArray(), New = now.ToArray() });
            now.CopyTo(was);
            runs++;
            bytes += len;
        }
    }

    // 件が変えた画素だけを元に戻す (同じ塊の中で件の外に書かれた所はそのまま)
    private static void Put(Run r)
    {
        var t = Tracks[r.Track];
        var a = t.Get();
        if (a == null || r.Start + r.Old.Length > a.Length) return;
        int lo = int.MaxValue, hi = -1;
        for (int i = 0; i < r.Old.Length; i++)
        {
            if (r.Old[i] == r.New[i]) continue;
            a[r.Start + i] = r.Old[i];
            if (i < lo) lo = i;
            hi = i;
        }
        if (hi >= 0) t.Dirty?.Invoke(r.Start + lo, hi - lo + 1);
        if (t.Copy != null && t.Copy.Length == a.Length) Array.Copy(a, r.Start, t.Copy, r.Start, r.Old.Length);
    }

    private static void DigestLine(EdgeCollider2D col)
    {
        if (!col.enabled) return;
        var tr = col.transform;
        var off = col.offset;
        var pts = col.points;
        var world = new List<Vector2>(pts.Length);
        foreach (var p in pts) world.Add(tr.TransformPoint(FxMath.V2(p.x + off.x, p.y + off.y)));
        TerrainDigest.Chain(world);
    }

    private static double Ms(long t0) => (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private static readonly byte[][] PrintCopies = new byte[Tracks.Length][];

    // 確認用: 壁の今の姿の指紋 (配列ごと + 船の中の有効な線の和)。壊す前と取り消した後で比べる
    internal static string Print()
    {
        WaterSim.EnsureIdle();
        var sb = new System.Text.StringBuilder("arrays=");
        var diffs = new System.Text.StringBuilder();
        for (int ti = 0; ti < Tracks.Length; ti++)
        {
            var a = Tracks[ti].Get();
            ulong h = 1469598103934665603UL;
            if (a != null) foreach (byte b in a) h = (h ^ b) * 1099511628211UL;
            sb.Append($"{(uint)(h ^ (h >> 32)):x8},");
            // 前の print との差 (数と添字の範囲)
            var was = PrintCopies[ti];
            if (a != null && was != null && was.Length == a.Length)
            {
                int dn = 0, lo = -1, hi = -1;
                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] == was[i]) continue;
                    dn++;
                    if (lo < 0) lo = i;
                    hi = i;
                }
                if (dn > 0) diffs.Append($" d{ti}={dn}@{lo}..{hi}");
            }
            else if ((a == null) != (was == null)) diffs.Append($" d{ti}={(a == null ? "null" : "new")}");
            PrintCopies[ti] = (byte[])a?.Clone();
        }
        sb.Append(diffs);
        ulong lines = 0;
        int n = 0;
        var ship = ShipStatus.Instance;
        if (ship)
            foreach (var c in ship.GetComponentsInChildren<Collider2D>(true))
            {
                if (!c || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                int layer = c.gameObject.layer;
                if (layer != 9 && layer != 10) continue;
                var e = c.TryCast<EdgeCollider2D>();
                if (!e) continue;
                var tr = e.transform;
                var off = e.offset;
                ulong h = 0x9E3779B97F4A7C15UL;
                foreach (var p in e.points)
                {
                    Vector2 w = tr.TransformPoint(FxMath.V2(p.x + off.x, p.y + off.y));
                    h = (h ^ (ulong)(uint)(int)MathF.Round(w.x * 64f) ^ ((ulong)(uint)(int)MathF.Round(w.y * 64f) << 32)) * 0xBF58476D1CE4E5B9UL;
                }
                lines += h ^ (ulong)layer;
                n++;
            }
        sb.Append($" lines={(uint)(lines ^ (lines >> 32)):x8} n={n}");
        return sb.ToString();
    }

    internal static string Probe() =>
        $"entries={Entries.Count} undone={Undone} begin={LastBeginMs:0.000}ms end={LastEndMs:0.000}ms (max {MaxEndMs:0.000}) undo={LastUndoMs:0.000}ms runs={LastRuns} bytes={LastBytes}";
}
