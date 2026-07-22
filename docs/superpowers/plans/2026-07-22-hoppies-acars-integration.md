# Hoppies ACARS Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Send real Hoppies ACARS telex messages (loadsheet, pushback reminder, airborne/landing calls, post-flight summary, weather/hazard advisories) to a pilot's ACARS-capable addon at the right points in a flight, and fix the CityHopper/Cargo callsign-prefix classification along the way.

**Architecture:** Six new singleton services (`HoppiesAcarsClient`, `HazardZoneService`, `WeatherAdvisoryService`, `HoppiesMessageComposer` (static), `HoppiesAcarsOrchestrator`) wired into the existing DI container, subscribing to events already raised by `SimBriefService`, `TakeoffPerformanceService`, `FlightPhaseDetector`, `LandingAnalyzer`, and `FlightSessionManager`. New Settings fields + UI section. New in-app log card on `Acars.razor`.

**Tech Stack:** .NET 8, RestSharp (HTTP), System.Text.Json (settings persistence), Serilog (logging) — all already in use in this codebase, no new package references.

**Verification note:** this project has no automated test project (confirmed: only one `.csproj` exists, no xUnit/NUnit/MSTest anywhere, matching CLAUDE.md's "There are no automated tests"). Per CLAUDE.md, this plan does not introduce a testing framework. Every task's verification step is `dotnet build` (must succeed with 0 new errors) plus a specific manual check, consistent with how this codebase has always been verified.

**Scope note:** the original ask included sending real-time-calculated V1/takeoff speeds with Boeing derate (D-TO1/D-TO2), Airbus FLEX temp, flap-selection heuristics, and THS trim. That grew into its own performance-engineering subsystem during design and was explicitly split off into a separate future project (per your decision during planning). **This plan does not send a takeoff-speeds telex at all** — that will be wired in once the standalone Takeoff Performance Calculator exists. Loadsheet, timing reminders, airborne/landing calls, post-flight summary, and weather/hazard advisories are all in scope.

---

## File Structure

**New files:**
- `AviatesAirTracker/Models/HoppiesModels.cs` — `HazardZoneEntry`, `HoppiesFlightState`, `HoppiesLogEntry`
- `AviatesAirTracker/Core/Analytics/WeightBalanceDatabase.cs` — MAC% calculation engine + seeded lookup table
- `AviatesAirTracker/Core/Backend/HoppiesAcarsClient.cs` — Hoppies telex HTTP client
- `AviatesAirTracker/Services/HazardZoneService.cs` — static hazard-zone JSON loader
- `AviatesAirTracker/Services/WeatherAdvisoryService.cs` — METAR-derived wind/visibility advisories
- `AviatesAirTracker/Services/HoppiesMessageComposer.cs` — pure message-text formatting
- `AviatesAirTracker/Services/HoppiesAcarsOrchestrator.cs` — scheduling/trigger brain

**Modified files:**
- `AviatesAirTracker/Services/SimBriefService.cs` — parse `weights.est_zfw`/`pax_count`/`cargo`
- `AviatesAirTracker/Models/FlightModels.cs` — add 3 fields to `SimBriefFlightPlan`
- `AviatesAirTracker/Services/SupportServices.cs` — add 7 fields to `AppSettings`
- `AviatesAirTracker/Pages/Settings.razor` — new "Hoppies ACARS" section
- `AviatesAirTracker/Services/RoutesService.cs` — fix `Subsidiary` fleet lists + normalized matching + `StripIcaoPrefix` helper
- `AviatesAirTracker/App.xaml.cs` — register 4 new singletons
- `AviatesAirTracker/Pages/Acars.razor` — new in-app Hoppies message log card

---

### Task 1: Extend `SimBriefFlightPlan` with ZFW/PAX/cargo fields

**Files:**
- Modify: `AviatesAirTracker/Models/FlightModels.cs:265-269`
- Modify: `AviatesAirTracker/Services/SimBriefService.cs:181-188`

- [ ] **Step 1: Add the new fields to `SimBriefFlightPlan`**

In `AviatesAirTracker/Models/FlightModels.cs`, find:
```csharp
    // Takeoff performance inputs — extracted from OFP weights/origin
    public double TakeoffWeightKg { get; set; }
    public int    DepartureRunwayLengthM { get; set; }
    public int    DepartureElevationFt { get; set; }

    // OFP content
```
Replace with:
```csharp
    // Takeoff performance inputs — extracted from OFP weights/origin
    public double TakeoffWeightKg { get; set; }
    public int    DepartureRunwayLengthM { get; set; }
    public int    DepartureElevationFt { get; set; }

    // Weights — extracted from OFP weights section, used by the Hoppies loadsheet
    public double ZeroFuelWeightKg { get; set; }
    public int    PaxCount { get; set; }
    public double CargoKg { get; set; }

    // OFP content
```

- [ ] **Step 2: Parse the new fields in `ParseSimBriefJson`**

In `AviatesAirTracker/Services/SimBriefService.cs`, find:
```csharp
            // Takeoff weight — same kg/lbs unit as fuel
            double ToKg(double v) => fuelInKg ? v : v / 2.20462;
            if (double.TryParse(root["weights"]?["est_tow"]?.ToString(), out double tow))
                plan.TakeoffWeightKg = ToKg(tow);

            // Departure elevation (ft)
```
Replace with:
```csharp
            // Takeoff weight — same kg/lbs unit as fuel
            double ToKg(double v) => fuelInKg ? v : v / 2.20462;
            if (double.TryParse(root["weights"]?["est_tow"]?.ToString(), out double tow))
                plan.TakeoffWeightKg = ToKg(tow);

            // Zero-fuel weight, passenger count, cargo weight — used by the Hoppies loadsheet
            if (double.TryParse(root["weights"]?["est_zfw"]?.ToString(), out double zfw))
                plan.ZeroFuelWeightKg = ToKg(zfw);
            if (int.TryParse(root["weights"]?["pax_count"]?.ToString(), out int paxCount))
                plan.PaxCount = paxCount;
            if (double.TryParse(root["weights"]?["cargo"]?.ToString(), out double cargo))
                plan.CargoKg = ToKg(cargo);

            // Departure elevation (ft)
```

- [ ] **Step 3: Build and verify**

Run:
```bash
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```
Expected: `Build succeeded. 0 Error(s)` (warning count may match the pre-existing baseline of 14, per prior session notes).

- [ ] **Step 4: Commit**

```bash
git add AviatesAirTracker/Models/FlightModels.cs AviatesAirTracker/Services/SimBriefService.cs
git commit -m "feat(simbrief): parse ZFW, pax count, and cargo weight from OFP"
```

---

### Task 2: Fix `Subsidiary` callsign-prefix classification

**Files:**
- Modify: `AviatesAirTracker/Services/RoutesService.cs:85-114`

- [ ] **Step 1: Replace the `Subsidiary` class**

Find the existing class (lines 85-114):
```csharp
public static class Subsidiary
{
    private static readonly HashSet<string> CityHopperTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "A319", "ATR 72", "ATR 42"
    };

    private static readonly HashSet<string> CargoTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "A350F"
    };

    /// <summary>Resolves the ICAO designator (VAV/VAC/VAH) for a given aircraft type.</summary>
    public static string IcaoPrefix(string? aircraftType)
    {
        var type = (aircraftType ?? "").Trim();
        if (CargoTypes.Contains(type))      return "VAC";
        if (CityHopperTypes.Contains(type)) return "VAH";
        return "VAV";
    }

    /// <summary>Resolves the human-readable subsidiary name for a given aircraft type.</summary>
    public static string Name(string? aircraftType)
    {
        var type = (aircraftType ?? "").Trim();
        if (CargoTypes.Contains(type))      return "Skycargo";
        if (CityHopperTypes.Contains(type)) return "CityHopper";
        return "Mainline";
    }
}
```
Replace with:
```csharp
public static class Subsidiary
{
    // Normalized (uppercase, single-spaced) so "ATR72", "atr 72", and "ATR  72"
    // all match the same entry — a raw string mismatch here used to silently
    // fall through to VAV for CityHopper/Cargo aircraft.
    private static readonly HashSet<string> CityHopperTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        Normalize("A319"), Normalize("B736"), Normalize("ATR72"), Normalize("ATR 42")
    };

    private static readonly HashSet<string> CargoTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        Normalize("B77F")
    };

    private static string Normalize(string type) =>
        string.Join(' ', (type ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
              .ToUpperInvariant();

    /// <summary>Resolves the ICAO designator (VAV/VAC/VAH) for a given aircraft type.</summary>
    public static string IcaoPrefix(string? aircraftType)
    {
        var type = Normalize(aircraftType ?? "");
        if (CargoTypes.Contains(type))      return "VAC";
        if (CityHopperTypes.Contains(type)) return "VAH";
        return "VAV";
    }

    /// <summary>Resolves the human-readable subsidiary name for a given aircraft type.</summary>
    public static string Name(string? aircraftType)
    {
        var type = Normalize(aircraftType ?? "");
        if (CargoTypes.Contains(type))      return "Skycargo";
        if (CityHopperTypes.Contains(type)) return "CityHopper";
        return "Mainline";
    }

    /// <summary>Strips a VAV/VAC/VAH ICAO prefix off a callsign, e.g. "VAH103" → "103".
    /// Returns the input unchanged if it doesn't start with one of the three prefixes.</summary>
    public static string StripIcaoPrefix(string callsign)
    {
        var cs = callsign ?? "";
        return cs.Length > 3 && StartsWithIcaoPrefix(cs) ? cs[3..] : cs;
    }

    private static bool StartsWithIcaoPrefix(string s) =>
        s.StartsWith("VAV", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("VAC", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("VAH", StringComparison.OrdinalIgnoreCase);
}
```

Note: mainline types (A320neo, A350, A380, B38M, B739ER, B748) need no explicit entry — they fall through to the `VAV` default, same as before.

- [ ] **Step 2: Build and verify**

```bash
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```
Expected: 0 errors. `SimBriefService.OpenDispatch` (which has its own private `StartsWithIcaoPrefix` local function) is untouched by this change and continues to compile — it's a separate copy, not calling into `Subsidiary`.

- [ ] **Step 3: Manual check**

In `AviatesAirTracker/Services/BookingService.cs`, temporarily confirm in a scratch REPL-style check isn't available (no test project) — instead, run the app (`.\build.ps1 -Run`), generate a booking with aircraft type `"ATR72"` (no space) via whatever flow currently creates bookings, and confirm the resulting callsign starts with `VAH` not `VAV`. Revert/discard any scratch booking data created for this check.

- [ ] **Step 4: Commit**

```bash
git add AviatesAirTracker/Services/RoutesService.cs
git commit -m "fix(routes): correct CityHopper/Cargo fleet lists and normalize type matching"
```

---

### Task 3: Add Hoppies models

**Files:**
- Create: `AviatesAirTracker/Models/HoppiesModels.cs`

- [ ] **Step 1: Create the file**

```csharp
namespace AviatesAirTracker.Models;

/// <summary>A single hand-maintained hazard-zone entry (conflict zone, volcano, or
/// wildfire) loaded from hazard_zones.json. No live data feed — VA staff edit this
/// file by hand when a real-world notice goes out.</summary>
public class HazardZoneEntry
{
    public string Icao { get; set; } = "";
    public string Type { get; set; } = ""; // "conflict" | "volcano" | "wildfire"
    public string Message { get; set; } = "";
}

/// <summary>Per-flight record of which Hoppies message triggers have already fired,
/// persisted to disk so a mid-flight app restart doesn't duplicate sends.</summary>
public class HoppiesFlightState
{
    public bool LoadsheetSent { get; set; }
    public bool PushbackReminderSent { get; set; }
    public bool AirborneSent { get; set; }
    public bool LandingSent { get; set; }
    public bool SummarySent { get; set; }
    public bool DepartureAdvisoriesSent { get; set; }
    public bool ArrivalAdvisoriesSent { get; set; }
}

/// <summary>One entry in the in-app Hoppies message log shown on the ACARS page.
/// Session-only — not persisted across restarts (only HoppiesFlightState is).</summary>
public class HoppiesLogEntry
{
    public DateTime SentAtUtc { get; set; }
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Success { get; set; }
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add AviatesAirTracker/Models/HoppiesModels.cs
git commit -m "feat(hoppies): add hazard zone, flight state, and log entry models"
```

---

### Task 4: WeightBalanceDatabase (MAC% engine + seeded table)

**Files:**
- Create: `AviatesAirTracker/Core/Analytics/WeightBalanceDatabase.cs`

- [ ] **Step 1: Create the file**

```csharp
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

    /// <summary>Returns the WB profile for the given aircraft type, or null if not seeded.</summary>
    public static AircraftWbProfile? GetProfile(string aircraftType) =>
        Profiles.TryGetValue(Normalize(aircraftType), out var profile) ? profile : null;

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
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add AviatesAirTracker/Core/Analytics/WeightBalanceDatabase.cs
git commit -m "feat(hoppies): add WeightBalanceDatabase for loadsheet %MAC calculation"
```

---

### Task 5: HoppiesAcarsClient

**Files:**
- Create: `AviatesAirTracker/Core/Backend/HoppiesAcarsClient.cs`

- [ ] **Step 1: Create the file**

```csharp
using RestSharp;
using Serilog;

namespace AviatesAirTracker.Core.Backend;

/// <summary>
/// Sends one-way telex messages over the real Hoppies ACARS network (hoppie.nl),
/// authenticated with the VA's own company logon code. Delivery is best-effort — a
/// failed send is logged and swallowed, never thrown into the caller, matching the
/// non-critical-call pattern used elsewhere in AviatesBackendClient.
/// </summary>
public class HoppiesAcarsClient
{
    // VA company Hoppies logon code. Authorizes outgoing sends — not the pilot's own
    // personal code (that's AppSettings.HoppiesPilotCode, stored but unused for
    // sending in this build). Hardcoded per explicit decision: this is a VA
    // operations credential, not a high-value secret.
    private const string CompanyLogonCode = "ykWrqTsbepVQAaj";
    private const string SenderStationId = "VAOPS";
    private const string HoppiesEndpoint = "https://www.hoppie.nl/acars/system/connect.html";

    private readonly RestClient _client = new();

    /// <summary>Sends a telex message to the given callsign. Returns true if Hoppies
    /// acknowledged the request (response body starts with "ok").</summary>
    public async Task<bool> SendTelexAsync(string toCallsign, string message)
    {
        if (string.IsNullOrWhiteSpace(toCallsign) || string.IsNullOrWhiteSpace(message))
            return false;

        try
        {
            var request = new RestRequest(HoppiesEndpoint, Method.Get)
                .AddQueryParameter("logon", CompanyLogonCode)
                .AddQueryParameter("from", SenderStationId)
                .AddQueryParameter("to", toCallsign)
                .AddQueryParameter("type", "telex")
                .AddQueryParameter("packet", message);

            var response = await _client.ExecuteAsync(request);
            var body = (response.Content ?? "").Trim();
            var success = response.IsSuccessful && body.StartsWith("ok", StringComparison.OrdinalIgnoreCase);

            if (success)
                Log.Information("[HoppiesAcars] Sent telex to {Callsign}: {Preview}", toCallsign, Preview(message));
            else
                Log.Warning("[HoppiesAcars] Telex to {Callsign} not accepted: {Body}", toCallsign, body);

            return success;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[HoppiesAcars] Failed to send telex to {Callsign} (non-critical)", toCallsign);
            return false;
        }
    }

    private static string Preview(string message)
    {
        var oneLine = message.Replace("\r", " ").Replace("\n", " ");
        return oneLine.Length > 60 ? oneLine[..60] + "..." : oneLine;
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```
Expected: 0 errors.

- [ ] **Step 3: Manual smoke test**

Hoppies' network accepts test traffic from any registered logon code. Temporarily add a throwaway call (e.g. in `App.xaml.cs`'s startup, or a scratch button) invoking:
```csharp
await new HoppiesAcarsClient().SendTelexAsync("TEST123", "AVIATESAIR HOPPIES INTEGRATION TEST");
```
Check the Serilog log output for `[HoppiesAcars] Sent telex to TEST123` (success) rather than a warning. Remove the scratch call afterward — this step is a one-off connectivity check, not permanent code.

- [ ] **Step 4: Commit**

```bash
git add AviatesAirTracker/Core/Backend/HoppiesAcarsClient.cs
git commit -m "feat(hoppies): add HoppiesAcarsClient for real Hoppies telex delivery"
```

---

### Task 6: HazardZoneService

**Files:**
- Create: `AviatesAirTracker/Services/HazardZoneService.cs`

- [ ] **Step 1: Create the file**

```csharp
using System.Text.Json;
using AviatesAirTracker.Models;
using Serilog;

namespace AviatesAirTracker.Services;

/// <summary>
/// Loads a hand-maintained list of hazard zones (conflict areas, volcanic activity,
/// wildfires) from a local JSON file so Hoppies advisories can flag departure/arrival
/// airports without any live news/data feed. Call Reload() any time to pick up manual
/// edits to the file without restarting the app.
/// </summary>
public class HazardZoneService
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AviatesAirTracker", "hazard_zones.json");

    private List<HazardZoneEntry> _zones = [];

    public HazardZoneService()
    {
        Reload();
    }

    /// <summary>Re-reads hazard_zones.json from disk, seeding an example file if none exists yet.</summary>
    public void Reload()
    {
        try
        {
            if (!File.Exists(_path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var example = new List<HazardZoneEntry>
                {
                    new() { Icao = "OSDI", Type = "conflict", Message = "ACTIVE CONFLICT ZONE - EXERCISE EXTREME CAUTION" },
                    new() { Icao = "BIKF", Type = "volcano",  Message = "VOLCANIC ACTIVITY REPORTED IN VICINITY - MONITOR NOTAMS" }
                };
                File.WriteAllText(_path, JsonSerializer.Serialize(example, new JsonSerializerOptions { WriteIndented = true }));
                _zones = example;
                Log.Information("[HazardZones] Seeded example file at {Path}", _path);
                return;
            }

            var json = File.ReadAllText(_path);
            _zones = JsonSerializer.Deserialize<List<HazardZoneEntry>>(json) ?? [];
            Log.Information("[HazardZones] Loaded {Count} hazard zone entries", _zones.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[HazardZones] Failed to load hazard_zones.json");
            _zones = [];
        }
    }

    /// <summary>Returns any hazard entries matching the given ICAO exactly (case-insensitive).</summary>
    public List<HazardZoneEntry> GetHazards(string icao)
    {
        if (string.IsNullOrWhiteSpace(icao)) return [];
        return _zones.Where(z => string.Equals(z.Icao, icao, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```
Expected: 0 errors.

- [ ] **Step 3: Manual check**

Run the app once (`.\build.ps1 -Run`), then confirm `%AppData%\AviatesAirTracker\hazard_zones.json` was created with the two example entries. Edit the file, add a third entry, call nothing yet (no UI wired up to `Reload` until Task 11) — just confirm the file is hand-editable JSON as intended.

- [ ] **Step 4: Commit**

```bash
git add AviatesAirTracker/Services/HazardZoneService.cs
git commit -m "feat(hoppies): add HazardZoneService for static conflict/volcano/wildfire advisories"
```

---

### Task 7: WeatherAdvisoryService

**Files:**
- Create: `AviatesAirTracker/Services/WeatherAdvisoryService.cs`

- [ ] **Step 1: Create the file**

```csharp
using System.Text.RegularExpressions;

namespace AviatesAirTracker.Services;

/// <summary>
/// Derives simple weather advisories (high wind, low visibility, volcanic ash/smoke
/// remarks) from the raw METAR text already fetched by SimBriefService. Reuses the
/// existing aviationweather.gov call — no new external dependency. This is heuristic
/// regex parsing of raw METAR text, not a full METAR parser — good enough for
/// "worth a heads-up" advisories, not for real dispatch decisions.
/// </summary>
public partial class WeatherAdvisoryService(SimBriefService simBrief)
{
    private const int HighWindKt = 25;
    private const int HighGustKt = 35;
    private const double LowVisibilitySm = 1.0;
    private const int LowVisibilityM = 1600;

    [GeneratedRegex(@"\b(\d{3}|VRB)(\d{2,3})(G(\d{2,3}))?KT\b")]
    private static partial Regex WindRegex();

    [GeneratedRegex(@"\b(\d)/(\d)SM\b")]
    private static partial Regex FractionalVisRegex();

    [GeneratedRegex(@"\b(\d{1,2})SM\b")]
    private static partial Regex WholeVisRegex();

    // Excludes matches preceded by another digit, Q, or A — avoids misreading the
    // day/time group (e.g. "251853Z") or an altimeter setting (e.g. "Q1013"/"A2992")
    // as a meters-visibility group.
    [GeneratedRegex(@"(?<![\dQA])(\d{4})(?!\d)")]
    private static partial Regex MetersVisRegex();

    /// <summary>Fetches the current METAR for the given ICAO and returns any advisory strings triggered by it.</summary>
    public async Task<List<string>> GetAdvisoriesAsync(string icao)
    {
        var advisories = new List<string>();
        var metar = await simBrief.FetchMetarAsync(icao);
        if (string.IsNullOrWhiteSpace(metar)) return advisories;

        var windMatch = WindRegex().Match(metar);
        if (windMatch.Success)
        {
            var speed = int.Parse(windMatch.Groups[2].Value);
            var gustText = windMatch.Groups[4].Value;
            var gust = string.IsNullOrEmpty(gustText) ? (int?)null : int.Parse(gustText);

            if (gust is >= HighGustKt)
                advisories.Add($"WIND GUSTING {gust}KT EXCEEDS RECOMMENDED LIMITS");
            else if (speed >= HighWindKt)
                advisories.Add($"SUSTAINED WIND {speed}KT EXCEEDS RECOMMENDED LIMITS");
        }

        var fracVis = FractionalVisRegex().Match(metar);
        if (fracVis.Success)
        {
            var num = double.Parse(fracVis.Groups[1].Value);
            var den = double.Parse(fracVis.Groups[2].Value);
            if (num / den < LowVisibilitySm)
                advisories.Add($"VISIBILITY {fracVis.Value} - LOW VIS OPS MAY APPLY");
        }
        else
        {
            var wholeVis = WholeVisRegex().Match(metar);
            if (wholeVis.Success && double.TryParse(wholeVis.Groups[1].Value, out var sm) && sm < LowVisibilitySm)
            {
                advisories.Add($"VISIBILITY {wholeVis.Value} - LOW VIS OPS MAY APPLY");
            }
            else
            {
                var metersVis = MetersVisRegex().Match(metar);
                if (metersVis.Success && int.TryParse(metersVis.Groups[1].Value, out var m) && m != 9999 && m < LowVisibilityM)
                    advisories.Add($"VISIBILITY {m}M - LOW VIS OPS MAY APPLY");
            }
        }

        if (Regex.IsMatch(metar, @"\bVA\b"))
            advisories.Add("VOLCANIC ASH REPORTED IN METAR REMARKS");
        if (Regex.IsMatch(metar, @"\bFU\b"))
            advisories.Add("SMOKE REPORTED IN METAR REMARKS");

        return advisories;
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```
Expected: 0 errors.

- [ ] **Step 3: Manual check**

Temporarily call `await weatherAdvisoryService.GetAdvisoriesAsync("KJFK")` from a scratch button/breakpoint and confirm it returns an empty or populated list without throwing, for a real ICAO with a live METAR. Try an ICAO with known gusty conditions if possible (e.g. checking a live METAR site first) to confirm the wind-gust branch fires correctly. Remove the scratch call afterward.

- [ ] **Step 4: Commit**

```bash
git add AviatesAirTracker/Services/WeatherAdvisoryService.cs
git commit -m "feat(hoppies): add WeatherAdvisoryService for METAR-derived wind/visibility advisories"
```

---

### Task 8: HoppiesMessageComposer

**Files:**
- Create: `AviatesAirTracker/Services/HoppiesMessageComposer.cs`

- [ ] **Step 1: Create the file**

```csharp
using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Models;

namespace AviatesAirTracker.Services;

/// <summary>
/// Pure formatting: builds each Hoppies telex message body. No I/O, no state — every
/// method takes the data it needs and returns plain text.
/// </summary>
public static class HoppiesMessageComposer
{
    private static string Hms(TimeSpan t) => $"{(int)t.TotalHours:D2}:{t.Minutes:D2}";

    public static string Loadsheet(string flightNumber, string registration, SimBriefFlightPlan plan)
    {
        var date = plan.ScheduledDepartureUtc?.ToString("ddMMMyy", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant() ?? "";
        var lines = new List<string>
        {
            "LOADSHEET",
            $"{flightNumber} {date}",
            $"{plan.DepartureICAO} {plan.ArrivalICAO} {registration}",
            ".........................",
            $"ZFW  {plan.ZeroFuelWeightKg:F0}",
            $"TOW  {plan.TakeoffWeightKg:F0}",
            $"PAX  {plan.PaxCount}"
        };

        var wb = WeightBalanceDatabase.Compute(plan.AircraftType, plan.ZeroFuelWeightKg, plan.TakeoffWeightKg);
        if (wb is not null)
        {
            lines.Add($"MACZFW {wb.MacZfwPercent:F1}");
            lines.Add($"MACTOW {wb.MacTowPercent:F1}");
        }

        lines.Add($"FUEL IN TANKS {plan.TakeoffFuelLbs:F0}");
        lines.Add("END");

        return string.Join("\n", lines);
    }

    public static string PushbackReminder(string callsign, DateTime scheduledDepartureUtc) =>
        $"{callsign}, SCHEDULED OFF-BLOCK IN 5 MIN ({scheduledDepartureUtc:HHmm}Z). PREPARE FOR PUSHBACK.";

    public static string AirborneCall(string callsign, string departureIcao, DateTime timeUtc) =>
        $"{callsign} AIRBORNE {departureIcao} {timeUtc:HHmm}Z. HAVE A SAFE FLIGHT.";

    public static string LandingCall(string callsign, string arrivalIcao, DateTime timeUtc) =>
        $"{callsign} TOUCHDOWN {arrivalIcao} {timeUtc:HHmm}Z. WELCOME TO {arrivalIcao}.";

    public static string PostFlightSummary(FlightRecord record)
    {
        var landingRate = record.PrimaryLanding is not null
            ? $"{Math.Abs(record.PrimaryLanding.VerticalSpeedFPM):F0} FPM ({record.PrimaryLanding.LandingGrade})"
            : "N/A";

        return string.Join("\n",
            $"FLIGHT SUMMARY - {record.FlightNumber} / {record.Callsign}",
            $"ROUTE: {record.DepartureICAO}-{record.ArrivalICAO}",
            $"BLOCK TIME: {Hms(record.BlockTime)} AIR TIME: {Hms(record.AirTime)}",
            $"LANDING RATE: {landingRate}",
            "THANK YOU FOR FLYING WITH US");
    }

    public static string HazardAdvisory(string icao, string hazardText) =>
        string.Join("\n",
            $"SECURITY ADVISORY - {icao}",
            hazardText,
            "EXERCISE CAUTION AND FOLLOW COMPANY/ATC GUIDANCE");

    public static string WeatherAdvisory(string icao, string condition, string phaseLabel) =>
        string.Join("\n",
            $"WX ADVISORY - {icao}",
            condition,
            $"REVIEW LATEST METAR/TAF BEFORE {phaseLabel}");
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add AviatesAirTracker/Services/HoppiesMessageComposer.cs
git commit -m "feat(hoppies): add HoppiesMessageComposer for telex message formatting"
```

---

### Task 9: HoppiesAcarsOrchestrator

**Files:**
- Create: `AviatesAirTracker/Services/HoppiesAcarsOrchestrator.cs`

This is the scheduling brain. It subscribes to five existing event sources and never duplicates timer logic that already exists (`TakeoffPerformanceService.BriefingTriggered` supplies the -15min loadsheet signal).

- [ ] **Step 1: Create the file**

```csharp
using System.Text.Json;
using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using Serilog;

namespace AviatesAirTracker.Services;

/// <summary>
/// Schedules and sends every automated Hoppies ACARS telex for the current flight:
/// loadsheet (via TakeoffPerformanceService's existing -15min timer), pushback
/// reminder (-5min, own timer), airborne/landing calls, post-flight summary, and
/// departure/arrival weather + hazard advisories. No-ops entirely when
/// AppSettings.HoppiesEnabled is false. Persists per-flight sent-state to disk so a
/// mid-flight app restart never duplicates a message.
/// </summary>
public class HoppiesAcarsOrchestrator : IDisposable
{
    private readonly SettingsService _settings;
    private readonly SimBriefService _simBrief;
    private readonly TakeoffPerformanceService _takeoffPerf;
    private readonly FlightPhaseDetector _phaseDetector;
    private readonly FlightSessionManager _session;
    private readonly BookingService _booking;
    private readonly LandingAnalyzer _landingAnalyzer;
    private readonly HoppiesAcarsClient _client;
    private readonly HazardZoneService _hazards;
    private readonly WeatherAdvisoryService _weather;

    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AviatesAirTracker", "hoppies_state.json");

    private HoppiesFlightState _state = new();
    private System.Timers.Timer? _pushbackTimer;
    private FlightPhase _lastPhase = FlightPhase.Unknown;

    /// <summary>Raised after every send attempt (success or failure) for the in-app log.</summary>
    public event EventHandler<HoppiesLogEntry>? MessageSent;

    public HoppiesAcarsOrchestrator(
        SettingsService settings,
        SimBriefService simBrief,
        TakeoffPerformanceService takeoffPerf,
        FlightPhaseDetector phaseDetector,
        FlightSessionManager session,
        BookingService booking,
        LandingAnalyzer landingAnalyzer,
        HoppiesAcarsClient client,
        HazardZoneService hazards,
        WeatherAdvisoryService weather)
    {
        _settings = settings;
        _simBrief = simBrief;
        _takeoffPerf = takeoffPerf;
        _phaseDetector = phaseDetector;
        _session = session;
        _booking = booking;
        _landingAnalyzer = landingAnalyzer;
        _client = client;
        _hazards = hazards;
        _weather = weather;

        _simBrief.FlightPlanLoaded += OnFlightPlanLoaded;
        _takeoffPerf.BriefingTriggered += OnLoadsheetDue;
        _phaseDetector.PhaseChanged += OnPhaseChanged;
        _landingAnalyzer.LandingDetected += OnLandingDetected;
        _session.FlightCompleted += OnFlightCompleted;

        Log.Information("[HoppiesAcars] Orchestrator initialized");
    }

    private bool Enabled => _settings.Settings.HoppiesEnabled;

    // ── Trigger: SimBrief plan loaded → reset state, arm pushback timer, check departure advisories ──

    private void OnFlightPlanLoaded(object? sender, SimBriefFlightPlan plan)
    {
        _state = new HoppiesFlightState();
        SaveState();
        _lastPhase = FlightPhase.Unknown;
        ResetPushbackTimer();

        if (!Enabled) return;

        if (plan.ScheduledDepartureUtc is { } dep)
            SchedulePushbackTimer(dep);

        if (_settings.Settings.HoppiesSendWeatherAdvisories || _settings.Settings.HoppiesSendHazardAdvisories)
            _ = SendDepartureAdvisoriesAsync(plan);
    }

    private void SchedulePushbackTimer(DateTime scheduledDepartureUtc)
    {
        var alertAt = scheduledDepartureUtc.AddMinutes(-5);
        var delay = alertAt - DateTime.UtcNow;

        if (delay.TotalSeconds <= 0)
        {
            _ = SendPushbackReminderAsync(scheduledDepartureUtc);
            return;
        }

        _pushbackTimer = new System.Timers.Timer(delay.TotalMilliseconds) { AutoReset = false };
        _pushbackTimer.Elapsed += (_, _) => _ = SendPushbackReminderAsync(scheduledDepartureUtc);
        _pushbackTimer.Start();
    }

    private void ResetPushbackTimer()
    {
        _pushbackTimer?.Stop();
        _pushbackTimer?.Dispose();
        _pushbackTimer = null;
    }

    // ── Trigger: TakeoffPerformanceService fires 15 min before scheduled departure ──

    private void OnLoadsheetDue(object? sender, TakeoffBriefingEventArgs e)
    {
        if (!Enabled || !_settings.Settings.HoppiesSendLoadsheet || _state.LoadsheetSent) return;
        _ = SendLoadsheetAsync(e.Plan);
    }

    private async Task SendLoadsheetAsync(SimBriefFlightPlan plan)
    {
        var booking = _booking.ActiveBooking;
        if (booking is null) return;

        var flightNumber = Subsidiary.StripIcaoPrefix(
            !string.IsNullOrEmpty(booking.RouteCallsign) ? booking.RouteCallsign : booking.Callsign);
        var message = HoppiesMessageComposer.Loadsheet(flightNumber, booking.Registration ?? "", plan);

        var success = await _client.SendTelexAsync(booking.Callsign, message);
        Log(success, "Loadsheet", message);
        if (success)
        {
            _state.LoadsheetSent = true;
            SaveState();
        }
    }

    private async Task SendPushbackReminderAsync(DateTime scheduledDepartureUtc)
    {
        if (!Enabled || !_settings.Settings.HoppiesSendTimingReminders || _state.PushbackReminderSent) return;

        var booking = _booking.ActiveBooking;
        if (booking is null) return;

        var message = HoppiesMessageComposer.PushbackReminder(booking.Callsign, scheduledDepartureUtc);
        var success = await _client.SendTelexAsync(booking.Callsign, message);
        Log(success, "Pushback Reminder", message);
        if (success)
        {
            _state.PushbackReminderSent = true;
            SaveState();
        }
    }

    // ── Trigger: flight phase changes → airborne call + arrival advisories ──

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEvent e)
    {
        var enteringInitialClimb = e.Current == FlightPhase.InitialClimb && _lastPhase != FlightPhase.InitialClimb;
        var enteringDescent = (e.Current == FlightPhase.TopOfDescent || e.Current == FlightPhase.Descent)
            && _lastPhase is not (FlightPhase.TopOfDescent or FlightPhase.Descent);
        _lastPhase = e.Current;

        if (!Enabled) return;

        if (enteringInitialClimb && _settings.Settings.HoppiesSendAirborneLandingCalls && !_state.AirborneSent)
            _ = SendAirborneCallAsync();

        if (enteringDescent && (_settings.Settings.HoppiesSendWeatherAdvisories || _settings.Settings.HoppiesSendHazardAdvisories)
            && !_state.ArrivalAdvisoriesSent && _simBrief.CurrentPlan is { } plan)
            _ = SendArrivalAdvisoriesAsync(plan);
    }

    private async Task SendAirborneCallAsync()
    {
        var booking = _booking.ActiveBooking;
        var plan = _simBrief.CurrentPlan;
        if (booking is null || plan is null) return;

        var message = HoppiesMessageComposer.AirborneCall(booking.Callsign, plan.DepartureICAO, DateTime.UtcNow);
        var success = await _client.SendTelexAsync(booking.Callsign, message);
        Log(success, "Airborne Call", message);
        if (success)
        {
            _state.AirborneSent = true;
            SaveState();
        }
    }

    // ── Trigger: LandingAnalyzer detects a landing ──

    private void OnLandingDetected(object? sender, LandingResult result)
    {
        if (!Enabled || !_settings.Settings.HoppiesSendAirborneLandingCalls || _state.LandingSent) return;
        _ = SendLandingCallAsync(result);
    }

    private async Task SendLandingCallAsync(LandingResult result)
    {
        var booking = _booking.ActiveBooking;
        if (booking is null) return;

        var arrivalIcao = string.IsNullOrEmpty(result.AirportICAO) ? (_simBrief.CurrentPlan?.ArrivalICAO ?? "") : result.AirportICAO;
        var message = HoppiesMessageComposer.LandingCall(booking.Callsign, arrivalIcao, DateTime.UtcNow);
        var success = await _client.SendTelexAsync(booking.Callsign, message);
        Log(success, "Landing Call", message);
        if (success)
        {
            _state.LandingSent = true;
            SaveState();
        }
    }

    // ── Trigger: FlightSessionManager.FlightCompleted → post-flight summary ──

    private void OnFlightCompleted(object? sender, FlightRecord record)
    {
        if (!Enabled || !_settings.Settings.HoppiesSendPostFlightSummary || _state.SummarySent) return;
        _ = SendSummaryAsync(record);
    }

    private async Task SendSummaryAsync(FlightRecord record)
    {
        var message = HoppiesMessageComposer.PostFlightSummary(record);
        var success = await _client.SendTelexAsync(record.Callsign, message);
        Log(success, "Flight Summary", message);
        if (success)
        {
            _state.SummarySent = true;
            SaveState();
        }
    }

    // ── Weather / hazard advisories ──

    private async Task SendDepartureAdvisoriesAsync(SimBriefFlightPlan plan)
    {
        await SendAdvisoriesAsync(plan.DepartureICAO, "DEPARTURE");
        _state.DepartureAdvisoriesSent = true;
        SaveState();
    }

    private async Task SendArrivalAdvisoriesAsync(SimBriefFlightPlan plan)
    {
        await SendAdvisoriesAsync(plan.ArrivalICAO, "ARRIVAL");
        _state.ArrivalAdvisoriesSent = true;
        SaveState();
    }

    private async Task SendAdvisoriesAsync(string icao, string phaseLabel)
    {
        var booking = _booking.ActiveBooking;
        if (booking is null || string.IsNullOrEmpty(icao)) return;

        if (_settings.Settings.HoppiesSendHazardAdvisories)
        {
            foreach (var hazard in _hazards.GetHazards(icao))
            {
                var message = HoppiesMessageComposer.HazardAdvisory(icao, hazard.Message);
                var success = await _client.SendTelexAsync(booking.Callsign, message);
                Log(success, "Hazard Advisory", message);
            }
        }

        if (_settings.Settings.HoppiesSendWeatherAdvisories)
        {
            foreach (var condition in await _weather.GetAdvisoriesAsync(icao))
            {
                var message = HoppiesMessageComposer.WeatherAdvisory(icao, condition, phaseLabel);
                var success = await _client.SendTelexAsync(booking.Callsign, message);
                Log(success, "Weather Advisory", message);
            }
        }
    }

    // ── Persistence + logging ──

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(_state));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[HoppiesAcars] Failed to save state");
        }
    }

    private void Log(bool success, string category, string message)
    {
        MessageSent?.Invoke(this, new HoppiesLogEntry
        {
            SentAtUtc = DateTime.UtcNow,
            Category = category,
            Message = message,
            Success = success
        });
    }

    public void Dispose()
    {
        ResetPushbackTimer();
        _simBrief.FlightPlanLoaded -= OnFlightPlanLoaded;
        _takeoffPerf.BriefingTriggered -= OnLoadsheetDue;
        _phaseDetector.PhaseChanged -= OnPhaseChanged;
        _landingAnalyzer.LandingDetected -= OnLandingDetected;
        _session.FlightCompleted -= OnFlightCompleted;
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```
Expected: build will FAIL at this point with DI-resolution-unrelated compile errors are not expected, but note this class is not yet registered in `App.xaml.cs` — that's fine, it will simply be unused/uninstantiated until Task 10. Confirm 0 *compile* errors (unresolved-reference errors, if any, indicate a typo against one of the real event/method signatures from Tasks 1-8 — double check against those files).

- [ ] **Step 3: Commit**

```bash
git add AviatesAirTracker/Services/HoppiesAcarsOrchestrator.cs
git commit -m "feat(hoppies): add HoppiesAcarsOrchestrator to wire all message triggers together"
```

---

### Task 10: Settings — AppSettings fields + Settings.razor UI

**Files:**
- Modify: `AviatesAirTracker/Services/SupportServices.cs:341-384`
- Modify: `AviatesAirTracker/Pages/Settings.razor`

- [ ] **Step 1: Add fields to `AppSettings`**

In `AviatesAirTracker/Services/SupportServices.cs`, find the last line of the class:
```csharp
    // Interval (minutes) between automatic ACARS position reports. 0 = off.
    public int AutoPirepIntervalMinutes { get; set; } = 5;
}
```
Replace with:
```csharp
    // Interval (minutes) between automatic ACARS position reports. 0 = off.
    public int AutoPirepIntervalMinutes { get; set; } = 5;

    // Hoppies ACARS integration. HoppiesPilotCode is stored/validated but not used
    // for sending in this build — reserved for a future two-way messaging feature.
    public string HoppiesPilotCode { get; set; } = "";
    public bool HoppiesEnabled { get; set; } = false;
    public bool HoppiesSendLoadsheet { get; set; } = true;
    public bool HoppiesSendTimingReminders { get; set; } = true;
    public bool HoppiesSendAirborneLandingCalls { get; set; } = true;
    public bool HoppiesSendPostFlightSummary { get; set; } = true;
    public bool HoppiesSendWeatherAdvisories { get; set; } = true;
    public bool HoppiesSendHazardAdvisories { get; set; } = true;
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```
Expected: 0 errors.

- [ ] **Step 3: Add the "Hoppies ACARS" card to `Settings.razor`**

Find the closing `</div>` of the "ACARS Configuration" `panel-card` (immediately after the `AutoPirepIntervalMinutes` `<select>` block, i.e. right after line ~200 where that card's outer `<div class="panel-card anim-up-d2">` closes) and insert a new card immediately after it:
```razor
<!-- Hoppies ACARS -->
<div class="panel-card anim-up-d2">
    <div class="form-section-title">Hoppies ACARS</div>
    <div style="display:flex;flex-direction:column;gap:var(--sp-4);">
        <div class="form-group">
            <label class="form-label">Your Hoppies Code</label>
            <div style="display:flex;gap:var(--sp-3);">
                <input class="form-input" type="password"
                       style="flex:1;"
                       value="@SettingsService.Settings.HoppiesPilotCode"
                       @oninput="OnHoppiesPilotCodeInput"
                       placeholder="Paste your personal Hoppies logon code" />
            </div>
            <div class="form-hint">Stored for a future feature. Not required for company messages to reach you — those use the VA's own Hoppies connection.</div>
        </div>

        <div class="form-group">
            <label class="form-toggle">
                <input type="checkbox" checked="@SettingsService.Settings.HoppiesEnabled" @onchange="OnHoppiesEnabledChanged" />
                <span>Enable Hoppies ACARS Messages</span>
            </label>
        </div>

        <div style="display:flex;flex-direction:column;gap:var(--sp-2);opacity:@(SettingsService.Settings.HoppiesEnabled ? "1" : "0.5");">
            <label class="form-toggle">
                <input type="checkbox" checked="@SettingsService.Settings.HoppiesSendLoadsheet"
                       disabled="@(!SettingsService.Settings.HoppiesEnabled)"
                       @onchange="@((ChangeEventArgs e) => OnCategoryToggle(v => SettingsService.Settings.HoppiesSendLoadsheet = v, e))" />
                <span>Loadsheet</span>
            </label>
            <label class="form-toggle">
                <input type="checkbox" checked="@SettingsService.Settings.HoppiesSendTimingReminders"
                       disabled="@(!SettingsService.Settings.HoppiesEnabled)"
                       @onchange="@((ChangeEventArgs e) => OnCategoryToggle(v => SettingsService.Settings.HoppiesSendTimingReminders = v, e))" />
                <span>Timing Reminders</span>
            </label>
            <label class="form-toggle">
                <input type="checkbox" checked="@SettingsService.Settings.HoppiesSendAirborneLandingCalls"
                       disabled="@(!SettingsService.Settings.HoppiesEnabled)"
                       @onchange="@((ChangeEventArgs e) => OnCategoryToggle(v => SettingsService.Settings.HoppiesSendAirborneLandingCalls = v, e))" />
                <span>Airborne / Landing Calls</span>
            </label>
            <label class="form-toggle">
                <input type="checkbox" checked="@SettingsService.Settings.HoppiesSendPostFlightSummary"
                       disabled="@(!SettingsService.Settings.HoppiesEnabled)"
                       @onchange="@((ChangeEventArgs e) => OnCategoryToggle(v => SettingsService.Settings.HoppiesSendPostFlightSummary = v, e))" />
                <span>Post-Flight Summary</span>
            </label>
            <label class="form-toggle">
                <input type="checkbox" checked="@SettingsService.Settings.HoppiesSendWeatherAdvisories"
                       disabled="@(!SettingsService.Settings.HoppiesEnabled)"
                       @onchange="@((ChangeEventArgs e) => OnCategoryToggle(v => SettingsService.Settings.HoppiesSendWeatherAdvisories = v, e))" />
                <span>Weather Advisories</span>
            </label>
            <label class="form-toggle">
                <input type="checkbox" checked="@SettingsService.Settings.HoppiesSendHazardAdvisories"
                       disabled="@(!SettingsService.Settings.HoppiesEnabled)"
                       @onchange="@((ChangeEventArgs e) => OnCategoryToggle(v => SettingsService.Settings.HoppiesSendHazardAdvisories = v, e))" />
                <span>Hazard Advisories</span>
            </label>
        </div>
    </div>
</div>
```
Note: `form-toggle`/`form-hint` classes are assumed present in `app.css` following the existing design system; if they don't already exist, use whatever the existing toggle markup pattern in this same page uses instead (check for a `.toggle-switch` or similar class already used elsewhere in `Settings.razor`, e.g. the theme selector, and match it exactly rather than inventing a new class name).

- [ ] **Step 4: Add the C# handlers**

In the `@code` block of `Settings.razor`, near `OnAcarsKeyInput` (around line 623), add:
```csharp
private void OnHoppiesPilotCodeInput(ChangeEventArgs e)
{
    SettingsService.Settings.HoppiesPilotCode = (e.Value?.ToString() ?? "").Trim();
    SettingsService.Save();
}

private void OnHoppiesEnabledChanged(ChangeEventArgs e)
{
    SettingsService.Settings.HoppiesEnabled = (bool)(e.Value ?? false);
    SettingsService.Save();
}

private void OnCategoryToggle(Action<bool> setter, ChangeEventArgs e)
{
    setter((bool)(e.Value ?? false));
    SettingsService.Save();
}
```

- [ ] **Step 5: Build and verify**

```bash
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```
Expected: 0 errors. If `form-toggle`/`form-hint` classes don't exist in `app.css`, this step is where a Razor markup compile error (or, more likely, just unstyled-but-functional checkboxes at runtime — Blazor doesn't error on unknown CSS classes) would surface visually rather than as a build error; visually verify in Step 6.

- [ ] **Step 6: Manual check**

Run `.\build.ps1 -Run`, open Settings, confirm: the new Hoppies card renders, the pilot-code field saves on blur/change, the master toggle enables/disables the six checkboxes' visual state, and toggling any checkbox persists across an app restart (check `%AppData%\AviatesAirTracker\aviates_settings.json` for the new keys).

- [ ] **Step 7: Commit**

```bash
git add AviatesAirTracker/Services/SupportServices.cs AviatesAirTracker/Pages/Settings.razor
git commit -m "feat(settings): add Hoppies ACARS configuration section"
```

---

### Task 11: DI registration

**Files:**
- Modify: `AviatesAirTracker/App.xaml.cs`

- [ ] **Step 1: Register the four new services**

Find:
```csharp
    // Business
    services.AddSingleton<AviatesBackendClient>();
    services.AddSingleton<AcarsPositionService>();
```
Replace with:
```csharp
    // Business
    services.AddSingleton<AviatesBackendClient>();
    services.AddSingleton<AcarsPositionService>();
    services.AddSingleton<Core.Backend.HoppiesAcarsClient>();
    services.AddSingleton<HazardZoneService>();
    services.AddSingleton<WeatherAdvisoryService>();
    services.AddSingleton<HoppiesAcarsOrchestrator>();
```

- [ ] **Step 2: Force orchestrator instantiation at startup**

Singletons registered via `AddSingleton<T>()` are only constructed on first resolution. Nothing currently resolves `HoppiesAcarsOrchestrator` directly (it's meant to run in the background from app start, subscribing to events, not be injected into a page). Find wherever `App.xaml.cs` already forces early construction of a similar background-only singleton (search for how `AcarsPositionService` or `DiscordPresenceService` get resolved once at startup — likely a block of `serviceProvider.GetRequiredService<...>()` calls after `BuildServiceProvider()`) and add:
```csharp
serviceProvider.GetRequiredService<HoppiesAcarsOrchestrator>();
```
to that same block, immediately after (or alongside) the existing `AcarsPositionService` resolution.

- [ ] **Step 3: Build and verify**

```bash
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```
Expected: 0 errors.

- [ ] **Step 4: Manual check**

Run `.\build.ps1 -Run`, check the Serilog log file in `logs/` for `[HoppiesAcars] Orchestrator initialized`, confirming the singleton was actually constructed at startup rather than sitting unused.

- [ ] **Step 5: Commit**

```bash
git add AviatesAirTracker/App.xaml.cs
git commit -m "feat(hoppies): register and eagerly start HoppiesAcarsOrchestrator"
```

---

### Task 12: In-app message log on Acars.razor

**Files:**
- Modify: `AviatesAirTracker/Pages/Acars.razor`

- [ ] **Step 1: Inject the orchestrator**

Near the existing `@inject` lines (lines 6-16), add:
```razor
@inject HoppiesAcarsOrchestrator HoppiesOrchestrator
```

- [ ] **Step 2: Add the log-entry field and subscribe/unsubscribe**

In the `@code` block, add a field near the other private state fields:
```csharp
private readonly List<HoppiesLogEntry> _hoppiesLog = new();
```
In `OnInitialized()`, alongside the existing subscriptions (near `Session.TelemetryUpdated += OnTelemetryUpdated;`), add:
```csharp
HoppiesOrchestrator.MessageSent += OnHoppiesMessageSent;
```
In `DisposeAsync()`, alongside the existing unsubscriptions, add:
```csharp
HoppiesOrchestrator.MessageSent -= OnHoppiesMessageSent;
```

- [ ] **Step 3: Add the handler**

```csharp
private async void OnHoppiesMessageSent(object? sender, HoppiesLogEntry entry)
{
    if (_disposed) return;
    _hoppiesLog.Insert(0, entry);
    if (_hoppiesLog.Count > 50) _hoppiesLog.RemoveAt(_hoppiesLog.Count - 1);
    await InvokeAsync(StateHasChanged);
}
```

- [ ] **Step 4: Add the log card markup**

Add near the existing METAR/NOTAM cards (matching the same `panel-card` style used throughout the page):
```razor
<div class="panel-card">
    <div class="form-section-title">Hoppies ACARS Log</div>
    @if (_hoppiesLog.Count == 0)
    {
        <div class="empty-state">No messages sent yet this session.</div>
    }
    else
    {
        <div style="display:flex;flex-direction:column;gap:var(--sp-2);max-height:300px;overflow-y:auto;">
            @foreach (var entry in _hoppiesLog)
            {
                <div style="display:flex;justify-content:space-between;gap:var(--sp-3);padding:var(--sp-2) 0;border-bottom:1px solid var(--border-subtle);">
                    <span style="font-weight:600;">@entry.Category</span>
                    <span style="color:@(entry.Success ? "var(--accent)" : "var(--danger)");">@(entry.Success ? "SENT" : "FAILED")</span>
                    <span style="opacity:0.6;">@entry.SentAtUtc.ToString("HH:mm:ss")Z</span>
                </div>
            }
        </div>
    }
</div>
```
Note: `--danger`/`--accent`/`--border-subtle` CSS variable names are assumed from the existing design system per CLAUDE.md's brand-color guidance — verify these exact variable names exist in `app.css` before using them; if different names are already established there (e.g. `--color-danger`), use those instead.

- [ ] **Step 5: Build and verify**

```bash
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```
Expected: 0 errors.

- [ ] **Step 6: Manual check**

Run `.\build.ps1 -Run`, navigate to the ACARS page, confirm the new "Hoppies ACARS Log" card renders with the empty state. A full end-to-end message-send check requires an active flight with `HoppiesEnabled` on and a real booking — exercise this after Task 13's full smoke test below rather than in isolation here.

- [ ] **Step 7: Commit**

```bash
git add AviatesAirTracker/Pages/Acars.razor
git commit -m "feat(acars): add in-app Hoppies message log card"
```

---

### Task 13: End-to-end smoke test

**Files:** none (verification only)

- [ ] **Step 1: Full build**

```bash
.\build.ps1 -Run
```
Expected: app launches with 0 build errors.

- [ ] **Step 2: Configure**

In Settings, enter a real Hoppies pilot code (optional — not used for sending, just confirm it saves), enable "Enable Hoppies ACARS Messages", leave all six category toggles on.

- [ ] **Step 3: Exercise the flow**

Using MSFS (or `SimConnectStub` fallback mode if MSFS isn't running — confirm which mode is active from the connection banner), import a SimBrief OFP with a scheduled departure time in the near future via the ACARS page's existing SimBrief import. Confirm, in order, over the following real-time minutes and flight phases:
1. Departure advisory telex(es) fire immediately after import if the departure airport has any `hazard_zones.json` entry or a live METAR wind/visibility condition worth flagging (add a test entry to `hazard_zones.json` for your test departure ICAO beforehand to guarantee at least one fires).
2. Loadsheet telex fires at T-15min (or immediately if the OFP was imported past that point).
3. Pushback reminder fires at T-5min.
4. Airborne call fires on climb-out.
5. Arrival advisory telex(es) fire at top of descent.
6. Landing call fires on touchdown.
7. Post-flight summary fires on engine shutdown.

Confirm each telex actually appears in whatever real Hoppies-compatible ACARS client/addon you have configured with the matching callsign, AND appears in the in-app "Hoppies ACARS Log" card on the ACARS page.

- [ ] **Step 4: Restart-safety check**

Mid-flight, after the loadsheet has fired, close and relaunch the app. Confirm the loadsheet does NOT fire a second time (check `%AppData%\AviatesAirTracker\hoppies_state.json` shows `LoadsheetSent: true` before the restart).

- [ ] **Step 5: Report results**

No further steps — this task is manual verification only. Report back on which of the 7 checks in Step 3 passed, and note any that didn't for follow-up debugging before considering this feature complete.

---

## Plan Self-Review

**Spec coverage:** every section of the approved spec maps to a task — Settings (Task 10), loadsheet + MAC (Tasks 4, 8, 9), timing triggers (Task 9, reusing `TakeoffPerformanceService`), airborne/landing/summary calls (Task 9), weather/hazard advisories (Tasks 6, 7, 9), callsign fix (Task 2), in-app log (Task 12), restart-safety persistence (Task 9's `SaveState`/`hoppies_state.json`). The one spec item NOT implemented — the takeoff-speeds telex — is explicitly called out in the Goal/Scope note above as descoped to a future project, per your decision during planning; it is not silently dropped.

**Placeholder scan:** no TBD/TODO markers. Two spots are flagged as needing an on-the-spot judgment call during implementation rather than a fixed answer, both clearly bounded (not open-ended): the exact `form-toggle`/`--danger` CSS class/variable names in Task 10/12 (verify against `app.css`'s actual existing names — a mechanical lookup, not a design decision) and the exact location of the "force singleton construction at startup" block in Task 11 (follow the existing `AcarsPositionService` precedent already in the file).

**Type consistency:** `HoppiesLogEntry`, `HoppiesFlightState`, `HazardZoneEntry` (Task 3) are used identically in Tasks 9 and 12. `WeightBalanceResult`/`WeightBalanceDatabase.Compute` (Task 4) signature matches its call site in Task 8. `HoppiesMessageComposer`'s method signatures (Task 8) match their call sites in Task 9 exactly (parameter order/types cross-checked). `Subsidiary.StripIcaoPrefix` (Task 2) matches its call in Task 9.
