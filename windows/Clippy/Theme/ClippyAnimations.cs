using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Clippy.Views;

public static class ClippyAnimations
{
    public static void AnimateContentIn(FrameworkElement element, DispatcherQueue dispatcher)
    {
        element.Opacity = 0;
        var transform = new TranslateTransform { Y = 18 };
        element.RenderTransform = transform;

        var started = DateTime.UtcNow;
        var timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            var progress = Math.Clamp(elapsed / 380.0, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            element.Opacity = eased;
            transform.Y = 18 * (1 - eased);
            if (progress >= 1)
            {
                timer.Stop();
                element.Opacity = 1;
                transform.Y = 0;
            }
        };
        timer.Start();
    }

    public static void PulseLogo(FrameworkElement element, DispatcherQueue dispatcher)
    {
        var scale = new ScaleTransform { ScaleX = 1, ScaleY = 1, CenterX = element.ActualWidth / 2, CenterY = element.ActualHeight / 2 };
        element.RenderTransform = scale;
        element.Loaded += (_, _) =>
        {
            scale.CenterX = element.ActualWidth / 2;
            scale.CenterY = element.ActualHeight / 2;
        };

        var growing = true;
        var timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(40);
        timer.Tick += (_, _) =>
        {
            var target = growing ? 1.06 : 0.94;
            var current = scale.ScaleX;
            var next = current + (target - current) * 0.12;
            scale.ScaleX = next;
            scale.ScaleY = next;
            if (Math.Abs(next - target) < 0.002)
            {
                growing = !growing;
            }
        };
        timer.Start();
    }

    public static void AnimateProgressSegment(UIElement segment)
    {
        segment.Opacity = 0;
        var storyboard = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(280),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, segment);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }
}
