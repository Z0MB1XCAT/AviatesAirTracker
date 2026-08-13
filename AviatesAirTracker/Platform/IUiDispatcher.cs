namespace AviatesAirTracker.Platform;

/// <summary>
/// Marshals work onto the UI thread. Exists so the ViewModel layer depends on no UI
/// framework type — this replaced direct <c>Application.Current.Dispatcher</c> calls
/// during the WinUI 3 migration, and keeps the 20Hz telemetry path testable.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Runs <paramref name="action"/> on the UI thread and waits for it.</summary>
    void Invoke(Action action);

    /// <summary>Queues <paramref name="action"/> on the UI thread without waiting.</summary>
    void BeginInvoke(Action action);
}
