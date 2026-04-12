using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using Serilog;

namespace AviatesAirTracker.Services;

// ============================================================
// RUNWAY DETECTOR
//
// Identifies which runway the aircraft is landing on.
// Uses aircraft position + heading on touchdown to match
// against an embedded runway database.
//
// For full production use this should query the Navigraph
// or MSFS airport data. Here we provide a built-in dataset
// of major airports + a proximity/heading matching algorithm.
// ============================================================

public class RunwayDetector
{
    private readonly List<RunwayInfo> _runwayDatabase = [];

    public RunwayDetector()
    {
        LoadBuiltInRunways();
    }

    // =====================================================
    // DETECT RUNWAY FROM POSITION AND HEADING
    // =====================================================

    public RunwayInfo? Detect(double lat, double lon, double headingTrue)
    {
        if (_runwayDatabase.Count == 0) return null;

        RunwayInfo? best = null;
        double bestScore = double.MaxValue;

        foreach (var rwy in _runwayDatabase)
        {
            // Distance from threshold
            double distNm = HaversineNm(lat, lon, rwy.ThresholdLatitude, rwy.ThresholdLongitude);
            if (distNm > 5.0) continue;  // Must be within 5nm of threshold

            // Heading alignment
            double headingDiff = Math.Abs(DeltaAngle(headingTrue, rwy.HeadingTrue));
            if (headingDiff > 20) continue;  // Must be within 20° of runway heading

            double score = distNm * 10 + headingDiff;
            if (score < bestScore)
            {
                bestScore = score;
                best = rwy;
            }
        }

        if (best != null)
            Log.Debug("[RunwayDetector] Matched: {ICAO} {RWY}", best.AirportICAO, best.Identifier);

        return best;
    }

    // =====================================================
    // BUILT-IN RUNWAY DATABASE (major hubs)
    // Extend this or replace with MSFS airport data API
    // =====================================================

    private void LoadBuiltInRunways()
    {
        // London Heathrow (EGLL)
        AddRunways("EGLL", new[]
        {
            ("09L", 90.0,  51.4775, -0.4814, 51.4775, -0.4381, 12799),
            ("27R", 270.0, 51.4775, -0.4381, 51.4775, -0.4814, 12799),
            ("09R", 90.0,  51.4639, -0.4814, 51.4639, -0.4381, 12008),
            ("27L", 270.0, 51.4639, -0.4381, 51.4639, -0.4814, 12008),
        });

        // London Gatwick (EGKK)
        AddRunways("EGKK", new[]
        {
            ("08R", 80.0,  51.1481, -0.1903, 51.1506, -0.1538, 10879),
            ("26L", 260.0, 51.1506, -0.1538, 51.1481, -0.1903, 10879),
        });

        // Amsterdam Schiphol (EHAM)
        AddRunways("EHAM", new[]
        {
            ("06",  59.0,  52.3081,  4.7428, 52.3286,  4.7939, 11155),
            ("24", 239.0,  52.3286,  4.7939, 52.3081,  4.7428, 11155),
            ("18R",185.0,  52.3636,  4.7439, 52.2994,  4.7436, 11329),
            ("36L",  5.0,  52.2994,  4.7436, 52.3636,  4.7439, 11329),
        });

        // Frankfurt (EDDF)
        AddRunways("EDDF", new[]
        {
            ("07L",  70.0, 50.0464,  8.5350, 50.0536,  8.6167, 13123),
            ("25R", 250.0, 50.0536,  8.6167, 50.0464,  8.5350, 13123),
            ("07C",  70.0, 50.0358,  8.5397, 50.0431,  8.6214, 13123),
            ("25C", 250.0, 50.0431,  8.6214, 50.0358,  8.5397, 13123),
        });

        // JFK New York (KJFK)
        AddRunways("KJFK", new[]
        {
            ("04L",  42.0, 40.6198, -73.7789, 40.6414, -73.7597, 11351),
            ("22R", 222.0, 40.6414, -73.7597, 40.6198, -73.7789, 11351),
            ("13R", 131.0, 40.6625, -73.7989, 40.6328, -73.7625, 14511),
            ("31L", 311.0, 40.6328, -73.7625, 40.6625, -73.7989, 14511),
        });

        // Los Angeles (KLAX)
        AddRunways("KLAX", new[]
        {
            ("06L",  69.0, 33.9425,-118.4181, 33.9503,-118.3806, 12091),
            ("24R", 249.0, 33.9503,-118.3806, 33.9425,-118.4181, 12091),
            ("06R",  69.0, 33.9294,-118.4181, 33.9372,-118.3806, 11095),
            ("24L", 249.0, 33.9372,-118.3806, 33.9294,-118.4181, 11095),
        });

        // Dubai (OMDB)
        AddRunways("OMDB", new[]
        {
            ("12L", 120.0, 25.2644,  55.3603, 25.2386,  55.3903, 13123),
            ("30R", 300.0, 25.2386,  55.3903, 25.2644,  55.3603, 13123),
        });

        // Singapore Changi (WSSS)
        AddRunways("WSSS", new[]
        {
            ("02C",  20.0,  1.3556, 103.9867,  1.3769, 103.9900, 13123),
            ("20C", 200.0,  1.3769, 103.9900,  1.3556, 103.9867, 13123),
        });

        Log.Information("[RunwayDetector] Loaded {Count} runways", _runwayDatabase.Count);
    }

    private void AddRunways(string icao,
        IEnumerable<(string id, double hdg, double thrLat, double thrLon, double endLat, double endLon, int lengthFt)> runways)
    {
        foreach (var (id, hdg, thrLat, thrLon, endLat, endLon, lengthFt) in runways)
        {
            _runwayDatabase.Add(new RunwayInfo
            {
                AirportICAO = icao,
                Identifier = id,
                HeadingTrue = hdg,
                ThresholdLatitude = thrLat,
                ThresholdLongitude = thrLon,
                EndLatitude = endLat,
                EndLongitude = endLon,
                LengthFt = lengthFt,
                SurfaceType = "Asphalt"
            });
        }
    }

    // =====================================================
    // MATH
    // =====================================================

    private static double HaversineNm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 3440.065;
        double dLat = (lat2 - lat1) * Math.PI / 180;
        double dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DeltaAngle(double a, double b)
    {
        double diff = ((a - b) + 360) % 360;
        return diff > 180 ? diff - 360 : diff;
    }

    // =====================================================
    // LIVE INTEGRATION: Called from FlightSessionManager during approach
    // =====================================================

    // CRIT-02: Was calling Detect() but discarding the result AND never called anywhere.
    // Now returns the detected runway so the caller can pass it to LandingAnalyzer.SetRunwayInfo().
    public RunwayInfo? UpdateForApproach(Core.SimConnect.TelemetrySnapshot snap)
    {
        if (snap.AltitudeAGL < 3000)
            return Detect(snap.Latitude, snap.Longitude, snap.Raw.HeadingTrue);
        return null;
    }
}
