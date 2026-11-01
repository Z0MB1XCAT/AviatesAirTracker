using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AviatesAirTracker.ViewModels;

/// <summary>
/// Backs the Landing Analysis page: the pilot's landings, newest first, and the full
/// <see cref="LandingReport"/> (with its assessment) for the one being looked at. The report is
/// the same shared projection the touchdown pop-up and the EFB render, built here with the
/// flight, the landing history (for ranking) and the stored approach trace.
/// </summary>
public partial class LandingAnalysisViewModel : ObservableObject
{
    private readonly ILandingRepository _landingRepo;
    private readonly IFlightRepository _flightRepo;

    public IReadOnlyList<LandingResult> Landings { get; private set; } = [];
    public LandingResult? Selected { get; private set; }
    public LandingReport? Report { get; private set; }
    public FlightRecord? SelectedFlight { get; private set; }

    public LandingAnalysisViewModel(ILandingRepository landingRepo, IFlightRepository flightRepo)
    {
        _landingRepo = landingRepo;
        _flightRepo = flightRepo;
    }

    public async Task LoadAsync()
    {
        Landings = (await _landingRepo.GetAllAsync()).OrderByDescending(l => l.Timestamp).ToList();
    }

    /// <summary>Builds the report for one landing; null if it no longer exists.</summary>
    public async Task<LandingReport?> SelectAsync(Guid landingId)
    {
        var landing = Landings.FirstOrDefault(l => l.Id == landingId);
        if (landing == null) return null;

        FlightRecord? flight = null;
        if (Guid.TryParse(landing.FlightId, out var fid))
            flight = await _flightRepo.GetByIdAsync(fid);

        var trace = await _landingRepo.GetTraceAsync(landing.Id);
        Selected = landing;
        SelectedFlight = flight;
        Report = LandingReportBuilder.Build(landing, flight, Landings.ToList(), trace);
        return Report;
    }

    /// <summary>Average touchdown rate (fpm, positive) across all landings, for comparison.</summary>
    public double AverageVsFpm => Landings.Count > 0 ? Landings.Average(l => Math.Abs(l.VerticalSpeedFPM)) : 0;
}
