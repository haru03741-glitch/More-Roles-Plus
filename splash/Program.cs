using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace MoreRolesPlus.Splash
{
    // 起動中の画面。ゲームの中の patcher (MoreRolesPlus.BootAccel の LoadingSplash) が起こし、
    // 標準入力へ 1 行ずつ今の段階を送ってくる:
    //   STAGE n      n 番目の段階に入った (1 環境 / 2 パッチャー / 3 MOD / 4 ゲームの開始)
    //   COUNT n      読み込む MOD の数
    //   PLUGIN name  その MOD を読み込み始めた
    //   LOG text     今の細かい様子 (BepInEx のログの最後の行)
    //   DONE         メニューまで来た (少し見せて閉じる)
    // 引数: ゲームのプロセス ID・言葉 (ja / en)・ゲームの exe (絵を借りる)・前の回の時間を覚えておくフォルダ
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            int pid = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 0;
            string lang = args.Length > 1 ? args[1] : "en";
            string gameExe = args.Length > 2 ? args[2] : "";
            string cacheDir = args.Length > 3 ? args[3] : "";

            Process game = null;
            try { if (pid > 0) game = Process.GetProcessById(pid); }
            catch { return 0; }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new SplashForm(game, lang == "ja", gameExe, cacheDir);

            // 標準入力は別の糸で読み、画面の糸へ渡す
            var reader = new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = Console.In.ReadLine()) != null) form.Post(line);
                }
                catch { }
                form.Post("EOF");
            }) { IsBackground = true, Name = "stdin" };
            reader.Start();

            Application.Run(form);
            return 0;
        }
    }
}
