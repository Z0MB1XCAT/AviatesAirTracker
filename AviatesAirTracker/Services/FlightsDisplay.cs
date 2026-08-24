using AviatesAirTracker.Models;

namespace AviatesAirTracker.Services;

/// <summary>
/// Display-formatting helpers ported from Flights.razor's @code block. Introduced in Task 1 with
/// only RouteLabel — the piece FlightsViewModel.SubmitDeletionRequestAsync needs to build the
/// FlightDeletionRequest's Route field. Task 3 (flight table rows) adds the remaining display
/// helpers below, verbatim from Flights.razor's own private static methods.
///
/// These stay plain string-returning methods (no WinUI Brush/ImageSource types here) so this
/// class remains platform-agnostic and unit-testable, same separation FlightsFilterLogic already
/// established -- FlightsPage.xaml.cs (Task 3) is the one place that translates the
/// GradeChipClass/StatusChipClass CSS-class-name strings below into actual native Brush objects.
/// </summary>
public static class FlightsDisplay
{
    public static string RouteLabel(FlightRecord f) =>
        !string.IsNullOrWhiteSpace(f.DepartureICAO) && !string.IsNullOrWhiteSpace(f.ArrivalICAO)
            ? $"{f.DepartureICAO} → {f.ArrivalICAO}"
            : "—";

    public static string FormatDuration(TimeSpan t)
    {
        if (t == TimeSpan.Zero) return "—";
        return t.Hours > 0 ? $"{t.Hours}h {t.Minutes:D2}m" : $"{t.Minutes}m";
    }

    public static string GradeLetter(int score) => score switch
    {
        >= 90 => "A",
        >= 75 => "B",
        >= 60 => "C",
        >= 40 => "D",
        _ => "F"
    };

    // Verbatim from Flights.razor's GradeChipClass -- already bug-free (uses chip-amber, not the
    // undefined chip-yellow PilotHubViewModel had to fix), no correction needed here.
    public static string GradeChipClass(int score) => score switch
    {
        >= 90 => "chip-green",
        >= 75 => "chip-blue",
        >= 60 => "chip-amber",
        _ => "chip-red"
    };

    public static string StatusLabel(FlightStatus s) => s switch
    {
        FlightStatus.InProgress => "Active",
        FlightStatus.Completed => "Completed",
        FlightStatus.Diverted => "Diverted",
        FlightStatus.Aborted => "Aborted",
        FlightStatus.Crashed => "Crashed",
        _ => s.ToString()
    };

    // "pending" here means "not in-progress and not cleanly completed" -- an odd label for
    // Diverted/Aborted/Crashed, but that's what Flights.razor's own StatusChipClass does; ported
    // as-is rather than "fixed" into something more descriptive.
    public static string StatusChipClass(FlightStatus s) => s switch
    {
        FlightStatus.InProgress => "active",
        FlightStatus.Completed => "done",
        _ => "pending"
    };
}
