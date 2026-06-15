using Clippy.Theme;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace Clippy.Views;

public static class ClippyControls
{
    public static Style PlainButtonStyle =>
        (Style)Application.Current.Resources["ClippyPlainButtonStyle"];

    public static Border CreateBackground(Action<RadialGradientBrush>? configureGradient = null)
    {
        var gradient = new RadialGradientBrush
        {
            Center = new Windows.Foundation.Point(1, 0),
            RadiusX = 1.2,
            RadiusY = 1.2,
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(20, 46, 217, 107), Offset = 0 },
                new GradientStop { Color = Microsoft.UI.Colors.Transparent, Offset = 1 }
            }
        };
        configureGradient?.Invoke(gradient);

        return new Border
        {
            Background = ClippyTheme.BackgroundBrush,
            Child = new Grid
            {
                Children =
                {
                    new Border { Background = gradient, IsHitTestVisible = false }
                }
            }
        };
    }

    public static FrameworkElement CreateLogoBadge(int size, bool glow = false)
    {
        var logoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "clippy-logo.png");
        var image = new Image
        {
            Width = size - 8,
            Height = size - 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Source = System.IO.File.Exists(logoPath)
                ? new BitmapImage(new Uri(logoPath))
                : null
        };

        var ring = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2.0),
            Background = new SolidColorBrush(Color.FromArgb(31, 46, 217, 107)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(89, 46, 217, 107)),
            BorderThickness = new Thickness(1),
            Child = image
        };

        if (!glow)
        {
            return ring;
        }

        return new Grid
        {
            Width = size + 16,
            Height = size + 16,
            Children =
            {
                new Border
                {
                    Width = size + 12,
                    Height = size + 12,
                    CornerRadius = new CornerRadius((size + 12) / 2.0),
                    Background = ClippyTheme.AccentGlowBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.45
                },
                ring
            }
        };
    }

    public static TextBlock Caption(string text, TextAlignment alignment = TextAlignment.Left) =>
        new()
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.WrapWholeWords,
            TextAlignment = alignment,
            HorizontalAlignment = alignment switch
            {
                TextAlignment.Center => HorizontalAlignment.Center,
                TextAlignment.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left
            },
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ClippyTheme.TextSecondaryBrush
        };

    public static Border CreateBadge(TextBlock label, Brush? background = null)
    {
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextAlignment = TextAlignment.Center;

        var content = new Grid
        {
            MinHeight = ClippyTheme.ControlHeight - 2,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Children = { label }
        };

        return new Border
        {
            Background = background ?? ClippyTheme.SurfaceBrush,
            BorderBrush = ClippyTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = ClippyTheme.ButtonRadius,
            Padding = new Thickness(14, 0, 14, 0),
            MinHeight = ClippyTheme.ControlHeight,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Child = content
        };
    }

    public static TextBlock Heading(string text, double size = 28, TextAlignment alignment = TextAlignment.Left) =>
        new()
        {
            Text = text,
            FontSize = size,
            FontWeight = FontWeights.Bold,
            TextAlignment = alignment,
            HorizontalAlignment = alignment switch
            {
                TextAlignment.Center => HorizontalAlignment.Center,
                TextAlignment.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left
            },
            Foreground = ClippyTheme.TextPrimaryBrush
        };

    public static Border CreatePill(UIElement child, Brush? background = null) =>
        new()
        {
            Background = background ?? ClippyTheme.SurfaceElevatedBrush,
            BorderBrush = ClippyTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = ClippyTheme.ButtonRadius,
            Padding = new Thickness(14, 0, 14, 0),
            MinHeight = ClippyTheme.ControlHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = child
        };

    public static StackPanel CreateStatusRow(params UIElement[] items)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        foreach (var item in items)
        {
            if (item is FrameworkElement fe)
            {
                fe.VerticalAlignment = VerticalAlignment.Center;
            }

            row.Children.Add(item);
        }

        return row;
    }

    public static Border CreateSection(string title, UIElement content, string? iconGlyph = null)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        if (!string.IsNullOrEmpty(iconGlyph))
        {
            header.Children.Add(new FontIcon
            {
                Glyph = iconGlyph,
                Foreground = ClippyTheme.AccentBrush,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ClippyTheme.TextPrimaryBrush
        });

        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(header);
        stack.Children.Add(content);

        return new Border
        {
            Background = ClippyTheme.SurfaceBrush,
            BorderBrush = ClippyTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = ClippyTheme.CardRadius,
            Padding = new Thickness(18),
            Child = stack
        };
    }

    public static Grid CreatePrimaryButton(string label, RoutedEventHandler click, out TextBlock labelBlock)
    {
        labelBlock = new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var face = new Border
        {
            Background = ClippyTheme.AccentBrush,
            CornerRadius = ClippyTheme.ButtonRadius,
            MinHeight = ClippyTheme.ControlHeight,
            Padding = new Thickness(16, 0, 16, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = labelBlock
        };

        var glow = new Border
        {
            Background = ClippyTheme.AccentGlowBrush,
            CornerRadius = ClippyTheme.ButtonRadius,
            Margin = new Thickness(-2),
            Opacity = 0
        };

        var host = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Children = { glow, face } };
        var button = new Button
        {
            Style = PlainButtonStyle,
            Content = host,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += click;

        var wrapper = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { button }
        };
        wrapper.Tag = glow;
        return wrapper;
    }

    public static void SetPrimaryButtonState(Grid wrapper, TextBlock label, bool ready, bool enabled, string text)
    {
        label.Text = text;
        if (wrapper.Children[0] is not Button button)
        {
            return;
        }

        button.IsEnabled = enabled;
        if (button.Content is not Grid host || host.Children.Count < 2)
        {
            return;
        }

        if (host.Children[0] is Border glow && host.Children[1] is Border face)
        {
            glow.Opacity = ready ? 0.55 : 0;
            face.Background = ready ? ClippyTheme.AccentBrush : ClippyTheme.AccentDimBrush;
        }
    }

    public static Border CreateTab(string label, bool selected, TappedEventHandler onTap)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = selected ? new SolidColorBrush(Microsoft.UI.Colors.Black) : ClippyTheme.TextSecondaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var tab = new Border
        {
            Background = selected ? ClippyTheme.AccentBrush : ClippyTheme.SurfaceBrush,
            BorderBrush = selected ? ClippyTheme.AccentBrush : ClippyTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = ClippyTheme.ButtonRadius,
            MinHeight = ClippyTheme.ControlHeight,
            Padding = new Thickness(16, 0, 16, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = text
        };
        tab.Tapped += onTap;
        return tab;
    }

    public static void SetTabSelected(Border tab, bool selected)
    {
        tab.Background = selected ? ClippyTheme.AccentBrush : ClippyTheme.SurfaceBrush;
        tab.BorderBrush = selected ? ClippyTheme.AccentBrush : ClippyTheme.BorderBrush;
        if (tab.Child is TextBlock label)
        {
            label.Foreground = selected
                ? new SolidColorBrush(Microsoft.UI.Colors.Black)
                : ClippyTheme.TextSecondaryBrush;
        }
    }

    public static Border CreateSecondaryButton(string label, RoutedEventHandler? click = null)
    {
        var button = new Button
        {
            Style = PlainButtonStyle,
            Content = new Border
            {
                Background = ClippyTheme.SurfaceElevatedBrush,
                BorderBrush = ClippyTheme.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = ClippyTheme.ButtonRadius,
                MinHeight = ClippyTheme.ControlHeight,
                Padding = new Thickness(16, 0, 16, 0),
                Child = new TextBlock
                {
                    Text = label,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ClippyTheme.TextPrimaryBrush,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            }
        };
        if (click != null)
        {
            button.Click += click;
        }

        return new Border { Child = button };
    }

    public static Border CreateAccentButton(string label, RoutedEventHandler click)
    {
        var button = new Button
        {
            Style = PlainButtonStyle,
            Content = new Border
            {
                Background = ClippyTheme.AccentBrush,
                CornerRadius = ClippyTheme.ButtonRadius,
                MinHeight = ClippyTheme.ControlHeight,
                Padding = new Thickness(20, 0, 20, 0),
                Child = new TextBlock
                {
                    Text = label,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black),
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            }
        };
        button.Click += click;
        return new Border { Child = button };
    }
}

public sealed class SegmentedPicker : UserControl
{
    private readonly IReadOnlyList<Border> _segments;
    private readonly IReadOnlyList<object> _tags;
    private int _selectedIndex;

    public event Action<object>? SelectionChanged;

    public SegmentedPicker(IReadOnlyList<string> labels, IReadOnlyList<object> tags, int selectedIndex)
    {
        _tags = tags;
        _selectedIndex = selectedIndex;

        var grid = new Grid();
        for (var i = 0; i < labels.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var segments = new List<Border>();
        for (var i = 0; i < labels.Count; i++)
        {
            var index = i;
            var segment = CreateSegment(labels[i], i == selectedIndex);
            segments.Add(segment);
            Grid.SetColumn(segment, i);
            grid.Children.Add(segment);
            segment.Tapped += (_, e) =>
            {
                Select(index);
                e.Handled = true;
            };
        }

        _segments = segments;

        Content = new Border
        {
            Background = ClippyTheme.SurfaceElevatedBrush,
            BorderBrush = ClippyTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4),
            Child = grid
        };
    }

    public int SelectedIndex => _selectedIndex;

    public void Select(int index)
    {
        if (index < 0 || index >= _segments.Count || index == _selectedIndex)
        {
            return;
        }

        SetSegmentStyle(_segments[_selectedIndex], false);
        _selectedIndex = index;
        SetSegmentStyle(_segments[_selectedIndex], true);
        SelectionChanged?.Invoke(_tags[_selectedIndex]);
    }

    private static Border CreateSegment(string label, bool selected)
    {
        return new Border
        {
            Background = selected ? ClippyTheme.AccentBrush : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                Foreground = selected
                    ? new SolidColorBrush(Microsoft.UI.Colors.Black)
                    : ClippyTheme.TextSecondaryBrush
            },
            Tag = label
        };
    }

    private static void SetSegmentStyle(Border segment, bool selected)
    {
        segment.Background = selected
            ? ClippyTheme.AccentBrush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        if (segment.Child is TextBlock text)
        {
            text.Foreground = selected
                ? new SolidColorBrush(Microsoft.UI.Colors.Black)
                : ClippyTheme.TextSecondaryBrush;
        }
    }
}
