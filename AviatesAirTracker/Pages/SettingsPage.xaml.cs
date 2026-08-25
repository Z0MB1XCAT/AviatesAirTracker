using AviatesAirTracker.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Linq;
using Windows.UI;

namespace AviatesAirTracker.Pages;

public sealed partial class SettingsPage : Page
{
    // Dark-theme hex values read directly from wwwroot/css/app.css's html[data-theme="dark"]
    // block (surface #0C111D, surface-2 #121A2A, border rgba(255,255,255,0.08) = #14FFFFFF,
    // text-secondary #CBD5E1, text-muted #64748B, accent #4D9BEC, accent-light
    // rgba(77,155,236,0.12) = #1F4D9BEC) -- same values every prior native page in this migration
    // hardcodes (RoutesPage/FleetPage/EventsPage).
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC));
    private static readonly Brush AccentLightBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0x4D, 0x9B, 0xEC));
    private static readonly Brush HairlineBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Surface2Brush = new SolidColorBrush(Color.FromArgb(0xFF, 0x12, 0x1A, 0x2A));
    private static readonly Brush TextMutedBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x64, 0x74, 0x8B));
    private static readonly Brush TextSecondaryBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1));

    // Status banner success/error variants -- same hex values and technique EventsPage.xaml.cs's
    // own UpdateStatusBanner already established (red #EF4444, accent #4D9BEC).
    private static readonly Brush StatusAccentBg = new SolidColorBrush(Color.FromArgb(0x1F, 0x4D, 0x9B, 0xEC));
    private static readonly Brush StatusAccentBorder = new SolidColorBrush(Color.FromArgb(0x4D, 0x4D, 0x9B, 0xEC));
    private static readonly Brush StatusAccentFg = new SolidColorBrush(Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC));
    private static readonly Brush StatusErrorBg = new SolidColorBrush(Color.FromArgb(0x1A, 0xEF, 0x44, 0x44));
    private static readonly Brush StatusErrorBorder = new SolidColorBrush(Color.FromArgb(0x4D, 0xEF, 0x44, 0x44));
    private static readonly Brush StatusErrorFg = new SolidColorBrush(Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));

    private readonly SettingsService _settings;

    // Page-local UI state, same placement decision every prior page has made for its own
    // _activeTab/_statusMessage-style fields (Settings.razor keeps these on the component too,
    // not on SettingsService, since they're purely display state, not persisted settings).
    private string? _statusMessage;
    private bool _isError;

    private string _displayName = "Pilot";
    private string _initials = "?";

    // _isVerified/_verifiedEmail: a "looks previously synced" heuristic, not a live verification
    // (see Loaded handler below) -- matches Settings.razor's OnInitialized() exactly. Only Task
    // 2's real ACARS sync flow can set _verifiedEmail to a non-empty value.
    private bool _isVerified;
    private string _verifiedEmail = "";

    public SettingsPage()
    {
        InitializeComponent();

        _settings = App.GetService<SettingsService>();

        // Initial field values. Setting .Text here also fires each TextChanged handler once,
        // which harmlessly re-writes the same value back to Settings -- not worth guarding
        // against since none of the three handlers below have a destructive side effect.
        PilotNameTextBox.Text = _settings.Settings.PilotName;
        PilotIdTextBox.Text = _settings.Settings.PilotId;
        SimBriefUsernameTextBox.Text = _settings.Settings.SimBriefUsername;

        UpdateStatusBanner();
        UpdateNameDisplay();
        UpdateThemeCardVisuals();
        UpdatePilotProfileVerifiedVisuals();
        UpdateSimBriefHintVisibility();

        Loaded += (_, _) =>
        {
            // Matches Settings.razor's OnInitialized() exactly: a previously-saved ACARS key +
            // pilot name together mean "looks synced" -- this is a heuristic flag, not a fresh
            // re-verification against the backend. _verifiedEmail always starts empty here; only
            // Task 2's SyncProfileAsync() (a real sync) can populate it.
            _isVerified = !string.IsNullOrEmpty(_settings.Settings.AcarsKey) &&
                          !string.IsNullOrEmpty(_settings.Settings.PilotName);
            _verifiedEmail = "";

            UpdateNameDisplay();
            UpdatePilotProfileVerifiedVisuals();
            UpdateSimBriefHintVisibility();
        };
    }

    // ── Pilot Profile ────────────────────────────────────────────────────────────────────────

    private void OnPilotNameTextChanged(object sender, TextChangedEventArgs e)
    {
        _settings.Settings.PilotName = PilotNameTextBox.Text;
        UpdateNameDisplay();
    }

    private void OnPilotIdTextChanged(object sender, TextChangedEventArgs e)
    {
        _settings.Settings.PilotId = PilotIdTextBox.Text;
        // The subtitle line ("{PilotId} · AviatesAir") reads PilotId live, so it needs a refresh
        // on every keystroke here too, not just at load.
        UpdatePilotProfileVerifiedVisuals();
    }

    // Recomputes _displayName ("Pilot" if blank) and _initials (first letter of up to the first 2
    // space-separated words, uppercased, "?" if blank) from Settings.PilotName, then pushes both
    // into the avatar/name TextBlocks. Ported verbatim from Settings.razor's own
    // UpdateNameDisplay(). Kept as a distinctly-named, parameterless, private method (not inlined
    // into OnPilotNameTextChanged) because Task 2's ACARS sync flow and Task 6's Reset handler
    // both call this exact method again later.
    private void UpdateNameDisplay()
    {
        var name = _settings.Settings.PilotName ?? "";
        _displayName = string.IsNullOrWhiteSpace(name) ? "Pilot" : name;
        _initials = string.IsNullOrWhiteSpace(name)
            ? "?"
            : string.Concat(
                name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Take(2)
                    .Select(w => w[0]))
              .ToUpper();

        DisplayNameText.Text = _displayName;
        AvatarInitialsText.Text = _initials;
    }

    // Refreshes everything gated on _isVerified/_verifiedEmail/PilotId: the checkmark badge, the
    // VERIFIED chip, the "{PilotId} · AviatesAir" subtitle (PilotId can change independently via
    // OnPilotIdTextChanged), and the verified-email row.
    private void UpdatePilotProfileVerifiedVisuals()
    {
        VerifiedBadgeBorder.Visibility = _isVerified ? Visibility.Visible : Visibility.Collapsed;
        VerifiedChipBorder.Visibility = _isVerified ? Visibility.Visible : Visibility.Collapsed;

        var pilotId = _settings.Settings.PilotId;
        PilotIdSubtitleText.Text = (string.IsNullOrEmpty(pilotId) ? "AviatesAir" : pilotId) + " · AviatesAir";

        var showEmail = _isVerified && !string.IsNullOrEmpty(_verifiedEmail);
        VerifiedEmailRow.Visibility = showEmail ? Visibility.Visible : Visibility.Collapsed;
        VerifiedEmailText.Text = _verifiedEmail;
    }

    // ── Appearance ───────────────────────────────────────────────────────────────────────────

    private void OnThemeCardTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border { Tag: string theme }) return;

        // Per the design spec's explicit scope decision: this persists Settings.Theme and
        // refreshes only these three cards' own active-state visuals. It does NOT re-theme the
        // running native shell -- every native page in this migration hardcodes its own
        // dark-theme hex values rather than using dynamic ThemeResource lookups, so live
        // re-theming would mean reworking every previously-built page, far beyond this page's
        // own scope.
        _settings.Settings.Theme = theme;
        _settings.Save();
        UpdateThemeCardVisuals();
    }

    private void UpdateThemeCardVisuals()
    {
        ApplyThemeCardVisual(ThemeSystemBorder, ThemeSystemIconGrid, ThemeSystemText, "system");
        ApplyThemeCardVisual(ThemeLightBorder, ThemeLightIconGrid, ThemeLightText, "light");
        ApplyThemeCardVisual(ThemeDarkBorder, ThemeDarkIconGrid, ThemeDarkText, "dark");
    }

    private void ApplyThemeCardVisual(Border cardBorder, Grid iconGrid, TextBlock label, string theme)
    {
        var isActive = _settings.Settings.Theme == theme;

        cardBorder.BorderBrush = isActive ? AccentBrush : HairlineBrush;
        cardBorder.Background = isActive ? AccentLightBrush : Surface2Brush;

        SetIconStroke(iconGrid, isActive ? AccentBrush : TextMutedBrush);

        label.Foreground = isActive ? AccentBrush : TextSecondaryBrush;
        label.FontWeight = isActive ? FontWeights.Bold : FontWeights.SemiBold;
    }

    // Every child of a theme-icon Grid is a Shape (Rectangle/Line/Ellipse/Path) whose Stroke
    // reproduces the original SVG's stroke="currentColor" -- walking the Grid's children and
    // setting Stroke on each avoids naming every individual shape element just to retint it.
    private static void SetIconStroke(Grid iconGrid, Brush stroke)
    {
        foreach (var child in iconGrid.Children)
        {
            if (child is Shape shape)
                shape.Stroke = stroke;
        }
    }

    // ── SimBrief Integration ─────────────────────────────────────────────────────────────────

    private void OnSimBriefUsernameTextChanged(object sender, TextChangedEventArgs e)
    {
        _settings.Settings.SimBriefUsername = SimBriefUsernameTextBox.Text;
        UpdateSimBriefHintVisibility();
    }

    // Reads live state (_isVerified, the current textbox value), not a value cached only at
    // page-load -- Task 2's ACARS sync flow can flip _isVerified and populate SimBriefUsername
    // well after this page has already loaded, and this hint needs to appear the moment that
    // happens.
    private void UpdateSimBriefHintVisibility()
    {
        var show = _isVerified && !string.IsNullOrEmpty(_settings.Settings.SimBriefUsername);
        SimBriefSyncedHintText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Status banner ────────────────────────────────────────────────────────────────────────
    // Same dismissible success/error pattern EventsPage.xaml.cs's own status banner already
    // established -- no Settings-specific behavior calls this yet in Task 1 (nothing raises a
    // status message), but the banner and its close handler are wired up now since later tasks
    // (ACARS sync, Save/Reset) both need it.

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
}
