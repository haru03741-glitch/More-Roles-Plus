using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using MrpInstaller.Core;

namespace MrpInstaller;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Discord の「参加する」から (mrp://join/<コード>)。画面は出さず、困った時だけ知らせる
        int join = Array.IndexOf(e.Args, "--join");
        if (join >= 0)
        {
            string msg = JoinLink.Join(join + 1 < e.Args.Length ? e.Args[join + 1] : null);
            if (msg != null) MessageBox.Show(msg, "More Roles Plus", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(msg == null ? 0 : 1);
            return;
        }
        // インストール式 (MoreRolesPlusSetup.exe): このPCへ入れて、入れた方を開く / 「アプリ」から消す
        if (SelfSetup.WantsUninstall(e.Args))
        {
            Shutdown(SelfSetup.Uninstall(e.Args.Contains("--quiet")));
            return;
        }
        if (SelfSetup.Wants(e.Args))
        {
            Shutdown(SelfSetup.Install(e.Args.Contains("--quiet")));
            return;
        }
        if (Cli.Wants(e.Args))
        {
            int code = Cli.Run(e.Args);
            Shutdown(code);
            return;
        }
        // 自分を更新した後は、退けておいた古い exe を消す
        ReleaseCheck.CleanupOld();
        JoinLink.Register();
        SelfSetup.RefreshEntry();
        if (e.Args.Contains("--tour"))
        {
            Settings.Current.TourDone = false;
            Settings.Current.Save();
        }
        DispatcherUnhandledException += OnError;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => WriteError(a.ExceptionObject as Exception);
        var w = new UI.MainWindow(e.Args.Contains("--updated"));
        MainWindow = w;
        w.Show();
    }

    // 思わぬエラーは %AppData%\MoreRolesPlusInstaller\error.log に残し、画面は落とさない
    private void OnError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        WriteError(e.Exception);
        try { UI.Dialog.Show(MainWindow, Lang.T("log.error", e.Exception.Message), e.Exception.GetType().Name, false); }
        catch (Exception again) { WriteError(again); }
    }

    public static void WriteError(Exception e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Settings.Dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(Settings.Dir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e}\r\n\r\n");
        }
        catch { }
    }
}
