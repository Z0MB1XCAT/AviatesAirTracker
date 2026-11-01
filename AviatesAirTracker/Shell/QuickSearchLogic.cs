using AviatesAirTracker.Models;

namespace AviatesAirTracker.Shell;

public enum QuickSearchKind { Page, Flight, Landing, Action }

/// <summary>One Ctrl+K result. <see cref="Target"/> is a page tag for pages, an action id for
/// actions, a landing id for landings, and a flight's landing id (empty if none) for flights.</summary>
public sealed record QuickSearchResult(string Title, string Detail, QuickSearchKind Kind, string Target)
{
    // AutoSuggestBox shows ToString() for each suggestion.
    public override string ToString() => string.IsNullOrEmpty(Detail) ? Title : $"{Title}    {Detail}";
}

/// <summary>
/// The matching behind the Ctrl+K search: pages (by name or a few common words for them), quick
/// actions, flights (number, callsign, airports) and landings (airport, runway). Pure so it is
/// unit-tested; the shell supplies the data and acts on the chosen result.
/// </summary>
public static class QuickSearchLogic
{
    public sealed record PageEntry(string Tag, string Label, string[] Keywords);

    public static IReadOnlyList<QuickSearchResult> Search(
        string query,
        IEnumerable<PageEntry> pages,
        IEnumerable<(string Id, string Title, string[] Keywords)> actions,
        IEnumerable<FlightRecord> flights,
        IEnumerable<LandingResult> landings,
        int max = 8)
    {
        var q = query.Trim();
        if (q.Length == 0) return [];

        var results = new List<(int Rank, QuickSearchResult Result)>();

        foreach (var p in pages)
        {
            var rank = Rank(q, p.Label, p.Keywords);
            if (rank >= 0) results.Add((rank, new QuickSearchResult(p.Label, "Page", QuickSearchKind.Page, p.Tag)));
        }

        foreach (var a in actions)
        {
            var rank = Rank(q, a.Title, a.Keywords);
            if (rank >= 0) results.Add((rank + 1, new QuickSearchResult(a.Title, "Action", QuickSearchKind.Action, a.Id)));
        }

        if (q.Length >= 2)
        {
            foreach (var f in flights.OrderByDescending(f => f.BlockOutTime).Take(500))
            {
                var label = !string.IsNullOrWhiteSpace(f.FlightNumber) ? f.FlightNumber : f.Callsign;
                var route = string.IsNullOrEmpty(f.DepartureICAO) ? "" : $"{f.DepartureICAO} to {f.ArrivalICAO}";
                var rank = Rank(q, label, [f.Callsign, f.DepartureICAO, f.ArrivalICAO, f.AircraftType]);
                if (rank < 0) continue;
                var when = f.BlockOutTime != default ? f.BlockOutTime.ToLocalTime().ToString("d MMM yyyy") : "";
                results.Add((rank + 2, new QuickSearchResult(
                    string.IsNullOrWhiteSpace(label) ? route : $"{label}  {route}".Trim(),
                    $"Flight, {when}".TrimEnd(',', ' '),
                    QuickSearchKind.Flight,
                    f.PrimaryLanding?.Id.ToString() ?? "")));
            }

            foreach (var l in landings.OrderByDescending(l => l.Timestamp).Take(500))
            {
                var rank = Rank(q, l.AirportICAO, [l.RunwayIdentifier, $"{l.AirportICAO} {l.RunwayIdentifier}"]);
                if (rank < 0) continue;
                results.Add((rank + 3, new QuickSearchResult(
                    $"Landing at {l.AirportICAO} {l.RunwayIdentifier}".Trim(),
                    $"Score {l.LandingScore}, {l.Timestamp.ToLocalTime():d MMM yyyy}",
                    QuickSearchKind.Landing,
                    l.Id.ToString())));
            }
        }

        return results.OrderBy(r => r.Rank).Select(r => r.Result).DistinctBy(r => (r.Kind, r.Target)).Take(max).ToList();
    }

    /// <summary>0 = starts with the query, 1 = a keyword starts with it, 2 = contains it, -1 = no match.</summary>
    internal static int Rank(string q, string? primary, IEnumerable<string?> keywords)
    {
        if (!string.IsNullOrEmpty(primary) && primary.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 0;
        var kws = keywords.Where(k => !string.IsNullOrEmpty(k)).ToList();
        if (kws.Any(k => k!.StartsWith(q, StringComparison.OrdinalIgnoreCase)
                      || k.Split(' ').Any(w => w.StartsWith(q, StringComparison.OrdinalIgnoreCase)))) return 1;
        if ((primary?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
            kws.Any(k => k!.Contains(q, StringComparison.OrdinalIgnoreCase))) return 2;
        return -1;
    }
}
