using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Models;
using Serilog;

namespace AviatesAirTracker.Services;

// ============================================================
// OTHER PILOTS SERVICE
//
// Polls the public live-positions endpoint every 30 seconds and
// raises an event with the latest snapshot. Mirrors MessagingService's
// timer-poll structure.
// ============================================================

public class OtherPilotsService : IDisposable
{
    private readonly AviatesBackendClient _backend;
    private readonly System.Threading.Timer _pollTimer;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private bool _disposed;

    public List<LivePilotPosition> LatestPositions { get; private set; } = new();

    public event EventHandler<List<LivePilotPosition>>? PositionsUpdated;

    public OtherPilotsService(AviatesBackendClient backend)
    {
        _backend = backend;

        _ = PollAsync();
        _pollTimer = new System.Threading.Timer(
            _ => { if (!_disposed) _ = PollAsync(); },
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
    }

    private async Task PollAsync()
    {
        if (!await _pollGate.WaitAsync(0)) return;
        try
        {
            var positions = await _backend.FetchLivePositionsAsync();
            LatestPositions = positions;
            PositionsUpdated?.Invoke(this, positions);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[OtherPilots] Poll error (non-critical)");
        }
        finally
        {
            _pollGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Dispose();
        _pollGate.Dispose();
    }
}
