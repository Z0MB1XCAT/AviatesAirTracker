using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
using AviatesAirTracker.Services.Efb;
using AviatesAirTracker.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System.IO;
using System.Windows;

namespace AviatesAirTracker;

public partial class App : Application
{
    private ServiceProvider _serviceProvider = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Global crash handlers — capture any exception before the window appears
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            DumpCrash("AppDomain", ex.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, ex) =>
        {
            DumpCrash("Dispatcher", ex.Exception);
            ex.Handled = false;
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
            DumpCrash("TaskScheduler", ex.Exception);

        try
        {
            base.OnStartup(e);

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
            // Register Blazor WebView services (required for BlazorWebView in WPF)
            services.AddWpfBlazorWebView();
            ConfigureServices(services);

            _serviceProvider = services.BuildServiceProvider();

            // Flight plan is session-only: drop any plan persisted by a previous run.
            _ = _serviceProvider.GetRequiredService<SettingsService>().ClearSimBriefPlanAsync();

            // Make the service provider available to BlazorWebView via dynamic resource
            Resources["services"] = _serviceProvider;

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

            _serviceProvider.GetRequiredService<MainWindow>().Show();

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
                    await Dispatcher.InvokeAsync(() =>
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
            DumpCrash("OnStartup", ex);
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
        // Window
        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider.GetService<EfbServer>()?.Dispose();
        _serviceProvider.GetService<SimConnectManager>()?.Disconnect();
        _serviceProvider.GetService<DiscordPresenceService>()?.Dispose();
        _serviceProvider.GetService<AcarsPositionService>()?.Dispose();
        _serviceProvider.GetService<HoppiesAcarsOrchestrator>()?.Dispose();
        _serviceProvider.GetService<PttInputService>()?.Dispose();
        _serviceProvider.GetService<DispatchRadioService>()?.Dispose();
        _serviceProvider.GetService<UpdateService>()?.Dispose();
        _serviceProvider.GetService<TakeoffPerformanceService>()?.Dispose();
        Log.CloseAndFlush();
        _serviceProvider.Dispose();
        base.OnExit(e);
    }
}
