using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
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
            ServiceLocator.Initialize(_serviceProvider);

            // Make the service provider available to BlazorWebView via dynamic resource
            Resources["services"] = _serviceProvider;

            _serviceProvider.GetRequiredService<DiscordPresenceService>().Initialize();

            // Resolve to subscribe to TelemetryUpdated (constructor wires the event)
            _serviceProvider.GetRequiredService<AcarsPositionService>();

            // Retry any PIREPs that failed or couldn't submit last session
            _ = RetryPendingPirepsAsync(_serviceProvider);

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
                    settingsSvc.Settings.CurrentAirportIata = active.DestIata;
                    settingsSvc.Save();
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

            _serviceProvider.GetRequiredService<MainWindow>().Show();
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

    private static void ConfigureServices(ServiceCollection services)
    {
        // Data layer — JSON-persisted (survive app restarts)
        services.AddSingleton<IFlightRepository, JsonFlightRepository>();
        services.AddSingleton<IPilotRepository, InMemoryPilotRepository>();
        services.AddSingleton<ILandingRepository, JsonLandingRepository>();
        services.AddSingleton<IFlightDeletionRepository, InMemoryFlightDeletionRepository>();
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
        // ViewModels
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<LiveFlightViewModel>();
        services.AddSingleton<MapViewModel>();
        services.AddSingleton<LandingAnalysisViewModel>();
        services.AddSingleton<StatisticsViewModel>();
        services.AddSingleton<PilotHubViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ReplayViewModel>();
        services.AddSingleton<TelemetryViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<FleetViewModel>();
        // Window
        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider.GetService<SimConnectManager>()?.Disconnect();
        _serviceProvider.GetService<DiscordPresenceService>()?.Dispose();
        _serviceProvider.GetService<AcarsPositionService>()?.Dispose();
        _serviceProvider.GetService<UpdateService>()?.Dispose();
        _serviceProvider.GetService<TakeoffPerformanceService>()?.Dispose();
        Log.CloseAndFlush();
        _serviceProvider.Dispose();
        base.OnExit(e);
    }
}

public static class ServiceLocator
{
    private static IServiceProvider? _provider;
    public static void Initialize(IServiceProvider p) => _provider = p;
    public static T Get<T>() where T : notnull
    {
        if (_provider == null) throw new InvalidOperationException("Not initialized");
        return _provider.GetRequiredService<T>();
    }
}
