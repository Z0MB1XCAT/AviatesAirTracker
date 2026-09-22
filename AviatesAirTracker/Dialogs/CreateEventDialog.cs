using AviatesAirTracker.Services;
using AviatesAirTracker.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace AviatesAirTracker.Dialogs;

// ============================================================
// Native conversion of Events.razor's create-event modal (@if (_showCreateModal) { ... }, plus
// its OpenCreateModal/CloseCreateModal/CreateEvent @code). Follows BookingDialog.cs's established
// dedicated-builder-class pattern -- a single ShowAsync entry point, ContentDialog built entirely
// in C#, dark-themed from the start (RequestedTheme = ElementTheme.Dark + a VisualTreeHelper walk
// to recolor the native Primary/Close buttons, since neither is exposed as a settable property
// and both otherwise default to stock Windows light chrome regardless of the dialog's own dark
// content -- the bug that shipped on Routes and had to be fixed after the fact; this dialog does
// not repeat it).
//
// Brush constants and the FindDescendant<T>/ThemeDialogButtons helpers are redeclared locally
// here rather than shared with BookingDialog -- BookingDialog's are private to that class, and
// duplicating a handful of small static members is simpler than introducing a shared internal
// helper type for two call sites. Values match BookingDialog's exactly (same app.css dark-theme
// tokens), so both dialogs stay visually identical.
//
// CreateEventDialog never touches EventsService directly -- EventsViewModel (Task 1) exposes
// CreateEventAsync as a thin wrapper specifically so this dialog only ever needs an
// EventsViewModel reference. SettingsService is still needed directly for the ACARS-key warning
// banner and the create-guard check, matching the original reading SettingsService itself rather
// than the ViewModel hiding it.
// ============================================================

public static class CreateEventDialog
{
    private const double FieldWidth = 380;

    // ── Colors, matching app.css's dark-theme tokens (same values BookingDialog.cs already
    //    uses) plus the amber warning tokens (--amber #F59E0B / --amber-light rgba(245,158,11,0.10)
    //    read directly from app.css) for the missing-ACARS-key banner. ──────────────────────────
    private static readonly SolidColorBrush TextPrimaryBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9));
    private static readonly SolidColorBrush TextSecondaryBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1));
    private static readonly SolidColorBrush TextMutedBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x64, 0x74, 0x8B));
    private static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC));
    private static readonly SolidColorBrush BorderBrush_ = new(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush SurfaceBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x0C, 0x11, 0x1D));
    private static readonly SolidColorBrush RedBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush RedLightBg = new(Windows.UI.Color.FromArgb(0x1A, 0xEF, 0x44, 0x44)); // rgba(239,68,68,0.10)
    private static readonly SolidColorBrush RedBorder = new(Windows.UI.Color.FromArgb(0x33, 0xEF, 0x44, 0x44));  // rgba(239,68,68,0.2)
    private static readonly SolidColorBrush AmberBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush AmberLightBg = new(Windows.UI.Color.FromArgb(0x1A, 0xF5, 0x9E, 0x0B)); // rgba(245,158,11,0.10)
    private static readonly SolidColorBrush AmberBorder = new(Windows.UI.Color.FromArgb(0x4D, 0xF5, 0x9E, 0x0B));  // rgba(245,158,11,0.3)

    // Returns whether an event was actually created -- EventsPage needs this to know whether to
    // switch to the Upcoming tab and reload, matching CreateEvent()'s own post-success sequence.
    public static async Task<bool> ShowAsync(XamlRoot xamlRoot, EventsViewModel viewModel, SettingsService settings)
    {
        var created = false;

        // ── ACARS-key warning banner -- matches the original's exact copy and amber styling,
        //    shown whenever no key is set (informational, not itself a blocking control). ───────
        var acarsWarning = new Border
        {
            Background = AmberLightBg,
            BorderBrush = AmberBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            Visibility = string.IsNullOrEmpty(settings.Settings.AcarsKey) ? Visibility.Visible : Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "No ACARS key found. Go to Settings and enter your ACARS key before creating events.",
                FontSize = 13,
                Foreground = AmberBrush,
                TextWrapping = TextWrapping.Wrap,
            },
        };

        // ── Fields, in the original's exact order. ─────────────────────────────────────────────
        var titleBox = new TextBox { PlaceholderText = "Event name", MaxLength = 100, Width = FieldWidth };
        var descriptionBox = new TextBox
        {
            PlaceholderText = "What's this event about?",
            MaxLength = 500,
            Width = FieldWidth,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 72,
        };

        // No explicit Width on these -- they live inside BuildTwoColumnRow's Star-sized Grid
        // columns (each ~half of FieldWidth) and stretch to fill via the default HorizontalAlignment;
        // a fixed FieldWidth here would overflow the column.
        //
        // MinDate blocks picking a past date outright; a same-day-but-past-time combination still
        // needs the separate check at submit time below, since MinDate can't see the time field.
        var datePicker = new CalendarDatePicker
        {
            PlaceholderText = "Select date",
            MinDate = DateTimeOffset.UtcNow.Date,
        };
        var timeUtcBox = new TextBox { PlaceholderText = "18:00", MaxLength = 10 };

        // Route search: pilots pick one of the airline's own already-defined routes instead of
        // typing free text, so an event's route can never reference something that doesn't
        // actually exist in Routes. All routes are loaded once when the dialog opens (RoutesService
        // has no per-keystroke search cost worth paying against ~a few hundred rows) and filtered
        // client-side as the pilot types.
        var routeBox = new AutoSuggestBox
        {
            PlaceholderText = "Search a route (e.g. EGLL, KJFK, VAV103)",
            Width = FieldWidth,
        };
        List<AviatesRoute> allRoutes = [];
        AviatesRoute? selectedRoute = null;

        // Aircraft dropdown: sourced from the fleet, replacing the old free-text "e.g. A320
        // family" box -- an event's aircraft is now always a real type from AircraftPerformanceProfiles'
        // FleetService list rather than an arbitrary string a pilot could mistype.
        var aircraftCombo = new ComboBox { PlaceholderText = "Any aircraft" };
        aircraftCombo.Items.Add(new ComboBoxItem { Content = "Any aircraft", Tag = "" });
        aircraftCombo.SelectedIndex = 0;

        // ── Inline validation / server error -- hidden until a failure. ───────────────────────
        var errorText = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Foreground = RedBrush,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var errorBorder = new Border
        {
            Background = RedLightBg,
            BorderBrush = RedBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            Visibility = Visibility.Collapsed,
            Child = errorText,
        };

        var content = new StackPanel
        {
            Spacing = 14,
            Width = FieldWidth,
            Children =
            {
                acarsWarning,
                BuildField("Title *", titleBox),
                BuildField("Description", descriptionBox),
                BuildTwoColumnRow(BuildField("Date *", datePicker), BuildField("Time (UTC)", timeUtcBox)),
                BuildField("Route", routeBox),
                BuildField("Aircraft", aircraftCombo),
                errorBorder,
            },
        };

        // Loaded once when the dialog opens -- RoutesService/FleetService are resolved here
        // rather than threaded through ShowAsync's signature, matching the inline
        // App.GetService<T>() pattern other pages already use for a one-off dependency a
        // constructor-injected field would be overkill for.
        var routesService = App.GetService<RoutesService>();
        var fleetService  = App.GetService<FleetService>();

        _ = LoadRoutesAsync(routesService, routeBox, r => allRoutes = r);
        _ = LoadFleetAsync(fleetService, aircraftCombo);

        routeBox.TextChanged += (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var query = sender.Text?.Trim() ?? "";
            selectedRoute = null;
            sender.ItemsSource = string.IsNullOrEmpty(query)
                ? []
                : allRoutes
                    .Where(r =>
                        r.OriginIata.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        r.DestIata.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        r.Callsign.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        r.FlightNumber.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Take(20)
                    .Select(r => $"{r.OriginIata} → {r.DestIata} · {r.FlightNumber}")
                    .ToList();
        };
        routeBox.SuggestionChosen += (sender, args) =>
        {
            var label = args.SelectedItem as string;
            selectedRoute = allRoutes.FirstOrDefault(r => $"{r.OriginIata} → {r.DestIata} · {r.FlightNumber}" == label);
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Background = SurfaceBrush,
            Foreground = TextPrimaryBrush,
            BorderBrush = BorderBrush_,
            CornerRadius = new CornerRadius(16),
            Title = new TextBlock
            {
                Text = "Create Event",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 17,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = TextPrimaryBrush,
            },
            Content = new ScrollViewer { Content = content, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Create Event",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        // Same fix BookingDialog.cs established: RequestedTheme/Background/Foreground above only
        // reach the dialog's own chrome, not the native Primary/Close Button instances (drawn from
        // the system's light-theme button brushes by default since neither is a settable property
        // on ContentDialog). PrimaryButton/CloseButton are fixed, stable template part names, so
        // once the template is applied (Opened) we reach in via VisualTreeHelper and recolor the
        // actual Button instances directly.
        dialog.Opened += (_, _) => ThemeDialogButtons(dialog);

        var isCreating = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (isCreating) { args.Cancel = true; return; } // re-entrancy guard against a fast double-click

            errorBorder.Visibility = Visibility.Collapsed;

            // ACARS key, title, date presence, then (below, once the date is known) that the
            // combined date+time isn't already in the past.
            var key = settings.Settings.AcarsKey;
            if (string.IsNullOrEmpty(key))
            {
                ShowError(errorBorder, errorText, "Set your ACARS key in Settings first.");
                args.Cancel = true;
                return;
            }
            if (string.IsNullOrWhiteSpace(titleBox.Text))
            {
                ShowError(errorBorder, errorText, "Event title is required.");
                args.Cancel = true;
                return;
            }
            if (datePicker.Date is null)
            {
                ShowError(errorBorder, errorText, "Event date is required.");
                args.Cancel = true;
                return;
            }

            var d = datePicker.Date.Value;
            var eventDateOnly = new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc);
            var timeOfDay = TimeSpan.TryParseExact(timeUtcBox.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var t)
                ? t : TimeSpan.Zero;
            // MinDate above already blocks a past DATE; this catches the one case it can't see --
            // today's date with a time that's already passed.
            if (eventDateOnly + timeOfDay <= DateTime.UtcNow)
            {
                ShowError(errorBorder, errorText, "Event date/time must be in the future.");
                args.Cancel = true;
                return;
            }

            var deferral = args.GetDeferral();
            try
            {
                isCreating = true;

                var eventDate = eventDateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var aircraftCode = (aircraftCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
                var routeLabel = selectedRoute != null
                    ? $"{selectedRoute.OriginIata} → {selectedRoute.DestIata}"
                    : "";

                var req = new CreateEventRequest
                {
                    Title = titleBox.Text.Trim(),
                    Description = descriptionBox.Text.Trim(),
                    EventDate = eventDate,
                    TimeUtc = timeUtcBox.Text.Trim(),
                    Route = routeLabel,
                    AircraftRestriction = aircraftCode,
                };

                var (ok, err) = await viewModel.CreateEventAsync(req);
                if (ok)
                {
                    created = true;
                    // args.Cancel left false -- the dialog closes normally.
                }
                else
                {
                    ShowError(errorBorder, errorText, err);
                    args.Cancel = true; // keep the dialog open on failure, matching _statusMessage display
                }
            }
            finally
            {
                isCreating = false;
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
        return created;
    }

    // Recolors the dialog's native Primary/Close buttons to match this dialog's own dark palette
    // instead of the stock Windows light-theme button chrome, identical treatment to
    // BookingDialog.ThemeDialogButtons.
    private static void ThemeDialogButtons(ContentDialog dialog)
    {
        if (FindDescendant<Button>(dialog, "PrimaryButton") is { } primary)
        {
            primary.Background = AccentBrush;
            primary.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            primary.BorderBrush = AccentBrush;
        }
        if (FindDescendant<Button>(dialog, "CloseButton") is { } close)
        {
            close.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            close.Foreground = TextSecondaryBrush;
            close.BorderBrush = BorderBrush_;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && typed.Name == name) return typed;
            if (FindDescendant<T>(child, name) is { } found) return found;
        }
        return null;
    }

    private static async Task LoadRoutesAsync(RoutesService routesService, AutoSuggestBox routeBox, Action<List<AviatesRoute>> onLoaded)
    {
        var (routes, _) = await routesService.GetRoutesAsync(limit: 500);
        onLoaded(routes);
    }

    private static async Task LoadFleetAsync(FleetService fleetService, ComboBox aircraftCombo)
    {
        var data = await fleetService.GetFleetDataAsync();
        foreach (var type in data.Types)
        {
            aircraftCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{type.Name} ({type.TypeCode})",
                Tag = type.TypeCode,
            });
        }
    }

    private static void ShowError(Border errorBorder, TextBlock errorText, string message)
    {
        errorText.Text = message;
        errorBorder.Visibility = Visibility.Visible;
    }

    private static StackPanel BuildField(string label, FrameworkElement control) => new()
    {
        Spacing = 6,
        Children =
        {
            new TextBlock { Text = label.ToUpperInvariant(), FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, CharacterSpacing = 40, Foreground = TextSecondaryBrush },
            control,
        },
    };

    private static Grid BuildTwoColumnRow(FrameworkElement left, FrameworkElement right)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }
}
