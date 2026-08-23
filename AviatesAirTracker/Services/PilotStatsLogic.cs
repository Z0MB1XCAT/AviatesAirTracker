using System;
using System.Collections.Generic;
using System.Linq;
using AviatesAirTracker.Models;

namespace AviatesAirTracker.Services;

public static class PilotStatsLogic
{
    /// <summary>
    /// Rank-name strings here MUST exactly match PilotStatsService.ComputeAsync()'s rank
    /// thresholds (Cadet/Junior First Officer/First Officer/Senior First Officer/Junior
    /// Captain/Captain/Senior Captain) — a prior version of this method (previously private
    /// in PilotHubViewModel) used different strings and silently misfired for 4 of 7 ranks.
    /// If PilotStatsService's rank thresholds ever change, this switch must change with them.
    /// </summary>
    public static (string NextRank, double ProgressPct) ComputeRankProgress(double hours, string rank) => rank switch
    {
        "Cadet"                => ("Junior First Officer",  Math.Min(hours / 25.0, 1.0) * 100),
        "Junior First Officer" => ("First Officer",         Math.Min((hours - 25) / 50.0, 1.0) * 100),
        "First Officer"        => ("Senior First Officer",  Math.Min((hours - 75) / 125.0, 1.0) * 100),
        "Senior First Officer" => ("Junior Captain",        Math.Min((hours - 200) / 250.0, 1.0) * 100),
        "Junior Captain"       => ("Captain",               Math.Min((hours - 450) / 350.0, 1.0) * 100),
        "Captain"              => ("Senior Captain",        Math.Min((hours - 800) / 700.0, 1.0) * 100),
        _                      => ("Top Rank",               100.0)
    };

    public readonly record struct MonthlyActivityPoint(string Label, int FlightCount);

    /// <summary>
    /// Groups completed flights by calendar month, oldest to newest, always returning exactly
    /// `months` points (zero-filled for months with no flights) ending at `asOfUtc`'s month. The
    /// UI needs a fixed-width bar row, not a sparse list that skips empty months.
    /// </summary>
    public static IReadOnlyList<MonthlyActivityPoint> ComputeMonthlyActivity(
        IEnumerable<FlightRecord> completedFlights, DateTime asOfUtc, int months = 7)
    {
        var counts = completedFlights
            .GroupBy(f => new DateTime(f.CreatedAt.Year, f.CreatedAt.Month, 1))
            .ToDictionary(g => g.Key, g => g.Count());

        var points = new List<MonthlyActivityPoint>();
        for (int i = months - 1; i >= 0; i--)
        {
            var monthStart = new DateTime(asOfUtc.Year, asOfUtc.Month, 1).AddMonths(-i);
            points.Add(new MonthlyActivityPoint(
                monthStart.ToString("MMM"),
                counts.TryGetValue(monthStart, out var c) ? c : 0));
        }
        return points;
    }
}
