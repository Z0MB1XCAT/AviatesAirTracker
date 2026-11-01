using AviatesAirTracker.Platform;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace AviatesAirTracker.Pages;

/// <summary>
/// Building blocks for pages whose content is built in code (Landing Analysis, Statistics),
/// matching the hand-written XAML pages: the same surfaces, borders, type and colours. Lives in
/// code because this SDK's XamlCompiler can't take a Style element.
/// </summary>
internal static class UiKit
{
    public static readonly Color TextColor = Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9);
    public static readonly Color SecondaryColor = Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1);
    public static readonly Color MutedColor = Color.FromArgb(0xFF, 0x64, 0x74, 0x8B);
    public static readonly Color AccentColor = Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC);
    public static readonly Color GreenColor = Color.FromArgb(0xFF, 0x3E, 0xC7, 0xAC);
    public static readonly Color AmberColor = Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B);
    public static readonly Color RedColor = Color.FromArgb(0xFF, 0xEF, 0x44, 0x44);

    public static readonly SolidColorBrush Text = new(TextColor);
    public static readonly SolidColorBrush Secondary = new(SecondaryColor);
    public static readonly SolidColorBrush Muted = new(MutedColor);
    public static readonly SolidColorBrush Accent = new(AccentColor);
    public static readonly SolidColorBrush Surface = new(Color.FromArgb(0xFF, 0x0C, 0x11, 0x1D));
    public static readonly SolidColorBrush SurfaceRaised = new(Color.FromArgb(0xFF, 0x12, 0x1A, 0x2A));
    public static readonly SolidColorBrush Hairline = new(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    public static readonly SolidColorBrush Divider = new(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));

    public static FontFamily Display => (FontFamily)Application.Current.Resources["DisplayFontBold"];

    public static Color Hex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        var v = Convert.ToUInt32(hex, 16);
        return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static SolidColorBrush Brush(Color c, byte alpha) => new(Color.FromArgb(alpha, c.R, c.G, c.B));

    public static Border Card(UIElement child, double padding = 20) => new()
    {
        Background = Surface,
        BorderBrush = Hairline,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(20),
        Padding = new Thickness(padding),
        Child = child,
    };

    public static TextBlock Heading(string text, double size = 16) => new()
    {
        Text = text,
        FontFamily = Display,
        FontSize = size,
        CharacterSpacing = size >= 22 ? -30 : 0,
        Foreground = Text,
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Body(string text, Brush? brush = null, double size = 13) => new()
    {
        Text = text,
        FontSize = size,
        LineHeight = size * 1.55,
        Foreground = brush ?? Secondary,
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = Muted,
    };

    /// <summary>A figure with its label underneath.</summary>
    public static StackPanel Stat(string value, string label, Brush? valueBrush = null, double size = 24) => new()
    {
        Spacing = 2,
        Children =
        {
            new TextBlock { Text = value, FontFamily = Display, FontSize = size, Foreground = valueBrush ?? Text },
            new TextBlock { Text = label, FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap },
        },
    };

    public static Border Pill(string text, Color color) => new()
    {
        Background = Brush(color, 0x26),
        CornerRadius = new CornerRadius(999),
        Padding = new Thickness(10, 3, 10, 4),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color) },
    };

    public static FontIcon Icon(string glyph, Color color, double size = 14) => new()
    {
        FontFamily = new FontFamily("Segoe Fluent Icons"),
        Glyph = glyph,
        FontSize = size,
        Foreground = new SolidColorBrush(color),
    };

    /// <summary>
    /// Turns any element into a button: click, Enter or Space runs <paramref name="onActivate"/>,
    /// it takes keyboard focus with the system focus ring, and screen readers read
    /// <paramref name="name"/>.
    /// </summary>
    public static T Activatable<T>(T element, Action onActivate, string name, bool effects = true) where T : UIElement
    {
        element.IsTabStop = true;
        element.UseSystemFocusVisuals = true;
        AutomationProperties.SetName(element, name);
        element.Tapped += (_, e) => { e.Handled = true; onActivate(); };
        element.KeyDown += (_, e) =>
        {
            if (e.Key is VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA)
            {
                e.Handled = true;
                onActivate();
            }
        };
        if (effects) InteractiveEffects.Attach(element);
        return element;
    }

    public static Grid Columns(double spacing, params GridLength[] widths)
    {
        var g = new Grid { ColumnSpacing = spacing };
        foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        return g;
    }

    public static T At<T>(this T element, int column, int row = 0) where T : FrameworkElement
    {
        Grid.SetColumn(element, column);
        Grid.SetRow(element, row);
        return element;
    }

    public static GridLength Star(double v = 1) => new(v, GridUnitType.Star);
    public static GridLength Px(double v) => new(v);
    public static readonly GridLength Auto = GridLength.Auto;

    /// <summary>A horizontal progress bar filled to <paramref name="fraction"/> (0..1).</summary>
    public static Grid Bar(double fraction, Color color, double height = 6)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        var track = new Grid { Height = height, CornerRadius = new CornerRadius(height / 2), Background = Brush(Colors.White, 0x12) };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(Math.Max(0.0001, fraction)) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(Math.Max(0.0001, 1 - fraction)) });
        track.Children.Add(new Border { Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(height / 2) });
        return track;
    }
}
