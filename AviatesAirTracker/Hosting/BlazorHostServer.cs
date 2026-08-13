using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Platform;
using AviatesAirTracker.Services;
using AviatesAirTracker.Services.Efb;
using AviatesAirTracker.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System.IO;

namespace AviatesAirTracker.Hosting;

// ════════════════════════════════════════════════════════════════════
// BLAZOR HOST SERVER  —  TRANSITIONAL SCAFFOLD
//
// WHY THIS EXISTS
//   The UI is Blazor Hybrid: Razor pages rendered by a BlazorWebView control.
//   That control ships in Microsoft.AspNetCore.Components.WebView.Wpf, which is
//   WPF-only — there is no WinUI 3 equivalent (only .Wpf and .WindowsForms exist,
//   and the MAUI variant requires the MAUI app model). So the moment the project
//   flips to WinUI, BlazorWebView disappears and the whole UI with it.
//
//   This class is the strangler bridge: it serves the very same Razor pages as
//   Blazor Server from an in-process Kestrel host, so a plain WebView2 can display
//   them while pages migrate to native XAML one at a time. When the last page is
//   native, this file and Hosting/_Host.cshtml are deleted.
//
// WHY LOOPBACK, UNLIKE EfbServer
//   EfbServer binds 0.0.0.0 and gates access behind a device token plus a 6-digit
//   pairing code, because tablets and phones on the LAN connect to it. This host is
//   the opposite: it renders the pilot's already-authenticated desktop UI, with no
//   login boundary of its own, and must never be reachable off-machine. It binds
//   127.0.0.1 only. That binding IS the security boundary — do not add a LAN bind,
//   and do not copy EfbServer's pairing logic here (it would imply this host was
//   ever meant to be shared, which it is not).
//
// WHY SINGLETONS ARE SHARED RATHER THAN A SECOND CONTAINER BUILT
//   During the migration the app is half native XAML and half Razor. Both halves
//   must observe the same live state — one SimConnectManager pumping at 20 Hz, one
//   FlightSessionManager owning the flight, one SettingsService. Letting the web
//   host build its own instances would silently fork that state: the XAML side and
//   the Razor side would each hold a different "current flight" and neither would
//   be wrong enough to notice quickly. So every service the Razor pages @inject is
//   resolved from the app's container and registered here as an existing INSTANCE.
// ════════════════════════════════════════════════════════════════════

/// <summary>
/// In-process Kestrel host that serves the legacy Razor UI as Blazor Server over
/// loopback. Transitional — see the header comment.
/// </summary>
public sealed class BlazorHostServer : IAsyncDisposable
{
    private readonly IServiceProvider _appServices;
    private WebApplication? _app;

    /// <summary>Root URL the host bound to, e.g. <c>http://127.0.0.1:51734</c>. Null until started.</summary>
    public string? BaseUrl { get; private set; }

    public BlazorHostServer(IServiceProvider appServices) => _appServices = appServices;

    /// <summary>
    /// Starts the loopback host and returns the resolved root URL. Binds port 0 so the
    /// OS picks a free port, then reads the actual port back off the server feature.
    /// </summary>
    public async Task<string> StartAsync()
    {
        if (_app != null && BaseUrl != null) return BaseUrl;

        var builder = WebApplication.CreateBuilder();

        // The app logs through Serilog; ASP.NET's own providers would duplicate and spam.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Loopback only. Port 0 = let the OS choose a free one.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // wwwroot is served from disk next to the exe (see the Content Update in the
        // .csproj) so every asset path already baked into the Razor pages —
        // css/app.css, js/interop.js, images/... — resolves unchanged.
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!Directory.Exists(webRoot))
            Log.Warning("[BlazorHost] wwwroot not found at {Path} — the UI will render unstyled", webRoot);

        // Also load the static-web-assets manifest. wwwroot alone is NOT enough: the Blazor
        // CSS-isolation bundle (AviatesAirTracker.styles.css, which _Host.cshtml links and
        // which carries the scoped styles for LandingResultModal/FlightRecoveryModal) is
        // generated into obj/ and only ever reachable through this manifest.
        builder.WebHost.UseStaticWebAssets();

        var services = builder.Services;

        // _Host.cshtml lives in Hosting/, not the conventional Pages/ root, so Razor Pages
        // discovery has to be pointed at it or the page is compiled but never routed.
        services.AddRazorPages().AddRazorPagesOptions(o => o.RootDirectory = "/Hosting");
        services.AddServerSideBlazor();

        ShareAppSingletons(services);

        var app = builder.Build();

        // Physical wwwroot first, manifest second — disk wins for anything present in both,
        // and the manifest supplies the scoped-CSS bundle that never lands on disk.
        var providers = new List<IFileProvider>();
        if (Directory.Exists(webRoot)) providers.Add(new PhysicalFileProvider(webRoot));
        if (app.Environment.WebRootFileProvider != null) providers.Add(app.Environment.WebRootFileProvider);
        if (providers.Count > 0)
            app.Environment.WebRootFileProvider = new CompositeFileProvider(providers);

        app.UseStaticFiles();
        app.UseRouting();
        app.MapBlazorHub();
        app.MapFallbackToPage("/_Host");

        await app.StartAsync();
        _app = app;

        var addresses = app.Services.GetRequiredService<IServer>()
                                    .Features.Get<IServerAddressesFeature>()?.Addresses;
        var bound = addresses?.FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "Blazor host started but reported no bound address.");

        BaseUrl = bound.TrimEnd('/');
        Log.Information("[BlazorHost] Legacy Razor UI served at {Url}", BaseUrl);
        return BaseUrl;
    }

    /// <summary>
    /// Registers the desktop app's live singletons into the web host's container as
    /// instances. The list mirrors ConfigureServices in App.xaml.cs — every type the
    /// Razor pages @inject must appear here or those pages throw at render time.
    /// </summary>
    private void ShareAppSingletons(IServiceCollection services)
    {
        void Share<T>() where T : class
        {
            var instance = _appServices.GetService<T>();
            if (instance != null) services.TryAddSingleton(instance);
            else Log.Warning("[BlazorHost] Shared service {Type} was not registered in the app container", typeof(T).Name);
        }

        // Data layer
        Share<IFlightRepository>();
        Share<IPilotRepository>();
        Share<ILandingRepository>();
        Share<IFlightDeletionRepository>();
        Share<IMessageRepository>();
        Share<IFriendRepository>();
        // Infrastructure
        Share<IUiDispatcher>();
        Share<SettingsService>();
        Share<AlertService>();
        // SimConnect
        Share<SimConnectManager>();
        Share<FlightPhaseDetector>();
        Share<TelemetryProcessor>();
        // Analytics
        Share<LandingAnalyzer>();
        Share<ApproachMonitor>();
        Share<FuelAnalyzer>();
        Share<RunwayDetector>();
        // Business
        Share<AviatesBackendClient>();
        Share<AcarsPositionService>();
        Share<HoppiesAcarsClient>();
        Share<HazardZoneService>();
        Share<WeatherAdvisoryService>();
        Share<HoppiesAcarsOrchestrator>();
        Share<OtherPilotsService>();
        Share<PttInputService>();
        Share<DispatchRadioService>();
        Share<MessagingService>();
        Share<RouteTracker>();
        Share<FlightSessionManager>();
        Share<DiscordPresenceService>();
        Share<SimBriefService>();
        Share<TakeoffPerformanceService>();
        Share<ExportService>();
        Share<PilotStatsService>();
        Share<EventsService>();
        Share<RoutesService>();
        Share<BookingService>();
        Share<FleetService>();
        Share<UpdateService>();
        Share<FlightRecoveryService>();
        // EFB companion
        Share<EfbStateService>();
        Share<EfbChecklistService>();
        Share<EfbManualsService>();
        Share<EfbAtisService>();
        Share<EfbServer>();
        // ViewModels — LiveFlight/Statistics/Telemetry were deleted earlier in the migration.
        Share<DashboardViewModel>();
        Share<MapViewModel>();
        Share<LandingAnalysisViewModel>();
        Share<PilotHubViewModel>();
        Share<SettingsViewModel>();
        Share<ReplayViewModel>();
        Share<MainViewModel>();
        Share<FleetViewModel>();
    }

    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Stops and disposes the host, never taking longer than <see cref="ShutdownTimeout"/>.
    /// </summary>
    /// <remarks>
    /// OnExit cannot be async, so it blocks the UI thread on this. Circuit teardown can marshal
    /// through the SHARED IUiDispatcher — the WPF dispatcher for that same blocked thread, which
    /// is not pumping while blocked — so any unbounded wait here can deadlock and hang the app on
    /// exit. Two things prevent that, and BOTH are required:
    ///
    ///   • The teardown runs on the thread pool and the wait is bounded by ShutdownTimeout.
    ///     StopAsync alone is not enough to bound: DisposeAsync tears down the service provider,
    ///     CircuitRegistry and hosted services with NO cancellation, so a blocked teardown would
    ///     sail past a cancelled StopAsync and hang on the dispose instead. Bounding the whole
    ///     block means no path can exceed the limit.
    ///   • ConfigureAwait(false) on every await, so continuations resume on the thread pool
    ///     rather than being posted to the blocked dispatcher — otherwise the awaits themselves
    ///     deadlock and the timeout never gets a chance to fire.
    ///
    /// On timeout the host is abandoned rather than force-killed: the process is exiting anyway,
    /// so leaked sockets die with it. Unreachable today (nothing connects to this host yet) but
    /// live from Task 7 onward.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        var app = _app;
        if (app == null) return;

        // Clear state up front — the host is going away regardless of how teardown ends.
        _app = null;
        BaseUrl = null;

        using var cts = new CancellationTokenSource(ShutdownTimeout);

        // Token gives teardown a chance to stop cooperatively; the bounded wait below is the
        // hard guarantee if it does not.
        var teardown = Task.Run(async () =>
        {
            await app.StopAsync(cts.Token).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        });

        try
        {
            await teardown.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
            Log.Information("[BlazorHost] Stopped");
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            // Host.StopAsync aggregates and rethrows, so a fired token usually surfaces as
            // AggregateException rather than a bare OperationCanceledException. Matching only
            // the bare form would misreport timeouts as generic failures.
            Log.Warning("[BlazorHost] Shutdown exceeded {Timeout:g} — abandoning host (non-critical)", ShutdownTimeout);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BlazorHost] Shutdown failed (non-critical)");
        }
    }

    /// <summary>True if the exception is a timeout/cancellation, including aggregated forms.</summary>
    private static bool IsCancellation(Exception ex) => ex switch
    {
        TimeoutException or OperationCanceledException => true,
        AggregateException agg => agg.Flatten().InnerExceptions.Any(IsCancellation),
        _ => false,
    };
}
