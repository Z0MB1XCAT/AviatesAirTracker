using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Core.SimConnect;
using Serilog;

namespace AviatesAirTracker.Services;

// ============================================================
// ACARS POSITION SERVICE
//
// Sends live aircraft position reports to the Aviates Air backend
// while a flight is in progress. Mirrors DiscordPresenceService
// in structure: subscribes to TelemetryUpdated and throttles calls.
//
// Outer gate: 30-second check interval (avoids spawning a Task
// on every 20Hz tick). The AviatesBackendClient.SendPositionReportAsync
// has its own inner 5-minute throttle for the actual HTTP call.
// ============================================================

public class AcarsPositionService : IDisposable
{
    private readonly FlightSessionManager _session;
    private readonly AviatesBackendClient _backend;
    private readonly SettingsService _settings;

    private DateTime _lastCheck = DateTime.MinValue;
    private const int CHECK_INTERVAL_SECONDS = 30;

    public AcarsPositionService(
        FlightSessionManager session,
        AviatesBackendClient backend,
        SettingsService settings)
    {
        _session = session;
        _backend = backend;
        _settings = settings;

        _session.TelemetryUpdated += OnTelemetryUpdated;
        Log.Information("[AcarsPosition] Position reporting service initialized");
    }

    private void OnTelemetryUpdated(object? sender, TelemetrySnapshot snap)
    {
        // Only report when actively airborne — no need to ping during taxi or idle
        if (_session.State is not (FlightSessionState.Airborne
            or FlightSessionState.OnApproach
            or FlightSessionState.Landed))
            return;

        // Outer throttle: avoid spawning a Task every 50ms
        if ((DateTime.UtcNow - _lastCheck).TotalSeconds < CHECK_INTERVAL_SECONDS)
            return;

        _lastCheck = DateTime.UtcNow;

        var key = _settings.Settings.AcarsKey.Trim();
        if (string.IsNullOrEmpty(key)) return;

        // Backend method self-throttles to one call per 5 minutes
        _ = _backend.SendPositionReportAsync(
            snap.Latitude,
            snap.Longitude,
            snap.AltitudePressure,    // pressure altitude = flight level reference
            (int)snap.GroundSpeedKts,
            snap.Phase.ToString(),
            key);
    }

    public void Dispose()
    {
        _session.TelemetryUpdated -= OnTelemetryUpdated;
        Log.Debug("[AcarsPosition] Disposed");
    }
}
