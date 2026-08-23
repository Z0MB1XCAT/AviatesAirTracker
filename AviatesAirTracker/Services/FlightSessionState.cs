namespace AviatesAirTracker.Services;

/// <summary>
/// Extracted from FlightSessionManager.cs so this enum can be linked standalone into the
/// test project (net8.0, no WinUI/SimConnect dependencies) without dragging in the rest of
/// FlightSessionManager's dependency graph. See ShellBadgeLogic and its tests.
/// </summary>
public enum FlightSessionState
{
    Idle,
    PreFlight,
    Taxiing,
    Airborne,
    OnApproach,
    Landed,
    Complete
}
