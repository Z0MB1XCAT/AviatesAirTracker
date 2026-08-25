using AviatesAirTracker.Services;
using AviatesAirTracker.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Globalization;
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

        // No explicit Width on these four -- they live inside BuildTwoColumnRow's Star-sized Grid
        // columns (each ~half of FieldWidth) and stretch to fill via the default HorizontalAlignment;
        // a fixed FieldWidth here would overflow the column.
        var datePicker = new CalendarDatePicker
        {
            PlaceholderText = "Select date",
        };
        var timeUtcBox = new TextBox { PlaceholderText = "18:00", MaxLength = 10 };

        var routeBox = new TextBox { PlaceholderText = "e.g. EGLL → KJFK", MaxLength = 100, Width = FieldWidth };

        var aircraftRestrictionBox = new TextBox { PlaceholderText = "e.g. A320 family", MaxLength = 100 };
        var rankRestrictionBox = new TextBox { PlaceholderText = "e.g. Captain+ only", MaxLength = 100 };

        // Plain numeric TextBox (0 = unlimited, matching the original's min="0" max="9999") rather
        // than NumberBox -- this field never validates a range beyond that in the original either,
        // so a numeric InputScope hint for the on-screen keyboard is enough.
        var maxParticipantsBox = new TextBox
        {
            PlaceholderText = "0",
            Text = "0",
            MaxLength = 4,
            Width = FieldWidth,
            InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } },
        };

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
                BuildTwoColumnRow(BuildField("Aircraft Restriction", aircraftRestrictionBox), BuildField("Rank Restriction", rankRestrictionBox)),
                BuildField("Max Participants (0 = unlimited)", maxParticipantsBox),
                errorBorder,
            },
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

            // Matches CreateEvent()'s exact 3 guard checks, in order -- no more, no less (no
            // max-participants range check, no time-format check; neither exists in the original).
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

            var deferral = args.GetDeferral();
            try
            {
                isCreating = true;

                var d = datePicker.Date.Value;
                var eventDate = new DateTime(d.Year, d.Month, d.Day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                // Non-numeric input just falls back to 0 (unlimited), no error shown -- matches
                // the original's lack of format validation on this field. (Return value
                // intentionally unused -- not discarded via "_" since the enclosing lambda's own
                // first parameter is already named "_" and is a real ContentDialog parameter, not
                // a true discard, since it's the only "_" in its parameter list.)
                int.TryParse(maxParticipantsBox.Text.Trim(), out var maxParticipants);

                var req = new CreateEventRequest
                {
                    Title = titleBox.Text.Trim(),
                    Description = descriptionBox.Text.Trim(),
                    EventDate = eventDate,
                    TimeUtc = timeUtcBox.Text.Trim(),
                    Route = routeBox.Text.Trim(),
                    AircraftRestriction = aircraftRestrictionBox.Text.Trim(),
                    RankRestriction = rankRestrictionBox.Text.Trim(),
                    MaxParticipants = maxParticipants,
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
