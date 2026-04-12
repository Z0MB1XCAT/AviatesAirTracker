using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Models;

namespace AviatesAirTracker.Services;

/// <summary>
/// Abstraction for future Aviates Air backend API integration.
/// Currently unused — wire in API implementations here when the backend is ready.
/// </summary>
public interface IApiService
{
    Task<bool> IsAvailableAsync();
    Task SyncFlightAsync(FlightRecord flight);
    Task<List<FlightRecord>> FetchFlightHistoryAsync(string pilotId);
    Task<PilotProfile?> FetchPilotProfileAsync(string pilotId);
}
