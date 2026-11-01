namespace AviatesAirTracker.Core.Analytics;

/// <summary>Computed %MAC for zero-fuel and takeoff weight. LoadIndex (LIZFW/LITOW)
/// is deliberately NOT computed here — it's an airline-specific computerized-loadsheet
/// convention derived from %MAC via reference constants (K1/K2) this VA hasn't supplied,
/// and inventing those constants would produce confidently-wrong numbers. Only %MAC,
/// which has a standard, well-defined formula, is calculated.</summary>
public class WeightBalanceResult
{
    public double MacZfwPercent { get; set; }
    public double MacTowPercent { get; set; }
}

/// <summary>Per-aircraft-type weight-and-balance reference data: basic empty weight (BEW),
/// its CG arm, the leading edge of mean aerodynamic chord (LEMAC), and MAC length — all
/// needed to convert a ZFW/TOW CG arm into a %MAC figure. Loaded-item arms (pax, cargo,
/// fuel) are approximated as a single "payload arm" close to the aircraft's typical
/// loading station, which is accurate enough for an illustrative ACARS loadsheet but is
/// NOT certified weight-and-balance data — see the comment on SeedTable below.
/// </summary>
public class AircraftWbProfile
{
    public double BewKg { get; set; }
    public double BewArmM { get; set; }
    public double LemacM { get; set; }
    public double MacLengthM { get; set; }
    public double PayloadArmM { get; set; }
    public double FuelArmM { get; set; }
}

public static class WeightBalanceDatabase
{
    // Keyed by normalized (uppercase, single-spaced) aircraft type string, matching
    // Services.RoutesService.Subsidiary's normalization convention.
    private static readonly Dictionary<string, AircraftWbProfile> Profiles = SeedTable();

    // ── APPROXIMATE, UNVERIFIED REFERENCE DATA ──────────────────────────────────
    // The two entries below are representative, publicly-cited approximations for
    // the A320 and 737 families (not read from a certified AFM/weight-and-balance
    // manual for a specific registration). They are seeded so the Hoppies loadsheet
    // has *something* to show for the two most common narrow-body families, but
    // should be cross-checked against your actual aircraft add-ons' real W&B data
    // before being trusted operationally. Every other fleet type intentionally has
    // NO entry — GetProfile returns null for anything not listed here, and the
    // loadsheet composer omits MAC/LI entirely rather than guess.
    //
    // To add a verified type: add an entry below with real BEW/arm/LEMAC/MAC-length
    // figures from that aircraft's actual weight-and-balance manual.
    private static Dictionary<string, AircraftWbProfile> SeedTable() => new()
    {
        ["A320NEO"] = new AircraftWbProfile
        {
            BewKg = 44300, BewArmM = 18.8, LemacM = 18.14, MacLengthM = 4.19,
            PayloadArmM = 19.0, FuelArmM = 18.6
        },
        ["B38M"] = new AircraftWbProfile
        {
            BewKg = 45070, BewArmM = 17.6, LemacM = 16.94, MacLengthM = 4.17,
            PayloadArmM = 18.0, FuelArmM = 17.4
        }
    };

    private static string Normalize(string type) =>
        string.Join(' ', (type ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
              .ToUpperInvariant();

    // ICAO type designators (what SimBrief / the EFB use) that name exactly the same airframe
    // as a seeded key. Deliberately NOT "B738" → "B38M": the 737-800 and MAX 8 have different
    // empty weights and arms, and this table must not guess.
    private static readonly Dictionary<string, string> IcaoAliases = new()
    {
        ["A20N"] = "A320NEO",
    };

    /// <summary>Returns the WB profile for the given aircraft type, or null if not seeded.</summary>
    public static AircraftWbProfile? GetProfile(string aircraftType)
    {
        var key = Normalize(aircraftType);
        if (IcaoAliases.TryGetValue(key, out var alias)) key = alias;
        return Profiles.TryGetValue(key, out var profile) ? profile : null;
    }

    /// <summary>Computes %MAC for ZFW and TOW given the aircraft's WB profile and the
    /// loaded weights. Returns null if the profile is missing or weights are non-positive.</summary>
    public static WeightBalanceResult? Compute(string aircraftType, double zfwKg, double towKg)
    {
        var profile = GetProfile(aircraftType);
        if (profile is null || zfwKg <= 0 || towKg <= 0) return null;

        var payloadKg = Math.Max(0, zfwKg - profile.BewKg);
        var fuelKg = Math.Max(0, towKg - zfwKg);

        double CgArm(double bewMoment, double extraKg, double extraArm, double totalKg) =>
            totalKg <= 0 ? profile.BewArmM : (bewMoment + extraKg * extraArm) / totalKg;

        var bewMoment = profile.BewKg * profile.BewArmM;
        var zfwArm = CgArm(bewMoment, payloadKg, profile.PayloadArmM, zfwKg);
        var towMoment = bewMoment + payloadKg * profile.PayloadArmM;
        var towArm = CgArm(towMoment, fuelKg, profile.FuelArmM, towKg);

        double MacPercent(double arm) => (arm - profile.LemacM) / profile.MacLengthM * 100.0;

        return new WeightBalanceResult
        {
            MacZfwPercent = MacPercent(zfwArm),
            MacTowPercent = MacPercent(towArm)
        };
    }
}
