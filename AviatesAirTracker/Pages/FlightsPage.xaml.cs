using AviatesAirTracker.Models;
using AviatesAirTracker.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using System.Linq;

namespace AviatesAirTracker.Pages;

public sealed partial class FlightsPage : Page
{
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

        UpdateTabVisuals();

        Loaded += async (_, _) =>
        {
            await _viewModel.RefreshAsync();
            RefreshStatStrip();
            RebuildFlightRows(); // Task 3 fills this in
        };
    }

    private void OnTabTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border { Tag: string tab }) return;

        _activeTab = tab;
        UpdateTabVisuals();
        RebuildFlightRows(); // Task 3 fills this in
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

        PendingCountText.Text = _viewModel.PendingRequests.Count.ToString(CultureInfo.InvariantCulture);
    }

    // Task 3 fills this in with the flight table (and pending-review table). Stubbed as a
    // no-op here so Task 2's Loaded handler and tab-click wiring can call it now without
    // inventing row-building logic ahead of schedule -- the plan explicitly calls this out as
    // the acceptable way to keep this task's build green.
    private void RebuildFlightRows() { }
}
