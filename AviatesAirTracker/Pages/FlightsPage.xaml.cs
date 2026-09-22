using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Core.Reference;
using AviatesAirTracker.Models;
using AviatesAirTracker.Platform;
using AviatesAirTracker.Services;
using AviatesAirTracker.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Windows.UI;

namespace AviatesAirTracker.Pages;

/// <summary>
/// Pre-formatted display row for the Flight Logbook table (Task 3). Named FlightLogRow rather
/// than the plan's literal "FlightDisplayRow" -- DashboardPage.xaml.cs already declares a public
/// sealed class FlightDisplayRow in this same AviatesAirTracker.Pages namespace (its Recent
/// Flights row), so reusing that name here would be a CS0101 duplicate-definition compile error,
/// not a style choice. Plain mutable class with { get; set; } properties, not a record (CS8852
/// risk, same as every prior display-row class in this migration).
/// </summary>
public sealed class FlightLogRow
{
    public string FlightNumber { get; set; } = "";
    public string DateText { get; set; } = "";
    public string RouteLabel { get; set; } = "";
    public string RouteTooltip { get; set; } = "";
    public ImageSource? DepartureFlagSource { get; set; }
    public ImageSource? ArrivalFlagSource { get; set; }
    public string AircraftType { get; set; } = "";
    public string DurationText { get; set; } = "";

    // "A · 87" when PrimaryLanding exists; "—" (plain muted text, no pill) otherwise -- reproduces
    // Flights.razor's own if/else (chip vs. bare "—" span) as a single bound TextBlock rather than
    // two visibility-toggled elements, by making the "no landing" case use a transparent chip
    // background and the same muted foreground Flights.razor's own else-branch uses.
    public string GradeChipText { get; set; } = "—";
    public Brush GradeChipColor { get; set; } = FlightsPage.TextMutedBrush;
    public Brush GradeChipBackground { get; set; } = FlightsPage.TransparentBrush;

    public string StatusLabel { get; set; } = "";
    public Brush StatusChipColor { get; set; } = FlightsPage.TextMutedBrush;
    public Brush StatusChipBackground { get; set; } = FlightsPage.TransparentBrush;

    // A flight with an open (Pending-status) deletion request renders at reduced opacity and
    // shows "Del. Pending" instead of its normal status chip -- Flights.razor's hasPendingDelete
    // check, reproduced here since it also gates which action icon (delete vs. force-remove)
    // Task 4 will wire up for this row.
    public double RowOpacity { get; set; } = 1.0;
    public Visibility ShowDeleteAction { get; set; } = Visibility.Collapsed;
    public Visibility ShowForceRemoveAction { get; set; } = Visibility.Collapsed;

    // Needed by Task 4's delete/force-remove handlers to know which flight a row's action
    // buttons act on.
    public FlightRecord Source { get; set; } = null!;
}

/// <summary>
/// Pre-formatted display row for the Pending Review tab's deletion-requests table (Task 4).
/// Reproduces Flights.razor's <c>_pendingRequests</c> table body: every request regardless of
/// status (not just open ones -- the tab shows Approved/Rejected history too), with Approve/
/// Reject actions shown only for still-open (Pending-status) rows and a reviewed-at timestamp
/// shown instead for already-resolved ones. Plain mutable class, not a record (CS8852 risk, same
/// as every prior display-row class in this migration).
/// </summary>
public sealed class DeletionRequestRow
{
    public string FlightNumber { get; set; } = "—";
    public string Route { get; set; } = "—";
    public string Reason { get; set; } = "";
    public string SubmittedText { get; set; } = "";

    public string StatusLabel { get; set; } = "";
    public Brush StatusColor { get; set; } = FlightsPage.TextMutedBrush;
    public Brush StatusBackground { get; set; } = FlightsPage.TransparentBrush;

    // Mutually exclusive, matching Flights.razor's own @if (req.Status == Pending) { buttons }
    // else { reviewed-at text } branch in the Action column.
    public Visibility ShowActions { get; set; } = Visibility.Collapsed;
    public Visibility ShowReviewedText { get; set; } = Visibility.Collapsed;
    public string ReviewedText { get; set; } = "";

    // Needed by Task 4's approve/reject handlers to know which request a row's buttons act on.
    public FlightDeletionRequest Source { get; set; } = null!;
}

public sealed partial class FlightsPage : Page
{
    // Shared brush constants used both by this page's own chip-color mapping below and as
    // FlightLogRow's default property values (same "internal-ish" reuse convention
    // PilotHubPage.xaml.cs established for its own grade brushes -- these are private since
    // nothing outside this file needs them, unlike PilotHubPage's internal ones which
    // LandingDisplayRow, a type in a different file, consumes).
    internal static readonly Brush TransparentBrush = new SolidColorBrush(Colors.Transparent);
    internal static readonly Brush TextMutedBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x64, 0x74, 0x8B));

    // Grade chip colors: reuses the exact same four hex values PilotHubPage.xaml.cs's Task 4
    // already established for its own grade chips (#3EC7AC/#4D9BEC/#F59E0B/#EF4444), rather than
    // redeclaring a second copy of the same four brushes -- PilotHubPage's are `internal static`
    // specifically so other pages in this assembly can share them.
    private static readonly Brush GradeGreenBg = new SolidColorBrush(Color.FromArgb(0x1F, 0x3E, 0xC7, 0xAC));
    private static readonly Brush GradeBlueBg = new SolidColorBrush(Color.FromArgb(0x1F, 0x4D, 0x9B, 0xEC));
    private static readonly Brush GradeAmberBg = new SolidColorBrush(Color.FromArgb(0x1F, 0xF5, 0x9E, 0x0B));
    private static readonly Brush GradeRedBg = new SolidColorBrush(Color.FromArgb(0x1F, 0xEF, 0x44, 0x44));

    // Status chip colors: app.css's .status-chip.active/.done/.pending rules (verified directly)
    // -- active = green, done = accent blue, pending = amber. "Del. Pending" (hasPendingDelete)
    // uses the same red app.css's own inline override for that case uses (var(--red)/--red-light).
    private static readonly Brush StatusGreen = new SolidColorBrush(Color.FromArgb(0xFF, 0x3E, 0xC7, 0xAC));
    private static readonly Brush StatusGreenBg = new SolidColorBrush(Color.FromArgb(0x1F, 0x3E, 0xC7, 0xAC));
    private static readonly Brush StatusBlue = new SolidColorBrush(Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC));
    private static readonly Brush StatusBlueBg = new SolidColorBrush(Color.FromArgb(0x1F, 0x4D, 0x9B, 0xEC));
    private static readonly Brush StatusAmber = new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B));
    private static readonly Brush StatusAmberBg = new SolidColorBrush(Color.FromArgb(0x1F, 0xF5, 0x9E, 0x0B));
    private static readonly Brush StatusRed = new SolidColorBrush(Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));
    private static readonly Brush StatusRedBg = new SolidColorBrush(Color.FromArgb(0x1F, 0xEF, 0x44, 0x44));

    public ObservableCollection<FlightLogRow> FlightRows { get; } = [];
    public ObservableCollection<DeletionRequestRow> PendingRequestRows { get; } = [];

    // ShellWindow.xaml.cs's ConfigureNavAppearance color convention for the nav rail's own
    // active item, reused here for the tab bar (same lookup mechanism, different control --
    // these are existing Theme/Tokens.xaml resource entries, not new ones, so referencing them
    // via Application.Current.Resources carries none of the XamlCompiler-crash risk a new
    // resource entry would).
    private static readonly Brush TabActiveBackground = (Brush)Application.Current.Resources["SidebarActiveBrush"];
    private static readonly Brush TabActiveForeground = (Brush)Application.Current.Resources["SidebarTextActiveBrush"];
    private static readonly Brush TabInactiveForeground = (Brush)Application.Current.Resources["SidebarTextBrush"];
    private static readonly Brush TabInactiveBackground = new SolidColorBrush(Colors.Transparent);

    private readonly FlightsViewModel _viewModel;

    // Lives on the page, not the ViewModel -- matches Flights.razor's own _activeTab field
    // placement (UI-local selection state; the ViewModel doesn't need to know which tab is
    // showing, only this page's row-rebuild logic does).
    private string _activeTab = "all";

    // Filter dialog state -- also page-local, same reasoning as _activeTab above. Empty string /
    // null both mean "no filter applied" (matches the ComboBox's "All Aircraft" placeholder item).
    private string _filterSearch = "";
    private string? _filterAircraft = null;

    public FlightsPage()
    {
        InitializeComponent();

        InteractiveEffects.Attach(TabAllBorder, TabActiveBorder, TabCompletedBorder, TabPendingBorder, FilterBorder);

        _viewModel = App.GetService<FlightsViewModel>();
        DataContext = _viewModel;
        FlightsList.ItemsSource = FlightRows;
        DeletionRequestsList.ItemsSource = PendingRequestRows;

        UpdateTabVisuals();

        Loaded += async (_, _) =>
        {
            await _viewModel.RefreshAsync();
            RefreshStatStrip();
            RefreshActiveTabContent();
            await ApplyAdminVisibilityAsync();
        };
    }

    // Pending Review (the flight-deletion request queue) is only meaningful for the small
    // admin team moderating those requests -- every other pilot only ever sees their own
    // local requests here anyway (deletion requests are stored locally, not server-side), so
    // showing the tab to everyone just invited confusion. Fails closed (tab stays hidden) on
    // any error, since accidentally exposing an admin-only panel is the wrong failure mode.
    private async Task ApplyAdminVisibilityAsync()
    {
        var settings = App.GetService<SettingsService>();
        var acarsKey = settings.Settings.AcarsKey.Trim();
        var isAdmin = !string.IsNullOrEmpty(acarsKey)
            && await App.GetService<AviatesBackendClient>().CheckIsAdminAsync(acarsKey);

        TabPendingBorder.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;

        // Was already showing the pending tab (e.g. re-navigated back to this page) but is no
        // longer admin -- switch back to a real tab instead of leaving an admin-only panel
        // open with its own tab button now hidden.
        if (!isAdmin && _activeTab == "pending")
        {
            _activeTab = "all";
            UpdateTabVisuals();
            RefreshActiveTabContent();
        }
    }

    private void OnTabTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border { Tag: string tab }) return;

        _activeTab = tab;
        UpdateTabVisuals();
        RefreshActiveTabContent();
    }

    // Dispatches to the right content builder for the active tab and toggles which of the two
    // sibling panels (Flight Logbook vs. Deletion Requests) is visible -- matching Flights.razor's
    // own top-level @if (_activeTab == "pending") { deletion-requests table } else { flight table }
    // branch. "pending" isn't one of FlightsFilterLogic.FilterByTab's cases (it falls through to
    // "all" there), so it's intercepted here before reaching RebuildFlightRows.
    private void RefreshActiveTabContent()
    {
        var isPending = _activeTab == "pending";
        FlightLogbookBorder.Visibility = isPending ? Visibility.Collapsed : Visibility.Visible;
        DeletionRequestsBorder.Visibility = isPending ? Visibility.Visible : Visibility.Collapsed;

        if (isPending) RebuildDeletionRequestRows();
        else RebuildFlightRows();
    }

    private void UpdateTabVisuals()
    {
        ApplyTabVisual(TabAllBorder, TabAllText, "all");
        ApplyTabVisual(TabActiveBorder, TabActiveText, "active");
        ApplyTabVisual(TabCompletedBorder, TabCompletedText, "completed");
        ApplyTabVisual(TabPendingBorder, TabPendingText, "pending");
    }

    private void ApplyTabVisual(Border border, TextBlock text, string tab)
    {
        var isActive = _activeTab == tab;
        border.Background = isActive ? TabActiveBackground : TabInactiveBackground;
        text.Foreground = isActive ? TabActiveForeground : TabInactiveForeground;
    }

    // Total Flights / Completed / Total Distance, set imperatively after RefreshAsync()
    // completes -- same pattern PilotHubPage.xaml.cs/DashboardPage.xaml.cs already use for
    // values that combine a raw ViewModel number with page-local formatting (Total Distance
    // here mirrors Flights.razor's own "N0 formatted number, em-dash when zero, unit shown
    // only when non-zero" rule; Completed is a small inline LINQ count over the ViewModel's
    // Flights collection since FlightsViewModel exposes no precomputed count of its own).
    private void RefreshStatStrip()
    {
        TotalFlightsText.Text = _viewModel.Flights.Count.ToString(CultureInfo.InvariantCulture);

        CompletedFlightsText.Text = _viewModel.Flights
            .Count(f => f.Status == FlightStatus.Completed)
            .ToString(CultureInfo.InvariantCulture);

        if (_viewModel.TotalNm > 0)
        {
            TotalDistanceText.Text = _viewModel.TotalNm.ToString("N0", CultureInfo.InvariantCulture);
            TotalDistanceUnitText.Visibility = Visibility.Visible;
        }
        else
        {
            TotalDistanceText.Text = "—";
            TotalDistanceUnitText.Visibility = Visibility.Collapsed;
        }

        // PendingRequests holds every deletion request regardless of status (the Pending Review
        // tab needs to show Approved/Rejected rows too), but Flights.razor's badge counts only
        // the genuinely-open ones -- matching that here rather than PendingRequests.Count, which
        // would include already-resolved requests in the nav badge.
        var openCount = _viewModel.PendingRequests.Count(r => r.Status == DeletionRequestStatus.Pending);
        PendingCountText.Text = openCount.ToString(CultureInfo.InvariantCulture);
    }

    // Loads a small ISO-2 flag SVG for a departure/arrival ICAO, or returns null when the
    // country can't be resolved or the flag file isn't shipped -- never throws, matching
    // Flights.razor's own markup, which simply omits the flag span when dep/arr is null. wwwroot
    // ships as loose Content next to the exe (same reasoning as DashboardPage.xaml.cs's hero
    // photo), so this is an absolute file path, not an ms-appx:// URI.
    private static ImageSource? LoadFlagImage(string? icao)
    {
        var iso = IcaoCountry.IsoFromIcao(icao);
        if (string.IsNullOrEmpty(iso)) return null;

        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot", "images", "flags", $"{iso}.svg");
        if (!System.IO.File.Exists(path)) return null;

        return new SvgImageSource(new Uri(path));
    }

    // Translates FlightsDisplay.GradeChipClass's CSS-class-name string into the (foreground,
    // background) brush pair this page's chip Border/TextBlock actually bind to.
    private static (Brush Foreground, Brush Background) GradeChipBrushes(string cssClass) => cssClass switch
    {
        "chip-green" => (PilotHubPage.GradeGreen, GradeGreenBg),
        "chip-blue" => (PilotHubPage.GradeBlue, GradeBlueBg),
        "chip-amber" => (PilotHubPage.GradeAmber, GradeAmberBg),
        _ => (PilotHubPage.GradeRed, GradeRedBg),
    };

    // Translates FlightsDisplay.StatusChipClass's CSS-class-name string ("active"/"done"/
    // "pending") into the (foreground, background) brush pair for the status chip.
    private static (Brush Foreground, Brush Background) StatusChipBrushes(string cssClass) => cssClass switch
    {
        "active" => (StatusGreen, StatusGreenBg),
        "done" => (StatusBlue, StatusBlueBg),
        _ => (StatusAmber, StatusAmberBg),
    };

    // Real implementation (Task 3), replacing Task 2's no-op stub. Called from Loaded and from
    // each tab's Tapped handler (both call sites already existed before this task). Builds one
    // FlightLogRow per flight FlightsFilterLogic.FilterByTab(...) returns for the active tab --
    // note "pending" isn't special-cased there (falls through to the default/"all" case), so the
    // Pending Review tab still shows the full flight table for now; the distinct
    // deletion-requests panel for that tab is Task 4's job (its own "Approve/Reject buttons
    // (Pending Review tab)" step), not this one's.
    private void RebuildFlightRows()
    {
        FlightRows.Clear();

        var filtered = FlightsFilterLogic.FilterByTab(_viewModel.Flights, _activeTab);

        if (_filterAircraft != null)
            filtered = filtered.Where(f => string.Equals(f.AircraftType, _filterAircraft, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(_filterSearch))
        {
            var q = _filterSearch;
            filtered = filtered.Where(f =>
                f.FlightNumber.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                f.DepartureICAO.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                f.ArrivalICAO.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var f in filtered)
        {
            var hasPendingDelete = _viewModel.PendingRequests
                .Any(r => r.FlightId == f.Id && r.Status == DeletionRequestStatus.Pending);

            var row = new FlightLogRow
            {
                FlightNumber = string.IsNullOrWhiteSpace(f.FlightNumber)
                    ? f.Id.ToString()[..8].ToUpperInvariant()
                    : f.FlightNumber,
                DateText = f.CreatedAt.ToLocalTime().ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
                RouteLabel = FlightsDisplay.RouteLabel(f),
                RouteTooltip = $"{f.DepartureICAO} → {f.ArrivalICAO}",
                DepartureFlagSource = LoadFlagImage(f.DepartureICAO),
                ArrivalFlagSource = LoadFlagImage(f.ArrivalICAO),
                AircraftType = string.IsNullOrWhiteSpace(f.AircraftType) ? "—" : f.AircraftType,
                DurationText = FlightsDisplay.FormatDuration(f.BlockTime),
                RowOpacity = hasPendingDelete ? 0.6 : 1.0,
                ShowDeleteAction = f.Status != FlightStatus.InProgress && !hasPendingDelete
                    ? Visibility.Visible : Visibility.Collapsed,
                ShowForceRemoveAction = f.Status == FlightStatus.InProgress
                    ? Visibility.Visible : Visibility.Collapsed,
                Source = f,
            };

            if (f.PrimaryLanding != null)
            {
                var grade = FlightsDisplay.GradeLetter(f.PrimaryLanding.LandingScore);
                var (fg, bg) = GradeChipBrushes(FlightsDisplay.GradeChipClass(f.PrimaryLanding.LandingScore));
                row.GradeChipText = $"{grade} · {f.PrimaryLanding.LandingScore}";
                row.GradeChipColor = fg;
                row.GradeChipBackground = bg;
            }
            // else: GradeChipText/GradeChipColor/GradeChipBackground keep FlightLogRow's own
            // defaults ("—", muted text, transparent background) -- matches Flights.razor's
            // plain-"—"-no-chip else branch.

            if (hasPendingDelete)
            {
                row.StatusLabel = "Del. Pending";
                row.StatusChipColor = StatusRed;
                row.StatusChipBackground = StatusRedBg;
            }
            else if (f.RequiresReview)
            {
                // See LandingComplianceChecker/FlightComplianceMonitor -- reuses the same
                // status-chip slot "Del. Pending" above overrides, same reasoning (a flight
                // can't be both, and neither wants the flight's real Completed/InProgress
                // status hidden underneath silently).
                row.StatusLabel = "Under Review";
                row.StatusChipColor = StatusRed;
                row.StatusChipBackground = StatusRedBg;
            }
            else
            {
                row.StatusLabel = FlightsDisplay.StatusLabel(f.Status);
                var (fg, bg) = StatusChipBrushes(FlightsDisplay.StatusChipClass(f.Status));
                row.StatusChipColor = fg;
                row.StatusChipBackground = bg;
            }

            FlightRows.Add(row);
        }

        // Exact empty-state copy from Flights.razor's "No flights yet" panel, except when the
        // active tab genuinely has flights but the Filter dialog's search/aircraft criteria
        // matched none of them -- showing "Connect MSFS and start your engines" there would be
        // actively wrong, so the title/subtitle swap to a filter-specific message instead. Toggles
        // the whole header-row-plus-ItemsControl group (FlightsTableContent), not just the
        // ItemsControl on its own -- Flights.razor's empty state replaces the entire <table>
        // (header included), not just its <tbody>.
        var isEmpty = FlightRows.Count == 0;
        var isFilterActive = _filterAircraft != null || !string.IsNullOrWhiteSpace(_filterSearch);
        if (isEmpty && isFilterActive)
        {
            EmptyStateTitle.Text = "No matching flights";
            EmptyStateSubtitle.Text = "Try a different search term or aircraft type, or clear the filter.";
        }
        else
        {
            EmptyStateTitle.Text = "No flights yet";
            EmptyStateSubtitle.Text = "Connect MSFS and start your engines to begin recording.";
        }
        EmptyStateText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        FlightsTableContent.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    // Translates a DeletionRequestStatus into the (label, foreground, background) triple for its
    // status chip -- Pending uses the same amber "pending" app.css treatment as the flight
    // table's default status chip, Approved reuses the blue "done" treatment, and Rejected gets
    // Flights.razor's own custom red inline style (not one of app.css's three named chip classes).
    private static (string Label, Brush Fg, Brush Bg) DeletionStatusVisuals(DeletionRequestStatus status) => status switch
    {
        DeletionRequestStatus.Pending => ("Pending", StatusAmber, StatusAmberBg),
        DeletionRequestStatus.Approved => ("Approved", StatusBlue, StatusBlueBg),
        _ => ("Rejected", StatusRed, StatusRedBg),
    };

    // Task 4: Pending Review tab's own table (distinct from the flight table -- Flights.razor
    // shows ALL deletion requests here regardless of status, not just open ones, since the tab
    // doubles as approve/reject history). Called from RefreshActiveTabContent whenever the
    // "pending" tab is active, in place of RebuildFlightRows.
    private void RebuildDeletionRequestRows()
    {
        PendingRequestRows.Clear();

        foreach (var req in _viewModel.PendingRequests)
        {
            var (label, fg, bg) = DeletionStatusVisuals(req.Status);
            var isPending = req.Status == DeletionRequestStatus.Pending;

            PendingRequestRows.Add(new DeletionRequestRow
            {
                FlightNumber = string.IsNullOrWhiteSpace(req.FlightNumber) ? "—" : req.FlightNumber,
                Route = string.IsNullOrWhiteSpace(req.Route) ? "—" : req.Route,
                Reason = req.Reason,
                SubmittedText = req.SubmittedAt.ToLocalTime().ToString("MM/dd/yy HH:mm", CultureInfo.InvariantCulture),
                StatusLabel = label,
                StatusColor = fg,
                StatusBackground = bg,
                ShowActions = isPending ? Visibility.Visible : Visibility.Collapsed,
                ShowReviewedText = isPending ? Visibility.Collapsed : Visibility.Visible,
                ReviewedText = req.ReviewedAt?.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture) ?? "",
                Source = req,
            });
        }

        // Exact empty-state copy from Flights.razor's "No deletion requests found." panel. Toggles
        // the whole header-row-plus-ItemsControl group (DeletionRequestsTableContent), not just
        // the ItemsControl on its own -- same reasoning as RebuildFlightRows's own
        // FlightsTableContent toggle just above.
        var isEmpty = PendingRequestRows.Count == 0;
        DeletionRequestsEmptyText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        DeletionRequestsTableContent.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── Filter dialog ────────────────────────────────────────────────────────────────────────────
    // Search text (flight number / departure / arrival ICAO) plus an aircraft-type picker, applied
    // on top of whichever tab is active. Same ContentDialog pattern as the delete/force-remove
    // dialogs below -- this page already proved ContentDialog + this.XamlRoot works correctly here.

    private async void OnFilterTapped(object sender, TappedRoutedEventArgs e) => await ShowFilterDialogAsync();

    private async Task ShowFilterDialogAsync()
    {
        var searchLabel = new TextBlock
        {
            FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1)),
            Text = "SEARCH",
        };
        var searchBox = new TextBox
        {
            PlaceholderText = "Flight number or airport (e.g. EGLL)...",
            Text = _filterSearch,
        };

        var aircraftLabel = new TextBlock
        {
            FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1)),
            Text = "AIRCRAFT TYPE", Margin = new Thickness(0, 8, 0, 0),
        };
        const string allAircraft = "All Aircraft";
        var aircraftTypes = _viewModel.Flights
            .Select(f => f.AircraftType)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase);
        var aircraftCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        aircraftCombo.Items.Add(allAircraft);
        foreach (var t in aircraftTypes) aircraftCombo.Items.Add(t);
        aircraftCombo.SelectedItem = _filterAircraft ?? allAircraft;
        if (aircraftCombo.SelectedIndex < 0) aircraftCombo.SelectedIndex = 0; // stale type from a since-deleted flight

        var panel = new StackPanel
        {
            Spacing = 6, Width = 340,
            Children = { searchLabel, searchBox, aircraftLabel, aircraftCombo },
        };

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = BuildDialogHeader("Filter Flights", "Narrows the logbook below by search text and/or aircraft type"),
            Content = panel,
            PrimaryButtonText = "Apply",
            SecondaryButtonText = "Clear Filters",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        dialog.SecondaryButtonClick += (_, _) =>
        {
            _filterSearch = "";
            _filterAircraft = null;
            UpdateFilterBadge();
            RefreshActiveTabContent();
        };

        dialog.PrimaryButtonClick += (_, _) =>
        {
            _filterSearch = searchBox.Text?.Trim() ?? "";
            var selected = aircraftCombo.SelectedItem as string;
            _filterAircraft = string.IsNullOrEmpty(selected) || selected == allAircraft ? null : selected;
            UpdateFilterBadge();
            RefreshActiveTabContent();
        };

        await dialog.ShowAsync();
    }

    private void UpdateFilterBadge()
    {
        var count = (string.IsNullOrWhiteSpace(_filterSearch) ? 0 : 1) + (_filterAircraft != null ? 1 : 0);
        FilterCountBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FilterCountText.Text = count.ToString(CultureInfo.InvariantCulture);
    }

    // ── Approve / Reject (Pending Review tab) ───────────────────────────────────────────────────
    // Plain Border-as-button elements, no confirmation dialog -- Flights.razor doesn't have one
    // for these two actions either, so none is added here.

    private async void OnApproveTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeletionRequestRow row }) return;
        await _viewModel.ApproveDeletionAsync(row.Source);
        RefreshStatStrip();
        RefreshActiveTabContent();
    }

    private async void OnRejectTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeletionRequestRow row }) return;
        await _viewModel.RejectDeletionAsync(row.Source);
        RefreshStatStrip();
        RefreshActiveTabContent();
    }

    // ── Delete-with-reason and force-remove dialogs ─────────────────────────────────────────────
    // WinUI's native ContentDialog, shown via ShowAsync() -- handles focus-trapping,
    // dismiss-on-escape, and the darkened backdrop for free, unlike a hand-rolled overlay Border
    // would. This is the first ContentDialog shown from a page hosted in ShellWindow's NativeHost
    // Frame anywhere in this migration; this.XamlRoot resolving correctly here was verified by
    // build + the plan's Task 4 note to check it explicitly (see this task's final report).

    private async void OnDeleteActionTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FlightLogRow row }) return;
        await ShowDeleteReasonDialogAsync(row.Source);
    }

    private async void OnForceRemoveActionTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FlightLogRow row }) return;
        await ShowForceRemoveDialogAsync(row.Source);
    }

    // Small shared header (title + muted subtitle) reused by both dialogs below, reproducing the
    // title/subtitle pairing from Flights.razor's own .modal-header markup for each modal
    // (ContentDialog.Title accepts any UIElement, not just a string, so this replaces the
    // single-line default rather than being tacked on as extra content).
    private static StackPanel BuildDialogHeader(string title, string subtitle) => new()
    {
        Spacing = 2,
        Children =
        {
            new TextBlock
            {
                Text = title, FontFamily = (FontFamily)Application.Current.Resources["DisplayFontBold"],
                FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9)),
            },
            new TextBlock { Text = subtitle, FontSize = 12, Foreground = TextMutedBrush, TextWrapping = TextWrapping.Wrap },
        },
    };

    // Exact copy from Flights.razor's delete-with-reason modal: title, subtitle, reason-field
    // label, placeholder, inline validation message, and helper text below the textarea -- read
    // from the .razor file, not invented. Reproduces a re-entrancy guard against a fast
    // double-click on the Submit button firing SubmitDeletionRequestAsync twice concurrently
    // before the dialog visually closes (FlightsViewModel itself has no such guard).
    private async Task ShowDeleteReasonDialogAsync(FlightRecord target)
    {
        var reasonLabel = new TextBlock
        {
            FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1)),
            Text = "REASON FOR DELETION *",
        };
        var reasonBox = new TextBox
        {
            PlaceholderText = "Explain why this flight record should be removed (e.g. sim crash, wrong aircraft, duplicate entry)...",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 96,
        };
        var errorText = new TextBlock
        {
            Text = "A reason is required to submit this request.",
            FontSize = 11.5, Foreground = StatusRed, Visibility = Visibility.Collapsed,
        };
        var helperText = new TextBlock
        {
            Text = "Your request will be reviewed. The flight will remain in your logbook until approved.",
            FontSize = 11, Foreground = TextMutedBrush, TextWrapping = TextWrapping.Wrap,
        };
        var panel = new StackPanel
        {
            Spacing = 8, Width = 380,
            Children = { reasonLabel, reasonBox, errorText, helperText },
        };

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = BuildDialogHeader("Request Flight Deletion", "A reason is required and must be approved before deletion"),
            Content = panel,
            PrimaryButtonText = "Submit Request",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var isSubmitting = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (isSubmitting) { args.Cancel = true; return; } // re-entrancy guard: ignore a second click while the first is still in flight

            if (string.IsNullOrWhiteSpace(reasonBox.Text))
            {
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true; // keep the dialog open on validation failure
                return;
            }

            var deferral = args.GetDeferral();
            try
            {
                isSubmitting = true;
                await _viewModel.SubmitDeletionRequestAsync(target, reasonBox.Text);
                RefreshStatStrip();
                RefreshActiveTabContent();
            }
            finally
            {
                isSubmitting = false;
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }

    // Exact copy from Flights.razor's force-remove modal: title, subtitle, and body text -- read
    // from the .razor file, not invented. Same re-entrancy guard pattern as the delete dialog
    // above.
    private async Task ShowForceRemoveDialogAsync(FlightRecord target)
    {
        var flightLabel = string.IsNullOrWhiteSpace(target.FlightNumber)
            ? target.Id.ToString()[..8].ToUpperInvariant()
            : target.FlightNumber;

        var body = new TextBlock
        {
            Text = $"Flight {flightLabel} will be permanently removed. This cannot be undone.",
            FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1)),
            TextWrapping = TextWrapping.Wrap, Width = 340,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = BuildDialogHeader("Remove Broken Flight", "This will permanently delete the active flight record"),
            Content = body,
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, // destructive action: Enter/default should not trigger a permanent delete
        };

        var isRemoving = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (isRemoving) { args.Cancel = true; return; } // re-entrancy guard: ignore a second click while the first is still in flight

            var deferral = args.GetDeferral();
            try
            {
                isRemoving = true;
                await _viewModel.ForceRemoveBrokenFlightAsync(target);
                RefreshStatStrip();
                RefreshActiveTabContent();
            }
            finally
            {
                isRemoving = false;
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }
}
