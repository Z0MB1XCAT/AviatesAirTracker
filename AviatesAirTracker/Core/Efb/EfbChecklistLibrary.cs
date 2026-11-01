namespace AviatesAirTracker.Core.Efb;

// ============================================================
// EFB CHECKLIST LIBRARY — per-aircraft normal checklists
//
// One checklist SET per AviatesAir fleet family, each set being that manufacturer's
// normal checklist in its own house style:
//   • Airbus (A319/A320neo, A350, A380) — ALL CAPS, "(BOTH)" / "(PF)" call-outs and
//     the black line that separates items done before and after pushback clearance.
//   • Boeing (737NG, 737 MAX, 777F, 747-8) — the QRH "Normal Checklists" wording
//     (sentence case, ___ for values the crew states).
//   • ATR 42/72-600 — the operator-style C/L flow (prop brake / hotel mode, CL FTR…).
//
// Sources (cross-checked, compiled for simulation — NOT a copy of any operator's QRH):
//   Airbus A319/A320 Normal Checklist (Airbus, Nov 2017 sim release); Airbus A350 FCOM
//   normal checklist excerpts; FlyByWire A380X simulation checklist (29 Nov 2021) aligned
//   to the Airbus ECL layout; Boeing 737NG/MAX, 777 and 747-8 QRH Normal Checklists as
//   published in training references (aviationhunt.com et al.); ATR 72-600 operator
//   normal checklist WA-OPS-02-08B (May 2016), with the ATR 42-600 derived from it
//   (common type rating; no tail prop, FLAPS 35 landing).
//
// ⚠ FOR SIMULATION USE ONLY. Real checklists are operator-, variant- and revision-specific.
//
// The library is static reference data; live check state lives in EfbChecklistService.
// Item ids are slugs of the challenge text so they stay stable across edits to wording
// of OTHER items (state is keyed set/checklist/item).
// ============================================================

public sealed class EfbChecklistItemDef
{
    public required string Id { get; init; }
    public required string Challenge { get; init; }
    public required string Response { get; init; }
    /// <summary>Who answers — "BOTH", "PF", "PM", "PF/PM" (Airbus call-outs); null = the PM.</summary>
    public string? Who { get; init; }
    /// <summary>Short amplifying note (e.g. which ECAM memo lines must be green).</summary>
    public string? Note { get; init; }
    /// <summary>Draw the Airbus "line" above this item (items below it wait for pushback/start clearance).</summary>
    public bool LineBefore { get; init; }
}

public sealed class EfbChecklistDef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Human phase label shown under the title ("Preflight", "Taxi", …).</summary>
    public required string Phase { get; init; }
    /// <summary>
    /// Canonical key the dashboard maps flight phases onto: preflight, before-start, after-start,
    /// before-taxi, taxi, before-takeoff, after-takeoff, cruise, descent, approach, landing,
    /// after-landing, shutdown, secure.
    /// </summary>
    public required string PhaseKey { get; init; }
    public required IReadOnlyList<EfbChecklistItemDef> Items { get; init; }
}

public sealed class EfbChecklistSet
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>airbus | boeing | atr | generic — drives the ECL look on the tablet.</summary>
    public required string Style { get; init; }
    /// <summary>ICAO type designators (and common aliases) this set covers.</summary>
    public required IReadOnlyList<string> Types { get; init; }
    public required string Source { get; init; }
    public required IReadOnlyList<EfbChecklistDef> Checklists { get; init; }
}

public static class EfbChecklistLibrary
{
    public const string GenericSetId = "generic";

    // ── builders ────────────────────────────────────────────
    private static EfbChecklistItemDef I(string challenge, string response, string? who = null, string? note = null) =>
        new() { Id = Slug(challenge), Challenge = challenge, Response = response, Who = who, Note = note };

    /// <summary>An item preceded by the Airbus separator line.</summary>
    private static EfbChecklistItemDef L(string challenge, string response, string? who = null, string? note = null) =>
        new() { Id = Slug(challenge), Challenge = challenge, Response = response, Who = who, Note = note, LineBefore = true };

    private static EfbChecklistDef C(string id, string name, string phase, string phaseKey, params EfbChecklistItemDef[] items)
    {
        // de-duplicate slug ids inside one checklist (e.g. two "BARO REF" lines)
        var seen = new Dictionary<string, int>();
        var list = items.Select(it =>
        {
            int n = seen.TryGetValue(it.Id, out var c) ? c + 1 : 1;
            seen[it.Id] = n;
            return n == 1 ? it : new EfbChecklistItemDef
            {
                Id = $"{it.Id}-{n}", Challenge = it.Challenge, Response = it.Response,
                Who = it.Who, Note = it.Note, LineBefore = it.LineBefore,
            };
        }).ToList();
        return new EfbChecklistDef { Id = id, Name = name, Phase = phase, PhaseKey = phaseKey, Items = list };
    }

    internal static string Slug(string s)
    {
        var chars = s.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    // ═══════════════════════════════════════════════════════
    // AIRBUS A319 / A320neo — Airbus normal checklist (paper C/L, 2017 layout)
    // ═══════════════════════════════════════════════════════
    private static readonly EfbChecklistSet A320Family = new()
    {
        Id = "a320", Name = "Airbus A319 / A320neo", Style = "airbus",
        Types = ["A318", "A319", "A320", "A321", "A19N", "A20N", "A21N", "A320NEO", "A319NEO", "A321NEO"],
        Source = "Airbus A319/A320 Normal Checklist (Airbus, Nov 2017 simulation release). ENG MODE SEL / neo items as per the A320neo cockpit.",
        Checklists =
        [
            C("before-start", "BEFORE START", "Preflight", "before-start",
                I("COCKPIT PREP", "COMPLETED", "BOTH"),
                I("GEAR PINS and COVERS", "REMOVED"),
                I("SIGNS", "ON/AUTO"),
                I("ADIRS", "NAV"),
                I("FUEL QUANTITY", "___ KG / BALANCED"),
                I("TO DATA", "SET"),
                I("BARO REF", "___ SET", "BOTH"),
                L("WINDOWS/DOORS", "CLOSED/ARMED", "BOTH"),
                I("BEACON", "ON"),
                I("MOBILE PHONE", "OFF", "BOTH"),
                I("THR LEVERS", "IDLE"),
                I("PARKING BRAKE", "SET")),
            C("after-start", "AFTER START", "Pushback", "after-start",
                I("ANTI ICE", "AS RQRD"),
                I("ECAM STATUS", "CHECKED"),
                I("PITCH TRIM", "___ % SET"),
                I("RUDDER TRIM", "ZERO")),
            C("before-takeoff", "BEFORE TAKEOFF", "Taxi", "before-takeoff",
                I("FLT CTL", "CHECKED", "BOTH"),
                I("FLT INSTRUMENTS", "CHECKED", "BOTH"),
                I("BRIEFING", "CONFIRMED"),
                I("FLAP SETTING", "CONF ___", "BOTH"),
                I("FMA & TO DATA", "READ / CHECKED", "PF/PM", "PF reads the FMA and takeoff data, PM checks"),
                I("TRANSPONDER", "___ SET"),
                I("ECAM MEMO", "TO NO BLUE", note: "AUTO BRK MAX · SIGNS ON · SPLRS ARM · FLAPS TO · TO CONFIG NORM"),
                L("CABIN", "SECURED"),
                I("ENG MODE SEL", "AS RQRD"),
                I("TCAS", "TA OR TA/RA"),
                I("PACKS", "AS RQRD")),
            C("after-takeoff", "AFTER TAKEOFF / CLIMB", "Climb", "after-takeoff",
                I("LDG GEAR", "UP"),
                I("FLAPS", "RETRACTED"),
                I("PACKS", "ON"),
                L("BARO REF", "STD SET", "BOTH", "At the transition altitude")),
            C("approach", "APPROACH", "Descent", "approach",
                I("BRIEFING", "CONFIRMED"),
                I("ECAM STATUS", "CHECKED"),
                I("SEAT BELTS", "ON"),
                I("BARO REF", "___ SET", "BOTH"),
                I("MINIMUM", "___ SET", "BOTH"),
                I("ENG MODE SEL", "AS RQRD")),
            C("landing", "LANDING", "Approach", "landing",
                I("CABIN", "SECURED"),
                I("A/THR", "SPEED / OFF"),
                I("GO-AROUND ALT", "___ SET"),
                I("ECAM MEMO", "LDG NO BLUE", note: "LDG GEAR DN · SIGNS ON · SPLRS ARM · FLAPS SET")),
            C("parking", "PARKING", "Parked", "shutdown",
                I("APU BLEED", "ON"),
                I("Y ELEC PUMP", "OFF"),
                I("ENGINES", "OFF"),
                I("SEAT BELTS", "OFF"),
                I("EXT LT", "AS RQRD"),
                I("FUEL PUMPS", "OFF"),
                I("PARK BRK and CHOCKS", "AS RQRD"),
                I("MOBILE PHONE", "ON"),
                I("TRANSPONDER", "STBY 2000"),
                I("RADAR / PWS", "OFF")),
            C("securing", "SECURING THE AIRCRAFT", "Parked", "secure",
                I("ADIRS", "OFF"),
                I("OXYGEN", "OFF"),
                I("APU BLEED", "OFF"),
                I("EMER EXIT LT", "OFF"),
                I("NO SMOKING", "OFF"),
                I("APU and BAT", "OFF")),
        ],
    };

    // ═══════════════════════════════════════════════════════
    // AIRBUS A350 — ECL normal checklist (auto pitch trim, no ENG MODE SEL)
    // ═══════════════════════════════════════════════════════
    private static readonly EfbChecklistSet A350 = new()
    {
        Id = "a350", Name = "Airbus A350", Style = "airbus",
        Types = ["A359", "A35K", "A350", "A350900", "A3501000"],
        Source = "Airbus A350 FCOM normal checklist (ECL) excerpts, aligned to the A320 C/L layout for items common to the family.",
        Checklists =
        [
            C("before-start", "BEFORE START", "Preflight", "before-start",
                I("COCKPIT PREP", "COMPLETED", "BOTH"),
                I("GEAR PINS & COVERS", "REMOVED"),
                I("SIGNS", "ON/AUTO"),
                I("ADIRS", "NAV"),
                I("FUEL QUANTITY", "___ KG"),
                I("T.O DATA", "SET"),
                I("BARO REF", "___ SET", "BOTH"),
                L("WINDOWS / DOORS", "CLOSED", "BOTH"),
                I("BEACON", "ON"),
                I("MOBILE PHONE", "OFF", "BOTH"),
                I("THR LEVERS", "IDLE"),
                I("PARKING BRAKE", "SET")),
            C("after-start", "AFTER START", "Pushback", "after-start",
                I("ANTI ICE", "AS RQRD"),
                I("ECAM STATUS", "CHECKED", note: "Pitch trim is set automatically on the A350")),
            C("before-takeoff", "BEFORE TAKEOFF", "Taxi", "before-takeoff",
                I("FLT CTL", "CHECKED", "BOTH"),
                I("FLT INSTRUMENTS", "CHECKED", "BOTH"),
                I("BRIEFING", "CONFIRMED"),
                I("FLAPS SETTING", "CONF ___", "BOTH"),
                I("FMA & T.O DATA", "READ / CHECKED", "PF/PM"),
                I("TRANSPONDER", "___ SET"),
                I("ECAM MEMO", "T.O NO BLUE", "BOTH", "AUTO BRK RTO · SIGNS ON · SPLRS ARM · FLAPS T.O · T.O CONFIG NORM"),
                L("CABIN", "SECURED"),
                I("TCAS", "TA OR TA/RA"),
                I("PACKS", "AS RQRD")),
            C("after-takeoff", "AFTER TAKEOFF / CLIMB", "Climb", "after-takeoff",
                I("LDG GEAR", "UP"),
                I("FLAPS", "RETRACTED"),
                I("PACKS", "ON"),
                L("BARO REF", "STD SET", "BOTH", "At the transition altitude")),
            C("approach", "APPROACH", "Descent", "approach",
                I("BRIEFING", "CONFIRMED"),
                I("ECAM STATUS", "CHECKED"),
                I("SEAT BELTS", "ON"),
                I("BARO REF", "___ SET", "BOTH"),
                I("MINIMUM", "___ SET", "BOTH"),
                I("AUTO BRK / BTV", "AS RQRD")),
            C("landing", "LANDING", "Approach", "landing",
                I("CABIN", "SECURED"),
                I("A/THR", "SPEED / OFF"),
                I("ECAM MEMO", "LDG NO BLUE", note: "LDG GEAR DN · SIGNS ON · SPLRS ARM · FLAPS SET")),
            C("parking", "PARKING", "Parked", "shutdown",
                I("APU BLEED", "ON"),
                I("ELEC PUMPS", "OFF"),
                I("ENGINES", "OFF"),
                I("SEAT BELTS", "OFF"),
                I("EXT LT", "AS RQRD"),
                I("FUEL PUMPS", "OFF"),
                I("PARK BRK and CHOCKS", "AS RQRD")),
            C("securing", "SECURING THE AIRCRAFT", "Parked", "secure",
                I("ADIRS", "OFF"),
                I("OXYGEN", "OFF"),
                I("APU BLEED", "OFF"),
                I("EMER EXIT LT", "OFF"),
                I("SIGNS", "OFF"),
                I("APU and BAT", "OFF")),
        ],
    };

    // ═══════════════════════════════════════════════════════
    // AIRBUS A380 — ECL normal checklist (four engines, BTV, RTO autobrake)
    // ═══════════════════════════════════════════════════════
    private static readonly EfbChecklistSet A380 = new()
    {
        Id = "a380", Name = "Airbus A380", Style = "airbus",
        Types = ["A388", "A380", "A380800"],
        Source = "FlyByWire A380X simulation checklist (29 Nov 2021) arranged in the Airbus ECL normal-checklist layout.",
        Checklists =
        [
            C("before-start", "BEFORE START", "Preflight", "before-start",
                I("COCKPIT PREP", "COMPLETED", "BOTH"),
                I("GEAR PINS & COVERS", "REMOVED"),
                I("OXYGEN", "CHECKED"),
                I("SIGNS", "ON/AUTO"),
                I("ADIRS", "NAV"),
                I("FUEL QUANTITY", "___ KG"),
                I("T.O DATA", "SET"),
                I("BARO REF", "___ SET", "BOTH"),
                L("WINDOWS / DOORS", "CLOSED", "BOTH"),
                I("BEACON", "ON"),
                I("THR LEVERS", "IDLE"),
                I("PARKING BRAKE", "SET")),
            C("after-start", "AFTER START", "Pushback", "after-start",
                I("ANTI ICE", "AS RQRD"),
                I("ECAM STATUS", "CHECKED"),
                I("PITCH TRIM", "___ CHECKED"),
                I("RUDDER TRIM", "NEUTRAL"),
                I("GROUND EQUIPMENT", "CLEAR")),
            C("before-takeoff", "BEFORE TAKEOFF", "Taxi", "before-takeoff",
                I("FLT CTL", "CHECKED", "BOTH"),
                I("FLT INSTRUMENTS", "CHECKED", "BOTH"),
                I("BRIEFING", "CONFIRMED"),
                I("FLAPS SETTING", "CONF ___", "BOTH"),
                I("FMA & T.O DATA", "READ / CHECKED", "PF/PM", "V1 · VR · V2 · FLEX TEMP"),
                I("AUTO BRK", "RTO"),
                I("TRANSPONDER", "___ SET"),
                I("ECAM MEMO", "T.O NO BLUE", "BOTH", "SIGNS ON · SPLRS ARM · FLAPS T.O · T.O CONFIG NORM"),
                L("CABIN", "SECURED"),
                I("TCAS", "TA OR TA/RA"),
                I("PACKS", "AS RQRD")),
            C("after-takeoff", "AFTER TAKEOFF / CLIMB", "Climb", "after-takeoff",
                I("LDG GEAR", "UP"),
                I("FLAPS", "RETRACTED"),
                I("PACKS", "ON"),
                I("APU MASTER", "OFF"),
                L("BARO REF", "STD SET", "BOTH", "At the transition altitude")),
            C("approach", "APPROACH", "Descent", "approach",
                I("BRIEFING", "CONFIRMED"),
                I("ECAM STATUS", "CHECKED"),
                I("SEAT BELTS", "ON"),
                I("BARO REF", "___ SET", "BOTH"),
                I("MINIMUM", "___ SET", "BOTH"),
                I("AUTO BRK / BTV", "AS RQRD")),
            C("landing", "LANDING", "Approach", "landing",
                I("CABIN", "SECURED"),
                I("A/THR", "SPEED / OFF"),
                I("ECAM MEMO", "LDG NO BLUE", note: "LDG GEAR DN · SIGNS ON · SPLRS ARM · FLAPS SET")),
            C("parking", "PARKING", "Parked", "shutdown",
                I("APU BLEED", "ON"),
                I("ENGINES (ALL 4)", "OFF"),
                I("SEAT BELTS", "OFF"),
                I("EXT LT", "AS RQRD"),
                I("FUEL PUMPS", "OFF"),
                I("PARK BRK and CHOCKS", "AS RQRD")),
            C("securing", "SECURING THE AIRCRAFT", "Parked", "secure",
                I("ADIRS", "OFF"),
                I("OXYGEN", "OFF"),
                I("APU BLEED", "OFF"),
                I("EMER EXIT LT", "OFF"),
                I("SIGNS", "OFF"),
                I("APU and BATTERIES", "OFF")),
        ],
    };

    // ═══════════════════════════════════════════════════════
    // BOEING 737NG / 737 MAX — QRH Normal Checklists
    // ═══════════════════════════════════════════════════════
    private static IReadOnlyList<EfbChecklistDef> Boeing737(bool max) =>
    [
        C("preflight", "PREFLIGHT", "Preflight", "preflight",
            I("Oxygen", "Tested, 100%"),
            I("Navigation transfer and display switches", "NORMAL, AUTO"),
            I("Window heat", "ON"),
            I("Pressurization mode selector", "AUTO"),
            I("Flight instruments", "Heading ___, Altimeter ___"),
            I("Parking brake", "Set"),
            I("Engine start levers", "CUTOFF")),
        C("before-start", "BEFORE START", "Preflight", "before-start",
            I("Flight deck door", "Closed and locked"),
            I("Fuel", "___ KGS, Pumps ON"),
            I("Passenger signs", "___"),
            I("Windows", "Locked"),
            I("MCP", "V2 ___, HDG ___, ALT ___"),
            I("Takeoff speeds", "V1 ___, VR ___, V2 ___"),
            I("CDU preflight", "Completed"),
            I("Rudder and aileron trim", "Free and 0"),
            I("Taxi and takeoff briefing", "Completed"),
            I("Anti collision light", "ON")),
        C("before-taxi", "BEFORE TAXI", "Pushback", "before-taxi",
            I("Generators", "ON"),
            I("Probe heat", max ? "AUTO" : "ON"),
            I("Anti-ice", "___"),
            I("Isolation valve", "AUTO"),
            I("Engine start switches", "CONT"),
            I("Recall", "Checked"),
            I("Autobrake", "RTO"),
            I("Engine start levers", "IDLE detent"),
            I("Flight controls", "Checked"),
            I("Ground equipment", "Clear")),
        C("before-takeoff", "BEFORE TAKEOFF", "Taxi", "before-takeoff",
            I("Flaps", "___, Green light"),
            I("Stabilizer trim", "___ Units")),
        C("after-takeoff", "AFTER TAKEOFF", "Climb", "after-takeoff",
            I("Engine bleeds", "ON"),
            I("Packs", "AUTO"),
            I("Landing gear", "UP and OFF"),
            I("Flaps", "UP, No lights")),
        C("descent", "DESCENT", "Descent", "descent",
            I("Pressurization", "LAND ALT ___"),
            I("Recall", "Checked"),
            I("Autobrake", "___"),
            I("Landing data", "VREF ___, Minimums ___"),
            I("Approach briefing", "Completed")),
        C("approach", "APPROACH", "Approach", "approach",
            I("Altimeters", "___")),
        C("landing", "LANDING", "Approach", "landing",
            I("Engine start switches", "CONT"),
            I("Speedbrake", "ARMED"),
            I("Landing gear", "Down"),
            I("Flaps", "___, Green light")),
        C("shutdown", "SHUTDOWN", "Parked", "shutdown",
            I("Fuel pumps", "OFF"),
            I("Probe heat", max ? "AUTO" : "OFF"),
            I("Hydraulic panel", "Set"),
            I("Flaps", "UP"),
            I("Parking brake", "___"),
            I("Engine start levers", "CUTOFF"),
            I("Weather radar", "OFF")),
        C("secure", "SECURE", "Parked", "secure",
            I("IRSs", "OFF"),
            I("Emergency exit lights", "OFF"),
            I("Window heat", "OFF"),
            I("Packs", "OFF")),
    ];

    private static readonly EfbChecklistSet B737NG = new()
    {
        Id = "b737ng", Name = "Boeing 737-600/-800/-900ER", Style = "boeing",
        Types = ["B736", "B737", "B738", "B739", "B739ER", "B737NG"],
        Source = "Boeing 737NG QRH Normal Checklists (as published in training references, e.g. aviationhunt.com).",
        Checklists = Boeing737(max: false),
    };

    private static readonly EfbChecklistSet B737Max = new()
    {
        Id = "b737max", Name = "Boeing 737 MAX 8", Style = "boeing",
        Types = ["B38M", "B37M", "B39M", "B3XM", "B737MAX", "B737MAX8"],
        Source = "Boeing 737NG QRH Normal Checklists with the 737 MAX differences (probe heat AUTO).",
        Checklists = Boeing737(max: true),
    };

    // ═══════════════════════════════════════════════════════
    // BOEING 777 / 777F and 747-8 — QRH Normal Checklists (ECL on the aircraft)
    // ═══════════════════════════════════════════════════════
    private static IReadOnlyList<EfbChecklistDef> BoeingWidebody(bool b747) =>
    [
        C("preflight", "PREFLIGHT", "Preflight", "preflight",
            I("Oxygen", "Tested, 100%"),
            I("Flight instruments", "Heading ___, Altimeter ___"),
            I("Parking brake", "Set"),
            I("Fuel control switches", "CUTOFF")),
        C("before-start", "BEFORE START", "Preflight", "before-start",
            I("Flight deck door", "Closed and locked"),
            I("Passenger signs", "___"),
            I("MCP", "V2 ___, HDG/TRK ___, ALT ___"),
            I("Takeoff speeds", "V1 ___, VR ___, V2 ___"),
            I("CDU preflight", "Completed"),
            I("Trim", "___ Units, 0, 0"),
            I("Taxi and takeoff briefing", "Completed"),
            I("Beacon", b747 ? "BOTH" : "ON")),
        C("before-taxi", "BEFORE TAXI", "Pushback", "before-taxi",
            I("Anti-ice", "___"),
            I("Recall", "Checked"),
            I("Autobrake", "RTO"),
            I("Flight controls", "Checked"),
            I("Ground equipment", "Clear")),
        C("before-takeoff", "BEFORE TAKEOFF", "Taxi", "before-takeoff",
            I("Flaps", "___")),
        C("after-takeoff", "AFTER TAKEOFF", "Climb", "after-takeoff",
            I("Landing gear", "UP"),
            I("Flaps", "UP")),
        C("descent", "DESCENT", "Descent", "descent",
            I("Recall", "Checked"),
            I("Notes", "Checked"),
            I("Autobrake", "___"),
            I("Landing data", "VREF ___, Minimums ___"),
            I("Approach briefing", "Completed")),
        C("approach", "APPROACH", "Approach", "approach",
            I("Altimeters", "___")),
        C("landing", "LANDING", "Approach", "landing",
            I("Speedbrake", "ARMED"),
            I("Landing gear", "Down"),
            I("Flaps", "___")),
        C("shutdown", "SHUTDOWN", "Parked", "shutdown",
            b747 ? I("Hydraulic demand pumps", "OFF") : I("Hydraulic panel", "Set"),
            I("Fuel pumps", "Off"),
            I("Flaps", "Up"),
            I("Parking brake", "___"),
            I("Fuel control switches", "CUTOFF"),
            I("Weather radar", "Off")),
        C("secure", "SECURE", "Parked", "secure",
            b747 ? I("IRSs", "OFF") : I("ADIRU", "Off"),
            I("Emergency lights", "OFF"),
            I("Packs", "Off")),
    ];

    private static readonly EfbChecklistSet B777 = new()
    {
        Id = "b777", Name = "Boeing 777F", Style = "boeing",
        Types = ["B77F", "B77L", "B77W", "B772", "B773", "B777", "B777F", "B778", "B779"],
        Source = "Boeing 777 QRH Normal Checklists (common to the 777-200LR/-300ER/777F electronic checklist).",
        Checklists = BoeingWidebody(b747: false),
    };

    private static readonly EfbChecklistSet B748 = new()
    {
        Id = "b748", Name = "Boeing 747-8", Style = "boeing",
        Types = ["B748", "B74F", "B744", "B747", "B7478", "B748F"],
        Source = "Boeing 747-8 QRH Normal Checklists (747-400 lineage: beacon BOTH, hydraulic demand pumps, IRSs).",
        Checklists = BoeingWidebody(b747: true),
    };

    // ═══════════════════════════════════════════════════════
    // ATR 72-600 / ATR 42-600 — operator normal checklist (hotel mode start on ENG 2)
    // ═══════════════════════════════════════════════════════
    private static IReadOnlyList<EfbChecklistDef> Atr(bool atr72) =>
    [
        C("final-cockpit-prep", "FINAL COCKPIT PREPARATION", "Preflight", "preflight",
            I("PARKING BRAKE", "ON / PRESS CHECKED"),
            I("ALTIMETERS", "SET"),
            I("LANDING ELEVATION", "SET"),
            I("FMS / COM / NAV", "SET / CHECKED"),
            I("FUEL QUANTITY / FOB", "CHECKED"),
            I("ENG FUEL USED", "RESET"),
            I("MEMO PANEL", "CHECKED"),
            I("PWR MGT", "TO"),
            I("PROP BRAKE", "AS RQD")),
        C("before-prop-rotation", "BEFORE PROPELLER ROTATION", "Preflight", "before-start",
            atr72 ? I("TAIL PROP", "ON BOARD") : I("CARGO DOORS", "CLOSED"),
            I("DOORS", "CLOSED"),
            I("FMS & T/O DATA", "CONFIRMED / SET"),
            I("TRIMS", "SET"),
            I("CDLS", "CHECKED"),
            I("SEAT BELTS", "ON"),
            I("BEACON", "ON")),
        C("before-taxi", "BEFORE TAXI", "Pushback", "before-taxi",
            I("FWS", "RCL"),
            I("PROP BRAKE", "OFF"),
            I("COCKPIT COM HATCH", "CLOSED"),
            I("NOSE WHEEL STEERING", "ON"),
            I("CL 1 + 2", "AUTO"),
            I("ANTI-ICING", "AS RQD"),
            I("ANTI-SKID", "TESTED"),
            I("FLAPS", "15 SET")),
        C("taxi", "TAXI", "Taxi", "taxi",
            I("TAXI & T/O LIGHTS", "ON"),
            I("BRAKES", "CHECKED"),
            I("FGCP / FMA", "SET / CHECKED"),
            I("TO CONFIG", "TESTED"),
            I("TAKEOFF BRIEFING", "COMPLETE")),
        C("before-takeoff", "BEFORE TAKEOFF", "Taxi", "before-takeoff",
            I("APM", "SET"),
            I("BLEED VALVES", "AS RQD", note: "OFF for bleeds-off takeoff performance"),
            I("AIR FLOW", "NORM"),
            I("GUST LOCK", "RELEASED"),
            I("FLIGHT CONTROLS", "CHECKED"),
            I("XPDR / TCAS", "TA/RA"),
            I("EXT LIGHTS", "ON"),
            I("FD BAR", "CENTRED", note: "When lined up"),
            I("RUDDER CAM", "CENTRED"),
            I("FLIGHT ATTENDANTS", "NOTIFIED")),
        C("after-takeoff", "AFTER TAKEOFF", "Climb", "after-takeoff",
            I("LDG GEAR", "UP"),
            I("FLAPS", "0°"),
            I("PWR MGT / NP", "CLB / CHECKED"),
            I("BLEED VALVES", "ON"),
            I("TAXI & T/O LIGHTS", "OFF"),
            I("ALTIMETERS", "SET & CHECKED"),
            I("SEAT BELTS", "AS RQD")),
        C("cruise", "CRUISE", "Cruise", "cruise",
            I("CRUISE SPEED", "SET"),
            I("PWR MGT", "CRZ"),
            I("STBY ALTIMETER", "SET"),
            I("PRESSURIZATION", "CHECKED"),
            I("TCAS", "ABOVE / BELOW")),
        C("descent", "DESCENT", "Descent", "descent",
            I("FWS", "RCL"),
            I("LDG ELEVATION", "SET / CHECKED"),
            I("FMS (NAV / PERF)", "SET / CHECKED"),
            I("DH / MDA", "SET"),
            I("ARRIVAL BRIEFING", "COMPLETE")),
        C("approach", "APPROACH", "Approach", "approach",
            I("SEAT BELTS", "ON"),
            I("LANDING LIGHTS", "ON"),
            I("ALTIMETERS", "QNH SET & CHECKED"),
            I("CABIN ALTITUDE", "CHECKED"),
            I("CROSSFEED", "CHECKED")),
        C("before-landing", "BEFORE LANDING", "Approach", "landing",
            I("LDG GEAR", "DOWN, 3 GREEN"),
            I("FLAPS", atr72 ? "30 SET" : "35 SET", note: atr72 ? "FLAPS 30 normal landing" : "FLAPS 35 normal landing (FLAPS 25 as required)"),
            I("PWR MGT", "TO"),
            I("TLU", "LO SPD"),
            I("ICING AOA LIGHT", "CHECKED"),
            I("EXT LIGHTS", "CHECKED"),
            I("FLIGHT ATTENDANTS", "ADVISED")),
        C("after-landing", "AFTER LANDING", "Rollout", "after-landing",
            I("XPDR / TCAS / RADAR", "STBY"),
            I("FLIGHT CONTROLS", "LOCKED"),
            I("FLAPS", "0°"),
            I("TRIMS", "SET"),
            I("LANDING LIGHTS / STROBES", "OFF"),
            I("ANTI / DE-ICING", "OFF"),
            I("ENG START ROTARY", "OFF"),
            I("CL 1", "FTR THEN FUEL SO", note: "After at least one minute at idle; taxi in on ENG 2")),
        C("parking", "PARKING", "Parked", "shutdown",
            I("PARKING BRAKE", "ON"),
            I("TAXI & T/O LIGHTS", "OFF"),
            I("CL 2", "FTR"),
            I("PROP BRAKE", "AS RQD", note: "ON for hotel mode"),
            I("BEACON", "OFF"),
            atr72 ? I("TAIL PROP", "IN POSITION") : I("CHOCKS", "IN PLACE"),
            I("SEAT BELTS", "OFF"),
            I("FWD CARGO DOOR", "OPEN")),
        C("leaving", "LEAVING THE AIRCRAFT", "Parked", "secure",
            I("OXYGEN MAIN SUPPLY", "OFF"),
            I("ICE & RAIN PROTECTION", "OFF"),
            I("EXT LIGHTS", "OFF"),
            I("EMER LIGHTS", "DISARMED"),
            I("RADAR", "OFF"),
            I("CL 2", "FUEL SO"),
            I("FUEL PUMPS 1 + 2", "OFF"),
            I("CDLS", "OFF"),
            I("BAT", "OFF")),
    ];

    private static readonly EfbChecklistSet Atr72 = new()
    {
        Id = "atr72", Name = "ATR 72-600", Style = "atr",
        Types = ["AT76", "AT75", "AT72", "AT73", "ATR72", "ATR72600"],
        Source = "ATR 72-600 operator normal checklist WA-OPS-02-08B (May 2016), reordered into ATR FCOM phase order.",
        Checklists = Atr(atr72: true),
    };

    private static readonly EfbChecklistSet Atr42 = new()
    {
        Id = "atr42", Name = "ATR 42-600", Style = "atr",
        Types = ["AT46", "AT45", "AT42", "AT43", "AT44", "ATR42", "ATR42600"],
        Source = "Derived from the ATR 72-600 operator normal checklist (common ATR 42/72 type rating): no tail prop, FLAPS 35 landing.",
        Checklists = Atr(atr72: false),
    };

    // ═══════════════════════════════════════════════════════
    // GENERIC — the original AviatesAir airline checklists, for types outside the fleet
    // ═══════════════════════════════════════════════════════
    private static readonly EfbChecklistSet Generic = new()
    {
        Id = GenericSetId, Name = "AviatesAir Generic", Style = "generic",
        Types = [],
        Source = "AviatesAir generic airline checklist, used when the aircraft type isn't in the fleet library.",
        Checklists =
        [
            C("before-start", "Before Start", "Preflight", "before-start",
                I("Preflight inspection", "COMPLETE"),
                I("Gear pins & covers", "REMOVED"),
                I("Seatbelt / No-smoking signs", "ON"),
                I("Fuel quantity", "___ CHECKED"),
                I("FMS / MCDU init", "COMPLETE"),
                I("Altimeters", "QNH SET"),
                I("Beacon", "ON"),
                I("Parking brake", "SET")),
            C("before-taxi", "Before Taxi", "Taxi", "before-taxi",
                I("Engines", "STARTED / STABLE"),
                I("Generators", "ON"),
                I("Flaps", "SET FOR T/O"),
                I("Flight controls", "CHECKED"),
                I("APU (as required)", "OFF/ON"),
                I("Ground equipment", "CLEAR"),
                I("Taxi clearance", "RECEIVED")),
            C("before-takeoff", "Before Takeoff", "Takeoff", "before-takeoff",
                I("Takeoff briefing", "COMPLETE"),
                I("V-speeds (V1/VR/V2)", "SET & CROSS-CHECKED"),
                I("Flaps", "CONFIRM T/O SETTING"),
                I("Cabin", "SECURE"),
                I("TCAS", "TA/RA"),
                I("Strobe lights", "ON"),
                I("Transponder", "ON / ALT"),
                I("Line-up / T/O clearance", "RECEIVED")),
            C("after-takeoff", "After Takeoff / Climb", "Climb", "after-takeoff",
                I("Landing gear", "UP"),
                I("Flaps", "RETRACTED ON SCHEDULE"),
                I("Climb thrust", "SET"),
                I("Packs / bleeds", "ON"),
                I("Landing lights (>10,000)", "OFF"),
                I("Altimeters (TA)", "STD SET")),
            C("cruise", "Cruise", "Cruise", "cruise",
                I("Cruise altitude", "REACHED / SET"),
                I("Fuel vs plan", "CHECKED"),
                I("Systems", "NORMAL"),
                I("Weather ahead", "REVIEWED")),
            C("descent", "Descent", "Descent", "descent",
                I("Arrival ATIS", "RECEIVED"),
                I("Approach & landing briefing", "COMPLETE"),
                I("Altimeters", "QNH SET AT TA"),
                I("Landing fuel", "CHECKED"),
                I("Minimums / MDA-DA", "SET")),
            C("approach", "Approach", "Approach", "approach",
                I("Speed", "MANAGED / SET"),
                I("Nav aids / course", "TUNED & IDENT"),
                I("Flaps", "APPROACH SETTING"),
                I("Cabin", "SECURE FOR LANDING"),
                I("Autobrake", "SET")),
            C("landing", "Landing", "Landing", "landing",
                I("Landing gear", "DOWN, 3 GREEN"),
                I("Flaps", "LANDING SETTING"),
                I("Speedbrake", "ARMED"),
                I("Landing clearance", "RECEIVED"),
                I("Go-around altitude", "SET")),
            C("after-landing", "After Landing", "Rollout", "after-landing",
                I("Speedbrake", "DOWN"),
                I("Flaps", "RETRACTED"),
                I("APU (as required)", "START"),
                I("Transponder", "STANDBY"),
                I("Landing / strobe lights", "OFF")),
            C("shutdown", "Shutdown / Secure", "Parked", "shutdown",
                I("Parking brake", "SET"),
                I("Engines", "SHUTDOWN"),
                I("Seatbelt signs", "OFF"),
                I("Fuel pumps", "OFF"),
                I("Beacon", "OFF"),
                I("IRS / ADIRS", "OFF"),
                I("Aircraft", "SECURE")),
        ],
    };

    /// <summary>Every checklist set, fleet order first, the generic fallback last.</summary>
    public static readonly IReadOnlyList<EfbChecklistSet> Sets =
        [A320Family, A350, A380, B737NG, B737Max, B777, B748, Atr72, Atr42, Generic];

    public static EfbChecklistSet? FindSet(string? id) =>
        Sets.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Pick the checklist set for an aircraft type as it arrives from a booking / SimBrief / the
    /// sim title — ICAO codes ("A20N", "B38M", "AT76"), fleet names ("A320NEO", "B739ER", "ATR 72")
    /// or free text ("Boeing 737-800"). Unknown or empty types get the generic set.
    /// </summary>
    public static EfbChecklistSet Resolve(string? aircraftType)
    {
        var t = new string((aircraftType ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (t.Length == 0) return Generic;

        var exact = Sets.FirstOrDefault(s => s.Types.Contains(t));
        if (exact != null) return exact;

        // Free-text fallbacks, most specific first (MAX before NG, A380 before A38x noise).
        if (Has(t, "A380", "A388")) return A380;
        if (Has(t, "A350", "A359", "A35K")) return A350;
        if (Has(t, "A318", "A319", "A320", "A321", "A19N", "A20N", "A21N")) return A320Family;
        if (Has(t, "737MAX", "B38M", "B37M", "B39M", "MAX8", "MAX9")) return B737Max;
        if (Has(t, "B73", "737")) return B737NG;
        if (Has(t, "B77", "777")) return B777;
        if (Has(t, "B74", "747")) return B748;
        if (Has(t, "ATR72", "AT7", "ATR7")) return Atr72;
        if (Has(t, "ATR42", "AT4", "ATR4")) return Atr42;
        return Generic;
    }

    private static bool Has(string t, params string[] keys) => keys.Any(t.Contains);
}
