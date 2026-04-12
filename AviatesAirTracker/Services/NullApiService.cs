using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Models;

namespace AviatesAirTracker.Services;

/// <summary>
/// No-op implementation of IApiService. Registered in DI so the interface is injectable
/// but harmless until the real Aviates Air backend exists.
/// </summary>
public class NullApiService : IApiService
{
    public Task<bool> IsAvailableAsync() => Task.FromResult(false);
    public Task SyncFlightAsync(FlightRecord flight) => Task.CompletedTask;
    public Task<List<FlightRecord>> FetchFlightHistoryAsync(string pilotId) => Task.FromResult(new List<FlightRecord>());
    public Task<PilotProfile?> FetchPilotProfileAsync(string pilotId) => Task.FromResult<PilotProfile?>(null);
}
