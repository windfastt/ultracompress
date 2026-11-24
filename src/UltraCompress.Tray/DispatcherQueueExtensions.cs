using Microsoft.UI.Dispatching;

namespace UltraCompress.Tray;

internal static class DispatcherQueueExtensions
{
    public static Task EnqueueAsync(this DispatcherQueue dispatcher, Action action)
    {
        var tcs = new TaskCompletionSource();
        if (!dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
        {
            tcs.SetException(new InvalidOperationException("Dispatcher queue rejected enqueue."));
        }

        return tcs.Task;
    }
}