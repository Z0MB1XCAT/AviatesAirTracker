using AviatesAirTracker.Models;
using AviatesAirTracker.Services;

namespace AviatesAirTracker.Shell;

/// <summary>
/// Pure, testable predicates behind the shell's live nav-rail badges. Kept in its own file
/// (rather than inline in ShellWindow.xaml.cs) with no WinUI usings so it can be linked
/// standalone into the test project — see tests/AviatesAirTracker.Tests/ShellWindowBadgeLogicTests.cs.
/// </summary>
internal static class ShellBadgeLogic
{
    /// <summary>
    /// Mirrors FlightSessionManager.cs:470's existing convention — Idle and Complete are
    /// both "nothing active" for every other purpose in this codebase, so the ACARS/Map
    /// "Live" badge follows that same rule rather than inventing a new one.
    /// </summary>
    public static bool IsFlightLive(FlightSessionState state) =>
        state is not (FlightSessionState.Idle or FlightSessionState.Complete);

    /// <summary>
    /// The Flights nav-rail dot: true when at least one completed flight hasn't synced to
    /// the backend yet. Same predicate as App.xaml.cs's RetryPendingPirepsAsync — reused,
    /// not reinvented.
    /// </summary>
    public static bool HasUnsyncedPireps(IEnumerable<FlightRecord> flights) =>
        flights.Any(f => !f.SyncedToBackend && f.Status == FlightStatus.Completed);
}
