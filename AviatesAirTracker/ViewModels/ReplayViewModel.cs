using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace AviatesAirTracker.ViewModels;

public partial class ReplayViewModel : ObservableObject
{
    [ObservableProperty] private bool _isReplaying;
    [ObservableProperty] private double _replayPosition;
    [ObservableProperty] private string _replayTimeText = "00:00:00";
    [ObservableProperty] private int _replaySpeed = 1;
    [ObservableProperty] private string _selectedFlightText = "No flight selected";

    private List<Models.PathPoint> _replayPath = [];
    private int _replayIndex = 0;
    // CRIT-01: Was System.Timers.Timer which fires on ThreadPool — setting [ObservableProperty] values
    // from it caused InvalidOperationException. DispatcherTimer fires on the UI thread.
    private DispatcherTimer? _replayTimer;

    [ObservableProperty] private double _replayLat;
    [ObservableProperty] private double _replayLon;
    [ObservableProperty] private double _replayAlt;
    [ObservableProperty] private double _replaySpeed2;
    [ObservableProperty] private string _replayPhase = "";

    private readonly IFlightRepository _flightRepo;

    public ObservableCollection<FlightRecord> AvailableFlights { get; } = [];

    public ReplayViewModel(IFlightRepository flightRepo)
    {
        _flightRepo = flightRepo;
    }

    public async Task LoadFlightAsync(FlightRecord flight)
    {
        _replayPath = flight.FlightPath;
        _replayIndex = 0;
        ReplayPosition = 0;
        SelectedFlightText = $"{flight.DepartureICAO} → {flight.ArrivalICAO}  {flight.TakeoffTime:yyyy-MM-dd}";
        await Task.CompletedTask;
    }

    [RelayCommand]
    public void PlayPause()
    {
        if (_replayPath.Count == 0) return;

        if (!IsReplaying)
        {
            IsReplaying = true;
            // CRIT-01: DispatcherTimer fires on the UI thread — safe for ObservableProperty writes.
            _replayTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500.0 / Math.Max(1, ReplaySpeed))
            };
            _replayTimer.Tick += (_, _) => AdvanceReplay();
            _replayTimer.Start();
        }
        else
        {
            IsReplaying = false;
            _replayTimer?.Stop();
        }
    }

    [RelayCommand]
    public void Stop()
    {
        IsReplaying = false;
        _replayTimer?.Stop();
        _replayIndex = 0;
        ReplayPosition = 0;
    }

    private void AdvanceReplay()
    {
        if (_replayIndex >= _replayPath.Count - 1)
        {
            IsReplaying = false;
            _replayTimer?.Stop();
            return;
        }

        _replayIndex++;
        var pt = _replayPath[_replayIndex];
        ReplayLat = pt.Latitude;
        ReplayLon = pt.Longitude;
        ReplayAlt = pt.AltitudeMSL;
        ReplaySpeed2 = pt.GroundSpeed;
        ReplayPhase = pt.Phase.ToString();
        ReplayPosition = (double)_replayIndex / _replayPath.Count * 100;

        var elapsed = pt.Timestamp - _replayPath[0].Timestamp;
        ReplayTimeText = elapsed.ToString(@"hh\:mm\:ss");
    }

    public async Task RefreshAvailableFlightsAsync()
    {
        var flights = await _flightRepo.GetRecentAsync(20);
        AvailableFlights.Clear();
        foreach (var f in flights.Where(f => f.FlightPath.Count > 0))
            AvailableFlights.Add(f);
    }
}
