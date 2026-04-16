using AviatesAirTracker.Models;
using Serilog;

namespace AviatesAirTracker.Services;

// ============================================================
// TAKEOFF PERFORMANCE SERVICE
//
// Monitors scheduled departure time from the loaded SimBrief OFP
// and triggers a takeoff briefing exactly 15 minutes before ETD.
//
// Flow:
//   1. SimBriefService raises FlightPlanLoaded after an OFP is parsed.
//   2. TakeoffPerformanceService reads ScheduledDepartureUtc from the plan.
//   3. A one-shot System.Timers.Timer fires at (ETD − 15 min).
//   4. BriefingTriggered event is raised — consumed by TakeoffPerformanceModal.
//
// If the OFP has no departure time (ScheduledDepartureUtc is null), no
// timer is started and the user can open the briefing manually.
// ============================================================

public class TakeoffBriefingEventArgs(SimBriefFlightPlan plan) : EventArgs
{
    public SimBriefFlightPlan Plan { get; } = plan;
}

public class TakeoffPerformanceService : IDisposable
{
    private readonly SimBriefService _simBrief;

    private System.Timers.Timer? _timer;
    private bool _briefingTriggered;
    private bool _disposed;

    /// <summary>Raised on the timer thread 15 minutes before scheduled departure.</summary>
    public event EventHandler<TakeoffBriefingEventArgs>? BriefingTriggered;

    public TakeoffPerformanceService(SimBriefService simBrief)
    {
        _simBrief = simBrief;
        _simBrief.FlightPlanLoaded += OnFlightPlanLoaded;
    }

    // ── Public API ──────────────────────────────────────────

    /// <summary>
    /// Manually trigger the takeoff briefing (e.g. user taps "Open Perf Brief" button).
    /// Also called automatically by the timer.
    /// </summary>
    public void TriggerNow()
    {
        var plan = _simBrief.CurrentPlan;
        if (plan is null)
        {
            Log.Warning("[TakeoffPerf] TriggerNow called but no SimBrief plan is loaded.");
            return;
        }
        RaiseBriefing(plan);
    }

    // ── Internal ────────────────────────────────────────────

    private void OnFlightPlanLoaded(object? sender, SimBriefFlightPlan plan)
    {
        // Cancel any previous countdown for an older OFP
        ResetTimer();
        _briefingTriggered = false;

        if (plan.ScheduledDepartureUtc is null)
        {
            Log.Information("[TakeoffPerf] OFP has no scheduled departure time — auto-brief disabled.");
            return;
        }

        ScheduleTimer(plan.ScheduledDepartureUtc.Value, plan);
    }

    private void ScheduleTimer(DateTime depUtc, SimBriefFlightPlan plan)
    {
        var alertAt = depUtc.AddMinutes(-15);
        var delay   = alertAt - DateTime.UtcNow;

        if (delay.TotalSeconds <= 0)
        {
            Log.Information("[TakeoffPerf] Departure is within 15 min (or past) — triggering brief immediately.");
            // Slight delay so the UI is ready
            Task.Delay(500).ContinueWith(_ => RaiseBriefing(plan));
            return;
        }

        Log.Information("[TakeoffPerf] Takeoff brief scheduled in {Min:F0} min (at {At:HH:mm}Z)",
            delay.TotalMinutes, alertAt);

        _timer = new System.Timers.Timer(delay.TotalMilliseconds) { AutoReset = false };
        _timer.Elapsed += (_, _) => RaiseBriefing(plan);
        _timer.Start();
    }

    private void RaiseBriefing(SimBriefFlightPlan plan)
    {
        if (_briefingTriggered) return;
        _briefingTriggered = true;

        Log.Information("[TakeoffPerf] Raising takeoff briefing for {Dep}→{Arr}",
            plan.DepartureICAO, plan.ArrivalICAO);

        BriefingTriggered?.Invoke(this, new TakeoffBriefingEventArgs(plan));
    }

    private void ResetTimer()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetTimer();
        _simBrief.FlightPlanLoaded -= OnFlightPlanLoaded;
    }
}
