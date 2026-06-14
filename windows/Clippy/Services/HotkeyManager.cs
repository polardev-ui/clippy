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

    private nint _windowHandle;
    private bool _registered;

    private HotkeyManager()
    {
    }

    public void AttachWindow(nint hwnd)
    {
        _windowHandle = hwnd;
    }

    public void Register(HotkeyBinding binding)
    {
        if (_windowHandle == nint.Zero) return;

        if (_registered)
        {
            UnregisterHotKey(_windowHandle, HotkeyId);
            _registered = false;
        }

        var mods = MapModifiers(binding.Modifiers);
        if (RegisterHotKey(_windowHandle, HotkeyId, mods, binding.VirtualKey))
        {
            _registered = true;
        }
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

    public void Dispose()
    {
        if (_registered && _windowHandle != nint.Zero)
        {
            UnregisterHotKey(_windowHandle, HotkeyId);
            _registered = false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
