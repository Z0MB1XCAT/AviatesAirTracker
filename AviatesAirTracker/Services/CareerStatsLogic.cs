using System.Globalization;
using AviatesAirTracker.Core.Reference;
using AviatesAirTracker.Models;

namespace AviatesAirTracker.Services;

public readonly record struct MonthTotals(DateTime Month, string Label, int Flights, double Hours);
public readonly record struct AircraftTotals(string Type, int Flights, double Hours);
public readonly record struct RouteTotals(string From, string To, int Flights, DateTime LastFlown);
public readonly record struct AirportTotals(string Icao, int Visits);
public readonly record struct ScorePoint(DateTime When, int Score, double RollingAverage);

public sealed class CareerStats
{
    public int Flights { get; init; }
    public double BlockHours { get; init; }
    /// <summary>Block hours with event (1.5x/2x) and charter (0.5x) multipliers applied: what counts toward rank.</summary>
    public double CreditedHours { get; init; }
    public double DistanceNm { get; init; }
    public double FuelLbs { get; init; }
    public int Landings { get; init; }
    public double AverageScore { get; init; }
    public int BestScore { get; init; }
    public double AverageTouchdownFpm { get; init; }
    public double SmoothestTouchdownFpm { get; init; }
    public int Countries { get; init; }
    public int Airports { get; init; }
    public int EventFlights { get; init; }
    public int CharterFlights { get; init; }
    public double? OnTimePercent { get; init; }
    public FlightRecord? Longest { get; init; }
    public FlightRecord? Farthest { get; init; }
    public LandingResult? BestLanding { get; init; }
    public IReadOnlyList<MonthTotals> Months { get; init; } = [];
    public IReadOnlyList<AircraftTotals> Aircraft { get; init; } = [];
    public IReadOnlyList<RouteTotals> Routes { get; init; } = [];
    public IReadOnlyList<AirportTotals> TopAirports { get; init; } = [];
    public IReadOnlyList<ScorePoint> ScoreTrend { get; init; } = [];
    /// <summary>Landing count per grade letter, A to F.</summary>
    public IReadOnlyDictionary<string, int> Grades { get; init; } = new Dictionary<string, int>();
}

/// <summary>
/// Career totals and breakdowns for the Statistics page, from the local flight and landing
/// history. Pure (no services or UI) so it is unit-tested. Same counting rules as
/// PilotStatsService: only completed flights, and flights/landings held for review don't count
/// until they've been reviewed.
/// </summary>
public static class CareerStatsLogic
{
    public static CareerStats Compute(IEnumerable<FlightRecord> flights, IEnumerable<LandingResult> landings,
        DateTime asOfUtc, int months = 12)
    {
        var done = flights.Where(f => f.Status == FlightStatus.Completed && !f.RequiresReview).ToList();
        var counted = landings.Where(l => !l.RequiresReview).OrderBy(l => l.Timestamp).ToList();

        var airportVisits = done
            .SelectMany(f => new[] { f.DepartureICAO, f.ArrivalICAO })
            .Where(IsIcao)
            .Select(i => i.ToUpperInvariant())
            .GroupBy(i => i)
            .Select(g => new AirportTotals(g.Key, g.Count()))
            .OrderByDescending(a => a.Visits).ThenBy(a => a.Icao)
            .ToList();

        var scheduled = done.Where(f => f.ScheduledDepUtc.HasValue).ToList();

        return new CareerStats
        {
            Flights = done.Count,
            BlockHours = done.Sum(f => f.BlockTime.TotalHours),
            CreditedHours = done.Sum(f => f.BlockTime.TotalHours * f.HoursMultiplier),
            DistanceNm = done.Sum(f => f.ActualDistanceNm),
            FuelLbs = done.Sum(f => Math.Max(0, f.FuelUsedLbs)),
            Landings = counted.Count,
            AverageScore = counted.Count > 0 ? counted.Average(l => l.LandingScore) : 0,
            BestScore = counted.Count > 0 ? counted.Max(l => l.LandingScore) : 0,
            AverageTouchdownFpm = counted.Count > 0 ? counted.Average(l => Math.Abs(l.VerticalSpeedFPM)) : 0,
            SmoothestTouchdownFpm = counted.Count > 0 ? counted.Min(l => Math.Abs(l.VerticalSpeedFPM)) : 0,
            Countries = airportVisits.Select(a => IcaoCountry.IsoFromIcao(a.Icao)).Where(c => !string.IsNullOrEmpty(c)).Distinct().Count(),
            Airports = airportVisits.Count,
            EventFlights = done.Count(f => f.EventId != null),
            CharterFlights = done.Count(f => f.IsCharterFlight),
            OnTimePercent = scheduled.Count > 0
                ? 100.0 * scheduled.Count(f => Math.Abs((f.BlockOutTime - f.ScheduledDepUtc!.Value).TotalMinutes) <= 15) / scheduled.Count
                : null,
            Longest = done.Where(f => f.BlockTime > TimeSpan.Zero).MaxBy(f => f.BlockTime),
            Farthest = done.Where(f => f.ActualDistanceNm > 0).MaxBy(f => f.ActualDistanceNm),
            BestLanding = counted.MaxBy(l => l.LandingScore),
            Months = ByMonth(done, asOfUtc, months),
            Aircraft = done
                .GroupBy(f => string.IsNullOrWhiteSpace(f.AircraftType) ? "Unknown" : f.AircraftType.Trim().ToUpperInvariant())
                .Select(g => new AircraftTotals(g.Key, g.Count(), g.Sum(f => f.BlockTime.TotalHours)))
                .OrderByDescending(a => a.Hours).ThenByDescending(a => a.Flights)
                .ToList(),
            Routes = done
                .Where(f => IsIcao(f.DepartureICAO) && IsIcao(f.ArrivalICAO))
                .GroupBy(f => (From: f.DepartureICAO.ToUpperInvariant(), To: f.ArrivalICAO.ToUpperInvariant()))
                .Select(g => new RouteTotals(g.Key.From, g.Key.To, g.Count(), g.Max(f => f.BlockOutTime)))
                .OrderByDescending(r => r.Flights).ThenByDescending(r => r.LastFlown)
                .Take(8)
                .ToList(),
            TopAirports = airportVisits.Take(8).ToList(),
            ScoreTrend = Trend(counted.TakeLast(30).ToList()),
            Grades = counted
                .GroupBy(l => Grade(l.LandingScore))
                .ToDictionary(g => g.Key, g => g.Count()),
        };
    }

    /// <summary>Letter grade, same bands as PilotHubViewModel.GradeLetter.</summary>
    public static string Grade(int score) => score switch
    {
        >= 90 => "A",
        >= 75 => "B",
        >= 60 => "C",
        >= 40 => "D",
        _ => "F",
    };

    private static IReadOnlyList<MonthTotals> ByMonth(List<FlightRecord> done, DateTime asOfUtc, int months)
    {
        var byMonth = done
            .GroupBy(f => new DateTime(When(f).Year, When(f).Month, 1))
            .ToDictionary(g => g.Key, g => (Flights: g.Count(), Hours: g.Sum(f => f.BlockTime.TotalHours)));

        var result = new List<MonthTotals>();
        var start = new DateTime(asOfUtc.Year, asOfUtc.Month, 1).AddMonths(-(months - 1));
        for (int i = 0; i < months; i++)
        {
            var m = start.AddMonths(i);
            byMonth.TryGetValue(m, out var t);
            result.Add(new MonthTotals(m, m.ToString("MMM", CultureInfo.InvariantCulture), t.Flights, t.Hours));
        }
        return result;
    }

    // Block-out is when the flight actually happened; CreatedAt is when the record was made,
    // which for history synced from the server is the day of the sync.
    private static DateTime When(FlightRecord f) => f.BlockOutTime != default ? f.BlockOutTime : f.CreatedAt;

    private static IReadOnlyList<ScorePoint> Trend(List<LandingResult> recent)
    {
        var points = new List<ScorePoint>(recent.Count);
        for (int i = 0; i < recent.Count; i++)
        {
            var window = recent.Skip(Math.Max(0, i - 4)).Take(Math.Min(5, i + 1));
            points.Add(new ScorePoint(recent[i].Timestamp, recent[i].LandingScore, window.Average(l => l.LandingScore)));
        }
        return points;
    }

    private static bool IsIcao(string? code) => code is { Length: 4 } && code.All(char.IsLetterOrDigit);
}
