using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
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

    // ── Task 2 colors. `.lrm-flag` (scoped CSS) sets its own base text color #475569 -- a
    //    DIFFERENT color from TextSubBrush's #64748B above (that one is confirmed only for
    //    .lrm-close-x). Header's sub-line color mismatch (found here, fixed in BuildHeader). ────
    private static readonly SolidColorBrush FlagTextBrush = new(Color.FromArgb(0xFF, 0x47, 0x55, 0x69)); // #475569

    // flag-ok / flag-fail / flag-neutral pip colors, scoped CSS: rgba(...,.12)/.15 backgrounds
    // behind a solid pip glyph color.
    private static readonly SolidColorBrush FlagOkPipBg = new(Color.FromArgb(0x1F, 0x22, 0xC5, 0x5E));   // rgba(34,197,94,.12)
    private static readonly SolidColorBrush FlagOkPipText = new(Color.FromArgb(0xFF, 0x16, 0xA3, 0x4A)); // #16A34A
    private static readonly SolidColorBrush FlagFailPipBg = new(Color.FromArgb(0x1F, 0xEF, 0x44, 0x44));  // rgba(239,68,68,.12)
    private static readonly SolidColorBrush FlagFailPipText = new(Color.FromArgb(0xFF, 0xDC, 0x26, 0x26)); // #DC2626
    private static readonly SolidColorBrush FlagNeutralPipBg = new(Color.FromArgb(0x26, 0x94, 0xA3, 0xB8)); // rgba(148,163,184,.15)

    // flag-best is NOT in the scoped CSS at all -- it lives in the global app.css
    // (".lrm-flag.flag-best") and, unlike ok/fail/neutral, tints the WHOLE chip (border +
    // background), not just the pip; that rule also sets no border-radius, so this stays a
    // square-cornered chip rather than a pill, matching the source exactly.
    private static readonly SolidColorBrush FlagBestText = new(Color.FromArgb(0xFF, 0xEA, 0xB3, 0x08));   // #EAB308
    private static readonly SolidColorBrush FlagBestBorder = new(Color.FromArgb(0x73, 0xEA, 0xB3, 0x08)); // rgba(234,179,8,.45)
    private static readonly SolidColorBrush FlagBestBg = new(Color.FromArgb(0x1F, 0xEA, 0xB3, 0x08));     // rgba(234,179,8,.12)

    // Score ring: r=80, stroke-width=10, confirmed directly against the scoped CSS's
    // `.ring-track`/`.ring-fill` (differs from both Dashboard's r=45/sw=8 and Pilot Hub's
    // r=65/sw=14). Unlike those two rings -- whose SVGs render 1:1 with their own viewBox
    // (Dashboard 100x100, Pilot Hub 160x160) -- this modal's SVG has viewBox="0 0 200 200" but is
    // rendered at a CSS-scaled-down 138x138 (`.lrm-ring-wrap`). BuildHero wraps this method's
    // return value in a Viewbox targeting that same 138x138 to match, so these constants can stay
    // the literal CSS r/stroke-width values without needing to be hand-rescaled themselves.
    private const double RingRadius = 80.0;
    private const double RingStrokeWidth = 10.0;
    private const double RingDiameter = RingRadius * 2;
    private const double RingGridSize = RingDiameter + RingStrokeWidth * 2; // 180: same "diameter + stroke headroom" idiom Dashboard/Pilot Hub used
    private const double RingCenter = RingGridSize / 2;

    // Score-breakdown bar row geometry -- same fixed-width-track idiom as PilotHubPage.xaml.cs's
    // RankProgressTrack/RankProgressFill (Width computed once in code, no live transition; a
    // Star-width Grid column can't be measured synchronously at construction time the way a
    // literal pixel Width can). Chosen to fit CardWidth (470) minus its 20px side padding, the
    // label column (matching f-label's CSS width:118px), and the points column.
    private const double FactorLabelWidth = 118.0;
    private const double FactorTrackWidth = 260.0;
    private const double FactorPtsWidth = 42.0;

    public static ContentDialog Build(XamlRoot xamlRoot, LandingReport report)
    {
        var contentStack = new StackPanel { Spacing = 0 };
        contentStack.Children.Add(BuildHeader(report));
        contentStack.Children.Add(BuildHero(report));
        contentStack.Children.Add(BuildRunwayDivider());
        contentStack.Children.Add(BuildParamsGrid(report));
        contentStack.Children.Add(BuildBreakdown(report));
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
                // .lrm-sub has no rule of its own in the scoped CSS -- app.css resolves it to
                // var(--text-muted), i.e. TextMutedBrush, not TextSubBrush (which is confirmed
                // only for .lrm-close-x/.lrm-flag).
                Foreground = TextMutedBrush,
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

    // ── Hero row: score ring (left) + grade badge/butter meter/quality flags column (right),
    //    matching `.lrm-hero`'s flex row. ──────────────────────────────────────────────────────
    private static Grid BuildHero(LandingReport report)
    {
        var hero = new Grid { Margin = new Thickness(20, 6, 20, 0) };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // The original SVG's viewBox="0 0 200 200" ring is CSS-scaled down to a rendered
        // 138x138 (`.lrm-ring-wrap { width:138px; height:138px }`). BuildRing's own logical
        // bounding box (RingGridSize=180, i.e. diameter+stroke headroom) doesn't need to match
        // 200 for this to work: wrapping it in a Viewbox targeting the real 138x138 render size
        // scales whatever BuildRing produces down to the correct on-screen footprint, the same
        // technique already used elsewhere in this migration (Dashboard/PilotHub/Flights all
        // wrap their small SVG-path icon reproductions in a Viewbox) rather than hand-rescaling
        // every ring constant to hit 138 directly.
        var ring = new Viewbox { Width = 138, Height = 138, Child = BuildRing(report) };
        Grid.SetColumn(ring, 0);

        var gradeColumn = BuildGradeColumn(report);
        Grid.SetColumn(gradeColumn, 1);
        gradeColumn.Margin = new Thickness(22, 0, 0, 0);

        hero.Children.Add(ring);
        hero.Children.Add(gradeColumn);
        return hero;
    }

    // ── Score ring: static muted-track Ellipse + a Path arc from BuildRingArc, same technique
    //    Dashboard's rank ring and Pilot Hub's landing ring already established -- this is the
    //    third consumer. `percent` is `report.Score` directly (already 0-100), no threshold math
    //    needed. Center text: big score number + smaller "/100", matching `.lrm-score-num`/
    //    `.lrm-score-denom`'s visual split. Decorative tick marks (`.ring-tick-maj`/`-min`) are
    //    skipped -- neither of this ring's two precedents drew them either, and the plan's own
    //    Step 1 doesn't call for them. ────────────────────────────────────────────────────────
    private static Grid BuildRing(LandingReport report)
    {
        var ringGrid = new Grid
        {
            Width = RingGridSize,
            Height = RingGridSize,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var track = new Ellipse
        {
            Width = RingDiameter,
            Height = RingDiameter,
            StrokeThickness = RingStrokeWidth,
            Stroke = ParamGridLineBrush, // #ECF0FB, matches .ring-track
        };

        // Fully-qualified: `using System.IO;` (Path.Combine, below in BuildPhotoOrNull) and
        // `using Microsoft.UI.Xaml.Shapes;` (this Path shape) both bring a type named `Path`
        // into scope, so the plain name is ambiguous here.
        var fill = new Microsoft.UI.Xaml.Shapes.Path
        {
            StrokeThickness = RingStrokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stroke = HexToBrush(report.GradeColor),
            Data = DashboardPage.BuildRingArc(centerX: RingCenter, centerY: RingCenter, radius: RingRadius, percent: report.Score),
        };

        var scoreCenter = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 1,
            Children =
            {
                new TextBlock
                {
                    Text = report.Score.ToString(CultureInfo.InvariantCulture),
                    FontFamily = (FontFamily)Application.Current.Resources["DisplayFontBold"],
                    FontSize = 36,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = TextPrimaryBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "/100",
                    FontSize = 11,
                    Foreground = TextMutedBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            },
        };

        ringGrid.Children.Add(track);
        ringGrid.Children.Add(fill);
        ringGrid.Children.Add(scoreCenter);
        return ringGrid;
    }

    private static StackPanel BuildGradeColumn(LandingReport report)
    {
        var column = new StackPanel { Spacing = 14 };
        column.Children.Add(BuildGradeBadge(report));
        column.Children.Add(BuildButterMeter(report));
        column.Children.Add(BuildFlags(report));
        return column;
    }

    // ── Grade badge: `report.Grade`, bordered/colored via `report.GradeColor`, background a
    //    ~10% tint of the same color -- reproduces `GradeBg` directly off the ring/border color
    //    rather than re-deriving the score-band switch, since they're the same colors either way
    //    (LandingResult.LandingGradeColor's own >=90/75/60/40 bands already match GradeBg's rgba
    //    literals exactly). ──────────────────────────────────────────────────────────────────
    private static Border BuildGradeBadge(LandingReport report)
    {
        var gradeBrush = HexToBrush(report.GradeColor);
        var gradeBg = new SolidColorBrush(Color.FromArgb(0x1A, gradeBrush.Color.R, gradeBrush.Color.G, gradeBrush.Color.B)); // ~10% (0x1A/255 ≈ 0.102)

        return new Border
        {
            BorderBrush = gradeBrush,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(8),
            Background = gradeBg,
            Padding = new Thickness(16, 6, 16, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = report.Grade,
                FontFamily = (FontFamily)Application.Current.Resources["DisplayFontBold"],
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                CharacterSpacing = 90, // ~0.09em
                Foreground = gradeBrush,
            },
        };
    }

    // ── Butter meter: rating headline + joke blurb + VS readout, all keyed off `report.ButterColor`.
    //    `.lrm-butter`'s CSS is `border:1px solid var(--border); border-left-width:3px;` with the
    //    inline `border-color` override replacing ALL four sides' color (only the left side's
    //    WIDTH stays different) -- reproduced as a uniform-color, asymmetric-width Border. The
    //    barely-visible `rgba(255,255,255,.04)` gradient over an already-white card is dropped as
    //    imperceptible, not missing. ──────────────────────────────────────────────────────────
    private static Border BuildButterMeter(LandingReport report)
    {
        var butterBrush = HexToBrush(report.ButterColor);

        var vsLine = new TextBlock
        {
            FontFamily = (FontFamily)Application.Current.Resources["DisplayFontBold"],
            FontSize = 12,
            Foreground = TextMutedBrush,
            Margin = new Thickness(0, 7, 0, 0),
        };
        vsLine.Inlines.Add(new Run
        {
            Text = report.VerticalSpeedFpm.ToString("F0", CultureInfo.InvariantCulture),
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = butterBrush,
        });
        vsLine.Inlines.Add(new Run { Text = " fpm touchdown" });

        var stack = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = report.ButterRating,
                    FontFamily = (FontFamily)Application.Current.Resources["DisplayFontBold"],
                    FontSize = 19,
                    FontWeight = Microsoft.UI.Text.FontWeights.ExtraBold,
                    Foreground = butterBrush,
                },
                new TextBlock
                {
                    Text = report.ButterBlurb,
                    FontSize = 12,
                    Foreground = TextMutedBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0),
                },
                vsLine,
            },
        };

        return new Border
        {
            BorderBrush = butterBrush,
            BorderThickness = new Thickness(3, 1, 1, 1), // left:3px, others:1px -- matches border-left-width override
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 12, 14, 12),
            Child = stack,
        };
    }

    // ── Quality flags: up to 4 rows. Stable Approach / Flare Detected / Bounce are always shown;
    //    the 4th row is an exact 3-way branch (Personal Best, OR rank/average if there's enough
    //    history, OR nothing at all) matching the original's `@if (IsPersonalBest) ... else if
    //    (TotalLandings > 1) ...` -- with no trailing `else`, so a pilot with 0 or 1 landings on
    //    record gets neither row. ────────────────────────────────────────────────────────────
    private static StackPanel BuildFlags(LandingReport report)
    {
        var flags = new StackPanel { Spacing = 6 };

        flags.Children.Add(BuildFlagRow(
            report.StableApproach ? "✓" : "✗",
            report.StableApproach ? FlagOkPipBg : FlagFailPipBg,
            report.StableApproach ? FlagOkPipText : FlagFailPipText,
            "Stable Approach"));

        flags.Children.Add(BuildFlagRow(
            report.FlareDetected ? "✓" : "–",
            report.FlareDetected ? FlagOkPipBg : FlagNeutralPipBg,
            report.FlareDetected ? FlagOkPipText : TextMutedBrush,
            "Flare Detected"));

        if (report.BounceCount > 0)
        {
            flags.Children.Add(BuildFlagRow("!", FlagFailPipBg, FlagFailPipText,
                $"{report.BounceCount} Bounce{(report.BounceCount > 1 ? "s" : "")}"));
        }
        else
        {
            flags.Children.Add(BuildFlagRow("✓", FlagOkPipBg, FlagOkPipText, "No Bounce"));
        }

        if (report.IsPersonalBest)
        {
            flags.Children.Add(BuildBestFlagRow());
        }
        else if (report.TotalLandings > 1)
        {
            flags.Children.Add(BuildFlagRow("#", FlagNeutralPipBg, TextMutedBrush,
                $"{report.RankAmongLandings} of {report.TotalLandings} · avg {report.AverageScore}"));
        }

        return flags;
    }

    private static StackPanel BuildFlagRow(string pip, Brush pipBackground, Brush pipForeground, string label)
    {
        var pipBadge = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(999),
            Background = pipBackground,
            Child = new TextBlock
            {
                Text = pip,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = pipForeground,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Children =
            {
                pipBadge,
                new TextBlock
                {
                    Text = label,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    Foreground = FlagTextBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
    }

    // Personal Best is architecturally different from the other three flag rows: app.css's
    // ".lrm-flag.flag-best" tints the WHOLE chip (border + background), not just a pip circle,
    // and sets no border-radius (a square chip, not a pill) -- reproduced as its own Border
    // rather than reusing BuildFlagRow's pip-badge shape.
    private static Border BuildBestFlagRow()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Children =
            {
                new TextBlock
                {
                    Text = "★",
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = FlagBestText,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "Personal Best",
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    Foreground = FlagBestText,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        return new Border
        {
            BorderBrush = FlagBestBorder,
            BorderThickness = new Thickness(1),
            Background = FlagBestBg,
            Padding = new Thickness(8, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = row,
        };
    }

    // ── Runway-dash divider: 6 equal-width dashes, matching `.lrm-rwy-strip`'s `flex:1` children
    //    -- a Grid with 6 Star columns is the true flex:1 equivalent (unlike a StackPanel, whose
    //    children can't equally share remaining width), with per-dash margins simulating the
    //    parent's `gap: 6px`. ───────────────────────────────────────────────────────────────────
    private static Grid BuildRunwayDivider()
    {
        var strip = new Grid { Margin = new Thickness(20, 2, 20, 16) };
        for (int i = 0; i < 6; i++)
        {
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dash = new Border
            {
                Height = 3.5,
                Background = TextPrimaryBrush,
                Opacity = 0.07,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(i == 0 ? 0 : 3, 0, i == 5 ? 0 : 3, 0),
            };
            Grid.SetColumn(dash, i);
            strip.Children.Add(dash);
        }
        return strip;
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

    // ── Score breakdown: 6 factors from `report.Factors`, each already carrying a computed
    //    Label/Score/Max/Pct/Color -- a Border-track + Border-fill-width row per factor, same
    //    fixed-width-track idiom PilotHubPage.xaml.cs's rank-progress bar established. ─────────
    private static StackPanel BuildBreakdown(LandingReport report)
    {
        var section = new StackPanel { Margin = new Thickness(20, 0, 20, 18) };
        section.Children.Add(new TextBlock
        {
            Text = "SCORE BREAKDOWN",
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextMutedBrush,
            CharacterSpacing = 120, // ~0.12em
            Margin = new Thickness(0, 0, 0, 10),
        });

        foreach (var factor in report.Factors)
        {
            section.Children.Add(BuildFactorRow(factor));
        }

        return section;
    }

    private static Grid BuildFactorRow(LandingReportFactor factor)
    {
        var color = HexToBrush(factor.Color);

        var label = new TextBlock
        {
            Text = factor.Label,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Foreground = FlagTextBrush, // #475569, matches .f-label
            Width = FactorLabelWidth,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var track = new Border
        {
            Width = FactorTrackWidth,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = ParamGridLineBrush, // #ECF0FB, matches .f-track
        };
        var fill = new Border
        {
            Width = FactorTrackWidth * (factor.Pct / 100.0),
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = color,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var trackHost = new Grid { Margin = new Thickness(9, 0, 9, 0), Children = { track, fill } };

        var pts = new TextBlock
        {
            FontFamily = (FontFamily)Application.Current.Resources["DisplayFontBold"],
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = color,
            Width = FactorPtsWidth,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        pts.Inlines.Add(new Run { Text = factor.Score.ToString("F0", CultureInfo.InvariantCulture) });
        pts.Inlines.Add(new Run { Text = $"/{(int)factor.Max}", Foreground = DotBrush }); // f-max color #CBD5E1

        var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(label, 0);
        Grid.SetColumn(trackHost, 1);
        Grid.SetColumn(pts, 2);
        row.Children.Add(label);
        row.Children.Add(trackHost);
        row.Children.Add(pts);
        return row;
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

        // Fully-qualified System.IO.Path -- `using Microsoft.UI.Xaml.Shapes;` (added for the
        // score ring's Ellipse/Path shapes) makes the plain `Path` name ambiguous in this file.
        var photoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot", report.AircraftImagePath);
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
