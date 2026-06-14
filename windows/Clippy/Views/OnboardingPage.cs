using Clippy.Models;
using Clippy.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace Clippy.Views;

public sealed class OnboardingPage : UserControl
{
    private int _step;
    private int _introPage;
    private readonly StackPanel _contentHost;
    private readonly StackPanel _progressBar;
    private readonly Button _primaryButton;
    private readonly Button _backButton;
    private readonly Button _skipButton;

    private const int StepCount = 5;

    public OnboardingPage()
    {
        MinWidth = 900;
        MinHeight = 620;

        var root = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 10, 10, 10))
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _progressBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(40, 28, 40, 0)
        };

        _contentHost = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 24
        };

        var footer = new Grid { Margin = new Thickness(40, 0, 40, 36) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _backButton = new Button { Content = "Back", Visibility = Visibility.Collapsed };
        _backButton.Click += (_, _) => GoBack();

        _primaryButton = new Button
        {
            Content = "Next",
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 46, 217, 107)),
            Foreground = new SolidColorBrush(Colors.Black),
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(24, 12, 24, 12)
        };
        _primaryButton.Click += (_, _) => GoForward();

        _skipButton = new Button { Content = "Skip for now", Visibility = Visibility.Collapsed };
        _skipButton.Click += (_, _) => AppCoordinator.Instance.CompleteOnboarding(fromVoiceDemo: false);

        Grid.SetColumn(_backButton, 0);
        Grid.SetColumn(_primaryButton, 2);
        Grid.SetColumn(_skipButton, 2);
        footer.Children.Add(_backButton);
        footer.Children.Add(_primaryButton);
        footer.Children.Add(_skipButton);

        Grid.SetRow(_progressBar, 0);
        Grid.SetRow(_contentHost, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(_progressBar);
        root.Children.Add(_contentHost);
        root.Children.Add(footer);

        Content = root;
        RenderStep();
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
        _backButton.Visibility = _step == 0 || _step == 4 ? Visibility.Collapsed : Visibility.Visible;
        _skipButton.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;
        _primaryButton.Visibility = _step == 4 ? Visibility.Collapsed : Visibility.Visible;
        _primaryButton.Content = _step == 3 ? "Continue" : "Next";

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
            _progressBar.Children.Add(new Border
            {
                Height = 4,
                Width = 120,
                CornerRadius = new CornerRadius(999),
                Background = new SolidColorBrush(i <= _step
                    ? Windows.UI.Color.FromArgb(255, 46, 217, 107)
                    : Windows.UI.Color.FromArgb(20, 255, 255, 255))
            });
        }
    }

    private void ShowWelcome()
    {
        _contentHost.Children.Add(CreateLogo(120));
        _contentHost.Children.Add(new TextBlock
        {
            Text = "Welcome to Clippy!",
            FontSize = 42,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Colors.White)
        });
        _contentHost.Children.Add(new TextBlock
        {
            Text = "Your instant replay button for Windows.",
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255))
        });
    }

    private void ShowIntro()
    {
        var cards = new[]
        {
            ("Always buffering", "Clippy quietly records the last minute of your screen in the background — ready whenever you need it."),
            ("Clip in an instant", "Save the last 15–60 seconds with a hotkey, button tap, or voice command."),
            ("Just say the word", "Try \"Clippy, clip that\" anytime. Clippy captures your screen, system audio, and microphone together.")
        };

        var (title, body) = cards[_introPage];
        _contentHost.Children.Add(CreateLogo(88));
        _contentHost.Children.Add(new Border
        {
            MaxWidth = 520,
            Padding = new Thickness(28),
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 20, 20, 20)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 46, 217, 107)),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 22,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = new SolidColorBrush(Colors.White)
                    },
                    new TextBlock
                    {
                        Text = body,
                        TextWrapping = TextWrapping.WrapWholeWords,
                        TextAlignment = TextAlignment.Center,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255))
                    }
                }
            }
        });
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
            DisplayMemberPath = "Name"
        };

        foreach (var device in devices)
        {
            combo.Items.Add(device);
            if (device.Id == selectedId) combo.SelectedItem = device;
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is AudioDevice device)
            {
                onSelect(device.Id);
                AppSettings.Instance.Persist();
            }
        };

        _contentHost.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Colors.White)
        });
        _contentHost.Children.Add(new TextBlock
        {
            Text = subtitle,
            TextWrapping = TextWrapping.WrapWholeWords,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255))
        });
        _contentHost.Children.Add(combo);
    }

    private void ShowVoicePractice()
    {
        _contentHost.Children.Add(CreateLogo(96));
        _contentHost.Children.Add(new TextBlock
        {
            Text = "Onboarding complete!",
            FontSize = 34,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Colors.White)
        });
        _contentHost.Children.Add(new TextBlock
        {
            Text = "Welcome to Clippy. To begin, just say:",
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255))
        });
        _contentHost.Children.Add(new Border
        {
            Padding = new Thickness(24, 14, 24, 14),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 46, 217, 107)),
            Child = new TextBlock
            {
                Text = "“Clippy, clip that”",
                FontSize = 28,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 46, 217, 107))
            }
        });

        var heard = new TextBlock
        {
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255))
        };
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

    private static Image CreateLogo(int size)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "clippy-logo.png");
        return new Image
        {
            Width = size,
            Height = size,
            Source = File.Exists(path) ? new BitmapImage(new Uri(path)) : null,
            HorizontalAlignment = HorizontalAlignment.Center
        };
    }
}
