namespace AviatesAirTracker;

// MINOR-17: AgentDebug was writing a debug-9ee785.log file to disk on every page navigation.
// Disabled: Log() is now a no-op. All calls in ViewCodeBehinds.cs have also been removed.
internal static class AgentDebug
{
    internal static void Log(string hypothesisId, string location, string message, object? data = null, string runId = "pre-fix")
    {
        // No-op in production builds.
    }
}

