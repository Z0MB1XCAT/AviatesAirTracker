using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Serilog;
using System.IO;

namespace AviatesAirTracker.Services;

// ============================================================
// DESKTOP NOTIFICATION SERVICE
//
// One place every "the pilot should know this" event goes through. While the app window is in
// front, it raises an in-app toast (AlertService → ShellWindow's toast stack). While the pilot is
// in the sim with the app behind it or in the tray, it raises a Windows notification instead, so
// a DM, an event reminder or a rejected PIREP isn't missed until the next alt-tab.
//
// Clicking a Windows notification brings the app forward on the page named by its "page"
// argument (a NavDestination tag); ShellWindow handles that through NavigateRequested.
// ============================================================

public sealed class DesktopNotificationService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly AlertService _alerts;
    private bool _registered;

    /// <summary>Set by ShellWindow: true while its window is the active foreground window.</summary>
    public Func<bool> IsAppInForeground { get; set; } = () => true;

    /// <summary>A Windows notification was clicked. Argument: the page tag to open ("" for none).
    /// Raised on a background thread.</summary>
    public event EventHandler<string>? NavigateRequested;

    public DesktopNotificationService(SettingsService settings, AlertService alerts)
    {
        _settings = settings;
        _alerts = alerts;
    }

    public void Initialize()
    {
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                Log.Information("[Notifications] Windows notifications not supported on this system");
                return;
            }

            AppNotificationManager.Default.NotificationInvoked += (_, args) =>
            {
                args.Arguments.TryGetValue("page", out var page);
                NavigateRequested?.Invoke(this, page ?? "");
            };

            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIconSmall.png");
            if (File.Exists(icon))
                AppNotificationManager.Default.Register("Aviates Air Tracker", new Uri(icon));
            else
                AppNotificationManager.Default.Register();

            _registered = true;
            Log.Information("[Notifications] Windows notifications registered");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Notifications] Could not register for Windows notifications -- in-app toasts only");
        }
    }

    /// <summary>
    /// Tells the pilot something. <paramref name="page"/> is the NavDestination tag a click on
    /// the Windows notification opens. <paramref name="alwaysInApp"/> also raises the in-app
    /// toast when the window is behind the sim, for things that must still be on screen when the
    /// pilot comes back to it.
    /// </summary>
    public void Notify(string title, string message, AlertLevel level = AlertLevel.Info,
        string? page = null, bool alwaysInApp = false)
    {
        var foreground = SafeIsForeground();
        if (foreground || alwaysInApp || !CanShowWindowsNotification())
            _alerts.ShowAlert(title, message, level);

        if (!foreground && CanShowWindowsNotification())
            ShowWindowsNotification(title, message, page);
    }

    private bool CanShowWindowsNotification() => _registered && _settings.Settings.WindowsNotificationsEnabled;

    private bool SafeIsForeground()
    {
        try { return IsAppInForeground(); }
        catch { return true; }
    }

    private static void ShowWindowsNotification(string title, string message, string? page)
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddArgument("page", page ?? "")
                .AddText(title)
                .AddText(message.Length > 180 ? message[..177] + "..." : message)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Notifications] Failed to show a Windows notification");
        }
    }

    public void Dispose()
    {
        if (!_registered) return;
        _registered = false;
        try { AppNotificationManager.Default.Unregister(); }
        catch (Exception ex) { Log.Debug(ex, "[Notifications] Unregister failed"); }
    }
}
