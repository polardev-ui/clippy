using Clippy.Models;
using Clippy.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Clippy.Views;

public static class ClipDetailDialog
{
    public static async Task ShowAsync(Clip clip, XamlRoot xamlRoot)
    {
        var titleBox = new TextBox
        {
            Text = clip.Title,
            PlaceholderText = "Clip title"
        };

        var player = new MediaPlayerElement
        {
            MinHeight = 280,
            AreTransportControlsEnabled = true
        };
        var mediaPlayer = new Windows.Media.Playback.MediaPlayer();
        mediaPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(clip.FilePath));
        mediaPlayer.Play();
        player.SetMediaPlayer(mediaPlayer);

        var dialog = new ContentDialog
        {
            Title = clip.Title,
            PrimaryButtonText = "Export",
            SecondaryButtonText = "Show in Explorer",
            CloseButtonText = "Done",
            DefaultButton = ContentDialogButton.Close,
            MinWidth = 760,
            MinHeight = 520,
            XamlRoot = xamlRoot,
            Content = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    player,
                    titleBox,
                    new TextBlock
                    {
                        Text = $"{clip.Duration:F1}s · {clip.CreatedAt:g}",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255))
                    }
                }
            }
        };

        titleBox.LostFocus += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(titleBox.Text))
            {
                ClipManager.Instance.RenameClip(clip, titleBox.Text.Trim());
            }
        };

        dialog.PrimaryButtonClick += async (_, _) => await ClipManager.Instance.ExportClipAsync(clip);
        dialog.SecondaryButtonClick += (_, _) => ClipManager.Instance.RevealInExplorer(clip);

        await dialog.ShowAsync();
    }
}
