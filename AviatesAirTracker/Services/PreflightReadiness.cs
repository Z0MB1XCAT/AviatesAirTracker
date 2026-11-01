using System.Text.RegularExpressions;

namespace AviatesAirTracker.Services;

public enum ReadinessState { Ready, Warning, Blocked, Unknown }

public sealed record ReadinessItem(string Title, string Detail, ReadinessState State);

/// <summary>What the readiness checks are computed from; null where it isn't known yet.</summary>
public sealed record ReadinessInputs(
    bool SimConnected,
    bool BookingActivated,
    string? BookingOrigin,
    string? BookingDestination,
    string? BookingAircraftType,
    string? PlanOrigin,
    string? PlanDestination,
    double? PlanOriginLat,
    double? PlanOriginLon,
    double? AircraftLat,
    double? AircraftLon,
    bool? OnGround,
    bool? EnginesRunning,
    string? SimAircraftTitle,
    string? SimAircraftModel);

/// <summary>
/// The ACARS page's pre-flight checklist: everything that decides whether the next flight will
/// be tracked and logged properly, checked before the pilot starts the engines rather than
/// discovered after landing. Pure logic so it is unit-tested; the page only renders it.
/// </summary>
public static class PreflightReadiness
{
    private const double AtAirportNm = 3.0;

    public static IReadOnlyList<ReadinessItem> Evaluate(ReadinessInputs i)
    {
        var items = new List<ReadinessItem>();

        items.Add(i.SimConnected
            ? new("Simulator connected", "Microsoft Flight Simulator is connected.", ReadinessState.Ready)
            : new("Simulator connected", "Start MSFS and load into the aircraft. The tracker connects by itself.", ReadinessState.Blocked));

        items.Add(i.BookingActivated
            ? new("Booking activated", $"{i.BookingOrigin} to {i.BookingDestination}.", ReadinessState.Ready)
            : new("Booking activated", "Activate your booking on Pilot Hub. Flights without one aren't tracked.", ReadinessState.Blocked));

        if (string.IsNullOrEmpty(i.PlanOrigin))
        {
            items.Add(new("Flight plan", "No SimBrief plan loaded. Optional, but it adds the route, ETA and fuel figures.", ReadinessState.Warning));
        }
        else if (i.BookingActivated && !SameAirport(i.PlanOrigin, i.BookingOrigin) || i.BookingActivated && !SameAirport(i.PlanDestination, i.BookingDestination))
        {
            items.Add(new("Flight plan", $"Your SimBrief plan is {i.PlanOrigin} to {i.PlanDestination}, but the booking is {i.BookingOrigin} to {i.BookingDestination}. Re-plan it in SimBrief and import it again.", ReadinessState.Warning));
        }
        else
        {
            items.Add(new("Flight plan", $"SimBrief plan {i.PlanOrigin} to {i.PlanDestination} loaded.", ReadinessState.Ready));
        }

        items.Add(Position(i));
        items.Add(Aircraft(i));

        if (i.OnGround == false)
            items.Add(new("On the ground", "You're airborne. Tracking only starts from the ground, so this flight won't be logged.", ReadinessState.Blocked));
        else if (i.EnginesRunning == true)
            items.Add(new("Engines", i.BookingActivated
                ? "Engines are running. Tracking has started, or starts as soon as everything above is ready."
                : "Engines are running. Tracking starts once the booking is activated, as long as you're still on the ground.", ReadinessState.Ready));

        return items;
    }

    private static ReadinessItem Position(ReadinessInputs i)
    {
        const string title = "At the departure airport";
        if (i.AircraftLat is not { } lat || i.AircraftLon is not { } lon)
            return new(title, "Waiting for the aircraft's position from the sim.", ReadinessState.Unknown);
        if (i.PlanOriginLat is not { } dlat || i.PlanOriginLon is not { } dlon || !SameAirport(i.PlanOrigin, i.BookingOrigin ?? i.PlanOrigin))
            return new(title, "Load the SimBrief plan for this booking to check the aircraft is at the right airport.", ReadinessState.Unknown);

        var nm = DistanceNm(lat, lon, dlat, dlon);
        return nm <= AtAirportNm
            ? new(title, $"Parked at {i.PlanOrigin}.", ReadinessState.Ready)
            : new(title, $"The aircraft is {nm:N0} nm from {i.PlanOrigin}. Move it to the departure airport, or the flight will be logged from the wrong place.", ReadinessState.Warning);
    }

    private static ReadinessItem Aircraft(ReadinessInputs i)
    {
        const string title = "Booked aircraft";
        if (string.IsNullOrWhiteSpace(i.BookingAircraftType))
            return new(title, "No aircraft type on the booking to compare with.", ReadinessState.Unknown);

        var match = AircraftMatches(i.BookingAircraftType, i.SimAircraftTitle, i.SimAircraftModel);
        return match switch
        {
            true => new(title, $"Flying the booked {i.BookingAircraftType}.", ReadinessState.Ready),
            false => new(title, $"The booking is for a {i.BookingAircraftType}, but the sim has {Describe(i)} loaded.", ReadinessState.Warning),
            null => new(title, $"Booked: {i.BookingAircraftType}. Waiting for the sim to report the aircraft.", ReadinessState.Unknown),
        };
    }

    private static string Describe(ReadinessInputs i) =>
        !string.IsNullOrWhiteSpace(i.SimAircraftTitle) ? i.SimAircraftTitle!.Trim() : i.SimAircraftModel ?? "another aircraft";

    // ICAO designators whose digits don't name the family the sim title uses.
    private static readonly Dictionary<string, string> Families = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A19N"] = "319", ["A20N"] = "320", ["A21N"] = "321", ["A318"] = "318", ["A319"] = "319", ["A320"] = "320", ["A321"] = "321",
        ["A332"] = "330", ["A333"] = "330", ["A338"] = "330", ["A339"] = "330", ["A359"] = "350", ["A35K"] = "350",
        ["A388"] = "380", ["A380"] = "380", ["B736"] = "737", ["B737"] = "737", ["B738"] = "737", ["B739"] = "737",
        ["B37M"] = "737", ["B38M"] = "737", ["B39M"] = "737", ["B744"] = "747", ["B748"] = "747",
        ["B752"] = "757", ["B753"] = "757", ["B763"] = "767", ["B764"] = "767", ["B772"] = "777", ["B773"] = "777",
        ["B77L"] = "777", ["B77W"] = "777", ["B778"] = "777", ["B779"] = "777", ["B788"] = "787", ["B789"] = "787", ["B78X"] = "787",
        ["CRJ7"] = "CRJ", ["CRJ9"] = "CRJ", ["CRJX"] = "CRJ", ["E170"] = "170", ["E175"] = "175", ["E190"] = "190", ["E195"] = "195",
        ["AT72"] = "ATR", ["AT76"] = "ATR", ["DH8D"] = "Q400",
    };

    /// <summary>
    /// Whether the sim's aircraft is the booked type (same family is close enough: an A320neo
    /// booking flown in an A320ceo counts). Null when the sim hasn't reported its aircraft.
    /// </summary>
    public static bool? AircraftMatches(string bookingType, string? simTitle, string? simModel)
    {
        var sim = Normalise($"{simTitle} {simModel}");
        if (sim.Length == 0) return null;

        var booked = Normalise(bookingType);
        if (booked.Length == 0) return null;
        if (sim.Contains(booked, StringComparison.Ordinal)) return true;

        var family = Families.TryGetValue(booked, out var f) ? f : Regex.Match(booked, @"\d{3}").Value;
        return family.Length > 0 && sim.Contains(Normalise(family), StringComparison.Ordinal);
    }

    private static string Normalise(string? s) => Regex.Replace((s ?? "").ToUpperInvariant(), "[^A-Z0-9]", "");

    private static bool SameAirport(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 3440.065;
        double dLat = (lat2 - lat1) * Math.PI / 180, dLon = (lon2 - lon1) * Math.PI / 180;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}
