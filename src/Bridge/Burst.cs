using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace MoreRolesPlus.Bridge;

// 連写: 毎フレームの Update から ScreenCapture で PNG を撮り、Screens/burst_<時刻>/NNN_<経過ms>.png へ。
// 演出の GIF 用 (ファイル名の経過時間をそのまま各コマの長さに使う)。撮っている間は書き出しの分だけ fps が落ちるが、
// 演出は経過時間で動くので、間引かれたコマが並ぶだけで速さは変わらない。
// コルーチンは使わない (managed の IEnumerator を il2cpp の StartCoroutine に渡すと GCHandle が残る)
internal static class Burst
{
    private static string _dir;
    private static int _left, _every, _index, _frame;
    private static readonly Stopwatch Clock = new();

    public static void Register()
    {
        TestBridge.Register("burst", "<枚数 (≤240)> [何フレームおき=2] 連写を Screens/burst_<時刻>/ へ (GIF 用)。次の行のコマンドは撮り始めてから動く", (args, reply) =>
        {
            string[] p = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0 || !int.TryParse(p[0], out int n)) { reply("ERR burst needs <count> [every]"); return; }
            if (_left > 0) { reply("ERR burst busy"); return; }
#if ANDROID
            // Android の CaptureScreenshot は絶対パスを受け付けず (persistentDataPath からの相対で書く)、
            // ブリッジの置き場 (ランチャーが渡すアプリのフォルダ) に届かない。動画は adb screenrecord で撮る
            reply("ERR burst is PC only (Android: adb shell screenrecord)");
            return;
#endif
            _every = p.Length > 1 && int.TryParse(p[1], out int e) ? Math.Clamp(e, 1, 60) : 2;
            _left = Math.Clamp(n, 1, 240);
            _index = 0;
            _frame = 0;
            string folder = $"burst_{DateTime.Now:yyyyMMdd_HHmmss}";
            _dir = Path.Combine(TestBridge.ScreensDir, folder);
            Directory.CreateDirectory(_dir);
            Clock.Restart();
            reply($"OK burst {_left} every {_every} -> {_dir}");
        });
    }

    public static void Tick()
    {
        if (_left <= 0) return;
        if (_frame++ % _every != 0) return;
        ScreenCapture.CaptureScreenshot(Path.Combine(_dir, $"{_index++:D3}_{Clock.ElapsedMilliseconds:D6}ms.png"));
        if (--_left == 0) TestBridge.Log($"burst done {_index} -> {_dir}");
    }
}
