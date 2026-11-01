using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Models;
using Serilog;

namespace AviatesAirTracker.Services;

// ============================================================
// PIREP SYNC SERVICE
//
// The one path a completed flight takes to the backend, whether it is the submission right after
// block-in, the startup retry of anything that didn't make it, or the pilot pressing Retry / Fix
// on the Flights page. Records the outcome on the flight itself (SyncError / SyncRejected) so the
// Flights page can say why a PIREP hasn't reached Aviates Air, and tells the pilot when one is
// accepted or refused.
//
// A PIREP the server refuses outright (HTTP 400, e.g. no departure/arrival airport) is marked
// SyncRejected and left alone by the automatic retry -- resending the same record can never
// succeed, and used to be retried (and logged as a failure) on every single launch.
// ============================================================

public sealed class PirepSyncService
{
    private readonly AviatesBackendClient _backend;
    private readonly IFlightRepository _flights;
    private readonly SettingsService _settings;
    private readonly DesktopNotificationService _notifications;
    private readonly SemaphoreSlim _retryGate = new(1, 1);

    /// <summary>A flight's sync state changed (submitted, failed, rejected, edited). Raised on
    /// the thread that did the submission.</summary>
    public event EventHandler<FlightRecord>? SyncStateChanged;

    public PirepSyncService(AviatesBackendClient backend, IFlightRepository flights,
        SettingsService settings, DesktopNotificationService notifications)
    {
        _backend = backend;
        _flights = flights;
        _settings = settings;
        _notifications = notifications;
    }

    public static bool IsAwaitingSync(FlightRecord f) =>
        f.Status == FlightStatus.Completed && !f.SyncedToBackend;

    /// <summary>Submits one flight and records the outcome on it. Announces the result when
    /// <paramref name="announce"/> is set (a fresh flight, or a pilot-initiated retry).</summary>
    public async Task<PirepSubmitResult> SubmitAsync(FlightRecord flight, bool announce)
    {
        var key = _settings.Settings.AcarsKey.Trim();
        if (string.IsNullOrEmpty(key))
            return new PirepSubmitResult(false, 0, "Sign in with your ACARS key to send PIREPs.");

        var result = await _backend.SubmitPirepDetailedAsync(flight, key);
        flight.LastSyncAttemptUtc = DateTime.UtcNow;

        if (result.Success)
        {
            flight.SyncedToBackend = true;
            flight.SyncError = null;
            flight.SyncRejected = false;
        }
        else
        {
            flight.SyncRejected = result.IsPermanent;
            flight.SyncError = Describe(result);
        }

        await _flights.UpdateAsync(flight);
        SyncStateChanged?.Invoke(this, flight);

        var route = string.IsNullOrEmpty(flight.DepartureICAO) ? Label(flight)
            : $"{flight.DepartureICAO} to {flight.ArrivalICAO}";
        if (result.Success)
        {
            Log.Information("[PirepSync] PIREP accepted for flight {Id}", flight.Id);
            if (announce)
                _notifications.Notify("PIREP accepted", $"{route} has been filed with Aviates Air.",
                    AlertLevel.Success, page: "flights");
        }
        else if (result.IsPermanent)
        {
            Log.Warning("[PirepSync] PIREP rejected for flight {Id}: {Error}", flight.Id, flight.SyncError);
            _notifications.Notify("PIREP rejected", $"{route}: {flight.SyncError} Fix the details on the Flights page.",
                AlertLevel.Critical, page: "flights", alwaysInApp: true);
        }
        else
        {
            Log.Warning("[PirepSync] PIREP not sent for flight {Id}: {Error} -- will retry", flight.Id, flight.SyncError);
            if (announce)
                _notifications.Notify("PIREP not sent yet", $"{route}: {flight.SyncError} It will be retried automatically.",
                    AlertLevel.Warning, page: "flights");
        }

        return result;
    }

    /// <summary>Resends every completed flight that hasn't synced, except ones the server has
    /// already refused. Quiet unless something is accepted or newly rejected.</summary>
    public async Task RetryPendingAsync()
    {
        if (!await _retryGate.WaitAsync(0)) return;
        try
        {
            if (string.IsNullOrEmpty(_settings.Settings.AcarsKey.Trim())) return;

            var pending = (await _flights.GetAllAsync())
                .Where(f => IsAwaitingSync(f) && !f.SyncRejected)
                .ToList();
            if (pending.Count == 0) return;

            Log.Information("[PirepSync] Retrying {Count} unsynced PIREP(s)", pending.Count);
            foreach (var flight in pending)
            {
                var result = await SubmitAsync(flight, announce: false);
                if (result.StatusCode == 0 || result.StatusCode == 401) break; // offline or signed out: stop for now
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[PirepSync] Retry scan failed (non-critical)");
        }
        finally
        {
            _retryGate.Release();
        }
    }

    /// <summary>Applies the pilot's corrections to a refused PIREP and sends it again.</summary>
    public Task<PirepSubmitResult> FixAndResubmitAsync(FlightRecord flight, string departureIcao,
        string arrivalIcao, string flightNumber, string aircraftType)
    {
        flight.DepartureICAO = departureIcao.Trim().ToUpperInvariant();
        flight.ArrivalICAO = arrivalIcao.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(flightNumber)) flight.FlightNumber = flightNumber.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(aircraftType)) flight.AircraftType = aircraftType.Trim().ToUpperInvariant();
        flight.SyncRejected = false;
        return SubmitAsync(flight, announce: true);
    }

    internal static string Describe(PirepSubmitResult r) => r.StatusCode switch
    {
        0 => "Couldn't reach Aviates Air.",
        401 => "Your ACARS key wasn't accepted. Sign in again from Settings.",
        429 => "Too many PIREPs sent at once. It will go through shortly.",
        >= 500 => "Aviates Air had a server problem.",
        _ when r.Error?.Contains("departure_icao", StringComparison.OrdinalIgnoreCase) == true =>
            "The departure and arrival airports are missing.",
        _ when !string.IsNullOrWhiteSpace(r.Error) => Sentence(r.Error!),
        _ => $"Aviates Air refused it (HTTP {r.StatusCode}).",
    };

    private static string Sentence(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return s;
        s = char.ToUpperInvariant(s[0]) + s[1..];
        return s.EndsWith('.') ? s : s + ".";
    }

    private static string Label(FlightRecord f) =>
        !string.IsNullOrWhiteSpace(f.FlightNumber) ? f.FlightNumber
        : !string.IsNullOrWhiteSpace(f.Callsign) ? f.Callsign
        : "Your flight";
}
