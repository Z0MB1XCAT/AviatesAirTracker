namespace AviatesAirTracker.Core.Efb;

// ============================================================
// EFB CHECKLIST LIBRARY
//
// Default AviatesAir airline-style normal checklists, one per phase.
// The library is static reference data; live check state lives in
// EfbChecklistService so multiple devices can share progress.
//
// To add an airline-specific checklist: append to DefaultChecklists
// (or later, load from a JSON pack keyed by aircraft type).
// ============================================================

public sealed class EfbChecklistItemDef
{
    public required string Id { get; init; }
    public required string Challenge { get; init; }
    public required string Response { get; init; }
}

public sealed class EfbChecklistDef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Phase { get; init; }
    public required IReadOnlyList<EfbChecklistItemDef> Items { get; init; }
}

public static class EfbChecklistLibrary
{
    private static EfbChecklistItemDef I(string id, string challenge, string response) =>
        new() { Id = id, Challenge = challenge, Response = response };

    public static readonly IReadOnlyList<EfbChecklistDef> DefaultChecklists =
    [
        new()
        {
            Id = "before-start", Name = "Before Start", Phase = "Preflight",
            Items =
            [
                I("preflight", "Preflight inspection", "COMPLETE"),
                I("gear-pins", "Gear pins & covers", "REMOVED"),
                I("signs", "Seatbelt / No-smoking signs", "ON"),
                I("fuel", "Fuel quantity", "____ CHECKED"),
                I("mcdu", "FMS / MCDU init", "COMPLETE"),
                I("altimeters", "Altimeters", "QNH SET"),
                I("beacon", "Beacon", "ON"),
                I("parking-brake", "Parking brake", "SET"),
            ],
        },
        new()
        {
            Id = "before-taxi", Name = "Before Taxi", Phase = "Taxi",
            Items =
            [
                I("engines", "Engines", "STARTED / STABLE"),
                I("gen", "Generators", "ON"),
                I("flaps", "Flaps", "SET FOR T/O"),
                I("flight-controls", "Flight controls", "CHECKED"),
                I("apu", "APU (as required)", "OFF/ON"),
                I("gnd-equip", "Ground equipment", "CLEAR"),
                I("taxi-clearance", "Taxi clearance", "RECEIVED"),
            ],
        },
        new()
        {
            Id = "before-takeoff", Name = "Before Takeoff", Phase = "Takeoff",
            Items =
            [
                I("takeoff-brief", "Takeoff briefing", "COMPLETE"),
                I("vspeeds", "V-speeds (V1/VR/V2)", "SET & CROSS-CHECKED"),
                I("flaps-to", "Flaps", "CONFIRM T/O SETTING"),
                I("cabin", "Cabin", "SECURE"),
                I("tcas", "TCAS", "TA/RA"),
                I("strobes", "Strobe lights", "ON"),
                I("transponder", "Transponder", "ON / ALT"),
                I("clearance", "Line-up / T/O clearance", "RECEIVED"),
            ],
        },
        new()
        {
            Id = "after-takeoff", Name = "After Takeoff / Climb", Phase = "Climb",
            Items =
            [
                I("gear-up", "Landing gear", "UP"),
                I("flaps-up", "Flaps", "RETRACTED ON SCHEDULE"),
                I("thrust", "Climb thrust", "SET"),
                I("packs", "Packs / bleeds", "ON"),
                I("landing-lights", "Landing lights (>10,000)", "OFF"),
                I("altimeters-std", "Altimeters (TA)", "STD SET"),
            ],
        },
        new()
        {
            Id = "cruise", Name = "Cruise", Phase = "Cruise",
            Items =
            [
                I("cruise-alt", "Cruise altitude", "REACHED / SET"),
                I("fuel-check", "Fuel vs plan", "CHECKED"),
                I("systems", "Systems", "NORMAL"),
                I("wx", "Weather ahead", "REVIEWED"),
            ],
        },
        new()
        {
            Id = "descent", Name = "Descent", Phase = "Descent",
            Items =
            [
                I("atis", "Arrival ATIS", "RECEIVED"),
                I("approach-brief", "Approach & landing briefing", "COMPLETE"),
                I("baro", "Altimeters", "QNH SET AT TA"),
                I("fuel-land", "Landing fuel", "CHECKED"),
                I("minimums", "Minimums / MDA-DA", "SET"),
            ],
        },
        new()
        {
            Id = "approach", Name = "Approach", Phase = "Approach",
            Items =
            [
                I("speed", "Speed", "MANAGED / SET"),
                I("nav", "Nav aids / course", "TUNED & IDENT"),
                I("flaps-appr", "Flaps", "APPROACH SETTING"),
                I("cabin-secure", "Cabin", "SECURE FOR LANDING"),
                I("autobrake", "Autobrake", "SET"),
            ],
        },
        new()
        {
            Id = "landing", Name = "Landing", Phase = "Landing",
            Items =
            [
                I("gear-down", "Landing gear", "DOWN, 3 GREEN"),
                I("flaps-land", "Flaps", "LANDING SETTING"),
                I("spoilers-armed", "Speedbrake", "ARMED"),
                I("landing-clearance", "Landing clearance", "RECEIVED"),
                I("go-around-alt", "Go-around altitude", "SET"),
            ],
        },
        new()
        {
            Id = "after-landing", Name = "After Landing", Phase = "Rollout",
            Items =
            [
                I("spoilers-down", "Speedbrake", "DOWN"),
                I("flaps-clean", "Flaps", "RETRACTED"),
                I("apu-al", "APU (as required)", "START"),
                I("transponder-al", "Transponder", "STANDBY"),
                I("landing-lights-al", "Landing / strobe lights", "OFF"),
            ],
        },
        new()
        {
            Id = "shutdown", Name = "Shutdown / Secure", Phase = "Parked",
            Items =
            [
                I("parking-brake-sd", "Parking brake", "SET"),
                I("engines-sd", "Engines", "SHUTDOWN"),
                I("seatbelts-sd", "Seatbelt signs", "OFF"),
                I("fuel-pumps", "Fuel pumps", "OFF"),
                I("beacon-sd", "Beacon", "OFF"),
                I("irs", "IRS / ADIRS", "OFF"),
                I("secure", "Aircraft", "SECURE"),
            ],
        },
    ];
}
