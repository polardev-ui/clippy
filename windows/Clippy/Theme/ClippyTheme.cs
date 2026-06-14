using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Clippy.Theme;

public static class ClippyTheme
{
    public static Color Background => Color.FromArgb(255, 10, 10, 10);
    public static Color Surface => Color.FromArgb(255, 20, 20, 20);
    public static Color SurfaceElevated => Color.FromArgb(255, 31, 31, 31);
    public static Color Accent => Color.FromArgb(255, 46, 217, 107);
    public static Color AccentDim => Color.FromArgb(255, 31, 140, 71);
    public static Color TextPrimary => Colors.White;
    public static Color TextSecondary => Color.FromArgb(140, 255, 255, 255);
    public static Color Border => Color.FromArgb(20, 255, 255, 255);

    public static SolidColorBrush BackgroundBrush => new(Background);
    public static SolidColorBrush SurfaceBrush => new(Surface);
    public static SolidColorBrush SurfaceElevatedBrush => new(SurfaceElevated);
    public static SolidColorBrush AccentBrush => new(Accent);
    public static SolidColorBrush AccentDimBrush => new(AccentDim);
    public static SolidColorBrush TextPrimaryBrush => new(TextPrimary);
    public static SolidColorBrush TextSecondaryBrush => new(TextSecondary);
    public static SolidColorBrush BorderBrush => new(Border);

    public static Thickness PagePadding => new(28);
    public static CornerRadius CardRadius => new(16);
    public static CornerRadius PillRadius => new(999);

    public static void ApplyDarkWindow(Window window)
    {
        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = ElementTheme.Dark;
        }
    }
}
