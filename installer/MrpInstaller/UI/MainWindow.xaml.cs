using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MrpInstaller.Core;

namespace MrpInstaller.UI;

public sealed class GameItem
{
    public GameItem(GameInstall g) { Game = g; }
    public GameInstall Game { get; }
    public string Title => "Among Us · " + Game.PlatformLabel;
    public string Path => Game.Path;
    public string Tip => Lang.T("tip.game");
    public string PlatformShort => Game.IsCopy ? "MRP" : Game.Platform switch { GamePlatform.Steam => "ST", GamePlatform.Epic => "EG", _ => "AU" };
    public Brush PlatformBrush => Game.IsCopy ? MainWindow.Res("BrandGrad") : Game.Platform switch
    {
        GamePlatform.Steam => new SolidColorBrush(Color.FromRgb(0x1B, 0x28, 0x38)),
        GamePlatform.Epic => new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
        _ => MainWindow.Res("Card2"),
    };
    public Brush PlatformFg => Game.IsCopy ? MainWindow.Res("AccentText") : Brushes.White;
    public string StateText => Lang.T("state." + Game.State);
    public Brush StateFg => MainWindow.StateColor(Game.State);
    public Brush StateBg => new SolidColorBrush(((SolidColorBrush)StateFg).Color) { Opacity = 0.16 };
}

public partial class MainWindow : Window
{
    private List<GameInstall> _games = new();
    private bool _busy;
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private bool _wasRunning;
    private readonly bool _justUpdated;

    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public static Brush StateColor(InstallState s) => s switch
    {
        InstallState.Mrp => Res("Accent"),
        InstallState.MrpDisabled => Res("Sub"),
        InstallState.OtherMod => Res("Yellow"),
        InstallState.Broken => Res("Red"),
        _ => Res("Cyan"),
    };

    public MainWindow(bool justUpdated = false)
    {
        _justUpdated = justUpdated;
        InitializeComponent();
        SourceInitialized += (_, _) => RoundCorners();
        StateChanged += (_, _) =>
        {
            MaxGlyph.Text = WindowState == WindowState.Maximized ? "" : "";
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        };
        PreviewKeyDown += Tour_Key;
        SizeChanged += (_, _) => TourRelayout();
        Shell.Opacity = 0;
        Loaded += async (_, _) =>
        {
            ApplyLanguage();
            LoadInstallerSettings();
            FillAbout();
            Rescan();
            PlayIntro();
            _watch.Tick += (_, _) => WatchGame();
            _watch.Start();
            if (_justUpdated)
            {
                string msg = Lang.T("upd.updated", "v" + ReleaseCheck.InstallerVersion.ToString(3));
                Log(msg);
                SetStatus(msg);
            }
            if (!Settings.Current.TourDone)
            {
                await Task.Delay(550);
                StartTour();
            }
            if (Settings.Current.CheckUpdateOnStart) await CheckUpdate(true);
        };
    }

    // Windows 11 の角丸と暗いタイトル
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private void RoundCorners()
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            int round = 2, dark = 1;
            DwmSetWindowAttribute(h, 33, ref round, 4);
            DwmSetWindowAttribute(h, 20, ref dark, 4);
        }
        catch { }
    }

    private GameInstall Sel => (Games.SelectedItem as GameItem)?.Game;

    // ---------- 言葉 ----------

    // ToolTip="L:キー" は 1 回目に本文へ置き換えるので、キーはここに覚えておく (言葉を切り替えた時に使う)
    private readonly Dictionary<FrameworkElement, string> _tipKeys = new();

    private void ApplyLanguage()
    {
        Walk(this);
        SideVersion.Text = $"Installer v{ReleaseCheck.InstallerVersion.ToString(3)}";
        LangJa.Opacity = Lang.Code == "ja" ? 1 : 0.5;
        LangEn.Opacity = Lang.Code == "en" ? 1 : 0.5;
        BannerSub.Text = $"{Lang.T("home.payload")}: v{Payload.MrpVersion}  ·  {Payload.MrpTime:yyyy/MM/dd HH:mm}";
        UpdatePageTitle();
        RefreshList();
        RestoreStatus();
    }

    private void Walk(DependencyObject o)
    {
        if (o is FrameworkElement fe)
        {
            if (fe.Tag is string tag && tag.StartsWith("L:"))
            {
                string text = Lang.T(tag.Substring(2));
                if (o is TextBlock tb) tb.Text = text;
                else if (o is ContentControl cc) cc.Content = text;
            }
            if (fe.ToolTip is string tip && tip.StartsWith("L:")) _tipKeys[fe] = tip.Substring(2);
            if (_tipKeys.TryGetValue(fe, out string key)) fe.ToolTip = Lang.T(key);
        }
        foreach (object child in LogicalTreeHelper.GetChildren(o))
            if (child is DependencyObject d) Walk(d);
    }

    private void SetLanguage(string code)
    {
        Settings.Current.Language = code;
        Settings.Current.Save();
        ApplyLanguage();
        ShowRelease();
        ShowUpdateBanner();
        if (Tour.Visibility == Visibility.Visible) ShowTourStep(_tourStep, false);
    }

    private void LangJa_Click(object sender, RoutedEventArgs e) => SetLanguage("ja");
    private void LangEn_Click(object sender, RoutedEventArgs e) => SetLanguage("en");

    // ---------- 画面の切り替え ----------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHome == null) return;
        FrameworkElement show = NavTools.IsChecked == true ? PageTools : NavBackups.IsChecked == true ? PageBackups
            : NavSettings.IsChecked == true ? PageSettings : NavAbout.IsChecked == true ? PageAbout : PageHome;
        foreach (var page in new FrameworkElement[] { PageHome, PageTools, PageBackups, PageSettings, PageAbout })
            if (page != show) page.Visibility = Visibility.Collapsed;
        if (show.Visibility != Visibility.Visible)
        {
            show.Visibility = Visibility.Visible;
            Anim.SlideIn(show, 0, 16);
        }
        UpdatePageTitle();
        if (show == PageTools) LoadLog();
        if (show == PageBackups) FillBackups();
        if (show == PageSettings) LoadGameSettings();
    }

    private void UpdatePageTitle()
    {
        string page = NavTools.IsChecked == true ? "nav.tools" : NavBackups.IsChecked == true ? "nav.backups" : NavSettings.IsChecked == true ? "nav.settings" : NavAbout.IsChecked == true ? "nav.about" : "nav.home";
        PageTitle.Text = "More Roles Plus Installer  ·  " + Lang.T(page);
        string target = Sel != null ? "→ " + Sel : Lang.T("home.select");
        ToolsTarget.Text = Lang.T("tools.hint") + "  " + target;
        BackupsTarget.Text = target;
        SettingsTarget.Text = target;
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Help_Click(object sender, RoutedEventArgs e) => StartTour();

    // ---------- ゲームの一覧 ----------

    private void Rescan()
    {
        string keep = Sel?.Path ?? Settings.Current.LastSelected;
        _games = GameDetector.Detect();
        RefreshList(keep);
        Anim.Stagger(Games);
        SetStatus(_games.Count == 0 ? Lang.T("home.noGame").Split('\n')[0] : $"{Lang.T("home.ready")} · {_games.Count} Among Us");
        ShowUpdateBanner();
    }

    private bool _refreshing;

    private void RefreshList(string keep = null)
    {
        keep ??= Sel?.Path;
        foreach (var g in _games) g.Refresh();
        var items = _games.Select(g => new GameItem(g)).ToList();
        _refreshing = true;
        Games.ItemsSource = items;
        NoGames.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Games.SelectedItem = items.FirstOrDefault(i => string.Equals(i.Path, keep, StringComparison.OrdinalIgnoreCase))
                             ?? items.FirstOrDefault(i => i.Game.State is InstallState.Mrp or InstallState.MrpDisabled)
                             ?? items.FirstOrDefault();
        _refreshing = false;
        ShowDetail();
    }

    private void Games_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        if (Sel != null)
        {
            Settings.Current.LastSelected = Sel.Path;
            Settings.Current.Save();
        }
        ShowDetail();
        Anim.Fade(Detail, 0, 1, 220);
        Anim.Move(DetailShift, TranslateTransform.XProperty, 12, 0, 260);
        UpdatePageTitle();
        if (PageTools.Visibility == Visibility.Visible) LoadLog();
        if (PageBackups.Visibility == Visibility.Visible) FillBackups();
        if (PageSettings.Visibility == Visibility.Visible) LoadGameSettings();
    }

    private void ShowDetail()
    {
        var g = Sel;
        Detail.Visibility = g == null ? Visibility.Collapsed : Visibility.Visible;
        NoSelection.Visibility = g == null ? Visibility.Visible : Visibility.Collapsed;
        if (g == null) return;

        DTitle.Text = "Among Us · " + g.PlatformLabel;
        DPath.Text = g.Path;
        DState.Text = Lang.T("state." + g.State);
        DState.Foreground = StateColor(g.State);
        DStateBox.Background = new SolidColorBrush(((SolidColorBrush)StateColor(g.State)).Color) { Opacity = 0.16 };

        bool has = g.State is InstallState.Mrp or InstallState.MrpDisabled;
        bool current = has && g.MrpIsCurrent;
        var ver = ReleaseCheck.FileVersionOf(g.MrpDllPath);
        IMrp.Text = g.MrpBuilt.HasValue
            ? $"{(ver != null ? "v" + ver.ToString(3) + "  ·  " : "")}{g.MrpBuilt:yyyy/MM/dd HH:mm}  ·  {ModInstaller.Size(g.MrpSize)}  ·  {(current ? "✓ " + Lang.T("home.uptodate") : Lang.T("home.outdated"))}"
            : "—";
        IMrp.Foreground = current ? Res("Accent") : Res("Text");
        IPayload.Text = $"v{Payload.MrpVersion}  ·  {Payload.MrpTime:yyyy/MM/dd HH:mm}";
        IAccel.Text = g.HasPatcher ? "ON" : File.Exists(g.PatcherDisabledPath) ? "OFF" : "—";
        IOthers.Text = g.OtherPlugins.Count == 0 ? "—" : string.Join(", ", g.OtherPlugins);
        IGame.Text = g.GameUpdated.HasValue ? $"{g.GameUpdated:yyyy/MM/dd HH:mm}" + (g.Is64Bit ? "  ·  x64" : "  ·  x86") : "—";

        var warns = new List<string>();
        if (ModInstaller.GameRunning()) warns.Add(Lang.T("home.runningWarn"));
        if (!g.Is64Bit) warns.Add(Lang.T("home.x86Warn"));
        if (g.State == InstallState.OtherMod) warns.Add(Lang.T("home.otherWarn", g.OtherPlugins.Count > 0 ? string.Join(", ", g.OtherPlugins.Take(4)) : "BepInEx"));
        WarnBox.Visibility = warns.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarnText.Text = string.Join("\n", warns);

        BtnInstallText.Text = Lang.T(!has && g.State != InstallState.Broken ? "home.install" : current ? "home.reinstall" : "home.update");
        BtnInstall.IsEnabled = !_busy && g.Is64Bit;
        BtnLaunch.IsEnabled = !_busy;
        BtnToggle.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        BtnToggleText.Text = Lang.T(g.State == InstallState.MrpDisabled ? "home.enable" : "home.disable");
        BtnToggle.IsEnabled = !_busy;
        BtnUninstall.Visibility = g.State is InstallState.NotInstalled ? Visibility.Collapsed : Visibility.Visible;
        BtnUninstall.IsEnabled = !_busy;
        BtnRemove.Visibility = Settings.Current.ExtraFolders.Any(d => d.Equals(g.Path, StringComparison.OrdinalIgnoreCase)) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => Rescan();

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        string dir = PickFolder(Lang.T("dlg.pickGame"));
        if (dir == null) return;
        if (!File.Exists(System.IO.Path.Combine(dir, "Among Us.exe")))
        {
            Dialog.Show(this, Lang.T("dlg.notGame"), null, false);
            return;
        }
        Settings.Current.AddFolder(dir);
        Rescan();
        Games.SelectedItem = Games.Items.Cast<GameItem>().FirstOrDefault(i => i.Path.Equals(System.IO.Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Sel == null) return;
        Settings.Current.RemoveFolder(Sel.Path);
        Rescan();
    }

    private void DPath_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Sel != null) Tools.Open(Sel.Path);
    }

    private static string PickFolder(string description)
    {
        using var d = new System.Windows.Forms.FolderBrowserDialog { Description = description, ShowNewFolderButton = true };
        return d.ShowDialog() == System.Windows.Forms.DialogResult.OK ? d.SelectedPath : null;
    }

    // ---------- 動作 ----------

    private void Log(string text)
    {
        LogList.Items.Add($"{DateTime.Now:HH:mm:ss}  {text}");
        while (LogList.Items.Count > 200) LogList.Items.RemoveAt(0);
        LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    // 重い作業を裏で回し、進み具合を下の帯とログに出す。書き込めない時は管理者で開き直すか聞く
    private async Task<bool> Run(Action<ModInstaller.Report> work)
    {
        if (_busy) return false;
        _busy = true;
        ShowDetail();
        SetProgress(0);
        string last = null;
        ModInstaller.Report report = (f, m) => Dispatcher.BeginInvoke(new Action(() =>
        {
            SetProgress(f);
            SetStatus(m.Trim());
            // ファイル 1 つずつの細かい行はログに出さない
            if (!m.StartsWith("  ") && m != last) { Log(m); last = m; }
        }));
        try
        {
            await Task.Run(() => work(report));
            SetProgress(1);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log(Lang.T("log.error", ex.Message));
            if (!Tools.IsAdmin() && Dialog.Show(this, Lang.T("dlg.admin")) && Tools.RestartAsAdmin()) Close();
            return false;
        }
        catch (Exception ex)
        {
            if (ex is AggregateException { InnerException: { } inner }) ex = inner;
            string msg = ex is InstallException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
            Log(Lang.T("log.error", msg));
            SetStatus(Lang.T("log.error", msg));
            Dialog.Show(this, msg, Lang.T("log.error", ""), false);
            return false;
        }
        finally
        {
            _busy = false;
            RefreshList();
            ShowUpdateBanner();
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var g = Sel;
        if (g == null) return;
        if (await Run(r => ModInstaller.Install(g, r))) Anim.Pop(DStateBox);
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        var g = Sel;
        if (g == null) return;
        try
        {
            Tools.Launch(g);
            Log(Lang.T("log.launching"));
            SetStatus(Lang.T("log.launching"));
            if (Settings.Current.CloseAfterLaunch) Close();
        }
        catch (Exception ex) { Dialog.Show(this, ex.Message, null, false); }
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        var g = Sel;
        if (g == null) return;
        bool on = false;
        if (await Run(_ => on = ModInstaller.Toggle(g)))
        {
            Log(Lang.T(on ? "log.toggledOn" : "log.toggledOff"));
            SetStatus(Lang.T(on ? "log.toggledOn" : "log.toggledOff"));
            Anim.Pop(DStateBox);
        }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var g = Sel;
        if (g == null || !Dialog.Show(this, Lang.T("dlg.uninstall", g), null, true, true)) return;
        bool restore = g.Backups.Any(b => b.EndsWith("_other", StringComparison.OrdinalIgnoreCase)) && Dialog.Show(this, Lang.T("dlg.restoreOther"));
        if (await Run(r => ModInstaller.Uninstall(g, Settings.Current.KeepBackups, restore, r))) Anim.Pop(DStateBox);
    }

    private void WatchGame()
    {
        bool running = ModInstaller.GameRunning();
        if (running == _wasRunning) return;
        _wasRunning = running;
        ShowDetail();
        if (!running && PageTools.Visibility == Visibility.Visible) LoadLog();
    }

    // ---------- ツール ----------

    private bool NeedGame()
    {
        if (Sel != null) return true;
        Dialog.Show(this, Lang.T("home.select"), null, false);
        return false;
    }

    private void OpenGame_Click(object sender, RoutedEventArgs e) { if (NeedGame()) Tools.Open(Sel.Path); }
    private void OpenPlugins_Click(object sender, RoutedEventArgs e) { if (NeedGame()) Tools.Open(Sel.PluginsDir); }
    private void OpenConfig_Click(object sender, RoutedEventArgs e) { if (NeedGame()) Tools.Open(Sel.P("BepInEx", "config")); }
    private void OpenLang_Click(object sender, RoutedEventArgs e) { if (NeedGame()) Tools.Open(Sel.LanguageDir); }
    private void OpenLog_Click(object sender, RoutedEventArgs e) { if (NeedGame()) Tools.Select(Sel.LogPath); }

    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame()) return;
        var g = Sel;
        long freed = 0;
        if (await Run(_ => freed = ModInstaller.ClearCache(g))) { Log(Lang.T("log.cacheCleared", ModInstaller.Size(freed))); SetStatus(Lang.T("log.cacheCleared", ModInstaller.Size(freed))); }
    }

    private async void Diag_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame()) return;
        var g = Sel;
        string path = null;
        if (await Run(_ => path = Tools.Diagnostics(g)))
        {
            Log(Lang.T("log.diag", path));
            SetStatus(Lang.T("log.diag", path));
            Tools.Select(path);
        }
    }

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame()) return;
        try
        {
            string path = Tools.Shortcut(Sel);
            Log(Lang.T("log.shortcut", path));
            SetStatus(Lang.T("log.shortcut", path));
        }
        catch (Exception ex) { Dialog.Show(this, ex.Message, null, false); }
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedGame()) return;
        var src = Sel;
        string dest = PickFolder(Lang.T("dlg.copyDest"));
        if (dest == null) return;
        GameInstall copy = null;
        await Run(r =>
        {
            ModInstaller.CopyGame(src.Path, dest, r);
            r(1, Lang.T("log.copyDone"));
            copy = new GameInstall(dest, GamePlatform.Steam, null, true);
            ModInstaller.Install(copy, r);
        });
        if (copy != null)
        {
            Settings.Current.AddFolder(dest, true);
            Rescan();
            Games.SelectedItem = Games.Items.Cast<GameItem>().FirstOrDefault(i => i.Path.Equals(copy.Path, StringComparison.OrdinalIgnoreCase));
            NavHome.IsChecked = true;
        }
    }

    private void LoadLog()
    {
        var g = Sel;
        string text = g == null ? "" : Tools.ReadLogTail(g, 600);
        if (text == null) { LogView.Text = Lang.T("tools.logNone"); return; }
        if (LogErrors.IsChecked == true)
            text = string.Join("\n", text.Split('\n').Where(l => l.Contains("[Error") || l.Contains("[Fatal") || l.Contains("[Warning") || l.Contains("Exception")));
        LogView.Text = text;
        LogView.ScrollToEnd();
    }

    private void LogRefresh_Click(object sender, RoutedEventArgs e) => LoadLog();
    private void LogFilter_Changed(object sender, RoutedEventArgs e) => LoadLog();

    // ---------- バックアップ ----------

    private void FillBackups()
    {
        BackupList.Children.Clear();
        var g = Sel;
        g?.Refresh();
        var list = g?.Backups ?? new List<string>();
        NoBackups.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int i = 0;
        foreach (string dir in list)
        {
            string name = System.IO.Path.GetFileName(dir);
            bool other = name.EndsWith("_other", StringComparison.OrdinalIgnoreCase);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var badge = new Border
            {
                Width = 44, Height = 44, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center,
                Background = other ? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xD2, 0x3F)) : new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0xE6, 0x76)),
                Child = new TextBlock { Text = other ? "MOD" : "MRP", FontWeight = FontWeights.Black, FontSize = 11, Foreground = other ? Res("Yellow") : Res("Accent"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            row.Children.Add(badge);
            var info = new StackPanel { Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            DateTime.TryParseExact(name.Split('_')[0], "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.None, out var when);
            info.Children.Add(new TextBlock { Text = when == default ? name : when.ToString("yyyy/MM/dd HH:mm:ss"), FontWeight = FontWeights.SemiBold, FontSize = 14 });
            info.Children.Add(new TextBlock { Text = ModInstaller.BackupDescription(dir) + "  ·  " + ModInstaller.Size(ModInstaller.DirSize(dir)), Style = (Style)FindResource("Hint") });
            Grid.SetColumn(info, 1);
            row.Children.Add(info);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var restore = new Button { Content = Lang.T("bak.restore"), Margin = new Thickness(0, 0, 8, 0), ToolTip = Lang.T("dlg.restore", name) };
            restore.Click += async (_, _) =>
            {
                if (!Dialog.Show(this, Lang.T("dlg.restore", name))) return;
                var game = Sel;
                await Run(r => ModInstaller.Restore(game, dir, r));
                FillBackups();
            };
            var open = new Button { Content = Lang.T("bak.open"), Style = (Style)FindResource("Ghost"), Margin = new Thickness(0, 0, 8, 0), ToolTip = dir };
            open.Click += (_, _) => Tools.Open(dir);
            var del = new Button { Content = Lang.T("bak.delete"), Style = (Style)FindResource("Danger"), ToolTip = Lang.T("dlg.deleteBackup", name) };
            del.Click += (_, _) =>
            {
                if (!Dialog.Show(this, Lang.T("dlg.deleteBackup", name), null, true, true)) return;
                try { ModInstaller.DeleteBackup(dir); } catch (Exception ex) { Dialog.Show(this, ex.Message, null, false); }
                FillBackups();
                RefreshList();
            };
            buttons.Children.Add(restore);
            buttons.Children.Add(open);
            buttons.Children.Add(del);
            Grid.SetColumn(buttons, 2);
            row.Children.Add(buttons);
            var card = new Border { Style = (Style)FindResource("CardBox"), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 10), Child = row };
            BackupList.Children.Add(card);
            Anim.SlideIn(card, i++ * 50, 10);
        }
    }

    // ---------- 設定 ----------

    private bool _loadingSettings;

    private void LoadInstallerSettings()
    {
        _loadingSettings = true;
        SetClose.IsChecked = Settings.Current.CloseAfterLaunch;
        SetCheck.IsChecked = Settings.Current.CheckUpdateOnStart;
        SetKeep.IsChecked = Settings.Current.KeepBackups;
        _loadingSettings = false;
    }

    private void SetInstaller_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        Settings.Current.CloseAfterLaunch = SetClose.IsChecked == true;
        Settings.Current.CheckUpdateOnStart = SetCheck.IsChecked == true;
        Settings.Current.KeepBackups = SetKeep.IsChecked == true;
        Settings.Current.Save();
    }

    private void LoadGameSettings()
    {
        var g = Sel;
        bool ok = g != null && g.State is InstallState.Mrp or InstallState.MrpDisabled;
        foreach (UIElement c in new UIElement[] { SetAccel, SetConsole }) c.IsEnabled = ok;
        if (!ok) return;
        SetAccel.IsChecked = g.HasPatcher;
        SetAccel.IsEnabled = g.HasPatcher || File.Exists(g.PatcherDisabledPath);
        SetConsole.IsChecked = string.Equals(IniFile.Get(g.BepInExCfg, "Logging.Console", "Enabled"), "true", StringComparison.OrdinalIgnoreCase);
    }

    private void SetAccel_Click(object sender, RoutedEventArgs e)
    {
        var g = Sel;
        if (g == null) return;
        try { ModInstaller.SetPatcher(g, SetAccel.IsChecked == true); }
        catch (Exception ex) { Dialog.Show(this, ex.Message, null, false); }
        LoadGameSettings();
        ShowDetail();
    }

    private void SetConsole_Click(object sender, RoutedEventArgs e)
    {
        var g = Sel;
        if (g == null) return;
        try { IniFile.Set(g.BepInExCfg, "Logging.Console", "Enabled", SetConsole.IsChecked == true ? "true" : "false"); }
        catch (Exception ex) { Dialog.Show(this, ex.Message, null, false); }
    }

    // ---------- 情報 ----------

    private void FillAbout()
    {
        AVersion.Text = "v" + ReleaseCheck.InstallerVersion.ToString(3);
        AMrp.Text = $"v{Payload.MrpVersion}  ·  {Payload.MrpTime:yyyy/MM/dd HH:mm}";
        ABepInEx.Text = Payload.BepInExVersion;
        ASize.Text = $"{ModInstaller.Size(Payload.TotalSize)}  ({Payload.Index.Count} files, zip {ModInstaller.Size(Payload.CompressedSize)})";
    }

    private void GitHub_Click(object sender, RoutedEventArgs e) => Tools.Open(ReleaseCheck.RepoUrl);
    private void ReleasePage_Click(object sender, RoutedEventArgs e) => Tools.Open(_release?.Url ?? ReleaseCheck.ReleasesUrl);
}
