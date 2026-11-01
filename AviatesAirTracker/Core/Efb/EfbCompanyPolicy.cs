namespace AviatesAirTracker.Core.Efb;

// ============================================================
// COMPANY FLAP POLICY — the operational restrictions AviatesAir publishes as Company NOTAMs
// (Manuals/cn-002, cn-003), applied wherever the tools make a flap decision:
//   • EFB Departure/Arrival Perf offer only the permitted detents, OPT never picks a
//     prohibited one, and a prohibited request is flagged.
//   • LandingComplianceChecker judges "landing configuration by 1000 ft" against the
//     company minimum (so a legal minimum-flap landing is never held for review).
//
//   CN-002  Boeing 737 NG / MAX — takeoff FLAPS 5 minimum (FLAPS 1 prohibited: tail strike).
//   CN-003  Boeing 737 NG / MAX — landing FLAPS 25 minimum, FLAPS 30 standard.
//           Airbus A320 family  — landing CONF 3 minimum, CONF FULL standard.
//
// Change the NOTAMs and this file together.
// ============================================================

public sealed record CompanyFlapPolicy(
    string Family,
    IReadOnlyList<string> ProhibitedTakeoffFlaps,
    string? TakeoffNotice,
    string? MinLandingFlap,
    string StandardLandingFlap,
    string? LandingNotice,
    /// <summary>SimConnect trailing-edge flap % that the company minimum landing flap reaches.</summary>
    double MinLandingFlapPercent);

public static class EfbCompanyPolicy
{
    /// <summary>Flap % treated as "landing configuration" when no company minimum applies.</summary>
    public const double DefaultLandingFlapPercent = 70.0;

    private static readonly CompanyFlapPolicy B737 = new(
        "Boeing 737 NG / MAX",
        ProhibitedTakeoffFlaps: ["Flaps 1"],
        TakeoffNotice: "CN-002 · 737 takeoff FLAPS 5 minimum; FLAPS 1 is not permitted.",
        MinLandingFlap: "Flaps 25",
        StandardLandingFlap: "Flaps 30",
        LandingNotice: "CN-003 · 737 landing FLAPS 25 minimum, FLAPS 30 standard.",
        MinLandingFlapPercent: 60.0);        // F25 of 40 ≈ 62 %

    private static readonly CompanyFlapPolicy A320Family = new(
        "Airbus A320 family",
        ProhibitedTakeoffFlaps: [],
        TakeoffNotice: null,
        MinLandingFlap: "Config 3",
        StandardLandingFlap: "Config Full",
        LandingNotice: "CN-003 · A320 family landing CONF 3 minimum, CONF FULL standard.",
        MinLandingFlapPercent: 55.0);        // CONF 3 (20°) of FULL (35°) ≈ 57 %

    public static CompanyFlapPolicy? For(string? aircraftType)
    {
        var t = new string((aircraftType ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (t.Length == 0) return null;
        if (t.StartsWith("B73") || t is "B37M" or "B38M" or "B39M" or "B3XM" || t.Contains("737")) return B737;
        if (t is "A318" or "A319" or "A320" or "A321" or "A19N" or "A20N" or "A21N" or "A21X"
            || t.StartsWith("A320") || t.StartsWith("A319") || t.StartsWith("A321")) return A320Family;
        return null;
    }

    public static IReadOnlyList<FlapSetting> TakeoffFlaps(EfbAircraftProfile p)
    {
        var pol = For(p.Code);
        return pol == null ? p.TakeoffFlaps
            : p.TakeoffFlaps.Where(f => !pol.ProhibitedTakeoffFlaps.Contains(f.Label, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public static bool IsTakeoffFlapProhibited(EfbAircraftProfile p, string? label) =>
        For(p.Code) is { } pol && label != null && pol.ProhibitedTakeoffFlaps.Contains(label.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Landing detents at or above the company minimum (profiles list detents in increasing order).</summary>
    public static IReadOnlyList<FlapSetting> LandingFlaps(EfbAircraftProfile p)
    {
        var pol = For(p.Code);
        if (pol?.MinLandingFlap == null) return p.LandingFlaps;
        int i = p.LandingFlaps.ToList().FindIndex(f => string.Equals(f.Label, pol.MinLandingFlap, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? p.LandingFlaps : p.LandingFlaps.Skip(i).ToList();
    }

    public static bool IsLandingFlapBelowMinimum(EfbAircraftProfile p, string? label) =>
        label != null && p.LandingFlaps.Any(f => string.Equals(f.Label, label.Trim(), StringComparison.OrdinalIgnoreCase))
        && !LandingFlaps(p).Any(f => string.Equals(f.Label, label.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The company's normal landing detent; else the profile's reference detent.</summary>
    public static string StandardLandingFlap(EfbAircraftProfile p) =>
        For(p.Code)?.StandardLandingFlap is { } s && p.LandingFlaps.Any(f => f.Label == s)
            ? s
            : p.LandingFlaps.OrderBy(f => Math.Abs(f.DistanceFactor - 1.0)).First().Label;

    public static double MinLandingFlapPercent(string? aircraftType) =>
        For(aircraftType)?.MinLandingFlapPercent ?? DefaultLandingFlapPercent;

    public static IReadOnlyList<string> Notices(string? aircraftType) =>
        For(aircraftType) is { } pol
            ? new[] { pol.TakeoffNotice, pol.LandingNotice }.Where(n => n != null).Select(n => n!).ToList()
            : [];
}
