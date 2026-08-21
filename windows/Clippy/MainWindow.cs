using System.Runtime.InteropServices;
using Clippy.Services;
using Clippy.Theme;
using Clippy.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace Clippy;

public sealed class MainWindow : Window
{
    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);
    private readonly WndProcDelegate _wndProcDelegate;
    private readonly ContentControl _rootHost;
    private nint _originalWndProc;

    public MainWindow()
    {
        _wndProcDelegate = WndProc;

        Title = "Clippy";

        _rootHost = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };

        var root = new Grid { Background = ClippyTheme.BackgroundBrush };
        root.Children.Add(_rootHost);
        Content = root;

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(980, 680));

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "clippy-icon.ico");
        if (File.Exists(iconPath))
        {
            appWindow.SetIcon(iconPath);
        }

        SubclassWindow(hwnd);
        AppCoordinator.Instance.Bootstrap(hwnd);

        AppCoordinator.Instance.StateChanged += OnCoordinatorStateChanged;
        ScreenRecorder.Instance.StateChanged += OnRecorderStateChanged;
        VoiceCommandListener.Instance.StateChanged += OnVoiceStateChanged;

        Closed += OnClosed;

        UpdateRootContent();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        AppCoordinator.Instance.StateChanged -= OnCoordinatorStateChanged;
        ScreenRecorder.Instance.StateChanged -= OnRecorderStateChanged;
        VoiceCommandListener.Instance.StateChanged -= OnVoiceStateChanged;

        HotkeyManager.Instance.Dispose();
        _ = ScreenRecorder.Instance.StopCaptureAsync();
        _ = VoiceCommandListener.Instance.StopListeningAsync();
    }

    private void SubclassWindow(nint hwnd)
    {
        _originalWndProc = SetWindowLongPtrW(
            hwnd, GwlpWndproc, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (HotkeyManager.Instance.ProcessMessage(msg, wParam))
        {
            return nint.Zero;
        }

        return CallWindowProcW(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    private void OnCoordinatorStateChanged() =>
        DispatcherQueue.TryEnqueue(UpdateRootContent);

    private void OnRecorderStateChanged() =>
        DispatcherQueue.TryEnqueue(RefreshHostedContent);

    private void OnVoiceStateChanged() =>
        DispatcherQueue.TryEnqueue(RefreshHostedContent);

    private void UpdateRootContent()
    {
        if (AppCoordinator.Instance.ShowOnboarding)
        {
            if (_rootHost.Content is not OnboardingPage)
            {
                _rootHost.Content = new OnboardingPage();
            }
        }
        else if (_rootHost.Content is not MainContentPage)
        {
            _rootHost.Content = new MainContentPage();
        }
        else
        {
            RefreshHostedContent();
        }
    }

    private void RefreshHostedContent()
    {
        if (_rootHost.Content is MainContentPage page)
        {
            page.RefreshState();
        }
    }

    private const int GwlpWndproc = -4;

    // The W variants matter: WinUI registers its window class as Unicode, and pairing it
    // with the ANSI entry points mangles text in any message we pass through.
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", ExactSpelling = true)]
    private static extern nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW", ExactSpelling = true)]
    private static extern nint CallWindowProcW(nint lpPrevWndProc, nint hWnd, uint msg, nint wParam, nint lParam);
}
