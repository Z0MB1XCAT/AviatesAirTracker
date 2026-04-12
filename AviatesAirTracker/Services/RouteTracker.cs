using AviatesAirTracker.Models;
using Serilog;

namespace AviatesAirTracker.Services;

// ============================================================
// ROUTE TRACKER
// Records actual flight path and manages planned waypoints
// Provides data for live map rendering and replay
// ============================================================

public class RouteTracker
{
    private readonly List<PathPoint> _recordedPath = [];
    private readonly List<Waypoint> _plannedRoute = [];
    private readonly object _lock = new();
    private bool _recording;
    private PathPoint? _previousPoint;

    public double TotalDistanceNm { get; private set; }
    public int WaypointCount => _plannedRoute.Count;
    public int PassedWaypoints => _plannedRoute.Count(w => w.IsPassed);

    // =====================================================
    // RECORDING CONTROL
    // =====================================================

    public void StartRecording()
    {
        lock (_lock)
        {
            _recording = true;
            _recordedPath.Clear();
            TotalDistanceNm = 0;
            _previousPoint = null;
            Log.Information("[RouteTracker] Recording started");
        }
    }

    public void StopRecording()
    {
        _recording = false;
        Log.Information("[RouteTracker] Recording stopped. {Count} points, {Dist:F1}nm",
            _recordedPath.Count, TotalDistanceNm);
    }

    public void AddPoint(PathPoint point)
    {
        if (!_recording) return;

        lock (_lock)
        {
            _recordedPath.Add(point);

            if (_previousPoint != null)
            {
                TotalDistanceNm += HaversineNm(
                    _previousPoint.Latitude, _previousPoint.Longitude,
                    point.Latitude, point.Longitude);
            }

            _previousPoint = point;

            // Check waypoint progression
            UpdateWaypointProgress(point);
        }
    }

    // =====================================================
    // PLANNED ROUTE
    // =====================================================

    public void SetPlannedRoute(List<Waypoint> waypoints)
    {
        lock (_lock)
        {
            _plannedRoute.Clear();
            _plannedRoute.AddRange(waypoints.OrderBy(w => w.SequenceNumber));
            Log.Information("[RouteTracker] Planned route set: {Count} waypoints", waypoints.Count);
        }
    }

    private void UpdateWaypointProgress(PathPoint current)
    {
        // Mark waypoints as passed when within 2nm
        foreach (var wp in _plannedRoute.Where(w => !w.IsPassed))
        {
            double dist = HaversineNm(current.Latitude, current.Longitude, wp.Latitude, wp.Longitude);
            if (dist < 2.0)
            {
                wp.IsPassed = true;
                Log.Debug("[RouteTracker] Waypoint passed: {Id}", wp.Identifier);
            }
        }
    }

    public Waypoint? GetNextWaypoint()
    {
        lock (_lock)
        {
            return _plannedRoute.FirstOrDefault(w => !w.IsPassed);
        }
    }

    public double GetRemainingDistanceNm(double currentLat, double currentLon)
    {
        lock (_lock)
        {
            var remaining = _plannedRoute.Where(w => !w.IsPassed).ToList();
            if (!remaining.Any()) return 0;

            double total = HaversineNm(currentLat, currentLon, remaining[0].Latitude, remaining[0].Longitude);
            for (int i = 1; i < remaining.Count; i++)
            {
                total += HaversineNm(remaining[i - 1].Latitude, remaining[i - 1].Longitude,
                                     remaining[i].Latitude, remaining[i].Longitude);
            }
            return total;
        }
    }

    // =====================================================
    // DATA ACCESS
    // =====================================================

    public List<PathPoint> GetRecordedPath()
    {
        lock (_lock) { return [.. _recordedPath]; }
    }

    public List<PathPoint> GetRecordedPathSnapshot()
    {
        lock (_lock) { return _recordedPath.TakeLast(500).ToList(); }
    }

    public List<Waypoint> GetPlannedRoute()
    {
        lock (_lock) { return [.. _plannedRoute]; }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _recording = false;
            _recordedPath.Clear();
            _plannedRoute.Clear();
            TotalDistanceNm = 0;
            _previousPoint = null;
        }
    }

    // =====================================================
    // MATH
    // =====================================================

    private static double HaversineNm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 3440.065; // Earth radius in nm
        double dLat = (lat2 - lat1) * Math.PI / 180;
        double dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
