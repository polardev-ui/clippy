using Clippy.Models;
using Clippy.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace Clippy.Views;

public sealed class MainContentPage : UserControl
{
    private readonly TextBlock _statusText;
    private readonly TextBlock _bufferPillText;
    private readonly TextBlock _hotkeyText;
    private readonly Button _clipButton;
    private readonly TextBlock _durationBadge;
    private readonly Grid _contentHost;
    private readonly Border _banner;
    private readonly TextBlock _bannerTitle;
    private readonly LibraryPage _libraryPage;
    private readonly SettingsPage _settingsPage;
    private readonly StackPanel _voiceIndicator;
    private int _selectedTab;

    public MainContentPage()
    {
        MinWidth = 900;
        MinHeight = 620;

        var root = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 10, 10, 10))
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var gradient = new Border
        {
            Background = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(1, 0),
                RadiusX = 1.2,
                RadiusY = 1.2,
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(20, 46, 217, 107), Offset = 0 },
                    new GradientStop { Color = Colors.Transparent, Offset = 1 }
                }
            },
            IsHitTestVisible = false
        };
        Grid.SetRowSpan(gradient, 3);
        root.Children.Add(gradient);

        _statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)),
            FontSize = 14
        };

        _hotkeyText = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Colors.White)
        };

        _bufferPillText = new TextBlock
        {
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White)
        };

        _voiceIndicator = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _voiceIndicator.Children.Add(new FontIcon
        {
            Glyph = "\uE9D9",
            Foreground = new SolidColorBrush(Color.FromArgb(255, 46, 217, 107)),
            FontSize = 12
        });

        var statusPillInner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(Color.FromArgb(255, 46, 217, 107)) },
                _bufferPillText,
                new Border { Width = 1, Height = 14, Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)) },
                _hotkeyText
            }
        };
        statusPillInner.Children.Add(_voiceIndicator);
        var statusPill = CreatePill(statusPillInner);

        _clipButton = new Button
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 46, 217, 107)),
            Foreground = new SolidColorBrush(Colors.Black),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(18, 10, 18, 10)
        };
        _clipButton.Click += async (_, _) =>
            await AppCoordinator.Instance.TriggerClipAsync(AppCoordinator.ClipSource.Button);

        var logoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "clippy-logo.png");
        var logo = new Image
        {
            Width = 44,
            Height = 44,
            Source = File.Exists(logoPath) ? new BitmapImage(new Uri(logoPath)) : null
        };

        var header = new Grid { Padding = new Thickness(28, 22, 28, 22) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel { Margin = new Thickness(16, 0, 0, 0), Spacing = 4 };
        titleStack.Children.Add(new TextBlock
        {
            Text = "Clippy",
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(Colors.White)
        });
        titleStack.Children.Add(_statusText);

        Grid.SetColumn(logo, 0);
        Grid.SetColumn(titleStack, 1);
        Grid.SetColumn(statusPill, 3);
        Grid.SetColumn(_clipButton, 4);
        header.Children.Add(logo);
        header.Children.Add(titleStack);
        header.Children.Add(statusPill);
        header.Children.Add(_clipButton);

        var tabBar = new Grid { Padding = new Thickness(28, 0, 28, 16) };
        tabBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tabBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var libraryTab = CreateTabButton("Library", 0);
        var settingsTab = CreateTabButton("Settings", 1);
        _durationBadge = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var badgeBorder = CreatePill(_durationBadge);
        Grid.SetColumn(libraryTab, 0);
        Grid.SetColumn(settingsTab, 1);
        Grid.SetColumn(badgeBorder, 3);
        tabBar.Children.Add(libraryTab);
        tabBar.Children.Add(settingsTab);
        tabBar.Children.Add(badgeBorder);

        _libraryPage = new LibraryPage();
        _settingsPage = new SettingsPage();
        _contentHost = new Grid();
        _contentHost.Children.Add(_libraryPage);

        _bannerTitle = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255))
        };
        _banner = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 31, 31, 31)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(100, 46, 217, 107)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            Margin = new Thickness(28, 12, 28, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Clip saved",
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = new SolidColorBrush(Colors.White)
                    },
                    _bannerTitle
                }
            }
        };

        Grid.SetRow(header, 0);
        Grid.SetRow(tabBar, 1);
        Grid.SetRow(_contentHost, 2);
        Grid.SetRowSpan(_banner, 3);
        root.Children.Add(header);
        root.Children.Add(tabBar);
        root.Children.Add(_contentHost);
        root.Children.Add(_banner);

        Content = root;
        AppCoordinator.Instance.StateChanged += () => DispatcherQueue.TryEnqueue(RefreshState);
        RefreshState();
    }

    private Button CreateTabButton(string label, int index)
    {
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(index == 0 ? 0 : 8, 0, 0, 0),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(18, 10, 18, 10)
        };
        button.Click += (_, _) =>
        {
            _selectedTab = index;
            _contentHost.Children.Clear();
            _contentHost.Children.Add(index == 0 ? _libraryPage : _settingsPage);
            RefreshState();
        };
        return button;
    }

    private static Border CreatePill(UIElement child)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 31, 31, 31)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(14, 8, 14, 8),
            Child = child
        };
    }

    public void RefreshState()
    {
        var recorder = ScreenRecorder.Instance;
        var settings = AppSettings.Instance;
        var voice = VoiceCommandListener.Instance;
        var coordinator = AppCoordinator.Instance;

        _statusText.Text = recorder.StatusMessage;
        _bufferPillText.Text = recorder.IsBufferReady ? "Buffer active" : "Buffering";
        _hotkeyText.Text = settings.Hotkey.DisplayString;
        _durationBadge.Text = $"Last {settings.ClipDuration.ShortLabel()}";
        _voiceIndicator.Visibility = voice.IsListening ? Visibility.Visible : Visibility.Collapsed;

        _clipButton.Content = recorder.IsClipping
            ? "Clipping…"
            : recorder.IsBufferReady ? "Clip Now" : "Buffering…";
        _clipButton.IsEnabled = recorder.IsBufferReady && !recorder.IsClipping;
        _clipButton.Background = new SolidColorBrush(recorder.IsBufferReady
            ? Color.FromArgb(255, 46, 217, 107)
            : Color.FromArgb(255, 31, 140, 71));

        _banner.Visibility = coordinator.ShowClipSavedBanner ? Visibility.Visible : Visibility.Collapsed;
        _bannerTitle.Text = coordinator.LastClip?.Title ?? "";

        if (!string.IsNullOrEmpty(coordinator.ErrorMessage))
        {
            _ = ShowErrorAsync(coordinator.ErrorMessage);
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Clippy",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
        AppCoordinator.Instance.ClearError();
    }
}
