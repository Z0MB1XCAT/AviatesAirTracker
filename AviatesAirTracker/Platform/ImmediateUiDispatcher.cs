namespace AviatesAirTracker.Platform;

/// <summary>
/// Runs everything inline on the calling thread. Used by tests, and as a safe fallback
/// before the UI thread's <c>DispatcherQueue</c> exists (e.g. during startup).
/// </summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public void Invoke(Action action) => action();
    public void BeginInvoke(Action action) => action();
}
