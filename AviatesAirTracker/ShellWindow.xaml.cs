using AviatesAirTracker.Navigation;
using AviatesAirTracker.Platform;
using AviatesAirTracker.Services;
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
    private readonly SettingsService _settings;

    public ShellWindow(MainViewModel viewModel, FilePickerService filePicker, SettingsService settings)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _filePicker = filePicker;
        _settings = settings;

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
                // must never let a signed-out pilot skip the login gate.
                Nav.IsPaneVisible = false;
                Nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
                UiHost.CoreWebView2.Navigate($"{App.LegacyUiUrl}/login?shell=native");
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

            Nav.MenuItems.Add(StyleNavItem(new NavigationViewItem
            {
                Content = destination.Label,
                Tag = destination.Tag,
            }));
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
    /// named `RootSplitView` part whose Pane color comes from a fixed
    /// `PaneBackground="{ThemeResource NavigationViewDefaultPaneBackground}"` — not a
    /// TemplateBinding to anything NavigationView itself exposes. Overriding that same
    /// ThemeResource key is therefore the correct, surgical equivalent of what the plan
    /// wanted, and — like the selection-state keys below — is set here in code rather than as
    /// a Window.Resources declaration, since any resource-dictionary declaration in this
    /// project's XAML hits the same compiler crash a &lt;Style&gt; element does. ThemeResource
    /// lookups climb the visual tree's resource dictionaries at runtime, so setting the key on
    /// Nav.Resources here is equivalent to declaring it in Window.Resources.
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

        Nav.Resources["NavigationViewItemBackgroundPointerOver"] = (Brush)resources["SidebarHoverBrush"];
        Nav.Resources["NavigationViewItemBackgroundSelected"] = (Brush)resources["SidebarActiveBrush"];
        Nav.Resources["NavigationViewItemBackgroundSelectedPointerOver"] = (Brush)resources["SidebarActiveBrush"];
        Nav.Resources["NavigationViewItemBackgroundSelectedPressed"] = (Brush)resources["SidebarActiveBrush"];
        Nav.Resources["NavigationViewItemForegroundSelected"] = (Brush)resources["SidebarTextActiveBrush"];
        Nav.Resources["NavigationViewItemForegroundSelectedPointerOver"] = (Brush)resources["SidebarTextActiveBrush"];
        Nav.Resources["NavigationViewItemForegroundSelectedPressed"] = (Brush)resources["SidebarTextActiveBrush"];
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
