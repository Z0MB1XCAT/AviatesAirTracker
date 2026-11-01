using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Core.Efb;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
using Serilog;

namespace AviatesAirTracker.Services.Efb;

// ============================================================
// EFB STATE SERVICE
//
// The single source of truth the EFB tablet reads from. Subscribes
// to the live telemetry pipeline, the SimBrief plan and the active
// booking, folds them into a flat EfbSnapshot, and raises a
// throttled SnapshotUpdated event the WebSocket hub broadcasts.
//
// Nothing here touches SimConnect directly — it is a pure consumer
// of FlightSessionManager, so it works identically with the live
// sim or the SimConnectStub fallback.
// ============================================================

public sealed class EfbStateService : IDisposable
{
    private readonly FlightSessionManager _session;
    private readonly SimBriefService _simBrief;
    private readonly BookingService _booking;
    private readonly SettingsService _settings;

    private readonly object _gate = new();
    private EfbSnapshot _current = new();
    private DateTime _lastTelemetryUtc = DateTime.MinValue;
    private DateTime _lastBroadcastUtc = DateTime.MinValue;

    // A flight plan imported from *inside* the EFB. It is used only for the EFB's
    // own display/perf and is NEVER pushed back into the ACARS/SimBrief pipeline.
    // The ACARS-imported plan (SimBriefService.CurrentPlan) always takes priority.
    private SimBriefFlightPlan? _efbLocalPlan;

    /// <summary>Minimum spacing between broadcasts — telemetry arrives at 20 Hz, EFB needs ~4 Hz.</summary>
    private static readonly TimeSpan BroadcastInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Raised (throttled) whenever a fresh snapshot is available to push.</summary>
    public event EventHandler<EfbSnapshot>? SnapshotUpdated;

    public EfbStateService(
        FlightSessionManager session,
        SimBriefService simBrief,
        BookingService booking,
        SettingsService settings)
    {
        _session = session;
        _simBrief = simBrief;
        _booking = booking;
        _settings = settings;

        _session.TelemetryUpdated += OnTelemetry;
        _session.SessionStateChanged += OnSessionStateChanged;
    }

    /// <summary>Latest snapshot — always safe to read, even before any telemetry.</summary>
    public EfbSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>The plan the EFB should use: the ACARS-imported plan wins, else the EFB-local one.</summary>
    private SimBriefFlightPlan? EffectivePlan() => _simBrief.CurrentPlan ?? _efbLocalPlan;

    /// <summary>True when the effective plan came from an in-EFB import (no ACARS plan present).</summary>
    private bool UsingEfbLocalPlan => _simBrief.CurrentPlan == null && _efbLocalPlan != null;

    /// <summary>
    /// Stores a plan imported inside the EFB. Deliberately does NOT call
    /// SimBriefService.NotifyPlanLoaded, so it never affects ACARS, the takeoff
    /// briefing, Hoppies, or the desktop app. Rebuilds + pushes the snapshot.
    /// </summary>
    /// <summary>The ATC callsign of the plan in use (the desktop's SimBrief plan, else one imported in
    /// the EFB): its atc/callsign, else airline + flight number. Empty when there's no plan.</summary>
    public string PlanCallsign()
    {
        var p = EffectivePlan();
        return p == null ? "" : FirstNonEmpty(p.Callsign, p.FlightNumber);
    }

    public void SetEfbLocalPlan(SimBriefFlightPlan? plan)
    {
        _efbLocalPlan = plan;
        var snap = _session.LatestTelemetry;
        var built = snap != null ? Build(snap) : Current;
        lock (_gate) _current = built;
        SnapshotUpdated?.Invoke(this, built);
    }

    // ── Telemetry fold ──────────────────────────────────────

    private void OnTelemetry(object? sender, TelemetrySnapshot snap)
    {
        var built = Build(snap);
        bool broadcast;
        lock (_gate)
        {
            _current = built;
            _lastTelemetryUtc = DateTime.UtcNow;
            broadcast = DateTime.UtcNow - _lastBroadcastUtc >= BroadcastInterval;
            if (broadcast) _lastBroadcastUtc = DateTime.UtcNow;
        }
        if (broadcast)
            SnapshotUpdated?.Invoke(this, built);
    }

    private void OnSessionStateChanged(object? sender, FlightSessionState state)
    {
        // State transitions are rare — always rebuild and push so the EFB
        // reflects e.g. "Idle → PreFlight" immediately even between telemetry ticks.
        var snap = _session.LatestTelemetry;
        var built = snap != null ? Build(snap) : Current;
        built.SessionState = state.ToString();
        built.HasActiveFlight = state is not (FlightSessionState.Idle or FlightSessionState.Complete);
        lock (_gate) _current = built;
        SnapshotUpdated?.Invoke(this, built);
    }

    private EfbSnapshot Build(TelemetrySnapshot snap)
    {
        var plan = EffectivePlan();
        var booking = _booking.ActiveBooking;
        var flight = _session.CurrentFlight;

        var s = new EfbSnapshot
        {
            ServerTimeUtc = DateTime.UtcNow,
            SimConnected = true,
            HasActiveFlight = _session.State is not (FlightSessionState.Idle or FlightSessionState.Complete),
            SessionState = _session.State.ToString(),

            // Identity — booking first (authoritative), then flight record, then plan.
            Callsign = FirstNonEmpty(booking?.Callsign, flight?.Callsign, plan?.Callsign, plan?.FlightNumber),
            FlightNumber = FirstNonEmpty(booking?.RouteCallsign, flight?.FlightNumber, plan?.FlightNumber),
            Registration = FirstNonEmpty(booking?.Registration, plan?.AircraftRegistration),
            AircraftType = FirstNonEmpty(booking?.AircraftType, plan?.AircraftType, flight?.AircraftType),
            AircraftName = flight?.AircraftTitle ?? "",
            DepartureIcao = FirstNonEmpty(plan?.DepartureICAO, flight?.DepartureICAO),
            DepartureName = booking?.OriginName ?? "",
            ArrivalIcao = FirstNonEmpty(plan?.ArrivalICAO, flight?.ArrivalICAO),
            ArrivalName = booking?.DestName ?? "",
            AlternateIcao = plan?.AlternateICAO ?? "",

            // Live
            Latitude = snap.Latitude,
            Longitude = snap.Longitude,
            AltitudeFt = snap.AltitudePressure,
            AltitudeAglFt = snap.AltitudeAGL,
            IasKts = snap.IASKts,
            TasKts = snap.TASKts,
            GroundKts = snap.GroundSpeedKts,
            Mach = snap.Raw.Mach,
            HeadingDeg = snap.Raw.HeadingMagnetic,
            TrackDeg = snap.Raw.TrackTrue,
            VerticalSpeedFpm = snap.VerticalSpeedFPM,
            PitchDeg = snap.Raw.Pitch,
            BankDeg = snap.Raw.Bank,
            Phase = snap.Phase.ToString(),
            PhaseColor = PhaseColor(snap.Phase),

            // Fuel
            FuelLbs = snap.Raw.FuelTotalLbs,
            FuelPct = snap.Raw.MaxGrossWeight > 0
                ? Math.Clamp(snap.Raw.FuelTotalLbs / (snap.Raw.MaxGrossWeight * 0.4), 0, 1)
                : 0,
            FuelBurnPph = snap.FuelBurnRatePPH,

            // Config
            Gear = snap.GearDown ? "DN" : "UP",
            FlapsPct = snap.Raw.FlapsPercent,
            SpoilersPct = snap.Raw.SpoilerPercent,
            ApMaster = snap.AutopilotEngaged,
            ApModes = ApModes(snap),
            Squawk = ((int)snap.Raw.TransponderCode).ToString("D4"),

            // Weather
            WindDirDeg = snap.Raw.WindDirection,
            WindKts = snap.Raw.WindSpeed,
            HeadwindKts = snap.HeadwindComponent,
            CrosswindKts = snap.CrosswindComponent,
            OatC = snap.Raw.OAT,
            QnhMb = snap.Raw.SeaLevelPressure,

            // Plan-derived
            PlannedDistanceNm = plan?.PlannedDistanceNm ?? 0,
            CruiseAltitudeFt = plan?.CruiseAltitudeFt ?? 0,

            // Schedule + OOOI — the recorded times live on the flight record
            ScheduledOutUtc = flight?.ScheduledDepUtc ?? booking?.ScheduledDepUtc ?? plan?.ScheduledDepartureUtc,
            ScheduledInUtc = plan?.ScheduledArrivalUtc,
            BlockOutUtc = Recorded(flight?.BlockOutTime),
            TakeoffUtc = Recorded(flight?.TakeoffTime),
            LandingUtc = Recorded(flight?.LandingTime),
            BlockInUtc = Recorded(flight?.BlockInTime),
        };

        ComputeProgress(s, snap, plan);
        return s;
    }

    /// <summary>FlightRecord keeps unset times as DateTime.MinValue; the wire uses null.</summary>
    private static DateTime? Recorded(DateTime? t) =>
        t is { } v && v > DateTime.MinValue.AddDays(1) ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    /// <summary>Fills distance-remaining, progress %, ETE and ETA from the current position.</summary>
    private static void ComputeProgress(EfbSnapshot s, TelemetrySnapshot snap, SimBriefFlightPlan? plan)
    {
        if (plan == null || plan.ArrivalLat == 0 && plan.ArrivalLon == 0) return;
        if (snap.Latitude == 0 && snap.Longitude == 0) return;

        double remaining = GeoMath.DistanceNm(snap.Latitude, snap.Longitude, plan.ArrivalLat, plan.ArrivalLon);
        s.DistanceRemainingNm = Math.Round(remaining, 1);
        s.BearingToDestDeg = Math.Round(GeoMath.InitialBearingDeg(snap.Latitude, snap.Longitude, plan.ArrivalLat, plan.ArrivalLon));

        if (plan.PlannedDistanceNm > 1)
            s.ProgressPct = Math.Clamp(100.0 * (1.0 - remaining / plan.PlannedDistanceNm), 0, 100);

        double gs = snap.GroundSpeedKts;
        if (gs > 40 && remaining > 0.5)
        {
            double hours = remaining / gs;
            s.EteMinutes = (int)Math.Round(hours * 60);
            s.EtaUtc = DateTime.UtcNow.AddHours(hours);
        }

        ComputeNextWaypoint(s, snap, plan, gs);
        ComputeDescentAndFuel(s, snap, plan, remaining, gs);
    }

    /// <summary>Next waypoint = the one after the nearest, once the aircraft is closer to
    /// that following fix than the nearest fix itself is (i.e. the nearest is behind it).</summary>
    private static void ComputeNextWaypoint(EfbSnapshot s, TelemetrySnapshot snap, SimBriefFlightPlan plan, double gs)
    {
        var wps = plan.Waypoints.Where(w => w.Latitude != 0 || w.Longitude != 0).ToList();
        if (wps.Count < 2) return;
        int near = 0; double nearD = double.MaxValue;
        for (int i = 0; i < wps.Count; i++)
        {
            double d = GeoMath.DistanceNm(snap.Latitude, snap.Longitude, wps[i].Latitude, wps[i].Longitude);
            if (d < nearD) { nearD = d; near = i; }
        }
        int next = near;
        if (near < wps.Count - 1)
        {
            double legLen = GeoMath.DistanceNm(wps[near].Latitude, wps[near].Longitude, wps[near + 1].Latitude, wps[near + 1].Longitude);
            double toFollowing = GeoMath.DistanceNm(snap.Latitude, snap.Longitude, wps[near + 1].Latitude, wps[near + 1].Longitude);
            if (toFollowing < legLen) next = near + 1;
        }
        var wp = wps[next];
        s.NextWaypoint = wp.Identifier;
        s.NextWaypointNm = Math.Round(GeoMath.DistanceNm(snap.Latitude, snap.Longitude, wp.Latitude, wp.Longitude), 1);
        if (gs > 40) s.NextWaypointEtaUtc = DateTime.UtcNow.AddHours(s.NextWaypointNm / gs);
    }

    /// <summary>
    /// EFB-exclusive advisor: top-of-descent and predicted fuel on arrival.
    ///
    /// TOD uses the classic 3:1 rule — 3 nm per 1,000 ft to lose — plus a
    /// 10 nm deceleration/approach allowance. Fuel-on-arrival extrapolates the
    /// current burn rate over the remaining time.
    /// </summary>
    private static void ComputeDescentAndFuel(
        EfbSnapshot s, TelemetrySnapshot snap, SimBriefFlightPlan plan, double remainingNm, double gs)
    {
        // ── Descent ──
        double fieldElev = plan.ArrivalElevationFt;
        double toLose = snap.AltitudePressure - fieldElev;
        if (toLose > 500 && remainingNm > 0)
        {
            double descentNm = toLose / 1000.0 * 3.0 + 10.0;   // 3:1 + 10 nm approach allowance
            double todDist = remainingNm - descentNm;
            s.DescendNow = todDist <= 0;
            s.TodDistanceNm = Math.Round(Math.Max(0, todDist), 1);
            if (gs > 40 && todDist > 0) s.TodMinutes = (int)Math.Round(todDist / gs * 60);
            // Gradient needed to reach the field from here
            s.RequiredDescentFtPerNm = Math.Round(toLose / Math.Max(remainingNm, 1), 0);
        }

        // ── Fuel on arrival ──
        s.PlannedLandingFuelLbs = Math.Round(plan.LandingFuelLbs, 0);
        double burnPph = snap.FuelBurnRatePPH;
        if (gs > 40 && burnPph > 0 && remainingNm > 0)
        {
            double hoursLeft = remainingNm / gs;
            double predicted = snap.Raw.FuelTotalLbs - burnPph * hoursLeft;
            s.PredictedLandingFuelLbs = Math.Round(Math.Max(0, predicted), 0);

            // Compare against the OFP figure when we have one; otherwise use a
            // 45-minute-at-current-burn reserve as the yardstick.
            double reserve = plan.LandingFuelLbs > 0 ? plan.LandingFuelLbs : burnPph * 0.75;
            s.FuelStatus = predicted < reserve * 0.85 ? "LOW"
                         : predicted < reserve ? "CAUTION"
                         : "OK";
        }
    }

    // ── Flight plan projection ──────────────────────────────

    /// <summary>Briefing package for the Pilot Brief app (see <see cref="EfbBriefing"/>).</summary>
    public EfbBriefing GetBriefing()
    {
        var p = EffectivePlan();
        if (p == null) return new EfbBriefing { HasPlan = false };

        bool kg = p.OfpUnitsKg;
        int F(double lbs) => (int)Math.Round(kg ? lbs / 2.20462 : lbs);   // fuel is stored in lb
        int W(double kgs) => (int)Math.Round(kg ? kgs : kgs * 2.20462);   // weights are stored in kg
        var booking = _booking.ActiveBooking;

        return new EfbBriefing
        {
            HasPlan = true,
            HasPdf = !string.IsNullOrEmpty(p.PdfUrl),
            PdfUrl = p.PdfUrl,
            Units = kg ? "KGS" : "LBS",
            FlightNumber = p.FlightNumber,
            Callsign = !string.IsNullOrEmpty(p.Callsign) ? p.Callsign : booking?.Callsign ?? "",
            Registration = p.AircraftRegistration,
            AircraftType = p.AircraftType,
            AircraftName = p.AircraftName,
            DepartureIcao = p.DepartureICAO, DepartureName = p.DepartureName,
            ArrivalIcao = p.ArrivalICAO, ArrivalName = p.ArrivalName,
            AlternateIcao = p.AlternateICAO, AlternateName = p.AlternateName,
            DepartureRunway = p.DepartureRunway, ArrivalRunway = p.ArrivalRunway,
            Sid = p.SID, Star = p.STAR, Route = p.Route,
            CruiseAltitudeFt = p.CruiseAltitudeFt,
            CostIndex = p.CostIndex,
            DistanceNm = p.PlannedDistanceNm,
            AvgWindComponentKts = p.AvgWindComponentKts,
            ScheduledOutUtc = p.ScheduledDepartureUtc,
            ScheduledInUtc = p.ScheduledArrivalUtc,
            BlockTimeMin = p.BlockTimeMin,
            AirTimeMin = p.PlannedFlightTimeMin,
            GeneratedAtUtc = p.GeneratedAt,
            TaxiFuel = F(p.TaxiFuelLbs),
            TripFuel = F(p.TripFuelLbs),
            ContingencyFuel = F(p.ContingencyFuelLbs),
            AlternateFuel = F(p.AlternateFuelLbs),
            FinalReserveFuel = F(p.ReserveFuelLbs),
            ExtraFuel = F(p.ExtraFuelLbs),
            MinTakeoffFuel = F(p.MinTakeoffFuelLbs),
            PlanTakeoffFuel = F(p.PlanTakeoffFuelLbs),
            BlockFuel = F(p.TakeoffFuelLbs),        // plan_ramp
            LandingFuel = F(p.LandingFuelLbs),
            Zfw = W(p.ZeroFuelWeightKg), MaxZfw = W(p.MaxZeroFuelWeightKg),
            Tow = W(p.TakeoffWeightKg), MaxTow = W(p.MaxTakeoffWeightKg),
            Ldw = W(p.LandingWeightKg), MaxLdw = W(p.MaxLandingWeightKg),
            Payload = W(p.PayloadKg),
            PaxCount = p.PaxCount,
            Cargo = W(p.CargoKg),
            MacTowPercent = MacTow(p),
            MacZfwPercent = Wb(p) is { } wb ? Math.Round(wb.MacZfwPercent, 1) : null,
            DepartureMetar = p.DepartureMetar, DepartureTaf = p.DepartureTaf,
            ArrivalMetar = p.ArrivalMetar, ArrivalTaf = p.ArrivalTaf,
            AlternateMetar = p.AlternateMetar, AlternateTaf = p.AlternateTaf,
            Notams = p.NOTAMs,
            OfpHtml = p.OFPText,
        };
    }

    /// <summary>TOW %MAC from the same W&amp;B model the Hoppies loadsheet uses, so the EFB's
    /// "Sync Loadsheet" CG matches what ACARS sent. Tries the booked type, then the OFP's
    /// ICAO designator; null when neither is a seeded W&amp;B type (the pilot types it instead).</summary>
    private WeightBalanceResult? Wb(SimBriefFlightPlan p)
    {
        foreach (var type in new[] { _booking.ActiveBooking?.AircraftType, p.AircraftType })
        {
            if (string.IsNullOrWhiteSpace(type)) continue;
            var wb = WeightBalanceDatabase.Compute(type, p.ZeroFuelWeightKg, p.TakeoffWeightKg);
            if (wb != null) return wb;
        }
        return null;
    }
    private double? MacTow(SimBriefFlightPlan p) => Wb(p) is { } wb ? Math.Round(wb.MacTowPercent, 1) : null;

    public EfbFlightPlan GetFlightPlan()
    {
        var p = EffectivePlan();
        if (p == null) return new EfbFlightPlan { HasPlan = false };

        return new EfbFlightPlan
        {
            HasPlan = true,
            DepartureIcao = p.DepartureICAO,
            ArrivalIcao = p.ArrivalICAO,
            AlternateIcao = p.AlternateICAO,
            Route = p.Route,
            AircraftType = p.AircraftType,
            Registration = p.AircraftRegistration,
            CruiseAltitudeFt = p.CruiseAltitudeFt,
            CruiseMach = p.CruiseMach,
            CostIndex = p.CostIndex,
            PlannedDistanceNm = p.PlannedDistanceNm,
            PlannedFlightTimeMin = p.PlannedFlightTimeMin,
            RampFuelLbs = p.TakeoffFuelLbs,
            LandingFuelLbs = p.LandingFuelLbs,
            TakeoffWeightKg = p.TakeoffWeightKg,
            ZeroFuelWeightKg = p.ZeroFuelWeightKg,
            PaxCount = p.PaxCount,
            CargoKg = p.CargoKg,
            DepartureRunway = p.DepartureRunway,
            ArrivalRunway = p.ArrivalRunway,
            DepartureElevationFt = p.DepartureElevationFt,
            DepartureRunwayLengthM = p.DepartureRunwayLengthM,
            DepartureTransAltFt = p.DepartureTransitionAltFt,
            FlightNumber = p.FlightNumber,
            DepartureName = p.DepartureName,
            ArrivalName = p.ArrivalName,
            Sid = p.SID,
            Star = p.STAR,
            ScheduledArrivalUtc = p.ScheduledArrivalUtc,
            MacTowPercent = MacTow(p),
            ArrivalElevationFt = p.ArrivalElevationFt,
            ArrivalRunwayLengthM = p.ArrivalRunwayLengthM,
            DepartureMetar = p.DepartureMetar,
            ArrivalMetar = p.ArrivalMetar,
            ScheduledDepartureUtc = p.ScheduledDepartureUtc,
            PdfUrl = p.PdfUrl,
            FromEfbImport = UsingEfbLocalPlan,
            Waypoints = p.Waypoints.Select(w => new EfbWaypoint
            {
                Seq = w.SequenceNumber,
                Ident = w.Identifier,
                Type = w.Type,
                Latitude = w.Latitude,
                Longitude = w.Longitude,
                AltitudeFt = w.AltitudeConstraintFt ?? 0,
            }).ToList(),
        };
    }

    /// <summary>Marks the snapshot stale if no telemetry has arrived recently (sim not connected).</summary>
    public EfbSnapshot GetLiveSnapshot()
    {
        lock (_gate)
        {
            _current.SimConnected = DateTime.UtcNow - _lastTelemetryUtc < TimeSpan.FromSeconds(3);
            return _current;
        }
    }

    // ── Helpers ─────────────────────────────────────────────

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static string ApModes(TelemetrySnapshot snap)
    {
        if (!snap.AutopilotEngaged) return "OFF";
        var modes = new List<string>();
        if (snap.Raw.AutopilotAltitudeLock > 0.5) modes.Add("ALT");
        if (snap.Raw.AutopilotHeadingLock > 0.5) modes.Add("HDG");
        if (snap.Raw.AutopilotNAV1 > 0.5) modes.Add("NAV");
        if (snap.Raw.AutopilotAPPR > 0.5) modes.Add("APP");
        if (snap.Raw.AutopilotFLC > 0.5) modes.Add("FLC");
        return modes.Count > 0 ? string.Join(" ", modes) : "CMD";
    }

    private static string PhaseColor(FlightPhase phase) => phase switch
    {
        FlightPhase.Cruise => "#22C55E",
        FlightPhase.Approach or FlightPhase.FinalApproach => "#F97316",
        FlightPhase.Landing => "#EF4444",
        FlightPhase.Climb or FlightPhase.InitialClimb => "#4D9BEC",
        FlightPhase.Descent or FlightPhase.TopOfDescent => "#EAB308",
        FlightPhase.Takeoff => "#22D3EE",
        _ => "#4A5568"
    };

    public void Dispose()
    {
        _session.TelemetryUpdated -= OnTelemetry;
        _session.SessionStateChanged -= OnSessionStateChanged;
    }
}
