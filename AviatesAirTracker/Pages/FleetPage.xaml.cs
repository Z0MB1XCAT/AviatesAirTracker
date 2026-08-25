using AviatesAirTracker.Models;
using AviatesAirTracker.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Windows.UI;

namespace AviatesAirTracker.Pages;

/// <summary>
/// Pre-formatted display row for one type card in the Fleet grid. Plain mutable class with
/// { get; set; } properties, not a record (CS8852 risk, same as every prior display-row class
/// in this migration -- RouteDisplayRow/FlightLogRow/etc.). Every visual value the card needs
/// (chip colors, dimmed text, dashed-border overlay visibility) is precomputed here rather than
/// via a converter, since classic {Binding} has no converter without a Page.Resources entry
/// (the same XamlCompiler-crash risk documented throughout this migration -- see
/// DashboardPage.xaml's header comment). <see cref="Source"/> is the full AircraftType Task 2's
/// selection wiring needs when a card is tapped.
/// </summary>
public sealed class TypeDisplayRow
{
    // Card chrome: solid surface/border normally; accent ring when selected (approximates
    // .fleet-card--selected's box-shadow ring with a thicker accent-colored border, since WinUI
    // Border has no box-shadow equivalent); ShowDashedOverlay switches in a separate dashed
    // Rectangle drawn on top of a surface-2 background when the type is on order
    // (.fleet-card--ordered), since Border itself has no dash-pattern property.
    public Brush CardBackground { get; set; } = FleetPage.SurfaceBrush;
    public Brush CardBorderBrush { get; set; } = FleetPage.HairlineBrush;
    public double CardBorderThickness { get; set; } = 1;
    public Visibility ShowDashedOverlay { get; set; } = Visibility.Collapsed;

    public Brush StatusChipColor { get; set; } = FleetPage.TextMutedBrush;
    public Brush StatusChipBackground { get; set; } = FleetPage.TransparentBrush;
    public string StatusLabel { get; set; } = "";

    public string TypeCode { get; set; } = "";

    public ImageSource? PhotoSource { get; set; }
    public Visibility ShowPhoto { get; set; } = Visibility.Collapsed;
    public Visibility ShowEmojiPlaceholder { get; set; } = Visibility.Visible;
    public string EmojiText { get; set; } = "✈";
    public double PhotoOpacity { get; set; } = 1.0;

    public string Name { get; set; } = "";
    public Brush NameColor { get; set; } = FleetPage.TextPrimaryBrush;

    public string SubText { get; set; } = "";
    public Brush SubTextColor { get; set; } = FleetPage.TextMutedBrush;

    public string Stat1Val { get; set; } = "";
    public string Stat1Lbl { get; set; } = "";
    public Brush Stat1Color { get; set; } = FleetPage.TextPrimaryBrush;

    public string Stat2Val { get; set; } = "";
    public Brush Stat2Color { get; set; } = FleetPage.TextPrimaryBrush;

    public string Stat3Val { get; set; } = "";
    public string Stat3Lbl { get; set; } = "";
    public Brush Stat3Color { get; set; } = FleetPage.TextPrimaryBrush;

    public string Stat4Val { get; set; } = "";
    public Brush Stat4Color { get; set; } = FleetPage.TextPrimaryBrush;

    public string ExpandText { get; set; } = "Show registrations";
    public Brush ExpandColor { get; set; } = FleetPage.TextMutedBrush;

    // Task 2 wires this: the click handler needs the full AircraftType to call
    // _viewModel.SelectTypeAsync(type).
    public AircraftType Source { get; set; } = null!;
}

public sealed partial class FleetPage : Page
{
    // ── Shared brushes ──────────────────────────────────────────────────────────────────────
    // Dark-theme hex values read directly from wwwroot/css/app.css's html[data-theme="dark"]
    // block, same values every prior native page in this migration hardcodes (surface #0C111D,
    // surface-2 #121A2A, border rgba(255,255,255,0.08), text-primary #F1F5F9, text-muted
    // #64748B, text-faint #334155, accent #4D9BEC). Declared fresh here rather than shared from
    // another page's `internal` brushes, matching RoutesPage/FlightsPage's own precedent of each
    // page keeping its own private copy.
    internal static readonly Brush SurfaceBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x0C, 0x11, 0x1D));
    private static readonly Brush Surface2Brush = new SolidColorBrush(Color.FromArgb(0xFF, 0x12, 0x1A, 0x2A));
    internal static readonly Brush HairlineBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    internal static readonly Brush TextPrimaryBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9));
    internal static readonly Brush TextMutedBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x64, 0x74, 0x8B));
    private static readonly Brush TextFaintBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x41, 0x55));
    internal static readonly Brush TransparentBrush = new SolidColorBrush(Colors.Transparent);
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC));

    // Stat-icon chip colors: same green/amber/violet set Dashboard's own stat strip and PilotHub
    // already established (verbatim hex + the same 0x1F alpha convention for the tinted chip
    // background every prior page uses, including Dashboard's own #1FB8934A violet chip). The
    // Total Aircraft stat card's blue chip and the error banner's red reuse AccentBrush and a
    // literal hex directly in FleetPage.xaml instead of a dedicated field here, since neither
    // needs a second consumer in code-behind.
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x3E, 0xC7, 0xAC));
    private static readonly Brush GreenBg = new SolidColorBrush(Color.FromArgb(0x1F, 0x3E, 0xC7, 0xAC));
    private static readonly Brush AmberBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B));
    private static readonly Brush AmberBg = new SolidColorBrush(Color.FromArgb(0x1F, 0xF5, 0x9E, 0x0B));

    // Filter tab active/inactive treatment: matches app.css's .fleet-filter-tab.active rule
    // directly (solid accent fill + white text), a different technique from Routes/Flights' own
    // tab bars (which reuse the sidebar's SidebarActiveBrush); Fleet.razor's own CSS uses a
    // plain accent-fill pill instead, so this reproduces that rather than the other pages' look.
    private static readonly Brush TabActiveBg = AccentBrush;
    private static readonly Brush TabActiveFg = new SolidColorBrush(Colors.White);
    private static readonly Brush TabInactiveBg = SurfaceBrush;
    private static readonly Brush TabInactiveFg = TextMutedBrush;

    // Only three unique website photos exist today; every other type falls back to the branded
    // gradient placeholder with the type's own Emoji. Exact same 5-entry dictionary + fallback
    // as Fleet.razor's own _fleetPhotos (A380/A380-800/A388 are forward-looking guesses for a
    // type that doesn't exist in the fleet yet, per that file's own code comment -- harmless
    // no-ops today).
    private static readonly Dictionary<string, string> FleetPhotoFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A319"] = "fleet-a319.webp",
        ["A320neo"] = "fleet-a320neo.webp",
        ["A380"] = "fleet-a380.webp",
        ["A380-800"] = "fleet-a380.webp",
        ["A388"] = "fleet-a380.webp",
    };

    private readonly FleetViewModel _viewModel;

    // Lives on the page, not the ViewModel -- matches Fleet.razor's own VM.SubsidiaryFilter
    // being the source of truth already, mirrored here only so the tab Borders know which one
    // to paint active without re-reading the ViewModel on every visual pass.
    private string _activeFilter = "All";

    public ObservableCollection<TypeDisplayRow> TypeRows { get; } = [];

    public FleetPage()
    {
        InitializeComponent();

        _viewModel = App.GetService<FleetViewModel>();
        DataContext = _viewModel;
        TypeCardsList.ItemsSource = TypeRows;

        UpdateFilterVisuals();

        Loaded += async (_, _) =>
        {
            await _viewModel.LoadAsync();
            RefreshUiState();
        };
    }

    // ── Type card grid columns ──────────────────────────────────────────────────────────────
    // Ported directly from RoutesPage.xaml.cs's RouteCardsList_SizeChanged/
    // UpdateRouteGridColumns (already merged this session) rather than reinvented: a fixed
    // ItemsWrapGrid.ItemWidth leaves a dead gap on the right whenever the available width isn't
    // an exact multiple of it, the bug Routes shipped once and had to fix after the fact. Applying
    // the same recompute-on-resize technique here from the start avoids repeating it.
    private const double CardMinWidth = 380;
    private const double CardGutter = 16;

    private void TypeCardsList_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTypeGridColumns();

    private void UpdateTypeGridColumns()
    {
        if (TypeCardsList.ItemsPanelRoot is not ItemsWrapGrid panel) return;

        var available = TypeCardsList.ActualWidth;
        if (available <= 0) return;

        var columns = Math.Max(1, (int)Math.Floor((available + CardGutter) / (CardMinWidth + CardGutter)));
        panel.ItemWidth = Math.Floor(available / columns);
    }

    // ── Refresh ──────────────────────────────────────────────────────────────────────────────
    // Static icon, not spun on IsLoading: the plan calls a spin animation optional polish. A
    // future pass could add a Storyboard RotateTransform keyed off IsLoading the way the CSS
    // ".fleet-refresh.spinning" class does, but a plain static icon is an acceptable
    // simplification for this task.
    private async void OnRefreshTapped(object sender, TappedRoutedEventArgs e)
    {
        await _viewModel.RefreshAsync();
        RefreshUiState();
    }

    // ── Loading / error / main-content states ───────────────────────────────────────────────
    // Reproduces Fleet.razor's own @if / else if / else exactly: these three states are mutually
    // exclusive, never shown alongside one another.
    private void RefreshUiState()
    {
        var showLoading = _viewModel.IsLoading && _viewModel.Data.Types.Count == 0;
        var showError = !showLoading && _viewModel.Error != null;
        var showMain = !showLoading && !showError;

        LoadingPanel.Visibility = showLoading ? Visibility.Visible : Visibility.Collapsed;
        ErrorBanner.Visibility = showError ? Visibility.Visible : Visibility.Collapsed;
        MainContent.Visibility = showMain ? Visibility.Visible : Visibility.Collapsed;

        if (showError)
        {
            ErrorText.Text = _viewModel.Error ?? "";
        }

        if (showMain)
        {
            RebuildStats();
            RebuildTypeCards();
        }
    }

    private void RebuildStats()
    {
        var stats = _viewModel.Data.Stats;
        StatTotalText.Text = stats.TotalAircraft.ToString(CultureInfo.InvariantCulture);
        StatInServiceText.Text = stats.InService.ToString(CultureInfo.InvariantCulture);
        StatMaintenanceText.Text = stats.InMaintenance.ToString(CultureInfo.InvariantCulture);
        StatOnOrderText.Text = stats.OnOrder.ToString(CultureInfo.InvariantCulture);
    }

    // ── Filter tabs ──────────────────────────────────────────────────────────────────────────
    private void OnFilterTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border { Tag: string filter } || filter == _activeFilter) return;

        _activeFilter = filter;
        UpdateFilterVisuals();

        // SetFilter already clears an out-of-filter selection internally -- no redundant guard
        // needed here, per the plan.
        _viewModel.SetFilter(filter);
        RebuildTypeCards();
    }

    private void UpdateFilterVisuals()
    {
        ApplyTabVisual(TabAllBorder, TabAllText, "All");
        ApplyTabVisual(TabMainlineBorder, TabMainlineText, "Mainline");
        ApplyTabVisual(TabRegionalBorder, TabRegionalText, "Regional");
        ApplyTabVisual(TabCargoBorder, TabCargoText, "Cargo");
    }

    private void ApplyTabVisual(Border border, TextBlock text, string filter)
    {
        var isActive = _activeFilter == filter;
        border.Background = isActive ? TabActiveBg : TabInactiveBg;
        text.Foreground = isActive ? TabActiveFg : TabInactiveFg;
    }

    // ── Type card grid ───────────────────────────────────────────────────────────────────────
    private void RebuildTypeCards()
    {
        TypeRows.Clear();

        foreach (var type in _viewModel.FilteredTypes)
        {
            var isSelected = _viewModel.SelectedType?.TypeCode == type.TypeCode;
            var isOrdered = type.PrimaryStatus == "ordered";

            var (statusLabel, statusFg, statusBg) = type.PrimaryStatus switch
            {
                "maintenance" => ("Maintenance", AmberBrush, AmberBg),
                "ordered" => ("On Order", TextMutedBrush, Surface2Brush),
                _ => ("In Service", GreenBrush, GreenBg),
            };

            var dimColor = isOrdered ? TextMutedBrush : TextPrimaryBrush;

            string stat3Val;
            string stat3Lbl;
            Brush stat3Color;
            if (type.PrimaryStatus == "maintenance")
            {
                stat3Val = $"{type.MaintenanceCount} MX";
                stat3Lbl = "Active";
                stat3Color = AmberBrush;
            }
            else if (type.PrimaryStatus == "ordered")
            {
                stat3Val = type.OrderedCount.ToString(CultureInfo.InvariantCulture);
                stat3Lbl = "On Order";
                stat3Color = TextMutedBrush;
            }
            else
            {
                stat3Val = type.ActiveCount.ToString(CultureInfo.InvariantCulture);
                stat3Lbl = "Active";
                stat3Color = TextPrimaryBrush;
            }

            var photo = FleetPhoto(type.TypeCode);

            var row = new TypeDisplayRow
            {
                CardBackground = isOrdered ? Surface2Brush : SurfaceBrush,
                CardBorderBrush = isSelected ? AccentBrush : HairlineBrush,
                CardBorderThickness = isSelected ? 2 : 1,
                // .fleet-card--ordered's dashed border is drawn as a separate overlay
                // (see FleetPage.xaml's TypeCardsList DataTemplate) since Border has no
                // dash-pattern property; not shown at all when selected (selection takes the
                // accent-ring treatment instead, matching the CSS's own !important override).
                ShowDashedOverlay = isOrdered && !isSelected ? Visibility.Visible : Visibility.Collapsed,

                StatusLabel = statusLabel,
                StatusChipColor = statusFg,
                StatusChipBackground = statusBg,

                TypeCode = type.TypeCode,

                PhotoSource = photo,
                ShowPhoto = photo is not null ? Visibility.Visible : Visibility.Collapsed,
                ShowEmojiPlaceholder = photo is null ? Visibility.Visible : Visibility.Collapsed,
                EmojiText = type.Emoji,
                PhotoOpacity = isOrdered ? 0.4 : 1.0,

                Name = type.Name,
                NameColor = dimColor,

                SubText = isOrdered
                    ? $"{type.OrderedCount} on order"
                    : $"{type.ActiveCount} active · {type.TotalAircraft} total",
                SubTextColor = isOrdered ? TextFaintBrush : TextMutedBrush,

                Stat1Val = type.CapacityDisplay,
                Stat1Lbl = type.CapacityLabel,
                Stat1Color = dimColor,

                Stat2Val = type.RangeNm.ToString("N0", CultureInfo.InvariantCulture),
                Stat2Color = dimColor,

                Stat3Val = stat3Val,
                Stat3Lbl = stat3Lbl,
                Stat3Color = stat3Color,

                Stat4Val = type.Subsidiary,
                Stat4Color = dimColor,

                ExpandText = isSelected ? "Hide registrations" : "Show registrations",
                ExpandColor = isSelected ? AccentBrush : TextMutedBrush,

                Source = type,
            };

            TypeRows.Add(row);
        }

        // TypeCardsList may not have fired a usable SizeChanged yet if this is the first
        // population after being empty -- call this explicitly, same reasoning
        // RoutesPage.xaml.cs's RebuildRouteCards documents for its own grid.
        UpdateTypeGridColumns();
    }

    // Task 2 wires this to _viewModel.SelectTypeAsync(type) followed by a grid/panel rebuild.
    private void OnTypeCardTapped(object sender, TappedRoutedEventArgs e)
    {
        // Task 2 wires this.
    }

    private static ImageSource? FleetPhoto(string? typeCode)
    {
        if (typeCode is null || !FleetPhotoFiles.TryGetValue(typeCode, out var file)) return null;

        // wwwroot ships as loose Content next to the exe, not a packaged ms-appx:// asset, so
        // the photo is loaded from an absolute file path -- same technique Dashboard's own
        // hero.webp and RoutesPage's flag images already established.
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot", "images", file);
        return System.IO.File.Exists(path) ? new BitmapImage(new Uri(path)) : null;
    }
}
