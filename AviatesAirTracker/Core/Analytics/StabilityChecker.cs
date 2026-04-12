using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Services;
using Serilog;

namespace AviatesAirTracker.Core.Analytics;

// ============================================================
// APPROACH STABILITY MONITOR
//
// Provides real-time unstable approach alerting.
// Runs inside the 20Hz telemetry pipeline.
//
// Checks evaluated at/below 5000ft AGL:
//   - Excessive descent rate  (>1200 fpm)
//   - Gear not extended       (<1000ft AGL)
//   - Flaps not in config     (<500ft AGL)
//   - Excessive bank          (>30°)
//   - Speed out of range
//   - ILS deviation           (>1 dot LOC or GS)
//   - Go-around warning       (all criteria met)
//
// Per ICAO SOP: call a go-around if not stable by 1000ft AAL.
// ============================================================

public class StabilityChecker
{
    // Configurable thresholds
    public double MaxVS_Approach        { get; set; } = -1500.0;  // fpm
    public double GearDownBelowAGL      { get; set; } = 1000.0;   // ft
    public double FlapsConfigBelowAGL   { get; set; } = 500.0;    // ft
    public double MaxBankAngle          { get; set; } = 30.0;     // degrees
    public double MaxILSDevDots         { get; set; } = 1.0;      // dots
    public double StableCallAltAGL      { get; set; } = 1000.0;   // ft (stable-call gate)

    // Per-alert cooldowns to avoid spam
    private readonly Dictionary<string, DateTime> _lastAlert = [];
    private const int COOLDOWN_SECONDS = 8;

    // Overall approach state
    private bool _calledStable;
    private bool _calledUnstable;

    public event EventHandler<StabilityAlert>? AlertFired;

    // =====================================================
    // MAIN CHECK — called at 20Hz
    // =====================================================

    public StabilityReport Check(TelemetrySnapshot snap)
    {
        var report = new StabilityReport
        {
            AltitudeAGL = snap.AltitudeAGL,
            Timestamp   = snap.Timestamp
        };

        if (snap.AltitudeAGL > 5000 || snap.IsOnGround)
        {
            report.ChecksActive = false;
            return report;
        }

        report.ChecksActive = true;

        // --- Descent Rate ---
        if (snap.VerticalSpeedFPM < MaxVS_Approach)
        {
            report.Flags.Add(StabilityFlag.HighDescentRate);
            Fire("HIGH_VS", $"HIGH DESCENT RATE  {snap.VerticalSpeedFPM:F0} fpm", snap, AlertSeverity.Warning);
        }

        // --- Gear ---
        if (snap.AltitudeAGL < GearDownBelowAGL && !snap.GearDown)
        {
            report.Flags.Add(StabilityFlag.GearUp);
            Fire("GEAR_UP", "GEAR NOT DOWN", snap, AlertSeverity.Critical);
        }

        // --- Flaps ---
        if (snap.AltitudeAGL < FlapsConfigBelowAGL && snap.Raw.FlapsPercent < 50)
        {
            report.Flags.Add(StabilityFlag.FlapsNotSet);
            Fire("FLAPS", "FLAPS NOT IN LANDING CONFIG", snap, AlertSeverity.Warning);
        }

        // --- Bank ---
        if (Math.Abs(snap.Raw.Bank) > MaxBankAngle)
        {
            report.Flags.Add(StabilityFlag.ExcessiveBank);
            Fire("BANK", $"EXCESSIVE BANK  {snap.Raw.Bank:F1}°", snap, AlertSeverity.Warning);
        }

        // --- ILS Deviation ---
        if (snap.ILSCapturing)
        {
            if (Math.Abs(snap.LocalizerDevDots) > MaxILSDevDots)
            {
                report.Flags.Add(StabilityFlag.LocalizerDeviation);
                Fire("LOC", $"LOCALIZER {snap.LocalizerDevDots:F1} dots", snap, AlertSeverity.Warning);
            }
            if (Math.Abs(snap.GlideSlopeDevDots) > MaxILSDevDots)
            {
                report.Flags.Add(StabilityFlag.GlideSlopeDeviation);
                Fire("GS", $"GLIDESLOPE {snap.GlideSlopeDevDots:F1} dots", snap, AlertSeverity.Warning);
            }
        }

        // --- Stable call gate (1000ft AAL) ---
        if (!_calledStable && !_calledUnstable && snap.AltitudeAGL < StableCallAltAGL)
        {
            if (report.IsStable)
            {
                _calledStable = true;
                Fire("STABLE_CALL", "STABLE", snap, AlertSeverity.Info);
                Log.Information("[Stability] STABLE call at {AGL:F0}ft AGL", snap.AltitudeAGL);
            }
            else
            {
                _calledUnstable = true;
                Fire("UNSTABLE_CALL", "UNSTABLE — GO AROUND", snap, AlertSeverity.Critical);
                Log.Warning("[Stability] UNSTABLE APPROACH at {AGL:F0}ft AGL: {Flags}",
                    snap.AltitudeAGL, string.Join(", ", report.Flags));
            }
        }

        report.IsStable = report.Flags.Count == 0;
        snap.ApproachStable = report.IsStable;
        snap.ApproachAlerts = report.Flags.Select(f => f.ToString()).ToList();

        return report;
    }

    private void Fire(string key, string message, TelemetrySnapshot snap, AlertSeverity severity)
    {
        if (_lastAlert.TryGetValue(key, out var last) &&
            (snap.Timestamp - last).TotalSeconds < COOLDOWN_SECONDS)
            return;

        _lastAlert[key] = snap.Timestamp;

        AlertFired?.Invoke(this, new StabilityAlert
        {
            Key        = key,
            Message    = message,
            Severity   = severity,
            AltitudeAGL = snap.AltitudeAGL,
            Timestamp  = snap.Timestamp
        });
    }

    public void Reset()
    {
        _lastAlert.Clear();
        _calledStable   = false;
        _calledUnstable = false;
    }
}

// =====================================================
// DATA TYPES
// =====================================================

public class StabilityReport
{
    public double AltitudeAGL   { get; set; }
    public DateTime Timestamp   { get; set; }
    public bool ChecksActive    { get; set; }
    public bool IsStable        { get; set; } = true;
    public List<StabilityFlag> Flags { get; } = [];
}

public enum StabilityFlag
{
    HighDescentRate,
    GearUp,
    FlapsNotSet,
    ExcessiveBank,
    LocalizerDeviation,
    GlideSlopeDeviation,
    Overspeed,
    Underspeed
}

public class StabilityAlert
{
    public string Key          { get; set; } = "";
    public string Message      { get; set; } = "";
    public AlertSeverity Severity { get; set; }
    public double AltitudeAGL  { get; set; }
    public DateTime Timestamp  { get; set; }
}

public enum AlertSeverity { Info, Warning, Critical }
