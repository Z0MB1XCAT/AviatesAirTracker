using AviatesAirTracker.Dialogs;
using AviatesAirTracker.Platform;
using AviatesAirTracker.Services;
using AviatesAirTracker.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using Windows.UI;

namespace AviatesAirTracker.Pages;

/// <summary>
/// Pre-formatted display row for one event card in the list. Plain mutable class with
/// { get; set; } properties, not a record (CS8852 risk, same as every prior display-row class
/// in this migration -- RouteDisplayRow/TypeDisplayRow/etc.). ShowRegisterButton/
/// ShowUnregisterButton/ShowFullChip are mutually exclusive and only meaningful when
/// ShowActionRow is Visible (the whole trailing action area is hidden entirely on the Past tab,
/// matching Events.razor's own `@if (_activeTab != "past")`).
/// </summary>
public sealed class EventDisplayRow
{
    public int EventId { get; set; }
    public bool CurrentlyRegistered { get; set; }

    public string DayText { get; set; } = "";
    public string MonthText { get; set; } = "";

    public string Title { get; set; } = "";
    public Visibility ShowFeaturedChip { get; set; } = Visibility.Collapsed;
    public Visibility ShowRegisteredChip { get; set; } = Visibility.Collapsed;

    public string Description { get; set; } = "";
    public Visibility ShowDescription { get; set; } = Visibility.Collapsed;

    public string ByLine { get; set; } = "";
    public Visibility ShowByLine { get; set; } = Visibility.Collapsed;

    public string TimeUtcText { get; set; } = "";
    public Visibility ShowTimeUtc { get; set; } = Visibility.Collapsed;

    public string RegCountText { get; set; } = "";

    public string RouteText { get; set; } = "";
    public Visibility ShowRoute { get; set; } = Visibility.Collapsed;

    public string AircraftRestrictionText { get; set; } = "";
    public Visibility ShowAircraftRestriction { get; set; } = Visibility.Collapsed;

    // "Who's attending" -- names fetched via EventsViewModel.GetAttendeesAsync and cached per
    // reload in EventsPage's _attendeesByEvent (see ReloadAsync), not sourced from AviatesEvent
    // itself (the list endpoint only ever returns a count, not names).
    public string AttendeesText { get; set; } = "";
    public Visibility ShowAttendees { get; set; } = Visibility.Collapsed;

    public Visibility ShowActionRow { get; set; } = Visibility.Visible;
    public Visibility ShowRegisterButton { get; set; } = Visibility.Collapsed;
    public Visibility ShowUnregisterButton { get; set; } = Visibility.Collapsed;
    public Visibility ShowFullChip { get; set; } = Visibility.Collapsed;
    public string RegisterButtonText { get; set; } = "Register";
    public string UnregisterButtonText { get; set; } = "Unregister";

    // Disables the specific tapped event's own button while its register/unregister call is in
    // flight (Events.razor's `_busyEvents` HashSet) -- other events' buttons are built from their
    // own IsActionEnabled and stay interactive throughout.
    public bool IsActionEnabled { get; set; } = true;
}

public sealed partial class EventsPage : Page
{
    // Enter/Space on a focused Border-as-button runs its Tapped handler (see KeyboardActivation).
    private void OnActivateKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e) =>
        AviatesAirTracker.Platform.KeyboardActivation.Handle(this, sender, e);

    // Dark-theme hex values read directly from wwwroot/css/app.css's html[data-theme="dark"]
    // block (surface #0C111D, border rgba(255,255,255,0.08), text-primary #F1F5F9, text-muted
    // #64748B, accent #4D9BEC, accent-light rgba(77,155,236,0.12), accent-glow
    // rgba(77,155,236,0.30), red #EF4444, red-light rgba(239,68,68,0.10)) -- same values every
    // prior native page in this migration hardcodes (RoutesPage/FleetPage).
    private static readonly Brush StatusAccentBg = new SolidColorBrush(Color.FromArgb(0x1F, 0x4D, 0x9B, 0xEC));
    private static readonly Brush StatusAccentBorder = new SolidColorBrush(Color.FromArgb(0x4D, 0x4D, 0x9B, 0xEC));
    private static readonly Brush StatusAccentFg = new SolidColorBrush(Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC));
    private static readonly Brush StatusErrorBg = new SolidColorBrush(Color.FromArgb(0x1A, 0xEF, 0x44, 0x44));
    private static readonly Brush StatusErrorBorder = new SolidColorBrush(Color.FromArgb(0x4D, 0xEF, 0x44, 0x44));
    private static readonly Brush StatusErrorFg = new SolidColorBrush(Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));

    // Tab bar active/inactive treatment: reuses the sidebar's own resource brushes, same
    // technique RoutesPage.xaml.cs's tab bar already established.
    private static readonly Brush TabActiveBackground = (Brush)Application.Current.Resources["SidebarActiveBrush"];
    private static readonly Brush TabActiveForeground = (Brush)Application.Current.Resources["SidebarTextActiveBrush"];
    private static readonly Brush TabInactiveForeground = (Brush)Application.Current.Resources["SidebarTextBrush"];
    private static readonly Brush TabInactiveBackground = new SolidColorBrush(Colors.Transparent);

    public ObservableCollection<EventDisplayRow> EventRows { get; } = [];

    private readonly EventsViewModel _viewModel;
    private readonly SettingsService _settings;

    // Lives on the page, not the ViewModel -- matches Events.razor's own _activeTab/
    // _statusMessage/_isError field placement, the same placement decision RoutesPage made for
    // its own _activeTab/_searchQuery.
    private string _activeTab = "upcoming";
    private string? _statusMessage;
    private bool _isError;

    // Matches Events.razor's own _busyEvents HashSet<int> exactly: only the specific tapped
    // event's own button is disabled/shows "..." while its register/unregister call is in
    // flight, every other event's button stays interactive.
    private readonly System.Collections.Generic.HashSet<int> _busyEvents = [];

    // Populated fresh in ReloadAsync -- see RebuildEventRows's own doc comment for why this
    // isn't fetched per-row.
    private System.Collections.Generic.Dictionary<int, List<EventAttendee>> _attendeesByEvent = [];

    public EventsPage()
    {
        InitializeComponent();

        InteractiveEffects.Attach(TabUpcomingBorder, TabPastBorder, TabMyBorder);

        _viewModel = App.GetService<EventsViewModel>();
        _settings = App.GetService<SettingsService>();
        DataContext = _viewModel;
        EventsList.ItemsSource = EventRows;

        UpdateTabVisuals();
        UpdateStatusBanner();

        Loaded += async (_, _) =>
        {
            await ReloadAsync();
        };
    }

    // ── Tab bar ──────────────────────────────────────────────────────────────────────────────

    private async void OnTabTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border { Tag: string tab } || tab == _activeTab) return; // matches SwitchTab's `if (_activeTab == tab) return;`

        _activeTab = tab;
        _statusMessage = null; // matches SwitchTab's own `_statusMessage = null` reset
        UpdateTabVisuals();
        await ReloadAsync();
    }

    private void UpdateTabVisuals()
    {
        ApplyTabVisual(TabUpcomingBorder, TabUpcomingText, "upcoming");
        ApplyTabVisual(TabPastBorder, TabPastText, "past");
        ApplyTabVisual(TabMyBorder, TabMyText, "my");
    }

    private void ApplyTabVisual(Border border, TextBlock text, string tab)
    {
        var isActive = _activeTab == tab;
        border.Background = isActive ? TabActiveBackground : TabInactiveBackground;
        text.Foreground = isActive ? TabActiveForeground : TabInactiveForeground;
    }

    // ── Create Event ─────────────────────────────────────────────────────────────────────────

    private async void OnCreateEventTapped(object sender, TappedRoutedEventArgs e)
    {
        var created = await CreateEventDialog.ShowAsync(this.XamlRoot, _viewModel, _settings);
        if (!created) return;

        // Matches CreateEvent()'s exact post-success sequence:
        // _showCreateModal = false; _newEvent = new(); _statusMessage = "Event created! It's now
        // live in Upcoming Events."; _isError = false; _activeTab = "upcoming"; await LoadEvents();
        // (the dialog itself already closed and reset its own fields on success -- this page just
        // reacts to the returned bool.)
        _activeTab = "upcoming";
        UpdateTabVisuals();
        _statusMessage = "Event created! It's now live in Upcoming Events.";
        _isError = false;
        await ReloadAsync();
    }

    // ── Status banner ────────────────────────────────────────────────────────────────────────

    private void OnStatusCloseTapped(object sender, TappedRoutedEventArgs e)
    {
        _statusMessage = null;
        UpdateStatusBanner();
    }

    private void UpdateStatusBanner()
    {
        if (_statusMessage == null)
        {
            StatusBanner.Visibility = Visibility.Collapsed;
            return;
        }

        StatusBanner.Visibility = Visibility.Visible;
        StatusText.Text = _statusMessage;

        var bg = _isError ? StatusErrorBg : StatusAccentBg;
        var border = _isError ? StatusErrorBorder : StatusAccentBorder;
        var fg = _isError ? StatusErrorFg : StatusAccentFg;

        StatusBanner.Background = bg;
        StatusBanner.BorderBrush = border;
        StatusBanner.BorderThickness = new Thickness(1);
        StatusText.Foreground = fg;
        StatusCloseText.Foreground = fg;
    }

    // ── Reload / loading / featured hero / empty states / event list ───────────────────────────

    private async Task ReloadAsync()
    {
        ShowLoadingState();
        await _viewModel.LoadEventsAsync(_activeTab);

        // Fetched once per reload for every currently-loaded event, in parallel, rather than
        // per-row inside RebuildEventRows -- that method runs synchronously and can re-run
        // several times per reload (e.g. from HandleRegisterAsync's busy-state rebuild), so an
        // await there would either block the UI thread or require a much larger restructure for
        // no benefit given how few events are ever on screen at once.
        var ids = _viewModel.Events.Select(e => e.Id).ToList();
        var pairs = await Task.WhenAll(ids.Select(async id => (id, attendees: await _viewModel.GetAttendeesAsync(id))));
        _attendeesByEvent = pairs.ToDictionary(p => p.id, p => p.attendees);

        RebuildUi();
    }

    private void ShowLoadingState()
    {
        LoadingPanel.Visibility = Visibility.Visible;
        FeaturedHeroBorder.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Collapsed;
        EventsList.Visibility = Visibility.Collapsed;
    }

    private void RebuildUi()
    {
        UpdateStatusBanner();
        LoadingPanel.Visibility = Visibility.Collapsed;

        // Featured hero: Upcoming tab only, matches `_activeTab == "upcoming" && _featuredEvent != null`.
        var featured = _activeTab == "upcoming" ? _viewModel.FeaturedEvent : null;
        if (featured != null)
        {
            FeaturedHeroBorder.Visibility = Visibility.Visible;
            var monthYear = featured.ParsedDate?.ToString("MMMM yyyy", CultureInfo.InvariantCulture) ?? "";
            FeaturedEyebrowText.Text = $"Featured Event · {monthYear}";
            FeaturedTitleText.Text = featured.Title;
            FeaturedDescText.Text = featured.Description;
            FeaturedCtaText.Text = featured.IsRegistered ? "Unregister" : "Register Now";
        }
        else
        {
            FeaturedHeroBorder.Visibility = Visibility.Collapsed;
        }

        if (_viewModel.Events.Count == 0)
        {
            EmptyPanel.Visibility = Visibility.Visible;
            EventsList.Visibility = Visibility.Collapsed;

            // Three distinct messages, matching Events.razor lines 79-94 exactly.
            EmptyText.Text = _activeTab switch
            {
                "my" => string.IsNullOrEmpty(_settings.Settings.AcarsKey)
                    ? "Set your ACARS key in Settings to view your registered events."
                    : "You haven't registered for any events yet.",
                "past" => "No past events found.",
                _ => "No upcoming events at the moment. Check back soon!",
            };
            return;
        }

        EmptyPanel.Visibility = Visibility.Collapsed;
        EventsList.Visibility = Visibility.Visible;
        RebuildEventRows();
    }

    private void RebuildEventRows()
    {
        EventRows.Clear();

        var showAction = _activeTab != "past"; // matches `@if (_activeTab != "past")`

        foreach (var evt in _viewModel.Events)
        {
            var full = evt.MaxParticipants > 0 && evt.RegistrationCount >= evt.MaxParticipants && !evt.IsRegistered;
            var busy = _busyEvents.Contains(evt.Id);

            EventRows.Add(new EventDisplayRow
            {
                EventId = evt.Id,
                CurrentlyRegistered = evt.IsRegistered,

                DayText = evt.DisplayDay,
                MonthText = evt.DisplayMonth,

                Title = evt.Title,
                ShowFeaturedChip = evt.IsFeatured ? Visibility.Visible : Visibility.Collapsed,
                ShowRegisteredChip = evt.IsRegistered ? Visibility.Visible : Visibility.Collapsed,

                Description = evt.Description,
                ShowDescription = string.IsNullOrEmpty(evt.Description) ? Visibility.Collapsed : Visibility.Visible,

                ByLine = string.IsNullOrEmpty(evt.CreatedByName) ? "" : $"By {evt.CreatedByName}",
                ShowByLine = string.IsNullOrEmpty(evt.CreatedByName) ? Visibility.Collapsed : Visibility.Visible,

                TimeUtcText = $"{evt.TimeUtc} UTC",
                ShowTimeUtc = string.IsNullOrEmpty(evt.TimeUtc) ? Visibility.Collapsed : Visibility.Visible,

                RegCountText = $"{evt.RegistrationCount}{(evt.MaxParticipants > 0 ? $"/{evt.MaxParticipants}" : "")} registered",

                RouteText = evt.Route,
                ShowRoute = string.IsNullOrEmpty(evt.Route) ? Visibility.Collapsed : Visibility.Visible,

                AircraftRestrictionText = evt.AircraftRestriction,
                ShowAircraftRestriction = string.IsNullOrEmpty(evt.AircraftRestriction) ? Visibility.Collapsed : Visibility.Visible,

                AttendeesText = BuildAttendeesText(_attendeesByEvent.GetValueOrDefault(evt.Id, [])),
                ShowAttendees = _attendeesByEvent.TryGetValue(evt.Id, out var atts) && atts.Count > 0
                    ? Visibility.Visible : Visibility.Collapsed,

                ShowActionRow = showAction ? Visibility.Visible : Visibility.Collapsed,
                ShowRegisterButton = showAction && !evt.IsRegistered && !full ? Visibility.Visible : Visibility.Collapsed,
                ShowUnregisterButton = showAction && evt.IsRegistered ? Visibility.Visible : Visibility.Collapsed,
                ShowFullChip = showAction && !evt.IsRegistered && full ? Visibility.Visible : Visibility.Collapsed,
                RegisterButtonText = busy ? "..." : "Register",
                UnregisterButtonText = busy ? "..." : "Unregister",
                IsActionEnabled = !busy,
            });
        }
    }

    // Names up front (up to 4), "+N more" for the rest -- an event with a large turnout would
    // otherwise overflow the card. "No one" case is handled by ShowAttendees staying Collapsed,
    // this never runs on an empty list.
    private static string BuildAttendeesText(List<EventAttendee> attendees)
    {
        const int maxNames = 4;
        var names = attendees.Take(maxNames).Select(a => string.IsNullOrWhiteSpace(a.PilotName) ? "Pilot" : a.PilotName);
        var text = string.Join(", ", names);
        var remaining = attendees.Count - maxNames;
        return remaining > 0 ? $"{text} +{remaining} more" : text;
    }

    // ── Register / Unregister ───────────────────────────────────────────────────────────────
    // Shared by both the featured hero's CTA and each list item's own button.

    private async void OnFeaturedCtaTapped(object sender, TappedRoutedEventArgs e)
    {
        var featured = _viewModel.FeaturedEvent;
        if (featured == null) return;
        await HandleRegisterAsync(featured.Id, featured.IsRegistered);
    }

    private async void OnEventActionTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: EventDisplayRow row }) return;
        await HandleRegisterAsync(row.EventId, row.CurrentlyRegistered);
    }

    // Matches HandleRegister's exact sequencing: ACARS-key guard (return without calling the
    // service) -> add to busy set -> rebuild (shows the busy state) -> register/unregister call
    // -> status message -> remove from busy set -> a separate LoadEventsAsync call + rebuild.
    // Deliberately not bundled into one step, per EventsViewModel's own doc comment.
    private async Task HandleRegisterAsync(int eventId, bool currentlyRegistered)
    {
        if (string.IsNullOrEmpty(_settings.Settings.AcarsKey))
        {
            _statusMessage = "Set your ACARS key in Settings before registering for events.";
            _isError = true;
            UpdateStatusBanner();
            return;
        }

        _busyEvents.Add(eventId);
        RebuildEventRows(); // only this event's own button goes busy; the rest are rebuilt from their own current busy state

        var (ok, message) = await _viewModel.RegisterAsync(eventId, currentlyRegistered);
        _statusMessage = message;
        _isError = !ok;

        _busyEvents.Remove(eventId);

        await ReloadAsync();
    }
}
