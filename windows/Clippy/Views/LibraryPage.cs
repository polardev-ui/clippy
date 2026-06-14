using Clippy.Models;
using Clippy.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Clippy.Views;

public sealed class LibraryPage : UserControl
{
    private readonly Grid _host;
    private readonly ItemsControl _grid;

    public LibraryPage()
    {
        _grid = new ItemsControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        _host = new Grid();
        Content = _host;
        ClipManager.Instance.Clips.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        _host.Children.Clear();

        if (ClipManager.Instance.Clips.Count == 0)
        {
            var settings = AppSettings.Instance;
            _host.Children.Add(new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 18,
                Children =
                {
                    new FontIcon
                    {
                        Glyph = "\uE8B2",
                        FontSize = 54,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 46, 217, 107))
                    },
                    new TextBlock
                    {
                        Text = "No clips yet",
                        FontSize = 22,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = new SolidColorBrush(Colors.White),
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = $"Press {settings.Hotkey.DisplayString} or say \"Clippy, clip that\" to save your first clip.",
                        TextWrapping = TextWrapping.WrapWholeWords,
                        TextAlignment = TextAlignment.Center,
                        MaxWidth = 420,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255))
                    }
                }
            });
            return;
        }

        var wrap = (ItemsPanelTemplate)XamlReader.Load(
            "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<ItemsWrapGrid MaximumRowsOrColumns='3' ItemWidth='280' ItemHeight='220'/>" +
            "</ItemsPanelTemplate>");
        _grid.ItemsPanel = wrap;
        _grid.Items.Clear();

        foreach (var clip in ClipManager.Instance.Clips)
        {
            _grid.Items.Add(CreateCard(clip));
        }

        _host.Children.Add(new ScrollViewer
        {
            Content = _grid,
            Padding = new Thickness(28)
        });
    }

    private UIElement CreateCard(Clip clip)
    {
        var card = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 20, 20, 20)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(0)
        };

        var stack = new StackPanel();
        var thumb = new Image
        {
            Height = 140,
            Stretch = Stretch.UniformToFill
        };
        _ = LoadThumbnailAsync(clip, thumb);

        stack.Children.Add(thumb);
        stack.Children.Add(new TextBlock
        {
            Text = clip.Title,
            Margin = new Thickness(12, 10, 12, 0),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{clip.Duration:F0}s · {clip.CreatedAt:g}",
            Margin = new Thickness(12, 4, 12, 12),
            FontSize = 12,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255))
        });
        card.Content = stack;
        card.Click += async (_, _) => await ClipDetailDialog.ShowAsync(clip, XamlRoot);
        return card;
    }

    private static async Task LoadThumbnailAsync(Clip clip, Image target)
    {
        try
        {
            if (!File.Exists(clip.FilePath)) return;
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(clip.FilePath);
            var stream = await file.OpenAsync(FileAccessMode.Read);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            target.Source = bitmap;
        }
        catch
        {
        }
    }
}
