namespace AviatesAirTracker.Hosting;

/// <summary>
/// The app's handle on the transitional Blazor UI host, which lives in
/// AviatesAirTracker.LegacyWeb. That assembly references this one, so it cannot be
/// referenced back at compile time — the app loads it by name at startup and finds the
/// single implementation of this interface.
/// </summary>
/// <remarks>Deleted along with the RCL in the final migration phase.</remarks>
public interface ILegacyUiHost : IAsyncDisposable
{
    /// <summary>Starts the loopback host and returns its root URL.</summary>
    Task<string> StartAsync(IServiceProvider appServices);
}
