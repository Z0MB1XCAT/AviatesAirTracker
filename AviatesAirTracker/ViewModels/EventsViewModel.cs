using AviatesAirTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AviatesAirTracker.ViewModels;

/// <summary>
/// Ported from Events.razor's @code block (verbatim behavior, not a redesign). RegisterAsync
/// deliberately does NOT reload afterward -- HandleRegister in the original sets the status
/// message first, then calls LoadEvents() as a separate step; the page (Task 2) owns that
/// sequencing. CreateEventAsync is a thin wrapper with no validation of its own -- the original's
/// three guard checks (ACARS key present, title non-blank, date non-blank) live in CreateEvent()
/// on the page/dialog side, not in the service wrapper.
/// </summary>
public partial class EventsViewModel : ObservableObject
{
    private readonly EventsService _events;
    private readonly SettingsService _settings;

    [ObservableProperty] private AviatesEvent? _featuredEvent;
    [ObservableProperty] private bool _isLoading;

    public ObservableCollection<AviatesEvent> Events { get; } = [];

    public EventsViewModel(EventsService events, SettingsService settings)
    {
        _events = events;
        _settings = settings;
    }

    public async Task LoadEventsAsync(string tab)
    {
        IsLoading = true;
        try
        {
            var key = _settings.Settings.AcarsKey;

            var result = tab switch
            {
                "past" => await _events.GetEventsAsync("past"),
                "my" when !string.IsNullOrEmpty(key) => await _events.GetMyEventsAsync(key),
                "my" => [],
                _ => await _events.GetEventsAsync("upcoming"),
            };

            Events.Clear();
            foreach (var e in result) Events.Add(e);
            FeaturedEvent = Events.FirstOrDefault(e => e.IsFeatured);
        }
        finally { IsLoading = false; }
    }

    // Matches HandleRegister's exact sequencing: register/unregister call, then the caller sets
    // the status message, then a separate LoadEventsAsync call. Deliberately NOT bundled here --
    // the page (Task 2) calls LoadEventsAsync itself right after this returns.
    public async Task<(bool Success, string Message)> RegisterAsync(int eventId, bool currentlyRegistered)
    {
        var key = _settings.Settings.AcarsKey;

        var (ok, err) = currentlyRegistered
            ? await _events.UnregisterFromEventAsync(eventId, key)
            : await _events.RegisterForEventAsync(eventId, key);

        var message = ok
            ? (currentlyRegistered ? "Successfully unregistered." : "Registered! See you there.")
            : err;

        return (ok, message);
    }

    public async Task<(bool Success, string Error)> CreateEventAsync(CreateEventRequest req)
        => await _events.CreateEventAsync(req, _settings.Settings.AcarsKey);
}
