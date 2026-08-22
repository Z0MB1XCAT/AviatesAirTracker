namespace AviatesAirTracker.Platform;

/// <summary>
/// Runs everything inline on the calling thread. Available for tests that need an
/// <see cref="IUiDispatcher"/> without a real UI thread behind it.
/// </summary>
/// <remarks>
/// Not used by the app itself: <see cref="App"/> registers <c>DispatcherQueueUiDispatcher</c>
/// unconditionally, built from <c>DispatcherQueue.GetForCurrentThread()</c> inside
/// <c>OnLaunched</c>, which already runs on the UI thread — so there is no startup window
/// where the real queue is unavailable for this to stand in for.
/// </remarks>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public void Invoke(Action action) => action();
    public void BeginInvoke(Action action) => action();
}
