using AviatesAirTracker.Core.Reference;
using AviatesAirTracker.Services;
using AviatesAirTracker.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using Windows.UI;

namespace AviatesAirTracker.Pages;

/// <summary>
/// Pre-formatted display row for one route card in the grid. Plain mutable class with
/// { get; set; } properties, not a record (CS8852 risk, same as every prior display-row class
/// in this migration). <see cref="Source"/> is the full route object Task 3's booking dialog
/// needs when the card's "Book" button is tapped.
/// </summary>
public sealed class RouteDisplayRow
{
    public string RouteLabel { get; set; } = "";
    public string RouteTooltip { get; set; } = "";
    public ImageSource? OriginFlagSource { get; set; }
    public ImageSource? DestFlagSource { get; set; }
    public string SubText { get; set; } = "";
    public Brush IconColor { get; set; } = RoutesPage.FleetBlue;
    public Brush IconBackground { get; set; } = RoutesPage.FleetBlueBg;

    public string AircraftType { get; set; } = "";
    public Visibility ShowAircraftChip { get; set; } = Visibility.Collapsed;
    public string Frequency { get; set; } = "";
    public Visibility ShowFrequencyChip { get; set; } = Visibility.Collapsed;
    public string FlightNumber { get; set; } = "";
    public Visibility ShowFlightNumberChip { get; set; } = Visibility.Collapsed;

    public string DistanceText { get; set; } = "";
    public string BlockTimeText { get; set; } = "";

    public AviatesRoute Source { get; set; } = null!;
}

public sealed partial class RoutesPage : Page
{
    // Fleet-group icon colors: matches Routes.razor's own GetFleetColor switch exactly
    // (mainline -> blue, cargo -> amber, cityhopper -> cyan, default -> blue), using the same
    // hex values app.css's --accent/--amber/--cyan tokens resolve to in the dark theme.
    internal static readonly Brush FleetBlue = new SolidColorBrush(Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC));
    internal static readonly Brush FleetBlueBg = new SolidColorBrush(Color.FromArgb(0x1F, 0x4D, 0x9B, 0xEC));
    private static readonly Brush FleetAmber = new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B));
    private static readonly Brush FleetAmberBg = new SolidColorBrush(Color.FromArgb(0x1F, 0xF5, 0x9E, 0x0B));
    private static readonly Brush FleetCyan = new SolidColorBrush(Color.FromArgb(0xFF, 0x06, 0xB6, 0xD4));
    private static readonly Brush FleetCyanBg = new SolidColorBrush(Color.FromArgb(0x1F, 0x06, 0xB6, 0xD4));

    private static readonly Brush PositionCountBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1));

    // ShellWindow.xaml.cs's ConfigureNavAppearance color convention for the nav rail's own
    // active item, reused here for the tab bar -- same lookup mechanism FlightsPage.xaml.cs's
    // tab bar already uses (existing Theme/Tokens.xaml resource entries, not new ones).
    private static readonly Brush TabActiveBackground = (Brush)Application.Current.Resources["SidebarActiveBrush"];
    private static readonly Brush TabActiveForeground = (Brush)Application.Current.Resources["SidebarTextActiveBrush"];
    private static readonly Brush TabInactiveForeground = (Brush)Application.Current.Resources["SidebarTextBrush"];
    private static readonly Brush TabInactiveBackground = new SolidColorBrush(Colors.Transparent);

    public ObservableCollection<RouteDisplayRow> RouteRows { get; } = [];

    private readonly RoutesViewModel _viewModel;

    // Lives on the page, not the ViewModel -- matches Routes.razor's own _activeTab/_searchQuery
    // field placement (UI-local selection state; RoutesViewModel.LoadRoutesAsync takes both as
    // parameters rather than owning them itself, per Task 1's design).
    private string _activeTab = "all";
    private string _searchQuery = "";
    private bool _showSearch;

    // 400ms debounce, reset (Stop+Start) on every keystroke -- the native equivalent of
    // Routes.razor's OnSearchInput Task.Delay+CancellationTokenSource cancel-and-restart pattern.
    private DispatcherTimer? _searchDebounce;

    public RoutesPage()
    {
        InitializeComponent();

        _viewModel = App.GetService<RoutesViewModel>();
        DataContext = _viewModel;
        RouteCardsList.ItemsSource = RouteRows;

        UpdateTabVisuals();

        Loaded += async (_, _) =>
        {
            ShowLoadingState();
            await _viewModel.SyncPositionAsync();
            await _viewModel.LoadRoutesAsync(_activeTab, _searchQuery);
            UpdatePositionBanner();
            RebuildRouteCards();
        };
    }

    // ── Route card grid columns ─────────────────────────────────────────────────────────────

    // Minimum comfortable card width before dropping to fewer columns, and the right/bottom
    // gutter baked into each card's own Margin (see the DataTemplate in RoutesPage.xaml).
    private const double CardMinWidth = 360;
    private const double CardGutter = 16;

    // ItemsWrapGrid wraps at a fixed ItemWidth, so a hardcoded card width leaves a dead gap
    // whenever the available width isn't an exact multiple of it (the bug reported after Task 4:
    // cards stopped short of the window edge instead of filling it). Recomputed on every resize
    // so columns always stretch to fill RouteCardsList's own width exactly.
    private void RouteCardsList_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateRouteGridColumns();

    private void UpdateRouteGridColumns()
    {
        if (RouteCardsList.ItemsPanelRoot is not ItemsWrapGrid panel) return;

        var available = RouteCardsList.ActualWidth;
        if (available <= 0) return;

        var columns = Math.Max(1, (int)Math.Floor((available + CardGutter) / (CardMinWidth + CardGutter)));
        panel.ItemWidth = Math.Floor(available / columns);
    }

    // ── Position instrument banner ──────────────────────────────────────────────────────────

    private void UpdatePositionBanner()
    {
        var iata = _viewModel.CurrentAirportIata;
        var locked = !string.IsNullOrEmpty(iata);

        PositionLockedBorder.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        PositionFreeBorder.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        if (!locked) return;

        PositionIataText.Text = iata;

        var n = _viewModel.Routes.Count;
        PositionRouteCountText.Inlines.Clear();
        PositionRouteCountText.Inlines.Add(new Run
        {
            Text = n.ToString(CultureInfo.InvariantCulture),
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = PositionCountBrush,
        });
        PositionRouteCountText.Inlines.Add(new Run { Text = $" route{(n == 1 ? "" : "s")} available" });
    }

    // Matches Routes.razor's ClearPosition() exactly: SetPositionAsync then a separate reload
    // call, not a single combined step -- RoutesViewModel.ClearPositionAsync deliberately does
    // not reload internally (Task 1's design), so this page calls LoadRoutesAsync itself right
    // after, with its own current tab/search state.
    private async void OnRepositionTapped(object sender, TappedRoutedEventArgs e)
    {
        await _viewModel.ClearPositionAsync();
        await ReloadAsync();
    }

    // ── Tab bar ──────────────────────────────────────────────────────────────────────────────

    private async void OnTabTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border { Tag: string tab } || tab == _activeTab) return; // matches SwitchTab's `if (_activeTab == tab) return;`

        _activeTab = tab;
        _searchQuery = ""; // matches SwitchTab's exact `_searchQuery = ""` reset
        SearchBox.Text = "";
        UpdateTabVisuals();
        await ReloadAsync();
    }

    private void UpdateTabVisuals()
    {
        ApplyTabVisual(TabAllBorder, TabAllText, "all");
        ApplyTabVisual(TabMainlineBorder, TabMainlineText, "mainline");
        ApplyTabVisual(TabCargoBorder, TabCargoText, "cargo");
        ApplyTabVisual(TabRegionalBorder, TabRegionalText, "cityhopper");
    }

    private void ApplyTabVisual(Border border, TextBlock text, string tab)
    {
        var isActive = _activeTab == tab;
        border.Background = isActive ? TabActiveBackground : TabInactiveBackground;
        text.Foreground = isActive ? TabActiveForeground : TabInactiveForeground;
    }

    // ── Search ───────────────────────────────────────────────────────────────────────────────

    private async void OnFindRouteTapped(object sender, TappedRoutedEventArgs e)
    {
        _showSearch = !_showSearch;
        SearchBox.Visibility = _showSearch ? Visibility.Visible : Visibility.Collapsed;

        // Matches ToggleSearch()'s exact behavior: closing the search box while a query is
        // active clears it and reloads.
        if (!_showSearch && !string.IsNullOrEmpty(_searchQuery))
        {
            _searchQuery = "";
            SearchBox.Text = "";
            await ReloadAsync();
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = SearchBox.Text;

        _searchDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _searchDebounce.Stop();
        _searchDebounce.Tick -= OnSearchDebounceTick;
        _searchDebounce.Tick += OnSearchDebounceTick;
        _searchDebounce.Start();
    }

    private async void OnSearchDebounceTick(object? sender, object e)
    {
        _searchDebounce?.Stop();
        await ReloadAsync();
    }

    // ── Reload / loading / empty states / result count / card grid ─────────────────────────────

    private async System.Threading.Tasks.Task ReloadAsync()
    {
        ShowLoadingState();
        await _viewModel.LoadRoutesAsync(_activeTab, _searchQuery);
        UpdatePositionBanner();
        RebuildRouteCards();
    }

    private void ShowLoadingState()
    {
        LoadingPanel.Visibility = Visibility.Visible;
        EmptyLockedPanel.Visibility = Visibility.Collapsed;
        EmptyGenericPanel.Visibility = Visibility.Collapsed;
        ResultCountText.Visibility = Visibility.Collapsed;
        RouteCardsList.Visibility = Visibility.Collapsed;
    }

    private void RebuildRouteCards()
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        RouteRows.Clear();

        if (_viewModel.Routes.Count == 0)
        {
            RouteCardsList.Visibility = Visibility.Collapsed;
            ResultCountText.Visibility = Visibility.Collapsed;

            // Two distinct empty states, matching Routes.razor's own branch exactly: a locked
            // position with zero departures gets the "no scheduled routes" panel; every other
            // empty case (free position, or a search with no matches) gets the generic panel.
            var lockedNoResults = !string.IsNullOrEmpty(_viewModel.CurrentAirportIata) && string.IsNullOrEmpty(_searchQuery);
            EmptyLockedPanel.Visibility = lockedNoResults ? Visibility.Visible : Visibility.Collapsed;
            EmptyGenericPanel.Visibility = lockedNoResults ? Visibility.Collapsed : Visibility.Visible;

            if (lockedNoResults)
            {
                EmptyLockedIataText.Text = _viewModel.CurrentAirportIata;
            }
            else
            {
                EmptyGenericText.Text = string.IsNullOrEmpty(_searchQuery)
                    ? "No routes found."
                    : $"No routes matching \"{_searchQuery}\".";
            }
            return;
        }

        EmptyLockedPanel.Visibility = Visibility.Collapsed;
        EmptyGenericPanel.Visibility = Visibility.Collapsed;
        RouteCardsList.Visibility = Visibility.Visible;

        if (_viewModel.TotalCount > 0)
        {
            ResultCountText.Text = $"Showing {_viewModel.Routes.Count} of {_viewModel.TotalCount} routes";
            ResultCountText.Visibility = Visibility.Visible;
        }
        else
        {
            ResultCountText.Visibility = Visibility.Collapsed;
        }

        foreach (var route in _viewModel.Routes)
        {
            var (iconFg, iconBg) = FleetColorBrushes(route.FleetGroup);

            RouteRows.Add(new RouteDisplayRow
            {
                RouteLabel = $"{route.OriginIata} → {route.DestIata}",
                RouteTooltip = $"{route.OriginIata} → {route.DestIata}",
                OriginFlagSource = LoadFlagImage(route.OriginIata),
                DestFlagSource = LoadFlagImage(route.DestIata),
                SubText = string.IsNullOrEmpty(route.Notes)
                    ? $"{route.OriginName} to {route.DestName}"
                    : $"{route.OriginName} to {route.DestName} — {route.Notes}",
                IconColor = iconFg,
                IconBackground = iconBg,
                AircraftType = route.AircraftType,
                ShowAircraftChip = string.IsNullOrEmpty(route.AircraftType) ? Visibility.Collapsed : Visibility.Visible,
                Frequency = route.Frequency,
                ShowFrequencyChip = string.IsNullOrEmpty(route.Frequency) ? Visibility.Collapsed : Visibility.Visible,
                FlightNumber = route.FlightNumber,
                ShowFlightNumberChip = string.IsNullOrEmpty(route.FlightNumber) ? Visibility.Collapsed : Visibility.Visible,
                DistanceText = route.DistanceNm.ToString(CultureInfo.InvariantCulture),
                BlockTimeText = route.BlockTimeDisplay,
                Source = route,
            });
        }

        // RouteCardsList was Collapsed (zero size, no layout) while empty/loading, so its own
        // SizeChanged may not have fired with a usable width yet by the time rows exist -- call
        // this explicitly rather than rely solely on the event once it's Visible with content.
        UpdateRouteGridColumns();
    }

    // Matches Routes.razor's GetFleetColor exactly (mainline -> blue, cargo -> amber,
    // cityhopper -> cyan, anything else -> blue).
    private static (Brush Fg, Brush Bg) FleetColorBrushes(string fleetGroup) => fleetGroup switch
    {
        "mainline" => (FleetBlue, FleetBlueBg),
        "cargo" => (FleetAmber, FleetAmberBg),
        "cityhopper" => (FleetCyan, FleetCyanBg),
        _ => (FleetBlue, FleetBlueBg),
    };

    // Loads a small ISO-2 flag SVG for a departure/arrival ICAO (Routes.razor's own
    // "*_iata" route fields actually hold ICAO codes, not IATA -- confirmed against the
    // original, which calls IcaoCountry.IsoFromIcao(route.OriginIata) directly), same technique
    // FlightsPage.xaml.cs's LoadFlagImage already established. Never throws; returns null when
    // the country can't be resolved or the flag file isn't shipped.
    private static ImageSource? LoadFlagImage(string? icao)
    {
        var iso = IcaoCountry.IsoFromIcao(icao);
        if (string.IsNullOrEmpty(iso)) return null;

        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot", "images", "flags", $"{iso}.svg");
        if (!System.IO.File.Exists(path)) return null;

        return new SvgImageSource(new Uri(path));
    }

    // ── Book button ──────────────────────────────────────────────────────────────────────────
    // A Page exposes XamlRoot directly (unlike ShellWindow, which needed RootGrid.XamlRoot) --
    // this.XamlRoot is correct here. Reload runs unconditionally after the dialog closes (a
    // reload after a failed/cancelled booking is a harmless no-op) -- simpler than threading a
    // success flag back out, matching the plan's Task 3 note.
    private async void OnBookTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RouteDisplayRow row }) return;
        await AviatesAirTracker.Dialogs.BookingDialog.ShowAsync(this.XamlRoot, _viewModel, row.Source);
        await ReloadAsync();
    }
}
