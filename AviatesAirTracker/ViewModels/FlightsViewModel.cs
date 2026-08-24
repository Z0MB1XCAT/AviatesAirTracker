using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AviatesAirTracker.ViewModels;

/// <summary>
/// Ported from Flights.razor's @code block (verbatim behavior, not a redesign). The four action
/// methods below are plain async methods rather than [RelayCommand]-wrapped ones: Task 4 wires
/// them to ContentDialog button handlers and row Tapped/PointerPressed events in code-behind, not
/// XAML Command="{Binding}" bindings, so the ICommand wrapper the source generator would produce
/// has no consumer. Call signatures for Task 4:
///   SubmitDeletionRequestAsync(FlightRecord flight, string reason)
///   ApproveDeletionAsync(FlightDeletionRequest req)
///   RejectDeletionAsync(FlightDeletionRequest req)
///   ForceRemoveBrokenFlightAsync(FlightRecord target)
/// </summary>
public partial class FlightsViewModel : ObservableObject
{
    private readonly IFlightRepository _flightRepo;
    private readonly IFlightDeletionRepository _deletionRepo;
    private readonly FlightSessionManager _session;

    [ObservableProperty] private double _totalNm;
    [ObservableProperty] private bool _isLoading;

    public ObservableCollection<FlightRecord> Flights { get; } = [];
    public ObservableCollection<FlightDeletionRequest> PendingRequests { get; } = [];

    public FlightsViewModel(IFlightRepository flightRepo, IFlightDeletionRepository deletionRepo, FlightSessionManager session)
    {
        _flightRepo = flightRepo;
        _deletionRepo = deletionRepo;
        _session = session;
        session.FlightCompleted += (_, _) => _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var flights = await _flightRepo.GetAllAsync();
            Flights.Clear();
            foreach (var f in flights) Flights.Add(f);

            TotalNm = flights.Where(f => f.Status == FlightStatus.Completed).Sum(f => f.ActualDistanceNm);

            var pending = await _deletionRepo.GetAllAsync();
            PendingRequests.Clear();
            foreach (var p in pending) PendingRequests.Add(p);
        }
        finally { IsLoading = false; }
    }

    // ── Action methods (see class doc-comment for why these aren't [RelayCommand]) ─────────────

    public async Task SubmitDeletionRequestAsync(FlightRecord flight, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return; // caller (the page) surfaces the validation message

        var deletionRequest = new FlightDeletionRequest
        {
            FlightId     = flight.Id,
            FlightNumber = flight.FlightNumber,
            Route        = FlightsDisplay.RouteLabel(flight),
            Reason       = reason.Trim(),
        };
        await _deletionRepo.SaveAsync(deletionRequest);
        var pending = await _deletionRepo.GetAllAsync();
        PendingRequests.Clear();
        foreach (var p in pending) PendingRequests.Add(p);
    }

    public async Task ApproveDeletionAsync(FlightDeletionRequest req)
    {
        await _deletionRepo.ApproveAsync(req.Id);
        await _flightRepo.DeleteAsync(req.FlightId);
        await RefreshAsync();
    }

    public async Task RejectDeletionAsync(FlightDeletionRequest req)
    {
        await _deletionRepo.RejectAsync(req.Id);
        await RefreshAsync();
    }

    public async Task ForceRemoveBrokenFlightAsync(FlightRecord target)
    {
        if (_session.CurrentFlight?.Id == target.Id)
            _session.Reset();

        await _flightRepo.DeleteAsync(target.Id);
        await RefreshAsync();
    }
}
