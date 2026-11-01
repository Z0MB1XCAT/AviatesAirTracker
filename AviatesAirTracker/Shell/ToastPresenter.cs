using AviatesAirTracker.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Numerics;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI;

namespace AviatesAirTracker.Shell;

/// <summary>
/// The stack of in-app toasts in the top-right corner of the shell, fed by
/// <see cref="AlertService.AlertRaised"/>. Built in code rather than XAML for the same reason as
/// the nav rail: any &lt;Style&gt; element crashes this SDK's XamlCompiler.
/// </summary>
internal sealed class ToastPresenter
{
    private const int MaxVisible = 4;

    private static readonly SolidColorBrush Surface = new(Color.FromArgb(0xFF, 0x12, 0x1A, 0x2A));
    private static readonly SolidColorBrush TextPrimary = new(Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9));
    private static readonly SolidColorBrush TextSecondary = new(Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1));
    private static readonly SolidColorBrush CloseHover = new(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

    private readonly StackPanel _host;
    private readonly DispatcherQueue _dispatcher;
    private readonly AlertService _alerts;
    private readonly bool _animate = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    public ToastPresenter(StackPanel host, DispatcherQueue dispatcher, AlertService alerts)
    {
        _host = host;
        _dispatcher = dispatcher;
        _alerts = alerts;
        AutomationProperties.SetName(_host, "Notifications");
    }

    /// <summary>Safe to call from any thread.</summary>
    public void Show(AlertNotification alert) => _dispatcher.TryEnqueue(() => ShowOnUi(alert));

    private void ShowOnUi(AlertNotification alert)
    {
        var accent = AccentFor(alert.Level);
        var toast = BuildToast(alert, accent, out var close);

        while (_host.Children.Count >= MaxVisible)
            _host.Children.RemoveAt(_host.Children.Count - 1);
        _host.Children.Insert(0, toast);

        if (_animate)
        {
            toast.Opacity = 0;
            toast.Translation = new Vector3(28, 0, 0);
            toast.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) };
            toast.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(240) };
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                toast.Opacity = 1;
                toast.Translation = Vector3.Zero;
            });
        }

        // Screen readers announce it without moving focus.
        AutomationProperties.SetLiveSetting(toast, AutomationLiveSetting.Polite);
        toast.Loaded += (_, _) =>
            FrameworkElementAutomationPeer.FromElement(toast)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

        void Dismiss() => Remove(toast, alert);
        close.Tapped += (_, e) => { e.Handled = true; Dismiss(); };
        close.KeyDown += (_, e) =>
        {
            if (e.Key is VirtualKey.Enter or VirtualKey.Space) { e.Handled = true; Dismiss(); }
        };

        var lifetime = alert.ExpiresAt is { } expires
            ? expires - DateTime.UtcNow
            : TimeSpan.FromSeconds(8);
        var timer = _dispatcher.CreateTimer();
        timer.Interval = lifetime > TimeSpan.Zero ? lifetime : TimeSpan.FromSeconds(1);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Dismiss();
        timer.Start();

        // Hovering keeps it on screen long enough to read.
        toast.PointerEntered += (_, _) => timer.Stop();
        toast.PointerExited += (_, _) => { timer.Interval = TimeSpan.FromSeconds(3); timer.Start(); };
    }

    private void Remove(FrameworkElement toast, AlertNotification alert)
    {
        _alerts.Dismiss(alert.Id);
        if (!_host.Children.Contains(toast)) return;
        if (!_animate)
        {
            _host.Children.Remove(toast);
            return;
        }
        toast.Opacity = 0;
        var t = _dispatcher.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(190);
        t.IsRepeating = false;
        t.Tick += (_, _) => _host.Children.Remove(toast);
        t.Start();
    }

    private static Border BuildToast(AlertNotification alert, Color accent, out Border close)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new Rectangle
        {
            Fill = new SolidColorBrush(accent),
            RadiusX = 1.5,
            RadiusY = 1.5,
        });

        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrWhiteSpace(alert.Title))
        {
            text.Children.Add(new TextBlock
            {
                Text = alert.Title,
                FontFamily = (FontFamily)Application.Current.Resources["BodyFontBold"],
                FontSize = 13.5,
                Foreground = TextPrimary,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        text.Children.Add(new TextBlock
        {
            Text = alert.Message,
            FontSize = 12.5,
            LineHeight = 19,
            Foreground = string.IsNullOrWhiteSpace(alert.Title) ? TextPrimary : TextSecondary,
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        close = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Top,
            IsTabStop = true,
            UseSystemFocusVisuals = true,
            Child = new FontIcon
            {
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                Glyph = "",
                FontSize = 10,
                Foreground = TextSecondary,
            },
        };
        var closeRef = close;
        close.PointerEntered += (_, _) => closeRef.Background = CloseHover;
        close.PointerExited += (_, _) => closeRef.Background = new SolidColorBrush(Colors.Transparent);
        AutomationProperties.SetName(close, "Dismiss notification");
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);

        var toast = new Border
        {
            Width = 360,
            Background = Surface,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 12, 8, 12),
            Child = grid,
        };
        AutomationProperties.SetName(toast,
            string.IsNullOrWhiteSpace(alert.Title) ? alert.Message : $"{alert.Title}. {alert.Message}");
        return toast;
    }

    private static Color AccentFor(AlertLevel level) => level switch
    {
        AlertLevel.Success => Color.FromArgb(0xFF, 0x3E, 0xC7, 0xAC),
        AlertLevel.Warning => Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B),
        AlertLevel.Critical => Color.FromArgb(0xFF, 0xEF, 0x44, 0x44),
        _ => Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC),
    };
}
