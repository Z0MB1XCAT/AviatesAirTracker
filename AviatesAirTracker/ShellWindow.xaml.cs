using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Dialogs;
using AviatesAirTracker.Models;
using AviatesAirTracker.Navigation;
using AviatesAirTracker.Platform;
using AviatesAirTracker.Services;
using AviatesAirTracker.Shell;
using AviatesAirTracker.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Windowing;
using Serilog;
using Windows.Graphics;
using Windows.UI;
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
    private readonly SettingsService _settings;
    private readonly FlightSessionManager _sessionManager;
    private readonly MessagingService _messagingService;
    private readonly IFlightRepository _flightRepository;
    private readonly ILandingRepository _landingRepository;
    private readonly AlertService _alertService;

    // Named references into Nav.MenuItems for the four items that carry a live badge.
    // BuildNavItems assigns these by matching NavDestination.Tag; every handler below
    // null-checks before use since a tag-matching bug would otherwise null-ref the moment
    // an event fires (SessionStateChanged/UnreadCountChanged/FlightCompleted can all fire
    // before or independently of BuildNavItems in theory, though today it always runs first
    // in the constructor).
    private NavigationViewItem? _flightsItem;
    private NavigationViewItem? _acarsItem;
    private NavigationViewItem? _mapItem;
    private NavigationViewItem? _messagesItem;

    // ACARS/Map "Live" pill. NavigationViewItem.InfoBadge.Value is numeric-only (WinUI's
    // InfoBadge has no built-in text-content mode), and building a custom InfoBadge
    // ControlTemplate requires either a XAML <Style>/<ControlTemplate> element (which
    // silently crashes XamlCompiler.exe on this SDK — see docs/superpowers/plans/
    // 2026-08-23-winui3-phase2-shell-visual.md's Task 5 note) or XamlReader.Load'ing a
    // template string at runtime, which is more machinery than this task warrants. Instead
    // each item's Content is a label + a plain Border/TextBlock pill built in code-behind,
    // toggled by Visibility.
    private Border? _acarsLivePill;
    private Border? _mapLivePill;

    // Flights dot. Populated by BuildNavItems: if the framework ships
    // "AttentionDotInfoBadgeStyle" (it does, as of Windows App SDK 1.8 — verified at
    // runtime below rather than assumed), it's applied to a real InfoBadge on
    // _flightsItem.InfoBadge. If that resource is ever missing, _flightsDot is the
    // code-behind Ellipse fallback built into the item's Content instead.
    private Style? _dotBadgeStyle;
    private Ellipse? _flightsDot;

    public ShellWindow(
        MainViewModel viewModel,
        FilePickerService filePicker,
        SettingsService settings,
        FlightSessionManager sessionManager,
        MessagingService messagingService,
        IFlightRepository flightRepository,
        ILandingRepository landingRepository,
        AlertService alertService)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _filePicker = filePicker;
        _settings = settings;
        _sessionManager = sessionManager;
        _messagingService = messagingService;
        _flightRepository = flightRepository;
        _landingRepository = landingRepository;
        _alertService = alertService;

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
        ConfigureNavAppearance();

        _sessionManager.SessionStateChanged += OnSessionStateChanged;
        _messagingService.UnreadCountChanged += OnUnreadCountChanged;
        // FlightCompleted is the closest available signal to "PIREP sync state changed":
        // AviatesBackendClient exposes no completion event for the fire-and-forget PIREP
        // submit in FlightSessionManager.SubmitPirepSafeAsync, so the dot is refreshed the
        // moment a flight completes (when it is, by definition, newly unsynced) rather than
        // when the background submit actually finishes.
        _sessionManager.FlightCompleted += OnFlightCompleted;
        _alertService.LandingResultReady += OnLandingResultReady;
        _ = RefreshFlightsBadgeAsync();

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

            // Reveals the rail once the WebView2 navigates away from /login (e.g. after a
            // successful sign-in) and hides it again if it ever navigates back to /login.
            // Attached before the first Navigate/CoreWebView2.Navigate call below so it
            // catches every completion, including the very first one.
            UiHost.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                var url = UiHost.CoreWebView2.Source ?? string.Empty;
                var onLogin = url.Contains("/login", StringComparison.OrdinalIgnoreCase);

                Nav.IsPaneVisible = !onLogin;
                Nav.IsBackButtonVisible = onLogin
                    ? NavigationViewBackButtonVisible.Collapsed
                    : NavigationViewBackButtonVisible.Visible;
            };

            // Native-side analogue of the WebView2 handler directly above: covers both the
            // startup login-gate (OnFirstActivated's else branch, native LoginPage) and any
            // future logout-triggered return to login (SettingsPage's OnLogOutTapped navigating
            // Frame back to LoginPage) with one central handler, so neither call site needs to
            // know anything about rail visibility itself. Fires after every successful
            // NativeHost.Navigate — including a Navigate triggered from inside a hosted page's
            // own Frame.Navigate call, per Frame.Navigated's normal bubbling behavior.
            NativeHost.Navigated += (_, e) =>
            {
                var onLogin = e.SourcePageType == typeof(Pages.LoginPage);

                Nav.IsPaneVisible = !onLogin;
                Nav.IsBackButtonVisible = onLogin
                    ? NavigationViewBackButtonVisible.Collapsed
                    : NavigationViewBackButtonVisible.Visible;
            };

            if (IsLoggedIn())
            {
                // The rail defaults to hidden in XAML (see the comment there) so a
                // signed-out pilot never sees it during this async gap — reveal it here now
                // that a stored ACARS key has actually been confirmed.
                Nav.IsPaneVisible = true;
                Nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Visible;
                Navigate(_pendingDestination);
            }
            else
            {
                // Login owns the whole window: no rail, no back button, until it completes.
                // Deliberately ignores _pendingDestination — a click during the startup gap
                // must never let a signed-out pilot skip the login gate. These two lines are
                // redundant with the NativeHost.Navigated handler above (which will also set
                // them once Navigate below completes) — kept anyway as belt-and-suspenders
                // against a one-frame flash of the rail before that handler fires.
                Nav.IsPaneVisible = false;
                Nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;

                // The final page conversion in this migration: login now goes through the
                // native LoginPage via NativeHost, the exact same mechanism Navigate(NavDestination)
                // already uses for every other native page, instead of the legacy WebView2 route.
                NativeHost.Navigate(typeof(Pages.LoginPage));
                NativeHost.Visibility = Visibility.Visible;
                UiHost.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Shell] Failed to initialise the legacy UI host");
        }
    }

    /// <summary>
    /// Mirrors the guard in MainLayout.razor:315. A stored ACARS key is what "signed in"
    /// means in this app; Login.razor validates it against the backend before storing it.
    /// </summary>
    private bool IsLoggedIn() =>
        !string.IsNullOrWhiteSpace(_settings.Settings.AcarsKey);

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

        // WinUI 3 (Windows App SDK 1.8.250906003) does ship "AttentionDotInfoBadgeStyle" in
        // XamlControlsResources — confirmed here at runtime rather than assumed, per the
        // spec's flagged "unverified assumption". _dotBadgeStyle stays null (falling back to
        // a custom Ellipse dot below) only if a future SDK ever drops the resource.
        _dotBadgeStyle = Application.Current.Resources.TryGetValue("AttentionDotInfoBadgeStyle", out var dotStyleObj)
            ? dotStyleObj as Style
            : null;
        Log.Debug(
            "[Shell] Flights nav badge: {Approach}",
            _dotBadgeStyle != null ? "AttentionDotInfoBadgeStyle" : "custom Ellipse fallback (AttentionDotInfoBadgeStyle not found)");

        foreach (var destination in NavDestinations.All)
        {
            // Emit a section heading whenever the group changes, mirroring the
            // "Overview" / "Pilot" / "Community" labels in the Blazor sidebar.
            if (destination.Group != null && destination.Group != currentGroup)
            {
                // Uppercased here, not in NavDestinations.cs: app.css's .nav-section-label
                // is `text-transform: uppercase` applied at render time, and NavDestinations
                // is shared, plain-cased source data — this is a purely visual concern.
                Nav.MenuItems.Add(StyleHeader(new NavigationViewItemHeader { Content = destination.Group.ToUpperInvariant() }));
                currentGroup = destination.Group;
            }

            var item = new NavigationViewItem { Tag = destination.Tag };

            if (destination.Tag is "acars" or "map")
            {
                var (content, pill) = BuildLabelWithLivePill(destination.Label);
                item.Content = content;
                if (destination.Tag == "acars") _acarsLivePill = pill; else _mapLivePill = pill;
            }
            else if (destination.Tag == "flights" && _dotBadgeStyle == null)
            {
                var (content, dot) = BuildLabelWithDot(destination.Label);
                item.Content = content;
                _flightsDot = dot;
            }
            else
            {
                item.Content = destination.Label;
            }

            // StyleNavItem only touches item-level properties (font/size/color/margin/radius),
            // never Content, so it applies uniformly regardless of which branch above built
            // this item's Content — plain label, live-pill panel, or dot panel.
            StyleNavItem(item);
            Nav.MenuItems.Add(item);

            switch (destination.Tag)
            {
                case "flights": _flightsItem = item; break;
                case "acars": _acarsItem = item; break;
                case "map": _mapItem = item; break;
                case "messages": _messagesItem = item; break;
            }
        }

        // Index 0 is the "Overview" header, so the first selectable item is index 1.
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().First();
    }

    /// <summary>
    /// app.css's .nav-item: 13.5px Plus Jakarta Sans Medium, --sb-text, a 12px radius on a
    /// 10px/2px margin. Applied here in code, item-by-item, rather than as an implicit
    /// &lt;Style TargetType="NavigationViewItem"&gt; in ShellWindow.xaml — see the Task 5
    /// verification notes: any &lt;Style&gt; element anywhere in this project's XAML makes
    /// XamlCompiler.exe (Windows App SDK 1.8.250906003's tools\net472\XamlCompiler.exe) exit
    /// silently with code 1 and no diagnostic output, in this build environment. Confirmed
    /// isolated to Style elements specifically (an empty Window.Resources also reproduces it),
    /// not to font/brush/margin usage in general — the same brushes/fonts already work fine
    /// as plain property values. Setting the properties directly on each item, since
    /// BuildNavItems() already constructs them in code, avoids the crash entirely and
    /// produces the identical rendered result an implicit style would have.
    /// </summary>
    private static NavigationViewItem StyleNavItem(NavigationViewItem item)
    {
        item.FontFamily = (FontFamily)Application.Current.Resources["BodyFontMedium"];
        item.FontSize = 13.5;
        item.Foreground = (Brush)Application.Current.Resources["SidebarTextBrush"];
        item.Margin = new Thickness(10, 2, 10, 2);
        item.CornerRadius = new CornerRadius(12);
        return item;
    }

    /// <summary>
    /// app.css's .nav-section-label: 9px/700 Plus Jakarta Sans Bold, --sb-label, 0.12em
    /// tracking (CharacterSpacing is in 1/1000 em, so 0.12em ≈ 120 — confirmed present on
    /// Control in this SDK via the metadata docs and a successful build with it set). See
    /// StyleNavItem's doc comment for why this is done in code rather than an implicit XAML
    /// Style.
    /// </summary>
    private static NavigationViewItemHeader StyleHeader(NavigationViewItemHeader header)
    {
        header.FontFamily = (FontFamily)Application.Current.Resources["BodyFontBold"];
        header.FontSize = 9;
        header.CharacterSpacing = 120;
        header.Foreground = (Brush)Application.Current.Resources["SidebarLabelBrush"];
        header.Margin = new Thickness(20, 16, 20, 8);
        return header;
    }

    /// <summary>
    /// The flat navy pane background (Task 5 Step 1) plus the active-item accent-color
    /// substitution (Task 5 Step 2's "accept WinUI's default selection visual, substitute
    /// this app's colors" option — see the plan's fidelity-gap note; the 3px accent bar from
    /// app.css's .nav-item.active::after is not reproduced, only the flat colors are).
    /// <para>
    /// The plan's Step 1 says to set `PaneBackground="{StaticResource SidebarBackgroundBrush}"`
    /// directly on the &lt;NavigationView&gt; element. That property does not exist: checked
    /// against this SDK's own metadata (Microsoft.WindowsAppSDK.WinUI 1.8.250906003's
    /// metadata\Microsoft.UI.Xaml.xml), `PaneBackground` belongs to `SplitView`, not
    /// `NavigationView` — NavigationView never forwards it. Writing the nonexistent attribute
    /// in XAML doesn't fail with the usual "property not found" diagnostic in this compiler
    /// build, though: XamlCompiler.exe (tools\net472\XamlCompiler.exe) exits silently with
    /// code 1 and zero output instead, which is what made this take a full bisection to find
    /// (a `&lt;Style&gt;` element anywhere in the project's XAML hits the same silent-crash
    /// path for unrelated reasons, which was the initial, wrong lead).
    /// </para>
    /// <para>
    /// NavigationView's actual control template (Microsoft.UI's Themes/generic.xaml) hosts a
    /// named `RootSplitView` part whose base Pane color comes from
    /// `PaneBackground="{ThemeResource NavigationViewDefaultPaneBackground}"` — not a
    /// TemplateBinding to anything NavigationView itself exposes. Overriding that key alone
    /// is NOT sufficient, though (found post-hoc, by a visual regression once this was
    /// actually screenshotted): the template's "PaneDisplayMode states" VisualStateGroup has
    /// a `PaneNotOverlaying` state — the one active for our exact configuration,
    /// `PaneDisplayMode="Left"` with the window wide enough not to need overlay mode — whose
    /// `VisualState.Setters` include `RootSplitView.PaneBackground =
    /// {ThemeResource NavigationViewExpandedPaneBackground}`, a *different* key that
    /// unconditionally wins once that state is active. In the dark theme dictionary this
    /// second key resolves to `SolidBackgroundFillColorTransparent` — fully transparent, by
    /// design, so Mica shows through the expanded pane. That is why the pane rendered as a
    /// translucent Mica-tinted strip (picking up desktop wallpaper color) despite the
    /// `NavigationViewDefaultPaneBackground` override below: that key was never the one
    /// actually driving the visible background in this layout. Both keys must be overridden
    /// for the pane to render as the intended flat opaque navy in every pane-display mode this
    /// app might reach.
    /// </para>
    /// <para>
    /// Overriding that same ThemeResource key is therefore the correct, surgical equivalent of
    /// what the plan wanted, and — like the selection-state keys below — is set here in code
    /// rather than as a Window.Resources declaration, since any resource-dictionary
    /// declaration in this project's XAML hits the same compiler crash a &lt;Style&gt; element
    /// does. ThemeResource lookups climb the visual tree's resource dictionaries at runtime,
    /// so setting the key on Nav.Resources here is equivalent to declaring it in
    /// Window.Resources.
    /// </para>
    /// <para>
    /// The NavigationViewItemBackground*/Foreground* keys below are the same named
    /// ThemeResource keys NavigationViewItem's own control template already looks up for its
    /// pointer-over/selected states, for the same reason.
    /// </para>
    /// </summary>
    private void ConfigureNavAppearance()
    {
        var resources = Application.Current.Resources;

        Nav.Resources["NavigationViewDefaultPaneBackground"] = (Brush)resources["SidebarBackgroundBrush"];
        Nav.Resources["NavigationViewExpandedPaneBackground"] = (Brush)resources["SidebarBackgroundBrush"];

        Nav.Resources["NavigationViewItemBackgroundPointerOver"] = (Brush)resources["SidebarHoverBrush"];
        Nav.Resources["NavigationViewItemBackgroundSelected"] = (Brush)resources["SidebarActiveBrush"];
        Nav.Resources["NavigationViewItemBackgroundSelectedPointerOver"] = (Brush)resources["SidebarActiveBrush"];
        Nav.Resources["NavigationViewItemBackgroundSelectedPressed"] = (Brush)resources["SidebarActiveBrush"];
        Nav.Resources["NavigationViewItemForegroundSelected"] = (Brush)resources["SidebarTextActiveBrush"];
        Nav.Resources["NavigationViewItemForegroundSelectedPointerOver"] = (Brush)resources["SidebarTextActiveBrush"];
        Nav.Resources["NavigationViewItemForegroundSelectedPressed"] = (Brush)resources["SidebarTextActiveBrush"];
    }

    /// <summary>
    /// Label + a hidden-by-default "Live" pill, standing in for ACARS/Map's live badge —
    /// see the InfoBadge text-content note on the field declarations above.
    /// </summary>
    private static (StackPanel Content, Border LivePill) BuildLabelWithLivePill(string label)
    {
        var pill = new Border
        {
            Background = (Brush)Application.Current.Resources["AccentBrush"],
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "Live",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Colors.White),
            },
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(pill);

        return (panel, pill);
    }

    /// <summary>
    /// Label + a hidden-by-default small dot — the AttentionDotInfoBadgeStyle fallback for
    /// the Flights item, only used if that framework resource is missing.
    /// </summary>
    private static (StackPanel Content, Ellipse Dot) BuildLabelWithDot(string label)
    {
        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            // app.css's --green (.nav-badge-dot background: #3EC7AC), inline rather than a
            // Theme/Tokens.xaml resource — adding any new resource entry to that file reliably
            // crashes XamlCompiler.exe on this SDK (verified empirically: a single extra
            // <Color>/<SolidColorBrush> pair, otherwise identical to the ones already there,
            // reproduces the same silent exit-code-1 failure documented for <Style> elements).
            Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0x3E, 0xC7, 0xAC)),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(dot);

        return (panel, dot);
    }

    private void OnSessionStateChanged(object? sender, FlightSessionState state)
    {
        var isLive = ShellBadgeLogic.IsFlightLive(state);
        var visibility = isLive ? Visibility.Visible : Visibility.Collapsed;

        if (_acarsLivePill != null) _acarsLivePill.Visibility = visibility;
        if (_mapLivePill != null) _mapLivePill.Visibility = visibility;
    }

    private void OnUnreadCountChanged(object? sender, int count)
    {
        if (_messagesItem == null) return;

        _messagesItem.InfoBadge = count > 0
            ? new InfoBadge { Value = Math.Min(count, 99) }
            : null;
    }

    private void OnFlightCompleted(object? sender, FlightRecord flight) => _ = RefreshFlightsBadgeAsync();

    private async Task RefreshFlightsBadgeAsync()
    {
        if (_flightsItem == null) return;

        try
        {
            var flights = await _flightRepository.GetAllAsync();
            var show = ShellBadgeLogic.HasUnsyncedPireps(flights);

            if (_dotBadgeStyle != null)
            {
                _flightsItem.InfoBadge = show ? new InfoBadge { Style = _dotBadgeStyle } : null;
            }
            else if (_flightsDot != null)
            {
                _flightsDot.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Shell] Failed to refresh the Flights nav badge");
        }
    }

    /// <summary>
    /// Native replacement for LandingResultModal.razor's global popup: fires after every
    /// landing, on the desktop shell rather than a page, so it has to live here. Context
    /// lookups (flight record, landing history for personal-best/ranking) are best-effort —
    /// per the original modal's own comment, a failure here must never block the popup, so
    /// on any exception we log and fall through with flight: null / history: null rather
    /// than letting the pilot miss their landing report entirely.
    /// </summary>
    private async void OnLandingResultReady(object? sender, LandingResult result)
    {
        FlightRecord? flight = null;
        List<LandingResult>? history = null;
        try
        {
            history = await _landingRepository.GetAllAsync();
            if (Guid.TryParse(result.FlightId, out var fid))
                flight = (await _flightRepository.GetAllAsync()).FirstOrDefault(f => f.Id == fid);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Shell] Landing report context lookup failed");
        }

        var report = LandingReportBuilder.Build(result, flight, history);
        var dialog = LandingResultDialog.Build(RootGrid.XamlRoot, report);

        // Auto-dismiss after 60s, matching the original modal. The timer callback runs on a
        // thread-pool thread, so Hide() -- a UI-tree operation -- must be marshaled back onto
        // the window's dispatcher via TryEnqueue; calling it directly from the timer thread
        // would throw (WinUI UI objects are single-threaded-apartment-affine). Hide() is safe
        // to call while ShowAsync() is still awaiting -- it forces that await to complete with
        // ContentDialogResult.None, same as the user clicking the close button. If the dialog
        // is already closed by the time the timer fires (the pilot dismissed it manually), the
        // `using` below has already disposed the timer, so this path only runs when the dialog
        // is still open.
        using var autoClose = new System.Threading.Timer(
            _ => DispatcherQueue.TryEnqueue(() => dialog.Hide()),
            null,
            TimeSpan.FromSeconds(60),
            System.Threading.Timeout.InfiniteTimeSpan);

        await dialog.ShowAsync();
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
    /// Sends the rail's selection to whichever host owns that destination: a native Page via
    /// <see cref="NativeHost"/> if the destination has migrated, otherwise the legacy Blazor
    /// host via <see cref="UiHost"/> as before. Only one of the two is ever visible at a time.
    /// </summary>
    private void Navigate(NavDestination destination)
    {
        if (destination.NativePageType != null)
        {
            NativeHost.Navigate(destination.NativePageType);
            NativeHost.Visibility = Visibility.Visible;
            UiHost.Visibility = Visibility.Collapsed;
            return;
        }

        NativeHost.Visibility = Visibility.Collapsed;
        UiHost.Visibility = Visibility.Visible;

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
        if (NativeHost.Visibility == Visibility.Visible && NativeHost.CanGoBack) { NativeHost.GoBack(); return; }
        if (UiHost.CoreWebView2?.CanGoBack == true) UiHost.CoreWebView2.GoBack();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _sessionManager.SessionStateChanged -= OnSessionStateChanged;
        _messagingService.UnreadCountChanged -= OnUnreadCountChanged;
        _sessionManager.FlightCompleted -= OnFlightCompleted;
        _alertService.LandingResultReady -= OnLandingResultReady;

        _subclass.Detach();
        (Application.Current as App)?.Shutdown();
    }
}
