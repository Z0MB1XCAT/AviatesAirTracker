namespace AviatesAirTracker.Core.Efb;

// ============================================================
// EFB LANDING DISTANCE PROCEDURES (in-flight failures)
//
// The "LDG DISTANCE PROCEDURE" section of an airline landing-performance tool (Fenix EFB,
// Flysmart In-Flight Landing, the QRH "landing with failures" tables): each failure
// multiplies the landing distance and may add a VREF increment. When several are selected
// the factors multiply and the LARGEST ΔVREF applies (increments are not additive).
//
// The list is per aircraft family, named the way that family's QRH/ECAM/EICAS names it:
// Airbus green/yellow/blue hydraulics (the A350 and A380 have no blue system), 737 systems
// A/B/standby, 777 and 787 left/centre/right, 747 systems 1–4, ATR blue/green (and no
// autobrake or jet reversers on the ATR).
//
// ⚠ TRAINING-GRADE, NOT QRH DATA: representative magnitudes for sim practice only, in the
// same spirit as AircraftPerformanceProfiles. Real factors are type-, configuration- and
// runway-state-specific.
// ============================================================

public sealed record LandingFailure(string Id, string Category, string Name, double DistanceFactor, int DeltaVrefKts,
    string Note = "", bool NoAutobrake = false, IReadOnlyList<string>? Families = null)
{
    public bool AppliesTo(string family) => Families == null || Families.Contains(family);
}

public static class EfbLandingFailures
{
    public const string AirbusNarrow = "airbus", AirbusWide = "airbus-wb", B737 = "b737", B777 = "b777",
                        B747 = "b747", Atr = "atr", Generic = "generic";

    private static readonly string[] Airbus = [AirbusNarrow, AirbusWide];
    private static readonly string[] Jets = [AirbusNarrow, AirbusWide, B737, B777, B747, Generic];
    private static readonly string[] BoeingWide = [B777, B747];

    public static readonly IReadOnlyList<LandingFailure> All =
    [
        // ── Airbus (A319/A320neo, A350, A380) ──
        new("elec-emer",    "Electrical",       "ELEC EMER CONFIG",                1.60, 10, "Reduced braking and no reversers on the emergency network", Families: Airbus),
        new("elec-dc-bus",  "Electrical",       "DC BUS 1+2 FAULT",                1.35,  0, "No autobrake, anti-skid degraded", NoAutobrake: true, Families: [AirbusNarrow]),
        new("fctl-spl",     "Flight Controls",  "SPOILERS: 2 OR MORE INOP",        1.20,  0, "Less lift dump on touchdown", Families: Airbus),
        new("fctl-stab",    "Flight Controls",  "STABILIZER JAMMED",               1.15,  5, Families: Airbus),
        new("flap-jam",     "Slats and Flaps",  "FLAPS JAMMED (SLATS OK)",         1.35, 25, Families: Airbus),
        new("slat-jam",     "Slats and Flaps",  "SLATS JAMMED (FLAPS OK)",         1.40, 25, Families: Airbus),
        new("slat-flap-0",  "Slats and Flaps",  "SLATS AND FLAPS JAMMED AT 0",     1.85, 55, "Consider the longest runway", Families: Airbus),
        new("hyd-g",        "Hydraulics",       "G SYS LO PR",                     1.25,  0, "Slow slats and flaps, no nosewheel steering", Families: Airbus),
        new("hyd-y",        "Hydraulics",       "Y SYS LO PR",                     1.10,  0, Families: Airbus),
        new("hyd-gy",       "Hydraulics",       "G + Y SYS LO PR",                 1.80, 25, "Alternate braking, flaps limited", Families: Airbus),
        new("hyd-gb",       "Hydraulics",       "G + B SYS LO PR",                 1.60, 10, Families: [AirbusNarrow]),
        new("nav-adr",      "Navigation",       "ADR 1 + 2 FAULT / UNRELIABLE SPD",1.20, 10, Families: Airbus),
        new("bleed-dual",   "Bleed",            "DUAL BLEED LOSS",                 1.00,  0, "No landing-distance penalty", Families: Airbus),

        // ── Boeing 737 NG / MAX ──
        new("b737-hyd-a",   "Hydraulics",       "LOSS OF SYSTEM A",                1.20,  0, "Ground spoilers inoperative", Families: [B737]),
        new("b737-hyd-b",   "Hydraulics",       "LOSS OF SYSTEM B",                1.30,  0, "Alternate flaps (slow), alternate brakes, autobrake inoperative", NoAutobrake: true, Families: [B737]),
        new("b737-hyd-ab",  "Hydraulics",       "LOSS OF SYSTEMS A AND B",         1.85, 15, "Manual reversion, flaps 15 by alternate flaps", NoAutobrake: true, Families: [B737]),
        new("b737-te",      "Flaps",            "TRAILING EDGE FLAP DISAGREE",     1.40, 30, "VREF per the QRH flap position table", Families: [B737]),
        new("b737-le",      "Flaps",            "LEADING EDGE FLAPS TRANSIT",      1.25, 15, Families: [B737]),
        new("b737-flaps-up","Flaps",            "FLAPS UP LANDING",                1.90, 55, "Consider the longest runway", Families: [B737]),
        new("b737-stby",    "Electrical",       "STANDBY POWER ONLY",              1.30,  0, "No autobrake, no reverser indication", NoAutobrake: true, Families: [B737]),
        new("b737-spd",     "Flight Controls",  "SPEED BRAKE DO NOT ARM",          1.20,  0, "Deploy the speedbrake manually", Families: [B737]),
        new("b737-stab",    "Flight Controls",  "STABILIZER TRIM INOPERATIVE",     1.15,  5, Families: [B737]),
        new("b737-ias",     "Navigation",       "AIRSPEED UNRELIABLE",             1.20, 10, Families: [B737]),

        // ── Boeing 777 / 787 and 747-8 ──
        new("b777-hyd-l",   "Hydraulics",       "HYD PRESS SYS L",                 1.05,  0, Families: [B777]),
        new("b777-hyd-c",   "Hydraulics",       "HYD PRESS SYS C",                 1.15,  0, "Flaps and slats in secondary mode (slow)", Families: [B777]),
        new("b777-hyd-r",   "Hydraulics",       "HYD PRESS SYS R",                 1.20,  0, "Normal brakes lost: alternate brakes, autobrake inoperative", NoAutobrake: true, Families: [B777]),
        new("b777-hyd-lc",  "Hydraulics",       "HYD PRESS SYS L + C",             1.30,  5, Families: [B777]),
        new("b777-hyd-cr",  "Hydraulics",       "HYD PRESS SYS C + R",             1.50, 10, "Alternate brakes, flaps and slats secondary", NoAutobrake: true, Families: [B777]),
        new("b747-hyd-1",   "Hydraulics",       "HYD PRESS SYS 1",                 1.05,  0, Families: [B747]),
        new("b747-hyd-4",   "Hydraulics",       "HYD PRESS SYS 4",                 1.20,  0, "Normal brakes lost: alternate brakes, autobrake inoperative", NoAutobrake: true, Families: [B747]),
        new("b747-hyd-12",  "Hydraulics",       "HYD PRESS SYS 1 + 2",             1.30,  5, Families: [B747]),
        new("bw-te",        "Flaps",            "FLAPS DRIVE / TE FLAP DISAGREE",  1.35, 25, Families: BoeingWide),
        new("bw-le",        "Flaps",            "SLAT / LE FLAP DISAGREE",         1.30, 20, Families: BoeingWide),
        new("bw-stby",      "Electrical",       "STANDBY POWER / BUS OFF",         1.30,  0, "Autobrake may be unavailable", NoAutobrake: true, Families: BoeingWide),
        new("bw-spd",       "Flight Controls",  "SPEEDBRAKE AUTO / SPOILERS",      1.20,  0, "Deploy the speedbrake manually", Families: BoeingWide),
        new("bw-ias",       "Navigation",       "AIRSPEED UNRELIABLE",             1.20, 10, Families: BoeingWide),

        // ── ATR 42 / 72 (no autobrake, no jet reversers) ──
        new("atr-hyd-b",    "Hydraulics",       "BLUE HYD SYS LO PR",              1.15,  0, "Flaps and roll spoilers lost if not already set", Families: [Atr]),
        new("atr-hyd-g",    "Hydraulics",       "GREEN HYD SYS LO PR",             1.55,  0, "Normal brakes lost: emergency brake, no anti-skid", Families: [Atr]),
        new("atr-hyd-bg",   "Hydraulics",       "BLUE + GREEN HYD SYS LO PR",      1.80, 15, "Gear by gravity, emergency brake, flaps as set", Families: [Atr]),
        new("atr-flap",     "Flaps",            "FLAPS JAM / FLAPS 0 LANDING",     1.45, 20, "Approach speed per the QRH flaps 0 table", Families: [Atr]),
        new("atr-icing",    "Anti-Ice",         "SEVERE ICING (ICING AOA LIT)",    1.15, 10, "Landing flaps 15 per the icing procedure", Families: [Atr]),
        new("atr-gi",       "Engine",           "ONE PROPELLER NOT IN GROUND IDLE",1.15,  0, "No reverse on that side", Families: [Atr]),

        // ── every family ──
        new("ice-accretion","Anti-Ice",         "SEVERE ICE ACCRETION",            1.10,  5, "VREF increment for ice on unprotected surfaces", Families: Jets),
        new("brk-antiskid", "Braking",          "ANTI-SKID INOP",                  1.55,  0, "Alternate braking without anti-skid", NoAutobrake: true),
        new("brk-autobrk",  "Braking",          "AUTOBRAKE INOP",                  1.05,  0, "Manual braking, computed at medium deceleration", NoAutobrake: true, Families: Jets),
        new("eng-oei",      "Engine",           "ONE ENGINE INOP",                 1.10,  0, "Only one reverser available"),
        new("eng-rev",      "Engine",           "REVERSER UNLOCKED / INOP",        1.05,  0, Families: Jets),
    ];

    /// <summary>The failure family for a SimBrief designator, alias or add-on title.</summary>
    public static string FamilyOf(string? aircraftCode)
    {
        var code = AircraftPerformanceProfiles.Find(aircraftCode)?.Code ?? "";
        return code switch
        {
            "A319" or "A20N" or "A21X" => AirbusNarrow,
            "A359" or "A388" or "A35F" => AirbusWide,
            "B736" or "B738" or "B739" or "B38M" => B737,
            "B77F" or "B77W" or "B789" => B777,
            "B748" => B747,
            "AT45" or "AT76" => Atr,
            _ => Generic,
        };
    }

    /// <summary>The failures a pilot can select for this aircraft, in display order.</summary>
    public static IReadOnlyList<LandingFailure> ForAircraft(string? aircraftCode)
    {
        var fam = FamilyOf(aircraftCode);
        return All.Where(f => f.AppliesTo(fam)).ToList();
    }

    public static IReadOnlyList<string> CategoriesFor(string? aircraftCode) =>
        ForAircraft(aircraftCode).Select(f => f.Category).Distinct().ToList();

    public static IReadOnlyList<string> Categories { get; } =
        All.Select(f => f.Category).Distinct().ToList();

    public static LandingFailure? Find(string id) =>
        All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
}
