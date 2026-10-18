using System.Globalization;
using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace EQ2Parser.Mac.ViewModels;

/// <summary>LiveCharts series for the drill's chart strip, built from
/// <see cref="DrillData"/>. The colours and the slice palette follow the
/// WPF app's dark theme.</summary>
public static class DrillCharts
{
    public static readonly SolidColorPaint TextPaint = new(SKColor.Parse("#E2E4F0"));
    public static readonly SolidColorPaint GridPaint = new(SKColor.Parse("#2A2E3D")) { StrokeThickness = 1 };
    public static readonly SolidColorPaint TooltipBackground = new(SKColor.Parse("#262A38"));

    private static readonly string[] SlicePalette =
    [
        "#93B4FF", "#C8A96E", "#4ADE80", "#F87171", "#FBBF24", "#E8BBFF",
        "#22D3EE", "#FB923C", "#A3E635", "#F472B6", "#60A5FA", "#8B90AB",
    ];

    public static ISeries[] Lines(List<ChartLine> lines) =>
    [
        .. lines.Select(line => (ISeries)new LineSeries<double>
        {
            Name = line.Name,
            Values = line.Rates,
            Stroke = new SolidColorPaint(SKColor.Parse(line.ColorHex)) { StrokeThickness = 2 },
            Fill = null,
            GeometrySize = 0,
            LineSmoothness = 0.35,
        }),
    ];

    public static Axis[] TimeAxis(double slotSeconds) =>
    [
        new Axis
        {
            Labeler = v => TimeSpan.FromSeconds(Math.Max(0, v * slotSeconds)).ToString(@"m\:ss", CultureInfo.InvariantCulture),
            LabelsPaint = TextPaint,
            TextSize = 11,
            SeparatorsPaint = null,
        },
    ];

    public static Axis[] RateAxis() =>
    [
        new Axis
        {
            Labeler = v => FightTable.Compact(Math.Max(0, v)),
            LabelsPaint = TextPaint,
            TextSize = 11,
            MinLimit = 0,
            SeparatorsPaint = GridPaint,
        },
    ];

    public static ISeries[] Slices(List<(string Name, double Value)> slices) =>
    [
        .. slices.Select((slice, i) => (ISeries)new PieSeries<double>
        {
            Name = slice.Name,
            Values = [slice.Value],
            InnerRadius = 45,
            Fill = new SolidColorPaint(SKColor.Parse(SlicePalette[i % SlicePalette.Length])),
            Stroke = null,
            DataLabelsPosition = PolarLabelsPosition.Outer,
        }),
    ];
}
