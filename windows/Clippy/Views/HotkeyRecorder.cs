using Clippy.Models;
using Clippy.Services;
using Clippy.Theme;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace Clippy.Views;

/// <summary>
/// Captures a new global shortcut, mirroring the macOS key recorder: press Change, press
/// the combination, and it takes effect immediately.
/// </summary>
public sealed class HotkeyRecorder : UserControl
{
    private readonly TextBlock _label;
    private readonly TextBlock _hint;
    private readonly Button _changeButton;
    private readonly Border _frame;
    private bool _isRecording;

    public HotkeyRecorder()
    {
        _label = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = ClippyTheme.TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 140
        };

        _hint = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.WrapWholeWords,
            Foreground = ClippyTheme.TextSecondaryBrush
        };

        _changeButton = new Button { Content = "Change" };
        _changeButton.Click += (_, _) => Toggle();

        var reset = new Button { Content = "Reset" };
        reset.Click += (_, _) =>
        {
            StopRecording();
            Apply(HotkeyBinding.Default);
        };

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(_label, 0);
        Grid.SetColumn(_changeButton, 1);
        Grid.SetColumn(reset, 2);
        row.Children.Add(_label);
        row.Children.Add(_changeButton);
        row.Children.Add(reset);

        _frame = new Border
        {
            Background = ClippyTheme.SurfaceElevatedBrush,
            BorderBrush = ClippyTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Child = row
        };

        Content = new StackPanel
        {
            Spacing = 8,
            Children = { _frame, _hint }
        };

        // The control itself must take focus for key events to reach it.
        IsTabStop = true;
        KeyDown += OnKeyDown;
        LostFocus += (_, _) => StopRecording();

        UpdateDisplay();
    }

    private void Toggle()
    {
        if (_isRecording)
        {
            StopRecording();
        }
        else
        {
            StartRecording();
        }
    }

    private void StartRecording()
    {
        _isRecording = true;

        // Release the global hotkey while recording, otherwise pressing the current
        // combination would fire a clip instead of being captured here.
        HotkeyManager.Instance.Unregister();

        _changeButton.Content = "Cancel";
        _label.Text = "Press keys…";
        _label.Foreground = ClippyTheme.AccentBrush;
        _frame.BorderBrush = ClippyTheme.AccentBrush;
        _frame.BorderThickness = new Thickness(2);
        _hint.Text = "Hold a modifier (Ctrl, Alt, Shift or Win) and press a key. Esc cancels.";

        Focus(FocusState.Programmatic);
    }

    private void StopRecording()
    {
        if (!_isRecording) return;
        _isRecording = false;

        _changeButton.Content = "Change";
        _frame.BorderThickness = new Thickness(1);
        HotkeyManager.Instance.Register(AppSettings.Instance.Hotkey);
        UpdateDisplay();
    }

    private void OnKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (!_isRecording) return;

        e.Handled = true;

        if (e.Key == VirtualKey.Escape)
        {
            StopRecording();
            return;
        }

        if (IsModifierKey(e.Key))
        {
            return; // Wait for the actual key.
        }

        var modifiers = CurrentModifiers();
        if (modifiers == HotkeyModifiers.None)
        {
            // A bare key would swallow that key system-wide, in every application.
            _hint.Text = "That needs a modifier — try Ctrl, Alt, Shift or Win with the key.";
            return;
        }

        StopRecordingWithoutRestore();
        Apply(new HotkeyBinding
        {
            VirtualKey = (uint)e.Key,
            Modifiers = (uint)modifiers
        });
    }

    private void StopRecordingWithoutRestore()
    {
        _isRecording = false;
        _changeButton.Content = "Change";
        _frame.BorderThickness = new Thickness(1);
    }

    private void Apply(HotkeyBinding binding)
    {
        var previous = AppSettings.Instance.Hotkey;

        if (!HotkeyManager.Instance.Register(binding))
        {
            _hint.Text = $"{binding.DisplayString} is already in use by another app — pick a different one.";
            AppSettings.Instance.Hotkey = previous;
            UpdateDisplay();
            return;
        }

        AppSettings.Instance.Hotkey = binding;
        AppSettings.Instance.Persist();
        ClippyDebugLog.Instance.Log("Hotkey", $"Shortcut set to {binding.DisplayString}");
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        _label.Text = AppSettings.Instance.Hotkey.DisplayString;
        _label.Foreground = ClippyTheme.TextPrimaryBrush;
        _frame.BorderBrush = ClippyTheme.BorderBrush;
        _hint.Text = "Press Change, then the key combination you want. Default is Ctrl+K.";
    }

    private static bool IsModifierKey(VirtualKey key) => key is
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static HotkeyModifiers CurrentModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        if (IsDown(VirtualKey.Control)) modifiers |= HotkeyModifiers.Control;
        if (IsDown(VirtualKey.Menu)) modifiers |= HotkeyModifiers.Alt;
        if (IsDown(VirtualKey.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
        {
            modifiers |= HotkeyModifiers.Win;
        }

        return modifiers;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
