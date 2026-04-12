using System.Globalization;
using System.Windows.Data;

namespace AviatesAirTracker.Converters;

// ============================================================
// BOOL → PLAY/PAUSE TEXT
// ============================================================
public class BoolToPlayPauseTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "⏸" : "▶";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ============================================================
// DOUBLE (seconds) → TIME STRING
// ============================================================
public class SecondsToTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double secs)
            return TimeSpan.FromSeconds(secs).ToString(@"hh\:mm\:ss");
        return "00:00:00";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ============================================================
// BOOL → INVERTED VISIBILITY (Collapsed when true)
// ============================================================
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
