using Clippy.Models;
using Clippy.Services;
using Clippy.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Clippy.Views;

public sealed class MainContentPage : UserControl
{
    private readonly TextBlock _statusText;
    private readonly TextBlock _bufferPillText;
    private readonly TextBlock _hotkeyText;
    private readonly TextBlock _clipButtonLabel;
    private readonly Grid _clipButtonHost;
    private readonly TextBlock _durationBadge;
    private readonly Grid _contentHost;
    private readonly Border _banner;
    private readonly TextBlock _bannerTitle;
    private readonly LibraryPage _libraryPage;
    private readonly SettingsPage _settingsPage;
    private readonly StackPanel _voiceIndicator;
    private readonly Border _libraryTab;
    private readonly Border _settingsTab;
    private readonly Ellipse _statusDot;
    private int _selectedTab;

    public MainContentPage()
    {
        MinWidth = 900;
        MinHeight = 620;

        var root = new Grid { Background = ClippyTheme.BackgroundBrush };
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
                    new GradientStop { Color = Microsoft.UI.Colors.Transparent, Offset = 1 }
                }
            },
            IsHitTestVisible = false
        };
        Grid.SetRowSpan(gradient, 3);
        root.Children.Add(gradient);

        _statusText = ClippyControls.Caption("Starting capture…");
        _hotkeyText = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = ClippyTheme.TextPrimaryBrush
        };
        _bufferPillText = new TextBlock
        {
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ClippyTheme.TextPrimaryBrush
        };
        _statusDot = new Ellipse { Width = 8, Height = 8, Fill = ClippyTheme.AccentBrush };

        _voiceIndicator = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Visibility = Visibility.Collapsed };
        _voiceIndicator.Children.Add(new FontIcon { Glyph = "\uE9D9", Foreground = ClippyTheme.AccentBrush, FontSize = 12 });

        var statusPill = ClippyControls.CreatePill(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                _statusDot,
                _bufferPillText,
                new Border { Width = 1, Height = 14, Background = ClippyTheme.BorderBrush },
                _hotkeyText,
                _voiceIndicator
            }
        });

        _clipButtonHost = ClippyControls.CreatePrimaryButton("Buffering…", async (_, _) =>
            await AppCoordinator.Instance.TriggerClipAsync(AppCoordinator.ClipSource.Button), out _clipButtonLabel);

        var logo = ClippyControls.CreateLogoBadge(52);
        var titleStack = new StackPanel
        {
            Margin = new Thickness(16, 0, 0, 0),
            Spacing = 4,
            Children = { ClippyControls.Heading("Clippy", 28), _statusText }
        };

        var header = new Grid { Padding = new Thickness(28, 22, 28, 22) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(logo, 0);
        Grid.SetColumn(titleStack, 1);
        Grid.SetColumn(statusPill, 3);
        Grid.SetColumn(_clipButtonHost, 4);
        header.Children.Add(logo);
        header.Children.Add(titleStack);
        header.Children.Add(statusPill);
        header.Children.Add(_clipButtonHost);

        _libraryTab = ClippyControls.CreateTab("Library", true, OnTabTapped);
        _settingsTab = ClippyControls.CreateTab("Settings", false, OnTabTapped);
        _libraryTab.Margin = new Thickness(0, 0, 8, 0);
        _libraryTab.Tag = 0;
        _settingsTab.Tag = 1;

        _durationBadge = ClippyControls.Caption("Last 15s");
        var durationPill = ClippyControls.CreatePill(_durationBadge, ClippyTheme.SurfaceBrush);
        var tabBar = new Grid { Padding = new Thickness(28, 0, 28, 16) };
        tabBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tabBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_libraryTab, 0);
        Grid.SetColumn(_settingsTab, 1);
        Grid.SetColumn(durationPill, 3);
        tabBar.Children.Add(_libraryTab);
        tabBar.Children.Add(_settingsTab);
        tabBar.Children.Add(durationPill);

        _libraryPage = new LibraryPage();
        _settingsPage = new SettingsPage();
        _contentHost = new Grid { Children = { _libraryPage } };

        _bannerTitle = ClippyControls.Caption("");
        _banner = new Border
        {
            Background = ClippyTheme.SurfaceElevatedBrush,
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
                        Foreground = ClippyTheme.TextPrimaryBrush
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

    private void OnTabTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border tab || tab.Tag is not int index || index == _selectedTab)
        {
            return;
        }

        _selectedTab = index;
        ClippyControls.SetTabSelected(_libraryTab, index == 0);
        ClippyControls.SetTabSelected(_settingsTab, index == 1);
        _contentHost.Children.Clear();
        _contentHost.Children.Add(index == 0 ? _libraryPage : _settingsPage);
        RefreshState();
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
        _statusDot.Fill = recorder.IsBufferReady
            ? ClippyTheme.AccentBrush
            : new SolidColorBrush(Color.FromArgb(255, 255, 149, 0));

        var clipLabel = recorder.IsClipping
            ? "Clipping…"
            : recorder.IsBufferReady ? "Clip Now" : "Buffering…";
        ClippyControls.SetPrimaryButtonState(
            _clipButtonHost,
            _clipButtonLabel,
            recorder.IsBufferReady && !recorder.IsClipping,
            recorder.IsBufferReady && !recorder.IsClipping,
            clipLabel);

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
