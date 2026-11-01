using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using AviatesAirTracker.Platform;
using AviatesAirTracker.Services;
using AviatesAirTracker.Services.Efb;
using AviatesAirTracker.Services.Voice;
using AviatesAirTracker.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Serilog;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Xaml;

namespace AviatesAirTracker;

public partial class App : Application
{
    private ServiceProvider _serviceProvider = null!;
    private bool _shutdownRan;

    // Held for the app's whole lifetime so the installer can tell whether the app is currently
    // running. Name must match installer\AviatesAirTracker_Setup.iss's own AppMutex directive
    // exactly -- Inno Setup checks for a mutex by this name before copying files and (with
    // CloseApplications=yes, the default) uses the Windows Restart Manager to close whatever
    // process is holding it first. Without this, UpdateService.DownloadAndInstallAsync's silent
    // installer launch had no synchronization with this process's own shutdown at all: it fired
    // Process.Start for the installer, waited a bare 500ms, then called Exit() -- if this
    // process's runtime/DLL file handles hadn't actually been released yet (self-contained .NET
    // apps hold dozens of loose DLLs, not just the one EXE), Setup's /VERYSILENT run would hit
    // file-in-use errors on those files, and /SUPPRESSMSGBOXES silently swallowed the resulting
    // failure -- the update would appear to run (window closes, app "restarts") while actually
    // still being the old version, matching the reported "download and install doesn't actually
    // install." AppMutex hands this synchronization to Setup itself instead of racing a fixed
    // delay against an unknown teardown time.
    private Mutex? _appMutex;

    // A second launch (desktop shortcut, Start with Windows, the sim's exe.xml) signals the copy
    // that's already running to show itself, then exits -- two copies would mean two SimConnect
    // clients recording, and submitting, the same flight.
    private const string ActivateSignalName = "AviatesAirTracker-Activate";
    private EventWaitHandle? _activateSignal;
    private RegisteredWaitHandle? _activateWait;

    // Set once the shell exists: from then on an unexpected UI error is logged and reported to
    // the pilot instead of closing the app.
    private bool _shellReady;

    public App()
    {
        InitializeComponent();
    }

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

    // Exposed so SettingsPage's Wipe Local Data & Resync action can re-run
    // SyncPirepHistoryAsync/SyncLandingHistoryAsync/SyncFriendsAsync below -- the exact same
    // IServiceProvider-based signature those three already use at startup, reused rather than
    // reimplemented.
    internal static IServiceProvider ServiceProvider => ((App)Application.Current)._serviceProvider;

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
        // Created first, disposed last (see Shutdown()) -- see the field's own doc comment for
        // why the installer needs this. Inno Setup's AppMutex check only cares that a mutex
        // object by this exact name exists in the kernel (i.e. some process holds an open handle
        // to it) -- not whether it's actually locked -- so this deliberately never calls
        // WaitOne()/ReleaseMutex(); it exists purely as Setup's "is the app running" signal, not
        // for this process's own mutual exclusion.
        _appMutex = new Mutex(initiallyOwned: false, name: "AviatesAirTracker-AppMutex", out var firstInstance);
        var background = Environment.GetCommandLineArgs().Contains(StartupLaunchService.BackgroundArg);
        if (!firstInstance)
        {
            if (!background && EventWaitHandle.TryOpenExisting(ActivateSignalName, out var signal))
            {
                signal.Set();
                signal.Dispose();
            }
            _appMutex.Dispose();
            _appMutex = null;
            Exit();
            return;
        }

        // Global crash handlers — capture any exception before the window appears
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            DumpCrash("AppDomain", ex.ExceptionObject as Exception);
        UnhandledException += (_, ex) =>
        {
            // SkiaSharp.Views.WinUI 2.88.7 (pulled in by Mapsui's MapControl) dereferences a
            // null XamlRoot in SKXamlCanvas.OnUnloaded when the Map/ACARS page leaves the Frame.
            // That handler only frees the canvas bitmap, so nothing is left half-done -- it was
            // just taking the app down on navigation (eight times in crash.log). Swallow exactly
            // that and nothing else.
            if (ex.Exception is NullReferenceException &&
                ex.Exception.StackTrace?.Contains("SKXamlCanvas.OnUnloaded", StringComparison.Ordinal) == true)
            {
                Log.Warning(ex.Exception, "[App] Ignored SkiaSharp SKXamlCanvas unload fault");
                ex.Handled = true;
                return;
            }

            DumpCrash("UnhandledException", ex.Exception);

            // Before the shell is up a failure here is a broken start; afterwards it is one
            // action that didn't finish, and the rest of the app (and the flight being
            // tracked) is fine -- so it's reported, not fatal.
            ex.Handled = _shellReady;
            if (_shellReady)
            {
                try
                {
                    _serviceProvider.GetRequiredService<AlertService>().ShowAlert(
                        "Something went wrong",
                        "That action didn't finish. The details were saved to the app's log; if it keeps happening, send it to us from Support.",
                        AlertLevel.Critical);
                }
                catch { /* reporting must never throw */ }
            }
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            DumpCrash("TaskScheduler", ex.Exception);
            ex.SetObserved();
        };

        try
        {
            // MINOR-07: Was a relative path — UnauthorizedAccessException when installed to Program Files.
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AviatesAirTracker", "logs");
            Directory.CreateDirectory(logDir);

            // Debug-level output (per-poll and per-retry chatter) only in debug builds, and each
            // day's file capped at 20 MB -- one day's log had reached 71 MB.
            Log.Logger = new LoggerConfiguration()
#if DEBUG
                .MinimumLevel.Debug()
#else
                .MinimumLevel.Information()
#endif
                .WriteTo.File(Path.Combine(logDir, "aviates_.log"), rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    fileSizeLimitBytes: 20 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            Log.Information("=== Aviates Air Flight Tracker v{Version} Starting ===",
                version is null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}");

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

            // Resolve to subscribe to DispatchRadioService's voice events (constructor wires all
            // five: VoiceChannelConnectRequested/Disconnected/DistanceUpdated/PttPressed/Released) --
            // must happen after DispatchRadioService above so those subscriptions are live before
            // any telemetry/PTT events can fire.
            _serviceProvider.GetRequiredService<VoiceOrchestrator>();

            // Retry any PIREPs that failed or couldn't submit last session
            _ = RetryPendingPirepsAsync(_serviceProvider);
            _ = SyncPirepHistoryAsync(_serviceProvider);
            _ = SyncLandingHistoryAsync(_serviceProvider);
            _ = SyncFriendsAsync(_serviceProvider);

            // BookingService.ActiveBooking is in-memory-only and starts every launch as null
            // even when a booking (activated or not) still exists server-side -- FlightSession
            // Manager.OnEnginesStarted reads ActiveBooking synchronously (no network call) at the
            // exact moment engines start to decide whether tracking is allowed to begin, so
            // without this a pilot who booked+activated, then restarted the app before flying,
            // would be silently blocked from ever having their flight tracked.
            _ = HydrateActiveBookingAsync(_serviceProvider);

            // Advancing the pilot's current airport to the booking destination when a booked
            // flight completes is done by BookingService.CompleteActiveBookingAsync, which
            // FlightSessionManager.OnFlightComplete calls right after FlightCompleted. (A
            // FlightCompleted handler here used to do it and clear ActiveBooking first, which
            // left CompleteActiveBookingAsync nothing to complete -- the booking was never
            // marked completed server-side.)
            var sessionMgr  = _serviceProvider.GetRequiredService<FlightSessionManager>();
            var settingsSvc = _serviceProvider.GetRequiredService<SettingsService>();

            // Mandatory updates: checked shortly after launch and every few hours, installed
            // whenever no flight is being tracked (see AutoUpdateCoordinator).
            _serviceProvider.GetRequiredService<AutoUpdateCoordinator>().Start();

            // Windows notifications (DMs, PIREP results, event reminders) while the app is behind
            // the sim; re-point the start-with-Windows/sim entries at this exe.
            _serviceProvider.GetRequiredService<DesktopNotificationService>().Initialize();
            _ = Task.Run(() => _serviceProvider.GetRequiredService<StartupLaunchService>().SyncOnStartup(
                settingsSvc.Settings.StartWithWindows, settingsSvc.Settings.StartWithSimulator));

            // EFB companion server — resolving it wires the telemetry bridge; auto-start
            // hosts the LAN tablet server if enabled in settings. Failures are non-fatal.
            var efbServer = _serviceProvider.GetRequiredService<EfbServer>();
            if (settingsSvc.Settings.EfbEnabled && settingsSvc.Settings.EfbAutoStart)
                _ = efbServer.StartAsync();

            var shell = _serviceProvider.GetRequiredService<ShellWindow>();
            if (background) shell.StartInTray();
            else shell.Activate();
            _shellReady = true;

            var dispatcher = DispatcherQueue.GetForCurrentThread();
            _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateSignalName);
            _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateSignal,
                (_, _) => dispatcher.TryEnqueue(shell.BringToFront), null, Timeout.Infinite, executeOnlyOnce: false);

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
            var msg  = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}\n{new string('-', 80)}\n";
            File.AppendAllText(path, msg);
            // Also try Serilog in case it's initialized
            try { Log.Fatal(ex, "[Crash/{Source}] Unhandled exception", source); } catch { }
        }
        catch { /* last resort — can't log the logger */ }
    }

    // See the call site's own comment above for why this exists. isCharter is derived from
    // RouteId being null rather than tracked separately server-side -- a charter booking has no
    // route_id at all (see BookingService.CreateCharterBookingAsync), a normal one always does.
    private static async Task HydrateActiveBookingAsync(IServiceProvider sp)
    {
        try
        {
            var bookingSvc = sp.GetRequiredService<BookingService>();
            var booking = await bookingSvc.GetCurrentBookingAsync();
            if (booking != null && !booking.IsCancelled)
                bookingSvc.SetActiveBooking(booking, isCharter: booking.RouteId == null);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[App] HydrateActiveBookingAsync failed");
        }
    }

    // PIREPs the server refused outright (missing airports etc.) are skipped here and wait for
    // the pilot to fix them on the Flights page -- see PirepSyncService.
    private static Task RetryPendingPirepsAsync(IServiceProvider sp) =>
        sp.GetRequiredService<PirepSyncService>().RetryPendingAsync();

    internal static async Task SyncPirepHistoryAsync(IServiceProvider sp)
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
                if (string.IsNullOrWhiteSpace(r.DepartureICAO) || string.IsNullOrWhiteSpace(r.ArrivalICAO))
                    continue;

                // FlightNumber can be legitimately blank: an approved manual PIREP (submitted on
                // the website with no airline flight number, identified only by callsign) gets
                // migrated into this same `pireps` table by worker.js's admin-approval handler,
                // which sets callsign = flight_number || aircraft_reg -- flight_number itself is
                // optional on that form and often blank. This loop used to also require
                // FlightNumber non-blank, which silently dropped every such PIREP from the app's
                // local sync entirely: it never showed up in Flights/Pilot Hub and never counted
                // toward local hours, even though it was already sitting in the real `pireps`
                // table and counting correctly on the website (server-side stats read
                // block_minutes directly, not gated on FlightNumber at all).
                var label = !string.IsNullOrWhiteSpace(r.FlightNumber) ? r.FlightNumber : r.Callsign;
                if (string.IsNullOrWhiteSpace(label)) continue;

                // Skip if already in local cache — match by the same label + block-out within 60 s
                var rBlockOut = r.BlockOutTime ?? r.SubmittedAt;
                bool exists = local.Any(f =>
                    (f.FlightNumber == label || f.Callsign == label) &&
                    Math.Abs((f.BlockOutTime - rBlockOut).TotalSeconds) < 60);
                if (exists) continue;

                // A migrated manual PIREP has no real block_out_time/block_in_time (worker.js
                // inserts both as null -- only block_minutes/air_minutes carry the real duration).
                // FlightRecord.BlockTime/AirTime are computed as InTime-OutTime, not read from any
                // minutes field, so leaving both timestamps equal (the old `?? r.SubmittedAt`
                // fallback for both) silently collapsed every migrated manual PIREP's hours to
                // zero even once it did show up locally. Synthesizing an End time exactly
                // BlockMinutes/AirMinutes after Start whenever the server didn't give us two
                // distinct real timestamps makes the flight's hours count locally exactly as if
                // it had flown with normal ACARS telemetry. A real ACARS-submitted flight always
                // has two genuine, already-distinct timestamps here, so this never changes that
                // case's own numbers.
                var blockOut = r.BlockOutTime ?? r.SubmittedAt;
                var blockIn  = r.BlockInTime ?? blockOut.AddMinutes(r.BlockMinutes);
                if (blockIn <= blockOut && r.BlockMinutes > 0)
                    blockIn = blockOut.AddMinutes(r.BlockMinutes);

                var record = new FlightRecord
                {
                    FlightNumber     = label,
                    Callsign         = r.Callsign,
                    DepartureICAO    = r.DepartureICAO,
                    ArrivalICAO      = r.ArrivalICAO,
                    AircraftType     = r.AircraftType,
                    BlockOutTime     = blockOut,
                    BlockInTime      = blockIn,
                    TakeoffTime      = blockOut,
                    LandingTime      = r.AirMinutes > 0 ? blockOut.AddMinutes(r.AirMinutes) : blockIn,
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

    // Mirrors SyncPirepHistoryAsync above, one level of detail down: rebuilds the local
    // per-landing history (full score breakdown, runway, wind, rollout, etc.) from
    // /api/v1/landings, instead of only the coarse VS/score embedded in each PIREP. This is
    // what makes Pilot Hub's Landing History table and average-score stat match across a
    // reinstall/new PC, not just the Flights list.
    internal static async Task SyncLandingHistoryAsync(IServiceProvider sp)
    {
        try
        {
            var settings = sp.GetRequiredService<SettingsService>();
            var key      = settings.Settings.AcarsKey.Trim();
            if (string.IsNullOrEmpty(key)) return;

            var backend  = sp.GetRequiredService<AviatesBackendClient>();
            var landings = sp.GetRequiredService<ILandingRepository>();

            var remote = await backend.FetchMyLandingsAsync(key);
            if (remote.Count == 0) return;

            var local = await landings.GetAllAsync();
            int added = 0;

            foreach (var r in remote)
            {
                // Dedupe by touchdown time within a few seconds -- two genuinely distinct
                // landings from the same pilot this close together essentially never happen,
                // matching SyncPirepHistoryAsync's own tolerance-window dedupe approach.
                bool exists = local.Any(l => Math.Abs((l.Timestamp - r.TouchdownAt).TotalSeconds) < 5);
                if (exists) continue;

                var record = new LandingResult
                {
                    Timestamp                  = r.TouchdownAt,
                    AirportICAO                = r.AirportIcao,
                    RunwayIdentifier            = r.RunwayIdentifier,
                    TouchdownLatitude           = r.TouchdownLat ?? 0,
                    TouchdownLongitude          = r.TouchdownLon ?? 0,
                    VerticalSpeedFPM            = r.VsFpm,
                    IASKts                      = r.IasKts,
                    GroundSpeedKts              = r.GsKts,
                    TouchdownPitchDeg           = r.PitchDeg,
                    TouchdownBankDeg            = r.BankDeg,
                    HeadwindComponent           = r.HeadwindKt,
                    CrosswindComponent          = r.CrosswindKt,
                    WindSpeedKts                = r.WindSpeedKt,
                    WindDirectionDeg            = r.WindDirDeg,
                    ApproachWasStable           = r.ApproachStable,
                    FlareDetected               = r.FlareDetected,
                    BounceCount                 = r.BounceCount,
                    RolloutDistanceFt           = r.RolloutDistanceFt,
                    AverageDecelerationKtPerSec = r.AvgDeceleration,
                    ThresholdDistanceFt         = r.ThresholdDistanceFt,
                    FuelOnTouchdownLbs          = r.FuelOnTouchdownLbs,
                    LandingScore                = r.Score,
                    ScoreBreakdown = new LandingScoreBreakdown
                    {
                        VerticalSpeedScore = r.ScoreVs,
                        PitchScore         = r.ScorePitch,
                        BankScore          = r.ScoreBank,
                        SpeedScore         = r.ScoreSpeed,
                        CrosswindScore     = r.ScoreCrosswind,
                        StabilityScore     = r.ScoreStability,
                        TotalScore         = r.Score,
                    },
                };

                await landings.SaveAsync(record);
                added++;
            }

            if (added > 0)
                Log.Information("[Startup] Synced {Count} historical landing(s) from Supabase", added);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Startup] Landing history sync failed (non-critical)");
        }
    }

    // Mirrors SyncPirepHistoryAsync/SyncLandingHistoryAsync above: rebuilds the local friend
    // list from /api/v1/friends so it survives a reinstall/new PC instead of living only in
    // the local JSON cache (adding a friend was never actually persisted server-side before
    // this pass -- see MessagingService.AddFriendByCodeAsync).
    internal static async Task SyncFriendsAsync(IServiceProvider sp)
    {
        try
        {
            var settings = sp.GetRequiredService<SettingsService>();
            var key      = settings.Settings.AcarsKey.Trim();
            if (string.IsNullOrEmpty(key)) return;

            var backend = sp.GetRequiredService<AviatesBackendClient>();
            var friends = sp.GetRequiredService<IFriendRepository>();

            var remote = await backend.FetchMyFriendsAsync(key);
            if (remote.Count == 0) return;

            int added = 0;
            foreach (var entry in remote)
            {
                if (string.IsNullOrWhiteSpace(entry.PilotId)) continue;
                if (await friends.ExistsAsync(entry.PilotId)) continue;

                await friends.AddAsync(entry);
                added++;
            }

            if (added > 0)
                Log.Information("[Startup] Synced {Count} friend(s) from Supabase", added);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Startup] Friends sync failed (non-critical)");
        }
    }

    private static void ConfigureServices(ServiceCollection services)
    {
        // Data layer — JSON-persisted (survive app restarts)
        services.AddSingleton<IFlightRepository, JsonFlightRepository>();
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
        services.AddSingleton<FlightComplianceMonitor>();
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
        // Native voice pipeline -- VoiceOrchestrator is the replacement for
        // MainLayout.razor's JS-interop voice bridge, wiring DispatchRadioService's
        // voice events into VoiceAudioEngine/VoiceChannelClient (eagerly constructed
        // at startup below, near DispatchRadioService's own eager resolve).
        services.AddSingleton<VoiceAudioEngine>();
        services.AddSingleton<VoiceChannelClient>();
        services.AddSingleton<VoiceOrchestrator>();
        services.AddSingleton<MessagingService>();
        services.AddSingleton<RouteTracker>();
        services.AddSingleton<FlightTrailService>();
        services.AddSingleton<FlightSessionManager>();
        services.AddSingleton<DiscordPresenceService>();
        services.AddSingleton<SimBriefService>();
        services.AddSingleton<TakeoffPerformanceService>();
        services.AddSingleton<ExportService>();
        services.AddSingleton<PilotStatsService>();
        services.AddSingleton<EventsService>();
        services.AddSingleton<EventReminderService>();
        services.AddSingleton<RoutesService>();
        services.AddSingleton<BookingService>();
        services.AddSingleton<FleetService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<AutoUpdateCoordinator>();
        services.AddSingleton<PirepSyncService>();
        services.AddSingleton<DesktopNotificationService>();
        services.AddSingleton<StartupLaunchService>();
        services.AddSingleton<FlightRecoveryService>();
        // EFB companion (virtual tablet) — embedded LAN web server + telemetry bridge
        services.AddSingleton<EfbStateService>();
        services.AddSingleton<EfbChecklistService>();
        services.AddSingleton<EfbManualsService>();
        services.AddSingleton<EfbAtisService>();
        services.AddSingleton<EfbNetworkTrafficService>();
        services.AddSingleton<EfbMapWeatherService>();
        services.AddSingleton<EfbDatalinkService>();
        services.AddSingleton<EfbServer>();
        // ViewModels
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<MapViewModel>();
        services.AddSingleton<LandingAnalysisViewModel>();
        services.AddSingleton<PilotHubViewModel>();
        services.AddSingleton<LogbookViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ReplayViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<FleetViewModel>();
        services.AddSingleton<FlightsViewModel>();
        services.AddSingleton<RoutesViewModel>();
        // EventsViewModel (Task 1) wasn't registered when it was added -- required here so
        // EventsPage's Task 2 App.GetService<EventsViewModel>() call resolves.
        services.AddSingleton<EventsViewModel>();
        services.AddSingleton<MessagesViewModel>();
        services.AddSingleton<AcarsViewModel>();
        services.AddSingleton<NewsViewModel>();
        services.AddSingleton<ShellWindow>();
    }

    /// <summary>
    /// Hard ceiling on how long the process may take to disappear once <see cref="Shutdown"/>
    /// starts. Comfortably above the sum of the individual disposal timeouts below (EFB 5 s,
    /// PTT 2 s), which in practice all complete in milliseconds.
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
        Step("EfbDatalink", () => _serviceProvider.GetService<EfbDatalinkService>()?.Dispose());
        Step("SimConnectManager", () => _serviceProvider.GetService<SimConnectManager>()?.Disconnect());
        Step("Discord", () => _serviceProvider.GetService<DiscordPresenceService>()?.Dispose());
        Step("AcarsPosition", () => _serviceProvider.GetService<AcarsPositionService>()?.Dispose());
        Step("Hoppies", () => _serviceProvider.GetService<HoppiesAcarsOrchestrator>()?.Dispose());
        Step("PttInput", () => _serviceProvider.GetService<PttInputService>()?.Dispose());
        Step("DispatchRadio", () => _serviceProvider.GetService<DispatchRadioService>()?.Dispose());
        Step("VoiceOrchestrator", () => _serviceProvider.GetService<VoiceOrchestrator>()?.Dispose());
        Step("VoiceChannelClient", () => _serviceProvider.GetService<VoiceChannelClient>()?.Dispose());
        Step("VoiceAudioEngine", () => _serviceProvider.GetService<VoiceAudioEngine>()?.Dispose());
        Step("AutoUpdate", () => _serviceProvider.GetService<AutoUpdateCoordinator>()?.Dispose());
        Step("UpdateService", () => _serviceProvider.GetService<UpdateService>()?.Dispose());
        Step("Notifications", () => _serviceProvider.GetService<DesktopNotificationService>()?.Dispose());
        Step("ActivateSignal", () => { _activateWait?.Unregister(null); _activateSignal?.Dispose(); });
        Step("EventReminderService", () => _serviceProvider.GetService<EventReminderService>()?.Dispose());
        Step("TakeoffPerf", () => _serviceProvider.GetService<TakeoffPerformanceService>()?.Dispose());

        Log.Information("[Shutdown] All services disposed in {Ms} ms — exiting", sw.ElapsedMilliseconds);
        Log.CloseAndFlush();
        _serviceProvider.Dispose();

        // Last, after the log is flushed -- releasing this drops the kernel object once no
        // process holds it, which is what lets the installer's AppMutex check see the app as
        // "gone" and proceed with copying files.
        _appMutex?.Dispose();
        _appMutex = null;
    }
}
