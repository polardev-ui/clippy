using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Clippy;

/// <summary>
/// Entry point. Normally generated from an App.xaml; written out here because the app
/// carries no XAML markup.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            Services.UiDispatcher.Initialize(DispatcherQueue.GetForCurrentThread());
            new App();
        });
    }
}
