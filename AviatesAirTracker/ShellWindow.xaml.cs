using AviatesAirTracker.Navigation;
using AviatesAirTracker.Platform;
using AviatesAirTracker.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Serilog;
using Windows.Graphics;
using WinRT.Interop;

namespace AviatesAirTracker;

/// <summary>
/// The application window. Owns the SimConnect message pump, the navigation rail and the
/// WebView2 that hosts not-yet-migrated pages.
/// </summary>
public sealed partial class ShellWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly FilePickerService _filePicker;
    private readonly WindowSubclass _subclass;

    public ShellWindow(MainViewModel viewModel, FilePickerService filePicker)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _filePicker = filePicker;

        // The second argument is not optional in practice. OnSubclassProc is a reverse
        // P/Invoke from comctl32: an exception escaping the callback unwinds across the
        // native boundary and fail-fasts the process. WindowSubclass swallows it to keep
        // the app alive, so if nothing is passed here the fault vanishes silently — and
        // this callback carries 20Hz SimConnect telemetry.
        _subclass = new WindowSubclass(
            (hwnd, msg, w, l) => _viewModel.OnWindowMessage(hwnd, msg, w, l),
            ex => Log.Error(ex, "[Shell] SimConnect message callback threw"));

        Title = "Aviates Air Flight Tracker";

        ConfigureTitleBar();
        ConfigureBackdrop();
        BuildNavItems();

        Activated += OnFirstActivated;
        Closed += OnClosed;
    }

    private bool _initialised;

    // The rail is interactive (BuildNavItems runs in the constructor) before
    // EnsureCoreWebView2Async completes in OnFirstActivated below, so a click in that gap
    // would otherwise be silently dropped by Navigate's not-ready guard, and then overridden
    // once the host WAS ready — the rail would show what the user clicked while the content
    // area showed Dashboard regardless. Recording the latest request here and navigating to
    // it (not always All[0]) once the WebView2 is ready closes that gap.
    private NavDestination _pendingDestination = NavDestinations.All[0];

    private async void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialised) return;
        _initialised = true;

        var hwnd = WindowNative.GetWindowHandle(this);

        // SimConnect posts its notifications as window message 0x0402 to this HWND.
        _viewModel.SetWindowHandle(hwnd);
        _filePicker.SetWindowHandle(hwnd);
        _subclass.Attach(hwnd);

        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.Resize(new SizeInt32(1440, 900));

        try
        {
            await UiHost.EnsureCoreWebView2Async();

            // Voice PTT needs getUserMedia() inside the page's WebView2 context. WebView2
            // blocks microphone access by default; without this handler getUserMedia()
            // hangs on a prompt the user has no way to answer.
            UiHost.CoreWebView2.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind == Microsoft.Web.WebView2.Core.CoreWebView2PermissionKind.Microphone)
                    e.State = Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Allow;
            };

            Navigate(_pendingDestination);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Shell] Failed to initialise the legacy UI host");
        }
    }

    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
    }

    private void ConfigureBackdrop()
    {
        // Mica Alt gives the window real depth behind the navigation rail. Falls back
        // silently on hardware that cannot render it.
        SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
    }

    private void BuildNavItems()
    {
        string? currentGroup = null;

        foreach (var destination in NavDestinations.All)
        {
            // Emit a section heading whenever the group changes, mirroring the
            // "Overview" / "Pilot" / "Community" labels in the Blazor sidebar.
            if (destination.Group != null && destination.Group != currentGroup)
            {
                Nav.MenuItems.Add(new NavigationViewItemHeader { Content = destination.Group });
                currentGroup = destination.Group;
            }

            Nav.MenuItems.Add(new NavigationViewItem
            {
                Content = destination.Label,
                Tag = destination.Tag,
            });
        }

        // Index 0 is the "Overview" header, so the first selectable item is index 1.
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().First();
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;

        var destination = NavDestinations.All.FirstOrDefault(d => d.Tag == tag);
        if (destination == null) return;

        // Recorded even if Navigate below no-ops (WebView2 not ready yet) — see the field's
        // doc comment. Once the host becomes ready, OnFirstActivated navigates to whatever
        // this was last set to, not always the first destination.
        _pendingDestination = destination;
        Navigate(destination);
    }

    /// <summary>
    /// Sends the rail's selection to whichever host owns that destination. Today every
    /// destination lives on the legacy Blazor host; as pages migrate this grows a branch
    /// that shows a native Page instead.
    /// </summary>
    private void Navigate(NavDestination destination)
    {
        if (UiHost.CoreWebView2 == null) return;

        var baseUrl = App.LegacyUiUrl;
        if (baseUrl == null)
        {
            Log.Error("[Shell] Legacy UI host is not running; cannot navigate");
            return;
        }

        UiHost.CoreWebView2.Navigate($"{baseUrl}{destination.Route}?shell=native");
    }

    private void OnBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (UiHost.CoreWebView2?.CanGoBack == true) UiHost.CoreWebView2.GoBack();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _subclass.Detach();
        (Application.Current as App)?.Shutdown();
    }
}
