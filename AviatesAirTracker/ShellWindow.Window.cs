using AviatesAirTracker.Dialogs;
using AviatesAirTracker.Platform;
using AviatesAirTracker.Services;
using AviatesAirTracker.Shell;
using Microsoft.UI.Windowing;
using Serilog;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace AviatesAirTracker;

// Window placement (remembered between launches and fitted to the screen), the tray icon,
// close-to-tray, and the "a flight is being tracked" check before quitting.
public sealed partial class ShellWindow
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);

    private AppWindow? _appWindow;
    private nint _hwnd;
    private TrayIcon? _tray;
    private bool _quitRequested;
    private bool _startHidden;
    private bool _toldAboutTray;

    /// <summary>Launched in the background (with Windows or the sim): set up everything, but keep
    /// the window hidden and live in the tray until the pilot opens it.</summary>
    public void StartInTray()
    {
        _startHidden = true;
        EnsureInitialised();
    }

    private void InitializeWindowBehaviour(nint hwnd, AppWindow appWindow)
    {
        _hwnd = hwnd;
        _appWindow = appWindow;

        RestorePlacement(appWindow);
        appWindow.Closing += OnAppWindowClosing;

        _tray = new TrayIcon(hwnd, "Aviates Air Tracker");
        _tray.OpenRequested += BringToFront;
        _tray.QuitRequested += () => _ = QuitAsync();
        _sessionManager.SessionStateChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateTrayTooltip);
        _settings.SettingsChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateTrayVisibility);

        if (_startHidden)
        {
            _isWindowVisible = false;
            appWindow.Hide();
        }
        UpdateTrayVisibility();
        UpdateTrayTooltip();
    }

    private void RestorePlacement(AppWindow appWindow)
    {
        var s = _settings.Settings;
        PixelRect? saved = s.WindowWidth > 0 ? new PixelRect(s.WindowX, s.WindowY, s.WindowWidth, s.WindowHeight) : null;

        var probe = saved is { } r ? new RectInt32(r.X, r.Y, r.Width, r.Height) : new RectInt32(0, 0, 1, 1);
        var display = saved != null
            ? DisplayArea.GetFromRect(probe, DisplayAreaFallback.Nearest)
            : DisplayArea.Primary;
        var work = display.WorkArea;

        var fit = WindowPlacementLogic.Fit(saved, new PixelRect(work.X, work.Y, work.Width, work.Height));
        appWindow.MoveAndResize(new RectInt32(fit.X, fit.Y, fit.Width, fit.Height));

        if (s.WindowMaximized && appWindow.Presenter is OverlappedPresenter p)
            p.Maximize();
    }

    private void SavePlacement()
    {
        if (_appWindow == null) return;
        try
        {
            var s = _settings.Settings;
            var maximized = _appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            var minimized = _appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
            s.WindowMaximized = maximized;
            // Maximized/minimized bounds aren't the ones to come back to; keep the last normal ones.
            if (!maximized && !minimized && _appWindow.IsVisible)
            {
                s.WindowX = _appWindow.Position.X;
                s.WindowY = _appWindow.Position.Y;
                s.WindowWidth = _appWindow.Size.Width;
                s.WindowHeight = _appWindow.Size.Height;
            }
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Shell] Could not save window placement");
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_quitRequested)
        {
            SavePlacement();
            return;
        }

        if (_updateInstalling)
        {
            args.Cancel = true;
            _alertService.ShowAlert("Update in progress",
                "The app will close and reopen by itself once the update has installed.", AlertLevel.Info);
            return;
        }

        if (_settings.Settings.MinimizeToTray)
        {
            args.Cancel = true;
            HideToTray();
            return;
        }

        if (ShellBadgeLogic.IsFlightLive(_sessionManager.State))
        {
            args.Cancel = true;
            _ = QuitAsync();
            return;
        }

        SavePlacement();
    }

    /// <summary>Exits for real (tray "Quit", or the close button with tray mode off), checking
    /// first if a flight is being tracked.</summary>
    private async Task QuitAsync()
    {
        if (_updateInstalling) return;

        if (ShellBadgeLogic.IsFlightLive(_sessionManager.State))
        {
            BringToFront();
            var quit = await ConfirmDialog.ShowAsync(
                RootGrid.XamlRoot,
                "Quit while a flight is being tracked?",
                "Tracking stops when the app closes. You can resume this flight next time you open the app, but anything flown while it's closed won't be recorded.",
                "Quit",
                "Keep tracking");
            if (!quit) return;
        }

        _quitRequested = true;
        SavePlacement();
        Close();
    }

    private void HideToTray()
    {
        SavePlacement();
        _tray?.Show();
        _appWindow?.Hide();
        _isWindowVisible = false;

        if (!_toldAboutTray)
        {
            _toldAboutTray = true;
            _notifications.Notify("Still running in the tray",
                "Aviates Air Tracker keeps tracking in the background. Right-click its tray icon to quit.",
                AlertLevel.Info);
        }
    }

    /// <summary>Shows and focuses the window: from the tray, from a notification, or when a
    /// second copy of the app is started.</summary>
    public void BringToFront()
    {
        if (_appWindow == null)
        {
            Activate();
            return;
        }
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p)
            p.Restore();
        _appWindow.Show();
        _isWindowVisible = true;
        Activate();
        if (_hwnd != 0) SetForegroundWindow(_hwnd);
        UpdateTrayVisibility();
    }

    private void UpdateTrayVisibility()
    {
        if (_tray == null) return;
        if (_settings.Settings.MinimizeToTray || !_isWindowVisible) _tray.Show();
        else _tray.Hide();
    }

    private void UpdateTrayTooltip()
    {
        if (_tray == null) return;
        var flight = _sessionManager.CurrentFlight;
        var status = ShellBadgeLogic.IsFlightLive(_sessionManager.State) && flight != null
            ? $"Tracking {(string.IsNullOrEmpty(flight.DepartureICAO) ? "flight" : $"{flight.DepartureICAO} to {flight.ArrivalICAO}")}"
            : "No flight being tracked";
        _tray.SetTooltip($"Aviates Air Tracker\n{status}");
    }
}
