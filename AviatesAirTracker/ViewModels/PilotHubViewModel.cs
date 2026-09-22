using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AviatesAirTracker.ViewModels;

public partial class PilotHubViewModel : ObservableObject
{
    // Career stat cards
    [ObservableProperty] private string _totalFlights = "—";
    [ObservableProperty] private string _blockHours = "—";
    [ObservableProperty] private string _avgLandingScore = "—";
    [ObservableProperty] private string _totalDistance = "—";

    // Rank card
    [ObservableProperty] private string _pilotRank = "—";
    [ObservableProperty] private string _nextRank = "—";
    [ObservableProperty] private double _rankProgressPct;   // 0–100, used for CSS width%

    // Last landing analysis card
    [ObservableProperty] private LandingResult? _lastLanding;
    [ObservableProperty] private string _lastLandingGradeLabel = "—";
    [ObservableProperty] private string _lastLandingGradeClass = "grade-a";
    [ObservableProperty] private double _lastLandingRingOffset = 408.41; // full-ring = empty state

    // State
    [ObservableProperty] private bool _hasLandings;
    [ObservableProperty] private bool _isLoading;

    // Current booking card (MaxBookings = 1 -- BookingService.cs -- so there's ever at most one
    // to show). Null CurrentBooking means the card is hidden entirely; BookingExpired
    // distinguishes "still confirmed, showing normally" from "just auto-cancelled because its
    // scheduled departure passed unflown" for the same booking object.
    [ObservableProperty] private FlightBooking? _currentBooking;
    [ObservableProperty] private bool _bookingExpired;

    // Landing history table (most recent 10)
    public ObservableCollection<LandingResult> RecentLandings { get; } = [];

    // Blazor pages subscribe to this to trigger StateHasChanged after a background refresh
    public event Action? DataRefreshed;

    private readonly PilotStatsService _statsService;
    private readonly ILandingRepository _landingRepo;
    private readonly BookingService _bookingService;
    private readonly SimBriefService _simBrief;

    public PilotHubViewModel(PilotStatsService statsService, ILandingRepository landingRepo,
        FlightSessionManager sessionManager, BookingService bookingService, SimBriefService simBrief)
    {
        _statsService = statsService;
        _landingRepo  = landingRepo;
        _bookingService = bookingService;
        _simBrief = simBrief;
        sessionManager.FlightCompleted += (_, _) => _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var stats   = await _statsService.ComputeAsync();
            var landings = (await _landingRepo.GetAllAsync())
                .OrderByDescending(l => l.Timestamp)
                .Take(10)
                .ToList();

            TotalFlights     = stats.TotalFlights.ToString();
            BlockHours       = $"{stats.TotalHoursBlock:F1}h";
            AvgLandingScore  = stats.TotalLandings > 0 ? $"{stats.AverageLandingScore:F0}" : "—";
            TotalDistance    = stats.TotalDistanceNm >= 1000
                               ? $"{stats.TotalDistanceNm / 1000:F1}k nm"
                               : $"{stats.TotalDistanceNm:F0} nm";

            PilotRank = stats.Rank;
            (NextRank, RankProgressPct) = PilotStatsLogic.ComputeRankProgress(stats.TotalHoursBlock, stats.Rank);

            HasLandings = landings.Count > 0;

            RecentLandings.Clear();
            foreach (var l in landings) RecentLandings.Add(l);

            LastLanding = landings.FirstOrDefault();
            if (LastLanding != null)
            {
                const double circumference = 2 * Math.PI * 65; // r=65 matches SVG
                LastLandingRingOffset  = circumference * (1.0 - LastLanding.LandingScore / 100.0);
                LastLandingGradeLabel  = GradeLabel(LastLanding.LandingScore);
                LastLandingGradeClass  = GradeClass(LastLanding.LandingScore);
            }

            await RefreshBookingAsync();
        }
        finally
        {
            IsLoading = false;
            DataRefreshed?.Invoke();
        }
    }

    // Fetches the pilot's single booking (server is the source of truth -- ActiveBooking is only
    // the in-memory "flight in progress right now" pointer) and expires it if its scheduled
    // departure has passed unflown. BookingExpired stays true for this one refresh so the card
    // can show "Booking Expired" once, then the booking simply won't be there on the next load.
    private async Task RefreshBookingAsync()
    {
        var booking = await _bookingService.GetCurrentBookingAsync();
        if (booking == null)
        {
            CurrentBooking = null;
            BookingExpired = false;
            return;
        }

        var expired = await _bookingService.ExpireIfStaleAsync(booking);
        CurrentBooking = booking;
        BookingExpired = expired != null;
    }

    // Instant local cancel -- clears CurrentBooking/ActiveBooking synchronously so Pilot Hub's
    // card (and anywhere else reading ActiveBooking, e.g. ACARS) updates the moment the pilot
    // taps Cancel, rather than waiting on a full round trip to the backend first. The actual
    // DELETE still runs in the background and still goes through every real server-side check
    // (ownership, already-cancelled, etc.) -- this doesn't skip or fake that, it just doesn't
    // block the pilot's own screen on it. A rebook attempted before the background cancel has
    // actually landed can still race the server's own MaxBookings check; that's an accepted
    // edge case of "instant locally, cloud catches up" rather than something silently papered
    // over -- a real failure there surfaces as a real error from CreateBookingAsync.
    public void CancelCurrentBooking()
    {
        // Mirrors the server's own guard (DELETE /api/bookings/:id 409s once activated_at is
        // set) -- an activated booking is a genuine point of no return, not just a UI
        // restriction, so this silently no-ops rather than firing a request that will fail.
        if (CurrentBooking == null || CurrentBooking.IsActivated) return;
        var booking = CurrentBooking;

        CurrentBooking = null;
        BookingExpired = false;
        if (_bookingService.ActiveBooking?.Id == booking.Id)
            _bookingService.SetActiveBooking(null);

        _ = _bookingService.CancelBookingAsync(booking.Id);
    }

    /// <summary>
    /// One-way: opens SimBrief dispatch pre-filled from the current booking (route, aircraft,
    /// registration, callsign, schedule -- everything OpenDispatch already builds into the URL),
    /// then marks the booking activated server-side so it can never be cancelled again. The
    /// browser opens regardless of whether the activate call itself succeeds -- SimBrief doesn't
    /// depend on our backend, and there's no way to "unopen" the tab if the network hiccups, so
    /// there's nothing to gain by ordering it after a successful response. Returns an error
    /// message if the server-side activation didn't go through (CurrentBooking is left
    /// unactivated in that case, and the pilot can just tap Activate again), or null on success.
    /// </summary>
    public async Task<string?> ActivateCurrentBookingAsync()
    {
        if (CurrentBooking == null || CurrentBooking.IsActivated) return null;
        var booking = CurrentBooking;

        _simBrief.OpenDispatch(booking);

        var (updated, error) = await _bookingService.ActivateBookingAsync(booking.Id);
        if (updated != null)
        {
            CurrentBooking = updated;

            // Keep BookingService.ActiveBooking's own activated_at in sync -- it's a *different*
            // object reference from CurrentBooking (this class fetches its own copy via
            // GetCurrentBookingAsync, ActiveBooking was set once at booking-creation time), and
            // FlightSessionManager.OnEnginesStarted reads ActiveBooking specifically (a
            // synchronous, no-network check at the moment engines start) to decide whether
            // tracking is allowed to begin -- if this drifts, activating here would never
            // actually unlock flying. Preserves whatever IsActiveBookingCharter already was;
            // SetActiveBooking's own isCharter parameter defaults to false, which would
            // silently clear the charter flag on activation otherwise.
            if (_bookingService.ActiveBooking?.Id == updated.Id)
                _bookingService.SetActiveBooking(updated, _bookingService.IsActiveBookingCharter);
        }
        return error;
    }

    /// <summary>Re-opens the same SimBrief dispatch URL for an already-activated booking (the
    /// pilot may have closed the tab) -- does NOT touch activation state.</summary>
    public void ReopenDispatch()
    {
        if (CurrentBooking != null) _simBrief.OpenDispatch(CurrentBooking);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    public static string GradeLabel(int score) => score switch
    {
        >= 90 => "Grade A",
        >= 75 => "Grade B",
        >= 60 => "Grade C",
        >= 40 => "Grade D",
        _     => "Grade F"
    };

    public static string GradeClass(int score) => score switch
    {
        >= 90 => "grade-a",
        >= 75 => "grade-b",
        >= 60 => "grade-c",
        >= 40 => "grade-d",
        _     => "grade-f"
    };

    public static string GradeChipClass(int score) => score switch
    {
        >= 90 => "chip-green",
        >= 75 => "chip-blue",
        >= 60 => "chip-yellow",
        >= 40 => "chip-orange",
        _     => "chip-red"
    };

    public static string GradeLetter(int score) => score switch
    {
        >= 90 => "A",
        >= 75 => "B",
        >= 60 => "C",
        >= 40 => "D",
        _     => "F"
    };

    public static string VsColor(double vsFpm) => vsFpm switch
    {
        > -250 => "var(--green)",
        > -400 => "var(--accent)",
        _      => "var(--red)"
    };

    public static string ScoreColor(int score) => score switch
    {
        >= 90 => "var(--green)",
        >= 75 => "var(--accent)",
        >= 60 => "var(--yellow)",
        _     => "var(--red)"
    };
}
