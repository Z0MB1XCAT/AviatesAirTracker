using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AviatesAirTracker.ViewModels;

public partial class LandingAnalysisViewModel : ObservableObject
{
    [ObservableProperty] private LandingResult? _latestLanding;
    [ObservableProperty] private string _scoreText = "—";
    [ObservableProperty] private string _scoreColor = "#4A5568";
    [ObservableProperty] private string _gradeText = "—";
    [ObservableProperty] private double _scoreArc = 0;
    [ObservableProperty] private bool _hasLanding;
    [ObservableProperty] private string _vsText = "—";
    [ObservableProperty] private string _pitchText = "—";
    [ObservableProperty] private string _bankText = "—";
    [ObservableProperty] private string _speedText = "—";
    [ObservableProperty] private string _crosswindText = "—";
    [ObservableProperty] private string _headwindText = "—";
    [ObservableProperty] private string _runwayText = "—";
    [ObservableProperty] private string _stabilityText = "—";
    [ObservableProperty] private string _stabilityColor = "#4A5568";
    [ObservableProperty] private string _bounceText = "—";
    [ObservableProperty] private bool _isLoading;

    // Score breakdown bar widths (0-100)
    [ObservableProperty] private double _vsScorePct;
    [ObservableProperty] private double _pitchScorePct;
    [ObservableProperty] private double _bankScorePct;
    [ObservableProperty] private double _speedScorePct;
    [ObservableProperty] private double _xwScorePct;
    [ObservableProperty] private double _stabilityScorePct;

    public ObservableCollection<LandingResult> LandingHistory { get; } = [];

    private readonly ILandingRepository _landingRepo;

    public LandingAnalysisViewModel(ILandingRepository landingRepo)
    {
        _landingRepo = landingRepo;
    }

    public void AddLanding(LandingResult result)
    {
        LatestLanding = result;
        HasLanding = true;

        ScoreText = result.LandingScore.ToString();
        ScoreColor = result.LandingGradeColor;
        GradeText = result.LandingGrade;
        ScoreArc = result.LandingScore * 2.8; // 0-280 degrees of arc

        VsText = $"{result.VerticalSpeedFPM:F0} fpm";
        PitchText = $"{result.TouchdownPitchDeg:F1}°";
        BankText = $"{result.TouchdownBankDeg:F1}°";
        SpeedText = $"{result.IASKts:F0} kt";
        CrosswindText = $"{result.CrosswindComponent:F1} kt";
        HeadwindText = $"{result.HeadwindComponent:F1} kt";
        RunwayText = $"{result.AirportICAO} {result.RunwayIdentifier}";
        StabilityText = result.ApproachWasStable ? "STABLE" : "UNSTABLE";
        StabilityColor = result.ApproachWasStable ? "#22C55E" : "#EF4444";
        BounceText = result.BounceCount == 0 ? "None" : $"{result.BounceCount} bounce(s)";

        // Score bars
        var sb = result.ScoreBreakdown;
        VsScorePct = sb.VerticalSpeedScore / 30.0 * 100;
        PitchScorePct = sb.PitchScore / 20.0 * 100;
        BankScorePct = sb.BankScore / 15.0 * 100;
        SpeedScorePct = sb.SpeedScore / 15.0 * 100;
        XwScorePct = sb.CrosswindScore / 10.0 * 100;
        StabilityScorePct = sb.StabilityScore / 10.0 * 100;

        LandingHistory.Insert(0, result);
        // CRIT-03: Removed _landingRepo.SaveAsync(result) — FlightSessionManager.OnLandingDetected
        // already saves to the repository. Calling it here too caused every landing to be doubled.
    }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var landings = await _landingRepo.GetAllAsync();
            LandingHistory.Clear();
            foreach (var l in landings) LandingHistory.Add(l);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
