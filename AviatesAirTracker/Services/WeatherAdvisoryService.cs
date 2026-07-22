using System.Text.RegularExpressions;

namespace AviatesAirTracker.Services;

/// <summary>
/// Derives simple weather advisories (high wind, low visibility, volcanic ash/smoke
/// remarks) from the raw METAR text already fetched by SimBriefService. Reuses the
/// existing aviationweather.gov call — no new external dependency. This is heuristic
/// regex parsing of raw METAR text, not a full METAR parser — good enough for
/// "worth a heads-up" advisories, not for real dispatch decisions.
/// </summary>
public partial class WeatherAdvisoryService(SimBriefService simBrief)
{
    private const int HighWindKt = 25;
    private const int HighGustKt = 35;
    private const double LowVisibilitySm = 1.0;
    private const int LowVisibilityM = 1600;

    [GeneratedRegex(@"\b(\d{3}|VRB)(\d{2,3})(G(\d{2,3}))?KT\b")]
    private static partial Regex WindRegex();

    [GeneratedRegex(@"\b(\d)/(\d)SM\b")]
    private static partial Regex FractionalVisRegex();

    [GeneratedRegex(@"\b(\d{1,2})SM\b")]
    private static partial Regex WholeVisRegex();

    // Excludes matches preceded by another digit, Q, or A — avoids misreading the
    // day/time group (e.g. "251853Z") or an altimeter setting (e.g. "Q1013"/"A2992")
    // as a meters-visibility group.
    [GeneratedRegex(@"(?<![\dQA])(\d{4})(?!\d)")]
    private static partial Regex MetersVisRegex();

    /// <summary>Fetches the current METAR for the given ICAO and returns any advisory strings triggered by it.</summary>
    public async Task<List<string>> GetAdvisoriesAsync(string icao)
    {
        var advisories = new List<string>();
        var metar = await simBrief.FetchMetarAsync(icao);
        if (string.IsNullOrWhiteSpace(metar)) return advisories;

        var windMatch = WindRegex().Match(metar);
        if (windMatch.Success)
        {
            var speed = int.Parse(windMatch.Groups[2].Value);
            var gustText = windMatch.Groups[4].Value;
            var gust = string.IsNullOrEmpty(gustText) ? (int?)null : int.Parse(gustText);

            if (gust is >= HighGustKt)
                advisories.Add($"WIND GUSTING {gust}KT EXCEEDS RECOMMENDED LIMITS");
            else if (speed >= HighWindKt)
                advisories.Add($"SUSTAINED WIND {speed}KT EXCEEDS RECOMMENDED LIMITS");
        }

        var fracVis = FractionalVisRegex().Match(metar);
        if (fracVis.Success)
        {
            var num = double.Parse(fracVis.Groups[1].Value);
            var den = double.Parse(fracVis.Groups[2].Value);
            if (num / den < LowVisibilitySm)
                advisories.Add($"VISIBILITY {fracVis.Value} - LOW VIS OPS MAY APPLY");
        }
        else
        {
            var wholeVis = WholeVisRegex().Match(metar);
            if (wholeVis.Success && double.TryParse(wholeVis.Groups[1].Value, out var sm) && sm < LowVisibilitySm)
            {
                advisories.Add($"VISIBILITY {wholeVis.Value} - LOW VIS OPS MAY APPLY");
            }
            else
            {
                var metersVis = MetersVisRegex().Match(metar);
                if (metersVis.Success && int.TryParse(metersVis.Groups[1].Value, out var m) && m != 9999 && m < LowVisibilityM)
                    advisories.Add($"VISIBILITY {m}M - LOW VIS OPS MAY APPLY");
            }
        }

        if (Regex.IsMatch(metar, @"\bVA\b"))
            advisories.Add("VOLCANIC ASH REPORTED IN METAR REMARKS");
        if (Regex.IsMatch(metar, @"\bFU\b"))
            advisories.Add("SMOKE REPORTED IN METAR REMARKS");

        return advisories;
    }
}
