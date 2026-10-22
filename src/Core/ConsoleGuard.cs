// Based on https://github.com/waffle-ful/Aeterna-End-K-not Modules/ConsoleGuard.cs and Modules/AsyncConsoleLog.cs (GPL-3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx;
using BepInEx.Logging;

namespace MoreRolesPlus;

// BepInEx のコンソール窓 (conhost) で文字が選択されている間、コンソールへの書き込み (WriteFile) は止まったまま待つ。
// 本来の ConsoleLogListener はメインスレッドから同期で書くので、窓をクリックしただけでゲームごと固まる。
// 2 段で防ぐ:
// - コンソールへの書き込みを専用スレッドへ移す (AsyncConsoleLog)。詰まっても止まるのはそのスレッドだけ。
// - 見張りのスレッドが 1 秒ごとに QuickEdit (左クリックで選択に入る) を切り直し、選択が 10 秒続いたら ESC を送って外す。
//   コンソールは後から作り直されることがあり、作り直された窓は QuickEdit が入っているので起動時の 1 回では足りない。
// 見張りと書き込みのスレッドは BepInEx の Logger を呼ばない (詰まったコンソールに自分も止められる)。
// 起きたことは数だけ持ち、メインスレッドの Health が 1 秒ごとに書き出す。
internal static class ConsoleGuard
{
    private const int StdInputHandle = -10;
    private const uint EnableQuickEditMode = 0x0040;
    private const uint EnableExtendedFlags = 0x0080;
    private const uint SelectionInProgress = 0x0001;
    private const uint SelectionNotEmpty = 0x0002;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const int VkEscape = 0x1B;
    private const int GraceSeconds = 10; // 範囲を選んで Enter でコピーする手間は残す
    private const int PollMs = 1000;

    private static Thread _watcher;
    private static int _reDisabled, _released, _stuck;
    private static int _seenReDisabled, _seenReleased, _seenStuck, _seenDropped;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleSelectionInfo(out SelectionInfo info);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct SelectionInfo
    {
        public uint Flags;
        public short AnchorX, AnchorY;
        public short Left, Top, Right, Bottom;
    }

    // Plugin.Load の最初に。結果は 1 行で返す (呼び出し元が書く)
    public static string Install()
    {
        if (!OperatingSystem.IsWindows()) return "skipped (not Windows)";
        string quick = TurnOffQuickEdit(out _) ? "QuickEdit off" : "QuickEdit unchanged";
        if (_watcher == null)
        {
            // 今コンソールが無くても回す (後から作られる窓も見張る)
            _watcher = new Thread(Watch) { IsBackground = true, Name = "MRP.ConsoleGuard" };
            _watcher.Start();
        }
        return $"{quick}, watcher on (grace {GraceSeconds}s), {AsyncConsoleLog.Install()}";
    }

    // QuickEdit が入っていたら切る。切った時だけ true
    private static bool TurnOffQuickEdit(out uint mode)
    {
        mode = 0;
        IntPtr h = GetStdHandle(StdInputHandle);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;
        if (!GetConsoleMode(h, out mode) || (mode & EnableQuickEditMode) == 0) return false;
        // ENABLE_EXTENDED_FLAGS を一緒に立てないと QuickEdit の変更が効かない
        return SetConsoleMode(h, (mode | EnableExtendedFlags) & ~EnableQuickEditMode);
    }

    private static bool Selecting(out uint flags)
    {
        flags = 0;
        if (!GetConsoleSelectionInfo(out var info)) return false;
        flags = info.Flags;
        return (flags & (SelectionInProgress | SelectionNotEmpty)) != 0;
    }

    private static void Watch()
    {
        int held = 0;
        bool warned = false;
        while (true)
        {
            try
            {
                Thread.Sleep(PollMs);
                if (TurnOffQuickEdit(out _)) Interlocked.Increment(ref _reDisabled);

                if (!Selecting(out _)) { held = 0; warned = false; continue; }
                if (++held < GraceSeconds) continue;
                IntPtr hwnd = GetConsoleWindow();
                if (hwnd == IntPtr.Zero) continue;

                // ESC で選択を抜ける (QuickEdit の選択にも Mark モードにも効く)
                PostMessage(hwnd, WmKeyDown, new IntPtr(VkEscape), IntPtr.Zero);
                PostMessage(hwnd, WmKeyUp, new IntPtr(VkEscape), IntPtr.Zero);
                Interlocked.Increment(ref _released);
                held = 0;

                Thread.Sleep(PollMs);
                if (!warned && Selecting(out _)) { warned = true; Interlocked.Increment(ref _stuck); }
            }
            catch
            {
                // 見張りは落とさない (固まりから戻る唯一の口)
            }
        }
    }

    // Health.Second (メインスレッド・1 秒ごと) から。増えた分だけ書く
    public static void Report()
    {
        if (_watcher == null) return;
        int re = Volatile.Read(ref _reDisabled), rel = Volatile.Read(ref _released), st = Volatile.Read(ref _stuck);
        int dr = AsyncConsoleLog.Dropped;
        if (re == _seenReDisabled && rel == _seenReleased && st == _seenStuck && dr - _seenDropped < 100) return;
        Plugin.Logger.LogWarning($"CONSOLEGUARD quickEditReDisabled={re} selectionReleased={rel} escapeNoEffect={st} droppedLines={dr}");
        _seenReDisabled = re; _seenReleased = rel; _seenStuck = st; _seenDropped = dr;
    }
}

// BepInEx の ConsoleLogListener の代わり。LogEvent は列に積むだけで待たない (一杯なら捨てる)。
// 書き込みは専用スレッド 1 本。ConsoleManager.ConsoleStream は窓の作り直しで替わるので毎回読む。
internal sealed class AsyncConsoleLog : ILogListener
{
    private const int Capacity = 4096;

    private static AsyncConsoleLog _installed;
    private static int _dropped;

    private readonly BlockingCollection<(string Line, LogLevel Level)> _queue = new(new ConcurrentQueue<(string, LogLevel)>(), Capacity);

    private AsyncConsoleLog(LogLevel filter)
    {
        LogLevelFilter = filter;
        new Thread(WriteLoop) { IsBackground = true, Name = "MRP.AsyncConsoleLog" }.Start();
    }

    public LogLevel LogLevelFilter { get; }

    public static int Dropped => Volatile.Read(ref _dropped);

    public void LogEvent(object sender, LogEventArgs eventArgs)
    {
        try
        {
            if (!_queue.TryAdd((eventArgs.ToStringLine(), eventArgs.Level))) Interlocked.Increment(ref _dropped);
        }
        catch
        {
            // ログの道の例外でゲームを巻き込まない
        }
    }

    public void Dispose()
    {
        try { _queue.CompleteAdding(); }
        catch { }
    }

    // 今の ConsoleLogListener を外して差し替える。失敗したら何も変えない
    public static string Install()
    {
        if (_installed != null) return "async console already";
        try
        {
            List<ILogListener> old = BepInEx.Logging.Logger.Listeners.Where(l => l is ConsoleLogListener).ToList();
            if (old.Count == 0) return "no console listener";
            var async = new AsyncConsoleLog(old[0].LogLevelFilter); // BepInEx.cfg の表示レベルを引き継ぐ
            BepInEx.Logging.Logger.Listeners.Add(async);
            foreach (var l in old)
            {
                BepInEx.Logging.Logger.Listeners.Remove(l);
                try { l.Dispose(); }
                catch { }
            }
            _installed = async;
            return "async console";
        }
        catch (Exception e) { return $"async console failed: {e.Message}"; }
    }

    private void WriteLoop()
    {
        foreach (var (line, level) in _queue.GetConsumingEnumerable())
        {
            try
            {
                var stream = ConsoleManager.ConsoleStream;
                if (stream == null) continue;
                ConsoleManager.SetConsoleColor(ColorFor(level));
                stream.Write(line);
                ConsoleManager.SetConsoleColor(ConsoleColor.Gray);
            }
            catch (ObjectDisposedException)
            {
                // 読んだ直後に窓が閉じられた (その 1 行だけ失う)
            }
            catch (IOException) { }
            catch
            {
                // 詰まった等。捨てて次へ
            }
        }
    }

    private static ConsoleColor ColorFor(LogLevel level)
    {
        if ((level & (LogLevel.Fatal | LogLevel.Error)) != 0) return ConsoleColor.Red;
        if ((level & LogLevel.Warning) != 0) return ConsoleColor.Yellow;
        if ((level & LogLevel.Message) != 0) return ConsoleColor.White;
        if ((level & LogLevel.Info) != 0) return ConsoleColor.Gray;
        return ConsoleColor.DarkGray;
    }
}
