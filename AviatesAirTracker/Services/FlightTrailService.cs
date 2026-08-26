using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using Serilog;

namespace AviatesAirTracker.Services;

// ============================================================
// FLIGHT TRAIL SERVICE
//
// Persistent, unbounded flight-path recording, deliberately independent of
// RouteTracker and its per-flight lifecycle. RouteTracker is wiped on every
// flight-complete/end-flight via FlightSessionManager.Reset(), which
// conflicts with "keep the trail until a new, genuinely-different OFP is
// imported" -- so this service owns its own full history instead.
//
// - Recording starts at SimConnect connect (not takeoff, not page-open):
//   AddPoint no-ops unless SimConnectManager.ConnectionStatus is currently
//   Connected, so SimConnectStub's fallback/disconnected data is never
//   recorded.
// - Never cleared by FlightSessionManager.Reset(), flight-complete, landing,
//   or page navigation/reconnects. The only clear trigger is the
//   OFP-mismatch wipe path (AcarsViewModel.WouldWipeTrail, a later task in
//   this same plan) calling Clear().
// - Both MapPage and AcarsPage's mini-map read from GetTrailForRendering(),
//   the one shared source of truth for the colored flown path.
// ============================================================

public class FlightTrailService
{
    private readonly List<PathPoint> _trail = [];
    private readonly object _lock = new();
    private readonly SimConnectManager _simConnect;

    public FlightTrailService(SimConnectManager simConnect)
    {
        _simConnect = simConnect;

        // SimConnectManager exposes a readable current-status property
        // (ConnectionStatus), not just an event, so AddPoint's gate below reads
        // that live property directly rather than tracking a locally-cached
        // copy of the status. This subscription exists only for connect/
        // disconnect logging -- it does not drive the recording gate.
        _simConnect.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    private void OnConnectionStatusChanged(object? sender, SimConnectionStatus status)
    {
        Log.Information("[FlightTrailService] SimConnect status changed to {Status} ({Count} points recorded so far)",
            status, RecordedPointCount);
    }

    /// <summary>
    /// No-op unless SimConnectManager's current status is Connected. Fed from the same
    /// already-2Hz-decimated telemetry tick FlightSessionManager already calls
    /// RouteTracker.AddPoint(point) from -- called alongside it, reusing the existing cadence
    /// rather than adding a second telemetry subscription.
    /// </summary>
    public void AddPoint(PathPoint point)
    {
        if (_simConnect.ConnectionStatus != SimConnectionStatus.Connected) return;

        lock (_lock)
        {
            _trail.Add(point);
        }
    }

    /// <summary>
    /// Empties the full history. Called only from the OFP-import mismatch path -- never from
    /// FlightSessionManager.Reset() or any flight-lifecycle event.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _trail.Clear();
        }
        Log.Information("[FlightTrailService] Trail cleared (OFP mismatch on import)");
    }

    /// <summary>Total number of points recorded, unbounded -- for GetPathIfChanged-style change detection.</summary>
    public int RecordedPointCount
    {
        get { lock (_lock) return _trail.Count; }
    }

    /// <summary>
    /// Stride-thins the full, unbounded history down to at most ~maxPoints, using the same
    /// "keep every Nth point" technique as NativeMapRenderer.SetPlannedRoute's waypoint-label
    /// thinning (step = Math.Max(1, count / maxPoints)), so a long flight is always shown
    /// start-to-finish at a coarser resolution rather than losing its beginning. The most recent
    /// point is always included even if it doesn't fall on the stride, so the trail's leading
    /// edge tracks the aircraft precisely.
    /// </summary>
    public IReadOnlyList<(double Lat, double Lon, double AltFt)> GetTrailForRendering(int maxPoints)
    {
        lock (_lock)
        {
            var count = _trail.Count;
            if (count == 0) return Array.Empty<(double, double, double)>();

            var step = Math.Max(1, count / maxPoints);
            var result = new List<(double Lat, double Lon, double AltFt)>();
            for (var i = 0; i < count; i += step)
            {
                var p = _trail[i];
                result.Add((p.Latitude, p.Longitude, p.AltitudeMSL));
            }

            var lastIndex = count - 1;
            if (lastIndex % step != 0)
            {
                var last = _trail[lastIndex];
                result.Add((last.Latitude, last.Longitude, last.AltitudeMSL));
            }

            return result;
        }
    }
}
