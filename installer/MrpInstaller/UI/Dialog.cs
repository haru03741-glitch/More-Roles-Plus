using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using MrpInstaller.Core;

namespace MrpInstaller.UI;

// テーマに合わせた確認窓 (標準の MessageBox は白くて浮くので)
public static class Dialog
{
    public static bool Show(Window owner, string message, string title = null, bool confirm = true, bool danger = false)
    {
        var w = new Window
        {
            Owner = owner != null && owner.IsVisible ? owner : null,
            WindowStartupLocation = owner != null && owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = (Brush)Application.Current.FindResource("Card"),
            ShowInTaskbar = owner == null || !owner.IsVisible,
            Title = title ?? "More Roles Plus",
            Foreground = (Brush)Application.Current.FindResource("Text"),
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
        };
        WindowChrome.SetWindowChrome(w, new WindowChrome { CaptionHeight = 30, GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(0) });
        bool result = false;

        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 20), MaxWidth = 460, MinWidth = 340 };
        panel.Children.Add(new TextBlock { Text = title ?? Lang.T("dlg.confirm"), Style = (Style)Application.Current.FindResource("H2"), Foreground = (Brush)Application.Current.FindResource(danger ? "Red" : "Accent") });
        panel.Children.Add(new TextBlock { Text = message, FontSize = 13, LineHeight = 20, Margin = new Thickness(0, 0, 0, 20) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (confirm)
        {
            var no = new Button { Content = Lang.T("dlg.cancel"), Margin = new Thickness(0, 0, 8, 0), IsCancel = true, MinWidth = 90 };
            no.Click += (_, _) => w.Close();
            buttons.Children.Add(no);
        }
        var ok = new Button
        {
            Content = confirm ? Lang.T("dlg.yes") : Lang.T("dlg.ok"),
            Style = (Style)Application.Current.FindResource(danger ? "Danger" : "Primary"),
            FontSize = 13, Padding = new Thickness(18, 8, 18, 8), IsDefault = true, MinWidth = 90,
        };
        ok.Click += (_, _) => { result = true; w.Close(); };
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        var scale = new ScaleTransform(0.94, 0.94);
        var frame = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("Line"),
            BorderThickness = new Thickness(1),
            Child = panel,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
        };
        w.Content = frame;
        // ふわっと大きくなりながら出る
        w.Loaded += (_, _) =>
        {
            Anim.Fade(frame, 0, 1, 200);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim.D(0.94, 1, 280, 0, Anim.Back));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim.D(0.94, 1, 280, 0, Anim.Back));
        };
        w.ShowDialog();
        return result;
    }
}
