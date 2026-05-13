using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
///
/// Performance: exception markers are binned by pixel column per row so a
/// process that threw 50,000 exceptions yields at most ~timelineWidth ticks
/// rather than 50,000 individual XAML elements. Rendering is also batched
/// with cooperative yields so very wide sessions don't hang the UI thread.
/// </summary>
public sealed partial class ProcessTimelineControl : UserControl
{
    private const double LabelWidth = 220;
    private const double RowHeight = 22;
    private const double HeaderHeight = 24;
    private const double LeftPadding = 6;
    private const double RightPadding = 12;

    // Hard cap on the number of process lanes rendered. Beyond this point the
    // visualizer stops being scannable and starts being expensive; rows are
    // prioritized by exception count so the most "interesting" processes
    // surface first.
    private const int MaxRowsRendered = 400;

    // Yield to the UI dispatcher every N rows during render so input stays
    // responsive on very large sessions.
    private const int RenderYieldEvery = 25;

    private static readonly SolidColorBrush BarBrush = new(Color.FromArgb(0xFF, 0x60, 0x80, 0xA8));
    private static readonly SolidColorBrush LabelBrush = new(Color.FromArgb(0xFF, 0xDD, 0xDD, 0xDD));
    private static readonly SolidColorBrush AxisBrush = new(Color.FromArgb(0x80, 0xCC, 0xCC, 0xCC));
    private static readonly SolidColorBrush GridBrush = new(Color.FromArgb(0x30, 0xCC, 0xCC, 0xCC));

    // Stable color palette. A type's index is determined by a deterministic
    // hash of its name so colors don't change between renders.
    private static readonly Color[] Palette = new[]
    {
        Color.FromArgb(0xFF, 0xE5, 0x4B, 0x4B),
        Color.FromArgb(0xFF, 0xE6, 0x8A, 0x2E),
        Color.FromArgb(0xFF, 0xE6, 0xC8, 0x2E),
        Color.FromArgb(0xFF, 0x6F, 0xC8, 0x3C),
        Color.FromArgb(0xFF, 0x2E, 0xB3, 0x6B),
        Color.FromArgb(0xFF, 0x2E, 0xC8, 0xC8),
        Color.FromArgb(0xFF, 0x4A, 0x8C, 0xE6),
        Color.FromArgb(0xFF, 0x8E, 0x6E, 0xE6),
        Color.FromArgb(0xFF, 0xC8, 0x55, 0xE6),
        Color.FromArgb(0xFF, 0xE6, 0x66, 0xA6),
        Color.FromArgb(0xFF, 0xA0, 0x6A, 0x42),
        Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E),
    };

    private IReadOnlyList<TimelineRowViewModel> _rows = Array.Empty<TimelineRowViewModel>();
    private double _maxMSec = 1;
    private readonly Dictionary<string, SolidColorBrush> _typeBrushCache = new();

    // Cancels any in-flight render when data changes or the size changes —
    // without this, two renders could race and double-add elements.
    private CancellationTokenSource? _renderCts;

    public ProcessTimelineControl()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ScheduleRender();
    }

    /// <summary>Assigns the data and triggers a render.</summary>
    public void SetData(IReadOnlyList<TimelineRowViewModel> rows, double maxMSec)
    {
        _rows = rows ?? Array.Empty<TimelineRowViewModel>();
        _maxMSec = maxMSec > 0 ? maxMSec : 1;
        ScheduleRender();
    }

    public void Clear()
    {
        _rows = Array.Empty<TimelineRowViewModel>();
        _maxMSec = 1;
        ScheduleRender();
    }

    private void ScheduleRender()
    {
        _renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _renderCts = cts;
        _ = RenderAsync(cts.Token);
    }

    private async Task RenderAsync(CancellationToken ct)
    {
        DrawCanvas.Children.Clear();

        if (_rows.Count == 0)
        {
            EmptyText.Text = "No session loaded.";
            EmptyText.Visibility = Visibility.Visible;
            DrawCanvas.Width = 0;
            DrawCanvas.Height = 0;
            return;
        }

        double availableWidth = VScroll.ViewportWidth > 0 ? VScroll.ViewportWidth : ActualWidth;
        if (availableWidth <= 0) return;

        // Prioritize the most-exception-heavy processes when capping; those
        // are usually what an engineer wants to see first.
        IReadOnlyList<TimelineRowViewModel> rowsToRender;
        bool truncated;
        if (_rows.Count > MaxRowsRendered)
        {
            var ordered = new List<TimelineRowViewModel>(_rows);
            ordered.Sort((a, b) => b.Exceptions.Count.CompareTo(a.Exceptions.Count));
            ordered.RemoveRange(MaxRowsRendered, ordered.Count - MaxRowsRendered);
            // Re-sort kept rows by start time so they still read chronologically.
            ordered.Sort((a, b) => a.StartMSec.CompareTo(b.StartMSec));
            rowsToRender = ordered;
            truncated = true;
        }
        else
        {
            rowsToRender = _rows;
            truncated = false;
        }

        if (truncated)
        {
            EmptyText.Text = $"Showing top {MaxRowsRendered:N0} processes by exception count (of {_rows.Count:N0}).";
            EmptyText.HorizontalAlignment = HorizontalAlignment.Right;
            EmptyText.VerticalAlignment = VerticalAlignment.Top;
            EmptyText.Margin = new Thickness(0, 4, 12, 0);
            EmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyText.Visibility = Visibility.Collapsed;
        }

        DrawCanvas.Width = availableWidth;
        DrawCanvas.Height = HeaderHeight + rowsToRender.Count * RowHeight;

        double timelineLeft = LabelWidth;
        double timelineWidth = availableWidth - LabelWidth - RightPadding;
        if (timelineWidth < 50) timelineWidth = 50;

        DrawTimeAxis(timelineLeft, timelineWidth);
        DrawGridlines(timelineLeft, timelineWidth, rowsToRender.Count);

        for (int i = 0; i < rowsToRender.Count; i++)
        {
            if (ct.IsCancellationRequested) return;
            DrawRow(rowsToRender[i], i, timelineLeft, timelineWidth);

            if ((i + 1) % RenderYieldEvery == 0)
            {
                await Task.Yield();
                if (ct.IsCancellationRequested) return;
            }
        }
    }

    private void DrawTimeAxis(double left, double width)
    {
        DrawCanvas.Children.Add(new Line
        {
            X1 = LeftPadding,
            X2 = left + width,
            Y1 = HeaderHeight - 0.5,
            Y2 = HeaderHeight - 0.5,
            Stroke = AxisBrush,
            StrokeThickness = 1,
        });

        int targetTicks = Math.Max(2, (int)(width / 120.0));
        double step = NiceStep(_maxMSec / targetTicks);
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

    private void DrawGridlines(double left, double width, int rowCount)
    {
        int targetTicks = Math.Max(2, (int)(width / 120.0));
        double step = NiceStep(_maxMSec / targetTicks);
        if (step <= 0) return;

        double bottom = HeaderHeight + rowCount * RowHeight;
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

        double xStart = left + (row.StartMSec / _maxMSec) * width;
        double xStop = left + (row.StopMSec / _maxMSec) * width;
        if (xStop < xStart) xStop = xStart;

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

        // Exception markers — pixel-binned per column. Within a single pixel
        // column we keep only one tick (first exception type seen wins). This
        // collapses tens of thousands of overlapping ticks down to at most
        // ~timelineWidth elements per row.
        double markerTop = y + 2;
        double markerHeight = RowHeight - 4;
        double scale = width / _maxMSec;
        int minCol = (int)left - 1;
        int maxCol = (int)(left + width) + 1;
        var seen = new HashSet<int>();
        foreach (var ex in row.Exceptions)
        {
            int col = (int)(left + ex.TimestampMSec * scale);
            if (col < minCol || col > maxCol) continue;
            if (!seen.Add(col)) continue;

            var tick = new Rectangle
            {
                Width = 2,
                Height = markerHeight,
                Fill = BrushForType(ex.ExceptionType),
            };
            Canvas.SetLeft(tick, col);
            Canvas.SetTop(tick, markerTop);
            DrawCanvas.Children.Add(tick);
        }
    }

    private SolidColorBrush BrushForType(string type)
    {
        type ??= "";
        if (_typeBrushCache.TryGetValue(type, out var brush)) return brush;
        // Stable FNV-1a hash so colors are deterministic across renders.
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
