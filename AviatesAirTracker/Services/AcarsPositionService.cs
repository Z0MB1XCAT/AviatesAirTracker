using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using Serilog;

namespace AviatesAirTracker.Services;

// ============================================================
// ACARS POSITION SERVICE
//
// Sends live aircraft position reports to the Aviates Air backend
// while a flight is in progress, and registers/deregisters the
// pilot's flight on the live map at the right phase transitions.
//
// Outer gate: 30-second check interval (avoids spawning a Task
// on every 20Hz tick). AviatesBackendClient.SendPositionReportAsync
// has its own inner 30-second throttle for the actual HTTP call.
// ============================================================

public class AcarsPositionService : IDisposable
{
    private readonly FlightSessionManager _session;
    private readonly AviatesBackendClient _backend;
    private readonly SettingsService _settings;
    private readonly SimBriefService _simBrief;

    // DateTime is not a valid volatile field type (CS0677). Use a long ticks backing field
    // with Interlocked for safe cross-thread access between the SimConnect pump and UI thread.
    private long _lastCheckTicks = DateTime.MinValue.Ticks;
    private const int CHECK_INTERVAL_SECONDS = 30;

    /// <summary>Last time the 30-second gate fired (not necessarily when an HTTP call went out).</summary>
    public DateTime LastCheckAt => new DateTime(Interlocked.Read(ref _lastCheckTicks), DateTimeKind.Utc);

    /// <summary>Raised each time the outer 30-second gate fires.</summary>
    public event Action? GateFired;

    public AcarsPositionService(
        FlightSessionManager session,
        AviatesBackendClient backend,
        SettingsService settings,
        SimBriefService simBrief)
    {
        _session  = session;
        _backend  = backend;
        _settings = settings;
        _simBrief = simBrief;

        _session.TelemetryUpdated    += OnTelemetryUpdated;
        _session.SessionStateChanged += OnSessionStateChanged;
        _simBrief.FlightPlanLoaded   += OnFlightPlanLoaded;
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
        var now = DateTime.UtcNow;
        if ((now.Ticks - Interlocked.Read(ref _lastCheckTicks)) < TimeSpan.TicksPerSecond * CHECK_INTERVAL_SECONDS)
            return;

        Interlocked.Exchange(ref _lastCheckTicks, now.Ticks);
        GateFired?.Invoke();

        if (!_settings.Settings.ShareLivePosition) return;

        var key = _settings.Settings.AcarsKey.Trim();
        if (string.IsNullOrEmpty(key)) return;

        // Backend method self-throttles to one call per 30 seconds
        _ = _backend.SendPositionReportAsync(
            snap.Latitude,
            snap.Longitude,
            snap.AltitudePressure,    // pressure altitude = flight level reference
            (int)snap.GroundSpeedKts,
            (int)snap.Raw.HeadingTrue,
            snap.Phase.ToString(),
            key);
    }

    private void OnSessionStateChanged(object? sender, FlightSessionState newState)
    {
        if (!_settings.Settings.ShareLivePosition) return;
        var key = _settings.Settings.AcarsKey.Trim();
        if (string.IsNullOrEmpty(key)) return;

        if (newState == FlightSessionState.Taxiing)
        {
            var flight = _session.CurrentFlight;
            _ = _backend.SendFlightStartAsync(
                flight?.DepartureICAO, flight?.ArrivalICAO, flight?.Callsign,
                null, key);
        }
        else if (newState == FlightSessionState.Complete)
        {
            _ = _backend.SendFlightEndAsync(key);
        }
    }

    private void OnFlightPlanLoaded(object? sender, SimBriefFlightPlan plan)
    {
        // Only re-announce if a flight is actually in progress — SimBrief plans can be
        // imported before engines start too, and that shouldn't register a "flight" yet.
        if (_session.State is not (FlightSessionState.Taxiing or FlightSessionState.Airborne
            or FlightSessionState.OnApproach or FlightSessionState.Landed))
            return;

        if (!_settings.Settings.ShareLivePosition) return;
        var key = _settings.Settings.AcarsKey.Trim();
        if (string.IsNullOrEmpty(key)) return;

        _ = _backend.SendFlightStartAsync(
            plan.DepartureICAO, plan.ArrivalICAO, _session.CurrentFlight?.Callsign,
            plan.Waypoints, key);
    }

    public void Dispose()
    {
        _session.TelemetryUpdated    -= OnTelemetryUpdated;
        _session.SessionStateChanged -= OnSessionStateChanged;
        _simBrief.FlightPlanLoaded   -= OnFlightPlanLoaded;
        Log.Debug("[AcarsPosition] Disposed");
    }
}
