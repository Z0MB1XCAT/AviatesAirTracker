using AviatesAirTracker.Core.Reference;
using AviatesAirTracker.Models;
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

    public FlightsPage()
    {
        InitializeComponent();

        _viewModel = App.GetService<FlightsViewModel>();
        DataContext = _viewModel;
        FlightsList.ItemsSource = FlightRows;

        UpdateTabVisuals();

        Loaded += async (_, _) =>
        {
            await _viewModel.RefreshAsync();
            RefreshStatStrip();
            RebuildFlightRows();
        };
    }

    private void OnTabTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border { Tag: string tab }) return;

        _activeTab = tab;
        UpdateTabVisuals();
        RebuildFlightRows();
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
            else
            {
                row.StatusLabel = FlightsDisplay.StatusLabel(f.Status);
                var (fg, bg) = StatusChipBrushes(FlightsDisplay.StatusChipClass(f.Status));
                row.StatusChipColor = fg;
                row.StatusChipBackground = bg;
            }

            FlightRows.Add(row);
        }

        // Exact empty-state copy from Flights.razor's "No flights yet" panel. Toggles the whole
        // header-row-plus-ItemsControl group (FlightsTableContent), not just the ItemsControl on
        // its own -- Flights.razor's empty state replaces the entire <table> (header included),
        // not just its <tbody>.
        var isEmpty = FlightRows.Count == 0;
        EmptyStateText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        FlightsTableContent.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }
}
