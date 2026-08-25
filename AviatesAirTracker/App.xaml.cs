using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Hosting;
using AviatesAirTracker.Models;
using AviatesAirTracker.Platform;
using AviatesAirTracker.Services;
using AviatesAirTracker.Services.Efb;
using AviatesAirTracker.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Serilog;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace AviatesAirTracker;

public partial class App : Application
{
    private ServiceProvider _serviceProvider = null!;
    private ILegacyUiHost? _blazorHost;
    private bool _shutdownRan;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>Root URL of the loopback Blazor Server host, once started. ShellWindow navigates to this.</summary>
    public static string? LegacyUiUrl { get; private set; }

    /// <summary>
    /// The one and only place a native page reaches into DI from outside constructor
    /// injection. <c>Frame.Navigate(Type)</c> constructs the target page via its parameterless
    /// constructor -- it has no way to run this app's constructor-injection DI the way
    /// <c>ShellWindow</c> itself gets built -- so a native page's parameterless constructor
    /// calls this instead. Don't scatter <c>Application.Current</c> casts elsewhere; route
    /// everything through here.
    /// </summary>
    public static T GetService<T>() where T : class =>
        ((App)Application.Current)._serviceProvider.GetRequiredService<T>();

    /// <summary>
    /// Tears down the loopback UI host. Called from <c>ShellWindow</c>'s Closing handler so
    /// teardown runs while the dispatcher is still pumping; <see cref="Shutdown"/> repeats the call
    /// as a safety net and <c>BlazorHostServer.DisposeAsync</c> makes the second one a no-op.
    /// </summary>
    internal static ValueTask ShutdownUiHostAsync() =>
        (Current as App)?._blazorHost?.DisposeAsync() ?? ValueTask.CompletedTask;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    /// <summary>
    /// Reports a fatal startup failure before any window exists to own a dialog. WinUI 3 has
    /// no synchronous, window-less message box (WPF's <c>MessageBox.Show</c> is gone along with
    /// the rest of PresentationFramework), so this goes straight to the Win32 API instead.
    /// </summary>
    private static void ShowFatalStartupMessage(string message, string title)
    {
        const uint MB_OK = 0x00000000;
        const uint MB_ICONERROR = 0x00000010;
        const uint MB_TOPMOST = 0x00040000;
        MessageBoxW(nint.Zero, message, title, MB_OK | MB_ICONERROR | MB_TOPMOST);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Global crash handlers — capture any exception before the window appears
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            DumpCrash("AppDomain", ex.ExceptionObject as Exception);
        UnhandledException += (_, ex) =>
        {
            DumpCrash("UnhandledException", ex.Exception);
            ex.Handled = false;
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
            DumpCrash("TaskScheduler", ex.Exception);

        try
        {
            // MINOR-07: Was a relative path — UnauthorizedAccessException when installed to Program Files.
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AviatesAirTracker", "logs");
            Directory.CreateDirectory(logDir);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(Path.Combine(logDir, "aviates_.log"), rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
            Log.Information("=== Aviates Air Flight Tracker v1.0 Starting ===");

            var services = new ServiceCollection();
            ConfigureServices(services);

            _serviceProvider = services.BuildServiceProvider();

            // Flight plan is session-only: drop any plan persisted by a previous run.
            _ = _serviceProvider.GetRequiredService<SettingsService>().ClearSimBriefPlanAsync();

            _serviceProvider.GetRequiredService<DiscordPresenceService>().Initialize();

            // Resolve to subscribe to TelemetryUpdated (constructor wires the event)
            _serviceProvider.GetRequiredService<AcarsPositionService>();

            // Resolve to subscribe to SimBrief/phase/loadsheet events (constructor wires them all)
            _serviceProvider.GetRequiredService<HoppiesAcarsOrchestrator>();

            // Resolve to subscribe to TelemetryUpdated and PttStateChanged (constructor wires both events)
            _serviceProvider.GetRequiredService<DispatchRadioService>();

            // Retry any PIREPs that failed or couldn't submit last session
            _ = RetryPendingPirepsAsync(_serviceProvider);
            _ = SyncPirepHistoryAsync(_serviceProvider);

            // When a booked flight completes, advance the pilot's current airport to the
            // booking destination so the Routes page only shows departures from there next time.
            var sessionMgr  = _serviceProvider.GetRequiredService<FlightSessionManager>();
            var bookingSvc  = _serviceProvider.GetRequiredService<BookingService>();
            var settingsSvc = _serviceProvider.GetRequiredService<SettingsService>();
            sessionMgr.FlightCompleted += (_, _) =>
            {
                var active = bookingSvc.ActiveBooking;
                if (active != null && !string.IsNullOrEmpty(active.DestIata))
                {
                    _ = bookingSvc.SetPositionAsync(active.DestIata);
                    bookingSvc.SetActiveBooking(null);
                    Log.Information("[Location] Pilot position advanced to {IATA}", active.DestIata);
                }
            };

            // Schedule update check 8 seconds after startup so it doesn't delay the UI
            var updateSvc = _serviceProvider.GetRequiredService<UpdateService>();
            _ = Task.Run(async () =>
            {
                await Task.Delay(8000);
                await updateSvc.CheckAsync();
            });

            // EFB companion server — resolving it wires the telemetry bridge; auto-start
            // hosts the LAN tablet server if enabled in settings. Failures are non-fatal.
            var efbServer = _serviceProvider.GetRequiredService<EfbServer>();
            if (settingsSvc.Settings.EfbEnabled && settingsSvc.Settings.EfbAutoStart)
                _ = efbServer.StartAsync();

            // Legacy Razor UI host — serves the not-yet-migrated pages to ShellWindow's WebView2
            // over loopback. Transitional; deleted when the last page becomes native XAML.
            //
            // LOAD-BEARING. The WebView2 renders nothing but this host's output, so a failure
            // here is not survivable: continuing would leave the pilot staring at an empty
            // window with the reason buried in a log file. Say so and stop instead.
            try
            {
                var blazorHost = LegacyUiHostLoader.Load();
                LegacyUiUrl = blazorHost.StartAsync(_serviceProvider).GetAwaiter().GetResult();
                Log.Information("[Startup] UI host ready at {Url}", LegacyUiUrl);
                _blazorHost = blazorHost;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Startup] UI host failed to start — cannot continue");
                ShowFatalStartupMessage(
                    "Aviates Air Tracker could not start the local host that renders its user " +
                    "interface, so there is nothing to display.\n\n" +
                    $"{ex.GetType().Name}: {ex.Message}\n\n" +
                    $"Details were written to:\n{logDir}\n\nThe application will now close.",
                    "Aviates Air Tracker — startup failed");
                // Unlike WPF's Shutdown(), WinUI has no dispatcher-deferred shutdown that runs
                // OnExit for us — so cleanup is run explicitly before exiting, and returning
                // here is what actually prevents ShellWindow from being created/activated.
                Shutdown();
                Exit();
                return;
            }

            var shell = _serviceProvider.GetRequiredService<ShellWindow>();
            shell.Activate();

#if DEBUG
            // Dev aid (debug builds only): `AviatesAirTracker.exe --debug-landing` fires a
            // synthetic landing result 12s after startup so the touchdown report modal can be
            // tested without flying. Values mimic a low-score landing.
            if (Environment.GetCommandLineArgs().Contains("--debug-landing"))
            {
                var alertSvc = _serviceProvider.GetRequiredService<AlertService>();
                _ = Task.Run(async () =>
                {
                    await Task.Delay(12000);
                    _serviceProvider.GetRequiredService<Platform.IUiDispatcher>().BeginInvoke(() =>
                    {
                        Log.Information("[TempDebug] Firing synthetic landing result");
                        alertSvc.ShowLandingResult(new LandingResult
                        {
                            Timestamp = DateTime.UtcNow,
                            VerticalSpeedFPM = -1,
                            GroundSpeedKts = 1,
                            IASKts = 0,
                            TouchdownPitchDeg = 0.2,
                            TouchdownBankDeg = 0.2,
                            HeadwindComponent = -1,
                            CrosswindComponent = 11,
                            LandingScore = 58,
                            ScoreBreakdown = new LandingScoreBreakdown
                            {
                                VerticalSpeedScore = 30, PitchScore = 8, BankScore = 15,
                                SpeedScore = 0, CrosswindScore = 3, StabilityScore = 2,
                                TotalScore = 58
                            },
                            RunwayIdentifier = "RWY??",
                            AirportICAO = "????"
                        });
                        Log.Information("[TempDebug] Synthetic landing dispatched");
                    });
                });
            }
#endif
        }
        catch (Exception ex)
        {
            DumpCrash("OnLaunched", ex);
            throw;
        }
    }

    private static void DumpCrash(string source, Exception? ex)
    {
        try
        {
            // MINOR-07: Use AppData path — AppContext.BaseDirectory may be read-only (Program Files).
            var crashDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AviatesAirTracker");
            Directory.CreateDirectory(crashDir);
            var path = Path.Combine(crashDir, "crash.log");
            var msg  = $"[{DateTime.Now:HH:mm:ss}] [{source}] {ex}\n{new string('-', 80)}\n";
            File.AppendAllText(path, msg);
            // Also try Serilog in case it's initialized
            try { Log.Fatal(ex, "[Crash/{Source}] Unhandled exception", source); } catch { }
        }
        catch { /* last resort — can't log the logger */ }
    }

    private static async Task RetryPendingPirepsAsync(IServiceProvider sp)
    {
        try
        {
            var flights  = sp.GetRequiredService<IFlightRepository>();
            var backend  = sp.GetRequiredService<AviatesBackendClient>();
            var settings = sp.GetRequiredService<SettingsService>();

            var key = settings.Settings.AcarsKey.Trim();
            if (string.IsNullOrEmpty(key)) return;

            var all     = await flights.GetAllAsync();
            var pending = all.Where(f =>
                !f.SyncedToBackend &&
                f.Status == FlightStatus.Completed).ToList();

            if (pending.Count == 0) return;

            Log.Information("[Startup] Retrying {Count} unsynced PIREP(s)...", pending.Count);

            foreach (var flight in pending)
            {
                var ok = await backend.SubmitPirepAsync(flight, key);
                if (ok)
                {
                    flight.SyncedToBackend = true;
                    await flights.UpdateAsync(flight);
                    Log.Information("[Startup] Retry OK for flight {Id}", flight.Id);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Startup] PIREP retry scan failed (non-critical)");
        }
    }

    private static async Task SyncPirepHistoryAsync(IServiceProvider sp)
    {
        try
        {
            var settings = sp.GetRequiredService<SettingsService>();
            var key      = settings.Settings.AcarsKey.Trim();
            if (string.IsNullOrEmpty(key)) return;

            var backend = sp.GetRequiredService<AviatesBackendClient>();
            var flights = sp.GetRequiredService<IFlightRepository>();

            var remote = await backend.FetchMyPirepsAsync(key);
            if (remote.Count == 0) return;

            var local = await flights.GetAllAsync();
            int added = 0;

            foreach (var r in remote)
            {
                if (string.IsNullOrWhiteSpace(r.FlightNumber) ||
                    string.IsNullOrWhiteSpace(r.DepartureICAO) ||
                    string.IsNullOrWhiteSpace(r.ArrivalICAO))
                    continue;

                // Skip if already in local cache — match by flight number + block-out within 60 s
                var rBlockOut = r.BlockOutTime ?? r.SubmittedAt;
                bool exists = local.Any(f =>
                    f.FlightNumber == r.FlightNumber &&
                    Math.Abs((f.BlockOutTime - rBlockOut).TotalSeconds) < 60);
                if (exists) continue;

                var record = new FlightRecord
                {
                    FlightNumber     = r.FlightNumber,
                    Callsign         = r.Callsign,
                    DepartureICAO    = r.DepartureICAO,
                    ArrivalICAO      = r.ArrivalICAO,
                    AircraftType     = r.AircraftType,
                    BlockOutTime     = r.BlockOutTime ?? r.SubmittedAt,
                    BlockInTime      = r.BlockInTime  ?? r.SubmittedAt,
                    FuelUsedLbs      = r.FuelUsedLbs,
                    ActualDistanceNm = r.DistanceNm,
                    Status           = FlightStatus.Completed,
                    SyncedToBackend  = true,
                };

                if (r.LandingVsFpm != 0 || r.LandingScore > 0)
                {
                    record.PrimaryLanding = new LandingResult
                    {
                        VerticalSpeedFPM = r.LandingVsFpm,
                        LandingScore     = (int)Math.Round(Math.Clamp(r.LandingScore, 0.0, 100.0)),
                        AirportICAO      = r.ArrivalICAO,
                    };
                }

                await flights.SaveAsync(record);
                added++;
            }

            if (added > 0)
                Log.Information("[Startup] Synced {Count} historical PIREP(s) from Supabase", added);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Startup] PIREP history sync failed (non-critical)");
        }
    }

    private static void ConfigureServices(ServiceCollection services)
    {
        // Data layer — JSON-persisted (survive app restarts)
        services.AddSingleton<IFlightRepository, JsonFlightRepository>();
        services.AddSingleton<IPilotRepository, InMemoryPilotRepository>();
        services.AddSingleton<ILandingRepository, JsonLandingRepository>();
        services.AddSingleton<IFlightDeletionRepository, JsonFlightDeletionRepository>();
        services.AddSingleton<IMessageRepository, JsonMessageRepository>();
        services.AddSingleton<IFriendRepository, JsonFriendRepository>();
        // Infrastructure
        // Must be resolved on the UI thread — OnLaunched runs there and the container is
        // built there, so DispatcherQueue.GetForCurrentThread() resolves to the right queue.
        services.AddSingleton<IUiDispatcher>(_ =>
            new DispatcherQueueUiDispatcher(DispatcherQueue.GetForCurrentThread()));
        services.AddSingleton<FilePickerService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<AlertService>();
        // SimConnect
        services.AddSingleton<SimConnectManager>();
        services.AddSingleton<FlightPhaseDetector>();
        services.AddSingleton<TelemetryProcessor>();
        // Analytics
        services.AddSingleton<LandingAnalyzer>();
        services.AddSingleton<ApproachMonitor>();
        services.AddSingleton<FuelAnalyzer>();
        services.AddSingleton<RunwayDetector>();
        // Business
        services.AddSingleton<AviatesBackendClient>();
        services.AddSingleton<AcarsPositionService>();
        services.AddSingleton<HoppiesAcarsClient>();
        services.AddSingleton<HazardZoneService>();
        services.AddSingleton<WeatherAdvisoryService>();
        services.AddSingleton<HoppiesAcarsOrchestrator>();
        services.AddSingleton<OtherPilotsService>();
        services.AddSingleton<PttInputService>();
        services.AddSingleton<DispatchRadioService>();
        services.AddSingleton<MessagingService>();
        services.AddSingleton<RouteTracker>();
        services.AddSingleton<FlightSessionManager>();
        services.AddSingleton<DiscordPresenceService>();
        services.AddSingleton<SimBriefService>();
        services.AddSingleton<TakeoffPerformanceService>();
        services.AddSingleton<ExportService>();
        services.AddSingleton<PilotStatsService>();
        services.AddSingleton<EventsService>();
        services.AddSingleton<RoutesService>();
        services.AddSingleton<BookingService>();
        services.AddSingleton<FleetService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<FlightRecoveryService>();
        // EFB companion (virtual tablet) — embedded LAN web server + telemetry bridge
        services.AddSingleton<EfbStateService>();
        services.AddSingleton<EfbChecklistService>();
        services.AddSingleton<EfbManualsService>();
        services.AddSingleton<EfbAtisService>();
        services.AddSingleton<EfbServer>();
        // ViewModels
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<MapViewModel>();
        services.AddSingleton<LandingAnalysisViewModel>();
        services.AddSingleton<PilotHubViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ReplayViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<FleetViewModel>();
        services.AddSingleton<FlightsViewModel>();
        services.AddSingleton<RoutesViewModel>();
        // EventsViewModel (Task 1) wasn't registered when it was added -- required here so
        // EventsPage's Task 2 App.GetService<EventsViewModel>() call resolves.
        services.AddSingleton<EventsViewModel>();
        services.AddSingleton<ShellWindow>();
    }

    /// <summary>
    /// Hard ceiling on how long the process may take to disappear once <see cref="Shutdown"/>
    /// starts. Comfortably above the sum of the individual disposal timeouts below (EFB 5 s,
    /// PTT 2 s, Blazor host 5 s), which in practice all complete in milliseconds.
    /// </summary>
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Runs one disposal step, logging its name first so that if it hangs, the last
    /// <c>[Shutdown] step</c> line in the log names the culprit. Nothing here may throw:
    /// one service failing to dispose must not skip the rest.
    /// </summary>
    private void Step(string name, Action action)
    {
        Log.Debug("[Shutdown] step: {Step}", name);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { action(); }
        catch (Exception ex) { Log.Warning(ex, "[Shutdown] {Step} threw while disposing", name); }
        if (sw.ElapsedMilliseconds > 250)
            Log.Warning("[Shutdown] {Step} took {Ms} ms", name, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Arms a watchdog that terminates the process if it is still alive once
    /// <see cref="ShutdownBudget"/> has elapsed.
    /// </summary>
    /// <remarks>
    /// A disposal that blocks the dispatcher thread — a sync-over-async wait whose
    /// continuation is posted back to that same blocked thread, a native/IPC handle that
    /// never releases — leaves the window gone but the process resident in Task Manager
    /// forever, with no way for the user to recover but to kill it by hand. A try/catch
    /// does not help: a hang is not an exception.
    ///
    /// So the budget is enforced from outside <see cref="Shutdown"/>, on a thread that cannot
    /// itself be blocked by it. Serilog is flushed before the kill, and the last
    /// <c>[Shutdown] step</c> line written names the call that hung — the whole point of
    /// this being a bounded exit rather than a silent one is that it stays diagnosable.
    ///
    /// The watchdog is never disarmed. It is a background thread, so a healthy shutdown
    /// simply outruns it and it dies with the process; if it ever does fire, the process
    /// has by definition already failed to exit.
    /// </remarks>
    private static void ArmShutdownWatchdog()
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(ShutdownBudget);
            try
            {
                Log.Fatal("[Shutdown] Still alive {Sec}s after Shutdown began — a disposal is " +
                          "blocked (see the last '[Shutdown] step' line above). Forcing exit.",
                          ShutdownBudget.TotalSeconds);
                Log.CloseAndFlush();
            }
            catch { /* the log is the last thing we have; don't let it stop the kill */ }
            // TerminateProcess. Environment.Exit would run finalizers and ProcessExit
            // handlers on the way out — more code that could block, which is the one
            // thing this thread exists to rule out.
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        })
        {
            IsBackground = true,
            Name = "ShutdownWatchdog",
        };
        watchdog.Start();
    }

    /// <summary>
    /// Ordered teardown. Guarded because this can now genuinely be reached twice in one run:
    /// <see cref="ShellWindow"/>'s <c>Closed</c> handler calls it, and so does
    /// <see cref="UpdateService"/>'s restart-after-update path (mirroring the fatal-startup-failure
    /// pattern above of calling <see cref="Shutdown"/> before <c>Exit()</c>) — and it is not
    /// certain whether <c>Application.Exit()</c> itself also triggers <c>ShellWindow.OnClosed</c>.
    /// Without this guard a second call would re-run every <see cref="Step"/> against already-
    /// disposed singletons and dispose <see cref="_serviceProvider"/> a second time.
    /// </summary>
    public void Shutdown()
    {
        if (_shutdownRan) return;
        _shutdownRan = true;

        ArmShutdownWatchdog();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Step("EfbServer", () => _serviceProvider.GetService<EfbServer>()?.Dispose());
        Step("SimConnectManager", () => _serviceProvider.GetService<SimConnectManager>()?.Disconnect());
        Step("Discord", () => _serviceProvider.GetService<DiscordPresenceService>()?.Dispose());
        Step("AcarsPosition", () => _serviceProvider.GetService<AcarsPositionService>()?.Dispose());
        Step("Hoppies", () => _serviceProvider.GetService<HoppiesAcarsOrchestrator>()?.Dispose());
        Step("PttInput", () => _serviceProvider.GetService<PttInputService>()?.Dispose());
        Step("DispatchRadio", () => _serviceProvider.GetService<DispatchRadioService>()?.Dispose());
        Step("UpdateService", () => _serviceProvider.GetService<UpdateService>()?.Dispose());
        Step("TakeoffPerf", () => _serviceProvider.GetService<TakeoffPerformanceService>()?.Dispose());
        // Safety net only. ShellWindow's Closing handler normally tears the host down while the
        // dispatcher is still pumping; by here it has stopped, so a circuit teardown marshalling
        // through IUiDispatcher could not complete and would ride the 5 s timeout inside
        // DisposeAsync. Reached when the window close path is bypassed (e.g. a forced exit, or a
        // startup failure before ShellWindow appears). No-op if teardown already happened.
        Step("BlazorHost", () => _blazorHost?.DisposeAsync().AsTask().GetAwaiter().GetResult());

        Log.Information("[Shutdown] All services disposed in {Ms} ms — exiting", sw.ElapsedMilliseconds);
        Log.CloseAndFlush();
        _serviceProvider.Dispose();
    }
}
