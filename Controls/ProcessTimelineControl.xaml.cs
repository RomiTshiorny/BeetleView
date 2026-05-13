using System;
using System.Collections.Generic;
using BeetleView.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace BeetleView.Controls;

/// <summary>
/// Renders a horizontal swimlane per process: one short lifetime bar plus a
/// vertical tick per exception event, colored by exception type. Inspired by
/// the timeline visualizer in PerfView-style tools.
/// </summary>
public sealed partial class ProcessTimelineControl : UserControl
{
    private const double LabelWidth = 220;
    private const double RowHeight = 22;
    private const double HeaderHeight = 24;
    private const double LeftPadding = 6;
    private const double RightPadding = 12;

    // Brushes for the bar fill (subtle) and bar stroke (the visible line).
    private static readonly SolidColorBrush BarBrush = new(Color.FromArgb(0xFF, 0x60, 0x80, 0xA8));
    private static readonly SolidColorBrush LabelBrush = new(Color.FromArgb(0xFF, 0xDD, 0xDD, 0xDD));
    private static readonly SolidColorBrush AxisBrush = new(Color.FromArgb(0x80, 0xCC, 0xCC, 0xCC));
    private static readonly SolidColorBrush GridBrush = new(Color.FromArgb(0x30, 0xCC, 0xCC, 0xCC));

    // Stable color palette for exception types. A type's index is determined
    // by a deterministic hash of its name so colors don't change between
    // renders within a session.
    private static readonly Color[] Palette = new[]
    {
        Color.FromArgb(0xFF, 0xE5, 0x4B, 0x4B), // red
        Color.FromArgb(0xFF, 0xE6, 0x8A, 0x2E), // orange
        Color.FromArgb(0xFF, 0xE6, 0xC8, 0x2E), // yellow
        Color.FromArgb(0xFF, 0x6F, 0xC8, 0x3C), // lime
        Color.FromArgb(0xFF, 0x2E, 0xB3, 0x6B), // green
        Color.FromArgb(0xFF, 0x2E, 0xC8, 0xC8), // cyan
        Color.FromArgb(0xFF, 0x4A, 0x8C, 0xE6), // blue
        Color.FromArgb(0xFF, 0x8E, 0x6E, 0xE6), // indigo
        Color.FromArgb(0xFF, 0xC8, 0x55, 0xE6), // magenta
        Color.FromArgb(0xFF, 0xE6, 0x66, 0xA6), // pink
        Color.FromArgb(0xFF, 0xA0, 0x6A, 0x42), // brown
        Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E), // gray
    };

    private IReadOnlyList<TimelineRowViewModel> _rows = Array.Empty<TimelineRowViewModel>();
    private double _maxMSec = 1;
    private readonly Dictionary<string, SolidColorBrush> _typeBrushCache = new();

    public ProcessTimelineControl()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Render();
        VScroll.ViewChanged += (_, _) => { /* horizontal width is fixed to viewport; no-op */ };
    }

    /// <summary>Assigns the data and triggers a render.</summary>
    public void SetData(IReadOnlyList<TimelineRowViewModel> rows, double maxMSec)
    {
        _rows = rows ?? Array.Empty<TimelineRowViewModel>();
        _maxMSec = maxMSec > 0 ? maxMSec : 1;
        Render();
    }

    public void Clear()
    {
        _rows = Array.Empty<TimelineRowViewModel>();
        _maxMSec = 1;
        Render();
    }

    private void Render()
    {
        DrawCanvas.Children.Clear();

        if (_rows.Count == 0)
        {
            EmptyText.Visibility = Visibility.Visible;
            DrawCanvas.Width = 0;
            DrawCanvas.Height = 0;
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;

        // Use the ScrollViewer's viewport for horizontal sizing; if it hasn't
        // been measured yet, fall back to the control's ActualWidth.
        double availableWidth = VScroll.ViewportWidth > 0 ? VScroll.ViewportWidth : ActualWidth;
        if (availableWidth <= 0) return;

        // Canvas spans the viewport width (no horizontal scroll). The total
        // height accommodates the time-axis header plus one row per process.
        DrawCanvas.Width = availableWidth;
        DrawCanvas.Height = HeaderHeight + _rows.Count * RowHeight;

        double timelineLeft = LabelWidth;
        double timelineWidth = availableWidth - LabelWidth - RightPadding;
        if (timelineWidth < 50) timelineWidth = 50;

        DrawTimeAxis(timelineLeft, timelineWidth);
        DrawGridlines(timelineLeft, timelineWidth);

        for (int i = 0; i < _rows.Count; i++)
        {
            DrawRow(_rows[i], i, timelineLeft, timelineWidth);
        }
    }

    private void DrawTimeAxis(double left, double width)
    {
        // Baseline line at the bottom of the header.
        var baseline = new Line
        {
            X1 = LeftPadding,
            X2 = left + width,
            Y1 = HeaderHeight - 0.5,
            Y2 = HeaderHeight - 0.5,
            Stroke = AxisBrush,
            StrokeThickness = 1,
        };
        DrawCanvas.Children.Add(baseline);

        // Major ticks: aim for one every ~120px, snapped to a "nice" duration
        // (1ms..1min progression) so labels read cleanly.
        int targetTicks = Math.Max(2, (int)(width / 120.0));
        double rawStep = _maxMSec / targetTicks;
        double step = NiceStep(rawStep);
        if (step <= 0) step = _maxMSec;

        for (double t = 0; t <= _maxMSec + 0.5; t += step)
        {
            double x = left + (t / _maxMSec) * width;

            DrawCanvas.Children.Add(new Line
            {
                X1 = x, X2 = x,
                Y1 = HeaderHeight - 6,
                Y2 = HeaderHeight,
                Stroke = AxisBrush,
                StrokeThickness = 1,
            });

            var label = new TextBlock
            {
                Text = FormatMSec(t),
                FontSize = 10,
                Foreground = LabelBrush,
            };
            Canvas.SetLeft(label, x + 2);
            Canvas.SetTop(label, 2);
            DrawCanvas.Children.Add(label);
        }
    }

    private void DrawGridlines(double left, double width)
    {
        int targetTicks = Math.Max(2, (int)(width / 120.0));
        double step = NiceStep(_maxMSec / targetTicks);
        if (step <= 0) return;

        double bottom = HeaderHeight + _rows.Count * RowHeight;
        for (double t = step; t < _maxMSec; t += step)
        {
            double x = left + (t / _maxMSec) * width;
            DrawCanvas.Children.Add(new Line
            {
                X1 = x, X2 = x,
                Y1 = HeaderHeight,
                Y2 = bottom,
                Stroke = GridBrush,
                StrokeThickness = 1,
            });
        }
    }

    private void DrawRow(TimelineRowViewModel row, int index, double left, double width)
    {
        double y = HeaderHeight + index * RowHeight;
        double midY = y + RowHeight / 2;

        // Label (truncated by Width + TextTrimming).
        var label = new TextBlock
        {
            Text = row.Label,
            FontSize = 11,
            Foreground = LabelBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = LabelWidth - LeftPadding - 8,
        };
        Canvas.SetLeft(label, LeftPadding);
        Canvas.SetTop(label, y + 4);
        DrawCanvas.Children.Add(label);

        // Lifetime bar.
        double xStart = left + (row.StartMSec / _maxMSec) * width;
        double xStop = left + (row.StopMSec / _maxMSec) * width;
        if (xStop < xStart) xStop = xStart;

        // Minimum visible width so very short-lived processes aren't invisible.
        double barWidth = Math.Max(2, xStop - xStart);
        var bar = new Rectangle
        {
            Width = barWidth,
            Height = 3,
            Fill = BarBrush,
        };
        Canvas.SetLeft(bar, xStart);
        Canvas.SetTop(bar, midY + 4);
        DrawCanvas.Children.Add(bar);

        // Exception markers — vertical ticks colored by exception type.
        double markerTop = y + 2;
        double markerHeight = RowHeight - 4;
        foreach (var ex in row.Exceptions)
        {
            double x = left + (ex.TimestampMSec / _maxMSec) * width;
            if (x < left - 1 || x > left + width + 1) continue; // clip

            var tick = new Rectangle
            {
                Width = 2,
                Height = markerHeight,
                Fill = BrushForType(ex.ExceptionType),
            };
            Canvas.SetLeft(tick, x - 1);
            Canvas.SetTop(tick, markerTop);
            ToolTipService.SetToolTip(tick, $"{ex.ExceptionType}\n@ {FormatMSec(ex.TimestampMSec)}");
            DrawCanvas.Children.Add(tick);
        }
    }

    private SolidColorBrush BrushForType(string type)
    {
        type ??= "";
        if (_typeBrushCache.TryGetValue(type, out var brush)) return brush;
        // Stable index from a simple FNV-1a-style hash so colors are
        // deterministic across renders within a session (and across runs).
        uint hash = 2166136261u;
        foreach (char c in type)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        var color = Palette[(int)(hash % (uint)Palette.Length)];
        brush = new SolidColorBrush(color);
        _typeBrushCache[type] = brush;
        return brush;
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        // Snap to 1, 2, 5 × 10^n.
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double snap = norm < 1.5 ? 1 : norm < 3.5 ? 2 : norm < 7.5 ? 5 : 10;
        return snap * mag;
    }

    private static string FormatMSec(double ms)
    {
        if (ms < 1000) return $"{ms:0}ms";
        double s = ms / 1000.0;
        if (s < 60) return $"{s:0.##}s";
        int mins = (int)(s / 60);
        double rem = s - mins * 60;
        return $"{mins}m {rem:0}s";
    }
}
