using AviatesAirTracker.Core.Reference;
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

namespace AviatesAirTracker.Dialogs;

// ============================================================
// Native conversion of Routes.razor's booking modal (@if (_modalRoute != null) { ... }, plus its
// OpenBookingModal/SubmitBooking/LoadRegistrationsAsync/RegenerateCallsign @code). Follows
// LandingResultDialog's dedicated-builder-class pattern, but exposes a single ShowAsync entry
// point (rather than a separate Build) since this dialog's PrimaryButtonClick handler needs to
// close over most of its own controls anyway -- splitting Build out would just mean threading
// all of them back out through an out-parameter bag for no real benefit, unlike LandingResultDialog
// which has no interactive submit flow.
//
// BookingDialog never touches BookingService directly -- RoutesViewModel (Task 1) exposes
// LoadRegistrationsAsync/GenerateCallsign/CreateBookingAsync/SetPositionAsync as thin wrappers
// specifically so this dialog only ever needs a RoutesViewModel reference.
// ============================================================

public static class BookingDialog
{
    private const double FieldWidth = 380;

    // ── Colors, matching app.css's dark-theme tokens (the same values FlightsPage.xaml.cs's own
    //    dialogs already use -- --text-primary/--text-secondary/--text-muted/--accent/--red in
    //    html[data-theme="dark"], plus the dark-theme --surface-2/--border overrides). ──────────
    private static readonly SolidColorBrush TextPrimaryBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9));
    private static readonly SolidColorBrush TextSecondaryBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1));
    private static readonly SolidColorBrush TextMutedBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x64, 0x74, 0x8B));
    private static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC));
    private static readonly SolidColorBrush AccentLightBg = new(Windows.UI.Color.FromArgb(0x1F, 0x4D, 0x9B, 0xEC));
    private static readonly SolidColorBrush BorderBrush_ = new(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush SurfaceBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x0C, 0x11, 0x1D));
    private static readonly SolidColorBrush Surface2Brush = new(Windows.UI.Color.FromArgb(0xFF, 0x12, 0x1A, 0x2A));
    private static readonly SolidColorBrush GreenBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3E, 0xC7, 0xAC));
    private static readonly SolidColorBrush RedBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush RedLightBg = new(Windows.UI.Color.FromArgb(0x1A, 0xEF, 0x44, 0x44)); // rgba(239,68,68,0.10)
    private static readonly SolidColorBrush RedBorder = new(Windows.UI.Color.FromArgb(0x33, 0xEF, 0x44, 0x44));  // rgba(239,68,68,0.2)

    public static async System.Threading.Tasks.Task ShowAsync(XamlRoot xamlRoot, RoutesViewModel viewModel, AviatesRoute route)
    {
        // ── Date / time fields -- CalendarDatePicker/TimePicker, defaults matching
        //    OpenBookingModal's exact "@DateTime.Today.AddDays(1)" / "10:00" defaults. ──────────
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

        // ── Registration selector -- populated async after the dialog opens (Step 3). ──────────
        var registrationCombo = new ComboBox { Width = FieldWidth, Visibility = Visibility.Collapsed };
        var registrationLoading = BuildInlineStatus("Loading registrations…", spin: true);
        var registrationNone = BuildInlineStatus("No registrations available for this aircraft", spin: false);
        registrationNone.Visibility = Visibility.Collapsed;

        // ── Aircraft: a route can be flown by several aircraft (possibly different
        //    subsidiaries). Start on the first one the pilot's own subsidiary flies. ─────────────
        var aircraftOptions = route.AircraftOptions;
        var pilotSubsidiary = App.GetService<SettingsService>().Settings.Subsidiary;
        var selectedAircraft = aircraftOptions.FirstOrDefault(a =>
                                   string.Equals(a.FleetGroup, pilotSubsidiary, StringComparison.OrdinalIgnoreCase))
                               ?? aircraftOptions[0];
        Action<RouteAircraft>? aircraftChanged = null; // set once the dialog exists, below

        // ── Callsign preview + regenerate -- initial value untrimmed, matching
        //    OpenBookingModal's exact `GenerateCallsign(route.AircraftType)` call (the registration
        //    load below uses `.Trim()`; this one deliberately does not -- a real inconsistency in
        //    the original, preserved rather than "fixed"). ─────────────────────────────────────
        var callsignValueText = new TextBlock
        {
            Text = RoutesViewModel.GenerateCallsign(selectedAircraft.Type),
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
        var regenerateText = new TextBlock { Text = "Regenerate", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = TextSecondaryBrush };
        regenerateBorder.Child = regenerateText;
        void Regenerate() => callsignValueText.Text = RoutesViewModel.GenerateCallsign(selectedAircraft.Type);
        regenerateBorder.Tapped += (_, _) => Regenerate();
        AviatesAirTracker.Platform.KeyboardActivation.Enable(regenerateBorder, Regenerate, "Generate a new callsign");

        var callsignRow = new Border
        {
            Background = AccentLightBg,
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, 0x4D, 0x9B, 0xEC)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            Child = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
            },
        };
        var callsignGrid = (Grid)callsignRow.Child;
        var callsignLabel = new TextBlock { Text = "YOUR CALLSIGN", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, CharacterSpacing = 60, Foreground = TextMutedBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        Grid.SetColumn(callsignLabel, 0);
        Grid.SetColumn(callsignValueText, 1);
        Grid.SetColumn(regenerateBorder, 2);
        callsignGrid.Children.Add(callsignLabel);
        callsignGrid.Children.Add(callsignValueText);
        callsignGrid.Children.Add(regenerateBorder);

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
                BuildStatsStrip(route, selectedAircraft),
                BuildAircraftTypeField(route, aircraftOptions, selectedAircraft, pilotSubsidiary, a => aircraftChanged?.Invoke(a)),
                BuildField("Departure Date", datePicker),
                BuildField("Departure Time (UTC)", timePicker),
                BuildRegistrationField(registrationCombo, registrationLoading, registrationNone),
                callsignRow,
                errorBorder,
                BuildInfoLine($"You can hold up to {BookingService.MaxBookings} active bookings. Activate it from Pilot Hub when you're ready to fly, which opens SimBrief dispatch pre-filled for this flight; activating locks the booking, so you can only cancel before then."),
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
            Title = BuildHeader(route),
            Content = new ScrollViewer { Content = content, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Confirm Booking",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false, // re-enabled once the registration load below completes
        };

        // RequestedTheme/Background/Foreground above only reach the dialog's own chrome -- the
        // Primary/Close buttons are drawn by ContentDialog's default template using the system's
        // light-theme button brushes regardless (the "looks like stock Windows" bug reported after
        // Task 4), since neither is exposed as a settable property. PrimaryButton/CloseButton are
        // fixed, stable template part names on every WinAppSDK version, so once the template is
        // applied (Opened) we reach in via VisualTreeHelper and recolor the actual Button instances
        // directly -- not a <Style> element, so the XamlCompiler crash risk this project has hit
        // repeatedly elsewhere doesn't apply here.
        dialog.Opened += (_, _) => ThemeDialogButtons(dialog);

        // Fire-and-forget: the dialog is shown immediately (loading indicator visible), and this
        // populates the ComboBox in place once FleetService responds -- matches OpenBookingModal's
        // own behavior (the modal renders before LoadRegistrationsAsync resolves, then re-renders).
        _ = LoadRegistrationsAsync(viewModel, selectedAircraft.Type, registrationCombo, registrationLoading, registrationNone, dialog);

        // Picking another aircraft changes its block time/subsidiary, its registrations and the
        // callsign prefix.
        aircraftChanged = aircraft =>
        {
            selectedAircraft = aircraft;
            content.Children[0] = BuildStatsStrip(route, aircraft);
            Regenerate();
            errorBorder.Visibility = Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = false;
            _ = LoadRegistrationsAsync(viewModel, aircraft.Type, registrationCombo, registrationLoading, registrationNone, dialog);
        };

        var isSubmitting = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (isSubmitting) { args.Cancel = true; return; } // re-entrancy guard against a fast double-click

            errorBorder.Visibility = Visibility.Collapsed;

            // Check 1: date/time both present. TimePicker.Time is a non-nullable TimeSpan (always
            // has a value), so only the CalendarDatePicker's Date -- which the user can clear via
            // its built-in clear affordance -- needs the null check.
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
                var (booking, error) = await viewModel.CreateBookingAsync(
                    route, selectedAircraft.Type.Trim(), registration, depUtc, callsignValueText.Text);

                if (booking != null)
                {
                    // Position is deliberately NOT advanced here. It used to jump to the route's
                    // destination the instant the booking was created, before the flight was ever
                    // flown -- since MaxBookings is 1, that never actually enabled booking a second
                    // future leg from here, it only left Routes filtering from the wrong airport
                    // if the pilot cancelled instead of flying (cancel had nothing to revert to,
                    // because nothing should have moved yet). BookingService.CompleteActiveBookingAsync
                    // already advances position on real completion, which is the only place it should.
                    ShowSuccessToast(xamlRoot, booking.Callsign);
                    // args.Cancel left false -- the dialog closes normally.
                }
                else
                {
                    ShowError(errorBorder, errorText, error ?? "Booking failed. Please try again.");
                    args.Cancel = true; // keep the dialog open on failure, matching _modalError display
                }
            }
            finally
            {
                isSubmitting = false;
                deferral.Complete();
            }
        };

        await dialog.ShowQueuedAsync();
    }

    // Recolors the dialog's native Primary/Close buttons to match this dialog's own dark palette
    // instead of the stock Windows light-theme button chrome: Confirm Booking gets a solid accent
    // fill (matching the app's own primary-action color), Cancel gets the same translucent-white
    // bordered look as every other secondary Border-button in this dialog (e.g. Regenerate).
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

    // ── Header: route summary (flags + origin -> dest), sub-line, flight number -- read directly
    //    from AviatesRoute, matching the modal header's exact content. ContentDialog.Title accepts
    //    any UIElement (same technique FlightsPage.xaml.cs's BuildDialogHeader uses). ────────────
    private static StackPanel BuildHeader(AviatesRoute route)
    {
        var routeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var depIso = IcaoCountry.IsoFromIcao(route.OriginIata);
        var arrIso = IcaoCountry.IsoFromIcao(route.DestIata);
        AddFlagIfAny(routeRow, depIso);
        AddFlagIfAny(routeRow, arrIso);

        routeRow.Children.Add(new TextBlock { Text = route.OriginIata, FontFamily = new FontFamily("Consolas"), FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = TextPrimaryBrush, VerticalAlignment = VerticalAlignment.Center });
        routeRow.Children.Add(new TextBlock { Text = "→", FontSize = 15, Foreground = TextMutedBrush, VerticalAlignment = VerticalAlignment.Center });
        routeRow.Children.Add(new TextBlock { Text = route.DestIata, FontFamily = new FontFamily("Consolas"), FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = TextPrimaryBrush, VerticalAlignment = VerticalAlignment.Center });

        var header = new StackPanel { Spacing = 4, Children = { routeRow } };
        header.Children.Add(new TextBlock { Text = $"{route.OriginName} → {route.DestName}", FontSize = 12.5, Foreground = TextMutedBrush });
        if (!string.IsNullOrEmpty(route.FlightNumber))
        {
            header.Children.Add(new TextBlock { Text = $"Flight {route.FlightNumber}", FontSize = 12.5, Foreground = TextMutedBrush });
        }
        return header;
    }

    private static void AddFlagIfAny(StackPanel row, string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return;
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot", "images", "flags", $"{iso}.svg");
        if (!System.IO.File.Exists(path)) return;
        row.Children.Add(new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri(path)), Width = 20, Height = 14, Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Center });
    }

    // ── Stats strip: distance / block time / fleet group, matching .booking-stats-strip. ───────
    private static Border BuildStatsStrip(AviatesRoute route, RouteAircraft aircraft)
    {
        var minutes = aircraft.BlockTimeMinutes ?? route.BlockTimeMinutes;
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        strip.Children.Add(BuildStat(route.DistanceNm.ToString(CultureInfo.InvariantCulture), "NM"));
        strip.Children.Add(BuildStat(minutes >= 60 ? $"{minutes / 60}h {minutes % 60:D2}m" : $"{minutes}m", "Block"));
        strip.Children.Add(BuildStat(string.IsNullOrEmpty(aircraft.FleetGroup) ? route.FleetGroup : aircraft.FleetGroup, "Type"));

        return new Border
        {
            Background = Surface2Brush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            Child = strip,
        };
    }

    private static StackPanel BuildStat(string value, string label) => new()
    {
        Spacing = 1,
        Children =
        {
            new TextBlock { Text = value, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = TextPrimaryBrush },
            new TextBlock { Text = label, FontSize = 10, Foreground = TextMutedBrush },
        },
    };

    // ── Aircraft type (fixed per route, not user-selectable) + subsidiary sub-line -- matches
    //    the original's read-only "form-input" div + "Flying for X" line exactly. ────────────────
    private static StackPanel BuildAircraftTypeField(
        AviatesRoute route, IReadOnlyList<RouteAircraft> options, RouteAircraft selected, string? pilotSubsidiary,
        Action<RouteAircraft> onChanged)
    {
        var flyingFor = new Microsoft.UI.Xaml.Documents.Run { Text = Subsidiary.Name(selected.Type), Foreground = AccentBrush, FontWeight = Microsoft.UI.Text.FontWeights.Bold };
        var sub = new TextBlock { FontSize = 11.5, Foreground = TextMutedBrush, TextWrapping = TextWrapping.Wrap };
        sub.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "Flying for " });
        sub.Inlines.Add(flyingFor);

        FrameworkElement box;
        if (options.Count > 1)
        {
            // Aircraft flown by another subsidiary are listed (so the pilot can see the route
            // offers them) but can't be picked; the server refuses them anyway.
            var combo = new ComboBox { Width = FieldWidth };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(combo, "Aircraft type");
            foreach (var a in options)
            {
                var other = !string.IsNullOrEmpty(pilotSubsidiary) &&
                            !string.Equals(a.FleetGroup, pilotSubsidiary, StringComparison.OrdinalIgnoreCase);
                var label = a.Type
                    + (string.IsNullOrEmpty(a.FlightNumber) ? "" : $"  ·  {a.FlightNumber}")
                    + (string.IsNullOrEmpty(a.Frequency) ? "" : $"  ·  {a.Frequency}")
                    + (other ? $"  ({a.FleetGroup} only)" : "");
                combo.Items.Add(new ComboBoxItem { Content = label, Tag = a, IsEnabled = !other });
            }
            combo.SelectedIndex = options.ToList().IndexOf(selected);
            combo.SelectionChanged += (_, _) =>
            {
                if ((combo.SelectedItem as ComboBoxItem)?.Tag is not RouteAircraft chosen) return;
                flyingFor.Text = Subsidiary.Name(chosen.Type);
                onChanged(chosen);
            };
            box = combo;
        }
        else
        {
            box = new Border
            {
                Background = Surface2Brush,
                BorderBrush = BorderBrush_,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 9, 12, 9),
                Child = new TextBlock { Text = selected.Type, FontFamily = new FontFamily("Consolas"), FontSize = 13, Foreground = TextPrimaryBrush },
            };
        }

        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "AIRCRAFT TYPE", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, CharacterSpacing = 40, Foreground = TextSecondaryBrush },
                box,
                sub,
            },
        };
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

    // Matches LoadRegistrationsAsync's exact fallback chain: prefer the first Status=="active"
    // registration, then the first registration overall, then empty (no selection) if the list
    // itself is empty.
    private static async System.Threading.Tasks.Task LoadRegistrationsAsync(
        RoutesViewModel viewModel, string aircraftType, ComboBox combo, StackPanel loading, StackPanel none, ContentDialog dialog)
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

        dialog.IsPrimaryButtonEnabled = true;
    }

    // ── Success toast: a Popup anchored to the same XamlRoot the ContentDialog used, so it stays
    //    visible after the dialog closes. Manually dismissed after 5s via DispatcherTimer, matching
    //    the original's HideToastAfterDelay's exact Task.Delay(5000). Positioned bottom-right,
    //    matching .booking-success-toast's `position:fixed; bottom:28px; right:28px`. ────────────
    private static void ShowSuccessToast(XamlRoot xamlRoot, string callsign)
    {
        const double toastWidth = 340;

        var titleText = new TextBlock { Text = "Booking confirmed!", FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = TextPrimaryBrush };

        var subText = new TextBlock { FontSize = 12, Foreground = TextMutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        subText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "Callsign " });
        subText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = callsign, FontFamily = new FontFamily("Consolas"), FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = AccentBrush });
        subText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = ". Check your booking in Pilot Hub." });

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

        var popup = new Popup
        {
            XamlRoot = xamlRoot,
            Child = card,
            IsLightDismissEnabled = false,
        };

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
