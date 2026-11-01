using AviatesAirTracker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using Windows.UI;

namespace AviatesAirTracker;

// The mandatory-update screen. Updates can't be skipped or cancelled (every pilot runs the
// current, patched build), so while one installs the whole window is covered by a screen that
// says what's happening, and closing the window is refused until the installer takes over.
public sealed partial class ShellWindow
{
    private volatile bool _updateInstalling;
    private TextBlock? _updateTitle;
    private TextBlock? _updateStatus;
    private ProgressBar? _updateProgress;

    private void InitializeUpdateOverlay()
    {
        _updates.InstallStageChanged += (_, e) => DispatcherQueue.TryEnqueue(() => ShowUpdateStage(e.Info, e.Stage));
        _updates.DownloadProgressChanged += (_, pct) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_updateProgress == null || !_updateInstalling) return;
            _updateProgress.IsIndeterminate = false;
            _updateProgress.Value = pct;
            if (_updateStatus != null) _updateStatus.Text = $"Downloading the update… {pct}%";
        });
        _updates.InstallFailed += (_, reason) => DispatcherQueue.TryEnqueue(() =>
        {
            _updateInstalling = false;
            OverlayHost.Visibility = Visibility.Collapsed;
            OverlayHost.Children.Clear();
            _alertService.ShowAlert("Update didn't install",
                $"{reason} The app will try again automatically.", AlertLevel.Warning);
        });
    }

    private void ShowUpdateStage(UpdateInfo info, UpdateStage stage)
    {
        if (!_updateInstalling)
        {
            _updateInstalling = true;
            BuildUpdateOverlay(info);
            _notifications.Notify("Updating Aviates Air Tracker",
                $"Version {info.Version} is installing. The app will close and reopen by itself.",
                AlertLevel.Info);
            Log.Information("[Shell] Mandatory update overlay shown for {Version}", info.Version);
        }

        if (_updateStatus == null || _updateProgress == null) return;
        switch (stage)
        {
            case UpdateStage.Downloading:
                _updateStatus.Text = "Downloading the update…";
                break;
            case UpdateStage.Verifying:
                _updateProgress.IsIndeterminate = true;
                _updateStatus.Text = "Checking the download is genuine…";
                break;
            case UpdateStage.Installing:
                _updateProgress.IsIndeterminate = true;
                _updateStatus.Text = "Installing. The app will reopen in a moment.";
                break;
        }
    }

    private void BuildUpdateOverlay(UpdateInfo info)
    {
        var text = new SolidColorBrush(Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9));
        var muted = new SolidColorBrush(Color.FromArgb(0xFF, 0x94, 0xA3, 0xB8));
        var accent = (Brush)Application.Current.Resources["AccentBrush"];

        _updateTitle = new TextBlock
        {
            Text = $"Updating to version {info.Version}",
            FontFamily = (FontFamily)Application.Current.Resources["DisplayFontBold"],
            FontSize = 24,
            CharacterSpacing = -30,
            Foreground = text,
            TextWrapping = TextWrapping.Wrap,
        };
        var body = new TextBlock
        {
            Text = "Aviates Air Tracker is installing a required update and will close and reopen on its own. "
                 + "Updates can't be skipped: keeping every pilot on the latest version keeps the app secure.",
            FontSize = 13.5,
            LineHeight = 22,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
        };
        _updateProgress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            IsIndeterminate = true,
            Foreground = accent,
            Height = 4,
        };
        _updateStatus = new TextBlock
        {
            Text = "Preparing the update…",
            FontSize = 12.5,
            Foreground = text,
        };
        AutomationProperties.SetLiveSetting(_updateStatus, AutomationLiveSetting.Polite);

        var stack = new StackPanel { Spacing = 14 };
        stack.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/AppIconSmall.png")),
            Width = 36,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        stack.Children.Add(_updateTitle);
        stack.Children.Add(body);
        stack.Children.Add(_updateProgress);
        stack.Children.Add(_updateStatus);

        var card = new Border
        {
            MaxWidth = 460,
            Margin = new Thickness(24),
            Padding = new Thickness(28, 26, 28, 26),
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0C, 0x11, 0x1D)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x4D, 0x9B, 0xEC)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack,
        };
        AutomationProperties.SetName(card, _updateTitle.Text);

        OverlayHost.Children.Clear();
        OverlayHost.Background = new SolidColorBrush(Color.FromArgb(0xEB, 0x0A, 0x0D, 0x17));
        OverlayHost.Children.Add(card);
        OverlayHost.Visibility = Visibility.Visible;
        BringToFront();
    }
}
