using System.Collections.Concurrent;

namespace AviatesAirTracker.Services.Efb;

// ============================================================
// EFB MANUALS / SOP LIBRARY
//
// A small built-in document library (company SOPs, aircraft guides,
// emergency memory items, ops notes, EFB help). Documents are markdown
// so the tablet renders them nicely. Supports categories, full-text
// search, bookmarks and a recent-documents list.
//
// To add real company manuals later: drop markdown files in a documents
// folder and load them here alongside the built-ins.
// ============================================================

public sealed class EfbDocument
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public required string Summary { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public required string BodyMarkdown { get; init; }
}

public sealed class EfbDocumentSummary
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public bool Bookmarked { get; set; }
}

public sealed class EfbManualsService
{
    private readonly ConcurrentDictionary<string, byte> _bookmarks = new();
    private readonly LinkedList<string> _recent = new();
    private readonly object _recentGate = new();

    public IReadOnlyList<EfbDocument> Documents { get; } = BuildLibrary();

    public IReadOnlyList<string> Categories =>
        Documents.Select(d => d.Category).Distinct().OrderBy(c => c).ToList();

    public IReadOnlyList<EfbDocumentSummary> List(string? category = null, string? query = null)
    {
        IEnumerable<EfbDocument> docs = Documents;

        if (!string.IsNullOrWhiteSpace(category) && !category.Equals("All", StringComparison.OrdinalIgnoreCase))
            docs = docs.Where(d => d.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            docs = docs.Where(d =>
                d.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                d.Summary.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                d.BodyMarkdown.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                d.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }

        return docs.Select(ToSummary).ToList();
    }

    public EfbDocument? Get(string id)
    {
        var doc = Documents.FirstOrDefault(d => d.Id == id);
        if (doc != null) PushRecent(id);
        return doc;
    }

    public IReadOnlyList<EfbDocumentSummary> Recent()
    {
        lock (_recentGate)
            return _recent.Select(id => Documents.First(d => d.Id == id)).Select(ToSummary).ToList();
    }

    public bool ToggleBookmark(string id)
    {
        if (Documents.All(d => d.Id != id)) return false;
        if (_bookmarks.ContainsKey(id)) _bookmarks.TryRemove(id, out _);
        else _bookmarks[id] = 1;
        return true;
    }

    // ── internals ───────────────────────────────────────────

    private EfbDocumentSummary ToSummary(EfbDocument d) => new()
    {
        Id = d.Id,
        Title = d.Title,
        Category = d.Category,
        Summary = d.Summary,
        Tags = d.Tags.ToList(),
        Bookmarked = _bookmarks.ContainsKey(d.Id),
    };

    private void PushRecent(string id)
    {
        lock (_recentGate)
        {
            _recent.Remove(id);
            _recent.AddFirst(id);
            while (_recent.Count > 8) _recent.RemoveLast();
        }
    }

    private static IReadOnlyList<EfbDocument> BuildLibrary() =>
    [
        new()
        {
            Id = "sop-general", Title = "General Operating Procedures", Category = "SOP",
            Summary = "AviatesAir standard flows, callouts and stabilised-approach policy.",
            Tags = ["sop", "callouts", "stable approach"],
            BodyMarkdown =
                "# General Operating Procedures\n\n" +
                "## Stabilised Approach\n" +
                "By **1,000 ft AAL (IMC)** or **500 ft AAL (VMC)** the aircraft must be:\n" +
                "- On the correct lateral & vertical path\n- In the landing configuration\n" +
                "- At target approach speed (Vapp), sink rate ≤ 1,000 fpm\n- Thrust stabilised\n\n" +
                "If not stabilised → **GO-AROUND**. No exceptions.\n\n" +
                "## Standard Callouts\n" +
                "- \"1,000\" / \"500\" radio altitude\n- \"Stabilised\" / \"Go-around, flaps\"\n" +
                "- \"Minimums\": continue or go-around\n\n" +
                "## Sterile Flight Deck\n" +
                "Below **10,000 ft** only operationally relevant communication.",
        },
        new()
        {
            Id = "sop-fuel", Title = "Fuel Policy", Category = "SOP",
            Summary = "Minimum reserves, in-flight fuel checks and MINIMUM FUEL / MAYDAY FUEL calls.",
            Tags = ["fuel", "reserves", "diversion"],
            BodyMarkdown =
                "# Fuel Policy\n\n" +
                "## Planned Fuel\n" +
                "Trip + Contingency (5%) + Alternate + Final Reserve (30 min holding) + Taxi.\n\n" +
                "## In-flight Checks\n" +
                "Compare actual vs plan at every waypoint. Investigate any adverse trend.\n\n" +
                "## Declarations\n" +
                "- **MINIMUM FUEL**: committed to land at a specific aerodrome and any change may result in landing below final reserve.\n" +
                "- **MAYDAY MAYDAY MAYDAY FUEL**: predicted usable fuel on landing < final reserve.",
        },
        new()
        {
            Id = "emer-memory", Title = "Emergency Memory Items", Category = "Emergency",
            Summary = "Immediate-action (recall) items: rejected takeoff, engine fire, cabin depressurisation.",
            Tags = ["emergency", "memory items", "recall", "engine fire"],
            BodyMarkdown =
                "# Emergency Memory Items\n\n" +
                "> Reference only; always confirm with the aircraft QRH.\n\n" +
                "## Rejected Takeoff (before V1)\n" +
                "1. Thrust levers: **CLOSE**\n2. Reverse: **MAX**\n3. Brakes: **AS REQUIRED / AUTO**\n4. Directional control: **MAINTAIN**\n\n" +
                "## Engine Fire / Severe Damage\n" +
                "1. Thrust lever (affected): **IDLE**\n2. Engine master (affected): **OFF**\n3. Fire push-button: **PUSH**\n4. If fire persists: **AGENT 1**, then **AGENT 2**\n\n" +
                "## Cabin Altitude / Rapid Depressurisation\n" +
                "1. Oxygen masks: **ON, 100%**\n2. Crew communication: **ESTABLISH**\n3. Emergency descent: **INITIATE** if cabin alt uncontrollable\n4. Signs: **ON**",
        },
        new()
        {
            Id = "guide-b738", Title = "B737-800 Quick Reference", Category = "Aircraft",
            Summary = "Typical speeds, weights and flap schedule for the AviatesAir 737-800.",
            Tags = ["b738", "737", "boeing", "speeds"],
            BodyMarkdown =
                "# Boeing 737-800 Quick Reference\n\n" +
                "| Item | Value |\n|---|---|\n" +
                "| MTOW | ~174,200 lb |\n| MLW | ~146,300 lb |\n| Typical cruise | M0.78 / FL360–380 |\n" +
                "| Flap schedule | UP → 1 → 5 (T/O) … 15 → 30/40 (LDG) |\n\n" +
                "## Notes\n- Flaps 5 is the standard takeoff setting; Flaps 1 for long/light, 15 for short field.\n" +
                "- Flaps 30 normal landing; Flaps 40 for short/wet runways.",
        },
        new()
        {
            Id = "guide-a20n", Title = "A320neo Quick Reference", Category = "Aircraft",
            Summary = "Typical speeds, weights and config schedule for the AviatesAir A320neo.",
            Tags = ["a20n", "a320", "airbus", "config"],
            BodyMarkdown =
                "# Airbus A320neo Quick Reference\n\n" +
                "| Item | Value |\n|---|---|\n" +
                "| MTOW | ~174,165 lb |\n| MLW | ~145,505 lb |\n| Typical cruise | M0.78 / FL360–390 |\n" +
                "| Config schedule | 1+F → 2 → 3 (T/O) … 3 → FULL (LDG) |\n\n" +
                "## Notes\n- Managed speed/descent is standard; monitor the energy circle.\n" +
                "- CONFIG FULL is the normal landing configuration.",
        },
        new()
        {
            Id = "ops-wx", Title = "Weather & Performance Ops Note", Category = "Operations",
            Summary = "Reading METAR/TAF, contaminated runways and the effect of density altitude.",
            Tags = ["weather", "metar", "taf", "performance", "runway"],
            BodyMarkdown =
                "# Weather & Performance\n\n" +
                "## METAR quick decode\n" +
                "`EGLL 231150Z 24012KT 9999 FEW035 12/06 Q1014` →\n" +
                "wind 240°/12kt, vis >10km, few cloud 3,500ft, temp 12/dew 6, QNH 1014.\n\n" +
                "## Contaminated runways\n" +
                "Standing water/snow/ice degrade braking sharply; always apply the wet/contaminated\n" +
                "landing distance factor and expect longer stopping.\n\n" +
                "## Density Altitude\n" +
                "High elevation + high temperature = higher true speeds and longer field lengths.\n" +
                "The EFB performance page corrects for this automatically.",
        },
        new()
        {
            Id = "efb-help", Title = "Using the AviatesAir EFB", Category = "Operations",
            Summary = "How to pair a device, read the dashboard, and use the performance & checklist tools.",
            Tags = ["efb", "help", "pairing", "getting started"],
            BodyMarkdown =
                "# Using the AviatesAir EFB\n\n" +
                "## Pairing\n" +
                "1. On the desktop app, open **Settings → EFB Companion**.\n" +
                "2. On your tablet/phone (same Wi-Fi), open the shown address or scan the QR.\n" +
                "3. If asked, enter the 6-digit pairing code.\n\n" +
                "## Dashboard\n" +
                "Live callsign, position, altitude, speed, fuel, phase and ETA, updated in real time.\n\n" +
                "## Performance\n" +
                "Pick aircraft, enter weight/runway/wind, and get V-speeds and required runway.\n\n" +
                "## Remote Monitor\n" +
                "Away from the sim? The dashboard keeps updating so you can watch progress from anywhere\n" +
                "on your network, e.g. *\"AVA102 · CRUISE · FL350 · Landing in 1h 12m\"*.",
        },
    ];
}
