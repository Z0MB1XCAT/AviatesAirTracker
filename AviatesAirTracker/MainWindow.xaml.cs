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
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to wire microphone auto-approve for voice PTT; mic prompts will use default WebView2 behavior");
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
