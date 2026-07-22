namespace AviatesAirTracker.Models;

/// <summary>A single hand-maintained hazard-zone entry (conflict zone, volcano, or
/// wildfire) loaded from hazard_zones.json. No live data feed — VA staff edit this
/// file by hand when a real-world notice goes out.</summary>
public class HazardZoneEntry
{
    public string Icao { get; set; } = "";
    public string Type { get; set; } = ""; // "conflict" | "volcano" | "wildfire"
    public string Message { get; set; } = "";
}

/// <summary>Per-flight record of which Hoppies message triggers have already fired,
/// persisted to disk so a mid-flight app restart doesn't duplicate sends.</summary>
public class HoppiesFlightState
{
    public bool LoadsheetSent { get; set; }
    public bool PushbackReminderSent { get; set; }
    public bool AirborneSent { get; set; }
    public bool LandingSent { get; set; }
    public bool SummarySent { get; set; }
    public bool DepartureAdvisoriesSent { get; set; }
    public bool ArrivalAdvisoriesSent { get; set; }
}

/// <summary>One entry in the in-app Hoppies message log shown on the ACARS page.
/// Session-only — not persisted across restarts (only HoppiesFlightState is).</summary>
public class HoppiesLogEntry
{
    public DateTime SentAtUtc { get; set; }
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Success { get; set; }
}
