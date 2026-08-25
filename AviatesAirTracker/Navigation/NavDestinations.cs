namespace AviatesAirTracker.Navigation;

/// <summary>One entry in the shell's navigation rail.</summary>
/// <param name="Tag">Stable identifier, also the NavigationViewItem tag.</param>
/// <param name="Label">Sidebar text, matching the current Blazor sidebar exactly.</param>
/// <param name="Route">Route on the legacy Blazor host.</param>
/// <param name="Group">Sidebar section heading this entry sits under, or null for none.</param>
/// <param name="NativePageType">
/// When set, this destination is hosted by <c>ShellWindow</c>'s native <c>Frame</c> instead of
/// the WebView2/Blazor bridge. Null (the default) for every destination that has not yet
/// migrated to a native WinUI page.
/// </param>
public sealed record NavDestination(string Tag, string Label, string Route, string? Group, Type? NativePageType = null);

/// <summary>
/// Single source of truth for shell navigation. Order, wording and grouping are copied
/// verbatim from <c>Shared/MainLayout.razor</c> lines 68–207, so the native rail is
/// indistinguishable from the sidebar it replaces. As pages migrate to XAML, only the
/// resolution in <c>ShellWindow.Navigate</c> changes — this list does not.
/// </summary>
public static class NavDestinations
{
    public static readonly IReadOnlyList<NavDestination> All =
    [
        new("dashboard", "Dashboard", "/",          "Overview", NativePageType: typeof(Pages.DashboardPage)),
        new("flights",   "Flights",   "/flights",   "Overview", NativePageType: typeof(Pages.FlightsPage)),
        new("routes",    "Routes",    "/routes",    "Overview", NativePageType: typeof(Pages.RoutesPage)),
        new("fleet",     "Fleet",     "/fleet",     "Overview"),
        new("pilot-hub", "Pilot Hub", "/pilot-hub", "Pilot", NativePageType: typeof(Pages.PilotHubPage)),
        new("acars",     "ACARS",     "/acars",     "Pilot"),
        new("map",       "Map",       "/map",       "Pilot"),
        new("messages",  "Messages",  "/messages",  "Pilot"),
        new("news",      "News",      "/news",      "Community"),
        new("events",    "Events",    "/events",    "Community"),
        new("support",   "Support",   "/support",   "Community"),
        // Settings sits below the connection-status block in the sidebar footer,
        // outside every section heading.
        new("settings",  "Settings",  "/settings",  null),
    ];
}
