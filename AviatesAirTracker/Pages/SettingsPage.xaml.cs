using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Core.Efb;
using AviatesAirTracker.Services;
using AviatesAirTracker.Services.Efb;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Serilog;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Enumeration;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

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

    // Task 5: EFB Companion's QR module grid -- rendered as plain black/white Rectangle cells
    // (see EnsureEfbQrBuilt below), not an SVG string (WinUI has no inline-SVG renderer).
    private static readonly Brush BlackBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x00, 0x00));

    // Task 5: About & Updates' download-progress fill Border's Width is set in code-behind as a
    // pixel fraction of this constant -- must match UpdateProgressTrack's fixed Width="380" in
    // the XAML, same technique PilotHubPage.xaml's RankProgressTrack/RankProgressFill pair
    // already established (RankProgressTrackWidth there).
    private const double UpdateProgressTrackWidth = 380.0;

    private readonly SettingsService _settings;
    private readonly PttInputService _pttInput;
    private readonly UpdateService _updateService;
    private readonly EfbServer _efbServer;

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

    // Task 4: Dispatch Radio's PTT hotkey/joystick capture state -- page-local UI state, same
    // placement decision as every other field above (Settings.razor keeps these on the component
    // too, not on SettingsService).
    private bool _capturingHotkey;
    private string? _hotkeyRejectedMessage;
    private bool _capturingJoystick;

    // Task 5: About & Updates + EFB Companion -- page-local UI state, same placement decision as
    // every other field above (Settings.razor keeps these on the component too, not on
    // SettingsService).
    private string _appVersion = "1.0.0";
    private bool _isCheckingUpdate;
    private bool _updateChecked;
    private bool _isInstallingUpdate;
    private int _updateDlProgress;

    private bool _efbBusy;

    // Cache key for the built QR module grid -- rebuilt only when EfbServer.PrimaryUrl actually
    // changes (port/token can change across a stop/start cycle), matching Settings.razor's own
    // _efbQrCache/_efbQrForUrl caching.
    private string? _efbQrBuiltForUrl;

    public SettingsPage()
    {
        InitializeComponent();

        _settings = App.GetService<SettingsService>();
        _pttInput = App.GetService<PttInputService>();
        _updateService = App.GetService<UpdateService>();
        _efbServer = App.GetService<EfbServer>();

        // Initial field values. Setting .Text here also fires each TextChanged handler once,
        // which harmlessly re-writes the same value back to Settings -- not worth guarding
        // against since none of the three handlers below have a destructive side effect.
        PilotNameTextBox.Text = _settings.Settings.PilotName;
        PilotIdTextBox.Text = _settings.Settings.PilotId;
        SimBriefUsernameTextBox.Text = _settings.Settings.SimBriefUsername;
        AcarsKeyPasswordBox.Password = _settings.Settings.AcarsKey;
        SelectAutoPirepIntervalItem(_settings.Settings.AutoPirepIntervalMinutes);

        // Task 3: Hoppies ACARS + App Preferences initial field values. Setting each
        // ToggleSwitch.IsOn here can harmlessly re-fire its own Toggled handler (same "harmless
        // re-write" reasoning as the TextBox initial values above) -- UpdateHoppiesCategoryEnabledState()
        // is called unconditionally afterward regardless, so the category toggles' enabled/opacity
        // state ends up correct either way.
        HoppiesPilotCodeBox.Password = _settings.Settings.HoppiesPilotCode;
        HoppiesEnabledToggle.IsOn = _settings.Settings.HoppiesEnabled;
        HoppiesLoadsheetToggle.IsOn = _settings.Settings.HoppiesSendLoadsheet;
        HoppiesTimingRemindersToggle.IsOn = _settings.Settings.HoppiesSendTimingReminders;
        HoppiesAirborneLandingToggle.IsOn = _settings.Settings.HoppiesSendAirborneLandingCalls;
        HoppiesPostFlightSummaryToggle.IsOn = _settings.Settings.HoppiesSendPostFlightSummary;
        HoppiesWeatherAdvisoriesToggle.IsOn = _settings.Settings.HoppiesSendWeatherAdvisories;
        HoppiesHazardAdvisoriesToggle.IsOn = _settings.Settings.HoppiesSendHazardAdvisories;
        UpdateHoppiesCategoryEnabledState();

        AutoConnectToggle.IsOn = _settings.Settings.AutoConnectSimConnect;
        ShowApproachAlertsToggle.IsOn = _settings.Settings.ShowApproachAlerts;
        MinimizeToTrayToggle.IsOn = _settings.Settings.MinimizeToTray;
        ShareLivePositionToggle.IsOn = _settings.Settings.ShareLivePosition;

        // Task 4: Dispatch Radio initial field values. The joystick list is populated once here
        // (GetConnectedJoysticks() is synchronous, plain C#) -- matching Settings.razor's
        // OnInitialized(), which also only loads this list once, not on every render. Audio
        // devices are enumerated asynchronously below (Loaded), since DeviceInformation.FindAllAsync
        // is a WinRT async call and the constructor can't await.
        DispatchBackendUrlTextBox.Text = _settings.Settings.DispatchBackendUrl;
        PopulateJoystickComboBox();

        // Task 5: About & Updates + EFB Companion initial field values.
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is not null)
            _appVersion = $"{version.Major}.{version.Minor}.{version.Build}";
        AppVersionText.Text = "v" + _appVersion;

        EfbEnabledToggle.IsOn = _settings.Settings.EfbEnabled;
        EfbAutoStartToggle.IsOn = _settings.Settings.EfbAutoStart;

        UpdateStatusBanner();
        UpdateNameDisplay();
        UpdateThemeCardVisuals();
        UpdatePilotProfileVerifiedVisuals();
        UpdateSimBriefHintVisibility();
        UpdateAcarsInfoBanner();
        UpdateVerifySyncButtonState();
        UpdateFriendCodePanel();
        UpdatePttHotkeyDisplay();
        UpdateJoystickBoundText();
        UpdateBindJoystickButtonState();
        UpdateCheckForUpdatesButtonState();
        UpdateUpdateAvailablePanel();
        UpdateEfbPanel();

        Loaded += (_, _) =>
        {
            // Task 5: UpdateService.DownloadProgressChanged/EfbServer.StatusChanged may not fire
            // on the UI thread -- same Loaded/Unloaded subscribe-unsubscribe shape
            // MessagesPage.xaml.cs established for MessagingService's events, with every mutation
            // marshaled via DispatcherQueue.TryEnqueue.
            _updateService.DownloadProgressChanged += OnUpdateProgress;
            _efbServer.StatusChanged += OnEfbStatusChanged;
        };

        Unloaded += (_, _) =>
        {
            _updateService.DownloadProgressChanged -= OnUpdateProgress;
            _efbServer.StatusChanged -= OnEfbStatusChanged;
        };

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

        // Per the design spec's explicit scope decision: enumerate REAL native Windows audio
        // devices via Windows.Devices.Enumeration.DeviceInformation.FindAllAsync -- there is no JS
        // runtime on this page to call "aviatesVoice.getAudioDevices" into, unlike Settings.razor.
        Loaded += async (_, _) => await LoadAudioDevicesAsync();
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

    // ── Dispatch Radio ───────────────────────────────────────────────────────────────────────

    private void OnDispatchBackendUrlTextChanged(object sender, TextChangedEventArgs e)
    {
        // Plain write-through, no side effect -- matches Settings.razor's plain @bind on this field.
        _settings.Settings.DispatchBackendUrl = DispatchBackendUrlTextBox.Text;
    }

    // ── PTT Hotkey ──

    private void OnPttHotkeyGotFocus(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        _hotkeyRejectedMessage = null;
        UpdatePttHotkeyDisplay();
    }

    private void OnPttHotkeyLostFocus(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = false;
        UpdatePttHotkeyDisplay();
    }

    // Reads the pressed key while capturing, translates it to the DOM "code" string PttKeyMap
    // expects (see ResolvePttDomCode below), and either rejects it or saves the binding. Ported
    // from Settings.razor's OnCaptureHotkey.
    private void OnPttHotkeyTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.Handled = true;

        var domCode = ResolvePttDomCode(e.Key);
        var vk = domCode != null ? PttKeyMap.ToVirtualKey(domCode) : null;

        if (vk is null)
        {
            _hotkeyRejectedMessage = $"\"{PttKeyDisplayName(e.Key)}\" can't be used as a PTT hotkey — try a different key.";
            _capturingHotkey = false;
        }
        else
        {
            _hotkeyRejectedMessage = null;
            _settings.Settings.PttHotkeyVkCode = vk.Value;
            _settings.Settings.PttHotkeyLabel = PttKeyMap.Describe(domCode!);
            _settings.Save();
            _capturingHotkey = false;
        }

        UpdatePttHotkeyDisplay();
    }

    private void OnClearHotkeyTapped(object sender, TappedRoutedEventArgs e)
    {
        _settings.Settings.PttHotkeyVkCode = 0;
        _settings.Settings.PttHotkeyLabel = "";
        _settings.Save();
        _hotkeyRejectedMessage = null;
        UpdatePttHotkeyDisplay();
    }

    private void UpdatePttHotkeyDisplay()
    {
        PttHotkeyTextBox.Text = _capturingHotkey
            ? "Press a key…"
            : (_settings.Settings.PttHotkeyVkCode == 0
                ? "Unbound — click and press a key"
                : _settings.Settings.PttHotkeyLabel);

        var hasRejection = !string.IsNullOrEmpty(_hotkeyRejectedMessage);
        PttHotkeyRejectionText.Text = _hotkeyRejectedMessage ?? "";
        PttHotkeyRejectionText.Visibility = hasRejection ? Visibility.Visible : Visibility.Collapsed;
        PttHotkeyHintText.Visibility = hasRejection ? Visibility.Collapsed : Visibility.Visible;
    }

    // Translates a native WinUI VirtualKey (from a KeyDown event) into the DOM "code" string
    // PttKeyMap.cs expects -- that class was written for BlazorWebView's browser
    // KeyboardEventArgs.Code, and this page has no browser to source that string from anymore.
    // Windows.System.VirtualKey's underlying int values already equal the same Win32 VK_* codes
    // PttKeyMap.Map stores (confirmed by reading PttKeyMap.cs directly), so this is a syntactic
    // translation only -- no new vk-code table is introduced, PttKeyMap.ToVirtualKey/Describe
    // remain the single source of truth for which keys are bindable and how they're labeled.
    //
    // Ctrl/Shift/Alt need special handling: a native KeyDown's e.Key reports the GENERIC
    // VirtualKey (Control/Shift/Menu) for either physical side, not the left/right-specific one
    // PttKeyMap.Map distinguishes. The left/right distinction requires probing
    // InputKeyboardSource for current key state -- the same technique MessagesPage.xaml.cs
    // already uses (OnComposeKeyDown) to check whether Shift is held during Enter.
    private static string? ResolvePttDomCode(VirtualKey key)
    {
        if (key == VirtualKey.Control) return IsKeyDown(VirtualKey.RightControl) ? "ControlRight" : "ControlLeft";
        if (key == VirtualKey.Shift) return IsKeyDown(VirtualKey.RightShift) ? "ShiftRight" : "ShiftLeft";
        if (key == VirtualKey.Menu) return IsKeyDown(VirtualKey.RightMenu) ? "AltRight" : "AltLeft";

        if (key >= VirtualKey.F1 && key <= VirtualKey.F12) return "F" + (key - VirtualKey.F1 + 1);
        if (key >= VirtualKey.A && key <= VirtualKey.Z) return "Key" + key;
        if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9) return "Digit" + (key - VirtualKey.Number0);
        if (key >= VirtualKey.NumberPad0 && key <= VirtualKey.NumberPad9) return "Numpad" + (key - VirtualKey.NumberPad0);

        return key switch
        {
            VirtualKey.Space => "Space",
            VirtualKey.CapitalLock => "CapsLock",
            VirtualKey.Enter => "Enter",
            VirtualKey.Up => "ArrowUp",
            VirtualKey.Down => "ArrowDown",
            VirtualKey.Left => "ArrowLeft",
            VirtualKey.Right => "ArrowRight",
            VirtualKey.Insert => "Insert",
            VirtualKey.Delete => "Delete",
            VirtualKey.Home => "Home",
            VirtualKey.End => "End",
            VirtualKey.PageUp => "PageUp",
            VirtualKey.PageDown => "PageDown",
            // OEM/punctuation keys have no named VirtualKey members, but the enum's underlying
            // int values still equal the same Win32 VK_OEM_* codes PttKeyMap.Map stores, so a
            // direct cast-to-int comparison (via a constant-pattern case label) is correct here.
            (VirtualKey)0xBD => "Minus",        // VK_OEM_MINUS
            (VirtualKey)0xBB => "Equal",        // VK_OEM_PLUS
            (VirtualKey)0xDB => "BracketLeft",  // VK_OEM_4
            (VirtualKey)0xDD => "BracketRight", // VK_OEM_6
            (VirtualKey)0xDC => "Backslash",    // VK_OEM_5
            (VirtualKey)0xBA => "Semicolon",    // VK_OEM_1
            (VirtualKey)0xDE => "Quote",        // VK_OEM_7
            (VirtualKey)0xBC => "Comma",        // VK_OEM_COMMA
            (VirtualKey)0xBE => "Period",       // VK_OEM_PERIOD
            (VirtualKey)0xBF => "Slash",        // VK_OEM_2
            (VirtualKey)0xC0 => "Backquote",    // VK_OEM_3
            _ => null,
        };
    }

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    // Used only for the rejection message's key name -- Ctrl/Shift/Alt never reach here since
    // ResolvePttDomCode above always resolves them successfully.
    private static string PttKeyDisplayName(VirtualKey key) => key.ToString();

    // ── PTT Joystick Button ──

    // Populated once on load (GetConnectedJoysticks() is a plain synchronous C# call) -- matches
    // Settings.razor's OnInitialized(), which also doesn't live-refresh this list.
    private void PopulateJoystickComboBox()
    {
        JoystickComboBox.Items.Clear();
        JoystickComboBox.Items.Add(new ComboBoxItem { Content = "-- No joystick --", Tag = "" });

        var savedGuid = _settings.Settings.PttJoystickGuid;
        ComboBoxItem? matched = null;

        foreach (var (instanceGuid, productName) in _pttInput.GetConnectedJoysticks())
        {
            var item = new ComboBoxItem { Content = productName, Tag = instanceGuid.ToString() };
            JoystickComboBox.Items.Add(item);
            if (savedGuid != Guid.Empty && instanceGuid == savedGuid)
                matched = item;
        }

        JoystickComboBox.SelectedItem = matched ?? JoystickComboBox.Items[0];
    }

    private void OnJoystickComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateBindJoystickButtonState();
    }

    // Ported from Settings.razor's BindJoystickButtonAsync -- a 10-second-timeout capture on the
    // currently-selected joystick, saving Settings.PttJoystickGuid/PttJoystickButton on success.
    private async void OnBindJoystickButtonTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_capturingJoystick) return;
        if (JoystickComboBox.SelectedItem is not ComboBoxItem { Tag: string tag } || string.IsNullOrEmpty(tag)) return;
        if (!Guid.TryParse(tag, out var guid)) return;

        _capturingJoystick = true;
        BindJoystickButtonText.Text = "Press a button…";
        UpdateBindJoystickButtonState();

        try
        {
            using var captureCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var button = await _pttInput.CaptureNextJoystickButtonAsync(guid, captureCts.Token);

            if (button is not null)
            {
                _settings.Settings.PttJoystickGuid = guid;
                _settings.Settings.PttJoystickButton = button.Value;
                _settings.Save();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Settings] Joystick button capture failed");
        }
        finally
        {
            _capturingJoystick = false;
            BindJoystickButtonText.Text = "Bind Button";
            UpdateBindJoystickButtonState();
            UpdateJoystickBoundText();
        }
    }

    // Disabled when no joystick is selected in the dropdown OR a capture is already in flight --
    // matches the original's exact `disabled="@(string.IsNullOrEmpty(_selectedJoystickGuid) ||
    // _capturingJoystick)"` condition.
    private void UpdateBindJoystickButtonState()
    {
        var hasSelection = JoystickComboBox.SelectedItem is ComboBoxItem { Tag: string tag } && !string.IsNullOrEmpty(tag);
        var enabled = hasSelection && !_capturingJoystick;
        BindJoystickButtonBorder.IsHitTestVisible = enabled;
        BindJoystickButtonBorder.Opacity = enabled ? 1.0 : 0.45;
    }

    private void UpdateJoystickBoundText()
    {
        var button = _settings.Settings.PttJoystickButton;
        var bound = button >= 0;
        JoystickBoundText.Visibility = bound ? Visibility.Visible : Visibility.Collapsed;
        if (bound)
            JoystickBoundText.Text = $"Bound: button {button + 1}";
    }

    // ── Microphone / Speaker ──
    //
    // Per the design spec's explicit scope decision (#2): this enumerates REAL native Windows
    // audio devices via Windows.Devices.Enumeration.DeviceInformation.FindAllAsync -- there is no
    // JS runtime on this native page to call "aviatesVoice.getAudioDevices" into, unlike
    // Settings.razor. Selecting a device here only persists PttMicrophoneDeviceId/
    // PttSpeakerDeviceId to SettingsService -- it deliberately does NOT call anything shaped like
    // "aviatesVoice.setMicrophone"/"setSpeaker", because there is no native audio pipeline to
    // receive that call yet (the actual PTT voice pipeline only runs inside the still-Blazor
    // ACARS page today, regardless of which Settings UI wrote the device id).

    private async Task LoadAudioDevicesAsync()
    {
        try
        {
            var microphones = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
            var speakers = await DeviceInformation.FindAllAsync(DeviceClass.AudioRender);

            PopulateAudioDeviceComboBox(MicrophoneComboBox, microphones, _settings.Settings.PttMicrophoneDeviceId);
            PopulateAudioDeviceComboBox(SpeakerComboBox, speakers, _settings.Settings.PttSpeakerDeviceId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Settings] Failed to enumerate audio devices");
        }
    }

    private static void PopulateAudioDeviceComboBox(ComboBox comboBox, DeviceInformationCollection devices, string savedDeviceId)
    {
        comboBox.Items.Clear();
        comboBox.Items.Add(new ComboBoxItem { Content = "System default", Tag = "" });

        ComboBoxItem? matched = null;
        foreach (var device in devices)
        {
            var item = new ComboBoxItem { Content = device.Name, Tag = device.Id };
            comboBox.Items.Add(item);
            if (!string.IsNullOrEmpty(savedDeviceId) && device.Id == savedDeviceId)
                matched = item;
        }

        comboBox.SelectedItem = matched ?? comboBox.Items[0];
    }

    private void OnMicrophoneSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MicrophoneComboBox.SelectedItem is ComboBoxItem { Tag: string deviceId })
        {
            _settings.Settings.PttMicrophoneDeviceId = deviceId;
            _settings.Save();
        }
    }

    private void OnSpeakerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeakerComboBox.SelectedItem is ComboBoxItem { Tag: string deviceId })
        {
            _settings.Settings.PttSpeakerDeviceId = deviceId;
            _settings.Save();
        }
    }

    // ── Hoppies ACARS ────────────────────────────────────────────────────────────────────────

    private void OnHoppiesPilotCodeChanged(object sender, RoutedEventArgs e)
    {
        // Trimmed on write, matching Settings.razor's OnHoppiesPilotCodeInput exactly.
        _settings.Settings.HoppiesPilotCode = HoppiesPilotCodeBox.Password.Trim();
    }

    private void OnHoppiesEnabledToggled(object sender, RoutedEventArgs e)
    {
        // Saves immediately -- not deferred to the page's Save button, matching the original's
        // OnHoppiesEnabledChanged exactly.
        _settings.Settings.HoppiesEnabled = HoppiesEnabledToggle.IsOn;
        _settings.Save();
        UpdateHoppiesCategoryEnabledState();
    }

    // Shared handler for all 6 Hoppies category toggles (Loadsheet/Timing Reminders/
    // Airborne-Landing Calls/Post-Flight Summary/Weather Advisories/Hazard Advisories) -- each
    // ToggleSwitch's Tag names which Settings.HoppiesSend* property it drives. Matches the
    // original's OnCategoryToggle: setter(value); SettingsService.Save(); exactly, just
    // deduplicated into one method instead of 6 near-identical handlers.
    private void OnHoppiesCategoryToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: string category } toggle) return;

        var value = toggle.IsOn;
        switch (category)
        {
            case "Loadsheet": _settings.Settings.HoppiesSendLoadsheet = value; break;
            case "TimingReminders": _settings.Settings.HoppiesSendTimingReminders = value; break;
            case "AirborneLandingCalls": _settings.Settings.HoppiesSendAirborneLandingCalls = value; break;
            case "PostFlightSummary": _settings.Settings.HoppiesSendPostFlightSummary = value; break;
            case "WeatherAdvisories": _settings.Settings.HoppiesSendWeatherAdvisories = value; break;
            case "HazardAdvisories": _settings.Settings.HoppiesSendHazardAdvisories = value; break;
        }

        _settings.Save();
    }

    // Dims (Opacity 0.5, matching the original's CSS opacity transition) AND disables
    // (IsEnabled = false) the 6 category toggles together whenever the master Hoppies toggle is
    // off, and restores both when it's on. Called on load AND every time the master toggle
    // changes (OnHoppiesEnabledToggled above) -- not just at page load.
    private void UpdateHoppiesCategoryEnabledState()
    {
        var enabled = _settings.Settings.HoppiesEnabled;
        HoppiesCategoriesPanel.Opacity = enabled ? 1.0 : 0.5;

        HoppiesLoadsheetToggle.IsEnabled = enabled;
        HoppiesTimingRemindersToggle.IsEnabled = enabled;
        HoppiesAirborneLandingToggle.IsEnabled = enabled;
        HoppiesPostFlightSummaryToggle.IsEnabled = enabled;
        HoppiesWeatherAdvisoriesToggle.IsEnabled = enabled;
        HoppiesHazardAdvisoriesToggle.IsEnabled = enabled;
    }

    // ── App Preferences ──────────────────────────────────────────────────────────────────────
    // Auto-connect / Desktop Alerts / Minimize to Tray are plain deferred-to-Save binds -- no
    // SettingsService.Save() call here, matching the original's plain @bind with no side effect
    // (only Task 6's Save button persists these). Share My Live Position is the one toggle in
    // this panel that DOES save immediately and calls AviatesBackendClient.SetLiveSharingAsync,
    // matching OnShareLivePositionChanged's exact immediate-effect behavior.

    private void OnAutoConnectToggled(object sender, RoutedEventArgs e)
    {
        _settings.Settings.AutoConnectSimConnect = AutoConnectToggle.IsOn;
    }

    private void OnShowApproachAlertsToggled(object sender, RoutedEventArgs e)
    {
        _settings.Settings.ShowApproachAlerts = ShowApproachAlertsToggle.IsOn;
    }

    private void OnMinimizeToTrayToggled(object sender, RoutedEventArgs e)
    {
        _settings.Settings.MinimizeToTray = MinimizeToTrayToggle.IsOn;
    }

    private async void OnShareLivePositionToggled(object sender, RoutedEventArgs e)
    {
        var enabled = ShareLivePositionToggle.IsOn;
        _settings.Settings.ShareLivePosition = enabled;
        _settings.Save();

        var key = (_settings.Settings.AcarsKey ?? "").Trim();
        if (!string.IsNullOrEmpty(key))
        {
            var backendClient = App.GetService<AviatesBackendClient>();
            await backendClient.SetLiveSharingAsync(enabled, key);
        }
    }

    // ── About & Updates ──────────────────────────────────────────────────────────────────────

    private async void OnCheckForUpdatesTapped(object sender, TappedRoutedEventArgs e)
    {
        // Defensive guard matching the original's disabled condition -- the button is already
        // dimmed/hit-test-disabled below, but guard here too in case Tapped still fires.
        if (_isCheckingUpdate || _isInstallingUpdate) return;

        await CheckForUpdatesAsync();
    }

    // Ported from Settings.razor's CheckForUpdatesAsync() -- sets the checking flag, rebuilds,
    // awaits UpdateService.CheckAsync(), then marks the check complete and resets the checking
    // flag in a finally, rebuilding again.
    private async Task CheckForUpdatesAsync()
    {
        _isCheckingUpdate = true;
        _updateChecked = false;
        UpdateCheckForUpdatesButtonState();
        UpdateUpdateAvailablePanel();

        try
        {
            await _updateService.CheckAsync();
            _updateChecked = true;
        }
        finally
        {
            _isCheckingUpdate = false;
            UpdateCheckForUpdatesButtonState();
            UpdateUpdateAvailablePanel();
        }
    }

    private async void OnDownloadInstallTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_isInstallingUpdate) return;

        await InstallFromSettingsAsync();
    }

    // Ported from Settings.razor's InstallFromSettingsAsync() -- on success, DownloadAndInstallAsync
    // shuts the app down itself (see UpdateService.cs), so there is nothing further to do here. On
    // exception, this silently resets the installing flag/progress and rebuilds without showing an
    // error -- that matches the original's own catch block exactly, even though it looks
    // incomplete; ported verbatim rather than "fixed".
    private async Task InstallFromSettingsAsync()
    {
        _isInstallingUpdate = true;
        _updateDlProgress = 0;
        UpdateUpdateAvailablePanel();

        try
        {
            await _updateService.DownloadAndInstallAsync();
        }
        catch
        {
            _isInstallingUpdate = false;
            _updateDlProgress = 0;
            UpdateUpdateAvailablePanel();
        }
    }

    // UpdateService.DownloadProgressChanged fires from a background download loop (see
    // UpdateService.DownloadAndInstallAsync's while-read loop), not the UI thread -- marshaled via
    // DispatcherQueue.TryEnqueue, same pattern as Messages' live-event subscriptions.
    private void OnUpdateProgress(object? sender, int pct)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _updateDlProgress = pct;
            UpdateUpdateAvailablePanel();
        });
    }

    // Matches the original's exact `_isCheckingUpdate || _isInstallingUpdate` disabled condition
    // and the idle/checking icon-vs-spinner swap.
    private void UpdateCheckForUpdatesButtonState()
    {
        var enabled = !_isCheckingUpdate && !_isInstallingUpdate;
        CheckForUpdatesButtonBorder.IsHitTestVisible = enabled;
        CheckForUpdatesButtonBorder.Opacity = enabled ? 1.0 : 0.45;

        CheckForUpdatesIdleIconViewbox.Visibility = _isCheckingUpdate ? Visibility.Collapsed : Visibility.Visible;
        CheckForUpdatesProgressRing.Visibility = _isCheckingUpdate ? Visibility.Visible : Visibility.Collapsed;
        CheckForUpdatesProgressRing.IsActive = _isCheckingUpdate;
        CheckForUpdatesButtonText.Text = _isCheckingUpdate ? "Checking…" : "Check for Updates";
    }

    // Matches Settings.razor's exact 3-way @if (AvailableUpdate is { } update) / else if
    // (_updateChecked) / implicit-else-nothing. The download-progress bar's fill Width is a pixel
    // fraction of UpdateProgressTrackWidth, same technique PilotHubPage.xaml's
    // RankProgressTrack/RankProgressFill pair already established.
    private void UpdateUpdateAvailablePanel()
    {
        var update = _updateService.AvailableUpdate;

        if (update is not null)
        {
            UpdateAvailableBorder.Visibility = Visibility.Visible;
            UpToDateBanner.Visibility = Visibility.Collapsed;

            UpdateAvailableVersionText.Text = $"v{update.Version} is available";

            UpdateProgressPanel.Visibility = _isInstallingUpdate ? Visibility.Visible : Visibility.Collapsed;
            DownloadInstallButtonBorder.Visibility = _isInstallingUpdate ? Visibility.Collapsed : Visibility.Visible;

            UpdateProgressPercentText.Text = $"{_updateDlProgress}%";
            UpdateProgressFill.Width = UpdateProgressTrackWidth * (_updateDlProgress / 100.0);
        }
        else if (_updateChecked)
        {
            UpdateAvailableBorder.Visibility = Visibility.Collapsed;
            UpToDateBanner.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateAvailableBorder.Visibility = Visibility.Collapsed;
            UpToDateBanner.Visibility = Visibility.Collapsed;
        }
    }

    // ── EFB Companion ────────────────────────────────────────────────────────────────────────

    // EfbServer.StatusChanged fires whenever the server starts/stops, which can happen from a
    // background async continuation -- marshaled via DispatcherQueue.TryEnqueue, same pattern as
    // OnUpdateProgress above and Messages' live-event subscriptions. Invalidates the cached QR
    // grid, matching Settings.razor's own `_efbQrCache = null` on this event (URL/port may have
    // changed across a stop/start cycle).
    private void OnEfbStatusChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _efbQrBuiltForUrl = null;
            UpdateEfbPanel();
        });
    }

    private async void OnStartEfbTapped(object sender, TappedRoutedEventArgs e) => await StartEfbAsync();

    private async void OnStopEfbTapped(object sender, TappedRoutedEventArgs e) => await StopEfbAsync();

    private async Task StartEfbAsync()
    {
        if (_efbBusy) return;
        _efbBusy = true;
        UpdateEfbPanel();

        try
        {
            await _efbServer.StartAsync();
        }
        finally
        {
            _efbBusy = false;
            UpdateEfbPanel();
        }
    }

    private async Task StopEfbAsync()
    {
        if (_efbBusy) return;
        _efbBusy = true;
        UpdateEfbPanel();

        try
        {
            await _efbServer.StopAsync();
        }
        finally
        {
            _efbBusy = false;
            UpdateEfbPanel();
        }
    }

    // Matches Settings.razor's OnEfbEnabledChanged exactly: saves immediately (unlike Start
    // Automatically below, a plain deferred-to-Save bind), and stops the running server if the
    // toggle is switched off while it's up.
    private async void OnEfbEnabledToggled(object sender, RoutedEventArgs e)
    {
        var enabled = EfbEnabledToggle.IsOn;
        _settings.Settings.EfbEnabled = enabled;
        _settings.Save();

        if (!enabled && _efbServer.IsRunning)
            await StopEfbAsync();
    }

    // Plain deferred-to-Save bind, no immediate side effect -- matches the original's plain @bind
    // on Settings.EfbAutoStart, explicitly different from Enable EFB Companion above.
    private void OnEfbAutoStartToggled(object sender, RoutedEventArgs e)
    {
        _settings.Settings.EfbAutoStart = EfbAutoStartToggle.IsOn;
    }

    private void UpdateEfbPanel()
    {
        var running = _efbServer.IsRunning;

        EfbRunningGrid.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        EfbStoppedBanner.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StopEfbButtonBorder.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StartEfbButtonBorder.Visibility = running ? Visibility.Collapsed : Visibility.Visible;

        // Both Start/Stop are disabled while a start/stop call is in flight, matching the
        // original's exact `disabled="@_efbBusy"` on both buttons.
        var enabled = !_efbBusy;
        StartEfbButtonBorder.IsHitTestVisible = enabled;
        StartEfbButtonBorder.Opacity = enabled ? 1.0 : 0.45;
        StartEfbButtonText.Text = _efbBusy ? "Starting…" : "Start EFB Server";

        StopEfbButtonBorder.IsHitTestVisible = enabled;
        StopEfbButtonBorder.Opacity = enabled ? 1.0 : 0.45;

        if (running)
        {
            EnsureEfbQrBuilt();
            EfbStatusText.Text = $"Running · port {_efbServer.Port}";
            EfbAddressText.Text = _efbServer.PrimaryUrl.Split('#')[0];
            EfbPairingCodeText.Text = _efbServer.PairingCode;
        }
    }

    // Renders the QR code as a Grid of black/white Rectangle cells built from QrCode.Size/
    // GetModule(x, y) -- per the design spec's explicit resolution of this (WinUI has no inline
    // SVG renderer), NOT the original's QrCode.Encode(...).ToSvg(...) MarkupString injection.
    // Cached per URL (EfbServer.PrimaryUrl encodes the pairing token, which changes across a
    // stop/start cycle), same reasoning as Settings.razor's own _efbQrCache/_efbQrForUrl pair.
    private void EnsureEfbQrBuilt()
    {
        var url = _efbServer.PrimaryUrl;
        if (string.IsNullOrEmpty(url)) return;
        if (_efbQrBuiltForUrl == url) return;

        EfbQrGrid.RowDefinitions.Clear();
        EfbQrGrid.ColumnDefinitions.Clear();
        EfbQrGrid.Children.Clear();

        try
        {
            var qr = QrCode.Encode(url, QrEcc.Medium);

            for (var i = 0; i < qr.Size; i++)
            {
                EfbQrGrid.RowDefinitions.Add(new RowDefinition());
                EfbQrGrid.ColumnDefinitions.Add(new ColumnDefinition());
            }

            for (var y = 0; y < qr.Size; y++)
            {
                for (var x = 0; x < qr.Size; x++)
                {
                    var cell = new Rectangle { Fill = qr.GetModule(x, y) ? BlackBrush : WhiteBrush };
                    Grid.SetRow(cell, y);
                    Grid.SetColumn(cell, x);
                    EfbQrGrid.Children.Add(cell);
                }
            }

            _efbQrBuiltForUrl = url;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Settings] Failed to build EFB QR");
        }
    }

    // ── Save / Reset ─────────────────────────────────────────────────────────────────────────
    // Ported from Settings.razor's SaveSettings()/ResetSettings() (lines 984-1012). Reset is the
    // one handler on this page that has no Blazor StateHasChanged()-equivalent to lean on: the
    // original just mutates SettingsService.Settings and lets the framework re-render every
    // @bind/@if expression from scratch. Here, every control that only ever WRITES to Settings
    // (TextBox.TextChanged, PasswordBox.PasswordChanged, ToggleSwitch.Toggled) has to be pushed
    // the freshly-reset values explicitly, then every panel's own Update*() method re-run --
    // otherwise the page keeps showing whatever was on screen before Reset was tapped.

    private void OnSaveSettingsTapped(object sender, TappedRoutedEventArgs e)
    {
        // Matches Settings.razor's SaveSettings() exactly: regenerate/persist the Friend Code
        // first (RefreshFriendCode() saves internally too), refresh the Pilot Profile name
        // display, then a final explicit Save() to persist everything else currently in memory --
        // including the 4 deferred-save toggles (Auto-connect to MSFS, Desktop Alerts, Minimize
        // to Tray, Start EFB Automatically) that intentionally don't save immediately anywhere
        // else on this page.
        _settings.RefreshFriendCode();
        UpdateNameDisplay();
        _settings.Save();

        _statusMessage = "Settings saved successfully.";
        _isError = false;
        UpdateStatusBanner();
    }

    private void OnResetSettingsTapped(object sender, TappedRoutedEventArgs e)
    {
        _settings.Reset();

        _isVerified = false;
        _verifiedEmail = "";

        // Re-push every control's raw value from the freshly-reset Settings object. This mirrors
        // the constructor's own initial-value assignments verbatim (including the same "setting
        // .Text/.Password/.IsOn here harmlessly re-fires its own handler, which just re-writes
        // the same value back to Settings" reasoning documented there) -- it is the simplest way
        // to guarantee every control agrees with the reset object rather than patching each one
        // individually and risking a miss.
        PilotNameTextBox.Text = _settings.Settings.PilotName;
        PilotIdTextBox.Text = _settings.Settings.PilotId;
        SimBriefUsernameTextBox.Text = _settings.Settings.SimBriefUsername;
        AcarsKeyPasswordBox.Password = _settings.Settings.AcarsKey;
        SelectAutoPirepIntervalItem(_settings.Settings.AutoPirepIntervalMinutes);

        HoppiesPilotCodeBox.Password = _settings.Settings.HoppiesPilotCode;
        HoppiesEnabledToggle.IsOn = _settings.Settings.HoppiesEnabled;
        HoppiesLoadsheetToggle.IsOn = _settings.Settings.HoppiesSendLoadsheet;
        HoppiesTimingRemindersToggle.IsOn = _settings.Settings.HoppiesSendTimingReminders;
        HoppiesAirborneLandingToggle.IsOn = _settings.Settings.HoppiesSendAirborneLandingCalls;
        HoppiesPostFlightSummaryToggle.IsOn = _settings.Settings.HoppiesSendPostFlightSummary;
        HoppiesWeatherAdvisoriesToggle.IsOn = _settings.Settings.HoppiesSendWeatherAdvisories;
        HoppiesHazardAdvisoriesToggle.IsOn = _settings.Settings.HoppiesSendHazardAdvisories;

        AutoConnectToggle.IsOn = _settings.Settings.AutoConnectSimConnect;
        ShowApproachAlertsToggle.IsOn = _settings.Settings.ShowApproachAlerts;
        MinimizeToTrayToggle.IsOn = _settings.Settings.MinimizeToTray;
        ShareLivePositionToggle.IsOn = _settings.Settings.ShareLivePosition;

        DispatchBackendUrlTextBox.Text = _settings.Settings.DispatchBackendUrl;
        // Joystick binding is cleared by Reset() too (PttJoystickGuid/PttJoystickButton are part
        // of AppSettings) -- re-populating the combo box re-selects "-- No joystick --" since no
        // connected device will match Guid.Empty.
        PopulateJoystickComboBox();

        EfbEnabledToggle.IsOn = _settings.Settings.EfbEnabled;
        EfbAutoStartToggle.IsOn = _settings.Settings.EfbAutoStart;

        // Re-run every panel's own refresh method against the now-reset Settings object -- the
        // same sequence the constructor runs on first load, so nothing is left showing stale
        // pre-reset state. Enumerated explicitly (not "rebuild the whole page") so it's provable
        // nothing was skipped: Pilot Profile, Appearance, SimBrief, ACARS Configuration, Friend
        // Code, Hoppies category dim/disable, Dispatch Radio's PTT hotkey/joystick displays, and
        // EFB Companion.
        UpdateNameDisplay();
        UpdateThemeCardVisuals();
        UpdatePilotProfileVerifiedVisuals();
        UpdateSimBriefHintVisibility();
        UpdateAcarsInfoBanner();
        UpdateVerifySyncButtonState();
        UpdateFriendCodePanel();
        UpdateHoppiesCategoryEnabledState();
        UpdatePttHotkeyDisplay();
        UpdateJoystickBoundText();
        UpdateBindJoystickButtonState();
        UpdateCheckForUpdatesButtonState();
        UpdateUpdateAvailablePanel();
        UpdateEfbPanel();

        // Microphone/Speaker selection also needs to re-sync (PttMicrophoneDeviceId/
        // PttSpeakerDeviceId are now blank) -- re-running the same async enumeration+select this
        // page already uses on load re-selects "System default" for both, since no device will
        // match an empty saved id.
        _ = LoadAudioDevicesAsync();

        _statusMessage = "Settings reset to defaults.";
        _isError = false;
        UpdateStatusBanner();
    }

    // ── Status banner ────────────────────────────────────────────────────────────────────────
    // Same dismissible success/error pattern EventsPage.xaml.cs's own status banner already
    // established. Task 2's SyncProfileAsync() above is the first behavior to actually raise a
    // status message; the Save/Reset row above is the next.

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
