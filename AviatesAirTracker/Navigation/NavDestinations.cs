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
/// <param name="IconGlyph">
/// Segoe Fluent Icons codepoint for this item's <c>NavigationViewItem.Icon</c>. Every item MUST
/// have one: WinUI's NavigationView only properly collapses to icon-only in compact/closed-pane
/// mode when an Icon is set -- without one, the pane just squeezes the item's Content (label
/// text, here) into the narrow compact width instead of hiding it, which is what the
/// hamburger-toggle "compressed text" bug actually was before icons were added. Written as a
/// \uXXXX escape (not a raw PUA character) so it stays legible/diffable in a plain editor.
/// </param>
public sealed record NavDestination(string Tag, string Label, string Route, string? Group, Type? NativePageType, string IconGlyph);

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
        new("dashboard", "Dashboard", "/",          "Overview", typeof(Pages.DashboardPage), IconGlyph: ""), // Home
        new("flights",   "Flights",   "/flights",   "Overview", typeof(Pages.FlightsPage),   IconGlyph: ""), // Airplane
        new("routes",    "Routes",    "/routes",    "Overview", typeof(Pages.RoutesPage),    IconGlyph: ""), // MapDirections
        new("fleet",     "Fleet",     "/fleet",     "Overview", typeof(Pages.FleetPage),     IconGlyph: ""), // AllApps
        new("pilot-hub", "Pilot Hub", "/pilot-hub", "Pilot", typeof(Pages.PilotHubPage),      IconGlyph: ""), // ContactInfo
        new("acars",     "ACARS",     "/acars",     "Pilot", typeof(Pages.AcarsPage),         IconGlyph: ""), // Radar
        new("map",       "Map",       "/map",       "Pilot", typeof(Pages.MapPage),           IconGlyph: ""), // Nav2DMapView
        new("messages",  "Messages",  "/messages",  "Pilot", typeof(Pages.MessagesPage),      IconGlyph: ""), // Message
        new("news",      "News",      "/news",      "Community", typeof(Pages.NewsPage),      IconGlyph: ""), // PostUpdate
        new("events",    "Events",    "/events",    "Community", typeof(Pages.EventsPage),    IconGlyph: ""), // Calendar
        new("support",   "Support",   "/support",   "Community", typeof(Pages.SupportPage),   IconGlyph: ""), // Help
        // Settings sits below the connection-status block in the sidebar footer,
        // outside every section heading.
        new("settings",  "Settings",  "/settings",  null, typeof(Pages.SettingsPage),          IconGlyph: ""), // Settings
    ];
}
