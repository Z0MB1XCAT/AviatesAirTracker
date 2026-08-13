using System.Windows;

namespace AviatesAirTracker.Platform;

/// <summary>
/// TRANSITIONAL — exists only while the app is still on WPF during the WinUI 3 migration.
/// Deleted when <c>DispatcherQueueUiDispatcher</c> takes over. Its only job is to let the
/// ViewModel layer move onto <see cref="IUiDispatcher"/> in a separately verifiable step,
/// rather than changing the threading seam and the UI framework at the same time.
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    public void Invoke(Action action) => Application.Current.Dispatcher.Invoke(action);
    public void BeginInvoke(Action action) => Application.Current.Dispatcher.BeginInvoke(action);
}
