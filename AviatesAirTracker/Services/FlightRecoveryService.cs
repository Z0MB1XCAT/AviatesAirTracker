using System.Linq;
using System.Threading.Tasks;
using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Models;
using Serilog;

namespace AviatesAirTracker.Services;

/// <summary>
/// Runs once at launch: finds an interrupted in-progress flight to offer for resume,
/// and prunes every persisted flight that never recorded a landing.
/// </summary>
public class FlightRecoveryService
{
    private readonly IFlightRepository _flights;

    public FlightRecoveryService(IFlightRepository flights) => _flights = flights;

    /// <summary>The interrupted flight offered for resume, or null. Set by DetectAsync.</summary>
    public FlightRecord? Pending { get; private set; }

    /// <summary>One-shot guard so the recovery modal shows once per launch.</summary>
    public bool Handled { get; set; }

    private bool _detected;

    /// <summary>Detect an interrupted flight and prune landing-less flights. Idempotent per launch.</summary>
    public async Task DetectAsync()
    {
        if (_detected) return;
        _detected = true;

        var inProgress = await _flights.GetInProgressAsync();
        Pending = inProgress.FirstOrDefault();   // already ordered newest-first

        // Delete every flight with no landing except the single recovery candidate.
        var removed = await _flights.PruneWithoutLandingAsync(Pending?.Id);
        if (removed > 0)
            Log.Information("[FlightRecovery] Pruned {Count} landing-less flight(s)", removed);
        if (Pending != null)
            Log.Information("[FlightRecovery] Interrupted flight available for resume: {Id}", Pending.Id);
    }

    /// <summary>Discard the pending recovery flight (user chose not to resume).</summary>
    public async Task DiscardAsync()
    {
        if (Pending != null) await _flights.DeleteAsync(Pending.Id);
        Pending = null;
        Handled = true;
    }
}
