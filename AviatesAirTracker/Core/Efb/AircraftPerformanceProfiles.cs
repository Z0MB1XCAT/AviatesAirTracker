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

    /// <summary>Other designators and title fragments that mean this aircraft (SimBrief codes,
    /// add-on titles), e.g. "A350" or "737-900", matched by <see cref="AircraftPerformanceProfiles.Find"/>.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>True for types AviatesAir operates (listed first in the EFB).</summary>
    public bool Fleet { get; init; } = true;
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
        new("Flaps 25", 1.03, 1.07),  // 737 company minimum (CN-003)
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
    // ATR 42: takeoff 15, landing 25 or 35 (the 72 lands 15 or 30)
    private static readonly IReadOnlyList<FlapSetting> Atr42LdgFlaps =
    [
        new("Flaps 25", 1.04, 1.10),
        new("Flaps 35", 1.00, 1.00),
    ];
    // 777 / 787: takeoff 5, 15 or 20; landing 25 or 30
    private static readonly IReadOnlyList<FlapSetting> B777ToFlaps =
    [
        new("Flaps 5", 1.00, 1.00),   // reference
        new("Flaps 15", 0.96, 0.90),
        new("Flaps 20", 0.94, 0.86),
    ];
    // 747-8: takeoff 10 or 20; landing 25 or 30
    private static readonly IReadOnlyList<FlapSetting> B747ToFlaps =
    [
        new("Flaps 10", 1.00, 1.00),  // reference
        new("Flaps 20", 0.95, 0.88),
    ];
    private static readonly IReadOnlyList<FlapSetting> WidebodyBoeingLdgFlaps =
    [
        new("Flaps 25", 1.03, 1.07),
        new("Flaps 30", 1.00, 1.00),  // reference
    ];

    // The AviatesAir fleet first (the 12 types SimBriefService maps), then a few other types
    // pilots fly on charters. Weights are typical certified maxima for a common variant; speeds
    // and field lengths are representative values at the reference conditions, not AFM data.
    public static readonly IReadOnlyList<EfbAircraftProfile> All =
    [
        // ── Airbus ──
        new()
        {
            Code = "A319", Name = "Airbus A319", Category = "Airbus", Aliases = ["A319CEO", "A19N", "A319NEO", "A319-100"],
            MtowLbs = 166_450, MlwLbs = 137_790, OewLbs = 89_950, MaxFuelLbs = 41_200,
            RefWeightLbs = 132_000, V1Ref = 133, VrRef = 136, V2Ref = 140, VrefLandingRef = 129,
            TodrRefM = 1_900, LdrRefM = 1_400,
            TakeoffFlaps = AirbusToFlaps, LandingFlaps = AirbusLdgFlaps,
        },
        new()
        {
            Code = "A20N", Name = "Airbus A320neo", Category = "Airbus", Aliases = ["A320NEO", "A320", "A320-200N", "A320-271N"],
            MtowLbs = 174_165, MlwLbs = 145_505, OewLbs = 93_900, MaxFuelLbs = 42_700,
            RefWeightLbs = 140_000, V1Ref = 142, VrRef = 145, V2Ref = 149, VrefLandingRef = 138,
            TodrRefM = 2_100, LdrRefM = 1_500,
            TakeoffFlaps = AirbusToFlaps, LandingFlaps = AirbusLdgFlaps,
        },
        new()
        {
            Code = "A359", Name = "Airbus A350-900", Category = "Airbus", Aliases = ["A350", "A350-900", "A3509", "A35K", "A350-1000"],
            MtowLbs = 617_300, MlwLbs = 456_360, OewLbs = 313_900, MaxFuelLbs = 248_700,
            RefWeightLbs = 507_000, V1Ref = 150, VrRef = 155, V2Ref = 160, VrefLandingRef = 141,
            TodrRefM = 2_650, LdrRefM = 1_750,
            TakeoffFlaps = AirbusToFlaps, LandingFlaps = AirbusLdgFlaps,
        },
        new()
        {
            Code = "A388", Name = "Airbus A380-800", Category = "Airbus", Aliases = ["A380", "A380-800", "A380-841", "A380-842"],
            MtowLbs = 1_267_650, MlwLbs = 868_620, OewLbs = 610_700, MaxFuelLbs = 559_900,
            RefWeightLbs = 1_102_300, V1Ref = 150, VrRef = 156, V2Ref = 162, VrefLandingRef = 139,
            TodrRefM = 3_000, LdrRefM = 2_000,
            TakeoffFlaps = AirbusToFlaps, LandingFlaps = AirbusLdgFlaps,
        },
        // ── Boeing 737 ──
        new()
        {
            Code = "B736", Name = "Boeing 737-600", Category = "Boeing", Aliases = ["737-600", "B737-600"],
            MtowLbs = 144_500, MlwLbs = 120_500, OewLbs = 80_200, MaxFuelLbs = 46_000,
            RefWeightLbs = 120_000, V1Ref = 128, VrRef = 130, V2Ref = 136, VrefLandingRef = 128,
            TodrRefM = 1_900, LdrRefM = 1_300,
            TakeoffFlaps = BoeingToFlaps, LandingFlaps = BoeingLdgFlaps,
        },
        new()
        {
            Code = "B738", Name = "Boeing 737-800", Category = "Boeing", Aliases = ["737-800", "B737-800", "B738W"],
            MtowLbs = 174_200, MlwLbs = 146_300, OewLbs = 91_300, MaxFuelLbs = 46_000,
            RefWeightLbs = 145_000, V1Ref = 145, VrRef = 148, V2Ref = 152, VrefLandingRef = 142,
            TodrRefM = 2_300, LdrRefM = 1_600,
            TakeoffFlaps = BoeingToFlaps, LandingFlaps = BoeingLdgFlaps,
        },
        new()
        {
            Code = "B739", Name = "Boeing 737-900ER", Category = "Boeing", Aliases = ["B739ER", "737-900ER", "737-900", "B737-900"],
            MtowLbs = 187_700, MlwLbs = 157_300, OewLbs = 98_500, MaxFuelLbs = 46_000,
            RefWeightLbs = 160_000, V1Ref = 150, VrRef = 153, V2Ref = 158, VrefLandingRef = 150,
            TodrRefM = 2_800, LdrRefM = 1_750,
            TakeoffFlaps = BoeingToFlaps, LandingFlaps = BoeingLdgFlaps,
        },
        new()
        {
            Code = "B38M", Name = "Boeing 737 MAX 8", Category = "Boeing", Aliases = ["737 MAX 8", "737MAX8", "B737MAX", "737 MAX", "B37M", "B39M"],
            MtowLbs = 182_200, MlwLbs = 152_800, OewLbs = 99_360, MaxFuelLbs = 45_900,
            RefWeightLbs = 150_000, V1Ref = 145, VrRef = 149, V2Ref = 154, VrefLandingRef = 146,
            TodrRefM = 2_500, LdrRefM = 1_650,
            TakeoffFlaps = BoeingToFlaps, LandingFlaps = BoeingLdgFlaps,
        },
        // ── Boeing widebody ──
        new()
        {
            Code = "B77F", Name = "Boeing 777F", Category = "Boeing", Aliases = ["777F", "B777F", "777-F", "B77L", "777-200LR"],
            MtowLbs = 766_800, MlwLbs = 575_000, OewLbs = 318_300, MaxFuelLbs = 320_800,
            RefWeightLbs = 600_000, V1Ref = 154, VrRef = 162, V2Ref = 168, VrefLandingRef = 152,
            TodrRefM = 3_100, LdrRefM = 2_000,
            TakeoffFlaps = B777ToFlaps, LandingFlaps = WidebodyBoeingLdgFlaps,
        },
        new()
        {
            Code = "B748", Name = "Boeing 747-8", Category = "Boeing", Aliases = ["747-8", "B747-8", "B748F", "B74F", "747-8F", "B747"],
            MtowLbs = 987_000, MlwLbs = 688_000, OewLbs = 485_300, MaxFuelLbs = 420_000,
            RefWeightLbs = 800_000, V1Ref = 157, VrRef = 166, V2Ref = 173, VrefLandingRef = 155,
            TodrRefM = 3_200, LdrRefM = 2_100,
            TakeoffFlaps = B747ToFlaps, LandingFlaps = WidebodyBoeingLdgFlaps,
        },
        // ── ATR ──
        new()
        {
            Code = "AT45", Name = "ATR 42-600", Category = "Turboprop", Aliases = ["AT46", "AT42", "AT43", "ATR42", "ATR 42", "ATR42-600"],
            MtowLbs = 41_005, MlwLbs = 40_345, OewLbs = 25_460, MaxFuelLbs = 9_920,
            RefWeightLbs = 37_480, V1Ref = 103, VrRef = 105, V2Ref = 110, VrefLandingRef = 104,
            TodrRefM = 1_150, LdrRefM = 980,
            TakeoffFlaps = AtrToFlaps, LandingFlaps = Atr42LdgFlaps,
        },
        new()
        {
            Code = "AT76", Name = "ATR 72-600", Category = "Turboprop", Aliases = ["AT75", "AT72", "AT73", "ATR72", "ATR 72", "ATR72-600"],
            MtowLbs = 50_265, MlwLbs = 49_600, OewLbs = 29_500, MaxFuelLbs = 11_000,
            RefWeightLbs = 48_000, V1Ref = 105, VrRef = 108, V2Ref = 112, VrefLandingRef = 104,
            TodrRefM = 1_350, LdrRefM = 1_050,
            TakeoffFlaps = AtrToFlaps, LandingFlaps = AtrLdgFlaps,
        },
        // ── other types (not in the AviatesAir fleet) ──
        new()
        {
            Code = "A21X", Name = "Airbus A321XLR", Category = "Airbus", Fleet = false, Aliases = ["A321XLR"],
            MtowLbs = 222_667, MlwLbs = 175_600, OewLbs = 110_500, MaxFuelLbs = 72_000,
            RefWeightLbs = 175_000, V1Ref = 150, VrRef = 154, V2Ref = 158, VrefLandingRef = 145,
            TodrRefM = 2_650, LdrRefM = 1_750,
            TakeoffFlaps = AirbusToFlaps, LandingFlaps = AirbusLdgFlaps,
        },
        new()
        {
            Code = "B77W", Name = "Boeing 777-300ER", Category = "Boeing", Fleet = false, Aliases = ["777-300ER", "B777-300ER"],
            MtowLbs = 775_000, MlwLbs = 554_000, OewLbs = 370_000, MaxFuelLbs = 320_000,
            RefWeightLbs = 550_000, V1Ref = 160, VrRef = 168, V2Ref = 175, VrefLandingRef = 150,
            TodrRefM = 3_200, LdrRefM = 1_900,
            TakeoffFlaps = BoeingToFlaps, LandingFlaps = BoeingLdgFlaps,
        },
        new()
        {
            Code = "B789", Name = "Boeing 787-9", Category = "Boeing", Fleet = false, Aliases = ["787-9", "B787-9"],
            MtowLbs = 561_500, MlwLbs = 425_000, OewLbs = 284_000, MaxFuelLbs = 223_000,
            RefWeightLbs = 420_000, V1Ref = 155, VrRef = 162, V2Ref = 168, VrefLandingRef = 148,
            TodrRefM = 2_900, LdrRefM = 1_750,
            TakeoffFlaps = BoeingToFlaps, LandingFlaps = BoeingLdgFlaps,
        },
        new()
        {
            Code = "A35F", Name = "Airbus A350F", Category = "Airbus", Fleet = false, Aliases = ["A350F"],
            MtowLbs = 705_500, MlwLbs = 518_000, OewLbs = 320_000, MaxFuelLbs = 296_000,
            RefWeightLbs = 515_000, V1Ref = 158, VrRef = 165, V2Ref = 171, VrefLandingRef = 149,
            TodrRefM = 3_000, LdrRefM = 1_800,
            TakeoffFlaps = AirbusToFlaps, LandingFlaps = AirbusLdgFlaps,
        },
    ];

    private static string Key(string s) => new(s.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The profile for a SimBrief designator, an alias, or an add-on title that contains
    /// one ("PMDG 737-800", "Airbus A380-842"). Null when nothing matches: callers must not
    /// substitute another type's data.</summary>
    public static EfbAircraftProfile? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var k = Key(code);
        if (k.Length == 0) return null;
        // exact code, then exact alias
        var hit = All.FirstOrDefault(p => Key(p.Code) == k)
               ?? All.FirstOrDefault(p => p.Aliases.Any(a => Key(a) == k));
        if (hit != null) return hit;
        // a title containing a code or alias: the longest match wins ("737MAX8" before "737")
        return All.SelectMany(p => p.Aliases.Append(p.Code).Select(a => (p, a: Key(a))))
                  .Where(x => x.a.Length >= 4 && k.Contains(x.a))
                  .OrderByDescending(x => x.a.Length)
                  .Select(x => x.p)
                  .FirstOrDefault();
    }
}
