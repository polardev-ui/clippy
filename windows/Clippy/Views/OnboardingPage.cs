using Clippy.Models;
using Clippy.Services;
using Clippy.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Clippy.Views;

public sealed class OnboardingPage : UserControl
{
    private int _step;
    private int _introPage;
    private readonly StackPanel _contentHost;
    private readonly Grid _progressBar;
    private readonly Border _primaryButtonHost;
    private readonly TextBlock _primaryButtonLabel;
    private readonly Border _backButtonHost;
    private readonly Border _skipButtonHost;

    private const int StepCount = 5;

    public OnboardingPage()
    {
        MinWidth = 900;
        MinHeight = 620;

        var root = new Grid { Background = ClippyTheme.BackgroundBrush };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var gradient = new Border
        {
            Background = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(0.5, 0),
                RadiusX = 1.1,
                RadiusY = 1.1,
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(26, 46, 217, 107), Offset = 0 },
                    new GradientStop { Color = Microsoft.UI.Colors.Transparent, Offset = 1 }
                }
            },
            IsHitTestVisible = false
        };
        Grid.SetRowSpan(gradient, 3);
        root.Children.Add(gradient);

        _progressBar = new Grid { Margin = new Thickness(40, 28, 40, 0) };
        for (var i = 0; i < StepCount; i++)
        {
            _progressBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        _contentHost = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 24,
            MaxWidth = 640
        };

        var footer = new Grid { Margin = new Thickness(40, 0, 40, 36) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _backButtonHost = ClippyControls.CreateSecondaryButton("Back");
        _backButtonHost.Visibility = Visibility.Collapsed;
        if (_backButtonHost.Child is Button backButton)
        {
            backButton.Click += (_, _) => GoBack();
        }

        _primaryButtonHost = ClippyControls.CreateAccentButton("Next", (_, _) => GoForward());
        _primaryButtonLabel = FindButtonLabel(_primaryButtonHost) ?? new TextBlock();

        _skipButtonHost = ClippyControls.CreateSecondaryButton("Skip for now");
        _skipButtonHost.Visibility = Visibility.Collapsed;
        if (_skipButtonHost.Child is Button skipButton)
        {
            skipButton.Click += (_, _) => AppCoordinator.Instance.CompleteOnboarding(fromVoiceDemo: false);
        }

        Grid.SetColumn(_backButtonHost, 0);
        Grid.SetColumn(_primaryButtonHost, 2);
        Grid.SetColumn(_skipButtonHost, 2);
        footer.Children.Add(_backButtonHost);
        footer.Children.Add(_primaryButtonHost);
        footer.Children.Add(_skipButtonHost);

        Grid.SetRow(_progressBar, 0);
        Grid.SetRow(_contentHost, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(_progressBar);
        root.Children.Add(_contentHost);
        root.Children.Add(footer);

        Content = root;
        RenderStep();
    }

    private static TextBlock? FindButtonLabel(Border host)
    {
        if (host.Child is Button { Content: Border { Child: TextBlock label } })
        {
            return label;
        }

        return null;
    }

    private void GoBack()
    {
        if (_step == 4)
        {
            _step = 3;
        }
        else if (_step == 1 && _introPage > 0)
        {
            _introPage--;
        }
        else
        {
            _step = Math.Max(0, _step - 1);
        }

        RenderStep();
    }

    private void GoForward()
    {
        if (_step == 1 && _introPage < 2)
        {
            _introPage++;
            RenderStep();
            return;
        }

        if (_step == 3)
        {
            AppCoordinator.Instance.ApplyOnboardingAudioDevices();
        }

        if (_step >= StepCount - 1)
        {
            return;
        }

        _step++;
        RenderStep();
    }

    private void RenderStep()
    {
        UpdateProgress();
        _contentHost.Children.Clear();
        _backButtonHost.Visibility = _step == 0 || _step == 4 ? Visibility.Collapsed : Visibility.Visible;
        _skipButtonHost.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;
        _primaryButtonHost.Visibility = _step == 4 ? Visibility.Collapsed : Visibility.Visible;
        _primaryButtonLabel.Text = _step == 3 ? "Continue" : "Next";

        switch (_step)
        {
            case 0: ShowWelcome(); break;
            case 1: ShowIntro(); break;
            case 2: ShowMicPicker(); break;
            case 3: ShowOutputPicker(); break;
            case 4: ShowVoicePractice(); break;
        }
    }

    private void UpdateProgress()
    {
        _progressBar.Children.Clear();
        for (var i = 0; i < StepCount; i++)
        {
            var segment = new Border
            {
                Height = 4,
                Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0),
                CornerRadius = new CornerRadius(999),
                Background = new SolidColorBrush(i <= _step ? ClippyTheme.Accent : ClippyTheme.Border)
            };
            Grid.SetColumn(segment, i);
            _progressBar.Children.Add(segment);
        }
    }

    private void ShowWelcome()
    {
        _contentHost.Children.Add(ClippyControls.CreateLogoBadge(120, glow: true));
        _contentHost.Children.Add(ClippyControls.Heading("Welcome to Clippy!", 42, TextAlignment.Center));
        _contentHost.Children.Add(ClippyControls.Caption("Your instant replay button for Windows.", TextAlignment.Center));
    }

    private void ShowIntro()
    {
        var cards = new[]
        {
            ("\uE823", "Always buffering", "Clippy quietly records the last minute of your screen in the background — ready whenever you need it."),
            ("\uE714", "Clip in an instant", "Save the last 15–60 seconds with a hotkey, button tap, or voice command."),
            ("\uE9D9", "Just say the word", "Try \"Clippy, clip that\" anytime. Clippy captures your screen, system audio, and microphone together.")
        };

        var (icon, title, body) = cards[_introPage];
        _contentHost.Children.Add(ClippyControls.CreateLogoBadge(88));
        _contentHost.Children.Add(CreateIntroCard(icon, title, body));
    }

    private static Border CreateIntroCard(string iconGlyph, string title, string body)
    {
        return new Border
        {
            MaxWidth = 520,
            Padding = new Thickness(28),
            CornerRadius = ClippyTheme.CardRadius,
            Background = ClippyTheme.SurfaceBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(100, 46, 217, 107)),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new FontIcon
                    {
                        Glyph = iconGlyph,
                        FontSize = 28,
                        Foreground = ClippyTheme.AccentBrush,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    ClippyControls.Heading(title, 22, TextAlignment.Center),
                    ClippyControls.Caption(body, TextAlignment.Center)
                }
            }
        };
    }

    private void ShowMicPicker() => ShowDevicePicker(
        "Choose your microphone",
        "This is the mic Clippy uses for voice commands and clips.",
        AudioDeviceManager.InputDevices,
        AppSettings.Instance.PreferredMicrophoneId,
        id => AppSettings.Instance.PreferredMicrophoneId = id);

    private void ShowOutputPicker() => ShowDevicePicker(
        "Choose your audio output",
        "Clippy captures system audio from your PC. Pick the speakers or headphones you're listening on.",
        AudioDeviceManager.OutputDevices,
        AppSettings.Instance.PreferredAudioOutputId,
        id => AppSettings.Instance.PreferredAudioOutputId = id);

    private void ShowDevicePicker(
        string title,
        string subtitle,
        IReadOnlyList<AudioDevice> devices,
        string selectedId,
        Action<string> onSelect)
    {
        var combo = new ComboBox
        {
            Width = 360,
            HorizontalAlignment = HorizontalAlignment.Center,
            DisplayMemberPath = "Name",
            RequestedTheme = ElementTheme.Dark
        };

        foreach (var device in devices)
        {
            combo.Items.Add(device);
            if (device.Id == selectedId)
            {
                combo.SelectedItem = device;
            }
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is AudioDevice device)
            {
                onSelect(device.Id);
                AppSettings.Instance.Persist();
            }
        };

        _contentHost.Children.Add(ClippyControls.Heading(title, 28, TextAlignment.Center));
        _contentHost.Children.Add(ClippyControls.Caption(subtitle, TextAlignment.Center));
        _contentHost.Children.Add(combo);
    }

    private void ShowVoicePractice()
    {
        _contentHost.Children.Add(ClippyControls.CreateLogoBadge(96, glow: true));
        _contentHost.Children.Add(ClippyControls.Heading("Onboarding complete!", 34, TextAlignment.Center));
        _contentHost.Children.Add(ClippyControls.Caption("Welcome to Clippy. To begin, just say:", TextAlignment.Center));
        _contentHost.Children.Add(new Border
        {
            Padding = new Thickness(24, 14, 24, 14),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(30, 46, 217, 107)),
            Child = new TextBlock
            {
                Text = "“Clippy, clip that”",
                FontSize = 28,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ClippyTheme.AccentBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        });

        var heard = ClippyControls.Caption("", TextAlignment.Center);
        VoiceCommandListener.Instance.StateChanged += () =>
            DispatcherQueue.TryEnqueue(() =>
            {
                heard.Text = string.IsNullOrEmpty(VoiceCommandListener.Instance.LastHeardPhrase)
                    ? VoiceCommandListener.Instance.StatusMessage
                    : $"Heard: \"{VoiceCommandListener.Instance.LastHeardPhrase}\"";
            });

        _contentHost.Children.Add(heard);
        AppCoordinator.Instance.BeginOnboardingVoicePractice();
    }
}
