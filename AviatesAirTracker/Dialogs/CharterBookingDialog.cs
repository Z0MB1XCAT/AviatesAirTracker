using AviatesAirTracker.Services;
using AviatesAirTracker.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace AviatesAirTracker.Dialogs;

// ============================================================
// CHARTER BOOKING DIALOG
//
// Reached from RoutesPage's "Charter" button (header, not per-route-card) -- everywhere else
// this app books a flight, the route comes first (tap a route card, aircraft is fixed by that
// route) and only the departure date/time/registration/callsign are chosen inside the dialog.
// A charter flips that: the pilot types both a departure and arrival ICAO directly (two plain
// text fields, not a search box) and picks the aircraft freely from the whole fleet, rather than
// either being fixed by a pre-tapped route card.
//
// Unlike a normal booking, a charter is NOT restricted to the scheduled route network -- it can
// go anywhere (format-checked, four letters/digits each, dep != arr; there's no worldwide
// airport database in this app to validate the ICAO further, so SimBrief itself is the real
// validation once the pilot activates the booking from Pilot Hub and it opens dispatch). The
// aircraft, however, IS still constrained to the real fleet: this dialog's ComboBox only ever
// lists /api/fleet/types entries (LoadFleetAsync below), and the server independently re-checks
// aircraftType against aircraft_types on submit (see worker.js's POST /api/bookings isCharter
// branch) so a stale/tampered client can't bypass it. Flight number and callsign are both
// randomly generated -- a charter isn't "flight VAV103", it's a one-off.
//
// Two real behavioral differences from a normal booking: a charter earns 0.5x hours
// (RoutesViewModel.CreateBookingAsync's isCharter flag -> BookingService.SetActiveBooking ->
// FlightSessionManager.OnEnginesStarted), stated explicitly via the amber banner below so it's
// never a surprise after the fact; and it has no AviatesRoute at all, so it goes through
// RoutesViewModel.CreateCharterBookingAsync rather than CreateBookingAsync.
//
// Colors/header/toast/callsign-row shapes mirror BookingDialog.cs's own (not shared -- see that
// file's own doc comment for why duplicating a handful of small members beats a shared internal
// type for two call sites).
// ============================================================

public static class CharterBookingDialog
{
    private const double FieldWidth = 380;

    private static readonly SolidColorBrush TextPrimaryBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9));
    private static readonly SolidColorBrush TextSecondaryBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1));
    private static readonly SolidColorBrush TextMutedBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x64, 0x74, 0x8B));
    private static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC));
    private static readonly SolidColorBrush AccentLightBg = new(Windows.UI.Color.FromArgb(0x1F, 0x4D, 0x9B, 0xEC));
    private static readonly SolidColorBrush BorderBrush_ = new(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush SurfaceBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x0C, 0x11, 0x1D));
    private static readonly SolidColorBrush GreenBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3E, 0xC7, 0xAC));
    private static readonly SolidColorBrush RedBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush RedLightBg = new(Windows.UI.Color.FromArgb(0x1A, 0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush RedBorder = new(Windows.UI.Color.FromArgb(0x33, 0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush AmberBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush AmberLightBg = new(Windows.UI.Color.FromArgb(0x1A, 0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush AmberBorder = new(Windows.UI.Color.FromArgb(0x4D, 0xF5, 0x9E, 0x0B));

    public static async Task ShowAsync(XamlRoot xamlRoot, RoutesViewModel viewModel)
    {
        var fleetService = App.GetService<FleetService>();

        var airportsValid = false;
        string? selectedAircraftCode = null;
        // Flight number/callsign are both randomly generated, never taken from the matched
        // route's own real scheduled service -- a charter isn't "flight VAV103", it's a one-off.
        var randomFlightNumber = GenerateFlightNumber();

        // ── 0.5x hours banner -- stated up front, not buried at the bottom. ────────────────────
        var hoursBanner = new Border
        {
            Background = AmberLightBg,
            BorderBrush = AmberBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            Child = new TextBlock
            {
                Text = "Charter flights earn 0.5x hours toward your career stats.",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = AmberBrush,
                TextWrapping = TextWrapping.Wrap,
            },
        };

        // ── Departure / arrival ICAO -- two plain text fields, checked only for well-formed
        //    4-character codes and dep != arr (ValidateAirports below). Unlike every other
        //    booking in this app, a charter is NOT matched against the scheduled route network --
        //    it can go anywhere. ─────────────────────────────────────────────────────────────
        var depBox = new TextBox { PlaceholderText = "e.g. EGLL", MaxLength = 4, CharacterCasing = CharacterCasing.Upper };
        var arrBox = new TextBox { PlaceholderText = "e.g. KJFK", MaxLength = 4, CharacterCasing = CharacterCasing.Upper };
        var routeRow = new Grid { ColumnSpacing = 12, Width = FieldWidth };
        routeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        routeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var depField = BuildField("Departure ICAO", depBox);
        var arrField = BuildField("Arrival ICAO", arrBox);
        Grid.SetColumn(depField, 0);
        Grid.SetColumn(arrField, 1);
        routeRow.Children.Add(depField);
        routeRow.Children.Add(arrField);
        var routeSummary = new TextBlock { FontSize = 12, Foreground = TextMutedBrush, Visibility = Visibility.Collapsed };

        // ── Aircraft dropdown -- unlike BookingDialog's fixed read-only type, a charter's
        //    aircraft is chosen freely from the fleet. ─────────────────────────────────────────
        var aircraftCombo = new ComboBox { PlaceholderText = "Select aircraft", Width = FieldWidth };

        // ── Date / time -- identical defaults to BookingDialog's own OpenBookingModal-matched
        //    "tomorrow at 10:00, max 90 days out" values. ───────────────────────────────────────
        var today = DateTime.Today;
        var datePicker = new CalendarDatePicker
        {
            Date = new DateTimeOffset(today.AddDays(1)),
            MinDate = new DateTimeOffset(today),
            MaxDate = new DateTimeOffset(today.AddDays(90)),
            PlaceholderText = "Select date",
            Width = FieldWidth,
        };
        var timePicker = new TimePicker
        {
            Time = new TimeSpan(10, 0, 0),
            ClockIdentifier = "24HourClock",
            Width = FieldWidth,
        };

        // ── Registration -- reloaded whenever the chosen aircraft type changes (BookingDialog
        //    only ever loads once, since its aircraft type never changes after the route is
        //    picked). ─────────────────────────────────────────────────────────────────────────
        var registrationCombo = new ComboBox { Width = FieldWidth, Visibility = Visibility.Collapsed };
        var registrationLoading = BuildInlineStatus("Loading registrations…", spin: true);
        var registrationNone = BuildInlineStatus("Select an aircraft to see registrations", spin: false);

        // ── Callsign preview + regenerate. ──────────────────────────────────────────────────
        var callsignValueText = new TextBlock
        {
            Text = "----",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = AccentBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var regenerateBorder = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = BorderBrush_,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 5, 10, 5),
            VerticalAlignment = VerticalAlignment.Center,
        };
        regenerateBorder.Child = new TextBlock { Text = "Regenerate", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = TextSecondaryBrush };
        regenerateBorder.Tapped += (_, _) =>
        {
            if (selectedAircraftCode == null) return;
            callsignValueText.Text = RoutesViewModel.GenerateCallsign(selectedAircraftCode);
            randomFlightNumber = GenerateFlightNumber();
        };

        var callsignRow = new Border
        {
            Background = AccentLightBg,
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, 0x4D, 0x9B, 0xEC)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
        };
        var callsignGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var callsignLabel = new TextBlock { Text = "YOUR CALLSIGN", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, CharacterSpacing = 60, Foreground = TextMutedBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        Grid.SetColumn(callsignLabel, 0);
        Grid.SetColumn(callsignValueText, 1);
        Grid.SetColumn(regenerateBorder, 2);
        callsignGrid.Children.Add(callsignLabel);
        callsignGrid.Children.Add(callsignValueText);
        callsignGrid.Children.Add(regenerateBorder);
        callsignRow.Child = callsignGrid;

        // ── Inline validation / server error. ───────────────────────────────────────────────
        var errorText = new TextBlock { FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = RedBrush, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var errorBorder = new Border { Background = RedLightBg, BorderBrush = RedBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Visibility = Visibility.Collapsed, Child = errorText };

        var content = new StackPanel
        {
            Spacing = 14,
            Width = FieldWidth,
            Children =
            {
                hoursBanner,
                routeRow,
                routeSummary,
                BuildField("Aircraft", aircraftCombo),
                BuildField("Departure Date", datePicker),
                BuildField("Departure Time (UTC)", timePicker),
                BuildRegistrationField(registrationCombo, registrationLoading, registrationNone),
                callsignRow,
                errorBorder,
                BuildInfoLine($"You can hold up to {BookingService.MaxBookings} active booking. Activate it from Pilot Hub when you're ready to fly, which opens SimBrief dispatch pre-filled for this flight; activating locks the booking, so you can only cancel before then."),
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
            Title = new TextBlock { Text = "Charter a Flight", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = TextPrimaryBrush },
            Content = new ScrollViewer { Content = content, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Confirm Charter",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        dialog.Opened += (_, _) => ThemeDialogButtons(dialog);

        _ = LoadFleetAsync(fleetService, aircraftCombo);

        // Format-only check, live as either ICAO box changes, not just at submit -- there's no
        // worldwide airport database in this app to validate the codes any further than "well
        // formed and different from each other"; SimBrief itself is the real check once the
        // pilot activates the booking from Pilot Hub.
        void ValidateAirports()
        {
            var dep = depBox.Text.Trim();
            var arr = arrBox.Text.Trim();
            var wellFormed = dep.Length == 4 && arr.Length == 4;
            airportsValid = wellFormed && !string.Equals(dep, arr, StringComparison.OrdinalIgnoreCase);

            if (airportsValid)
            {
                routeSummary.Text = $"Charter route: {dep} → {arr}";
                routeSummary.Foreground = TextMutedBrush;
                routeSummary.Visibility = Visibility.Visible;
            }
            else if (wellFormed)
            {
                routeSummary.Text = "Departure and arrival must be different airports.";
                routeSummary.Foreground = RedBrush;
                routeSummary.Visibility = Visibility.Visible;
            }
            else
            {
                routeSummary.Visibility = Visibility.Collapsed;
            }
        }
        depBox.TextChanged += (_, _) => ValidateAirports();
        arrBox.TextChanged += (_, _) => ValidateAirports();

        aircraftCombo.SelectionChanged += (_, _) =>
        {
            selectedAircraftCode = (aircraftCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            if (string.IsNullOrEmpty(selectedAircraftCode))
            {
                registrationCombo.Visibility = Visibility.Collapsed;
                registrationNone.Visibility = Visibility.Visible;
                registrationLoading.Visibility = Visibility.Collapsed;
                return;
            }

            callsignValueText.Text = RoutesViewModel.GenerateCallsign(selectedAircraftCode);
            randomFlightNumber = GenerateFlightNumber();
            _ = LoadRegistrationsAsync(viewModel, selectedAircraftCode, registrationCombo, registrationLoading, registrationNone);
        };

        var isSubmitting = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (isSubmitting) { args.Cancel = true; return; }

            errorBorder.Visibility = Visibility.Collapsed;

            ValidateAirports();
            if (depBox.Text.Trim().Length != 4 || arrBox.Text.Trim().Length != 4)
            {
                ShowError(errorBorder, errorText, "Enter both a departure and arrival ICAO code.");
                args.Cancel = true;
                return;
            }
            if (!airportsValid)
            {
                ShowError(errorBorder, errorText, "Departure and arrival must be different airports.");
                args.Cancel = true;
                return;
            }
            if (string.IsNullOrEmpty(selectedAircraftCode))
            {
                ShowError(errorBorder, errorText, "Select an aircraft.");
                args.Cancel = true;
                return;
            }
            if (datePicker.Date is null)
            {
                ShowError(errorBorder, errorText, "Please select a departure date and time.");
                args.Cancel = true;
                return;
            }

            var d = datePicker.Date.Value;
            var t = timePicker.Time;
            var depUtc = new DateTime(d.Year, d.Month, d.Day, t.Hours, t.Minutes, 0, DateTimeKind.Utc);

            if (depUtc <= DateTime.UtcNow)
            {
                ShowError(errorBorder, errorText, "Departure must be in the future.");
                args.Cancel = true;
                return;
            }
            if (depUtc > DateTime.UtcNow.AddDays(90))
            {
                ShowError(errorBorder, errorText, "Bookings cannot be made more than 90 days in advance.");
                args.Cancel = true;
                return;
            }

            var deferral = args.GetDeferral();
            try
            {
                isSubmitting = true;
                var registration = (registrationCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                var dep = depBox.Text.Trim();
                var arr = arrBox.Text.Trim();
                var (booking, error) = await viewModel.CreateCharterBookingAsync(
                    dep, arr, selectedAircraftCode, registration, depUtc, callsignValueText.Text);

                if (booking != null)
                {
                    // Random, not a scheduled route's real flight number -- see the
                    // randomFlightNumber field's own doc comment. FlightBooking is a reference
                    // type and RoutesViewModel.CreateCharterBookingAsync already called
                    // SetActiveBooking(booking) internally, so mutating it here still updates
                    // the exact object ActiveBooking now points to.
                    booking.RouteFlightNumber = randomFlightNumber;
                    booking.RouteCallsign = "";

                    // Position is deliberately NOT advanced here -- see BookingDialog.cs's own
                    // PrimaryButtonClick comment for why (same fix, same reasoning, applies here
                    // identically since a charter goes through the same MaxBookings=1 cap).
                    ShowSuccessToast(xamlRoot, booking.Callsign);
                }
                else
                {
                    ShowError(errorBorder, errorText, error ?? "Booking failed. Please try again.");
                    args.Cancel = true;
                }
            }
            finally
            {
                isSubmitting = false;
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }

    // 1-4 digits, matching RoutesViewModel.GenerateCallsign's own suffix-length convention --
    // never leading-zero as a lone digit, avoiding a "0" flight number reading as invalid.
    private static string GenerateFlightNumber()
    {
        var digits = Random.Shared.Next(1, 5);
        var first = Random.Shared.Next(1, 10);
        var rest = digits > 1 ? Random.Shared.Next(0, (int)Math.Pow(10, digits - 1)).ToString($"D{digits - 1}") : "";
        return first + rest;
    }

    private static async Task LoadFleetAsync(FleetService fleetService, ComboBox aircraftCombo)
    {
        var data = await fleetService.GetFleetDataAsync();
        foreach (var type in data.Types)
        {
            aircraftCombo.Items.Add(new ComboBoxItem { Content = $"{type.Name} ({type.TypeCode})", Tag = type.TypeCode });
        }
    }

    private static async Task LoadRegistrationsAsync(
        RoutesViewModel viewModel, string aircraftType, ComboBox combo, StackPanel loading, StackPanel none)
    {
        loading.Visibility = Visibility.Visible;
        combo.Visibility = Visibility.Collapsed;
        none.Visibility = Visibility.Collapsed;

        var registrations = await viewModel.LoadRegistrationsAsync(aircraftType.Trim());

        combo.Items.Clear();
        var defaultIndex = -1;
        for (var i = 0; i < registrations.Count; i++)
        {
            var reg = registrations[i];
            var label = reg.Registration
                + (reg.HubIcao != null ? $"  ({reg.HubIcao})" : "")
                + (reg.Status != "active" ? $"  ({reg.Status})" : "");
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = reg.Registration });
            if (defaultIndex < 0 && reg.Status == "active") defaultIndex = i;
        }
        if (defaultIndex < 0 && registrations.Count > 0) defaultIndex = 0;

        loading.Visibility = Visibility.Collapsed;
        if (registrations.Count == 0)
        {
            none.Visibility = Visibility.Visible;
        }
        else
        {
            combo.Visibility = Visibility.Visible;
            combo.SelectedIndex = defaultIndex;
        }
    }

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

    private static StackPanel BuildRegistrationField(ComboBox combo, StackPanel loading, StackPanel none) => new()
    {
        Spacing = 6,
        Children =
        {
            new TextBlock { Text = "REGISTRATION", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, CharacterSpacing = 40, Foreground = TextSecondaryBrush },
            loading,
            none,
            combo,
        },
    };

    private static StackPanel BuildInlineStatus(string text, bool spin) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children =
        {
            new ProgressRing { IsActive = spin, Width = 14, Height = 14 },
            new TextBlock { Text = text, FontSize = 12.5, Foreground = TextMutedBrush },
        },
    };

    private static TextBlock BuildInfoLine(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = TextMutedBrush,
        TextWrapping = TextWrapping.Wrap,
    };

    private static void ShowSuccessToast(XamlRoot xamlRoot, string callsign)
    {
        const double toastWidth = 340;

        var titleText = new TextBlock { Text = "Charter confirmed!", FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = TextPrimaryBrush };

        var subText = new TextBlock { FontSize = 12, Foreground = TextMutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        subText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "Callsign " });
        subText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = callsign, FontFamily = new FontFamily("Consolas"), FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = AccentBrush });
        subText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = ". Earns 0.5x hours. Check your booking in Pilot Hub." });

        var checkGlyph = new TextBlock { Text = "✓", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = GreenBrush, VerticalAlignment = VerticalAlignment.Top };

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(checkGlyph, 0);
        var textStack = new StackPanel { Children = { titleText, subText } };
        Grid.SetColumn(textStack, 1);
        row.Children.Add(checkGlyph);
        row.Children.Add(textStack);

        var card = new Border
        {
            Width = toastWidth,
            Background = SurfaceBrush,
            BorderBrush = BorderBrush_,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 14, 16, 14),
            Child = row,
        };

        var popup = new Popup { XamlRoot = xamlRoot, Child = card, IsLightDismissEnabled = false };

        var size = xamlRoot.Size;
        popup.HorizontalOffset = Math.Max(16, size.Width - toastWidth - 28);
        popup.VerticalOffset = Math.Max(16, size.Height - 96);
        popup.IsOpen = true;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            popup.IsOpen = false;
            timer.Stop();
        };
        timer.Start();
    }
}
