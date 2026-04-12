using Serilog;

namespace AviatesAirTracker.Core.Analytics;

// ============================================================
// AIRCRAFT PERFORMANCE PROFILES
//
// Maps MSFS aircraft title strings to performance data.
// Used by:
//   - StabilityChecker (Vref/Vapp bands for approach)
//   - LandingAnalyzer (expected pitch at touchdown)
//   - FuelAnalyzer (expected burn rates)
//   - LiveFlightView (flap detents labels)
// ============================================================

public class AircraftPerformanceDatabase
{
    private readonly Dictionary<string, AircraftProfile> _profiles = [];
    private AircraftProfile? _active;

    public AircraftProfile Active => _active ?? AircraftProfile.Generic;

    public AircraftPerformanceDatabase()
    {
        RegisterProfiles();
    }

    // =====================================================
    // PROFILE LOOKUP
    // =====================================================

    public AircraftProfile? Identify(string aircraftTitle)
    {
        if (string.IsNullOrEmpty(aircraftTitle)) return null;

        var title = aircraftTitle.ToUpperInvariant();

        foreach (var kvp in _profiles)
        {
            if (title.Contains(kvp.Key))
            {
                _active = kvp.Value;
                Log.Information("[AircraftDB] Identified: {Profile} from title '{Title}'",
                    kvp.Value.Name, aircraftTitle);
                return kvp.Value;
            }
        }

        Log.Debug("[AircraftDB] Unknown aircraft: {Title} — using generic profile", aircraftTitle);
        _active = AircraftProfile.Generic;
        return AircraftProfile.Generic;
    }

    // =====================================================
    // PROFILE REGISTRY
    // Add more aircraft here as needed
    // =====================================================

    private void RegisterProfiles()
    {
        // Airbus A320 Family
        Register("A320", new AircraftProfile
        {
            Name            = "Airbus A320",
            ICAOType        = "A320",
            Category        = AircraftCategory.NarrowBodyJet,
            VrefKts         = 137,
            VappKts         = 142,
            VrotateKts      = 148,
            V2Kts           = 155,
            MaxFlapSpeed    = new[] { 230, 215, 200, 185, 177, 164 },
            FlapNames       = new[] { "0", "1", "1+F", "2", "3", "FULL" },
            IdealTdPitchDeg = 3.0,
            MaxTdPitchDeg   = 6.0,
            MaxTdBankDeg    = 5.0,
            MaxTdVSfpm      = -600,
            GoodTdVSfpm     = -200,
            MaxLandWeightLbs = 142198,
            MaxTOWeightLbs  = 162040,
            FuelCapacityLbs = 40565,
            MaxCruiseMach   = 0.82,
            CruiseFL        = 350,
            TypeRating      = "A320"
        });

        Register("A319", new AircraftProfile
        {
            Name = "Airbus A319", ICAOType = "A319",
            Category = AircraftCategory.NarrowBodyJet,
            VrefKts = 131, VappKts = 136,
            IdealTdPitchDeg = 3.0, GoodTdVSfpm = -200, MaxTdVSfpm = -600,
            MaxCruiseMach = 0.82, CruiseFL = 350, TypeRating = "A320"
        });

        Register("A321", new AircraftProfile
        {
            Name = "Airbus A321", ICAOType = "A321",
            Category = AircraftCategory.NarrowBodyJet,
            VrefKts = 141, VappKts = 146,
            IdealTdPitchDeg = 3.5, GoodTdVSfpm = -200, MaxTdVSfpm = -600,
            MaxCruiseMach = 0.82, CruiseFL = 350, TypeRating = "A320"
        });

        // Airbus A330
        Register("A330", new AircraftProfile
        {
            Name = "Airbus A330", ICAOType = "A333",
            Category = AircraftCategory.WidebodyJet,
            VrefKts = 141, VappKts = 146,
            IdealTdPitchDeg = 4.0, GoodTdVSfpm = -200, MaxTdVSfpm = -600,
            MaxCruiseMach = 0.86, CruiseFL = 350, TypeRating = "A330"
        });

        // Boeing 737
        Register("737-800", new AircraftProfile
        {
            Name            = "Boeing 737-800",
            ICAOType        = "B738",
            Category        = AircraftCategory.NarrowBodyJet,
            VrefKts         = 138,
            VappKts         = 143,
            VrotateKts      = 152,
            V2Kts           = 160,
            FlapNames       = new[] { "0", "1", "2", "5", "10", "15", "25", "30", "40" },
            IdealTdPitchDeg = 2.5,
            MaxTdPitchDeg   = 5.0,
            MaxTdBankDeg    = 5.0,
            MaxTdVSfpm      = -600,
            GoodTdVSfpm     = -200,
            MaxLandWeightLbs = 146300,
            MaxTOWeightLbs  = 174200,
            FuelCapacityLbs = 46063,
            MaxCruiseMach   = 0.82,
            CruiseFL        = 370,
            TypeRating      = "B737"
        });

        Register("737-700", new AircraftProfile
        {
            Name = "Boeing 737-700", ICAOType = "B737",
            Category = AircraftCategory.NarrowBodyJet,
            VrefKts = 130, VappKts = 135,
            IdealTdPitchDeg = 2.5, GoodTdVSfpm = -200, MaxTdVSfpm = -600,
            MaxCruiseMach = 0.82, CruiseFL = 370, TypeRating = "B737"
        });

        Register("737 MAX", new AircraftProfile
        {
            Name = "Boeing 737 MAX", ICAOType = "B38M",
            Category = AircraftCategory.NarrowBodyJet,
            VrefKts = 138, VappKts = 143,
            IdealTdPitchDeg = 2.5, GoodTdVSfpm = -200, MaxTdVSfpm = -600,
            MaxCruiseMach = 0.82, CruiseFL = 390, TypeRating = "B737"
        });

        // Boeing 777
        Register("777", new AircraftProfile
        {
            Name = "Boeing 777", ICAOType = "B77W",
            Category = AircraftCategory.WidebodyJet,
            VrefKts = 145, VappKts = 150,
            IdealTdPitchDeg = 4.5, GoodTdVSfpm = -200, MaxTdVSfpm = -600,
            MaxCruiseMach = 0.89, CruiseFL = 350, TypeRating = "B777"
        });

        // Boeing 787
        Register("787", new AircraftProfile
        {
            Name = "Boeing 787 Dreamliner", ICAOType = "B78X",
            Category = AircraftCategory.WidebodyJet,
            VrefKts = 140, VappKts = 145,
            IdealTdPitchDeg = 4.0, GoodTdVSfpm = -200, MaxTdVSfpm = -600,
            MaxCruiseMach = 0.90, CruiseFL = 380, TypeRating = "B787"
        });

        // CRJ Family
        Register("CRJ", new AircraftProfile
        {
            Name = "Bombardier CRJ", ICAOType = "CRJ9",
            Category = AircraftCategory.RegionalJet,
            VrefKts = 125, VappKts = 130,
            IdealTdPitchDeg = 3.0, GoodTdVSfpm = -200, MaxTdVSfpm = -500,
            MaxCruiseMach = 0.82, CruiseFL = 350
        });

        // ATR
        Register("ATR", new AircraftProfile
        {
            Name = "ATR 72", ICAOType = "AT75",
            Category = AircraftCategory.Turboprop,
            VrefKts = 110, VappKts = 115,
            IdealTdPitchDeg = 2.5, GoodTdVSfpm = -200, MaxTdVSfpm = -400,
            MaxCruiseMach = 0.45, CruiseFL = 180
        });

        // Cessna 172
        Register("172", new AircraftProfile
        {
            Name = "Cessna 172", ICAOType = "C172",
            Category = AircraftCategory.SingleEnginePiston,
            VrefKts = 60, VappKts = 65,
            VrotateKts = 55, V2Kts = 70,
            IdealTdPitchDeg = 2.0, GoodTdVSfpm = -150, MaxTdVSfpm = -350,
            MaxCruiseMach = 0.18, CruiseFL = 60
        });

        Log.Information("[AircraftDB] {Count} aircraft profiles loaded", _profiles.Count);
    }

    private void Register(string key, AircraftProfile profile) =>
        _profiles[key.ToUpperInvariant()] = profile;
}

// =====================================================
// AIRCRAFT PROFILE
// =====================================================

public class AircraftProfile
{
    public string Name              { get; set; } = "Unknown";
    public string ICAOType          { get; set; } = "ZZZZ";
    public AircraftCategory Category { get; set; } = AircraftCategory.Unknown;
    public string TypeRating        { get; set; } = "";

    // Approach / Landing speeds
    public int VrefKts              { get; set; } = 130;
    public int VappKts              { get; set; } = 135;
    public int VrotateKts           { get; set; } = 140;
    public int V2Kts                { get; set; } = 150;

    // Flap configuration
    public int[] MaxFlapSpeed       { get; set; } = [];
    public string[] FlapNames       { get; set; } = [];

    // Touchdown targets
    public double IdealTdPitchDeg   { get; set; } = 3.0;
    public double MaxTdPitchDeg     { get; set; } = 6.0;
    public double MaxTdBankDeg      { get; set; } = 5.0;
    public int    GoodTdVSfpm       { get; set; } = -200;
    public int    MaxTdVSfpm        { get; set; } = -600;

    // Weights (lbs)
    public double MaxLandWeightLbs  { get; set; } = 0;
    public double MaxTOWeightLbs    { get; set; } = 0;
    public double FuelCapacityLbs   { get; set; } = 0;

    // Performance
    public double MaxCruiseMach     { get; set; } = 0.82;
    public int    CruiseFL          { get; set; } = 350;

    // Generic fallback
    public static AircraftProfile Generic => new()
    {
        Name = "Generic Aircraft", ICAOType = "ZZZZ",
        VrefKts = 130, VappKts = 135,
        IdealTdPitchDeg = 3.0, GoodTdVSfpm = -200, MaxTdVSfpm = -600,
        MaxCruiseMach = 0.82, CruiseFL = 350
    };
}

public enum AircraftCategory
{
    Unknown,
    SingleEnginePiston,
    MultiEnginePiston,
    Turboprop,
    RegionalJet,
    NarrowBodyJet,
    WidebodyJet,
    Supersonic
}
