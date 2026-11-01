using AviatesAirTracker.Models;
using AviatesAirTracker.Navigation;
using AviatesAirTracker.Services;
using AviatesAirTracker.Shell;
using Microsoft.UI.Xaml;

namespace AviatesAirTracker;

// In-app toasts and Windows notifications: what the pilot is told, and where a click on a
// Windows notification takes them.
public sealed partial class ShellWindow
{
    private ToastPresenter _toasts = null!;
    private volatile bool _isForeground = true;
    private volatile bool _isWindowVisible = true;

    private void InitializeNotifications()
    {
        _toasts = new ToastPresenter(ToastHost, DispatcherQueue, _alertService);
        _alertService.AlertRaised += OnAlertRaised;

        Activated += (_, e) => _isForeground = e.WindowActivationState != WindowActivationState.Deactivated;
        _notifications.IsAppInForeground = () => _isForeground && _isWindowVisible;
        _notifications.NavigateRequested += OnNotificationClicked;

        _messagingService.NewMessageReceived += OnDirectMessageReceived;
        _pirepSync.SyncStateChanged += OnPirepSyncStateChanged;
    }

    private void OnAlertRaised(object? sender, AlertNotification alert) => _toasts.Show(alert);

    private void OnNotificationClicked(object? sender, string pageTag) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            BringToFront();
            NavigateToTag(pageTag);
        });

    private void OnDirectMessageReceived(object? sender, PilotMessage message) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            // Already reading the conversation: the message appears there, no toast needed.
            if (_isForeground && NativeHost.CurrentSourcePageType == typeof(Pages.MessagesPage)) return;
            _notifications.Notify($"Message from {message.SenderName}", message.Content, AlertLevel.Info, page: "messages");
        });

    private void OnPirepSyncStateChanged(object? sender, FlightRecord flight) =>
        DispatcherQueue.TryEnqueue(() => _ = RefreshFlightsBadgeAsync());

    /// <summary>Opens the rail destination with this tag ("landings", "flights", ...). Unknown or
    /// empty tags leave the current page as it is.</summary>
    internal void NavigateToTag(string? tag, object? parameter = null)
    {
        if (string.IsNullOrEmpty(tag) || !IsLoggedIn()) return;
        var destination = NavDestinations.All.FirstOrDefault(d => d.Tag == tag);
        if (destination?.NativePageType == null) return;
        NativeHost.Navigate(destination.NativePageType, parameter);
    }
}
