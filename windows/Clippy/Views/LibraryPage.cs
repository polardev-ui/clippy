using Clippy.Models;
using Clippy.Services;
using Clippy.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Markup;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Clippy.Views;

public sealed class LibraryPage : UserControl
{
    private readonly Grid _host;
    private readonly ItemsControl _grid;
    private readonly ScrollViewer _scrollViewer;
    private readonly StackPanel _emptyState;
    private readonly TextBlock _emptyCaption;

    public LibraryPage()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Background = ClippyTheme.BackgroundBrush;

        _grid = new ItemsControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        _grid.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load(
            "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<ItemsWrapGrid Orientation='Horizontal' MaximumRowsOrColumns='0' ItemWidth='280' ItemHeight='228'/>" +
            "</ItemsPanelTemplate>");

        _scrollViewer = new ScrollViewer
        {
            Content = _grid,
            Padding = new Thickness(28),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Visibility = Visibility.Collapsed
        };

        _emptyCaption = ClippyControls.Caption(
            "Press Ctrl+K or say \"Clippy, clip that\" to save your first clip.",
            TextAlignment.Center);

        _emptyState = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 18,
            Visibility = Visibility.Visible,
            Children =
            {
                new FontIcon
                {
                    Glyph = "\uE8B2",
                    FontSize = 54,
                    Foreground = ClippyTheme.AccentBrush
                },
                ClippyControls.Heading("No clips yet", 22, TextAlignment.Center),
                _emptyCaption
            }
        };

        _host = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Children = { _scrollViewer, _emptyState }
        };
        Content = _host;

        ClipManager.Instance.Clips.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        var settings = AppSettings.Instance;
        _emptyCaption.Text =
            $"Press {settings.Hotkey.DisplayString} or say \"Clippy, clip that\" to save your first clip.";

        var hasClips = ClipManager.Instance.Clips.Count > 0;
        _emptyState.Visibility = hasClips ? Visibility.Collapsed : Visibility.Visible;
        _scrollViewer.Visibility = hasClips ? Visibility.Visible : Visibility.Collapsed;

        if (!hasClips)
        {
            _grid.Items.Clear();
            return;
        }

        _grid.Items.Clear();
        foreach (var clip in ClipManager.Instance.Clips)
        {
            _grid.Items.Add(CreateCard(clip));
        }
    }

    private UIElement CreateCard(Clip clip)
    {
        var card = new Button
        {
            Width = 268,
            Height = 216,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = ClippyTheme.SurfaceBrush,
            BorderBrush = ClippyTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(0)
        };

        var thumbHost = new Grid
        {
            Height = 140,
            Background = ClippyTheme.SurfaceElevatedBrush
        };
        var thumb = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var placeholder = new FontIcon
        {
            Glyph = "\uE714",
            FontSize = 36,
            Foreground = ClippyTheme.TextSecondaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        thumbHost.Children.Add(thumb);
        thumbHost.Children.Add(placeholder);
        _ = LoadThumbnailAsync(clip, thumb, placeholder);

        var stack = new StackPanel();
        stack.Children.Add(thumbHost);
        stack.Children.Add(new TextBlock
        {
            Text = clip.Title,
            Margin = new Thickness(12, 10, 12, 0),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ClippyTheme.TextPrimaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{clip.Duration:F0}s · {clip.CreatedAt:g}",
            Margin = new Thickness(12, 4, 12, 12),
            FontSize = 12,
            Foreground = ClippyTheme.TextSecondaryBrush
        });
        card.Content = stack;
        card.Click += async (_, _) => await ClipDetailDialog.ShowAsync(clip, XamlRoot);
        return card;
    }

    private static async Task LoadThumbnailAsync(Clip clip, Image target, FontIcon placeholder)
    {
        try
        {
            var thumbPath = ClipThumbnailService.PathFor(clip.Id);
            if (!File.Exists(thumbPath))
            {
                await ClipThumbnailService.GenerateAsync(clip.Id, clip.FilePath);
            }

            if (!File.Exists(thumbPath))
            {
                return;
            }

            var file = await StorageFile.GetFileFromPathAsync(thumbPath);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            target.Source = bitmap;
            placeholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
        }
    }
}
