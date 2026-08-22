using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AviatesAirTracker.Platform;

/// <summary>
/// WinUI file picking. Unlike WPF's dialogs, <see cref="FileOpenPicker"/> must be
/// associated with a window handle in an unpackaged app or it throws at show time.
/// </summary>
public sealed class FilePickerService
{
    private nint _hwnd;

    public void SetWindowHandle(nint hwnd) => _hwnd = hwnd;

    public async Task<string?> PickSingleFileAsync(params string[] fileTypes)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _hwnd);

        foreach (var type in fileTypes) picker.FileTypeFilter.Add(type);
        if (fileTypes.Length == 0) picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
