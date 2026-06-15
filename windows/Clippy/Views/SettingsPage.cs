using Clippy.Models;
using Clippy.Services;
using Clippy.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Clippy.Views;

public sealed class SettingsPage : UserControl
{
    private StackPanel _logPanel = null!;
    private TextBlock _lastClipSummary = null!;
    private ComboBox _micCombo = null!;
    private ComboBox _outputCombo = null!;
    private readonly ComboBox _displayCombo;
    private TextBlock _voiceStatus = null!;
    private TextBlock _voiceHeard = null!;

    public SettingsPage()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Background = ClippyTheme.BackgroundBrush;

        var scroll = new ScrollViewer
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var root = new StackPanel { Spacing = 18, Padding = new Thickness(28) };

        root.Children.Add(ClippyControls.CreateSection("Clip Length", CreateClipDurationPicker(), "\uE823"));
        root.Children.Add(ClippyControls.CreateSection("Video Quality", CreateQualityPicker(), "\uE714"));
        root.Children.Add(ClippyControls.CreateSection("Display", _displayCombo = CreateDisplayPicker(), "\uE7F4"));
        root.Children.Add(ClippyControls.CreateSection("Keyboard Shortcut", CreateHotkeyPanel(), "\uE765"));
        root.Children.Add(ClippyControls.CreateSection("Audio", CreateAudioPanel(), "\uE767"));
        root.Children.Add(ClippyControls.CreateSection("Voice Commands", CreateVoicePanel(), "\uE9D9"));
        root.Children.Add(ClippyControls.CreateSection("Feedback", CreateSoundToggle(), "\uE767"));
        root.Children.Add(ClippyControls.CreateSection("Debug Log", CreateDebugPanel(), "\uE7BA"));

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
        var durations = ClipDurationExtensions.All.ToList();
        var picker = new SegmentedPicker(
            durations.Select(d => d.Label()).ToList(),
            durations.Cast<object>().ToList(),
            AppSettings.Instance.ClipDuration switch
            {
                ClipDuration.Fifteen => 0,
                ClipDuration.Sixty => 2,
                _ => 1
            });
        picker.SelectionChanged += tag =>
        {
            if (tag is ClipDuration d)
            {
                AppSettings.Instance.ClipDuration = d;
                AppSettings.Instance.Persist();
            }
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(picker);
        panel.Children.Add(ClippyControls.Caption(
            "Clips use up to this length. If the buffer has less, Clippy saves whatever is available."));
        return panel;
    }

    private UIElement CreateQualityPicker()
    {
        var resolutions = CaptureResolutionExtensions.All.ToList();
        var resPicker = new SegmentedPicker(
            resolutions.Select(r => r.Label()).ToList(),
            resolutions.Cast<object>().ToList(),
            AppSettings.Instance.CaptureResolution switch
            {
                CaptureResolution.P360 => 0,
                CaptureResolution.P1080 => 2,
                CaptureResolution.P1440 => 3,
                _ => 1
            });
        resPicker.SelectionChanged += tag =>
        {
            if (tag is CaptureResolution r)
            {
                AppSettings.Instance.CaptureResolution = r;
                AppSettings.Instance.Persist();
                AppCoordinator.Instance.RefreshCaptureQuality();
            }
        };

        var frameRates = CaptureFrameRateExtensions.All.ToList();
        var fpsPicker = new SegmentedPicker(
            frameRates.Select(f => f.Label()).ToList(),
            frameRates.Cast<object>().ToList(),
            AppSettings.Instance.CaptureFrameRate switch
            {
                CaptureFrameRate.Fps15 => 0,
                CaptureFrameRate.Fps60 => 2,
                CaptureFrameRate.Fps120 => 3,
                _ => 1
            });
        fpsPicker.SelectionChanged += tag =>
        {
            if (tag is CaptureFrameRate f)
            {
                AppSettings.Instance.CaptureFrameRate = f;
                AppSettings.Instance.Persist();
                AppCoordinator.Instance.RefreshCaptureQuality();
            }
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "Resolution",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ClippyTheme.TextPrimaryBrush
        });
        panel.Children.Add(resPicker);
        panel.Children.Add(new TextBlock
        {
            Text = "Frame rate",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ClippyTheme.TextPrimaryBrush
        });
        panel.Children.Add(fpsPicker);
        panel.Children.Add(ClippyControls.Caption(
            "Default is 720p at 30 fps to keep Clippy light on your system. Higher settings use more CPU and disk."));
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
                ClippyControls.Caption("Requires Windows Settings → Privacy → Speech → Online speech recognition"),
                toggle,
                ClippyControls.CreateSecondaryButton("Open Speech Settings", (_, _) =>
                    VoiceCommandListener.OpenSpeechPrivacySettings()),
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
}
