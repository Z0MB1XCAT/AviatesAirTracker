namespace AviatesAirTracker.Platform;

/// <summary>A window rectangle in physical pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// Where the shell window opens. Pure (no WinUI types) so it is unit-tested: the window used to
/// open at a fixed 1440×900 every launch, which is taller and wider than many laptop screens.
/// </summary>
public static class WindowPlacementLogic
{
    public const int DefaultWidth = 1440;
    public const int DefaultHeight = 900;
    public const int MinWidth = 1000;
    public const int MinHeight = 640;

    /// <summary>
    /// The saved bounds when they still fit on the monitor they were on, nudged inside its work
    /// area; otherwise the default size (shrunk to 92% of a small screen), centred.
    /// </summary>
    public static PixelRect Fit(PixelRect? saved, PixelRect workArea)
    {
        if (saved is { Width: > 0, Height: > 0 } s)
        {
            int w = Math.Clamp(s.Width, Math.Min(MinWidth, workArea.Width), workArea.Width);
            int h = Math.Clamp(s.Height, Math.Min(MinHeight, workArea.Height), workArea.Height);
            int x = Math.Clamp(s.X, workArea.X, workArea.Right - w);
            int y = Math.Clamp(s.Y, workArea.Y, workArea.Bottom - h);
            return new PixelRect(x, y, w, h);
        }

        int dw = Math.Min(DefaultWidth, (int)(workArea.Width * 0.92));
        int dh = Math.Min(DefaultHeight, (int)(workArea.Height * 0.92));
        return new PixelRect(
            workArea.X + (workArea.Width - dw) / 2,
            workArea.Y + (workArea.Height - dh) / 2,
            dw, dh);
    }
}
