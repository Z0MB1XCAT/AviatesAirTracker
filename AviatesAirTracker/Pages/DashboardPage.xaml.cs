using AviatesAirTracker.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.Globalization;

namespace AviatesAirTracker.Pages;

/// <summary>
/// Pre-formatted display row for the Recent Flights list. Built in code-behind rather than
/// bound directly to <see cref="Models.FlightRecord"/> so the DataTemplate needs no
/// StringFormat/converter (classic WinUI {Binding} supports neither without a Page.Resources
/// entry, which risks the same XamlCompiler crash documented in DashboardPage.xaml's header
/// comment). Plain mutable properties, not an init-only positional record: XamlTypeInfo.g.cs
/// (generated because this type flows through an ItemsControl's ItemsSource) assigns each
/// property via a plain setter, which an init accessor does not satisfy (CS8852).
/// </summary>
public sealed class FlightDisplayRow
{
    public string Route { get; set; } = "";
    public string AircraftType { get; set; } = "";
    public string PilotName { get; set; } = "";
    public string DateText { get; set; } = "";

    public FlightDisplayRow(string route, string aircraftType, string pilotName, string dateText)
    {
        Route = route;
        AircraftType = aircraftType;
        PilotName = pilotName;
        DateText = dateText;
    }
}

public sealed partial class DashboardPage : Page
{
    private readonly DashboardViewModel _viewModel;

    public ObservableCollection<FlightDisplayRow> RecentFlightRows { get; } = [];

    public DashboardPage()
    {
        InitializeComponent();

        _viewModel = App.GetService<DashboardViewModel>();
        DataContext = _viewModel;
        FlightsList.ItemsSource = RecentFlightRows;

        Loaded += async (_, _) =>
        {
            await _viewModel.RefreshAsync();
            RebuildRecentFlightRows();
            RankProgressText.Text = $"{_viewModel.RankProgress:F0}%";
        };
    }

    private void RebuildRecentFlightRows()
    {
        RecentFlightRows.Clear();
        foreach (var f in _viewModel.RecentFlights)
        {
            RecentFlightRows.Add(new FlightDisplayRow(
                $"{f.DepartureICAO} → {f.ArrivalICAO}",
                f.AircraftType,
                f.PilotName,
                f.CreatedAt.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)));
        }

        // Recent Flights is a genuinely reachable empty state for a new pilot, unlike
        // Assignments/Operations Team, which are always-present static content.
        var isEmpty = RecentFlightRows.Count == 0;
        EmptyFlightsText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        FlightsList.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }
}
