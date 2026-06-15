using System.Runtime.InteropServices;
using Clippy.Services;
using Clippy.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Clippy;

public sealed partial class MainWindow : Window
{
    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);
    private readonly WndProcDelegate _wndProcDelegate;
    private nint _originalWndProc;

    public MainWindow()
    {
        InitializeComponent();
        _wndProcDelegate = WndProc;

        Title = "Clippy";

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(980, 680));

        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "clippy-icon.ico");
        if (System.IO.File.Exists(iconPath))
        {
            appWindow.SetIcon(iconPath);
        }

        SubclassWindow(hwnd);
        AppCoordinator.Instance.Bootstrap(hwnd);

        AppCoordinator.Instance.StateChanged += OnCoordinatorStateChanged;
        ScreenRecorder.Instance.StateChanged += OnRecorderStateChanged;
        VoiceCommandListener.Instance.StateChanged += OnVoiceStateChanged;

        UpdateRootContent();
    }

    private void SubclassWindow(nint hwnd)
    {
        _originalWndProc = SetWindowLongPtr(hwnd, GwlpWndproc, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (HotkeyManager.Instance.ProcessMessage(msg, wParam))
        {
            return nint.Zero;
        }

        return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
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
            if (RootHost.Content is not OnboardingPage)
            {
                RootHost.Content = new OnboardingPage();
            }
        }
        else if (RootHost.Content is not MainContentPage)
        {
            RootHost.Content = new MainContentPage();
        }
        else
        {
            RefreshHostedContent();
        }
    }

    private void RefreshHostedContent()
    {
        if (RootHost.Content is MainContentPage page)
        {
            page.RefreshState();
        }
    }

    private const int GwlpWndproc = -4;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint lpPrevWndProc, nint hWnd, uint msg, nint wParam, nint lParam);
}
