using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
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

    // ACARS Configuration's Verify & Sync button: "verified" visual state matches .btn-secondary
    // (background var(--surface) #0C111D, border var(--border) #14FFFFFF, text var(--text-primary)
    // #F1F5F9) -- reuses HairlineBrush above for the border, adds the two brushes .btn-secondary
    // needs that nothing else on this page has defined yet.
    private static readonly Brush SurfaceBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x0C, 0x11, 0x1D));
    private static readonly Brush TextPrimaryBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9));
    private static readonly Brush WhiteBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

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

    // Task 2: ACARS sync in-flight flag -- page-local like every other UI-state field here, not
    // persisted to Settings.
    private bool _isSyncing;

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
        AcarsKeyPasswordBox.Password = _settings.Settings.AcarsKey;
        SelectAutoPirepIntervalItem(_settings.Settings.AutoPirepIntervalMinutes);

        UpdateStatusBanner();
        UpdateNameDisplay();
        UpdateThemeCardVisuals();
        UpdatePilotProfileVerifiedVisuals();
        UpdateSimBriefHintVisibility();
        UpdateAcarsInfoBanner();
        UpdateVerifySyncButtonState();
        UpdateFriendCodePanel();

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
            UpdateAcarsInfoBanner();
            UpdateVerifySyncButtonState();
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

    // ── ACARS Configuration ──────────────────────────────────────────────────────────────────

    private void OnAcarsKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        _settings.Settings.AcarsKey = AcarsKeyPasswordBox.Password;

        // Key changed manually -- clear verified state until they re-sync (matches
        // Settings.razor's OnAcarsKeyInput exact side effect).
        _isVerified = false;
        _verifiedEmail = "";

        UpdatePilotProfileVerifiedVisuals();
        UpdateSimBriefHintVisibility();
        UpdateAcarsInfoBanner();
        UpdateVerifySyncButtonState();
        // Settings.razor recomputes the Friend Code panel on every render (Blazor re-renders after
        // every @oninput), so the empty-key prompt flips to a live-computed code the moment any
        // character is typed -- ported here for fidelity, not just after a real sync.
        UpdateFriendCodePanel();
    }

    private void UpdateAcarsInfoBanner()
    {
        AcarsVerifiedInfoBanner.Visibility = _isVerified ? Visibility.Visible : Visibility.Collapsed;
        AcarsUnverifiedInfoBanner.Visibility = _isVerified ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnVerifySyncTapped(object sender, TappedRoutedEventArgs e)
    {
        // Defensive guard matching the original's disabled condition -- the button is already
        // dimmed/hit-test-disabled below, but guard here too in case Tapped still fires.
        if (_isSyncing || string.IsNullOrWhiteSpace(_settings.Settings.AcarsKey)) return;

        await SyncProfileAsync();
    }

    // Ported from Settings.razor's SyncProfileAsync() -- trims the key, validates against the
    // backend, and on success populates PilotName/SimBriefUsername/PilotId, regenerates the
    // Friend Code, and flips _isVerified/_verifiedEmail. On failure or network exception, clears
    // verified state and shows the appropriate status message (both exact original error strings).
    private async Task SyncProfileAsync()
    {
        var key = (_settings.Settings.AcarsKey ?? "").Trim();
        if (string.IsNullOrEmpty(key)) return;

        // Write the trimmed key back so it's clean for future saves, and keep the password field
        // in sync with the trimmed value.
        _settings.Settings.AcarsKey = key;
        AcarsKeyPasswordBox.Password = key;

        _isSyncing = true;
        _statusMessage = null;
        UpdateVerifySyncButtonState();
        UpdateStatusBanner();

        var backendClient = App.GetService<AviatesBackendClient>();

        try
        {
            var result = await backendClient.ValidateAcarsKeyAsync(key);

            if (result is { IsValid: true })
            {
                // Always populate name from the backend -- it is authoritative.
                if (!string.IsNullOrWhiteSpace(result.Name))
                {
                    _settings.Settings.PilotName = result.Name;
                    PilotNameTextBox.Text = result.Name;
                    UpdateNameDisplay();
                }

                // Only populate SimBrief if the backend has a value.
                if (!string.IsNullOrWhiteSpace(result.SimBrief))
                {
                    _settings.Settings.SimBriefUsername = result.SimBrief;
                    SimBriefUsernameTextBox.Text = result.SimBrief;
                }

                // Populate PilotId from backend; derive from ACARS key if server didn't return one.
                // A stable PilotId is required for the messaging system to route and display
                // messages correctly.
                if (!string.IsNullOrWhiteSpace(result.PilotId))
                {
                    _settings.Settings.PilotId = result.PilotId;
                    PilotIdTextBox.Text = result.PilotId;
                }
                else if (string.IsNullOrEmpty(_settings.Settings.PilotId))
                {
                    // Derive a deterministic ID from the ACARS key so every pilot has one -- ported
                    // verbatim from Settings.razor's own derivation.
                    var hash = System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes("AVIATES_PID_" + key));
                    _settings.Settings.PilotId = "avt_" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
                    PilotIdTextBox.Text = _settings.Settings.PilotId;
                }

                _isVerified = true;
                _verifiedEmail = result.Email;

                // Regenerate friend code and persist.
                _settings.RefreshFriendCode();

                _statusMessage = $"Profile synced — welcome, {result.Name}!";
                _isError = false;
            }
            else
            {
                _isVerified = false;
                _verifiedEmail = "";
                _statusMessage = "ACARS key not recognised. Check the key and try again.";
                _isError = true;
            }
        }
        catch (Exception ex)
        {
            _isVerified = false;
            _verifiedEmail = "";
            _statusMessage = $"Could not reach AviatesAir servers — {ex.Message}";
            _isError = true;
        }
        finally
        {
            _isSyncing = false;
            UpdateVerifySyncButtonState();
            UpdatePilotProfileVerifiedVisuals();
            UpdateSimBriefHintVisibility();
            UpdateAcarsInfoBanner();
            UpdateFriendCodePanel();
            UpdateStatusBanner();
        }
    }

    // Refreshes the button's disabled/dimmed state (matches the original's exact
    // `_isSyncing || string.IsNullOrWhiteSpace(AcarsKey)` condition), its background/border/text
    // color (btn-primary while unverified, btn-secondary once verified -- driven only by
    // _isVerified, same as the original's class expression, independent of _isSyncing), and which
    // of the three icon/spinner/text combinations is showing (syncing beats verified beats idle,
    // matching the original's @if/@else-if chain).
    private void UpdateVerifySyncButtonState()
    {
        var hasKey = !string.IsNullOrWhiteSpace(_settings.Settings.AcarsKey);
        var enabled = !_isSyncing && hasKey;
        VerifySyncButtonBorder.IsHitTestVisible = enabled;
        VerifySyncButtonBorder.Opacity = enabled ? 1.0 : 0.45;

        VerifySyncButtonBorder.Background = _isVerified ? SurfaceBrush : AccentBrush;
        VerifySyncButtonBorder.BorderBrush = _isVerified ? HairlineBrush : null;
        VerifySyncButtonBorder.BorderThickness = _isVerified ? new Thickness(1) : new Thickness(0);

        VerifySyncIdleIconViewbox.Visibility = !_isSyncing && !_isVerified ? Visibility.Visible : Visibility.Collapsed;
        VerifySyncVerifiedIconViewbox.Visibility = !_isSyncing && _isVerified ? Visibility.Visible : Visibility.Collapsed;
        VerifySyncProgressRing.Visibility = _isSyncing ? Visibility.Visible : Visibility.Collapsed;
        VerifySyncProgressRing.IsActive = _isSyncing;

        VerifySyncButtonText.Text = _isSyncing ? "Syncing…" : (_isVerified ? "Synced" : "Verify & Sync");
        VerifySyncButtonText.Foreground = _isVerified ? TextPrimaryBrush : WhiteBrush;
    }

    private void OnAutoPirepIntervalSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AutoPirepIntervalComboBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            int.TryParse(tag, out var minutes))
        {
            _settings.Settings.AutoPirepIntervalMinutes = minutes;
        }
    }

    private void SelectAutoPirepIntervalItem(int minutes)
    {
        foreach (var obj in AutoPirepIntervalComboBox.Items)
        {
            if (obj is ComboBoxItem { Tag: string tag } item &&
                int.TryParse(tag, out var value) && value == minutes)
            {
                AutoPirepIntervalComboBox.SelectedItem = item;
                return;
            }
        }

        // No exact match (a hand-edited settings file, say) -- fall back to "Off" rather than
        // leaving the ComboBox with nothing selected.
        AutoPirepIntervalComboBox.SelectedIndex = 0;
    }

    // ── Friend Code ──────────────────────────────────────────────────────────────────────────

    // Matches Settings.razor's exact fallback: use the stored FriendCode if present, else compute
    // one live from the current AcarsKey (GenerateFriendCode is deterministic/static, so this is
    // safe to call on every refresh without persisting anything).
    private void UpdateFriendCodePanel()
    {
        var hasKey = !string.IsNullOrEmpty(_settings.Settings.AcarsKey);
        FriendCodeEmptyBanner.Visibility = hasKey ? Visibility.Collapsed : Visibility.Visible;
        FriendCodeDisplayGrid.Visibility = hasKey ? Visibility.Visible : Visibility.Collapsed;

        if (hasKey)
        {
            FriendCodeText.Text = string.IsNullOrEmpty(_settings.Settings.FriendCode)
                ? SettingsService.GenerateFriendCode(_settings.Settings.AcarsKey)
                : _settings.Settings.FriendCode;
        }
    }

    private async void OnCopyFriendCodeTapped(object sender, TappedRoutedEventArgs e)
    {
        var code = FriendCodeText.Text;
        if (string.IsNullOrEmpty(code)) return;

        // Native clipboard API -- this page has no WebView, so there is no JS interop equivalent
        // to fall back to.
        var package = new DataPackage();
        package.SetText(code);
        Clipboard.SetContent(package);

        CopyIdleIconViewbox.Visibility = Visibility.Collapsed;
        CopyDoneIconViewbox.Visibility = Visibility.Visible;
        CopyFriendCodeButtonText.Text = "Copied!";

        await Task.Delay(2000);

        CopyIdleIconViewbox.Visibility = Visibility.Visible;
        CopyDoneIconViewbox.Visibility = Visibility.Collapsed;
        CopyFriendCodeButtonText.Text = "Copy";
    }

    // ── Status banner ────────────────────────────────────────────────────────────────────────
    // Same dismissible success/error pattern EventsPage.xaml.cs's own status banner already
    // established. Task 2's SyncProfileAsync() above is the first behavior to actually raise a
    // status message; Task 6's Save/Reset row will be the next.

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
