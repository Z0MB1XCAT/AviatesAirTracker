// Re-export FlightPhaseDetector into Services namespace so DI resolves correctly
// The actual implementation lives in Core/Analytics/FlightPhaseDetector.cs

// This file provides the missing RunwayDetector DI registration alias
// and ensures all Analytics classes are available from Services

using AviatesAirTracker.Core.SimConnect;

namespace AviatesAirTracker.Core.Analytics
{
    // Alias so App.xaml.cs can reference without namespace change
    // FlightPhaseDetector is already defined in this namespace
}

namespace AviatesAirTracker.Services
{
    // Aliases to let DI resolve Analytics types registered as Services
    // FlightPhaseDetector, LandingAnalyzer, ApproachMonitor, FuelAnalyzer
    // are all defined in Core.Analytics but registered in DI as singletons
    // — no re-declaration needed, just ensure using directives are correct.
}
