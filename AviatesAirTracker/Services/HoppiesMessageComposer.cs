using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Models;

namespace AviatesAirTracker.Services;

/// <summary>
/// Pure formatting: builds each Hoppies telex message body. No I/O, no state — every
/// method takes the data it needs and returns plain text.
/// </summary>
public static class HoppiesMessageComposer
{
    private static string Hms(TimeSpan t) => $"{(int)t.TotalHours:D2}:{t.Minutes:D2}";

    public static string Loadsheet(string flightNumber, string registration, string aircraftType, SimBriefFlightPlan plan)
    {
        var date = plan.ScheduledDepartureUtc?.ToString("ddMMMyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant() ?? "";
        var lines = new List<string>
        {
            "LOADSHEET",
            $"{flightNumber} {date}",
            $"{plan.DepartureICAO} {plan.ArrivalICAO} {registration}",
            ".........................",
            $"ZFW  {plan.ZeroFuelWeightKg:F0}",
            $"TOW  {plan.TakeoffWeightKg:F0}",
            $"PAX  {plan.PaxCount}"
        };

        var wb = WeightBalanceDatabase.Compute(aircraftType, plan.ZeroFuelWeightKg, plan.TakeoffWeightKg);
        if (wb is not null)
        {
            lines.Add($"MACZFW {wb.MacZfwPercent:F1}");
            lines.Add($"MACTOW {wb.MacTowPercent:F1}");
        }

        lines.Add($"FUEL IN TANKS {plan.TakeoffFuelLbs:F0}");
        lines.Add("END");

        return string.Join("\n", lines);
    }

    public static string PushbackReminder(string callsign, DateTime scheduledDepartureUtc) =>
        $"{callsign}, SCHEDULED OFF-BLOCK IN 5 MIN ({scheduledDepartureUtc:HHmm}Z). PREPARE FOR PUSHBACK.";

    public static string AirborneCall(string callsign, string departureIcao, DateTime timeUtc) =>
        $"{callsign} AIRBORNE {departureIcao} {timeUtc:HHmm}Z. HAVE A SAFE FLIGHT.";

    public static string LandingCall(string callsign, string arrivalIcao, DateTime timeUtc) =>
        $"{callsign} TOUCHDOWN {arrivalIcao} {timeUtc:HHmm}Z. WELCOME TO {arrivalIcao}.";

    public static string PostFlightSummary(FlightRecord record)
    {
        var landingRate = record.PrimaryLanding is not null
            ? $"{Math.Abs(record.PrimaryLanding.VerticalSpeedFPM):F0} FPM ({record.PrimaryLanding.LandingGrade})"
            : "N/A";

        return string.Join("\n",
            $"FLIGHT SUMMARY - {record.FlightNumber} / {record.Callsign}",
            $"ROUTE: {record.DepartureICAO}-{record.ArrivalICAO}",
            $"BLOCK TIME: {Hms(record.BlockTime)} AIR TIME: {Hms(record.AirTime)}",
            $"LANDING RATE: {landingRate}",
            "THANK YOU FOR FLYING WITH US");
    }

    public static string HazardAdvisory(string icao, string hazardText) =>
        string.Join("\n",
            $"SECURITY ADVISORY - {icao}",
            hazardText,
            "EXERCISE CAUTION AND FOLLOW COMPANY/ATC GUIDANCE");

    public static string WeatherAdvisory(string icao, string condition, string phaseLabel) =>
        string.Join("\n",
            $"WX ADVISORY - {icao}",
            condition,
            $"REVIEW LATEST METAR/TAF BEFORE {phaseLabel}");
}
