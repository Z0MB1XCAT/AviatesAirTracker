using System.Runtime.ExceptionServices;
using Microsoft.UI.Dispatching;

namespace AviatesAirTracker.Platform;

/// <summary>
/// <see cref="IUiDispatcher"/> backed by WinUI's <see cref="DispatcherQueue"/>.
/// </summary>
public sealed class DispatcherQueueUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public DispatcherQueueUiDispatcher(DispatcherQueue queue) => _queue = queue;

    public void Invoke(Action action)
    {
        // Already on the UI thread — run inline. Enqueueing and waiting here would
        // deadlock, because the wait would block the very thread that must run the work.
        if (_queue.HasThreadAccess)
        {
            action();
            return;
        }

        using var done = new ManualResetEventSlim(false);
        Exception? captured = null;

        if (!_queue.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
            finally { done.Set(); }
        }))
        {
            // The queue is shutting down (app closing). Dropping the work is correct —
            // throwing here would turn every in-flight telemetry callback into a crash
            // during exit.
            return;
        }

        done.Wait();
        if (captured != null)
            ExceptionDispatchInfo.Capture(captured).Throw();
    }

    public void BeginInvoke(Action action) => _queue.TryEnqueue(() => action());
}
