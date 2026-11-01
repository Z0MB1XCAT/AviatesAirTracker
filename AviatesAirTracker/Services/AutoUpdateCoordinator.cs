using Serilog;

namespace AviatesAirTracker.Services;

// ============================================================
// AUTO UPDATE COORDINATOR
//
// Updates are mandatory: every pilot runs the current, patched build. This checks shortly after
// launch and every few hours after that (pilots leave the tracker open for days), and installs
// as soon as no flight is being tracked -- never mid-flight, where a restart would cut the
// recording short. If an update turns up during a flight it is installed the moment that flight
// ends. The shell shows a blocking "updating" overlay while it runs; there is no cancel.
// ============================================================

public sealed class AutoUpdateCoordinator : IDisposable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly UpdateService _updates;
    private readonly FlightSessionManager _session;
    private readonly System.Threading.Timer _timer;
    private int _installing;
    private volatile bool _waitingForFlightToEnd;

    public AutoUpdateCoordinator(UpdateService updates, FlightSessionManager session)
    {
        _updates = updates;
        _session = session;
        _timer = new System.Threading.Timer(_ => _ = CheckAndInstallAsync(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _session.SessionStateChanged += (_, state) =>
        {
            if (_waitingForFlightToEnd && state is FlightSessionState.Idle or FlightSessionState.Complete)
            {
                Log.Information("[Update] Flight finished -- installing the pending update");
                _ = InstallAsync();
            }
        };
    }

    public void Start() => _timer.Change(FirstCheckDelay, CheckInterval);

    private async Task CheckAndInstallAsync()
    {
        try
        {
            await _updates.CheckAsync();
            if (_updates.AvailableUpdate == null) return;

            if (_session.State != FlightSessionState.Idle)
            {
                if (!_waitingForFlightToEnd)
                    Log.Information("[Update] {Version} available -- will install when the current flight ends",
                        _updates.AvailableUpdate.Version);
                _waitingForFlightToEnd = true;
                return;
            }

            await InstallAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Update] Scheduled update check failed");
        }
    }

    private async Task InstallAsync()
    {
        if (_updates.AvailableUpdate == null) return;
        if (Interlocked.Exchange(ref _installing, 1) == 1) return;
        _waitingForFlightToEnd = false;
        try
        {
            Log.Information("[Update] Installing {Version}", _updates.AvailableUpdate.Version);
            await _updates.DownloadAndInstallAsync();
        }
        catch (Exception ex)
        {
            // UpdateService has already raised InstallFailed for the shell; the next scheduled
            // check tries again.
            Log.Error(ex, "[Update] Auto-install failed");
        }
        finally
        {
            Interlocked.Exchange(ref _installing, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}
