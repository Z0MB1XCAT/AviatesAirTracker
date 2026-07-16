namespace AviatesAirTracker.Services;

// Maps a browser KeyboardEventArgs.Code (stable, layout-independent) to its
// Win32 virtual-key code, for the subset of keys realistic as a PTT bind.
// BlazorWebView is WebView2 (Chromium), so DOM `code` values are the
// standard UI Events KeyboardEvent.code strings, captured directly in
// Settings.razor via @onkeydown — no JS interop needed for this.
public static class PttKeyMap
{
    private static readonly Dictionary<string, int> Map = new()
    {
        ["ControlLeft"] = 0xA2, ["ControlRight"] = 0xA3,
        ["AltLeft"] = 0xA4, ["AltRight"] = 0xA5,
        ["ShiftLeft"] = 0xA0, ["ShiftRight"] = 0xA1,
        ["Space"] = 0x20,
        ["CapsLock"] = 0x14,
        ["Backquote"] = 0xC0,
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73,
        ["F5"] = 0x74, ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77,
        ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
        ["KeyA"] = 0x41, ["KeyB"] = 0x42, ["KeyC"] = 0x43, ["KeyD"] = 0x44,
        ["KeyE"] = 0x45, ["KeyF"] = 0x46, ["KeyG"] = 0x47, ["KeyH"] = 0x48,
        ["KeyI"] = 0x49, ["KeyJ"] = 0x4A, ["KeyK"] = 0x4B, ["KeyL"] = 0x4C,
        ["KeyM"] = 0x4D, ["KeyN"] = 0x4E, ["KeyO"] = 0x4F, ["KeyP"] = 0x50,
        ["KeyQ"] = 0x51, ["KeyR"] = 0x52, ["KeyS"] = 0x53, ["KeyT"] = 0x54,
        ["KeyU"] = 0x55, ["KeyV"] = 0x56, ["KeyW"] = 0x57, ["KeyX"] = 0x58,
        ["KeyY"] = 0x59, ["KeyZ"] = 0x5A,
        ["Digit0"] = 0x30, ["Digit1"] = 0x31, ["Digit2"] = 0x32, ["Digit3"] = 0x33,
        ["Digit4"] = 0x34, ["Digit5"] = 0x35, ["Digit6"] = 0x36, ["Digit7"] = 0x37,
        ["Digit8"] = 0x38, ["Digit9"] = 0x39,
        ["Numpad0"] = 0x60, ["Numpad1"] = 0x61, ["Numpad2"] = 0x62, ["Numpad3"] = 0x63,
        ["Numpad4"] = 0x64, ["Numpad5"] = 0x65, ["Numpad6"] = 0x66, ["Numpad7"] = 0x67,
        ["Numpad8"] = 0x68, ["Numpad9"] = 0x69,
        // Punctuation/OEM keys — no good reason to exclude these from being
        // bindable; they're all easily-reachable single keys just like the
        // letters/digits above.
        ["Minus"] = 0xBD, ["Equal"] = 0xBB,
        ["BracketLeft"] = 0xDB, ["BracketRight"] = 0xDD,
        ["Backslash"] = 0xDC, ["Semicolon"] = 0xBA, ["Quote"] = 0xDE,
        ["Comma"] = 0xBC, ["Period"] = 0xBE, ["Slash"] = 0xBF,
        // Navigation/editing keys — common, low-conflict PTT choices.
        ["ArrowUp"] = 0x26, ["ArrowDown"] = 0x28, ["ArrowLeft"] = 0x25, ["ArrowRight"] = 0x27,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Enter"] = 0x0D, ["NumpadEnter"] = 0x0D,
    };

    public static int? ToVirtualKey(string domCode) => Map.TryGetValue(domCode, out var vk) ? vk : null;

    public static string Describe(string domCode) => domCode switch
    {
        "ControlLeft" => "Left Ctrl", "ControlRight" => "Right Ctrl",
        "AltLeft" => "Left Alt", "AltRight" => "Right Alt",
        "ShiftLeft" => "Left Shift", "ShiftRight" => "Right Shift",
        "Space" => "Space", "CapsLock" => "Caps Lock", "Tab" => "Tab", "Backquote" => "`",
        "Minus" => "-", "Equal" => "=", "BracketLeft" => "[", "BracketRight" => "]",
        "Backslash" => "\\", "Semicolon" => ";", "Quote" => "'",
        "Comma" => ",", "Period" => ".", "Slash" => "/",
        "ArrowUp" => "Up Arrow", "ArrowDown" => "Down Arrow", "ArrowLeft" => "Left Arrow", "ArrowRight" => "Right Arrow",
        "Insert" => "Insert", "Delete" => "Delete", "Home" => "Home", "End" => "End",
        "PageUp" => "Page Up", "PageDown" => "Page Down",
        "Enter" => "Enter", "NumpadEnter" => "Numpad Enter",
        _ when domCode.StartsWith("Key") => domCode[3..],
        _ when domCode.StartsWith("Digit") => domCode[5..],
        _ when domCode.StartsWith("Numpad") => "Numpad " + domCode[6..],
        _ => domCode,
    };
}
