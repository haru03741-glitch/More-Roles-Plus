using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MrpInstaller.Core;

namespace MrpInstaller.UI;

// GitHub のリリースからの更新。インストーラーが古ければ先にインストーラーを、そうでなければ各ゲームの MRP を入れ替える
public partial class MainWindow
{
    private ReleaseCheck.Release _release;
    private bool _checked;
    private bool _bannerForInstaller;

    private async Task CheckUpdate(bool quiet)
    {
        if (!quiet) { UpdateText.Text = Lang.T("about.checking"); UpdateText.Foreground = Res("Sub"); }
        try
        {
            _release = await ReleaseCheck.Latest();
            _checked = true;
        }
        catch
        {
            _release = null;
            _checked = false;
        }
        ShowRelease();
        ShowUpdateBanner();
    }

    private GameInstall[] OutdatedGames => _games.Where(g => ReleaseCheck.MrpOutdated(_release, g)).ToArray();

    private bool HasUpdate => ReleaseCheck.InstallerOutdated(_release) || OutdatedGames.Length > 0;

    private void ShowRelease()
    {
        if (UpdateText == null) return;
        if (!_checked) { UpdateText.Text = UpdateText.Text.Length == 0 ? "" : Lang.T("about.offline"); UpdateText.Foreground = Res("Sub"); }
        else if (_release == null) { UpdateText.Text = Lang.T("about.noRelease"); UpdateText.Foreground = Res("Sub"); }
        else
        {
            UpdateText.Text = Lang.T(HasUpdate ? "about.newRelease" : "about.latest", _release.Tag, _release.Published.ToLocalTime());
            UpdateText.Foreground = HasUpdate ? Res("Accent") : Res("Sub");
        }
        UpdateDot.Visibility = HasUpdate ? Visibility.Visible : Visibility.Collapsed;
        if (HasUpdate) Anim.Breathe(UpdateDot, OpacityProperty, 0.35, 1, 0.9);
        BtnSelfUpdate.Visibility = ReleaseCheck.InstallerOutdated(_release) ? Visibility.Visible : Visibility.Collapsed;
        BtnDownload.Visibility = _release?.DllUrl != null ? Visibility.Visible : Visibility.Collapsed;
        string notes = _release?.Notes?.Trim();
        NotesBox.Visibility = string.IsNullOrEmpty(notes) ? Visibility.Collapsed : Visibility.Visible;
        NotesText.Text = string.IsNullOrEmpty(notes) ? "" : Lang.T("about.notes") + "\n" + (notes.Length > 1500 ? notes.Substring(0, 1500) + " …" : notes);
    }

    // ホームの上のお知らせ。「後で」を押した版は、次の版が出るまで出さない
    private void ShowUpdateBanner()
    {
        if (UpdateBanner == null) return;
        bool show = HasUpdate && Settings.Current.SkippedVersion != _release?.Tag;
        if (show)
        {
            _bannerForInstaller = ReleaseCheck.InstallerOutdated(_release);
            UpdateBannerTitle.Text = _bannerForInstaller ? Lang.T("upd.installerTitle", _release.Tag) : Lang.T("upd.mrpTitle", _release.Tag);
            UpdateBannerSub.Text = _bannerForInstaller ? Lang.T("upd.installerSub") : Lang.T("upd.mrpSub", OutdatedGames.Length);
            UpdateBannerBtn.IsEnabled = !_busy;
        }
        bool was = UpdateBanner.Visibility == Visibility.Visible;
        UpdateBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && !was) Anim.SlideIn(UpdateBanner, 0, -10);
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.SkippedVersion = _release?.Tag;
        Settings.Current.Save();
        ShowUpdateBanner();
    }

    private async void UpdateBanner_Click(object sender, RoutedEventArgs e)
    {
        if (_bannerForInstaller) await UpdateInstaller();
        else await UpdateAllMrp();
    }

    private async void SelfUpdate_Click(object sender, RoutedEventArgs e) => await UpdateInstaller();

    private async Task UpdateInstaller()
    {
        var rel = _release;
        if (rel == null) return;
        string exe = null;
        if (!await Run(r => exe = ReleaseCheck.UpdateSelf(rel, p => r(p * 0.98, $"  {ReleaseCheck.InstallerAsset} {p:P0}")).GetAwaiter().GetResult())) return;
        SetStatus(Lang.T("upd.restarting"));
        await Task.Delay(400);
        Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    private async Task UpdateAllMrp()
    {
        var rel = _release;
        var games = OutdatedGames;
        if (rel == null || games.Length == 0) return;
        if (await Run(r =>
            {
                for (int i = 0; i < games.Length; i++)
                {
                    int n = i;
                    ReleaseCheck.InstallDll(games[i], rel, p => r((n + p) / games.Length, $"  {GameInstall.MrpDll} → {games[n].PlatformLabel} {p:P0}")).GetAwaiter().GetResult();
                }
            }))
        {
            string msg = Lang.T("upd.allDone", games.Length, rel.Tag);
            Log(msg);
            SetStatus(msg);
            ShowRelease();
        }
    }

    // 情報の画面のボタン: 選んでいるゲームだけ入れ替える
    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var g = Sel;
        if (g == null || _release == null) { NeedGame(); return; }
        if (g.State is not (InstallState.Mrp or InstallState.MrpDisabled))
        {
            Dialog.Show(this, Lang.T("err.notInstalled"), null, false);
            return;
        }
        var rel = _release;
        if (await Run(r => ReleaseCheck.InstallDll(g, rel, p => r(p, $"  {GameInstall.MrpDll} {p:P0}")).GetAwaiter().GetResult()))
        {
            Log(Lang.T("log.dllUpdated", rel.Tag));
            SetStatus(Lang.T("log.dllUpdated", rel.Tag));
            ShowRelease();
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckUpdate(false);
}
