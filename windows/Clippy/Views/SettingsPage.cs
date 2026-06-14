using Clippy.Models;
using Clippy.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Clippy.Views;

public sealed class SettingsPage : UserControl
{
    private readonly StackPanel _logPanel;
    private readonly TextBlock _lastClipSummary;
    private readonly ComboBox _micCombo;
    private readonly ComboBox _outputCombo;
    private readonly ComboBox _displayCombo;
    private readonly TextBlock _voiceStatus;
    private readonly TextBlock _voiceHeard;

    public SettingsPage()
    {
        var scroll = new ScrollViewer();
        var root = new StackPanel { Spacing = 18, Padding = new Thickness(28) };

        root.Children.Add(CreateSection("Clip Length", CreateClipDurationPicker()));
        root.Children.Add(CreateSection("Video Quality", CreateQualityPicker()));
        root.Children.Add(CreateSection("Display", _displayCombo = CreateDisplayPicker()));
        root.Children.Add(CreateSection("Keyboard Shortcut", CreateHotkeyPanel()));
        root.Children.Add(CreateSection("Audio", CreateAudioPanel()));
        root.Children.Add(CreateSection("Voice Commands", CreateVoicePanel()));
        root.Children.Add(CreateSection("Feedback", CreateSoundToggle()));
        root.Children.Add(CreateSection("Debug Log", CreateDebugPanel()));

        scroll.Content = root;
        Content = scroll;
        Loaded += (_, _) => RefreshDevices();
    }

    private void RefreshDevices()
    {
        var settings = AppSettings.Instance;
        _micCombo.Items.Clear();
        foreach (var device in AudioDeviceManager.InputDevices)
        {
            _micCombo.Items.Add(device);
        }

        _outputCombo.Items.Clear();
        foreach (var device in AudioDeviceManager.OutputDevices)
        {
            _outputCombo.Items.Add(device);
        }

        _displayCombo.Items.Clear();
        foreach (var display in DisplayManager.RefreshDisplays())
        {
            _displayCombo.Items.Add(display);
        }

        SelectDevice(_micCombo, settings.PreferredMicrophoneId);
        SelectDevice(_outputCombo, settings.PreferredAudioOutputId);
        SelectDisplay(settings.PreferredDisplayId);
    }

    private static void SelectDevice(ComboBox combo, string id)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is AudioDevice d && d.Id == id)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    private void SelectDisplay(string id)
    {
        for (var i = 0; i < _displayCombo.Items.Count; i++)
        {
            if (_displayCombo.Items[i] is CaptureDisplay d && d.Id == id)
            {
                _displayCombo.SelectedIndex = i;
                return;
            }
        }
    }

    private UIElement CreateClipDurationPicker()
    {
        var panel = new StackPanel { Spacing = 8 };
        var group = new RadioButtons();
        foreach (var duration in ClipDurationExtensions.All)
        {
            group.Items.Add(new ComboBoxItem { Content = duration.Label(), Tag = duration });
        }

        group.SelectedIndex = AppSettings.Instance.ClipDuration switch
        {
            ClipDuration.Fifteen => 0,
            ClipDuration.Sixty => 2,
            _ => 1
        };

        group.SelectionChanged += (_, _) =>
        {
            if (group.SelectedItem is ComboBoxItem { Tag: ClipDuration d })
            {
                AppSettings.Instance.ClipDuration = d;
                AppSettings.Instance.Persist();
            }
        };

        panel.Children.Add(group);
        panel.Children.Add(new TextBlock
        {
            Text = "Clips use up to this length. If the buffer has less, Clippy saves whatever is available.",
            FontSize = 12,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255))
        });
        return panel;
    }

    private UIElement CreateQualityPicker()
    {
        var panel = new StackPanel { Spacing = 10 };
        var resCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var res in CaptureResolutionExtensions.All)
        {
            resCombo.Items.Add(new ComboBoxItem { Content = res.Label(), Tag = res });
        }

        resCombo.SelectedIndex = AppSettings.Instance.CaptureResolution switch
        {
            CaptureResolution.P360 => 0,
            CaptureResolution.P1080 => 2,
            CaptureResolution.P1440 => 3,
            _ => 1
        };

        var fpsCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var fps in CaptureFrameRateExtensions.All)
        {
            fpsCombo.Items.Add(new ComboBoxItem { Content = fps.Label(), Tag = fps });
        }

        fpsCombo.SelectedIndex = AppSettings.Instance.CaptureFrameRate switch
        {
            CaptureFrameRate.Fps15 => 0,
            CaptureFrameRate.Fps60 => 2,
            CaptureFrameRate.Fps120 => 3,
            _ => 1
        };

        resCombo.SelectionChanged += (_, _) =>
        {
            if (resCombo.SelectedItem is ComboBoxItem { Tag: CaptureResolution r })
            {
                AppSettings.Instance.CaptureResolution = r;
                AppSettings.Instance.Persist();
                AppCoordinator.Instance.RefreshCaptureQuality();
            }
        };

        fpsCombo.SelectionChanged += (_, _) =>
        {
            if (fpsCombo.SelectedItem is ComboBoxItem { Tag: CaptureFrameRate f })
            {
                AppSettings.Instance.CaptureFrameRate = f;
                AppSettings.Instance.Persist();
                AppCoordinator.Instance.RefreshCaptureQuality();
            }
        };

        panel.Children.Add(new TextBlock { Text = "Resolution", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(resCombo);
        panel.Children.Add(new TextBlock { Text = "Frame rate", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(fpsCombo);
        return panel;
    }

    private ComboBox CreateDisplayPicker()
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.DisplayMemberPath = "Label";
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is CaptureDisplay display)
            {
                AppSettings.Instance.PreferredDisplayId = display.Id;
                AppSettings.Instance.Persist();
                AppCoordinator.Instance.RefreshDisplay();
            }
        };
        return combo;
    }

    private UIElement CreateHotkeyPanel()
    {
        var settings = AppSettings.Instance;
        var label = new TextBlock
        {
            Text = settings.Hotkey.DisplayString,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold
        };

        var reset = new Button
        {
            Content = "Reset to Ctrl+K",
            Margin = new Thickness(0, 8, 0, 0)
        };
        reset.Click += (_, _) =>
        {
            AppSettings.Instance.Hotkey = HotkeyBinding.Default;
            AppSettings.Instance.Persist();
            label.Text = AppSettings.Instance.Hotkey.DisplayString;
            AppCoordinator.Instance.RefreshHotkey();
        };

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "Default shortcut is Ctrl+K. Custom key capture coming soon on Windows.",
                    FontSize = 12,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255))
                },
                label,
                reset
            }
        };
    }

    private UIElement CreateAudioPanel()
    {
        _micCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Name" };
        _outputCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Name" };

        _micCombo.SelectionChanged += (_, _) =>
        {
            if (_micCombo.SelectedItem is AudioDevice device)
            {
                AppSettings.Instance.PreferredMicrophoneId = device.Id;
                AppSettings.Instance.Persist();
                AppCoordinator.Instance.RefreshMicrophone();
                AppCoordinator.Instance.RefreshCaptureQuality();
            }
        };

        _outputCombo.SelectionChanged += (_, _) =>
        {
            if (_outputCombo.SelectedItem is AudioDevice device)
            {
                AppSettings.Instance.PreferredAudioOutputId = device.Id;
                AppSettings.Instance.Persist();
                AppCoordinator.Instance.RefreshCaptureQuality();
            }
        };

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Clips include system audio from the recorded display plus your microphone.",
                    TextWrapping = TextWrapping.WrapWholeWords,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255))
                },
                new TextBlock { Text = "System audio output", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                _outputCombo,
                new TextBlock { Text = "Microphone", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                _micCombo
            }
        };
    }

    private UIElement CreateVoicePanel()
    {
        _voiceStatus = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)) };
        _voiceHeard = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(255, 46, 217, 107)) };

        var toggle = new ToggleSwitch
        {
            IsOn = AppSettings.Instance.VoiceCommandsEnabled,
            OnContent = "On",
            OffContent = "Off"
        };
        toggle.Toggled += (_, _) =>
        {
            AppSettings.Instance.VoiceCommandsEnabled = toggle.IsOn;
            AppSettings.Instance.Persist();
            AppCoordinator.Instance.RefreshVoiceListening();
        };

        VoiceCommandListener.Instance.StateChanged += () =>
            DispatcherQueue.TryEnqueue(() =>
            {
                _voiceStatus.Text = VoiceCommandListener.Instance.StatusMessage;
                _voiceHeard.Text = string.IsNullOrEmpty(VoiceCommandListener.Instance.LastHeardPhrase)
                    ? ""
                    : $"Heard: \"{VoiceCommandListener.Instance.LastHeardPhrase}\"";
            });

        _voiceStatus.Text = VoiceCommandListener.Instance.StatusMessage;

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Listen for \"Clippy, do your thing\" and \"Clippy, clip that\"" },
                toggle,
                _voiceStatus,
                _voiceHeard
            }
        };
    }

    private UIElement CreateSoundToggle()
    {
        var toggle = new ToggleSwitch
        {
            IsOn = AppSettings.Instance.SoundEnabled,
            OnContent = "Play sound when clipping",
            OffContent = "Muted"
        };
        toggle.Toggled += (_, _) =>
        {
            AppSettings.Instance.SoundEnabled = toggle.IsOn;
            AppSettings.Instance.Persist();
        };
        return toggle;
    }

    private UIElement CreateDebugPanel()
    {
        _logPanel = new StackPanel { Spacing = 6 };
        _lastClipSummary = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255))
        };
        _lastClipSummary.Text = ScreenRecorder.Instance.LastClipDebugSummary;

        var refresh = new Button { Content = "Refresh diagnostics" };
        refresh.Click += (_, _) =>
        {
            ClippyDebugLog.Instance.Log("Debug", RecorderDiagnostics.Snapshot(ScreenRecorder.Instance));
            VoiceDiagnostics.LogSnapshot(VoiceCommandListener.Instance);
            RefreshLog(_logPanel);
            _lastClipSummary.Text = ScreenRecorder.Instance.LastClipDebugSummary;
        };

        var copy = new Button { Content = "Copy log" };
        copy.Click += (_, _) =>
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(ClippyDebugLog.Instance.ExportText);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        };

        var clear = new Button { Content = "Clear" };
        clear.Click += (_, _) =>
        {
            ClippyDebugLog.Instance.Clear();
            RefreshLog(_logPanel);
        };

        RefreshLog(_logPanel);

        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Detailed errors from clipping, recording, and voice recognition appear here.",
                    TextWrapping = TextWrapping.WrapWholeWords,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255))
                },
                new TextBlock { Text = "Last clip attempt", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                _lastClipSummary,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { refresh, copy, clear }
                },
                new Border
                {
                    MaxHeight = 280,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(64, 0, 0, 0)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10),
                    Child = new ScrollViewer { Content = _logPanel }
                }
            }
        };
    }

    private static void RefreshLog(StackPanel panel)
    {
        panel.Children.Clear();
        foreach (var entry in ClippyDebugLog.Instance.Entries)
        {
            panel.Children.Add(new TextBlock
            {
                Text = entry.Formatted,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255))
            });
        }
    }

    private static Border CreateSection(string title, UIElement content)
    {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
        });
        stack.Children.Add(content);

        return new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 20, 20, 20)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18),
            Child = stack
        };
    }
}
