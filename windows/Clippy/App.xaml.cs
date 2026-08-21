using Clippy.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Clippy;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
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
