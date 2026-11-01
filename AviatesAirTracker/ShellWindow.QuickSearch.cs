using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using AviatesAirTracker.Navigation;
using AviatesAirTracker.Services;
using AviatesAirTracker.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Serilog;
using Windows.System;

namespace AviatesAirTracker;

// Ctrl+K search: the rail's own search box (NavigationView.AutoSuggestBox) over pages, a few
// quick actions, flights and landings. Ctrl+K opens the pane if needed and focuses it.
public sealed partial class ShellWindow
{
    private AutoSuggestBox _search = null!;
    private List<FlightRecord> _searchFlights = [];
    private List<LandingResult> _searchLandings = [];

    private static readonly Dictionary<string, string[]> PageKeywords = new()
    {
        ["dashboard"] = ["home", "overview"],
        ["flights"] = ["logbook", "pirep", "history"],
        ["routes"] = ["book", "booking", "schedule", "charter"],
        ["fleet"] = ["aircraft", "registration"],
        ["pilot-hub"] = ["profile", "rank", "booking", "activate"],
        ["acars"] = ["tracking", "simbrief", "ofp", "metar", "connect", "readiness"],
        ["map"] = ["live map", "vatsim", "ivao", "weather", "traffic"],
        ["landings"] = ["landing", "touchdown", "analysis", "debrief", "butter", "score"],
        ["statistics"] = ["stats", "career", "hours", "records"],
        ["messages"] = ["inbox", "friends", "dm", "chat", "broadcast"],
        ["news"] = ["announcements"],
        ["events"] = ["fly-in", "group flight"],
        ["support"] = ["help", "faq", "contact", "bug"],
        ["settings"] = ["preferences", "push to talk", "ptt", "efb", "tray", "notifications", "startup", "update"],
    };

    private static readonly (string Id, string Title, string[] Keywords)[] SearchActions =
    [
        ("connect", "Connect to the simulator", ["msfs", "simconnect", "connect"]),
        ("simbrief", "Import a SimBrief plan", ["ofp", "flight plan", "simbrief"]),
        ("pireps", "Send unsent PIREPs", ["pirep", "sync", "retry"]),
    ];

    private void InitializeQuickSearch()
    {
        _search = new AutoSuggestBox
        {
            PlaceholderText = "Search (Ctrl+K)",
            QueryIcon = new SymbolIcon(Symbol.Find),
        };
        AutomationProperties.SetName(_search, "Search pages, flights and landings");
        _search.GotFocus += async (_, _) => await LoadSearchDataAsync();
        _search.TextChanged += OnSearchTextChanged;
        _search.QuerySubmitted += OnSearchQuerySubmitted;
        Nav.AutoSuggestBox = _search;

        var shortcut = new KeyboardAccelerator { Key = VirtualKey.K, Modifiers = VirtualKeyModifiers.Control };
        shortcut.Invoked += (_, e) =>
        {
            e.Handled = true;
            FocusSearch();
        };
        RootGrid.KeyboardAccelerators.Add(shortcut);
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    private void FocusSearch()
    {
        if (!Nav.IsPaneVisible) return; // signed out: the login page owns the window
        if (!Nav.IsPaneOpen) Nav.IsPaneOpen = true;
        _search.Focus(FocusState.Keyboard);
    }

    private async Task LoadSearchDataAsync()
    {
        try
        {
            _searchFlights = await _flightRepository.GetAllAsync();
            _searchLandings = await _landingRepository.GetAllAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Shell] Search data load failed");
        }
    }

    private IReadOnlyList<QuickSearchResult> RunSearch(string query) => QuickSearchLogic.Search(
        query,
        NavDestinations.All.Select(d => new QuickSearchLogic.PageEntry(d.Tag, d.Label,
            PageKeywords.TryGetValue(d.Tag, out var k) ? k : [])),
        SearchActions,
        _searchFlights,
        _searchLandings);

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var results = RunSearch(sender.Text);
        sender.ItemsSource = results.Count > 0
            ? results
            : string.IsNullOrWhiteSpace(sender.Text) ? null : new[] { "No matches" };
    }

    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var result = args.ChosenSuggestion as QuickSearchResult ?? RunSearch(args.QueryText).FirstOrDefault();
        if (result == null) return;

        sender.Text = "";
        sender.ItemsSource = null;
        NativeHost.Focus(FocusState.Programmatic);

        switch (result.Kind)
        {
            case QuickSearchKind.Page:
                NavigateToTag(result.Target);
                break;
            case QuickSearchKind.Landing:
                NavigateToTag("landings", result.Target);
                break;
            case QuickSearchKind.Flight:
                if (result.Target.Length > 0) NavigateToTag("landings", result.Target);
                else NavigateToTag("flights");
                break;
            case QuickSearchKind.Action:
                RunSearchAction(result.Target);
                break;
        }
    }

    private void RunSearchAction(string id)
    {
        switch (id)
        {
            case "connect":
                App.GetService<SimConnectManager>().TryConnect();
                NavigateToTag("acars");
                break;
            case "simbrief":
                NavigateToTag("acars");
                break;
            case "pireps":
                _ = _pirepSync.RetryPendingAsync();
                NavigateToTag("flights");
                break;
        }
    }
}
