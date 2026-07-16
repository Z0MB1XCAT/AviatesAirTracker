using AviatesAirTracker.ViewModels;
using Microsoft.Web.WebView2.Core;
using Serilog;
using System.Windows;
using System.Windows.Interop;

namespace AviatesAirTracker;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _viewModel.SetWindowHandle(hwnd);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

        // Voice PTT needs getUserMedia() inside the Blazor page's WebView2
        // context. WebView2 blocks microphone access by default; without
        // this handler, getUserMedia() would hang waiting on a permission
        // prompt the user has no way to see or answer.
        try
        {
            await BlazorHost.WebView.EnsureCoreWebView2Async();
            BlazorHost.WebView.CoreWebView2.PermissionRequested += OnPermissionRequested;

            // WebView2 intercepts a set of "browser accelerator" keys (F3,
            // F5-F7, F10, F11, Alt, Ctrl+F/G/H/J/N/O/P/R/S/Y/Z, browser
            // back/forward, etc.) before they ever reach the page's DOM —
            // by design, since a normal browser would use them for its own
            // chrome. There's no chrome here, and the PTT hotkey capture
            // field in Settings needs to see every key the pilot presses,
            // so this app owns that entirely; disable WebView2's own
            // handling of them.
            BlazorHost.WebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to wire microphone auto-approve / disable accelerator keys for voice PTT; some keys may not register in Settings");
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
