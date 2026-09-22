using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Platform;
using AviatesAirTracker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI;

namespace AviatesAirTracker.Pages;

/// <summary>
/// Native reproduction of AviatesAirTracker.LegacyWeb\Pages\Login.razor -- the final page
/// conversion in this migration. See LoginPage.xaml's own header comment for the layout/video
/// background rationale; this class ports Login.razor's validation state machine
/// (_acarsKey/_loading/_success/_errorMessage) faithfully, including its exact copy, the ~900ms
/// success delay before navigating away, and the ~520ms shake feel on a rejected/failed key --
/// reproduced here as a code-behind Storyboard (DoubleAnimationUsingKeyFrames on a TranslateTransform)
/// rather than a CSS keyframe/class toggle, matching MessagesPage.xaml.cs's own
/// AnimateBubbleIn precedent for porting a CSS animation into this SDK.
///
/// Does not itself redirect away when a pilot is already logged in -- that check belongs to
/// ShellWindow's OnFirstActivated (Task 2 of this plan), the single gate that decides whether this
/// page is ever navigated to in the first place; duplicating it here would be redundant scope creep
/// for a page that Task 1 alone does not wire into the shell yet.
/// </summary>
public sealed partial class LoginPage : Page
{
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x64, 0x74, 0x8B));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));
    // The one deliberate accent color this page introduces beyond the app's existing blue -- a warm,
    // runway/taxiway-light-inspired amber used only for the "CLEARED FOR PUSHBACK" success moment.
    // Not reused anywhere else on this page or elsewhere in the app (design spec).
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0xB9, 0x42));
    private static readonly Color DefaultInputBorderColor = Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF);
    private static readonly Color ErrorInputBorderColor = Color.FromArgb(0xFF, 0xEF, 0x44, 0x44);
    private static readonly Color SuccessInputBorderColor = Color.FromArgb(0xFF, 0xF5, 0xB9, 0x42);

    private readonly SettingsService _settings;
    private readonly AviatesBackendClient _backendClient;

    private MediaPlayer? _mediaPlayer;

    private bool _loading;
    private bool _success;
    private string _errorMessage = "";

    public LoginPage()
    {
        InitializeComponent();

        InteractiveEffects.Attach(PasteButtonBorder, ConnectButtonBorder, RegisterLinkBorder);

        _settings = App.GetService<SettingsService>();
        _backendClient = App.GetService<AviatesBackendClient>();

        // wwwroot ships as loose Content next to the exe, not a packaged ms-appx:// asset -- same
        // absolute-path technique DashboardPage.xaml.cs's own hero.webp load already established.
        var logoPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "images", "AviatesairlogoWhite.webp");
        if (File.Exists(logoPath))
            BrandLogoImage.Source = new BitmapImage(new Uri(logoPath));

        // Video background with graceful fallback -- the user is recording login-bg.mp4 separately,
        // in parallel with this build, so it very likely does not exist yet. That is the expected,
        // normal state, not an error: FallbackBgGrid (declared Visible in XAML) stays showing and
        // BgVideoPlayer is simply never sourced. The moment a valid file lands at this exact path and
        // the app relaunches, this branch picks it up automatically -- no further code change needed.
        var videoPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "videos", "login-bg.mp4");
        if (File.Exists(videoPath))
        {
            _mediaPlayer = new MediaPlayer
            {
                IsLoopingEnabled = true,
                IsMuted = true,
                AutoPlay = true,
                Source = MediaSource.CreateFromUri(new Uri(videoPath)),
            };
            BgVideoPlayer.SetMediaPlayer(_mediaPlayer);
            BgVideoPlayer.Visibility = Visibility.Visible;
            FallbackBgGrid.Visibility = Visibility.Collapsed;
        }

        UpdateVisualState();

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        AcarsKeyTextBox.Focus(FocusState.Programmatic);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Pause();
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }
    }

    // ── Input ────────────────────────────────────────────────────────────────────────────────

    private async void OnAcarsKeyTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !_loading && !_success && !string.IsNullOrWhiteSpace(AcarsKeyTextBox.Text))
            await ValidateAsync();
    }

    private void OnAcarsKeyTextChanged(object sender, TextChangedEventArgs e) => UpdateVisualState();

    private async void OnPasteTapped(object sender, TappedRoutedEventArgs e)
    {
        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text)) return;

            var text = await view.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text)) return;

            AcarsKeyTextBox.Text = text.Trim();
            AcarsKeyTextBox.SelectionStart = AcarsKeyTextBox.Text.Length;
            _errorMessage = "";
            UpdateVisualState();
        }
        catch (Exception ex)
        {
            // Clipboard access denied or not available -- user can paste manually with Ctrl+V,
            // matching Login.razor's own PasteAsync try/catch (empty catch, same reasoning).
            Log.Debug(ex, "[Login] Clipboard paste unavailable");
        }
    }

    private async void OnConnectTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_loading || _success || string.IsNullOrWhiteSpace(AcarsKeyTextBox.Text)) return;
        await ValidateAsync();
    }

    // ── Validation flow (Login.razor's ValidateAsync, ported faithfully) ───────────────────────

    private async Task ValidateAsync()
    {
        var key = AcarsKeyTextBox.Text.Trim();
        if (string.IsNullOrEmpty(key)) return;

        _loading = true;
        _errorMessage = "";
        UpdateVisualState();

        try
        {
            var result = await _backendClient.ValidateAcarsKeyAsync(key);

            if (result is null)
            {
                await ShowErrorAsync("Invalid ACARS key. Check your pilot dashboard and try again.");
                return;
            }

            // Persist authenticated pilot profile -- exact field mapping confirmed against
            // Login.razor:162-167 and PilotAuthResult's own properties (Core/Backend/AviatesBackendClient.cs).
            _settings.Settings.AcarsKey = key;
            _settings.Settings.PilotName = result.Name;
            _settings.Settings.PilotId = result.PilotId;
            _settings.Settings.SimBriefUsername = result.SimBrief;
            _settings.Save();
            _settings.RefreshFriendCode();

            // Re-populate the (now-empty, per OnLogOutTapped's ClearAllAsync) local flights/
            // landings/friends caches for this pilot without requiring an app restart -- these
            // are the same App.xaml.cs startup-sync methods, reused. Fire-and-forget: the pilot
            // shouldn't wait on the network here, and every page that reads these repos already
            // refreshes on its own Loaded/OnNavigatedTo.
            var sp = App.ServiceProvider;
            _ = App.SyncPirepHistoryAsync(sp);
            _ = App.SyncLandingHistoryAsync(sp);
            _ = App.SyncFriendsAsync(sp);

            _loading = false;
            _success = true;
            UpdateVisualState();

            await Task.Delay(900); // matches Login.razor's own Task.Delay(900) before navigating
            Frame.Navigate(typeof(DashboardPage));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Login] ACARS key validation failed");
            await ShowErrorAsync("Unable to reach the server. Check your internet connection.");
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        _loading = false;
        _errorMessage = message;
        UpdateVisualState();

        await RunShakeAsync();
    }

    // ── Visual state ─────────────────────────────────────────────────────────────────────────

    private void UpdateVisualState()
    {
        var hasError = !string.IsNullOrEmpty(_errorMessage);
        var hasKey = !string.IsNullOrWhiteSpace(AcarsKeyTextBox.Text);

        AcarsKeyTextBox.IsEnabled = !_loading && !_success;
        AcarsKeyInputBorderBrush.Color = _success ? SuccessInputBorderColor
            : hasError ? ErrorInputBorderColor
            : DefaultInputBorderColor;

        ErrorText.Text = _errorMessage;
        ErrorPanel.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;

        var ctaEnabled = !_loading && !_success && hasKey;
        ConnectButtonBorder.Opacity = !hasKey && !_loading && !_success ? 0.5 : 1.0;
        ConnectButtonBorder.IsHitTestVisible = ctaEnabled;

        ConnectSpinner.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
        ConnectSpinner.IsActive = _loading;
        ConnectCheckText.Visibility = _success ? Visibility.Visible : Visibility.Collapsed;
        ConnectButtonText.Text = _loading ? "Validating…" : _success ? "Authenticated, Loading" : "Connect to AviatesAir";

        if (_loading)
        {
            ClearanceStripText.Text = "STATUS: VALIDATING CREDENTIALS";
            ClearanceStripText.Foreground = MutedBrush;
            ClearanceStripDot.Fill = MutedBrush;
        }
        else if (_success)
        {
            ClearanceStripText.Text = "STATUS: CLEARED FOR PUSHBACK";
            ClearanceStripText.Foreground = SuccessBrush;
            ClearanceStripDot.Fill = SuccessBrush;
        }
        else if (hasError)
        {
            ClearanceStripText.Text = "STATUS: CLEARANCE DENIED";
            ClearanceStripText.Foreground = ErrorBrush;
            ClearanceStripDot.Fill = ErrorBrush;
        }
        else
        {
            ClearanceStripText.Text = "STATUS: AWAITING CLEARANCE";
            ClearanceStripText.Foreground = MutedBrush;
            ClearanceStripDot.Fill = MutedBrush;
        }
    }

    // Reproduces Login.razor's CSS shake keyframe (a rejected/failed key) as a WinUI Storyboard --
    // same technique MessagesPage.xaml.cs's own AnimateBubbleIn uses to port a CSS animation into
    // this SDK. A handful of quick decaying oscillations totaling ~520ms, matching the original's
    // felt duration; unlike the CSS version there is no need for a 16ms "off, then on" class-toggle
    // trick to force a restart -- Storyboard.Begin() always restarts cleanly even mid-animation.
    private async Task RunShakeAsync()
    {
        if (LeftContentGrid.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            LeftContentGrid.RenderTransform = transform;
        }

        var keyFrames = new DoubleAnimationUsingKeyFrames();
        void AddFrame(double ms, double value) => keyFrames.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)),
            Value = value,
        });

        AddFrame(0, 0);
        AddFrame(60, -10);
        AddFrame(120, 8);
        AddFrame(180, -8);
        AddFrame(240, 6);
        AddFrame(300, -6);
        AddFrame(360, 4);
        AddFrame(420, -4);
        AddFrame(480, 2);
        AddFrame(520, 0);

        Storyboard.SetTarget(keyFrames, transform);
        Storyboard.SetTargetProperty(keyFrames, "X");

        var storyboard = new Storyboard();
        storyboard.Children.Add(keyFrames);

        var tcs = new TaskCompletionSource();
        void OnCompleted(object? s, object e)
        {
            storyboard.Completed -= OnCompleted;
            tcs.TrySetResult();
        }
        storyboard.Completed += OnCompleted;
        storyboard.Begin();

        await tcs.Task;
    }

    // ── Footer ───────────────────────────────────────────────────────────────────────────────

    private async void OnRegisterTapped(object sender, TappedRoutedEventArgs e)
    {
        await Windows.System.Launcher.LaunchUriAsync(new Uri("https://flyaviatesair.uk/register"));
    }
}
