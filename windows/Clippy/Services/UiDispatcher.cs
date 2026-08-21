using Microsoft.UI.Dispatching;

namespace Clippy.Services;

/// <summary>
/// Marshals work onto the UI thread. Clips are produced on background threads, but the
/// collections they land in are read by the UI, and WinUI collections have thread affinity.
/// </summary>
public static class UiDispatcher
{
    private static DispatcherQueue? _queue;

    public static void Initialize(DispatcherQueue queue) => _queue = queue;

    public static void Post(Action action)
    {
        var queue = _queue;
        if (queue == null || queue.HasThreadAccess)
        {
            action();
            return;
        }

        queue.TryEnqueue(() => action());
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread and waits for it.</summary>
    public static Task RunAsync(Action action)
    {
        var queue = _queue;
        if (queue == null || queue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }))
        {
            action();
            return Task.CompletedTask;
        }

        return completion.Task;
    }
}
