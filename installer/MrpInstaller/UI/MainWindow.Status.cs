using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace MrpInstaller.UI;

// 下の帯: ふだんは進み具合。マウスを乗せた物に説明 (ToolTip の文) があれば、それをここにも出す
public partial class MainWindow
{
    private string _status = "";
    private bool _hinting;

    static MainWindow()
    {
        EventManager.RegisterClassHandler(typeof(FrameworkElement), MouseEnterEvent, new MouseEventHandler(OnHoverEnter));
        EventManager.RegisterClassHandler(typeof(FrameworkElement), MouseLeaveEvent, new MouseEventHandler(OnHoverLeave));
    }

    private static void OnHoverEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.ToolTip is string tip && !tip.StartsWith("L:") && GetWindow(fe) is MainWindow w && w.Tour.Visibility != Visibility.Visible)
            w.ShowHint(tip);
    }

    private static void OnHoverLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.ToolTip is string && GetWindow(fe) is MainWindow w) w.RestoreStatus();
    }

    private void SetStatus(string text)
    {
        _status = text ?? "";
        if (!_hinting) ShowStatusText(_status, false);
    }

    private void ShowHint(string tip)
    {
        _hinting = true;
        ShowStatusText(tip, true);
    }

    private void RestoreStatus()
    {
        if (!_hinting && Status.Text == _status) return;
        _hinting = false;
        ShowStatusText(_status, false);
    }

    private void ShowStatusText(string text, bool hint)
    {
        if (Status.Text == text) return;
        Status.Text = text;
        Status.Foreground = hint ? Res("Text") : Res("Sub");
        StatusIcon.Text = hint ? "" : "";
        StatusIcon.Foreground = hint ? Res("Accent") : Res("Sub");
        Anim.Fade(Status, 0.2, 1, 160);
        Anim.Fade(StatusIcon, 0.2, 1, 160);
    }

    // 進み具合の棒は、値まで滑らかに伸ばす
    private void SetProgress(double value)
    {
        Progress.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(Progress.Value, Math.Max(0, Math.Min(1, value)), TimeSpan.FromMilliseconds(value <= 0 ? 1 : 260)) { EasingFunction = Anim.Out });
    }

    // 開いた時: 少し小さい所からふわっと出る。ロゴはゆっくり光る
    private void PlayIntro()
    {
        Anim.Fade(Shell, 0, 1, 380);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim.D(0.97, 1, 480));
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim.D(0.97, 1, 480));
        Anim.SlideIn(Banner, 120, 14);
        Anim.SlideIn(DetailCard, 260, 14);
        Anim.Breathe(LogoGlow, DropShadowEffect.OpacityProperty, 0.25, 0.75, 2.4);
    }
}
