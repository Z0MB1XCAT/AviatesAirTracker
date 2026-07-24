namespace AviatesAirTracker.Core.Efb;

// ============================================================
// AIRCRAFT PERFORMANCE PROFILES
//
// Reference performance data for the AviatesAir fleet, keyed by the
// SimBrief ICAO designator used across the app (B738, A20N, …).
//
// ⚠ TRAINING AID — the numbers here are simplified reference values,
// NOT certified AFM/QRH data. They give the EFB physically-plausible
// V-speeds and field lengths for immersion and airmanship practice.
// Do not use for real-world dispatch.
//
// To add an aircraft: append a new EfbAircraftProfile to All. The
// performance calculator and EFB dropdowns pick it up automatically.
// ============================================================

/// <summary>A single reference flap detent with its speed/distance multipliers.</summary>
public sealed record FlapSetting(string Label, double VSpeedFactor, double DistanceFactor);

public sealed class EfbAircraftProfile
{
    public required string Code { get; init; }          // SimBrief ICAO designator, e.g. "B738"
    public required string Name { get; init; }          // Display name
    public required string Category { get; init; }      // "Boeing" | "Airbus" | "Turboprop"

    // Weights (lbs)
    public required double MtowLbs { get; init; }
    public required double MlwLbs { get; init; }
    public required double OewLbs { get; init; }
    public required double MaxFuelLbs { get; init; }

    // Reference V-speeds are quoted at RefWeightLbs, flaps "reference" detent,
    // sea level / ISA. The calculator scales them by √(W/Wref) and applies
    // flap / density corrections.
    public required double RefWeightLbs { get; init; }
    public required double V1Ref { get; init; }
    public required double VrRef { get; init; }
    public required double V2Ref { get; init; }
    public required double VrefLandingRef { get; init; }   // at MLW, landing flap

    // Reference field lengths (metres), dry, sea level, ISA, no wind.
    public required double TodrRefM { get; init; }         // at MTOW
    public required double LdrRefM { get; init; }          // at MLW, medium autobrake

    public required IReadOnlyList<FlapSetting> TakeoffFlaps { get; init; }
    public required IReadOnlyList<FlapSetting> LandingFlaps { get; init; }
}

public static class AircraftPerformanceProfiles
{
    // Common Boeing-style flap tables (relative multipliers vs. the reference detent).
    private static readonly IReadOnlyList<FlapSetting> BoeingToFlaps =
    [
        new("Flaps 1", 1.06, 1.18),
        new("Flaps 5", 1.00, 1.00),   // reference
        new("Flaps 15", 0.95, 0.88),
    ];
    private static readonly IReadOnlyList<FlapSetting> BoeingLdgFlaps =
    [
        new("Flaps 30", 1.00, 1.00),  // reference
        new("Flaps 40", 0.96, 0.90),
    ];
    private static readonly IReadOnlyList<FlapSetting> AirbusToFlaps =
    [
        new("Config 1+F", 1.05, 1.16),
        new("Config 2", 1.00, 1.00),  // reference
        new("Config 3", 0.96, 0.90),
    ];
    private static readonly IReadOnlyList<FlapSetting> AirbusLdgFlaps =
    [
        new("Config 3", 1.03, 1.10),
        new("Config Full", 1.00, 1.00), // reference
    ];
    private static readonly IReadOnlyList<FlapSetting> AtrToFlaps =
    [
        new("Flaps 15", 1.00, 1.00),
    ];
    private static readonly IReadOnlyList<FlapSetting> AtrLdgFlaps =
    [
        new("Flaps 15", 1.04, 1.12),
        new("Flaps 30", 1.00, 1.00),
    ];

    public static readonly IReadOnlyList<EfbAircraftProfile> All =
    [
        new()
        {
            Code = "B738", Name = "Boeing 737-800", Category = "Boeing",
            MtowLbs = 174_200, MlwLbs = 146_300, OewLbs = 91_300, MaxFuelLbs = 46_000,
            RefWeightLbs = 145_000, V1Ref = 145, VrRef = 148, V2Ref = 152, VrefLandingRef = 142,
            TodrRefM = 2_300, LdrRefM = 1_600,
            TakeoffFlaps = BoeingToFlaps, LandingFlaps = BoeingLdgFlaps,
        },
        new()
        {
            Code = "A20N", Name = "Airbus A320neo", Category = "Airbus",
            MtowLbs = 174_165, MlwLbs = 145_505, OewLbs = 93_900, MaxFuelLbs = 42_700,
            RefWeightLbs = 140_000, V1Ref = 142, VrRef = 145, V2Ref = 149, VrefLandingRef = 138,
            TodrRefM = 2_100, LdrRefM = 1_500,
            TakeoffFlaps = AirbusToFlaps, LandingFlaps = AirbusLdgFlaps,
        },
        new()
        {
            Code = "A21X", Name = "Airbus A321XLR", Category = "Airbus",
            MtowLbs = 222_667, MlwLbs = 175_600, OewLbs = 110_500, MaxFuelLbs = 72_000,
            RefWeightLbs = 175_000, V1Ref = 150, VrRef = 154, V2Ref = 158, VrefLandingRef = 145,
            TodrRefM = 2_650, LdrRefM = 1_750,
            TakeoffFlaps = AirbusToFlaps, LandingFlaps = AirbusLdgFlaps,
        },
        new()
        {
            Code = "B77W", Name = "Boeing 777-300ER", Category = "Boeing",
            MtowLbs = 775_000, MlwLbs = 554_000, OewLbs = 370_000, MaxFuelLbs = 320_000,
            RefWeightLbs = 550_000, V1Ref = 160, VrRef = 168, V2Ref = 175, VrefLandingRef = 150,
            TodrRefM = 3_200, LdrRefM = 1_900,
            TakeoffFlaps = BoeingToFlaps, LandingFlaps = BoeingLdgFlaps,
        },
        new()
        {
            Code = "B789", Name = "Boeing 787-9", Category = "Boeing",
            MtowLbs = 561_500, MlwLbs = 425_000, OewLbs = 284_000, MaxFuelLbs = 223_000,
            RefWeightLbs = 420_000, V1Ref = 155, VrRef = 162, V2Ref = 168, VrefLandingRef = 148,
            TodrRefM = 2_900, LdrRefM = 1_750,
            TakeoffFlaps = BoeingToFlaps, LandingFlaps = BoeingLdgFlaps,
        },
        new()
        {
            Code = "A35F", Name = "Airbus A350F", Category = "Airbus",
            MtowLbs = 705_500, MlwLbs = 518_000, OewLbs = 320_000, MaxFuelLbs = 296_000,
            RefWeightLbs = 515_000, V1Ref = 158, VrRef = 165, V2Ref = 171, VrefLandingRef = 149,
            TodrRefM = 3_000, LdrRefM = 1_800,
            TakeoffFlaps = AirbusToFlaps, LandingFlaps = AirbusLdgFlaps,
        },
        new()
        {
            Code = "AT76", Name = "ATR 72-600", Category = "Turboprop",
            MtowLbs = 50_265, MlwLbs = 49_600, OewLbs = 29_500, MaxFuelLbs = 11_000,
            RefWeightLbs = 48_000, V1Ref = 105, VrRef = 108, V2Ref = 112, VrefLandingRef = 104,
            TodrRefM = 1_350, LdrRefM = 1_050,
            TakeoffFlaps = AtrToFlaps, LandingFlaps = AtrLdgFlaps,
        },
    ];

    public static EfbAircraftProfile? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        code = code.Trim();
        return All.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase))
            // tolerate a couple of common aliases so a raw SimConnect title still matches
            ?? All.FirstOrDefault(p => code.Contains(p.Code, StringComparison.OrdinalIgnoreCase));
    }
}
