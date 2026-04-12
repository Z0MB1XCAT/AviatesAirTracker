using AviatesAirTracker.Core.SimConnect;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AviatesAirTracker.Converters;

// ============================================================
// STRING TO COLOR CONVERTER
// Converts "#RRGGBB" hex strings to WPF Color
// ============================================================
public class StringToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrEmpty(hex))
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(hex);
            }
            catch { }
        }
        return Colors.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ============================================================
// BOOL TO VISIBILITY CONVERTER
// ============================================================
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

// ============================================================
// BOOL TO NAV BUTTON STYLE CONVERTER
// Returns active/inactive nav button style based on bool
// ============================================================
public class BoolToNavStyleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isActive = value is true;
        var key = isActive ? "AviatesNavButton_Active" : "AviatesNavButton";
        return Application.Current.Resources[key] as Style ?? new Style();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ============================================================
// DOUBLE TO WIDTH CONVERTER (for score bars)
// ============================================================
public class PctToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2 && values[0] is double pct && values[1] is double totalWidth)
            return totalWidth * pct / 100.0;
        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ============================================================
// LANDING SCORE TO COLOR CONVERTER
// ============================================================
public class LandingScoreToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int score)
        {
            var hex = score switch
            {
                >= 90 => "#22C55E",
                >= 75 => "#3D7EEE",
                >= 60 => "#EAB308",
                >= 40 => "#F97316",
                _ => "#EF4444"
            };
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ============================================================
// FLIGHT PHASE TO COLOR CONVERTER
// ============================================================
public class FlightPhaseToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Core.SimConnect.FlightPhase phase)
        {
            var hex = phase switch
            {
                Core.SimConnect.FlightPhase.Cruise => "#22C55E",
                Core.SimConnect.FlightPhase.Climb or
                Core.SimConnect.FlightPhase.InitialClimb => "#3D7EEE",
                Core.SimConnect.FlightPhase.Approach or
                Core.SimConnect.FlightPhase.FinalApproach => "#F97316",
                Core.SimConnect.FlightPhase.Landing => "#EF4444",
                Core.SimConnect.FlightPhase.Descent or
                Core.SimConnect.FlightPhase.TopOfDescent => "#EAB308",
                Core.SimConnect.FlightPhase.Takeoff => "#8B5CF6",
                _ => "#4A5568"
            };
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ============================================================
// NULL / EMPTY STRING TO VISIBILITY CONVERTER
// ============================================================
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s) return s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        return value != null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ============================================================
// NEGATIVE VS COLOR CONVERTER
// ============================================================
public class VSColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double vs)
        {
            var hex = vs switch
            {
                < -1500 => "#EF4444",
                < -800  => "#F97316",
                > 3000  => "#EAB308",
                _       => "#F0F4FF"
            };
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        return Brushes.White;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
