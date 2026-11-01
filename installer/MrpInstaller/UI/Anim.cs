using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MrpInstaller.UI;

// 画面の動き (ふわっと出る・滑る・ぽんと弾む)。どれも短く、後から来た動きで上書きされる
public static class Anim
{
    public static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction Back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };

    public static DoubleAnimation D(double from, double to, int ms, int delay = 0, IEasingFunction ease = null) =>
        new(from, to, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = ease ?? Out };

    public static void Fade(UIElement e, double from, double to, int ms, int delay = 0) =>
        e.BeginAnimation(UIElement.OpacityProperty, D(from, to, ms, delay));

    public static void Move(TranslateTransform t, DependencyProperty p, double from, double to, int ms, int delay = 0) =>
        t.BeginAnimation(p, D(from, to, ms, delay));

    // 下から少し上がりながら出る
    public static void SlideIn(FrameworkElement e, int delay, double dy)
    {
        if (e.RenderTransform is not TranslateTransform t)
        {
            t = new TranslateTransform();
            e.RenderTransform = t;
        }
        e.Opacity = 0;
        Fade(e, 0, 1, 260, delay);
        Move(t, TranslateTransform.YProperty, dy, 0, 340, delay);
    }

    // 一覧の項目を上から順にずらして出す
    public static void Stagger(ItemsControl list)
    {
        list.Dispatcher.BeginInvoke(new Action(() =>
        {
            for (int i = 0; i < list.Items.Count; i++)
                if (list.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement c) SlideIn(c, 60 + i * 70, 12);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // 変わった所を一瞬大きくして目立たせる
    public static void Pop(FrameworkElement e)
    {
        var s = new ScaleTransform(1, 1);
        e.RenderTransformOrigin = new Point(0.5, 0.5);
        e.RenderTransform = s;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.18, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), Out));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), Back));
        s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // ずっと続くゆっくりした明滅 (光るロゴ・チュートリアルの枠)
    public static void Breathe(DependencyObject target, DependencyProperty p, double from, double to, double seconds)
    {
        var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        ((IAnimatable)target).BeginAnimation(p, a);
    }
}
