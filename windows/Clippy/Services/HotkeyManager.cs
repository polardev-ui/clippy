using System.Runtime.InteropServices;
using Clippy.Models;

namespace Clippy.Services;

public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 1;

    private static HotkeyManager? _instance;
    public static HotkeyManager Instance => _instance ??= new HotkeyManager();

    public Action? OnTrigger { get; set; }

    private const uint ModNoRepeat = 0x4000;

    private nint _windowHandle;
    private bool _registered;
    private HotkeyBinding? _current;

    private HotkeyManager()
    {
    }

    public void AttachWindow(nint hwnd)
    {
        _windowHandle = hwnd;
    }

    /// <summary>
    /// Registers <paramref name="binding"/> as the global hotkey. Returns false if Windows
    /// refused it, which normally means another application already owns that combination.
    /// </summary>
    public bool Register(HotkeyBinding binding)
    {
        if (_windowHandle == nint.Zero) return false;

        Unregister();

        // MOD_NOREPEAT: holding the key down should clip once, not once per repeat.
        var mods = MapModifiers(binding.Modifiers) | ModNoRepeat;
        if (RegisterHotKey(_windowHandle, HotkeyId, mods, binding.VirtualKey))
        {
            _registered = true;
            _current = binding;
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        ClippyDebugLog.Instance.Log("Hotkey",
            $"Could not register {binding.DisplayString} (error {error}) — another app may already use it");

        // Fall back to whatever was working before, so the app is never left with no hotkey.
        if (_current != null && !ReferenceEquals(_current, binding))
        {
            var previous = _current;
            _current = null;
            Register(previous);
        }

        return false;
    }

    public void Unregister()
    {
        if (!_registered || _windowHandle == nint.Zero) return;
        UnregisterHotKey(_windowHandle, HotkeyId);
        _registered = false;
    }

    public bool ProcessMessage(uint msg, nint wParam)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            OnTrigger?.Invoke();
            return true;
        }

        return false;
    }

    private static uint MapModifiers(uint modifiers)
    {
        uint result = 0;
        var m = (HotkeyModifiers)modifiers;
        if (m.HasFlag(HotkeyModifiers.Alt)) result |= 0x0001;
        if (m.HasFlag(HotkeyModifiers.Control)) result |= 0x0002;
        if (m.HasFlag(HotkeyModifiers.Shift)) result |= 0x0004;
        if (m.HasFlag(HotkeyModifiers.Win)) result |= 0x0008;
        return result;
    }

    public void Dispose() => Unregister();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
