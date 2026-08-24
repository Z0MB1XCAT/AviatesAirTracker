using AviatesAirTracker.Models;

namespace AviatesAirTracker.Services;

/// <summary>
/// Display-formatting helpers ported from Flights.razor's @code block. Introduced here (Task 1)
/// with only RouteLabel — the piece FlightsViewModel.SubmitDeletionRequestAsync needs to build the
/// FlightDeletionRequest's Route field. Task 3 (flight table rows) adds the remaining display
/// helpers (DurationText, GradeLetter, chip colors, StatusLabel) to this same class.
/// </summary>
public static class FlightsDisplay
{
    public static string RouteLabel(FlightRecord f) =>
        !string.IsNullOrWhiteSpace(f.DepartureICAO) && !string.IsNullOrWhiteSpace(f.ArrivalICAO)
            ? $"{f.DepartureICAO} → {f.ArrivalICAO}"
            : "—";
}
