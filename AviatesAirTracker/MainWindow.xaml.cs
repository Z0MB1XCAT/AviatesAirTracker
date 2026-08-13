using AviatesAirTracker.ViewModels;
using Microsoft.Web.WebView2.Core;
using Serilog;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;

namespace AviatesAirTracker;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    /// <summary>Set once the UI host has been torn down, so the deferred close can proceed.</summary>
    private bool _readyToClose;
    /// <summary>Guards against a second close request re-entering teardown while it is in flight.</summary>
    private bool _teardownStarted;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _viewModel.SetWindowHandle(hwnd);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

        try
        {
            await UiHost.EnsureCoreWebView2Async();

            // Voice PTT needs getUserMedia() inside the Blazor page's WebView2
            // context. WebView2 blocks microphone access by default; without
            // this handler, getUserMedia() would hang waiting on a permission
            // prompt the user has no way to see or answer.
            UiHost.CoreWebView2.PermissionRequested += OnPermissionRequested;

            // Deliberately NOT setting AreBrowserAcceleratorKeysEnabled = false
            // here. That was an earlier attempt at fixing "PTT hotkey capture
            // never detects the key" — turned out the real cause was a
            // too-narrow key map (missing punctuation), not WebView2
            // intercepting keys. Disabling accelerator keys also silently
            // disables F12/Ctrl+Shift+I DevTools shortcuts, which made this
            // app much harder to debug for no actual benefit — not worth it.
        }
        catch (Exception ex)
        {
            // The WebView2 IS the UI now — there is no Hybrid fallback behind it, so this
            // is fatal rather than the warning it used to be (missing WebView2 Runtime,
            // or an unwritable user-data folder, are the usual causes).
            Log.Error(ex, "[UI] WebView2 failed to initialise — cannot render the UI");
            FailFast("The application could not start its embedded browser (WebView2).\n\n" +
                     $"{ex.GetType().Name}: {ex.Message}\n\n" +
                     "Installing or repairing the Microsoft Edge WebView2 Runtime usually fixes this.");
            return;
        }

        // Set by App.OnStartup from the loopback host's bound address. App shuts down when
        // the host fails to start, so this should already be non-null — the check exists so
        // a regression there surfaces as a message rather than a NullReferenceException.
        var url = App.LegacyUiUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            Log.Error("[UI] No UI host URL available — App.LegacyUiUrl is null");
            FailFast("The application could not determine the address of its user interface host.");
            return;
        }

        Log.Information("[UI] Navigating the UI WebView2 to {Url}", url);
        UiHost.Source = new Uri(url);
    }

    /// <summary>Reports an unrecoverable UI-startup failure to the user and ends the process.</summary>
    private static void FailFast(string message)
    {
        MessageBox.Show(
            message + "\n\nThe application will now close.",
            "Aviates Air Tracker — user interface failed to start",
            MessageBoxButton.OK, MessageBoxImage.Error);
        Application.Current?.Shutdown(1);
    }

    /// <summary>
    /// Tears the UI host down here rather than leaving it all to <c>App.OnExit</c>.
    /// </summary>
    /// <remarks>
    /// Blazor Server circuit teardown can marshal onto the shared <c>IUiDispatcher</c>, which is
    /// this thread's WPF dispatcher. OnExit cannot await, so it blocks the dispatcher thread on
    /// disposal — at which point that thread is no longer pumping and any such marshalled call
    /// deadlocks (bounded only by the 5 s backstop inside <c>BlazorHostServer.DisposeAsync</c>).
    ///
    /// Closing is early enough to avoid that, but ONLY if the handler does not block either.
    /// So the first close is cancelled, the window hidden so shutdown still feels immediate,
    /// and teardown awaited — the dispatcher keeps pumping throughout, so circuit teardown can
    /// complete. Once it finishes, Close() is called again and the second pass lets it through.
    ///
    /// OnExit still disposes as a safety net for paths that bypass this (notably
    /// Application.Shutdown(), which force-closes windows and ignores Cancel); DisposeAsync
    /// nulls its host field up front, so the second call is a no-op.
    /// </remarks>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_readyToClose) return;

        e.Cancel = true;
        if (_teardownStarted) return;

        _teardownStarted = true;
        Hide();
        _ = TeardownThenCloseAsync();
    }

    private async Task TeardownThenCloseAsync()
    {
        try
        {
            // No ConfigureAwait(false): the continuation MUST come back to the dispatcher
            // thread, both because Close() is thread-affine and because the whole point is
            // that this thread stays free to service teardown's marshalled callbacks.
            await App.ShutdownUiHostAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[UI] UI host teardown on window close failed (non-critical)");
        }
        finally
        {
            _readyToClose = true;
            Close();
        }
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
        {
            e.State = CoreWebView2PermissionState.Allow;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        _viewModel.OnWindowMessage(hwnd, msg, wParam, lParam);
        return IntPtr.Zero;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) MaximizeWindow(sender, e);
        else DragMove();
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
