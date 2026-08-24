using AviatesAirTracker.Core.Analytics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Globalization;
using System.IO;
using Windows.UI;

namespace AviatesAirTracker.Dialogs;

// ============================================================
// Native conversion of LandingResultModal.razor -- the global "touchdown report" overlay that
// pops up automatically after every landing (see docs/superpowers/specs/2026-08-23-winui3-
// phase3-landing-dialog-design.md). This is the first SHELL-level native UI in this migration:
// it's a plain static builder, not a Page, so ShellWindow.xaml.cs (Task 3) supplies the
// XamlRoot from its own RootGrid element rather than this class resolving one itself.
//
// The original modal is a deliberately LIGHT card (white background, dark text) distinct from
// the rest of this app's dark shell theme -- that is reproduced as-is, not adapted to the dark
// palette, because it is a genuine design choice in the source, not an oversight.
//
// Built from LandingReportBuilder.Build(...) directly -- LandingAnalysisViewModel is dead/stale
// code (unused, recomputes a different/simpler field set) and must not be used, per the design
// spec's explicit finding.
// ============================================================

public static class LandingResultDialog
{
    private const double CardWidth = 470;

    // ── Colors, verified directly against LandingResultModal.razor.css ───────────────────────
    private static readonly SolidColorBrush CardBackgroundBrush = new(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush TextPrimaryBrush = new(Color.FromArgb(0xFF, 0x0B, 0x16, 0x28));   // #0B1628
    private static readonly SolidColorBrush TextMutedBrush = new(Color.FromArgb(0xFF, 0x94, 0xA3, 0xB8));     // #94A3B8
    private static readonly SolidColorBrush TextSubBrush = new(Color.FromArgb(0xFF, 0x64, 0x74, 0x8B));       // #64748B (lrm-close-x / lrm-flag color)
    private static readonly SolidColorBrush DotBrush = new(Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1));           // #CBD5E1
    private static readonly SolidColorBrush ParamGridLineBrush = new(Color.FromArgb(0xFF, 0xEC, 0xF0, 0xFB)); // #ECF0FB
    private static readonly SolidColorBrush ParamCellBrush = new(Color.FromArgb(0xFF, 0xFA, 0xFB, 0xFF));     // #FAFBFF

    // Corrected against the real CSS (rgba(249, 115, 22, 0.06) / #EA580C) -- NOT red, despite this
    // project's other warning/danger tints using red. 0.06 * 255 ≈ 15 (0x0F).
    private static readonly SolidColorBrush ParamWarnBrush = new(Color.FromArgb(0x0F, 0xF9, 0x73, 0x16));
    private static readonly SolidColorBrush ParamWarnValueBrush = new(Color.FromArgb(0xFF, 0xEA, 0x58, 0x0C)); // #EA580C

    public static ContentDialog Build(XamlRoot xamlRoot, LandingReport report)
    {
        var contentStack = new StackPanel { Spacing = 0 };
        contentStack.Children.Add(BuildHeader(report));
        // Task 2 adds the hero row (ring + grade badge/butter meter/quality flags) and the
        // runway-dash divider here, between the header and the params grid.
        contentStack.Children.Add(BuildParamsGrid(report));
        // Task 2 adds the score-breakdown bars here, between the params grid and the footer.
        contentStack.Children.Add(BuildFooter(report));

        // A Grid layers the (optional) decorative aircraft photo behind the real content, the
        // same z-order as .lrm-photo (opacity 0.15, inset:0) sitting under .lrm-card's children.
        var layers = new Grid { Width = CardWidth };
        var photo = BuildPhotoOrNull(report);
        if (photo != null)
        {
            layers.Children.Add(photo);
        }
        layers.Children.Add(contentStack);

        var card = new Border
        {
            Background = CardBackgroundBrush,
            CornerRadius = new CornerRadius(18),
            Child = layers,
        };

        return new ContentDialog
        {
            XamlRoot = xamlRoot,
            Content = card,
            CloseButtonText = "Close Report",
        };
    }

    // ── Header: report chip, airport/runway, flight sub-line, Zulu time, grade-colored top
    //    accent border (a thin border, not a background tint, matching .lrm-header's
    //    `border-top: 3.5px solid <GradeColor>`). ────────────────────────────────────────────
    private static Border BuildHeader(LandingReport report)
    {
        var chip = new TextBlock
        {
            Text = "TOUCHDOWN REPORT",
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextMutedBrush,
            CharacterSpacing = 130, // ~0.13em
        };

        var airportRow = new TextBlock
        {
            FontFamily = (FontFamily)Application.Current.Resources["DisplayFontBold"],
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = TextPrimaryBrush,
        };
        airportRow.Inlines.Add(new Run { Text = report.AirportIcao });
        airportRow.Inlines.Add(new Run { Text = "  ·  ", Foreground = DotBrush });
        airportRow.Inlines.Add(new Run { Text = report.Runway });

        var headerLeft = new StackPanel { Spacing = 5, Children = { chip, airportRow } };

        // Matches the original's exact conditional: the sub-line only exists at all when
        // FlightNumber or Route is non-empty; AircraftType only tags along if also non-empty.
        bool hasSub = !string.IsNullOrEmpty(report.FlightNumber) || !string.IsNullOrEmpty(report.Route);
        if (hasSub)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(report.FlightNumber)) parts.Add(report.FlightNumber);
            if (!string.IsNullOrEmpty(report.Route)) parts.Add(report.Route);
            if (!string.IsNullOrEmpty(report.AircraftType)) parts.Add(report.AircraftType);

            headerLeft.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", parts),
                FontSize = 12.5,
                Foreground = TextSubBrush,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var zuluText = new TextBlock
        {
            // report.TimestampUtc is already UTC (per LandingReportBuilder), unlike the original
            // modal's own _result.Timestamp.ToUniversalTime() -- no second conversion needed.
            Text = report.TimestampUtc.ToString("HH:mm", CultureInfo.InvariantCulture) + "Z",
            FontSize = 12,
            Foreground = TextMutedBrush,
        };

        var headerRow = new Grid();
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(headerLeft, 0);
        Grid.SetColumn(zuluText, 1);
        headerRow.Children.Add(headerLeft);
        headerRow.Children.Add(zuluText);

        return new Border
        {
            BorderBrush = HexToBrush(report.GradeColor),
            BorderThickness = new Thickness(0, 3.5, 0, 0),
            CornerRadius = new CornerRadius(18, 18, 0, 0),
            Padding = new Thickness(20, 20, 20, 14),
            Child = headerRow,
        };
    }

    // ── Touchdown parameters: 6 cells (VS, IAS, pitch, bank, headwind, crosswind), 3x2 grid,
    //    with the VS/bank warning-threshold cells getting the orange tint. ───────────────────
    private static Border BuildParamsGrid(LandingReport report)
    {
        bool vsWarn = report.VerticalSpeedFpm > 600;
        bool bankWarn = report.BankDeg > 3;

        var cells = new (string Value, string Unit, string Label, bool Warn)[]
        {
            (report.VerticalSpeedFpm.ToString("F0", CultureInfo.InvariantCulture), "fpm", "VERT SPEED", vsWarn),
            (report.IasKts.ToString("F0", CultureInfo.InvariantCulture), "kt", "AIRSPEED", false),
            (report.PitchDeg.ToString("F1", CultureInfo.InvariantCulture), "°", "PITCH", false),
            (report.BankDeg.ToString("F1", CultureInfo.InvariantCulture), "°", "BANK", bankWarn),
            (report.HeadwindKts.ToString("F0", CultureInfo.InvariantCulture), "kt", "HEADWIND", false),
            (report.CrosswindKts.ToString("F0", CultureInfo.InvariantCulture), "kt", "CROSSWIND", false),
        };

        var grid = new Grid();
        for (int c = 0; c < 3; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < 2; r++) grid.RowDefinitions.Add(new RowDefinition());

        for (int i = 0; i < cells.Length; i++)
        {
            var cell = BuildParamCell(cells[i].Value, cells[i].Unit, cells[i].Label, cells[i].Warn);
            Grid.SetRow(cell, i / 3);
            Grid.SetColumn(cell, i % 3);
            grid.Children.Add(cell);
        }

        return new Border
        {
            Background = ParamGridLineBrush,
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(20, 0, 20, 18),
            Child = grid,
        };
    }

    private static Border BuildParamCell(string value, string unit, string label, bool warn)
    {
        var stack = new StackPanel
        {
            Spacing = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = value,
                    FontFamily = (FontFamily)Application.Current.Resources["DisplayFontBold"],
                    FontSize = 21,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = warn ? ParamWarnValueBrush : TextPrimaryBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = unit,
                    FontSize = 10,
                    Foreground = TextMutedBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = label,
                    FontSize = 9,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = DotBrush,
                    CharacterSpacing = 100,
                    Margin = new Thickness(0, 4, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            },
        };

        // Gap between cells is simulated by a small margin exposing the grid's own background
        // color underneath, the same visual effect as the CSS grid's `gap: 1px` on a tinted
        // background.
        return new Border
        {
            Background = warn ? ParamWarnBrush : ParamCellBrush,
            Padding = new Thickness(12, 11, 12, 10),
            Margin = new Thickness(0.5),
            Child = stack,
        };
    }

    // ── Footer stats: rollout/threshold/wind conditionally shown, fuel always shown. Close is
    //    handled by ContentDialog's own CloseButtonText, not a duplicate custom button. ───────
    private static Border BuildFooter(LandingReport report)
    {
        var footerStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };

        if (report.RolloutFt > 10)
        {
            footerStack.Children.Add(BuildFooterStat(report.RolloutFt.ToString("F0", CultureInfo.InvariantCulture), "ft rollout"));
        }
        if (report.ThresholdFt > 10)
        {
            footerStack.Children.Add(BuildFooterStat(report.ThresholdFt.ToString("F0", CultureInfo.InvariantCulture), "ft past threshold"));
        }
        if (report.WindSpeedKts > 0)
        {
            var windText = report.WindDirectionDeg.ToString("F0", CultureInfo.InvariantCulture) + "°/" +
                           report.WindSpeedKts.ToString("F0", CultureInfo.InvariantCulture);
            footerStack.Children.Add(BuildFooterStat(windText, "surface wind"));
        }
        footerStack.Children.Add(BuildFooterStat(report.FuelLbs.ToString("F0", CultureInfo.InvariantCulture), "lbs fuel"));

        return new Border
        {
            BorderBrush = ParamGridLineBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 14, 20, 20),
            Child = footerStack,
        };
    }

    private static StackPanel BuildFooterStat(string value, string label) => new()
    {
        Spacing = 1,
        Children =
        {
            new TextBlock
            {
                Text = value,
                FontFamily = (FontFamily)Application.Current.Resources["DisplayFontBold"],
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = TextPrimaryBrush,
            },
            new TextBlock { Text = label, FontSize = 10, Foreground = TextMutedBrush },
        },
    };

    // ── Aircraft photo: nullable wwwroot-relative path, loaded from AppContext.BaseDirectory the
    //    same way DashboardPage.xaml.cs loads hero.webp (wwwroot ships as loose Content next to
    //    the exe, not a packaged ms-appx:// asset). Missing file or null path -> no photo, no
    //    crash, no placeholder -- matching the original's own `@if (!string.IsNullOrEmpty(...))`. ─
    private static Image? BuildPhotoOrNull(LandingReport report)
    {
        if (string.IsNullOrEmpty(report.AircraftImagePath)) return null;

        var photoPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", report.AircraftImagePath);
        if (!File.Exists(photoPath)) return null;

        return new Image
        {
            Source = new BitmapImage(new Uri(photoPath)),
            Stretch = Stretch.UniformToFill,
            Opacity = 0.15,
            IsHitTestVisible = false,
        };
    }

    // ── Shared hex-string -> SolidColorBrush helper. LandingReport hands every color out as a
    //    hardcoded "#RRGGBB" string (GradeColor, ButterColor, per-factor Color) -- no existing
    //    helper for this in the codebase yet, so this is the first one; Task 2 reuses it. ──────
    private static SolidColorBrush HexToBrush(string hex)
    {
        hex = hex.TrimStart('#');
        byte r = Convert.ToByte(hex.Substring(0, 2), 16);
        byte g = Convert.ToByte(hex.Substring(2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(4, 2), 16);
        return new SolidColorBrush(Color.FromArgb(0xFF, r, g, b));
    }
}
