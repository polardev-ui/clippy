using Clippy.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Clippy;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        // Installed before anything else, so a failure during startup is recorded rather
        // than disappearing into a stowed exception with no managed stack.
        CrashLog.Install(this);

        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Captured here because this runs on the UI thread. Clips are produced on
        // background threads but land in collections the UI reads.
        UiDispatcher.Initialize(DispatcherQueue.GetForCurrentThread());

        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
