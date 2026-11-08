using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using MrpInstaller.Core;

namespace MrpInstaller.UI;

// 初めて開いた人へのチュートリアル。画面を暗くして、説明する所にだけ穴をあけて緑の枠で囲む。
// 穴・枠・説明のカードは、次へ進むたびに滑って移動する
public partial class MainWindow
{
    private sealed class TourStop
    {
        public TourStop(string key, Func<FrameworkElement> target, Func<RadioButton> page) { Key = key; Target = target; Page = page; }
        public string Key { get; }
        public Func<FrameworkElement> Target { get; }
        public Func<RadioButton> Page { get; }
    }

    private TourStop[] _steps;
    private int _tourStep;
    private readonly RectangleGeometry _tourAll = new();
    private readonly RectangleGeometry _tourHole = new(new Rect(0, 0, 0, 0), 12, 12);
    private Rect _holeNow;

    private TourStop[] Steps => _steps ??= new[]
    {
        new TourStop("welcome", null, () => NavHome),
        new TourStop("games", () => GamesPane, () => NavHome),
        new TourStop("detail", () => Detail.IsVisible ? (FrameworkElement)InfoGrid.Parent : DetailCard, () => NavHome),
        new TourStop("install", () => BtnInstall.IsVisible ? BtnInstall : DetailCard, () => NavHome),
        new TourStop("launch", () => BtnLaunch.IsVisible ? BtnLaunch : DetailCard, () => NavHome),
        new TourStop("sub", () => SubActions.IsVisible ? SubActions : DetailCard, () => NavHome),
        new TourStop("status", () => StatusBar, () => NavHome),
        new TourStop("tools", () => NavTools, () => NavTools),
        new TourStop("backups", () => NavBackups, () => NavBackups),
        new TourStop("settings", () => NavSettings, () => NavSettings),
        new TourStop("about", () => NavAbout, () => NavAbout),
        new TourStop("help", () => HelpBtn, () => NavHome),
        new TourStop("finish", null, () => NavHome),
    };

    private void StartTour()
    {
        if (Tour.Visibility == Visibility.Visible) return;
        _tourAll.Rect = new Rect(0, 0, Root.ActualWidth, Root.ActualHeight);
        TourShade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, _tourAll, _tourHole);
        var c = new Point(Root.ActualWidth / 2, Root.ActualHeight / 2);
        _holeNow = new Rect(c, new Size(0, 0));
        _tourHole.Rect = _holeNow;
        Tour.Visibility = Visibility.Visible;
        Anim.Fade(Tour, 0, 1, 320);
        Anim.Breathe(TourGlow, DropShadowEffect.BlurRadiusProperty, 12, 30, 1.1);
        BuildDots();
        ShowTourStep(0, true);
        Focus();
    }

    private void BuildDots()
    {
        TourDots.Children.Clear();
        foreach (var _ in Steps)
            TourDots.Children.Add(new Rectangle { Width = 6, Height = 6, RadiusX = 3, RadiusY = 3, Margin = new Thickness(0, 0, 5, 0), Fill = Res("Line") });
    }

    private void ShowTourStep(int i, bool animate)
    {
        _tourStep = Math.Max(0, Math.Min(Steps.Length - 1, i));
        var step = Steps[_tourStep];
        var page = step.Page();
        if (page.IsChecked != true) page.IsChecked = true;

        bool first = _tourStep == 0, last = _tourStep == Steps.Length - 1;
        TourStep.Text = Lang.T("tour.step", _tourStep + 1, Steps.Length);
        TourTitle.Text = Lang.T($"tour.{step.Key}.title");
        TourText.Text = Lang.T($"tour.{step.Key}.text");
        TourArt.Visibility = first || last ? Visibility.Visible : Visibility.Collapsed;
        TourBack.Visibility = first ? Visibility.Collapsed : Visibility.Visible;
        TourSkip.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        TourNextText.Text = Lang.T(first ? "tour.start" : last ? "tour.finish" : "tour.next");
        for (int d = 0; d < TourDots.Children.Count; d++)
        {
            var dot = (Rectangle)TourDots.Children[d];
            dot.Fill = d == _tourStep ? Res("Accent") : d < _tourStep ? Res("AccentSoft") : Res("Line");
            dot.BeginAnimation(WidthProperty, Anim.D(dot.ActualWidth > 0 ? dot.ActualWidth : 6, d == _tourStep ? 20 : 6, 260));
        }

        // ページが切り替わった直後は大きさが決まっていないので、描き終わってから位置を決める
        Dispatcher.BeginInvoke(new Action(() => PlaceTour(animate)), System.Windows.Threading.DispatcherPriority.Loaded);
        if (animate)
        {
            Anim.Fade(TourBodyPanel, 0, 1, 260, 80);
            Anim.Move(TourBodyShift, TranslateTransform.YProperty, 10, 0, 320, 80);
        }
    }

    private void PlaceTour(bool animate)
    {
        if (Tour.Visibility != Visibility.Visible) return;
        double W = Root.ActualWidth, H = Root.ActualHeight;
        _tourAll.Rect = new Rect(0, 0, W, H);
        var target = Steps[_tourStep].Target?.Invoke();
        Rect hole;
        bool centered = target == null || !target.IsVisible || target.ActualWidth < 1;
        if (centered) hole = new Rect(W / 2, H / 2, 0, 0);
        else
        {
            hole = target.TransformToAncestor(Root).TransformBounds(new Rect(target.RenderSize));
            hole.Inflate(8, 8);
            hole.Intersect(new Rect(2, 2, W - 4, H - 4));
        }

        int ms = animate ? 420 : 1;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        _tourHole.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(_holeNow, hole, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        _holeNow = hole;

        TourRing.BeginAnimation(MarginProperty, new ThicknessAnimation(new Thickness(hole.X, hole.Y, 0, 0), TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        TourRing.BeginAnimation(WidthProperty, new DoubleAnimation(hole.Width, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        TourRing.BeginAnimation(HeightProperty, new DoubleAnimation(hole.Height, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        TourRing.BeginAnimation(OpacityProperty, Anim.D(TourRing.Opacity, centered ? 0 : 1, 250));

        // カードは穴の右 → 下 → 上 → 左の順に、入る所へ置く
        TourCard.UpdateLayout();
        double cw = TourCard.ActualWidth, ch = TourCard.ActualHeight, gap = 18, pad = 16;
        double x, y;
        if (centered) { x = (W - cw) / 2; y = (H - ch) / 2; }
        else if (hole.Right + gap + cw <= W - pad) { x = hole.Right + gap; y = hole.Top; }
        else if (hole.Bottom + gap + ch <= H - pad) { x = hole.Left; y = hole.Bottom + gap; }
        else if (hole.Top - gap - ch >= pad) { x = hole.Left; y = hole.Top - gap - ch; }
        else { x = hole.Left - gap - cw; y = hole.Top; }
        x = Math.Max(pad, Math.Min(W - cw - pad, x));
        y = Math.Max(pad, Math.Min(H - ch - pad, y));
        TourCard.BeginAnimation(MarginProperty, new ThicknessAnimation(new Thickness(x, y, 0, 0), TimeSpan.FromMilliseconds(animate ? 460 : 1)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 } });
    }

    private void TourRelayout()
    {
        if (Tour.Visibility == Visibility.Visible) PlaceTour(false);
    }

    private void TourNext_Click(object sender, RoutedEventArgs e)
    {
        if (_tourStep >= Steps.Length - 1) EndTour();
        else ShowTourStep(_tourStep + 1, true);
    }

    private void TourBack_Click(object sender, RoutedEventArgs e) => ShowTourStep(_tourStep - 1, true);
    private void TourSkip_Click(object sender, RoutedEventArgs e) => EndTour();

    private void EndTour()
    {
        Settings.Current.TourDone = true;
        Settings.Current.Save();
        NavHome.IsChecked = true;
        var fade = Anim.D(1, 0, 260);
        fade.Completed += (_, _) =>
        {
            Tour.Visibility = Visibility.Collapsed;
            Tour.BeginAnimation(OpacityProperty, null);
            TourGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
        };
        Tour.BeginAnimation(OpacityProperty, fade);
        if (Sel != null && BtnInstall.IsVisible && BtnInstall.IsEnabled && Sel.State is not (InstallState.Mrp or InstallState.MrpDisabled)) Anim.Pop(BtnInstall);
    }

    private void Tour_Key(object sender, KeyEventArgs e)
    {
        if (Tour.Visibility != Visibility.Visible) return;
        switch (e.Key)
        {
            case Key.Right or Key.Enter or Key.Space: TourNext_Click(null, null); e.Handled = true; break;
            case Key.Left or Key.Back: if (_tourStep > 0) ShowTourStep(_tourStep - 1, true); e.Handled = true; break;
            case Key.Escape: EndTour(); e.Handled = true; break;
        }
    }
}
